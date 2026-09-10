namespace Auralis;

/// <summary>
/// Keeps the taskbar surface opt-in while allowing an enabled surface to wait
/// for the first playback event. A paused track remains reachable afterwards.
/// </summary>
internal sealed class TaskbarWidgetVisibilityPolicy
{
    private bool _enabled;
    private bool _autoShowOnPlayback;
    private bool _isPlaying;
    private bool _shownForPlaybackSession;

    internal bool ShouldShow =>
        _enabled && (!_autoShowOnPlayback || _isPlaying || _shownForPlaybackSession);

    internal void ApplyOptions(bool enabled, bool autoShowOnPlayback)
    {
        var wasEnabled = _enabled;
        _enabled = enabled;
        _autoShowOnPlayback = autoShowOnPlayback;

        if (!enabled)
        {
            _shownForPlaybackSession = false;
        }
        else if (!wasEnabled && _isPlaying)
        {
            _shownForPlaybackSession = true;
        }
    }

    internal void UpdatePlayback(bool isPlaying)
    {
        _isPlaying = isPlaying;
        if (_enabled && isPlaying)
        {
            _shownForPlaybackSession = true;
        }
    }
}
