using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auralis.Playback.Host;
using Auralis.Services;

internal static class LoaderTests
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT TESTED loader: Windows file-lease behavior requires Windows."); return; }
        var root = Path.Combine(Path.GetTempPath(), "Auralis-loaded-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // CLR may retain mapped images until process exit even after Unload; use a bounded child
            // for deterministic cleanup instead of promising unload timing or leaving fixtures behind.
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(LoaderTests).Assembly.Location);
            start.ArgumentList.Add("--loader-worker"); start.ArgumentList.Add(root);
            using var child = System.Diagnostics.Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); throw new Exception("Loader fixture timed out"); }
            Console.Write(output.GetAwaiter().GetResult());
            if (child.ExitCode != 0) throw new Exception("Loader fixture failed: " + error.GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void RunPackageTests(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("Auralis-loaded-fixture-", StringComparison.Ordinal) ||
            (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(fullRoot).Any())
            throw new InvalidOperationException("Loader worker requires an empty synthetic temporary directory.");
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
            var packageRoot = Path.Combine(root, "source"); Directory.CreateDirectory(packageRoot);
            var dll = Path.Combine(packageRoot, "Auralis.Playback.Fixture.dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture-payload", Path.GetFileName(dll)), dll);
            var bytes = File.ReadAllBytes(dll);
            var json = new JsonObject
            {
                ["schemaVersion"] = 1, ["kind"] = "playback", ["id"] = "fixture.loaded", ["displayName"] = "Synthetic",
                ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0",
                ["runtimeIdentifier"] = "win-x64", ["entryAssembly"] = Path.GetFileName(dll), ["entryType"] = "SyntheticPlayback.Factory",
                ["capabilities"] = new JsonArray("audio", "videoFrames", "separateAudio", "seek", "rate", "outputDevices", "mute"),
                ["files"] = new JsonArray(new JsonObject { ["path"] = Path.GetFileName(dll), ["length"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) })
            };
            var manifest = Path.Combine(packageRoot, PlaybackComponentManifest.FileName);
            void Write() => File.WriteAllText(manifest, json.ToJsonString());
            PlaybackPackageSnapshot Snapshot() => PlaybackComponentCatalog.Discover(packageRoot, PlaybackCapabilities.CompletePlayer).Single().Package!;
            PlaybackComponentRegistration Registration() { var p = Snapshot(); return PlaybackPackageLoader.CreateRegistration(p, new(p.Manifest.Descriptor.Id, p.ManifestSha256), true); }
            Write();
            var snapshot = Snapshot();
            AppContext.SetData("Auralis.Playback.Test.Events", "");
            try { PlaybackPackageLoader.CreateRegistration(snapshot, new("wrong", snapshot.ManifestSha256), true); throw new Exception("unapproved"); }
            catch (PlaybackPackageLoadException e) { Check(e.Issue == PlaybackPackageLoadIssue.ApprovalMismatch, "approval pins id"); }
            try { PlaybackPackageLoader.CreateRegistration(snapshot, new(snapshot.Manifest.Descriptor.Id, new string('0', 64)), true); throw new Exception("unapproved hash"); }
            catch (PlaybackPackageLoadException e) { Check(e.Issue == PlaybackPackageLoadIssue.ApprovalMismatch, "approval pins manifest"); }
            var registration = Registration();
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "", "registration inert");
            var registry = new PlaybackComponentRegistry([registration], registration.Descriptor.Id);
            registry.Inspect(PlaybackCapabilities.Audio);
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "", "inspect inert");
            registry.VerifyRuntime(null, PlaybackCapabilities.Audio);
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "activate;verify;factoryDispose;", "preflight activation/disposal no session");
            AppContext.SetData("Auralis.Playback.Test.Events", "");
            var session = registry.Create(null, PlaybackCapabilities.Audio).Session;
            var opened = 0; session.MediaOpened += (_, _) => opened++;
            session.Open(new Uri("https://example.invalid/fixture"), false, 7);
            session.Volume = .32; session.SpeedRatio = 1.5; session.Play();
            Check(opened == 1 && session.IsPlaying && session.Position.TotalSeconds == 7 && session.Volume == .32, "real loaded contract identity/events/controls");
            try { using var write = new FileStream(dll, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new Exception("file not leased"); }
            catch (IOException) { checks++; }
            try { File.WriteAllText(manifest, "{}"); throw new Exception("manifest not leased"); }
            catch (IOException) { checks++; }
            session.Dispose(); session.Dispose();
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "activate;verify;create;sessionDispose;factoryDispose;", "owned session before factory once");
            // A successful disposal releases the ordinary read lease. CLR module mappings may remain.
            Write(); Check(Snapshot().ManifestSha256 == snapshot.ManifestSha256, "manifest lease released");
            var pending = Registration();
            json["displayName"] = "Changed"; Write();
            try { pending.Activate(); throw new Exception("stale grant"); }
            catch (PlaybackPackageLoadException e) { Check(e.Issue == PlaybackPackageLoadIssue.IntegrityFailed && !e.ToString().Contains(root), "changed snapshot rejected privately"); }
            json["displayName"] = "Synthetic"; Write();
            var installRoot = Path.Combine(root, "installed");
            var installer = new PlaybackInstallationStore(installRoot, PlaybackCapabilities.CompletePlayer);
            AppContext.SetData("Auralis.Playback.Test.Events", "");
            var preview = installer.PreviewAsync(packageRoot).GetAwaiter().GetResult();
            installer.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256)).GetAwaiter().GetResult();
            Check(installer.ReadStartupPlan().Registrations.Count == 0, "real imported DLL defaults off");
            installer.SetEnabledAsync(preview.Descriptor.Id, true).GetAwaiter().GetResult();
            installer.SelectAsync(preview.Descriptor.Id).GetAwaiter().GetResult();
            var plan = new PlaybackInstallationStore(installRoot, PlaybackCapabilities.CompletePlayer).ReadStartupPlan();
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "", "import, approval, enable and startup plan do not activate");
            var installedHost = new PlaybackComponentRegistry(plan.Registrations, plan.SelectedId!);
            using (var installedSession = installedHost.Create(plan.SelectedId, PlaybackCapabilities.CompletePlayer).Session)
            {
                installedSession.Open(new Uri("https://example.invalid/synthetic"), false, 12);
                Check(installedSession.Position.TotalSeconds == 12 && !installedSession.IsPlaying, "receipt to installed DLL to live session");
                installer.SetEnabledAsync(preview.Descriptor.Id, false).GetAwaiter().GetResult();
                installedSession.Play();
                Check(installedSession.IsPlaying && installer.ReadStartupPlan().Registrations.Count == 0, "disable changes next startup, not live session");
            }
            Check((string)AppContext.GetData("Auralis.Playback.Test.Events")! == "activate;verify;create;sessionDispose;factoryDispose;", "installed component lifetime owned");
            var baseline = new PlaybackComponentDescriptor("fixture.default", "Default", new(1, 0, 0), 1, new(0, 1, 0), PlaybackCapabilities.CompletePlayer);
            foreach (var type in new[] { "NotAFactory", "PrivateFactory", "BrokenConstructor", "WrongDescriptor", "BrokenRuntime", "MissingType", "HostDependency" })
            {
                json["entryType"] = "SyntheticPlayback." + type; Write();
                var alternate = Registration();
                var recovering = new PlaybackComponentRegistry([alternate, new(baseline, true, () => new Factory(baseline))], baseline.Id);
                var recovered = recovering.Create(alternate.Descriptor.Id, PlaybackCapabilities.CompletePlayer);
                using var fallback = recovered.Session;
                Check(recovered.UsedFallback && recovered.ComponentId == baseline.Id && recovered.Issues.Count > 0,
                    "bad loaded factory falls back");
                Write(); // no manifest read lease left after rejected activation
            }
            // A caller cannot smuggle its own duplicate contract into a component package.
            var contractFile = Path.Combine(packageRoot, "Auralis.Playback.Abstractions.dll");
            File.Copy(typeof(IPlaybackSession).Assembly.Location, contractFile);
            var contractBytes = File.ReadAllBytes(contractFile);
            ((JsonArray)json["files"]!).Add(new JsonObject { ["path"] = Path.GetFileName(contractFile), ["length"] = contractBytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(contractBytes)) });
            json["entryType"] = "SyntheticPlayback.Factory"; Write();
            try { Registration().Activate(); throw new Exception("duplicate contract"); }
            catch (PlaybackPackageLoadException e) { Check(e.Issue == PlaybackPackageLoadIssue.DependencyInvalid, "private contract copy rejected"); }
            Console.WriteLine($"PASS playback loader: {checks} assertions; real DLL, exact grant, shared contract, read leases, fallback and lifecycle. No decoder/accounts.");
    }
}
