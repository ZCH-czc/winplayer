using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host.Tests;

internal static class PluginManagerTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is InvalidDataException or PlatformPluginCompatibilityException) { return; }
        throw new InvalidOperationException("Unsafe operation accepted");
    }
    private static string MakePackage(string root, string version, string? extra = null, int api = 1, string id = "tests.approved")
    {
        var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".auralis-plugin");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("platform.plugin.json");
        using (var writer = new StreamWriter(entry.Open())) writer.Write(JsonSerializer.Serialize(new
        {
            schemaVersion = 5, id, displayName = "Fixture", version,
            hostRequirements = new PlatformHostRequirements { MinimumHostSdkVersion = PlatformHostCompatibility.SdkVersion, RequiredFeatures = ["comment-artwork.v1", "lyrics-lookup.v1"] },
            minimumHostApiVersion = api, maximumHostApiVersion = api, entryAssembly = "Fixture.dll",
            entryType = "Auralis.Platform.Host.Tests.FixturePlugin",
            providers = new[] { new { id = "fixture", displayName = "Fixture", commentArtworkDomains = Array.Empty<string>(), capabilities = new[] { "LyricsLookup" } } }
        }));
        zip.CreateEntryFromFile(typeof(FixturePlugin).Assembly.Location, "Fixture.dll");
        if (extra is not null) { using var writer = new StreamWriter(zip.CreateEntry(extra).Open()); writer.Write("bad"); }
        return file;
    }
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var storage = Path.Combine(root, "user-plugins");
        var manager = new PlatformPluginManager(storage, []);
        Check(!Directory.Exists(storage), "Construction is inert");
        var emptyPlan = await manager.CreateSessionPlanAsync();
        Check(emptyPlan.Roots.Count == 0, "Missing plugin directory works");
        var services = new FixtureServices();
        PlatformPluginHost Host(PluginSessionPlan plan) => new(new PlatformPluginHostOptions([], services,
            trustPolicy: PlatformPluginIntegrity.VerifyAsync, directoryResolver: _ => Task.FromResult(plan.Roots),
            enabledPolicy: (m, _) => Task.FromResult(plan.EnabledIds.Contains(m.Id))));
        await using var emptyHost = Host(emptyPlan);
        var emptySession = await emptyHost.DiscoverAsync();
        var package = MakePackage(root, "1.0.0");
        var preview = await manager.PrepareImportAsync(package);
        Check(preview.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), "Preview hash matches source");
        Check((await manager.CreateSessionPlanAsync()).Roots.Count == 0, "Preview cannot register anything");
        await Reject(() => manager.ConfirmImportAsync(preview.Token, false));
        await Reject(() => manager.ConfirmImportAsync("wrong", true));
        await manager.ConfirmImportAsync(preview.Token, true);
        await Reject(() => manager.ConfirmImportAsync(preview.Token, true));
        var inventory = await manager.ReadInventoryAsync(emptySession);
        Check(inventory.Items.Single() is { Enabled: false, CanEnable: true, State: "disabled" }, "Import defaults OFF");
        Check((await manager.CreateSessionPlanAsync()).Roots.Count == 0, "Imported code is not activated");
        Check(services.Contexts == 0 && services.HttpClients == 0, "Import and inventory are zero-code/zero-network");
        await manager.SetEnabledAsync(preview.Id, true);
        Check((await manager.ReadInventoryAsync(emptySession)).Items.Single().State == "enablePending", "Enable is pending restart");
        var enabledPlan = await new PlatformPluginManager(storage, []).CreateSessionPlanAsync();
        await using var enabledHost = Host(enabledPlan);
        var session = await enabledHost.DiscoverAsync();
        Check(session.Providers.Count == 1 && services.Contexts == 0, "Restart recognizes enabled provider without activation");
        var lookup = await enabledHost.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("fixture",
            (p, ct) => p.LookupAsync(new("title", "artist", "album", 3), ct));
        Check(lookup.IsSuccess, "Explicit call loads real approved DLL");
        await manager.SetEnabledAsync(preview.Id, false);
        Check((await manager.ReadInventoryAsync(session)).Items.Single().State == "disablePending", "Stop awaits restart, not fake unload");
        Check((await enabledHost.DiscoverAsync()).Providers.Count == 1, "Current session remains stable");
        await using var disabledHost = Host(await manager.CreateSessionPlanAsync());
        Check((await disabledHost.DiscoverAsync()).Providers.Count == 0, "Disabled in new session");

        var oldDll = Path.Combine(enabledPlan.Roots.Single(), "Fixture.dll");
        var oldHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldDll)));
        var upgrade = await manager.PrepareImportAsync(MakePackage(root, "1.0.1"));
        await manager.ConfirmImportAsync(upgrade.Token, true);
        Check(File.Exists(oldDll) && oldHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldDll))), "Update preserves loaded old payload");
        inventory = await manager.ReadInventoryAsync(session);
        Check(inventory.Items.Single() is { Version: "1.0.1", Enabled: false }, "Update requires explicit enablement again");
        // A selected immutable revision shadows the old same-ID test bundle, never duplicate routes.
        var oldBundle = Directory.GetParent(enabledPlan.Roots.Single())!.FullName;
        Check((await new PlatformPluginManager(storage, [oldBundle]).ReadInventoryAsync(session)).Items.Count == 1, "Imported same-ID revision replaces bundle candidate");
        var registry = File.ReadAllText(Path.Combine(storage, "platform-state.json"));
        var beforeCancel = Directory.GetDirectories(Path.Combine(storage, "Packages")).Length;
        await manager.PrepareImportAsync(package);
        await manager.CancelImportAsync();
        Check(Directory.GetDirectories(Path.Combine(storage, "Packages")).Length == beforeCancel && File.Exists(package), "Cancel removes only staging, preserves source ZIP");
        foreach (var bad in new[] { "../escape.dll", "sub/payload.dll", "Fixture.dll", "fixture.DLL", "evil.ps1", "C:escape.dll" })
            await Reject(async () => { await manager.PrepareImportAsync(MakePackage(root, "1.0.0", bad)); });
        await Reject(async () => { await manager.PrepareImportAsync(MakePackage(root, "1.0.0", api: 99)); });
        Check(registry == File.ReadAllText(Path.Combine(storage, "platform-state.json")), "Bad imports leave settings intact");
        await Reject(() => manager.SetEnabledAsync("../escape", true));
        await Reject(() => manager.SetEnabledAsync("unknown", true));

        var knownFolders = Directory.GetDirectories(Path.Combine(storage, "Packages")).ToHashSet();
        var tampered = await manager.PrepareImportAsync(package);
        var stage = Directory.GetDirectories(Path.Combine(storage, "Packages")).Single(p => !knownFolders.Contains(p));
        File.AppendAllText(Path.Combine(stage, "tests.approved", "platform.plugin.json"), " ");
        await Reject(() => manager.ConfirmImportAsync(tampered.Token, true));
        Check(registry == File.ReadAllText(Path.Combine(storage, "platform-state.json")), "Changed preview cannot publish");
        await manager.CancelImportAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await manager.PrepareImportAsync(package, cancellation.Token); throw new Exception("Ignored cancel"); }
        catch (OperationCanceledException) { }
        Check(File.Exists(package), "Cancellation never removes source");
        var batchManager = new PlatformPluginManager(Path.Combine(root, "batch-user"), []);
        var first = MakePackage(root, "1.0.0", id: "batch.first");
        var second = MakePackage(root, "1.0.0", id: "batch.second");
        var badPackage = MakePackage(root, "1.0.0", "../escape.dll");
        var batch = await batchManager.PrepareBatchAsync([first, second, badPackage]);
        Check(batch.Items.Count == 3 && batch.Items.Count(p => p.Error is null) == 2, "Bad package does not block valid batch items");
        Check(batch.Items.All(p => !p.FileName.Contains(Path.DirectorySeparatorChar)), "Batch DTO only exposes base names");
        Check((await batchManager.ReadInventoryAsync(emptySession)).Items.Count == 0, "Batch preview registers no code");
        await Reject(() => batchManager.ConfirmBatchAsync(batch.Token, false));
        await Reject(() => batchManager.ConfirmBatchAsync("wrong", true));
        var batchResults = await batchManager.ConfirmBatchAsync(batch.Token, true);
        Check(batchResults.Count(p => p.Error is null) == 2 && batchResults.Count(p => p.Error is not null) == 1, "Per-file batch results");
        await Reject(() => batchManager.ConfirmBatchAsync(batch.Token, true));
        Check((await batchManager.ReadInventoryAsync(emptySession)).Items.All(p => !p.Enabled && p.CanEnable), "All batch imports stay OFF");
        var duplicateBatch = await batchManager.PrepareBatchAsync([first, first, second]);
        Check(duplicateBatch.Items.Count(p => p.Error == "duplicatePlugin") == 2, "Both duplicate IDs are rejected, no order-dependent winner");
        await batchManager.CancelImportAsync();
        await Reject(() => batchManager.ConfirmBatchAsync(duplicateBatch.Token, true));
        await Reject(() => batchManager.PrepareBatchAsync(Enumerable.Repeat(first, 17).ToArray()));
        await Reject(() => batchManager.PrepareBatchAsync([]));
        var batchStorage = Path.Combine(root, "batch-user", "Packages");
        var beforeBatch = Directory.GetDirectories(batchStorage).ToHashSet();
        var changedBatch = await batchManager.PrepareBatchAsync([first, second]);
        var firstStage = Directory.GetDirectories(batchStorage).Except(beforeBatch).Single(p => Directory.Exists(Path.Combine(p, "batch.first")));
        File.AppendAllText(Path.Combine(firstStage, "batch.first", "platform.plugin.json"), " ");
        var changedResults = await batchManager.ConfirmBatchAsync(changedBatch.Token, true);
        Check(changedResults.Single(p => p.Id == "batch.first").Error is not null && changedResults.Single(p => p.Id == "batch.second").Error is null,
            "Changed staged package rejected independently; other item commits");
        var cancelledCount = Directory.GetDirectories(batchStorage).Length;
        await batchManager.PrepareBatchAsync([first, second]); await batchManager.CancelImportAsync();
        Check(Directory.GetDirectories(batchStorage).Length == cancelledCount && File.Exists(first) && File.Exists(second), "Cancel cleans entire uncommitted batch only");
        try { await batchManager.PrepareBatchAsync([first], cancellation.Token); throw new Exception("Ignored batch cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS batch import: mixed valid/invalid, no pre-approval registration, OFF by default, duplicate IDs, replay, cancel, tamper, limits, source preservation.");
        var realPackages = Environment.GetEnvironmentVariable("AURALIS_PLUGIN_PACKAGES");
        if (!string.IsNullOrEmpty(realPackages))
        {
            var realManager = new PlatformPluginManager(Path.Combine(root, "actual-archives"), []);
            var archives = Directory.GetFiles(realPackages, "*.auralis-plugin");
            Check(archives.Length > 0, "Actual package directory is not empty");
            var actualBatch = await realManager.PrepareBatchAsync(archives);
            Check(actualBatch.Items.All(p => p.Error is null), "All actual archives validate in one batch");
            var actualResults = await realManager.ConfirmBatchAsync(actualBatch.Token, true);
            Check(actualResults.All(p => p.Error is null), "All actual archives commit in one batch");
            var imported = await realManager.ReadInventoryAsync(emptySession);
            Check(imported.Items.Count == archives.Length && imported.Items.All(p => !p.Enabled && p.CanEnable), "Actual archives import intact and disabled");
            foreach (var item in imported.Items) await realManager.SetEnabledAsync(item.Id, true);
            await using var realHost = Host(await realManager.CreateSessionPlanAsync());
            var contexts = services.Contexts;
            var actualSnapshot = await realHost.DiscoverAsync();
            Check(actualSnapshot.Providers.All(p => !p.Provider.UsesLegacyCommentArtworkPolicy),
                "Current private archives declare their own artwork policy; no implicit host platform table");
            Check(actualSnapshot.Plugins.Count == archives.Length, "Actual imported archives recognized in new session");
            var actualSettingsPath = Path.Combine(root, "actual-settings.json");
            var definitions = actualSnapshot.Providers.SelectMany(p => p.Provider.Settings).ToArray();
            var legacyValues = new Dictionary<string, string>();
            foreach (var setting in definitions)
                foreach (var alias in setting.LegacyKeys)
                    legacyValues[alias] = setting.Kind == "endpoint" ? "https://example.test/service/" : setting.Choices.Last().Value;
            await File.WriteAllTextAsync(actualSettingsPath, System.Text.Json.JsonSerializer.Serialize(legacyValues));
            var actualSettings = new Auralis.Services.PlatformSettingsStore(actualSettingsPath)
            {
                ResolveDeclarationsAsync = (id, _) => Task.FromResult<IReadOnlyList<PlatformSettingManifest>>(
                    actualSnapshot.Providers.Where(p => p.PluginId == id).SelectMany(p => p.Provider.Settings).ToArray())
            };
            foreach (var registration in actualSnapshot.Providers)
                foreach (var setting in registration.Provider.Settings.Where(s => s.LegacyKeys.Count > 0))
                {
                    Check(setting.TryNormalize(legacyValues[setting.LegacyKeys[0]], out var expected), "Actual package migration input validates");
                    Check(await actualSettings.ForPlugin(registration.PluginId).GetAsync(setting.Key, default) == expected,
                        "Actual package declarations drive provider settings reads");
                }
            Check(services.Contexts == contexts && services.HttpClients == 0, "Actual archive discovery never activates providers");
            var vaults = new Dictionary<string, MemoryStore>();
            IPlatformCredentialStore Vault(string scope)
            {
                if (!vaults.TryGetValue(scope, out var vault)) vaults[scope] = vault = new MemoryStore();
                return vault;
            }
            using var credentialFactory = new Auralis.Services.DefaultPlatformHostContextFactory(actualSettings, Auralis.Services.NullAppLogger.Instance, Vault);
            foreach (var manifest in actualSnapshot.Plugins.Where(p => p.SchemaVersion >= 3))
            {
                foreach (var alias in manifest.CredentialAliases)
                    ((MemoryStore)Vault(alias.Scope)).Values[alias.LegacyKey] = [1, 2];
                var credentialStore = credentialFactory.CreateContext(manifest).CredentialStore;
                foreach (var alias in manifest.CredentialAliases)
                {
                    using var credential = await credentialStore.GetAsync(alias.Key, default);
                    Check(credential?.Secret.Span.SequenceEqual(new byte[] { 1, 2 }) == true, "Actual package preserves synthetic old account");
                    await credentialStore.SetAsync(alias.Key, new byte[] { 3 }, null, default);
                    Check(((MemoryStore)Vault(alias.Scope)).Values[alias.LegacyKey][0] == 3, "Actual package refresh uses its declared address");
                }
                await credentialFactory.RevokeCredentialsAsync(manifest.Id, manifest, default);
                Check(manifest.CredentialAliases.All(a => !((MemoryStore)Vault(a.Scope)).Values.ContainsKey(a.LegacyKey)),
                    "Actual package disable clears all declared aliases, including service credentials");
            }
            Console.WriteLine($"PASS actual package import: {archives.Length} archives, approval, default OFF, explicit enable, restart discovery, no platform activation.");
        }
        Console.WriteLine("PASS management: explicit trust, default OFF, persisted enable/disable after restart, inert discovery, real DLL, immutable update, bundle replacement, cancellation, traversal/duplicate/script/API rejection, preview tamper, state preservation.");
    }
}
