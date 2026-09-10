using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auralis.Playback.Host;
using Auralis.Services;

internal static class InstallationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Auralis-playback-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        async Task Reject(Func<Task> action, PlaybackInstallationIssue issue)
        {
            try { await action(); throw new Exception("operation unexpectedly accepted"); }
            catch (PlaybackInstallationException e) { Check(e.Issue == issue, "typed rejection: " + issue); }
        }
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "native"));
            File.WriteAllBytes(Path.Combine(source, "Fixture.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "native", "codec.dll"), [4, 5]);
            JsonObject Row(string path) { var bytes = File.ReadAllBytes(Path.Combine(source, path)); return new()
                { ["path"] = path, ["length"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) }; }
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1, ["kind"] = "playback", ["id"] = "fixture.engine", ["displayName"] = "Synthetic",
                ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0",
                ["runtimeIdentifier"] = "win-x64", ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Factory",
                ["capabilities"] = new JsonArray("audio"), ["files"] = new JsonArray(Row("Fixture.dll"), Row("native/codec.dll"))
            };
            var manifestPath = Path.Combine(source, PlaybackComponentManifest.FileName);
            void SaveManifest() => File.WriteAllText(manifestPath, manifest.ToJsonString());
            SaveManifest();
            var install = Path.Combine(root, "install");
            PlaybackInstallationStore Store() => new(install, PlaybackCapabilities.Audio);
            var store = Store();
            Check(store.ReadState().Components.Count == 0 && !Directory.Exists(install), "empty reads do not create state");
            var preview = await store.PreviewAsync(source + Path.DirectorySeparatorChar);
            var grant = new PlaybackPackageApproval(preview.Descriptor.Id, preview.ManifestSha256);
            Check(preview.FileCount == 2 && preview.PayloadBytes == 5 && !Directory.Exists(install), "inert preview without installation");
            await Reject(() => store.ImportApprovedAsync(preview, grant with { ManifestSha256 = new string('0', 64) }), PlaybackInstallationIssue.ApprovalRequired);
            await Reject(() => Store().ImportApprovedAsync(preview, grant), PlaybackInstallationIssue.ApprovalRequired);
            Check(!Directory.Exists(install), "wrong or foreign approval does not write");
            File.WriteAllBytes(Path.Combine(source, "Fixture.dll"), [3, 2, 1]);
            await Reject(() => store.ImportApprovedAsync(preview, grant), PlaybackInstallationIssue.InvalidPackage);
            Check(store.ReadState().Components.Count == 0, "post-preview tamper cannot create receipt");
            File.WriteAllBytes(Path.Combine(source, "Fixture.dll"), [1, 2, 3]);
            var item = await store.ImportApprovedAsync(preview, grant);
            Check(!item.Enabled && store.ReadStartupPlan().Registrations.Count == 0, "import defaults off");
            var revision = Path.Combine(install, "revisions", item.ManifestSha256);
            Check(File.ReadAllBytes(Path.Combine(revision, "native", "codec.dll")).SequenceEqual(new byte[] { 4, 5 }), "nested payload copied");
            Check(Store().ReadState().Components.Single() == item, "receipt survives fresh store");
            await Reject(() => store.SelectAsync(item.ComponentId), PlaybackInstallationIssue.UnknownComponent);
            await Reject(() => store.SetEnabledAsync("fixture.missing", true), PlaybackInstallationIssue.UnknownComponent);
            await store.SetEnabledAsync(item.ComponentId, true); await store.SelectAsync(item.ComponentId);
            var boot = Store().ReadStartupPlan();
            Check(boot.Registrations.Count == 1 && boot.SelectedId == item.ComponentId, "persisted enabled selection creates inert registration");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture"), "invalid synthetic DLL was never reflected/loaded");
            await store.ImportApprovedAsync(preview, grant);
            Check(store.ReadState().SelectedId == item.ComponentId && store.ReadState().Components.Single().Enabled, "identical import preserves selection");
            await store.SetEnabledAsync(item.ComponentId, false);
            Check(Store().ReadStartupPlan().Registrations.Count == 0 && store.ReadState().SelectedId is null && boot.Registrations.Count == 1,
                "disable clears next-boot selection without mutating old plan");
            File.WriteAllBytes(Path.Combine(revision, "Fixture.dll"), [3, 2, 1]);
            await Reject(() => store.SetEnabledAsync(item.ComponentId, true), PlaybackInstallationIssue.InvalidPackage);
            await Reject(() => store.ImportApprovedAsync(preview, grant), PlaybackInstallationIssue.InvalidPackage);
            Check(!store.ReadState().Components.Single().Enabled, "corrupt installed revision is neither overwritten nor enabled");
            File.WriteAllBytes(Path.Combine(revision, "Fixture.dll"), [1, 2, 3]);
            await store.SetEnabledAsync(item.ComponentId, true); await store.SelectAsync(item.ComponentId);
            var old = File.ReadAllBytes(Path.Combine(revision, PlaybackComponentManifest.FileName));
            using (var inUse = new FileStream(Path.Combine(revision, "Fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                manifest["version"] = "1.1.0"; SaveManifest();
                var update = await store.PreviewAsync(source);
                await Reject(() => store.ImportApprovedAsync(update, grant), PlaybackInstallationIssue.ApprovalRequired);
                var updated = await store.ImportApprovedAsync(update, new(update.Descriptor.Id, update.ManifestSha256));
                Check(!updated.Enabled && updated.ManifestSha256 != item.ManifestSha256 && store.ReadState().SelectedId is null, "new revision needs separate grant and enable");
                Check(File.ReadAllBytes(Path.Combine(revision, PlaybackComponentManifest.FileName)).SequenceEqual(old), "update preserves locked old revision");
                Check(store.ReadState().Components.Count == 1 && Directory.EnumerateDirectories(Path.Combine(install, "revisions")).Count() == 2, "one active receipt, retained immutable history");
            }
            var statePath = Path.Combine(install, "playback-installations.json");
            var good = File.ReadAllText(statePath);
            using (var otherWriter = new FileStream(Path.Combine(install, "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Reject(() => Store().SelectAsync(null), PlaybackInstallationIssue.Busy);
            Check(File.ReadAllText(statePath) == good, "concurrent writer does not lose saved state");
            if (OperatingSystem.IsWindows())
            {
                using (var blockedCommit = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Reject(() => store.SelectAsync(null), PlaybackInstallationIssue.StorageFailure);
                Check(File.ReadAllText(statePath) == good, "failed atomic replacement preserves previous receipt");
            }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await store.SelectAsync(null, cancelled.Token); throw new Exception("cancel ignored"); }
                catch (OperationCanceledException) { checks++; }
            }
            Check(File.ReadAllText(statePath) == good, "cancel preserves receipt");
            foreach (var bad in new[] { "{", "{}", good.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                good.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), good.Replace("\"selectedId\":null", "\"selectedId\":\"fixture.missing\""),
                good.Replace("\"enabled\":false", "\"enabled\":false,\"unexpected\":true"),
                good.Replace("\"id\":\"fixture.engine\"", "\"id\":\"../outside\""),
                good.Replace("\"approvedManifestSha256\":\"", "\"approvedManifestSha256\":\"../") })
            {
                File.WriteAllText(statePath, bad);
                Check(store.ReadState().Issue == PlaybackInstallationIssue.InvalidState && store.ReadStartupPlan().Registrations.Count == 0, "bad receipt fails closed");
                await Reject(() => store.SelectAsync(null), PlaybackInstallationIssue.InvalidState);
                Check(File.ReadAllText(statePath) == bad, "bad state is not silently replaced");
            }
            File.WriteAllText(statePath, good);
            var lockPath = Path.Combine(install, "install.lock");
            File.Delete(lockPath); // Only the generated test store lock, no writer is active.
            var linkCreated = false;
            try
            {
                File.CreateSymbolicLink(lockPath, manifestPath); linkCreated = true;
                await Reject(() => store.SelectAsync(null), PlaybackInstallationIssue.StorageFailure);
                Check(File.ReadAllText(manifestPath) == manifest.ToJsonString(), "reparse lock cannot overwrite outside state");
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("NOT TESTED installation symbolic link: creation unavailable."); }
            finally { if (linkCreated) File.Delete(lockPath); }
            var latest = store.ReadState().Components.Single();
            await store.SetEnabledAsync(latest.ComponentId, true); await store.SelectAsync(latest.ComponentId);
            var latestManifest = Path.Combine(install, "revisions", latest.ManifestSha256, PlaybackComponentManifest.FileName);
            File.AppendAllText(latestManifest, " ");
            var rejected = Store().ReadStartupPlan();
            Check(rejected.Registrations.Count == 0 && rejected.SelectedId is null && rejected.RejectedComponentIds.SequenceEqual(new[] { latest.ComponentId }), "changed manifest cannot inherit grant");
            Check(store.ReadState().Components.Single().Enabled, "bad package leaves receipt intact for diagnosis");
            await store.SetEnabledAsync(latest.ComponentId, false);
            Check(!store.ReadState().Components.Single().Enabled, "can disable broken component");
            Console.WriteLine($"PASS playback installation: {checks} assertions; explicit approval, immutable revisions, inert startup, restart state, failure/cancel/concurrency. Synthetic files only.");
        }
        finally
        {
            // Only this invocation's random generated directory; no user paths and no followed links.
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(root).StartsWith("Auralis-playback-install-", StringComparison.Ordinal) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0) Directory.Delete(root, recursive: true);
        }
    }
}
