using Auralis.Services;

namespace SyntheticPlayback;

public class Factory : IPlaybackComponentFactory
{
    public Factory() => Mark("activate");
    public virtual PlaybackComponentDescriptor Descriptor => new("fixture.loaded", "Synthetic", new(1, 0, 0), 1, new(0, 1, 0), PlaybackCapabilities.CompletePlayer);
    public virtual void VerifyRuntime() => Mark("verify");
    public IPlaybackSession Create(SynchronizationContext? context) { Mark("create"); return new FixtureSession(); }
    public void Dispose() => Mark("factoryDispose");
    internal static void Mark(string value) => AppContext.SetData("Auralis.Playback.Test.Events", (AppContext.GetData("Auralis.Playback.Test.Events") as string ?? "") + value + ";");
}
public sealed class WrongDescriptor : Factory
{ public override PlaybackComponentDescriptor Descriptor => base.Descriptor with { Id = "fixture.wrong" }; }
public sealed class BrokenRuntime : Factory
{ public override void VerifyRuntime() => throw new Exception("never-leak-private-test-material"); }
public sealed class BrokenConstructor : Factory
{ public BrokenConstructor() => throw new Exception("never-leak-private-test-material"); }
public sealed class HostDependency : Factory
{ public override void VerifyRuntime() => Mark(Auralis.Playback.Host.PlaybackComponentRegistry.HostVersion.ToString()); }
public sealed class NotAFactory { }
internal sealed class PrivateFactory : Factory { }

internal sealed class FixtureSession : IPlaybackSession
{
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded { add { } remove { } }
    public event EventHandler<AudioPlaybackFailedEventArgs>? MediaFailed { add { } remove { } }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.FromMinutes(1);
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
    public double SpeedRatio { get; set; }
    public bool IsPlaying => WantsPlayback;
    public bool WantsPlayback { get; private set; }
    public void Open(Uri source, bool autoplay = true, double positionSeconds = 0, bool video = false, Uri? audioSlave = null)
    { Position = TimeSpan.FromSeconds(positionSeconds); WantsPlayback = autoplay; MediaOpened?.Invoke(this, EventArgs.Empty); }
    public void Play() => WantsPlayback = true;
    public void Pause() => WantsPlayback = false;
    public void Restart() { Position = TimeSpan.Zero; Play(); }
    public void Close() => Pause();
    public AudioPlaybackProfile CapturePlaybackProfile() => new(AudioOutputSettings.Default, Volume, SpeedRatio);
    public void ApplyAudioOutputSettings(AudioOutputSettings settings) { }
    public AudioDeviceSnapshot GetAudioDeviceSnapshot() => new([], [], AudioOutputSettings.Default, "", "", "", "", AudioEndpointLatencyInfo.Unavailable("fixture"));
    public AudioEndpointProbeRequest? GetAudioEndpointProbeRequest(AudioDeviceSnapshot snapshot) => null;
    public byte[]? ReadVideoFrame() => null;
    public void Dispose() => Factory.Mark("sessionDispose");
}
