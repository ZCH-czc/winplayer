using System.Runtime.CompilerServices;
using Auralis.Services;

namespace Auralis.Playback.Host;

public enum PlaybackComponentIssue
{
    None, NotRegistered, Disabled, ApiMismatch, HostTooOld, MissingCapability,
    ActivationFailed, DescriptorMismatch, RuntimeUnavailable, CreationFailed,
    ReusedSession, SessionDisposeFailed, FactoryDisposeFailed, ReusedFactory
}

public sealed record PlaybackComponentRegistration(
    PlaybackComponentDescriptor Descriptor, bool Enabled, Func<IPlaybackComponentFactory> Activate);
public sealed record PlaybackComponentStatus(PlaybackComponentDescriptor Descriptor, bool Enabled, PlaybackComponentIssue Issue);
public sealed record PlaybackSelection(IPlaybackSession Session, string ComponentId, bool UsedFallback,
    IReadOnlyList<PlaybackComponentIssue> Issues);
public sealed record PlaybackRuntimeSelection(string ComponentId, bool UsedFallback, IReadOnlyList<PlaybackComponentIssue> Issues);
public sealed class PlaybackComponentUnavailableException : InvalidOperationException
{
    public IReadOnlyList<PlaybackComponentIssue> Issues { get; }
    public PlaybackComponentUnavailableException(IEnumerable<PlaybackComponentIssue> issues)
        : base("播放组件不可用，请检查应用文件是否完整或选择兼容的播放组件。")
        => Issues = Array.AsReadOnly(issues.ToArray());
}

/// <summary>Explicit trusted registrations. Enumeration never invokes factories. This is not a disk
/// installer, assembly loader, sandbox or timeout boundary. A new registry represents new preferences;
/// already returned sessions stay owned by their callers.</summary>
public sealed class PlaybackComponentRegistry
{
    public const int ContractApiVersion = 1;
    public static Version HostVersion { get; } = new(0, 6, 0);
    private readonly Dictionary<string, PlaybackComponentRegistration> _registrations;
    private readonly PlaybackComponentRegistration _fallback;
    private readonly Action<PlaybackComponentIssue>? _diagnostic;
    private static readonly ConditionalWeakTable<IPlaybackSession, object> UsedSessions = new();
    private static readonly ConditionalWeakTable<IPlaybackComponentFactory, object> UsedFactories = new();
    private static readonly object SessionGate = new();

    public PlaybackComponentRegistry(IEnumerable<PlaybackComponentRegistration> registrations, string fallbackId,
        Action<PlaybackComponentIssue>? diagnostic = null)
        : this(registrations, diagnostic)
    {
        if (!_registrations.TryGetValue(fallbackId, out var fallback)) throw new ArgumentException("Default playback component must be registered.");
        _fallback = fallback;
    }

    /// <summary>Independent bundled fallback. An optional package may have the same component ID;
    /// its manifest/factory identity stays unchanged, while fallback remains a distinct registration.
    /// A null preferred ID always uses the bundled registration, never an optional same-ID candidate.</summary>
    public PlaybackComponentRegistry(IEnumerable<PlaybackComponentRegistration> optionalRegistrations,
        PlaybackComponentRegistration bundledFallback, Action<PlaybackComponentIssue>? diagnostic = null)
        : this(optionalRegistrations, diagnostic)
    {
        if (!ValidRegistration(bundledFallback) || !bundledFallback.Enabled) throw new ArgumentException("Invalid bundled fallback.");
        _fallback = bundledFallback;
    }

    private PlaybackComponentRegistry(IEnumerable<PlaybackComponentRegistration> registrations, Action<PlaybackComponentIssue>? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var snapshot = registrations.ToArray();
        if (snapshot.Length > 64) throw new ArgumentException("Invalid registration count.");
        _registrations = new(StringComparer.Ordinal);
        foreach (var registration in snapshot)
        {
            if (!ValidRegistration(registration) || !_registrations.TryAdd(registration.Descriptor.Id, registration))
                throw new ArgumentException("Invalid or duplicate playback registration.");
        }
        _fallback = null!; // Assigned by either public constructor before publishing this object.
        _diagnostic = diagnostic;
    }

    private static bool ValidRegistration(PlaybackComponentRegistration? registration) =>
        registration?.Descriptor is { } descriptor && registration.Activate is not null && ValidId(descriptor.Id) &&
        !string.IsNullOrWhiteSpace(descriptor.DisplayName) && descriptor.DisplayName.Length <= 80 &&
        descriptor.ComponentVersion is not null && descriptor.MinimumHostVersion is not null &&
        (descriptor.Capabilities & ~PlaybackCapabilities.CompletePlayer) == 0;

    private IEnumerable<(PlaybackComponentRegistration? Registration, bool UsedFallback)> Attempts(string? preferredId)
    {
        if (string.IsNullOrEmpty(preferredId)) { yield return (_fallback, false); yield break; }
        _registrations.TryGetValue(preferredId, out var candidate);
        yield return (candidate, false);
        if (!ReferenceEquals(candidate, _fallback)) yield return (_fallback, true);
    }

    public IReadOnlyList<PlaybackComponentStatus> Inspect(PlaybackCapabilities required)
    {
        ValidateRequirements(required);
        return Array.AsReadOnly(_registrations.Values.Append(_fallback).Distinct().Select(r => new PlaybackComponentStatus(
            r.Descriptor, r.Enabled, Check(r, required))).ToArray());
    }

    public PlaybackSelection Create(string? preferredId, PlaybackCapabilities required, SynchronizationContext? context = null)
    {
        ValidateRequirements(required);
        var issues = new List<PlaybackComponentIssue>();
        foreach (var (registration, usedFallback) in Attempts(preferredId))
        {
            if (registration is null) { issues.Add(PlaybackComponentIssue.NotRegistered); continue; }
            var issue = Check(registration, required);
            if (issue != PlaybackComponentIssue.None) { issues.Add(issue); continue; }
            IPlaybackComponentFactory? factory = null;
            IPlaybackSession? session = null;
            var ownsSession = false;
            var ownsFactory = false;
            try
            {
                issue = PlaybackComponentIssue.ActivationFailed;
                factory = registration.Activate() ?? throw new InvalidOperationException();
                issue = PlaybackComponentIssue.ReusedFactory;
                ClaimFactory(factory);
                ownsFactory = true;
                issue = PlaybackComponentIssue.DescriptorMismatch;
                if (factory.Descriptor != registration.Descriptor) throw new InvalidOperationException();
                issue = PlaybackComponentIssue.RuntimeUnavailable;
                factory.VerifyRuntime();
                issue = PlaybackComponentIssue.CreationFailed;
                session = factory.Create(context) ?? throw new InvalidOperationException();
                issue = PlaybackComponentIssue.ReusedSession;
                lock (SessionGate)
                {
                    if (UsedSessions.TryGetValue(session, out _)) throw new InvalidOperationException();
                    UsedSessions.Add(session, new object());
                    ownsSession = true;
                }
                issue = PlaybackComponentIssue.CreationFailed;
                if (session.IsPlaying || session.WantsPlayback) throw new InvalidOperationException();
                var owned = new OwnedPlaybackSession(session, factory, Report);
                session = null; factory = null;
                foreach (var finding in issues) Report(finding);
                return new(owned, registration.Descriptor.Id, usedFallback, Array.AsReadOnly(issues.ToArray()));
            }
            catch
            {
                issues.Add(issue);
                // A reused session belongs to an existing caller. Never dispose it as a failed candidate.
                if (ownsSession && session is not null) SafeDispose(session, PlaybackComponentIssue.SessionDisposeFailed);
                if (ownsFactory && factory is not null) SafeDispose(factory, PlaybackComponentIssue.FactoryDisposeFailed);
            }
        }
        foreach (var issue in issues) Report(issue);
        throw new PlaybackComponentUnavailableException(issues);
    }

    /// <summary>Preflight without creating a session or opening an audio device.</summary>
    public PlaybackRuntimeSelection VerifyRuntime(string? preferredId, PlaybackCapabilities required)
    {
        ValidateRequirements(required);
        var issues = new List<PlaybackComponentIssue>();
        foreach (var (registration, usedFallback) in Attempts(preferredId))
        {
            if (registration is null) { issues.Add(PlaybackComponentIssue.NotRegistered); continue; }
            var issue = Check(registration, required);
            if (issue != PlaybackComponentIssue.None) { issues.Add(issue); continue; }
            IPlaybackComponentFactory? factory = null;
            var ownsFactory = false;
            try
            {
                issue = PlaybackComponentIssue.ActivationFailed;
                factory = registration.Activate() ?? throw new InvalidOperationException();
                issue = PlaybackComponentIssue.ReusedFactory;
                ClaimFactory(factory);
                ownsFactory = true;
                issue = PlaybackComponentIssue.DescriptorMismatch;
                if (factory.Descriptor != registration.Descriptor) throw new InvalidOperationException();
                issue = PlaybackComponentIssue.RuntimeUnavailable;
                factory.VerifyRuntime();
                issue = PlaybackComponentIssue.FactoryDisposeFailed;
                ownsFactory = false;
                var verifiedFactory = factory;
                factory = null;
                verifiedFactory.Dispose();
                foreach (var finding in issues) Report(finding);
                return new(registration.Descriptor.Id, usedFallback, Array.AsReadOnly(issues.ToArray()));
            }
            catch { issues.Add(issue); }
            finally { if (ownsFactory && factory is not null) SafeDispose(factory, PlaybackComponentIssue.FactoryDisposeFailed); }
        }
        foreach (var issue in issues) Report(issue);
        throw new PlaybackComponentUnavailableException(issues);
    }

    private static void ClaimFactory(IPlaybackComponentFactory factory)
    {
        lock (SessionGate)
        {
            if (UsedFactories.TryGetValue(factory, out _)) throw new InvalidOperationException();
            UsedFactories.Add(factory, new object());
        }
    }

    private static PlaybackComponentIssue Check(PlaybackComponentRegistration r, PlaybackCapabilities required) =>
        !r.Enabled ? PlaybackComponentIssue.Disabled :
        r.Descriptor.ContractApiVersion != ContractApiVersion ? PlaybackComponentIssue.ApiMismatch :
        r.Descriptor.MinimumHostVersion > HostVersion ? PlaybackComponentIssue.HostTooOld :
        (r.Descriptor.Capabilities & required) != required ? PlaybackComponentIssue.MissingCapability : PlaybackComponentIssue.None;
    private static void ValidateRequirements(PlaybackCapabilities value)
    {
        if (value == PlaybackCapabilities.None || (value & ~PlaybackCapabilities.CompletePlayer) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 100 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private void SafeDispose(IDisposable target, PlaybackComponentIssue issue) { try { target.Dispose(); } catch { Report(issue); } }
    private void Report(PlaybackComponentIssue issue) { try { _diagnostic?.Invoke(issue); } catch { /* Diagnostics cannot break playback. */ } }
}
