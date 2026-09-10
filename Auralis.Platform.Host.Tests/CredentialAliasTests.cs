using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class CredentialAliasTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "tests.approved");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(FixturePlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
        var path = Path.Combine(folder, "platform.plugin.json");
        PlatformCredentialAlias[] aliases = [new("session", "old.fixture", "account.v1")];
        var document = JsonSerializer.Serialize(new
        {
            schemaVersion = 3, id = "tests.approved", displayName = "Fixture", version = "1.0.0",
            minimumHostApiVersion = 1, maximumHostApiVersion = 1, entryAssembly = "Fixture.dll",
            entryType = "Auralis.Platform.Host.Tests.FixturePlugin", credentialAliases = aliases,
            providers = new[] { new { id = "fixture", displayName = "Fixture", capabilities = new[] { "LyricsLookup" } } }
        });
        await File.WriteAllTextAsync(path, document);
        async Task<PlatformPluginDiscoveryResult> Discover() => await new PlatformPluginCatalog([folder]).DiscoverAsync();
        var manifest = (await Discover()).Plugins.Single();
        Check(manifest.CredentialAliases.SequenceEqual(aliases), "Schema 3 reads exact aliases without code activation");
        Check(!PlatformCredentialAlias.IsValidList([aliases[0], aliases[0]]) &&
            !PlatformCredentialAlias.IsValidList([aliases[0], aliases[0] with { Key = "another" }]) &&
            !PlatformCredentialAlias.IsValidList(Enumerable.Range(0, 17).Select(i => new PlatformCredentialAlias("key" + i, "scope", "key" + i)).ToArray()),
            "Duplicate keys, duplicate targets and oversized alias lists rejected");
        foreach (var bad in new[] { "*", "../vault", "https://example.test", "key\n", new string('a', 97), "" })
        {
            Check(!PlatformCredentialAlias.IsValidList([aliases[0] with { Key = bad }]), "Invalid key rejected");
            Check(!PlatformCredentialAlias.IsValidList([aliases[0] with { Scope = bad }]), "Invalid scope rejected");
            Check(!PlatformCredentialAlias.IsValidList([aliases[0] with { LegacyKey = bad }]), "Invalid legacy key rejected");
        }
        foreach (var invalid in new[]
        {
            document.Replace("\"schemaVersion\":3", "\"schemaVersion\":2"),
            document.Replace("\"schemaVersion\":3", "\"schemaVersion\":1"),
            document.Replace("\"key\":\"session\"", "\"key\":\"*\""),
            document.Replace("\"key\":\"session\"", "\"key\":\"session\",\"secret\":\"not-allowed\"")
        })
        {
            await File.WriteAllTextAsync(path, invalid);
            Check((await Discover()).Plugins.Count == 0, "Invalid aliases rejected by inert catalog");
        }
        await File.WriteAllTextAsync(path, document);
        var hashes = Directory.GetFiles(folder).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        Directory.CreateDirectory(Path.Combine(root, ".approvals"));
        var receiptPath = Path.Combine(root, ".approvals", "tests.approved.json");
        async Task Receipt(int version, IReadOnlyList<PlatformCredentialAlias>? grants) =>
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new PluginInstallReceipt(version, manifest.Id, hashes, grants)));
        var capturing = new ManifestFactory();
        await Receipt(1, null);
        Check(!await PlatformPluginIntegrity.VerifyAsync(manifest, default), "Hash-only approval cannot grant legacy access");
        await using (var denied = new PlatformPluginHost(new PlatformPluginHostOptions([root], capturing, trustPolicy: PlatformPluginIntegrity.VerifyAsync)))
            Check((await denied.DiscoverAsync()).Plugins.Count == 0 && capturing.Manifest is null,
                "Unapproved aliases never reach context creation");
        await Receipt(2, [aliases[0] with { Scope = "different.scope" }]);
        Check(!await PlatformPluginIntegrity.VerifyAsync(manifest, default), "Changed scope is not an exact grant");
        await Receipt(2, [aliases[0], new("extra", "other.scope", "extra")]);
        Check(!await PlatformPluginIntegrity.VerifyAsync(manifest, default), "Extra grants rejected");
        await Receipt(1, aliases);
        Check(!await PlatformPluginIntegrity.VerifyAsync(manifest, default), "Old receipt cannot smuggle grants");
        await Receipt(2, aliases);
        Check(await PlatformPluginIntegrity.VerifyAsync(manifest, default), "Exact approval succeeds");
        await using (var approved = new PlatformPluginHost(new PlatformPluginHostOptions([root], capturing, trustPolicy: PlatformPluginIntegrity.VerifyAsync)))
        {
            await approved.DiscoverAsync();
            Check(capturing.Manifest is null, "Discovery does not create a context");
            var result = await approved.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("fixture",
                (p, ct) => p.LookupAsync(new("title", "artist", "album", 3), ct));
            Check(result.IsSuccess && capturing.Manifest?.CredentialAliases.SequenceEqual(aliases) == true,
                "Real DLL activation receives the approved manifest, not an ID-only context");
        }

        var package = Path.Combine(root, "fixture.auralis-plugin");
        ZipFile.CreateFromDirectory(folder, package);
        // Historical schema 3 format/approval test; the current application import floor is tested separately.
        var manager = new PlatformPluginManager(Path.Combine(root, "managed"), [], minimumManifestSchemaVersion: 1);
        var preview = await manager.PrepareImportAsync(package);
        Check(preview.CredentialAliases?.SequenceEqual(aliases) == true, "Import preview exposes exact non-secret addresses");
        try { await manager.ConfirmImportAsync(preview.Token, false); throw new Exception("Missing trust accepted"); }
        catch (InvalidDataException) { }
        await manager.ConfirmImportAsync(preview.Token, true);
        var installed = await manager.FindManagedPluginAsync(manifest.Id, default);
        Check(installed is not null && await PlatformPluginIntegrity.VerifyAsync(installed, default), "Import records separate grants");
        Check((await manager.CreateSessionPlanAsync()).EnabledIds.Count == 0, "Approved aliases do not enable the plugin");

        // Exercise the production context factory with synthetic byte stores; never touch Windows vaults.
        var stores = new Dictionary<string, MemoryStore>(StringComparer.Ordinal);
        IPlatformCredentialStore Store(string scope)
        {
            if (!stores.TryGetValue(scope, out var store)) stores[scope] = store = new MemoryStore();
            return store;
        }
        var legacy = (MemoryStore)Store("old.fixture");
        legacy.Values["account.v1"] = [1, 2, 3];
        using var factory = new DefaultPlatformHostContextFactory(new PlatformSettingsStore(Path.Combine(root, "settings.json")), NullAppLogger.Instance, Store);
        var credentials = factory.CreateContext(manifest).CredentialStore;
        Check(legacy.Reads == 0, "Context construction never reads credentials");
        using (var existing = await credentials.GetAsync("session", default))
            Check(existing?.Secret.Span.SequenceEqual(new byte[] { 1, 2, 3 }) == true, "Declared alias preserves existing login");
        await credentials.SetAsync("session", new byte[] { 4 }, null, default);
        Check(legacy.Values["account.v1"].SequenceEqual(new byte[] { 4 }) && stores[manifest.Id].Values.Count == 0, "Refresh writes the original address");
        await credentials.SetAsync("pending.challenge", new byte[] { 5 }, null, default);
        Check(stores[manifest.Id].Values.ContainsKey("pending.challenge") && !legacy.Values.ContainsKey("pending.challenge"), "Temporary challenges stay private");
        Check(await credentials.GetAsync("Session", default) is null && legacy.Reads == 1, "Aliases are exact case-sensitive keys");
        await factory.RevokeCredentialsAsync(manifest.Id, manifest, default);
        Check(!legacy.Values.ContainsKey("account.v1"), "Host clears declared credentials even without plugin authentication capability");
        await credentials.DeleteAsync("session", default);
        Check(!legacy.Values.ContainsKey("account.v1") && await credentials.GetAsync("session", default) is null, "Revocation permits cleanup of the same address");
        try { await credentials.SetAsync("session", new byte[] { 8 }, null, default); throw new Exception("Late write accepted"); }
        catch (InvalidOperationException) { }
        Check(!legacy.Values.ContainsKey("account.v1"), "Late refresh cannot restore a revoked account");

        // Even a historically recognized plugin ID gets no implicit aliases in schema 3.
        var known = JsonNode.Parse(document)!.AsObject();
        known["id"] = "auralis.platform.tx";
        known["credentialAliases"] = new JsonArray();
        await File.WriteAllTextAsync(path, known.ToJsonString());
        var noAliases = (await Discover()).Plugins.Single();
        var oldVault = (MemoryStore)Store("auralis.online-sources");
        oldVault.Values["qq.session.v1"] = [9];
        Check(await factory.CreateContext(noAliases).CredentialStore.GetAsync("qq.session.v1", default) is null && oldVault.Reads == 0,
            "Schema 3 never falls back to platform-name permissions");
        known["schemaVersion"] = 2;
        known.Remove("credentialAliases");
        await File.WriteAllTextAsync(path, known.ToJsonString());
        var oldManifest = (await Discover()).Plugins.Single();
        using var oldFactory = new DefaultPlatformHostContextFactory(new PlatformSettingsStore(Path.Combine(root, "old-settings.json")), NullAppLogger.Instance, Store);
        using (var oldSession = await oldFactory.CreateContext(oldManifest).CredentialStore.GetAsync("qq.session.v1", default))
            Check(oldSession is null && oldVault.Reads == 0, "Old schema cannot gain implicit cross-scope account access");
        // No manifest at all grants only the plugin's own store, not historical ID permissions.
        using var idOnly = new DefaultPlatformHostContextFactory(new PlatformSettingsStore(Path.Combine(root, "id-settings.json")), NullAppLogger.Instance, Store);
        var reads = oldVault.Reads;
        Check(await idOnly.CreateContext("auralis.platform.tx").CredentialStore.GetAsync("qq.session.v1", default) is null && oldVault.Reads == reads,
            "ID-only context cannot access legacy credentials");
        Console.WriteLine("PASS credential declarations: exact receipt grants, inert discovery, real host activation, explicit approval, synthetic refresh/cleanup/isolation; no implicit aliases in any schema.");
    }

    private sealed class ManifestFactory : IManifestPlatformHostContextFactory
    {
        internal PlatformPluginManifest? Manifest;
        private readonly FixtureServices _inner = new();
        public PlatformHostContext CreateContext(string id) => throw new InvalidOperationException("ID-only context should not be used.");
        public PlatformHostContext CreateContext(PlatformPluginManifest manifest)
        {
            Manifest = manifest;
            return _inner.CreateContext(manifest.Id);
        }
    }
}
