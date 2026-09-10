using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Auralis.MediaTransport.Host;
using Auralis.MediaTransport;

internal static class ArchiveTests
{
    private sealed record Entry(string Path, byte[] Bytes, int Attributes = 0);
    internal static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assertions = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); assertions++; }
        var install = Path.Combine(root, "store");
        var store = new MediaTransportInstallationStore(install, MediaTransportCapabilities.AuthorizedHttp);
        var zipPath = Path.Combine(root, "test.auralis-transport.zip");
        var payload = new[] { new Entry("Fixture.dll", [1, 2, 3]), new Entry("native/codec.dll", [4, 5, 6]) };
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "mediaTransport", ["id"] = "fixture.archive", ["displayName"] = "Synthetic",
            ["version"] = "1.0.0", ["contractApiVersion"] = 1, ["minimumHostVersion"] = "0.1.0",
            ["runtimeIdentifier"] = "win-x64", ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Factory",
            ["capabilities"] = new JsonArray("authorizedHttp"), ["files"] = new JsonArray(payload.Select(e => (JsonNode)new JsonObject
                { ["path"] = e.Path, ["length"] = e.Bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(e.Bytes)) }).ToArray())
        };
        Entry[] Entries() => [new(MediaTransportComponentManifest.FileName, Encoding.UTF8.GetBytes(manifest.ToJsonString())), .. payload];
        void Zip(IEnumerable<Entry> entries)
        {
            using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var row in entries)
            {
                var entry = zip.CreateEntry(row.Path, CompressionLevel.Fastest); entry.ExternalAttributes = row.Attributes;
                using var writer = entry.Open(); writer.Write(row.Bytes);
            }
        }
        bool PreviewsEmpty() => !Directory.Exists(Path.Combine(install, "previews")) ||
            !Directory.EnumerateFileSystemEntries(Path.Combine(install, "previews")).Any();
        async Task BadArchive()
        {
            try { await using var accepted = await store.PreviewArchiveAsync(zipPath); throw new Exception("invalid ZIP accepted"); }
            catch (MediaTransportArchiveException e) { Check(!e.ToString().Contains(root), "typed path-free archive failure"); }
            Check(PreviewsEmpty(), "failed extraction cleans only owned preview");
        }
        try
        {
            Zip([.. Entries(), new("native/", [], 0x4000 << 16)]);
            var originalDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath)));
            var preview = await store.PreviewArchiveAsync(zipPath);
            Check(preview.FileCount == 2 && preview.PayloadBytes == 6 && preview.ArchiveSha256 == originalDigest, "bounded archive preview metadata and digest");
            Check(store.ReadState().Components.Count == 0 && !File.Exists(Path.Combine(install, "transport-installations.json")), "preview does not approve");
            try { await store.PreviewArchiveAsync(zipPath); throw new Exception("concurrent preview"); }
            catch (MediaTransportInstallationException e) { Check(e.Issue == MediaTransportInstallationIssue.Busy, "one pending preview per store"); }
            try { await store.ImportApprovedAsync(preview, new(preview.Descriptor.Id, new string('0', 64))); throw new Exception("wrong grant"); }
            catch (MediaTransportInstallationException e) { Check(e.Issue == MediaTransportInstallationIssue.ApprovalRequired, "archive exact approval"); }
            try { await new MediaTransportInstallationStore(install, MediaTransportCapabilities.AuthorizedHttp).ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256)); throw new Exception("foreign preview"); }
            catch (MediaTransportInstallationException e) { Check(e.Issue == MediaTransportInstallationIssue.ApprovalRequired, "archive preview owner"); }
            // The original file is not used at confirmation; the exact verified preview is installed.
            File.WriteAllBytes(zipPath, [0, 0, 0]);
            var installed = await store.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256));
            Check(!installed.Enabled && store.ReadStartupPlan().Registrations.Count == 0, "ZIP import starts off despite original replacement");
            await preview.DisposeAsync(); await preview.DisposeAsync();
            Check(PreviewsEmpty() && Directory.Exists(Path.Combine(install, "revisions", installed.ManifestSha256)), "close removes preview, never installed revision");
            try { await store.ImportApprovedAsync(preview, new(preview.Descriptor.Id, preview.ManifestSha256)); throw new Exception("disposed preview reused"); }
            catch (ObjectDisposedException) { assertions++; }
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture"), "fake DLL never loaded");

            Zip(Entries());
            await using (var tampered = await store.PreviewArchiveAsync(zipPath))
            {
                var directory = Directory.EnumerateDirectories(Path.Combine(install, "previews")).Single();
                File.WriteAllBytes(Path.Combine(directory, "Fixture.dll"), [3, 2, 1]);
                try { await store.ImportApprovedAsync(tampered, new(tampered.Descriptor.Id, tampered.ManifestSha256)); throw new Exception("tampered preview imported"); }
                catch (MediaTransportInstallationException e) { Check(e.Issue == MediaTransportInstallationIssue.InvalidPackage, "extracted preview revalidated before install"); }
            }
            Check(PreviewsEmpty() && store.ReadState().Components.Single() == installed, "tamper preserves receipt and clears preview");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await store.PreviewArchiveAsync(zipPath, cancel.Token); throw new Exception("cancel ignored"); }
                catch (OperationCanceledException) { Check(PreviewsEmpty(), "cancel before extraction"); }
            }
            await using (var afterCancel = await store.PreviewArchiveAsync(zipPath)) { Check(afterCancel.FileCount == 2, "cancel/failure releases preview slot"); }

            foreach (var path in new[] { "../escape.dll", "/escape.dll", "C:/escape.dll", "\\\\server\\file", "native\\bad.dll",
                "native/../bad.dll", "native//bad.dll", "Fixture.dll:stream", "CON.dll", "native/name. ", "中文.dll" })
            { Zip([.. Entries(), new(path, [9])]); await BadArchive(); }
            foreach (var rows in new Entry[][]
            {
                [.. Entries(), new("fixture.dll", [8])],
                [.. Entries(), new("extra.dll", [8])],
                [Entries()[0], payload[0]],
                [Entries()[0], payload[0] with { Path = "fixture.dll" }, payload[1]],
                [Entries()[0] with { Path = "MediaTransport.component.json" }, .. payload],
                [.. Entries(), new("empty/", [])],
                [.. Entries(), new("native/", []), new("native/", [])],
                [Entries()[0], payload[0] with { Attributes = 0xa000 << 16 }, payload[1]],
                [Entries()[0], payload[0] with { Attributes = (int)FileAttributes.ReparsePoint }, payload[1]],
                [Entries()[0], payload[0] with { Bytes = [3, 2, 1] }, payload[1]],
                [new(MediaTransportComponentManifest.FileName, Encoding.UTF8.GetBytes("{}")), .. payload],
                [.. Entries(), new("bomb.dll", new byte[2 * 1024 * 1024])]
            }) { Zip(rows); await BadArchive(); }
            Check(!File.Exists(Path.Combine(root, "escape.dll")), "no traversal output");
            var deepPayload = Enumerable.Range(0, 70).Select(i => new Entry("p" + i + "/" +
                string.Concat(Enumerable.Repeat("a/", 64)) + "code.dll", [1])).ToArray();
            var deepManifest = (JsonObject)manifest.DeepClone(); deepManifest["entryAssembly"] = deepPayload[0].Path;
            deepManifest["files"] = new JsonArray(deepPayload.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path,
                ["length"] = 1, ["sha256"] = Convert.ToHexString(SHA256.HashData(e.Bytes)) }).ToArray());
            Zip([new(MediaTransportComponentManifest.FileName, Encoding.UTF8.GetBytes(deepManifest.ToJsonString())), .. deepPayload]);
            await BadArchive(); // Entry count is small but the implicit directory count is excessive.

            void Patch(Action<byte[], int, int> change)
            {
                Zip(Entries()); var bytes = File.ReadAllBytes(zipPath); var end = bytes.Length - 22;
                var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
                change(bytes, central, end); File.WriteAllBytes(zipPath, bytes);
            }
            foreach (var mutate in new Action<byte[], int, int>[]
            {
                (b,c,e) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(c + 8), 1), // encryption
                (b,c,e) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(c + 10), 99), // unsupported method
                (b,c,e) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 4), 1), // multi-volume
                (b,c,e) => { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 8), 65535); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 10), 65535); },
                (b,c,e) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e + 12), 9 * 1024 * 1024),
                (b,c,e) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(c + 24), 65U * 1024 * 1024),
                (b,c,e) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(c + 42), uint.MaxValue),
                (b,c,e) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(c + 28), 65535)
            }) { Patch(mutate); await BadArchive(); }
            Zip(Entries()); var truncated = File.ReadAllBytes(zipPath); File.WriteAllBytes(zipPath, truncated[..^8]); await BadArchive();
            Zip(Entries()); var commented = File.ReadAllBytes(zipPath); BinaryPrimitives.WriteUInt16LittleEndian(commented.AsSpan(commented.Length - 2), 1);
            File.WriteAllBytes(zipPath, [.. commented, 0]); await BadArchive();
            var stateBefore = File.ReadAllText(Path.Combine(install, "transport-installations.json"));
            manifest["minimumHostVersion"] = "99.0.0"; Zip(Entries());
            try { await store.PreviewArchiveAsync(zipPath); throw new Exception("incompatible accepted"); }
            catch (MediaTransportInstallationException e) { Check(e.Issue == MediaTransportInstallationIssue.InvalidPackage, "ZIP compatibility via existing catalog"); }
            Check(PreviewsEmpty() && File.ReadAllText(Path.Combine(install, "transport-installations.json")) == stateBefore, "incompatible archive leaves installed state untouched");
            manifest["minimumHostVersion"] = "0.1.0"; manifest["version"] = "1.1.0"; Zip(Entries());
            await using (var update = await store.PreviewArchiveAsync(zipPath))
            {
                var revision = await store.ImportApprovedAsync(update, new(update.Descriptor.Id, update.ManifestSha256));
                Check(!revision.Enabled && revision.ManifestSha256 != installed.ManifestSha256 &&
                    Directory.Exists(Path.Combine(install, "revisions", installed.ManifestSha256)), "archive upgrade preserves original revision");
            }
            Console.WriteLine($"PASS transport archives: {assertions} assertions; bounded ZIP metadata/extraction, path/link/duplicate/CRC-independent payload integrity, exact approval, cancel/dispose, update and inert install.");
        }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(root).StartsWith("Auralis-transport-archive-", StringComparison.Ordinal) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0) Directory.Delete(root, recursive: true);
        }
        return assertions;
    }
}
