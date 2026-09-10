using Auralis.Services;

namespace Auralis.Playback.Host;

/// <summary>One process-start composition. Does not reread preferences between sessions. Managed
/// preflight/creation fallback is latched so subsequent windows do not retry the failed optional engine.
/// It is not native crash isolation or switching of an already-playing session.</summary>
public sealed class PlaybackComponentComposition
{
    private readonly object _gate = new();
    private readonly PlaybackComponentRegistry _registry;
    private readonly PlaybackComponentDescriptor _bundled;
    private readonly PlaybackComponentDescriptor? _optional;
    private readonly string? _preferred;
    private readonly PlaybackCapabilities _required;
    private bool _fellBack;
    public bool UsingBundled { get { lock (_gate) return _preferred is null || _fellBack; } }
    public bool FellBack { get { lock (_gate) return _fellBack; } }
    public PlaybackComponentDescriptor ActiveDescriptor { get { lock (_gate) return UsingBundled ? _bundled : _optional!; } }

    public PlaybackComponentComposition(PlaybackStartupPlan plan, PlaybackComponentRegistration bundled,
        PlaybackCapabilities required, Action<PlaybackComponentIssue>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(bundled);
        _bundled = bundled.Descriptor; _required = required;
        var selected = plan.Issue == PlaybackInstallationIssue.None && plan.SelectedId is not null
            ? plan.Registrations.SingleOrDefault(r => r.Descriptor.Id == plan.SelectedId) : null;
        _preferred = selected?.Descriptor.Id; _optional = selected?.Descriptor;
        _registry = new(selected is null ? [] : [selected], bundled, diagnostic);
        // Even enabled packages are not considered unless the user selected that exact component.
    }
    public IPlaybackSession Create(SynchronizationContext? context = null)
    {
        lock (_gate)
        {
            var result = _registry.Create(_fellBack ? null : _preferred, _required, context);
            _fellBack |= result.UsedFallback;
            return result.Session;
        }
    }
    public void VerifyRuntime()
    {
        lock (_gate)
        {
            var result = _registry.VerifyRuntime(_fellBack ? null : _preferred, _required);
            _fellBack |= result.UsedFallback;
        }
    }
}
