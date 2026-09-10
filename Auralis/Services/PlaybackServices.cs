using System.Diagnostics;
using System.IO;
using Auralis.Playback.Host;

namespace Auralis.Services;

/// <summary>Explicit application composition; windows know neither engine IDs nor constructors.
/// Installed platform plugins cannot register playback factories or modify this default.</summary>
internal static class PlaybackServices
{
    internal static readonly PlaybackComponentRegistration Bundled = new(
        new("auralis.playback.libvlc", "LibVLC", new Version(0, 4, 0), 1,
            new Version(0, 1, 0), PlaybackCapabilities.CompletePlayer),
            true, static () => new LibVlcPlaybackFactory());
    internal static string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Auralis", "PlaybackComponents");
    private sealed record Runtime(PlaybackInstallationStore? Store, PlaybackInstallationState InitialState, PlaybackComponentComposition Composition);
    private static readonly Lazy<Runtime> Current = new(CreateRuntime);
    internal static PlaybackInstallationStore Installations => Current.Value.Store ?? throw new PlaybackInstallationException(PlaybackInstallationIssue.StorageFailure);
    internal static PlaybackInstallationState InitialState => Current.Value.InitialState;
    internal static PlaybackComponentComposition Composition => Current.Value.Composition;

    internal static IPlaybackSession Create(SynchronizationContext? context) =>
        Composition.Create(context);

    // Preserve the existing startup diagnostic before any main-window/native session is created.
    internal static void VerifyRuntime() => Composition.VerifyRuntime();

    private static Runtime CreateRuntime()
    {
        try
        {
            var store = new PlaybackInstallationStore(StorageRoot, PlaybackCapabilities.CompletePlayer);
            var initial = store.ReadState();
            var plan = store.ReadStartupPlan();
            return new(store, initial, new(plan, Bundled, PlaybackCapabilities.CompletePlayer, Report));
        }
        catch
        {
            // An inaccessible optional-component location must not prevent local startup.
            Debug.WriteLine("Playback installation state unavailable; using bundled component.");
            return new(null, new(PlaybackInstallationIssue.StorageFailure, [], null),
                new(new(PlaybackInstallationIssue.StorageFailure, [], null, []), Bundled, PlaybackCapabilities.CompletePlayer, Report));
        }
    }
    private static void Report(PlaybackComponentIssue issue) => Debug.WriteLine($"Playback component: {issue}");
}
