using System.IO;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.Services;

/// <summary>
/// Owns the optional online-platform host without coupling it to the local-library or local-playback
/// paths. Construction is deliberately dormant: discovery, assembly loading, provider initialization,
/// and network access happen only after an explicit platform operation.
/// </summary>
public sealed class PlatformBackendService : IAsyncDisposable
{
    private readonly PlatformSettingsStore _settings;
    private readonly DefaultPlatformHostContextFactory _contextFactory;
    private readonly PlatformPluginHost _host;
    private PlatformPluginHostSnapshot? _discovered;
    internal string ProviderDisplayName(string providerId) => _discovered?.Providers
        .FirstOrDefault(p => !IsPluginDisabled(p.PluginId) && p.Provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.Provider.DisplayName ?? providerId;
    private int _disposeStarted;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    public bool IsPluginDisabled(string id) => _disabledPlugins.ContainsKey(id);

    internal async Task<(PlatformCommentArtworkPolicy Policy, Func<bool> IsActive)> GetCommentArtworkAuthorizationAsync(
        string providerId, CancellationToken token)
    {
        var registration = (await DiscoverAsync(token).ConfigureAwait(false)).Providers.FirstOrDefault(p =>
            p.Provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (registration is null) return (PlatformCommentArtworkPolicy.DenyAll, static () => false);
        return (registration.Provider.CommentArtworkPolicy,
            () => Volatile.Read(ref _disposeStarted) == 0 && !IsPluginDisabled(registration.PluginId));
    }

    internal async Task<IReadOnlyList<ProviderSettingView>> ReadSettingsAsync(PlatformProviderRegistration registration, CancellationToken token = default)
    {
        var result = new List<ProviderSettingView>();
        foreach (var setting in registration.Provider.Settings)
        {
            var stored = await _settings.GetScopedAsync(registration.PluginId, setting.Key, token);
            if (!setting.TryNormalize(stored ?? setting.DefaultValue, out var value)) value = setting.DefaultValue;
            result.Add(new(setting.Key, setting.Label, setting.Description, setting.Kind, value, setting.Required, setting.Choices));
        }
        return result;
    }

    internal async Task SaveSettingAsync(string providerId, string key, string value, CancellationToken token)
    {
        var registration = (await DiscoverAsync(token)).Providers.FirstOrDefault(p => p.Provider.Id == providerId);
        if (registration is null || IsPluginDisabled(registration.PluginId)) throw new InvalidOperationException("Provider unavailable.");
        var setting = registration.Provider.Settings.FirstOrDefault(s => s.Key == key);
        if (setting is null || !setting.TryNormalize(value, out var normalized)) throw new ArgumentException("Undeclared or invalid setting.");
        // No provider code runs when editing a manifest-declared public setting.
        await _settings.SetScopedAsync(registration.PluginId, setting.Key, normalized, token);
    }

    internal sealed record ProviderSettingView(string Key, string Label, string Description, string Kind, string Value,
        bool Required, IReadOnlyList<PlatformSettingChoice> Choices);

    internal async Task<PlatformPluginManifest?> FindPluginForDisableAsync(string id, CancellationToken token)
    {
        // An imported update is only next-session state. Close windows and clean data using the
        // same immutable revision that supplied this session's providers, aliases and native UI.
        var session = await DiscoverAsync(token);
        return session.Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? await PluginManager!.FindManagedPluginAsync(id, token);
    }

    internal async Task<bool> DisableAndClearAsync(string id, nint owner, CancellationToken token)
    {
        var manifest = await FindPluginForDisableAsync(id, token);
        var canExecuteCleanup = manifest is not null && manifest.SchemaVersion >= PlatformPluginManifestSchema.MinimumRuntimeVersion
            && await PlatformPluginIntegrity.VerifyAsync(manifest, token);
        await PluginManager!.SetEnabledAsync(id, false, token);
        _disabledPlugins.TryAdd(id, 0);
        _host.DisablePlugin(id);
        try
        {
            // Revoke already-issued contexts even if the file is now missing/tampered. Their pinned
            // grants were approved at activation; never read new grants or activate damaged code.
            if (!canExecuteCleanup)
            {
                await _contextFactory.RevokeExistingCredentialsAsync(id, token);
                return false;
            }
            await _contextFactory.RevokeCredentialsAsync(id, manifest, token);
            await using var cleanup = new PlatformPluginHost(new PlatformPluginHostOptions([manifest!.PluginDirectory],
                _contextFactory, trustPolicy: PlatformPluginIntegrity.VerifyAsync,
                minimumManifestSchemaVersion: PlatformPluginManifestSchema.MinimumRuntimeVersion));
            var cleared = true;
            foreach (var provider in manifest.Providers)
            {
                if (provider.Capabilities.Contains(Auralis.Platform.Abstractions.PlatformCapabilityKind.Authentication))
                    cleared &= (await cleanup.Router.SignOutAsync(provider.Id, token)).IsSuccess;
                if (provider.Capabilities.Contains(Auralis.Platform.Abstractions.PlatformCapabilityKind.NativeLogin))
                    cleared &= (await cleanup.Router.RouteAsync<Auralis.Platform.Abstractions.IPlatformNativeLoginCapability,
                        Auralis.Platform.Abstractions.PlatformUnit>(provider.Id, (capability, ct) => capability.ClearLoginDataAsync(owner, ct), token)).IsSuccess;
            }
            return cleared;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Disabled preference remains committed. Report incomplete cleanup, never claim success.
            return false;
        }
    }

    /// <summary>Creates the dormant backend for explicit bundled and per-user plugin roots.</summary>
    public PlatformBackendService()
        : this(NullAppLogger.Instance)
    {
    }

    /// <summary>
    /// Creates a dormant backend rooted at an explicit directory. Supplying a directory does not read it;
    /// it is inspected only when <see cref="DiscoverAsync"/> or a router operation is requested.
    /// </summary>
    public PlatformBackendService(string pluginDirectory)
        : this(pluginDirectory, NullAppLogger.Instance)
    {
    }

    internal PlatformBackendService(IAppLogger logger)
        : this(DefaultPluginDirectories, logger, new PlatformPluginManager(DefaultPluginStorage, DefaultPluginDirectories))
    {
    }

    internal PlatformBackendService(string pluginDirectory, IAppLogger logger)
        : this(new[] { pluginDirectory }, logger)
    {
    }

    private static string[] DefaultPluginDirectories =>
    [
        Path.Combine(AppContext.BaseDirectory, "plugins", "platforms"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auralis", "Plugins", "Platforms")
    ];

    private static string DefaultPluginStorage => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auralis", "Plugins");
    public PlatformPluginManager? PluginManager { get; }
    private readonly object _planGate = new();
    private Task<PluginSessionPlan>? _sessionPlan;
    private Task<PluginSessionPlan> GetSessionPlanAsync()
    {
        lock (_planGate) return _sessionPlan ??= PluginManager!.CreateSessionPlanAsync();
    }

    internal PlatformBackendService(IEnumerable<string> pluginDirectories, IAppLogger logger, PlatformPluginManager? manager = null, PlatformSettingsStore? settings = null,
        Func<string, IPlatformCredentialStore>? credentialStores = null)
    {
        PluginManager = manager;
        PluginDirectories = pluginDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _settings = settings ?? new PlatformSettingsStore();
        _contextFactory = new DefaultPlatformHostContextFactory(
            _settings,
            logger ?? throw new ArgumentNullException(nameof(logger)), credentialStores);
        _host = new PlatformPluginHost(new PlatformPluginHostOptions(
            PluginDirectories,
            _contextFactory,
            operationTimeout: TimeSpan.FromSeconds(30),
            trustPolicy: PlatformPluginIntegrity.VerifyAsync,
            directoryResolver: manager is null ? null : async token => (await GetSessionPlanAsync().WaitAsync(token).ConfigureAwait(false)).Roots,
            enabledPolicy: manager is null ? null : async (manifest, token) => (await GetSessionPlanAsync().WaitAsync(token).ConfigureAwait(false)).EnabledIds.Contains(manifest.Id),
            minimumManifestSchemaVersion: PlatformPluginManifestSchema.MinimumRuntimeVersion));
        _settings.ResolveDeclarationsAsync = async (id, token) => IsPluginDisabled(id) ? [] :
            (await _host.DiscoverAsync(token).ConfigureAwait(false)).Providers
                .Where(p => string.Equals(p.PluginId, id, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Provider.Settings).ToArray();
    }

    /// <summary>Gets the first root for compatibility; discovery uses <see cref="PluginDirectories"/>.</summary>
    public string PluginDirectory => PluginDirectories.FirstOrDefault() ?? string.Empty;

    /// <summary>Gets explicit bundled and per-user roots; no directory is created implicitly.</summary>
    public IReadOnlyList<string> PluginDirectories { get; }

    /// <summary>
    /// Gets the capability router. Merely getting this property is inert; invoking a route performs
    /// manifest discovery and may activate the selected plugin/provider.
    /// </summary>
    public PlatformRouter Router => _host.Router;

    /// <summary>
    /// Called only after the local/cache path and online consent checks. Resolve the capability,
    /// not a built-in provider ID. All candidates share one bounded request budget.
    /// </summary>
    internal async Task<PlatformLyricsLookupResult?> LookupLyricsAsync(PlatformLyricsLookupRequest request,
        CancellationToken token, TimeSpan? lookupBudget = null)
    {
        var timeout = lookupBudget ?? TimeSpan.FromSeconds(40);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(40)) throw new ArgumentOutOfRangeException(nameof(lookupBudget));
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(timeout);
        try
        {
            var snapshot = await DiscoverAsync(budget.Token).ConfigureAwait(false);
            foreach (var registration in snapshot.Providers
                         .Where(p => p.Provider.Capabilities.Contains(PlatformCapabilityKind.LyricsLookup))
                         .OrderBy(p => p.Provider.Id, StringComparer.OrdinalIgnoreCase))
            {
                budget.Token.ThrowIfCancellationRequested();
                if (IsPluginDisabled(registration.PluginId)) continue;
                var ready = true;
                foreach (var setting in registration.Provider.Settings.Where(s => s.Required))
                {
                    var value = await _settings.GetScopedAsync(registration.PluginId, setting.Key, budget.Token).ConfigureAwait(false);
                    if (!setting.TryNormalize(value ?? setting.DefaultValue, out var normalized) || string.IsNullOrWhiteSpace(normalized))
                        ready = false;
                }
                if (!ready) continue;
                var result = await Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>(registration.Provider.Id,
                    (capability, cancellation) => capability.LookupAsync(request, cancellation), budget.Token, timeout: timeout).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                budget.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposeStarted) != 0) return null;
                // The router timeout and shared budget timer can complete in either order.
                // A timed-out lookup is terminal even if the shared timer has not fired yet;
                // otherwise a fast fallback can incorrectly turn exhaustion into success.
                if (result.Error?.Code == PlatformErrorCode.Timeout) return null;
                if (IsPluginDisabled(registration.PluginId)) continue;
                if (result.IsSuccess && result.Value is { } lyrics && (lyrics.Instrumental || !string.IsNullOrWhiteSpace(lyrics.Text))) return lyrics;
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested) { return null; }
    }

    /// <summary>Gets a snapshot of sanitized host diagnostics accumulated after explicit use.</summary>
    public IReadOnlyList<PlatformPluginDiagnostic> Diagnostics => _host.Diagnostics;


    /// <summary>
    /// Explicitly performs manifest-only discovery. This reads JSON metadata but does not load plugin
    /// assemblies, construct HTTP clients, initialize providers, or make network requests.
    /// </summary>
    public async Task<PlatformPluginHostSnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        return _discovered = await _host.DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            if (PluginManager is not null) await PluginManager.CancelImportAsync().ConfigureAwait(false);
        }
        finally
        {
            _contextFactory.Dispose();
        }
    }
}
