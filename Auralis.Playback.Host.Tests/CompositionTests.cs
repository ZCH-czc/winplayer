using Auralis.Playback.Host;
using Auralis.Services;

internal static class CompositionTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        var descriptor = new PlaybackComponentDescriptor("fixture.engine", "Synthetic", new(1, 0, 0), 1,
            new(0, 1, 0), PlaybackCapabilities.CompletePlayer);
        var bundledCalls = 0; var optionalCalls = 0;
        PlaybackComponentRegistration Bundled() => new(descriptor, true, () => { bundledCalls++; return new Factory(descriptor); });
        var next = descriptor with { ComponentVersion = new(2, 0, 0) };
        var optional = new PlaybackComponentRegistration(next, true, () => { optionalCalls++; return new Factory(next); });
        var registry = new PlaybackComponentRegistry([optional], Bundled());
        Check(registry.Inspect(PlaybackCapabilities.Audio).Count == 2 && optionalCalls == 0 && bundledCalls == 0, "same ID independent inert sources");
        using (var session = registry.Create(null, PlaybackCapabilities.Audio).Session) { }
        Check(optionalCalls == 0 && bundledCalls == 1, "null selection does not pick same-ID optional component");
        var selected = registry.Create(descriptor.Id, PlaybackCapabilities.Audio);
        using (selected.Session) { Check(!selected.UsedFallback && optionalCalls == 1, "explicit same-ID optional selected"); }
        foreach (var phase in new[] { "activate", "verify", "create" })
        {
            var failedCalls = 0;
            var broken = optional with { Activate = () => { failedCalls++; if (phase == "activate") throw new Exception(); return new Factory(next) { Phase = phase }; } };
            var composition = new PlaybackComponentComposition(new(PlaybackInstallationIssue.None, [broken], descriptor.Id, []), Bundled(), PlaybackCapabilities.CompletePlayer);
            if (phase != "create") composition.VerifyRuntime();
            using (var recovered = composition.Create()) { Check(!recovered.IsPlaying, "fallback creates stopped session"); }
            Check(composition.UsingBundled && composition.FellBack && composition.ActiveDescriptor == descriptor, "same-ID fallback exposes actual bundled metadata");
            using (var second = composition.Create()) { }
            composition.VerifyRuntime();
            Check(failedCalls == 1, "failed optional engine not retried by subsequent windows/preflight");
        }
        optionalCalls = 0;
        var noChoice = new PlaybackComponentComposition(new(PlaybackInstallationIssue.None, [optional], null, []), Bundled(), PlaybackCapabilities.Audio);
        noChoice.VerifyRuntime(); using (var defaultSession = noChoice.Create()) { }
        Check(optionalCalls == 0 && noChoice.UsingBundled && !noChoice.FellBack, "enabled alone is not selected");
        var corrupt = new PlaybackComponentComposition(new(PlaybackInstallationIssue.InvalidState, [optional], descriptor.Id, []), Bundled(), PlaybackCapabilities.Audio);
        using (var safe = corrupt.Create()) { }
        Check(optionalCalls == 0 && corrupt.UsingBundled, "bad startup state is fail-closed for optional code");
        var valid = new PlaybackComponentComposition(new(PlaybackInstallationIssue.None, [optional], descriptor.Id, []), Bundled(), PlaybackCapabilities.Audio);
        Check(valid.ActiveDescriptor == next && optionalCalls == 0, "composition metadata does not activate");
        valid.VerifyRuntime(); using (var live = valid.Create()) { live.Play(); Check(live.IsPlaying, "chosen component session control"); }
        Check(optionalCalls == 2 && !valid.UsingBundled && !valid.FellBack, "preflight and session use fresh selected factories");
        Console.WriteLine($"PASS playback composition: {checks} assertions; independent same-ID default, explicit opt-in, managed fallback latch, inert metadata, preflight and sessions.");
    }
}
