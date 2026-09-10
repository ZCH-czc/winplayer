using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Auralis.MediaTransport.Host;
using Auralis.MediaTransport;

internal static class CatalogTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        // Generated data only. The DLL is deliberately not executable: discovery must not try to load it.
        var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "fixture"); Directory.CreateDirectory(package);
            Directory.CreateDirectory(Path.Combine(package, "native", "plugins"));
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [0, 1, 2, 3]);
            File.WriteAllBytes(Path.Combine(package, "native", "plugins", "codec.dll"), [4, 5, 6]);
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1, ["kind"] = "mediaTransport", ["id"] = "fixture.engine", ["displayName"] = "Synthetic",
                ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0",
                ["runtimeIdentifier"] = "win-x64", ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Factory",
                ["capabilities"] = new JsonArray("authorizedHttp", "completeBuffering", "prefetch", "independentResources", "sharedBudget", "sharedRequests"),
                ["files"] = new JsonArray(FileRow("Fixture.dll", [0, 1, 2, 3]), FileRow("native/plugins/codec.dll", [4, 5, 6]))
            };
            var manifestPath = Path.Combine(package, MediaTransportComponentManifest.FileName);
            void Write(JsonNode node) => File.WriteAllText(manifestPath, node.ToJsonString());
            MediaTransportPackageFinding Inspect() => MediaTransportComponentCatalog.Discover(root, MediaTransportCapabilities.Full).Single();
            Write(manifest);
            var finding = Inspect();
            Check(finding.Issue == MediaTransportPackageIssue.None && finding.Package!.Manifest.Files.Count == 2, "valid nested package");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture"), "no entry assembly loaded");
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(finding.Package!) == MediaTransportPackageIssue.None, "hash payload including native tree");
            var direct = MediaTransportComponentCatalog.Discover(package, MediaTransportCapabilities.AuthorizedHttp).Single();
            Check(direct.Package!.ManifestSha256 == finding.Package!.ManifestSha256, "direct package or catalog root");
            Check(MediaTransportComponentCatalog.Discover(Path.Combine(root, "missing"), MediaTransportCapabilities.AuthorizedHttp).Single().Issue == MediaTransportPackageIssue.RootUnavailable, "missing root");
            Check(MediaTransportComponentCatalog.Discover(@"\\example.invalid\never-connect", MediaTransportCapabilities.AuthorizedHttp).Single().Issue == MediaTransportPackageIssue.UnsafePath, "UNC rejected before IO");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { MediaTransportComponentCatalog.Discover(root, MediaTransportCapabilities.AuthorizedHttp, cancellationToken: cancellation.Token); throw new Exception("cancel discovery"); }
                catch (OperationCanceledException) { checks++; }
                try { await MediaTransportComponentCatalog.VerifyPayloadAsync(finding.Package!, cancellation.Token); throw new Exception("cancel verify"); }
                catch (OperationCanceledException) { checks++; }
            }
            foreach (var (key, value, expected) in new (string, JsonNode, MediaTransportPackageIssue)[]
            {
                ("contractApiVersion", JsonValue.Create(99)!, MediaTransportPackageIssue.ApiMismatch),
                ("minimumHostVersion", JsonValue.Create("99.0.0")!, MediaTransportPackageIssue.HostTooOld),
                ("runtimeIdentifier", JsonValue.Create("win-arm64")!, MediaTransportPackageIssue.RuntimeMismatch),
                ("capabilities", new JsonArray("authorizedHttp"), MediaTransportPackageIssue.MissingCapability)
            })
            {
                var edited = manifest.DeepClone(); edited[key] = value; Write(edited);
                Check(Inspect().Issue == expected && Inspect().Package is not null, "metadata compatibility diagnostic");
            }
            foreach (var path in new[] { "../escape.dll", "C:/escape.dll", "native\\codec.dll", "/absolute.dll", "a//b.dll", "NUL.dll", "codec.dll:stream", ".hidden.dll", "codec.dll.", "a/../../b.dll" })
            {
                var edited = manifest.DeepClone(); edited["files"]![1]!["path"] = path; Write(edited);
                Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "unsafe payload path");
            }
            foreach (var edit in new Action<JsonNode>[]
            {
                n => n["unknownPermission"] = "anything", n => n["schemaVersion"] = 2,
                n => n["kind"] = "platform", n => n["version"] = "1.0.0-preview",
                n => n["entryAssembly"] = "missing.dll", n => n["entryType"] = "Factory, OtherAssembly",
                n => n["files"]![1]!["path"] = "FIXTURE.DLL", n => n["files"]![1]!["path"] = "Fixture.dll/child",
                n => n["files"]![1]!["length"] = -1, n => n["files"]![1]!["length"] = MediaTransportComponentManifest.MaximumPayloadBytes + 1,
                n => n["files"]![1]!["sha256"] = "not-a-hash", n => n["files"]![1]!["extra"] = true,
                n => n["capabilities"] = new JsonArray("authorizedHttp", "authorizedHttp"), n => n["capabilities"] = new JsonArray("future"),
                n => n["capabilities"] = new JsonArray(), n => n["displayName"] = "bad\nname",
                n => n["contractApiVersion"] = "1", n => n["files"] = null
            })
            {
                var edited = manifest.DeepClone(); edit(edited); Write(edited);
                Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "strict schema field validation");
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
            Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "duplicate JSON key");
            File.WriteAllText(manifestPath, new string(' ', MediaTransportComponentManifest.MaximumManifestBytes + 1));
            Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "bounded manifest");
            Write(manifest);
            var before = Inspect().Package!;
            var oversized = manifest.DeepClone();
            oversized["files"] = new JsonArray(Enumerable.Range(0, MediaTransportComponentManifest.MaximumFiles + 1)
                .Select(i => (JsonNode)FileRow(i == 0 ? "Fixture.dll" : "file" + i + ".dll", [1])).ToArray());
            Write(oversized);
            Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "file count bound");
            var excessive = manifest.DeepClone();
            excessive["files"]![0]!["length"] = MediaTransportComponentManifest.MaximumPayloadBytes;
            Write(excessive);
            Check(Inspect().Issue == MediaTransportPackageIssue.ManifestInvalid, "aggregate payload bound");
            Write(manifest);
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [0]);
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.PayloadChanged, "payload length bound");
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [3, 2, 1, 0]);
            Check(Inspect().Issue == MediaTransportPackageIssue.None, "metadata discovery does not hash payload");
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.PayloadChanged, "same length tamper detected");
            File.WriteAllBytes(Path.Combine(package, "Fixture.dll"), [0, 1, 2, 3]);
            File.WriteAllBytes(Path.Combine(package, "extra.dll"), [0]);
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.PayloadUnexpected, "undeclared DLL");
            File.Delete(Path.Combine(package, "extra.dll"));
            var extraDirectory = Path.Combine(package, "extra-directory"); Directory.CreateDirectory(extraDirectory);
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.PayloadUnexpected, "undeclared empty directory");
            Directory.Delete(extraDirectory);
            File.Delete(Path.Combine(package, "native", "plugins", "codec.dll"));
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.PayloadMissing, "missing native DLL");
            File.WriteAllBytes(Path.Combine(package, "native", "plugins", "codec.dll"), [4, 5, 6]);
            var changed = manifest.DeepClone(); changed["displayName"] = "Changed"; Write(changed);
            Check(await MediaTransportComponentCatalog.VerifyPayloadAsync(before) == MediaTransportPackageIssue.ManifestChanged && before.Manifest.Descriptor.DisplayName == "Synthetic", "immutable snapshot invalidation");
            Write(manifest);
            var link = Path.Combine(package, "linked");
            var target = Path.Combine(root, "link-target"); Directory.CreateDirectory(target);
            var madeLink = false;
            try { Directory.CreateSymbolicLink(link, target); madeLink = true; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { Console.WriteLine("NOT TESTED transport catalog symlink: OS did not permit creating a synthetic link."); }
            if (madeLink)
            {
                try
                {
                    var linkedIssue = await MediaTransportComponentCatalog.VerifyPayloadAsync(before);
                    Check(linkedIssue == MediaTransportPackageIssue.UnsafePath, "nested directory symlink rejected: " + linkedIssue +
                        "; attributes=" + File.GetAttributes(link) + "; hasLinkTarget=" + (new DirectoryInfo(link).LinkTarget is not null));
                    Check(MediaTransportComponentCatalog.Discover(link, MediaTransportCapabilities.AuthorizedHttp).Single().Issue == MediaTransportPackageIssue.UnsafePath, "root symlink rejected");
                }
                finally { Directory.Delete(link); }
            }
            Directory.Delete(target);
            var duplicate = Path.Combine(root, "duplicate"); Directory.CreateDirectory(duplicate);
            File.Copy(manifestPath, Path.Combine(duplicate, MediaTransportComponentManifest.FileName));
            Check(MediaTransportComponentCatalog.Discover(root, MediaTransportCapabilities.AuthorizedHttp).All(r => r.Issue == MediaTransportPackageIssue.DuplicateId), "duplicates all rejected");
            File.Delete(Path.Combine(duplicate, MediaTransportComponentManifest.FileName));
            Check(MediaTransportComponentCatalog.Discover(root, MediaTransportCapabilities.AuthorizedHttp).Count(r => r.Issue == MediaTransportPackageIssue.None) == 1, "bad sibling isolated");
            for (var i = 0; i < MediaTransportComponentCatalog.MaximumPackages; i++) Directory.CreateDirectory(Path.Combine(root, "empty" + i));
            Check(MediaTransportComponentCatalog.Discover(root, MediaTransportCapabilities.AuthorizedHttp).Single().Issue == MediaTransportPackageIssue.TooManyPackages, "catalog limit not partial selection");
            Check(MediaTransportComponentCatalog.Discover("relative", MediaTransportCapabilities.Full).Single().Issue == MediaTransportPackageIssue.UnsafePath, "relative root rejected");
            Console.WriteLine($"PASS transport catalog: {checks} assertions; strict metadata, no DLL activation, bounded nested payload, snapshot/tamper/compatibility/cancellation. No user plugins or accounts.");
            return checks;
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
