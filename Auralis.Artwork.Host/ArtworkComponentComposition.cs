using System.Runtime.CompilerServices;

namespace Auralis.Artwork.Host;

public enum ArtworkComponentIssue
{
    NotRegistered, Disabled, ApiMismatch, HostTooOld, MissingCapability, ActivationFailed,
    DescriptorMismatch, CreationFailed, ReusedFactory, ReusedSource, SourceDisposeFailed, FactoryDisposeFailed
}
public sealed record ArtworkRegistration(ArtworkDescriptor Descriptor, bool Enabled, Func<IArtworkSourceFactory> Activate);
public sealed record ArtworkStatus(ArtworkDescriptor Descriptor, bool Bundled, ArtworkComponentIssue? Issue);
public sealed class ArtworkComponentException : InvalidOperationException
{
    public IReadOnlyList<ArtworkComponentIssue> Issues { get; }
    public ArtworkComponentException(IEnumerable<ArtworkComponentIssue> issues) : base("Artwork component is unavailable.") =>
        Issues = Array.AsReadOnly(issues.ToArray());
}

/// <summary>Immutable explicit startup selection; no discovery, disk I/O, trust decision or HTTP.
/// Only selected code is activated on first Fetch. Managed creation fallback never replays a request.</summary>
public sealed class ArtworkComponentComposition
{
    public const int ApiVersion = 1;
    public static Version HostVersion { get; } = new(0, 1, 0);
    private readonly Dictionary<string, ArtworkRegistration> _optional = new(StringComparer.Ordinal);
    private readonly ArtworkRegistration _bundled;
    private readonly string? _preferred;
    private readonly ArtworkCapabilities _required;
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly Dictionary<ArtworkRegistration, ArtworkComponentIssue> _failures = new(ReferenceEqualityComparer.Instance);
    private readonly object _failureGate = new();
    private static readonly object OwnershipGate = new();
    private static readonly ConditionalWeakTable<IArtworkSourceFactory, object> Factories = new();
    private static readonly ConditionalWeakTable<IArtworkSource, object> Sources = new();
    private int _fellBack, _activated;
    public bool FellBack => Volatile.Read(ref _fellBack) != 0;
    public bool Activated => Volatile.Read(ref _activated) != 0;
    public bool UsingBundled => _preferred is null || !_optional.ContainsKey(_preferred) || FellBack;
    // Before activation: chosen metadata, not proof of a loaded component.
    public ArtworkDescriptor CurrentDescriptor => UsingBundled ? _bundled.Descriptor : _optional[_preferred!].Descriptor;

    public ArtworkComponentComposition(IEnumerable<ArtworkRegistration> optional, ArtworkRegistration bundled,
        string? preferredId = null, ArtworkCapabilities required = ArtworkCapabilities.Full)
    {
        ArgumentNullException.ThrowIfNull(optional);
        if (!Valid(bundled) || !bundled.Enabled) throw new ArgumentException("Invalid bundled artwork registration.");
        if (required == ArtworkCapabilities.None || (required & ~ArtworkCapabilities.Full) != 0) throw new ArgumentOutOfRangeException(nameof(required));
        _bundled = bundled; _preferred = string.IsNullOrEmpty(preferredId) ? null : preferredId; _required = required;
        foreach (var row in optional.Take(65))
            if (!Valid(row) || _optional.Count == 64 || !_optional.TryAdd(row.Descriptor.Id, row))
                throw new ArgumentException("Invalid or duplicate artwork registration.");
    }
    private static bool Valid(ArtworkRegistration? row) => row?.Descriptor is { } d && row.Activate is not null &&
        d.Id is { Length: > 0 and <= 100 } && d.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        !string.IsNullOrWhiteSpace(d.DisplayName) && d.DisplayName.Length <= 80 && !d.DisplayName.Any(char.IsControl) &&
        d.ComponentVersion is not null && d.MinimumHostVersion is not null && (d.Capabilities & ~ArtworkCapabilities.Full) == 0;
    private ArtworkComponentIssue? Check(ArtworkRegistration row)
    {
        if (!row.Enabled) return ArtworkComponentIssue.Disabled;
        if (row.Descriptor.ApiVersion != ApiVersion) return ArtworkComponentIssue.ApiMismatch;
        if (row.Descriptor.MinimumHostVersion > HostVersion) return ArtworkComponentIssue.HostTooOld;
        if ((row.Descriptor.Capabilities & _required) != _required) return ArtworkComponentIssue.MissingCapability;
        lock (_failureGate) return _failures.TryGetValue(row, out var failed) ? failed : null;
    }
    public IReadOnlyList<ArtworkStatus> Inspect() => Array.AsReadOnly(_optional.Values.Append(_bundled)
        .Select(row => new ArtworkStatus(row.Descriptor, ReferenceEquals(row, _bundled), Check(row))).ToArray());
    public IArtworkSource CreateDeferred() => new DeferredSource(CreateAsync);

    private sealed record Owned(IArtworkSource Source, IArtworkSourceFactory Factory);
    private async Task<Owned> CreateAsync()
    {
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            var issues = new List<ArtworkComponentIssue>();
            var attempts = new List<ArtworkRegistration>();
            if (_preferred is not null)
            {
                if (_optional.TryGetValue(_preferred, out var selected)) attempts.Add(selected);
                else issues.Add(ArtworkComponentIssue.NotRegistered);
            }
            attempts.Add(_bundled);
            foreach (var row in attempts)
            {
                if (Check(row) is { } rejected) { issues.Add(rejected); continue; }
                IArtworkSourceFactory? factory = null; IArtworkSource? source = null;
                var ownsFactory = false; var ownsSource = false;
                var issue = ArtworkComponentIssue.ActivationFailed;
                try
                {
                    factory = row.Activate() ?? throw new InvalidOperationException();
                    issue = ArtworkComponentIssue.ReusedFactory;
                    lock (OwnershipGate)
                    { if (Factories.TryGetValue(factory, out _)) throw new InvalidOperationException(); Factories.Add(factory, new()); ownsFactory = true; }
                    issue = ArtworkComponentIssue.DescriptorMismatch;
                    if (factory.Descriptor != row.Descriptor) throw new InvalidOperationException();
                    issue = ArtworkComponentIssue.CreationFailed;
                    source = factory.Create() ?? throw new InvalidOperationException();
                    issue = ArtworkComponentIssue.ReusedSource;
                    lock (OwnershipGate)
                    { if (Sources.TryGetValue(source, out _)) throw new InvalidOperationException(); Sources.Add(source, new()); ownsSource = true; }
                    if (issues.Count != 0) Volatile.Write(ref _fellBack, 1);
                    Volatile.Write(ref _activated, 1);
                    return new(source, factory);
                }
                catch
                {
                    issues.Add(issue);
                    if (!ReferenceEquals(row, _bundled)) lock (_failureGate) _failures.TryAdd(row, issue);
                    if (ownsSource && source is not null) await CloseResourceAsync(source, ArtworkComponentIssue.SourceDisposeFailed, issues).ConfigureAwait(false);
                    if (ownsFactory && factory is not null) await CloseResourceAsync(factory, ArtworkComponentIssue.FactoryDisposeFailed, issues).ConfigureAwait(false);
                }
            }
            throw new ArtworkComponentException(issues);
        }
        finally { _creation.Release(); }
    }
    private static async Task CloseResourceAsync(IAsyncDisposable item, ArtworkComponentIssue issue, List<ArtworkComponentIssue> issues)
    { try { await item.DisposeAsync().ConfigureAwait(false); } catch { issues.Add(issue); } }

    private sealed class DeferredSource(Func<Task<Owned>> create) : IArtworkSource
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<Owned>? _creation;
        private Task? _close;
        private int _inflight;

        public async Task<ArtworkPayload> FetchAsync(ArtworkRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is null) throw new ArtworkException(ArtworkFailure.InvalidRequest);
            Task<Owned> creation; CancellationTokenSource lifetime;
            lock (_gate)
            {
                if (_close is not null) throw new ArtworkException(ArtworkFailure.Disposed);
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                _inflight++;
                creation = _creation ??= Task.Run(create);
            }
            using (lifetime)
            {
                try
                {
                    var owned = await creation.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    lifetime.Token.ThrowIfCancellationRequested();
                    var image = await owned.Source.FetchAsync(request, lifetime.Token).ConfigureAwait(false);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (image is null) throw new ArtworkException(ArtworkFailure.EmptyPayload);
                    if (image.Length > request.MaximumBytes) throw new ArtworkException(ArtworkFailure.TooLarge);
                    return image;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { throw new OperationCanceledException(cancellationToken); }
                catch (OperationCanceledException)
                { throw new ArtworkException(_shutdown.IsCancellationRequested ? ArtworkFailure.Disposed : ArtworkFailure.NetworkFailure); }
                catch (ArtworkException) { throw; }
                catch (ArtworkComponentException) { throw new ArtworkException(ArtworkFailure.ComponentUnavailable); }
                catch { throw new ArtworkException(ArtworkFailure.NetworkFailure); }
                finally
                {
                    lock (_gate) { _inflight--; if (_inflight == 0 && _close is not null) _drained.TrySetResult(); }
                }
            }
        }
        // Synchronous UI teardown schedules cleanup; callers needing its outcome use DisposeAsync.
        public void Dispose()
        {
            var close = DisposeAsync().AsTask();
            _ = close.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_close is null)
                {
                    _close = Task.Run(CloseAsync);
                    if (_inflight == 0) _drained.TrySetResult();
                }
                return new(_close);
            }
        }
        private async Task CloseAsync()
        {
            var issues = new List<ArtworkComponentIssue>();
            try { _shutdown.Cancel(); } catch { issues.Add(ArtworkComponentIssue.SourceDisposeFailed); }
            Task<Owned>? creating;
            lock (_gate) creating = _creation;
            Owned? owned = null;
            if (creating is not null)
            {
                try { owned = await creating.ConfigureAwait(false); }
                catch (ArtworkComponentException) { /* Creation failure cleaned its owned candidates. */ }
            }
            if (owned is not null) await CloseResourceAsync(owned.Source, ArtworkComponentIssue.SourceDisposeFailed, issues).ConfigureAwait(false);
            await _drained.Task.ConfigureAwait(false);
            if (owned is not null) await CloseResourceAsync(owned.Factory, ArtworkComponentIssue.FactoryDisposeFailed, issues).ConfigureAwait(false);
            _shutdown.Dispose();
            if (issues.Count != 0) throw new ArtworkComponentException(issues);
        }
    }
}
