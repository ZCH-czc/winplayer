using Auralis.Services;

namespace Auralis.Playback.Host;

internal sealed class OwnedPlaybackSession(IPlaybackSession inner, IPlaybackComponentFactory factory,
    Action<PlaybackComponentIssue> report) : IPlaybackSession, IPlaybackAudioInformation
{
    public PlaybackAudioInformation? AudioInformation => (Inner as IPlaybackAudioInformation)?.AudioInformation;
    private int _disposed;
    private IPlaybackSession Inner { get { ObjectDisposedException.ThrowIf(_disposed != 0, this); return inner; } }
    public event EventHandler? MediaOpened { add => Inner.MediaOpened += value; remove => inner.MediaOpened -= value; }
    public event EventHandler? MediaEnded { add => Inner.MediaEnded += value; remove => inner.MediaEnded -= value; }
    public event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed { add => Inner.MediaFailed += value; remove => inner.MediaFailed -= value; }
    public TimeSpan Position { get => Inner.Position; set => Inner.Position = value; }
    public TimeSpan Duration => Inner.Duration;
    public double Volume { get => Inner.Volume; set => Inner.Volume = value; }
    public bool IsMuted { get => Inner.IsMuted; set => Inner.IsMuted = value; }
    public double SpeedRatio { get => Inner.SpeedRatio; set => Inner.SpeedRatio = value; }
    public bool IsPlaying => Inner.IsPlaying;
    public bool WantsPlayback => Inner.WantsPlayback;
    public void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null) =>
        Inner.Open(source, autoplay, positionSeconds, video, audioSlave);
    public void Play() => Inner.Play();
    public void Pause() => Inner.Pause();
    public void Restart() => Inner.Restart();
    public void Close() { if (_disposed == 0) inner.Close(); }
    public AudioPlaybackProfile CapturePlaybackProfile() => Inner.CapturePlaybackProfile();
    public void ApplyAudioOutputSettings(AudioOutputSettings settings) => Inner.ApplyAudioOutputSettings(settings);
    public AudioDeviceSnapshot GetAudioDeviceSnapshot() => Inner.GetAudioDeviceSnapshot();
    public AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot) => Inner.GetAudioEndpointProbeRequest(snapshot);
    public byte[]? ReadVideoFrame() => _disposed == 0 ? inner.ReadVideoFrame() : null;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { inner.Dispose(); } catch { report(PlaybackComponentIssue.SessionDisposeFailed); }
        try { factory.Dispose(); } catch { report(PlaybackComponentIssue.FactoryDisposeFailed); }
    }
}
