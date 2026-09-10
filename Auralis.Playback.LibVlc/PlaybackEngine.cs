namespace Auralis.Services;

/// <summary>Bundled default component composition, not a dynamic/untrusted plugin loader.</summary>
public static class PlaybackEngine
{
    public static IPlaybackSession Create(SynchronizationContext? context = null) => new BundledAudioPlayer(context);
    public static void VerifyNativeRuntime() => BundledAudioPlayer.VerifyNativeRuntime();
}
