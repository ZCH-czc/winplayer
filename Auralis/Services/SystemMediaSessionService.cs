using System.IO;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Auralis.Services;

/// <summary>
/// Publishes Auralis playback to the Windows System Media Transport Controls (SMTC). Windows owns
/// the taskbar/quick-settings card; this class supplies bounded metadata, artwork, timeline state,
/// and routes the system buttons back to the existing Auralis playback pipeline.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed class SystemMediaSessionService : IDisposable
{
    private const int MaximumMetadataLength = 512;
    private static readonly TimeSpan TimelineRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private SystemMediaTransportControls? _controls;
    private Dispatcher? _dispatcher;
    private long _metadataRevision;
    private long _lastTimelineUpdateTick;
    private TimeSpan _lastDuration;
    private bool _lastPlaying;
    private int _disposed;

    internal event Action<SystemMediaCommand>? CommandRequested;

    internal event Action<TimeSpan>? PositionChangeRequested;

    internal bool IsAvailable => _controls is not null;

    internal bool Initialize(nint windowHandle, Dispatcher dispatcher)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (windowHandle == nint.Zero || _controls is not null)
        {
            return _controls is not null;
        }

        try
        {
            var controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            controls.IsEnabled = false;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;
            controls.IsStopEnabled = false;
            controls.IsFastForwardEnabled = false;
            controls.IsRewindEnabled = false;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.ButtonPressed += OnButtonPressed;
            controls.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
            _dispatcher = dispatcher;
            _controls = controls;
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or InvalidOperationException or
                                          NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media controls are unavailable: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Replaces the card metadata and returns a revision token that can be used to attach artwork
    /// asynchronously without allowing an old cover to overwrite a newer track.
    /// </summary>
    internal long UpdateMetadata(SystemMediaMetadata metadata)
    {
        var revision = Interlocked.Increment(ref _metadataRevision);
        var controls = _controls;
        if (controls is null || Volatile.Read(ref _disposed) != 0)
        {
            return revision;
        }

        try
        {
            var updater = controls.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = Normalize(metadata.MediaId, "auralis-current-track");
            updater.MusicProperties.Title = Normalize(metadata.Title, "未知歌曲");
            updater.MusicProperties.Artist = Normalize(metadata.Artist, "未知艺术家");
            updater.MusicProperties.AlbumTitle = Normalize(metadata.Album, "本地音乐");
            updater.Thumbnail = null;
            updater.Update();

            controls.IsEnabled = true;
            controls.PlaybackStatus = MediaPlaybackStatus.Changing;
            _lastDuration = TimeSpan.Zero;
            _lastTimelineUpdateTick = 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media metadata could not be updated: {exception.Message}");
        }

        return revision;
    }

    internal async Task UpdateArtworkAsync(
        long metadataRevision,
        string? artworkPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artworkPath) ||
            metadataRevision != Volatile.Read(ref _metadataRevision) ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(artworkPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(cancellationToken)
                .ConfigureAwait(false);
            var thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (metadataRevision != _metadataRevision || _controls is null || _disposed != 0)
                {
                    return;
                }

                _controls.DisplayUpdater.Thumbnail = thumbnail;
                _controls.DisplayUpdater.Update();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer track replaced this artwork request.
        }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or
                                          InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media artwork could not be updated: {exception.Message}");
        }
    }

    internal void UpdatePlayback(bool isPlaying, TimeSpan position, TimeSpan duration, double playbackRate)
    {
        var controls = _controls;
        if (controls is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            controls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            controls.PlaybackRate = Math.Clamp(playbackRate, 0.5, 2.0);

            var now = Environment.TickCount64;
            var durationChanged = Math.Abs((duration - _lastDuration).TotalMilliseconds) >= 250;
            var stateChanged = isPlaying != _lastPlaying;
            if (!durationChanged && !stateChanged &&
                now - _lastTimelineUpdateTick < TimelineRefreshInterval.TotalMilliseconds)
            {
                return;
            }

            var safeDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            var safePosition = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : safeDuration > TimeSpan.Zero && position > safeDuration
                    ? safeDuration
                    : position;
            var timeline = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = safePosition,
                MaxSeekTime = safeDuration,
                EndTime = safeDuration
            };
            controls.UpdateTimelineProperties(timeline);
            _lastTimelineUpdateTick = now;
            _lastDuration = safeDuration;
            _lastPlaying = isPlaying;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media playback state could not be updated: {exception.Message}");
        }
    }

    internal void Clear()
    {
        Interlocked.Increment(ref _metadataRevision);
        var controls = _controls;
        if (controls is null)
        {
            return;
        }

        try
        {
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
            controls.IsEnabled = false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media session could not be cleared: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var controls = _controls;
        _controls = null;
        if (controls is null)
        {
            return;
        }

        try
        {
            controls.ButtonPressed -= OnButtonPressed;
            controls.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
            controls.IsEnabled = false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media session could not finish shutting down: {exception.Message}");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemMediaCommand.Play,
            SystemMediaTransportControlsButton.Pause => SystemMediaCommand.Pause,
            SystemMediaTransportControlsButton.Next => SystemMediaCommand.Next,
            SystemMediaTransportControlsButton.Previous => SystemMediaCommand.Previous,
            _ => SystemMediaCommand.None
        };
        if (command != SystemMediaCommand.None)
        {
            Dispatch(() => CommandRequested?.Invoke(command));
        }
    }

    private void OnPlaybackPositionChangeRequested(
        SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        var requested = args.RequestedPlaybackPosition < TimeSpan.Zero
            ? TimeSpan.Zero
            : args.RequestedPlaybackPosition;
        Dispatch(() => PositionChangeRequested?.Invoke(requested));
    }

    private void Dispatch(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Input, action);
    }

    private static string Normalize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var filtered = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (filtered.Length == 0)
        {
            return fallback;
        }

        return filtered.Length <= MaximumMetadataLength
            ? filtered
            : filtered[..MaximumMetadataLength];
    }
}

internal sealed record SystemMediaMetadata(
    string MediaId,
    string Title,
    string Artist,
    string Album);

internal enum SystemMediaCommand
{
    None,
    Play,
    Pause,
    Previous,
    Next
}
