namespace Auralis.Services;

/// <summary>Backend-only media session. No platform, WPF, HWND or decoder types cross this boundary.
/// Calls are serialized by the owner; events are dispatched to its supplied synchronization context.
/// Source URIs are host-authorized backend resources, never main-WebView input or serialized UI state.</summary>
public interface IPlaybackSession : IDisposable
{
    event EventHandler? MediaOpened;
    event EventHandler? MediaEnded;
    event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed;
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    bool IsMuted { get; set; }
    double SpeedRatio { get; set; }
    bool IsPlaying { get; }
    bool WantsPlayback { get; }
    void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null);
    void Play();
    void Pause();
    void Restart();
    void Close();
    AudioPlaybackProfile CapturePlaybackProfile();
    void ApplyAudioOutputSettings(AudioOutputSettings settings);
    AudioDeviceSnapshot GetAudioDeviceSnapshot();
    AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot);
    /// <summary>Latest complete JPEG; null before first frame and after Close.
    /// Treat the returned buffer as immutable. MediaOpened alone is not first-frame readiness.</summary>
    byte[]? ReadVideoFrame();
}
