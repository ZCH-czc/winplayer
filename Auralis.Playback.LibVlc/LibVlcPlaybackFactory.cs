namespace Auralis.Services;

public sealed class LibVlcPlaybackFactory : IPlaybackComponentFactory
{
    public PlaybackComponentDescriptor Descriptor { get; } = new(
        "auralis.playback.libvlc", "LibVLC", new Version(0, 4, 0), 1,
        new Version(0, 1, 0), PlaybackCapabilities.CompletePlayer);
    public void VerifyRuntime() => PlaybackEngine.VerifyNativeRuntime();
    public IPlaybackSession Create(SynchronizationContext? context) => PlaybackEngine.Create(context);
    public void Dispose() { } // Native ownership belongs to each independently created session.
}
