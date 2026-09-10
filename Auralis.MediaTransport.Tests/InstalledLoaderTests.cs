using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

internal static class InstalledLoaderTests
{
    // Runs inside the existing bounded loader child. Parent cleans only after process exit,
    // because collectible contexts do not promise immediate release of mapped DLL images.
    public static async Task RunAsync(string root, string fixturePackage)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        string Events() => (string?)AppContext.GetData("Auralis.Transport.Test.Events") ?? "";
        var install = Path.Combine(root, "installed-fixture");
        MediaTransportInstallationStore Store() => new(install, MediaTransportCapabilities.Full);
        var store = Store();
        AppContext.SetData("Auralis.Transport.Test.Events", "");
        var archive = Path.Combine(root, "fixture.auralis-transport.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(fixturePackage, archive);
        var preview = await store.PreviewArchiveAsync(archive);
        var first = await store.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256));
        await preview.DisposeAsync();
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(install, "previews")).Any(), "real ZIP preview cleaned after installed copy");
        Check(!first.Enabled && Store().ReadStartupPlan().Registrations.Count == 0 && Events() == "", "installed import is inert and off");
        await store.SetEnabledAsync(first.ComponentId, true); await store.SelectAsync(first.ComponentId);
        var boot = Store().ReadStartupPlan();
        Check(boot.SelectedId == first.ComponentId && boot.Registrations.Count == 1 && Events() == "", "fresh store restores inert approved boot plan");
        var bundled = new MediaTransportRegistration(HttpMediaTransportFactory.Metadata, true, () => new HttpMediaTransportFactory());
        var composition = new MediaTransportComponentComposition(boot, bundled, MediaTransportCapabilities.Full);
        var context = new MediaTransportContext(Path.Combine(root, "installed-cache"), new MediaTransferBudget());
        var active = composition.CreateDeferred(context);
        var request = new MediaTransportRequest(new Uri("https://fixture.invalid/no-request"), null, null, "synthetic");
        var oldPath = Path.Combine(install, "revisions", first.ManifestSha256, "Auralis.MediaTransport.Fixture.dll");
        try
        {
            await using (var pin = await active.PrepareAsync(request, null, default))
                Check(!composition.FellBack && composition.Activated && pin.Source.LocalPath == oldPath, "startup composition loads exact immutable installed revision, not import source");
            var oldManifestPath = Path.Combine(Path.GetDirectoryName(oldPath)!, MediaTransportComponentManifest.FileName);
            var oldManifest = File.ReadAllBytes(oldManifestPath);
            var sourceManifest = Path.Combine(fixturePackage, MediaTransportComponentManifest.FileName);
            // Whitespace changes the exact receipt revision while retaining the real factory descriptor.
            File.AppendAllText(sourceManifest, "\n");
            var updateArchive = Path.Combine(root, "update.auralis-transport.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(fixturePackage, updateArchive);
            var update = await store.PreviewArchiveAsync(updateArchive);
            var second = await store.ImportApprovedAsync(update, new(update.Descriptor.Id, update.ManifestSha256));
            await update.DisposeAsync();
            Check(second.ManifestSha256 != first.ManifestSha256 && !second.Enabled && Store().ReadStartupPlan().SelectedId is null,
                "new installed revision requires enable/selection and does not inherit trust state");
            Check(File.ReadAllBytes(oldManifestPath).SequenceEqual(oldManifest), "update cannot overwrite active old manifest");
            await using (var pin = await active.PrepareAsync(request, null, default))
                Check(pin.Source.LocalPath == oldPath, "existing session survives imported revision without hot replacement");
            await store.SetEnabledAsync(second.ComponentId, true); await store.SelectAsync(second.ComponentId);
            var nextBoot = Store().ReadStartupPlan();
            Check(nextBoot.SelectedId == second.ComponentId && boot.Registrations[0].Descriptor == nextBoot.Registrations[0].Descriptor,
                "new boot plan independently captures the newly approved bytes");
            var nextComposition = new MediaTransportComponentComposition(nextBoot, bundled, MediaTransportCapabilities.Full);
            var next = nextComposition.CreateDeferred(context);
            try
            {
                await using (var pin = await next.PrepareAsync(request, null, default))
                    Check(!nextComposition.FellBack && pin.Source.LocalPath == Path.Combine(install, "revisions", second.ManifestSha256, Path.GetFileName(oldPath)),
                        "new composition executes new revision while old revision remains active");
                await store.SetEnabledAsync(second.ComponentId, false);
                Check(Store().ReadStartupPlan().Registrations.Count == 0 && Store().ReadStartupPlan().SelectedId is null,
                    "disabled receipt excludes component from future composition");
                await using (var pin = await next.PrepareAsync(request, null, default))
                    Check(File.Exists(pin.Source.LocalPath), "restart-only disable does not dispose somebody else's active session");
            }
            finally { await next.DisposeAsync(); }
            await store.SetEnabledAsync(second.ComponentId, true); await store.SelectAsync(second.ComponentId);
            var captured = Store().ReadStartupPlan();
            File.AppendAllText(Path.Combine(install, "revisions", second.ManifestSha256, MediaTransportComponentManifest.FileName), " ");
            var fallback = await new MediaTransportRegistry(captured.Registrations, bundled).CreateAsync(captured.SelectedId, context, MediaTransportCapabilities.Full);
            try { Check(fallback.UsedFallback && fallback.Issues.Contains(MediaTransportIssue.ActivationFailed), "post-plan tamper fails at activation and retains independent default"); }
            finally { await fallback.Session.DisposeAsync(); }
            Check(Store().ReadStartupPlan().RejectedComponentIds.SequenceEqual(new[] { second.ComponentId }), "changed installed manifest rejected by next startup discovery");
        }
        finally { await active.DisposeAsync(); }
        Check(Events().Split("factoryClosed;").Length - 1 == 2, "both real installed factories closed exactly once");
        Console.WriteLine($"PASS installed transport loader: {checks} assertions; real immutable revisions, persisted startup, live old-session upgrade and safe default. Synthetic fixture only.");
    }
}
