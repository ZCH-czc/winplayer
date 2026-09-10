using System.IO;
using System.Reflection;
using Auralis.MediaTransport.Host;
using Auralis.Services;

namespace Auralis;

internal static class IsolationChecks
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks++;
        }
        bool DefaultInitialized()
        {
            var lazy = typeof(MediaTransportServices).GetField("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            return (bool)lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!;
        }
        // Headless checks: do not construct Application/WebView, simulate clicks or access real user state.
        var root = Path.Combine(AppContext.BaseDirectory, "isolation-checks", Guid.NewGuid().ToString("N"));
        try
        {
            Check(!DefaultInitialized(), "production lazy starts dormant");
            var first = MediaTransportServices.CreateRuntime(Path.Combine(root, "first"));
            Check(first.Store is not null && first.InitialState.Issue == MediaTransportInstallationIssue.None, "explicit missing store valid");
            Check(first.InitialState.Components.Count == 0 && first.InitialState.SelectedId is null, "no invented components");
            Check(first.Composition.UsingBundled && !first.Composition.FellBack, "empty selection uses default without failure");
            Check(!first.Composition.Activated, "metadata does not activate component");
            Check(first.StorageRoot == Path.Combine(root, "first"), "explicit root retained");
            Check(!Directory.Exists(root), "composition and discovery did not create storage");
            var second = MediaTransportServices.CreateRuntime(Path.Combine(root, "second"));
            Check(!ReferenceEquals(first.Store, second.Store) && !ReferenceEquals(first.Composition, second.Composition), "independent runtimes");
            Check(!Directory.Exists(root) && !DefaultInitialized(), "second fixture leaves production lazy dormant");
            Directory.CreateDirectory(second.StorageRoot);
            File.WriteAllText(Path.Combine(second.StorageRoot, "transport-installations.json"), "invalid synthetic receipt");
            var restarted = MediaTransportServices.CreateRuntime(second.StorageRoot);
            Check(restarted.InitialState.Issue != MediaTransportInstallationIssue.None, "fresh composition observes invalid receipt");
            Check(restarted.Composition.UsingBundled && !restarted.Composition.Activated, "invalid optional receipt leaves bundled choice dormant");
            Check(File.ReadAllText(Path.Combine(second.StorageRoot, "transport-installations.json")) == "invalid synthetic receipt", "invalid receipt not silently reset");
            Check(second.InitialState.Issue == MediaTransportInstallationIssue.None && !second.Composition.FellBack, "existing startup snapshot not changed");
            Check(!Directory.Exists(first.StorageRoot), "other fixture untouched");
            Check(!DefaultInitialized(), "all checks leave real application runtime uninitialized");
            Console.WriteLine($"PASS isolated production composition: {checks} checks; no Application, WebView, user state, credentials or component activation.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL isolated production composition after {checks} checks: {e.GetType().Name}");
            return 1;
        }
    }
}
