using Auralis.Platform.Abstractions;
using Auralis.Services;

internal static class VideoQualitySelectionTests
{
    internal static void Run()
    {
        static void Check(bool ok) { if (!ok) throw new InvalidOperationException("Video quality scope/validation failed"); }
        var selection = new VideoQualitySelection();
        var lease = new PlatformVideoLease(new Uri("https://example.invalid/private"), null, "video/mp4") {
            Qualities = [new("64", "720p"), new("80", "1080p")], SelectedQualityId = "64" };
        selection.Update("track-a", lease);
        var token = selection.Options[1].Handle;
        Check(selection.Options.Count == 2 && selection.TryResolve("track-a", token, out var id) && id == "80");
        Check(!selection.TryResolve("track-b", token, out _) && !selection.TryResolve("track-a", "80", out _));
        Check(selection.TryResolve("track-b", null, out var automatic) && automatic is null);
        selection.Update("track-a", lease);
        Check(!selection.TryResolve("track-a", token, out _));
        foreach (var bad in new[] { lease with { SelectedQualityId = "missing" },
            lease with { Qualities = [new("64", "720p"),new("64", "duplicate")] },
            lease with { Qualities = [new("https://private", "secret")] },
            lease with { Qualities = [new("64", "bad\nlabel")] } }) {
            selection.Update("track-a", bad); Check(selection.Options.Count == 0);
        }
        selection.Update("track-a", new(new Uri("https://example.invalid/legacy"), null, null));
        Check(selection.Options.Count == 0); selection.Clear();
        Console.WriteLine("PASS video quality opaque handles / stale / legacy / metadata validation");
    }
}
