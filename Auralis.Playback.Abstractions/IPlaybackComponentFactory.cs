namespace Auralis.Services;

[Flags]
public enum PlaybackCapabilities
{
    None = 0, Audio = 1, VideoFrames = 2, SeparateAudio = 4, Seek = 8,
    Rate = 16, OutputDevices = 32, Mute = 64,
    CompletePlayer = Audio | VideoFrames | SeparateAudio | Seek | Rate | OutputDevices | Mute
}

/// <summary>Immutable metadata supplied before activation; no filesystem, URLs or native types.</summary>
public sealed record PlaybackComponentDescriptor(
    string Id, string DisplayName, Version ComponentVersion, int ContractApiVersion,
    Version MinimumHostVersion, PlaybackCapabilities Capabilities);

/// <summary>Trusted backend factory. Verification must not start playback. Each Create returns a fresh,
/// independently owned, stopped session; activation must return a fresh factory, not a singleton.
/// The host disposes the session before its factory. Failure before returning
/// a session must clean up partial native resources. This interface is not a security sandbox.</summary>
public interface IPlaybackComponentFactory : IDisposable
{
    PlaybackComponentDescriptor Descriptor { get; }
    void VerifyRuntime();
    IPlaybackSession Create(SynchronizationContext? context);
}
