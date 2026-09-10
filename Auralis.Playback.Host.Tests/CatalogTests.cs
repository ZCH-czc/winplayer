using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auralis.Playback.Host;
using Auralis.Services;

internal static class CatalogTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        // Generated data only. The DLL is deliberately not executable: discovery must not try to load it.
        var root = Path.Combine(Path.GetTempPath(), "Auralis-playback-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "fixture"); Directory.CreateDirectory(package);
            Directory.CreateDirectory(Path.Combine(package, "native", "plugins"));
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [0, 1, 2, 3]);
            File.WriteAllBytes(Path.Combine(package, "native", "plugins", "codec.dll"), [4, 5, 6]);
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1, ["kind"] = "playback", ["id"] = "fixture.engine", ["displayName"] = "Synthetic",
                ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0",
                ["runtimeIdentifier"] = "win-x64", ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Factory",
                ["capabilities"] = new JsonArray("audio", "videoFrames", "separateAudio", "seek", "rate", "outputDevices", "mute"),
                ["files"] = new JsonArray(FileRow("Fixture.dll", [0, 1, 2, 3]), FileRow("native/plugins/codec.dll", [4, 5, 6]))
            };
            var manifestPath = Path.Combine(package, PlaybackComponentManifest.FileName);
            void Write(JsonNode node) => File.WriteAllText(manifestPath, node.ToJsonString());
            PlaybackPackageFinding Inspect() => PlaybackComponentCatalog.Discover(root, PlaybackCapabilities.CompletePlayer).Single();
            Write(manifest);
            var finding = Inspect();
            Check(finding.Issue == PlaybackPackageIssue.None && finding.Package!.Manifest.Files.Count == 2, "valid nested package");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture"), "no entry assembly loaded");
            Check(await PlaybackComponentCatalog.VerifyPayloadAsync(finding.Package!) == PlaybackPackageIssue.None, "hash payload including native tree");
            var direct = PlaybackComponentCatalog.Discover(package, PlaybackCapabilities.Audio).Single();
            Check(direct.Package!.ManifestSha256 == finding.Package!.ManifestSha256, "direct package or catalog root");
            Check(PlaybackComponentCatalog.Discover(Path.Combine(root, "missing"), PlaybackCapabilities.Audio).Single().Issue == PlaybackPackageIssue.RootUnavailable, "missing root");
            Check(PlaybackComponentCatalog.Discover(@"\\example.invalid\never-connect", PlaybackCapabilities.Audio).Single().Issue == PlaybackPackageIssue.UnsafePath, "UNC rejected before IO");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { PlaybackComponentCatalog.Discover(root, PlaybackCapabilities.Audio, cancellationToken: cancellation.Token); throw new Exception("cancel discovery"); }
                catch (OperationCanceledException) { checks++; }
                try { await PlaybackComponentCatalog.VerifyPayloadAsync(finding.Package!, cancellation.Token); throw new Exception("cancel verify"); }
                catch (OperationCanceledException) { checks++; }
            }
            foreach (var (key, value, expected) in new (string, JsonNode, PlaybackPackageIssue)[]
            {
                ("contractApiVersion", JsonValue.Create(99)!, PlaybackPackageIssue.ApiMismatch),
                ("minimumHostVersion", JsonValue.Create("99.0.0")!, PlaybackPackageIssue.HostTooOld),
                ("runtimeIdentifier", JsonValue.Create("win-arm64")!, PlaybackPackageIssue.RuntimeMismatch),
                ("capabilities", new JsonArray("audio"), PlaybackPackageIssue.MissingCapability)
            })
            {
                var edited = manifest.DeepClone(); edited[key] = value; Write(edited);
                Check(Inspect().Issue == expected && Inspect().Package is not null, "metadata compatibility diagnostic");
            }
            foreach (var path in new[] { "../escape.dll", "C:/escape.dll", "native\\codec.dll", "/absolute.dll", "a//b.dll", "NUL.dll", "codec.dll:stream", ".hidden.dll", "codec.dll.", "a/../../b.dll" })
            {
                var edited = manifest.DeepClone(); edited["files"]![1]!["path"] = path; Write(edited);
                Check(Inspect().Issue == PlaybackPackageIssue.ManifestInvalid, "unsafe payload path");
            }
            foreach (var edit in new Action<JsonNode>[]
            {
                n => n["unknownPermission"] = "anything", n => n["schemaVersion"] = 2,
                n => n["kind"] = "platform", n => n["version"] = "1.0.0-preview",
                n => n["entryAssembly"] = "missing.dll", n => n["entryType"] = "Factory, OtherAssembly",
                n => n["files"]![1]!["path"] = "FIXTURE.DLL", n => n["files"]![1]!["path"] = "Fixture.dll/child",
                n => n["files"]![1]!["length"] = -1, n => n["files"]![1]!["length"] = PlaybackComponentManifest.MaximumPayloadBytes + 1,
                n => n["files"]![1]!["sha256"] = "not-a-hash", n => n["files"]![1]!["extra"] = true,
                n => n["capabilities"] = new JsonArray("audio", "audio"), n => n["capabilities"] = new JsonArray("future"),
                n => n["capabilities"] = new JsonArray(), n => n["displayName"] = "bad\nname",
                n => n["contractApiVersion"] = "1", n => n["files"] = null
            })
            {
                var edited = manifest.DeepClone(); edit(edited); Write(edited);
                Check(Inspect().Issue == PlaybackPackageIssue.ManifestInvalid, "strict schema field validation");
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
            Check(Inspect().Issue == PlaybackPackageIssue.ManifestInvalid, "duplicate JSON key");
            File.WriteAllText(manifestPath, new string(' ', PlaybackComponentManifest.MaximumManifestBytes + 1));
            Check(Inspect().Issue == PlaybackPackageIssue.ManifestInvalid, "bounded manifest");
            Write(manifest);
            var before = Inspect().Package!;
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [3, 2, 1, 0]);
            Check(Inspect().Issue == PlaybackPackageIssue.None, "metadata discovery does not hash payload");
            Check(await PlaybackComponentCatalog.VerifyPayloadAsync(before) == PlaybackPackageIssue.PayloadChanged, "same length tamper detected");
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [0, 1, 2, 3]);
            File.WriteAllBytes(Path.Combine(package, "extra.dll"), [0]);
            Check(await PlaybackComponentCatalog.VerifyPayloadAsync(before) == PlaybackPackageIssue.PayloadUnexpected, "undeclared DLL");
            File.Delete(Path.Combine(package, "extra.dll"));
            File.Delete(Path.Combine(package, "native", "plugins", "codec.dll"));
            Check(await PlaybackComponentCatalog.VerifyPayloadAsync(before) == PlaybackPackageIssue.PayloadMissing, "missing native DLL");
            File.WriteAllBytes(Path.Combine(package, "native", "plugins", "codec.dll"), [4, 5, 6]);
            var changed = manifest.DeepClone(); changed["displayName"] = "Changed"; Write(changed);
            Check(await PlaybackComponentCatalog.VerifyPayloadAsync(before) == PlaybackPackageIssue.ManifestChanged && before.Manifest.Descriptor.DisplayName == "Synthetic", "immutable snapshot invalidation");
            Write(manifest);
            var link = Path.Combine(package, "linked");
            var target = Path.Combine(root, "link-target"); Directory.CreateDirectory(target);
            var madeLink = false;
            try { Directory.CreateSymbolicLink(link, target); madeLink = true; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { Console.WriteLine("NOT TESTED playback catalog symlink: OS did not permit creating a synthetic link."); }
            if (madeLink)
            {
                try
                {
                    var linkedIssue = await PlaybackComponentCatalog.VerifyPayloadAsync(before);
                    Check(linkedIssue == PlaybackPackageIssue.UnsafePath, "nested directory symlink rejected: " + linkedIssue +
                        "; attributes=" + File.GetAttributes(link) + "; hasLinkTarget=" + (new DirectoryInfo(link).LinkTarget is not null));
                    Check(PlaybackComponentCatalog.Discover(link, PlaybackCapabilities.Audio).Single().Issue == PlaybackPackageIssue.UnsafePath, "root symlink rejected");
                }
                finally { Directory.Delete(link); }
            }
            Directory.Delete(target);
            var duplicate = Path.Combine(root, "duplicate"); Directory.CreateDirectory(duplicate);
            File.Copy(manifestPath, Path.Combine(duplicate, PlaybackComponentManifest.FileName));
            Check(PlaybackComponentCatalog.Discover(root, PlaybackCapabilities.Audio).All(r => r.Issue == PlaybackPackageIssue.DuplicateId), "duplicates all rejected");
            File.Delete(Path.Combine(duplicate, PlaybackComponentManifest.FileName));
            Check(PlaybackComponentCatalog.Discover(root, PlaybackCapabilities.Audio).Count(r => r.Issue == PlaybackPackageIssue.None) == 1, "bad sibling isolated");
            for (var i = 0; i < PlaybackComponentCatalog.MaximumPackages; i++) Directory.CreateDirectory(Path.Combine(root, "empty" + i));
            Check(PlaybackComponentCatalog.Discover(root, PlaybackCapabilities.Audio).Single().Issue == PlaybackPackageIssue.TooManyPackages, "catalog limit not partial selection");
            Console.WriteLine($"PASS playback catalog: {checks} assertions; strict metadata, no DLL activation, bounded nested payload, snapshot/tamper/compatibility/cancellation. No user plugins or accounts.");
        }
        finally
        {
            // Only this random synthetic fixture root, never a caller-selected or installed-plugin path.
            Directory.Delete(root, true);
        }
    }
    private static JsonObject FileRow(string path, byte[] bytes) => new()
    {
        ["path"] = path, ["length"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
    };
}
