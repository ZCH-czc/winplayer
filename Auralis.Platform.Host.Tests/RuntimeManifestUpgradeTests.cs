using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class RuntimeManifestUpgradeTests
{
    internal static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        foreach (var schema in new[] { 1, 2, 3, 4 })
        {
            var test = Path.Combine(root, "schema-" + schema);
            var payload = Path.Combine(test, "payload"); Directory.CreateDirectory(payload);
            var dll = Path.Combine(payload, "Fixture.dll"); File.Copy(typeof(FixturePlugin).Assembly.Location, dll);
            var dllHash = SHA256.HashData(await File.ReadAllBytesAsync(dll));
            var document = JsonNode.Parse("""
                {"schemaVersion":5,"id":"tests.approved","displayName":"Fixture","version":"1.0.0",
                 "minimumHostApiVersion":1,"maximumHostApiVersion":1,"entryAssembly":"Fixture.dll",
                 "entryType":"Auralis.Platform.Host.Tests.FixturePlugin",
                 "credentialAliases":[{"key":"session","scope":"fixture.old","legacyKey":"account.v1"}],
                 "hostRequirements":{"minimumHostSdkVersion":"1.2.0","requiredFeatures":["comment-artwork.v1","credential-aliases.v1","lyrics-lookup.v1"]},
                 "providers":[{"id":"fixture","displayName":"Fixture","capabilities":["LyricsLookup"],"commentArtworkDomains":["cdn.example.test"]}]}
                """)!.AsObject();
            async Task<string> Package(JsonObject manifest, string name)
            {
                await File.WriteAllTextAsync(Path.Combine(payload, "platform.plugin.json"), manifest.ToJsonString());
                var archive = Path.Combine(test, name + ".auralis-plugin");
                ZipFile.CreateFromDirectory(payload, archive); return archive;
            }
            var old = (JsonObject)document.DeepClone(); old["schemaVersion"] = schema;
            old["providers"]![0]!.AsObject().Remove("commentArtworkDomains");
            if (schema < 4) old.Remove("hostRequirements");
            if (schema < 3) old.Remove("credentialAliases");
            var archive = await Package(old, "old");
            var oldArchiveHash = SHA256.HashData(await File.ReadAllBytesAsync(archive));
            var storage = Path.Combine(test, "managed");
            // Reconstruct a previous release's explicitly approved/enabled installation, in test state only.
            var prior = new PlatformPluginManager(storage, [], minimumManifestSchemaVersion: 1);
            var previousPreview = await prior.PrepareImportAsync(archive);
            await prior.ConfirmImportAsync(previousPreview.Token, true);
            await prior.SetEnabledAsync("tests.approved", true);
            var priorPlan = await prior.CreateSessionPlanAsync();
            var oldManifest = (await new PlatformPluginCatalog(priorPlan.Roots).DiscoverAsync()).Plugins.Single();
            var oldManifestBytes = await File.ReadAllBytesAsync(oldManifest.ManifestPath);
            Check(oldManifest.Providers.Single().CommentArtworkPolicy.Domains.Count == 0, "Old metadata gets no inferred image domains");
            await using (var legacyControl = new PlatformBackendService([], NullAppLogger.Instance, prior,
                new PlatformSettingsStore(Path.Combine(test, "legacy-control-settings.json"))))
            {
                Check(!await legacyControl.DisableAndClearAsync("tests.approved", 0, default),
                    "Old package disable reports incomplete cleanup rather than guessing account addresses");
                Check(!(await prior.ReadInventoryAsync(await legacyControl.DiscoverAsync())).Items.Single().Enabled,
                    "Even an old package's enabled preference can be turned off");
            }
            await prior.SetEnabledAsync("tests.approved", true); // Restore the synthetic pre-upgrade starting state.
            var current = new PlatformPluginManager(storage, []);
            var statePath = Path.Combine(storage, "platform-state.json");
            var originalState = await File.ReadAllBytesAsync(statePath);
            var settingsPath = Path.Combine(test, "settings.json");
            await File.WriteAllTextAsync(settingsPath, "{\"fixture.nonsecret\":\"kept\"}");
            await using var backend = new PlatformBackendService([], NullAppLogger.Instance, current, new PlatformSettingsStore(settingsPath));
            var snapshot = await backend.DiscoverAsync();
            Check(snapshot.Providers.Count == 0, "Old enabled preference does not activate in the current player");
            Check((await current.ReadInventoryAsync(snapshot)).Items.Single() is
                { State: "upgradeRequired", CompatibilityIssue: "manifestUpgradeRequired", Enabled: true, CanEnable: false, Active: false }, "Old installation remains visible with upgrade explanation");
            Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(originalState), "Inspecting old state never rewrites preferences");
            try { await current.SetEnabledAsync("tests.approved", true); throw new Exception("Old package enabled"); }
            catch (PlatformPluginCompatibilityException e) { Check(e.Code == "manifestUpgradeRequired", "Enable uses a typed upgrade error"); }
            await using (var explicitRoot = new PlatformBackendService(priorPlan.Roots, NullAppLogger.Instance,
                             settings: new PlatformSettingsStore(Path.Combine(test, "direct-settings.json"))))
            {
                Check((await explicitRoot.DiscoverAsync()).Providers.Count == 0, "Passing an unpacked directory cannot bypass runtime floor");
                Check(explicitRoot.Diagnostics.Any(d => d.Code == PlatformPluginDiagnosticCode.ManifestUpgradeRequired), "Direct runtime diagnostic is actionable");
            }
            var updated = await Package(document, "updated");
            var review = await current.PrepareBatchAsync([archive, updated]);
            Check(review.Items[0].Error == "manifestUpgradeRequired" && review.Items[0].Preview is null, "Old import rejected before approval");
            Check(review.Items[1].Preview?.CredentialAliases?.Single() == new PlatformCredentialAlias("session", "fixture.old", "account.v1"), "Upgrade preview exposes the exact account grant");
            Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(originalState), "Preparing upgrade does not install it");
            try { await current.ConfirmBatchAsync(review.Token, false); throw new Exception("Trust bypassed"); }
            catch (InvalidDataException) { checks++; }
            Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(originalState), "Refused trust preserves previous installation");
            var imported = await current.ConfirmBatchAsync(review.Token, true);
            Check(imported.Count(r => r.Error is null) == 1 && imported.Count(r => r.Error == "manifestUpgradeRequired") == 1,
                "Only explicitly confirmed current package imports; old-package error remains visible");
            Check((await current.ReadInventoryAsync(snapshot)).Items.Single() is { State: "disabled", Enabled: false, CanEnable: true }, "Upgrade remains off until enabled");
            Check((await backend.DiscoverAsync()).Providers.Count == 0, "Existing host session never hot-loads upgrade");
            await current.SetEnabledAsync("tests.approved", true);
            await using var restarted = new PlatformBackendService([], NullAppLogger.Instance, current, new PlatformSettingsStore(settingsPath));
            var active = await restarted.DiscoverAsync();
            Check(active.Providers.Count == 1 && active.Plugins.Single().SchemaVersion == 5, "Restart recognizes current explicit plugin");
            Check((await restarted.LookupLyricsAsync(new("fixture", "artist", "album", 30), default)) is not null,
                "Real DLL capability still works after explicit upgrade: " + string.Join(",", restarted.Diagnostics.Select(d => d.Code)));
            var vaults = new Dictionary<string, MemoryStore>();
            IPlatformCredentialStore Store(string scope) => vaults.TryGetValue(scope, out var value) ? value : vaults[scope] = new MemoryStore();
            var legacyVault = (MemoryStore)Store("fixture.old"); legacyVault.Values["account.v1"] = [7, 8];
            using var factory = new DefaultPlatformHostContextFactory(new PlatformSettingsStore(settingsPath), NullAppLogger.Instance, Store);
            var credentials = factory.CreateContext(active.Plugins.Single()).CredentialStore;
            using (var saved = await credentials.GetAsync("session", default)) Check(saved!.Secret.Span.SequenceEqual(new byte[] { 7, 8 }), "Declared upgrade preserves original synthetic account address");
            await credentials.SetAsync("session", new byte[] { 9 }, null, default);
            Check(legacyVault.Values["account.v1"].SequenceEqual(new byte[] { 9 }), "Refresh writes the declared original address");
            await factory.RevokeCredentialsAsync("tests.approved", active.Plugins.Single(), default);
            Check(!legacyVault.Values.ContainsKey("account.v1"), "Declared cleanup deletes that same synthetic address");
            Check((await File.ReadAllBytesAsync(oldManifest.ManifestPath)).SequenceEqual(oldManifestBytes), "Old installed package was not edited in place");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(archive)).SequenceEqual(oldArchiveHash) && SHA256.HashData(await File.ReadAllBytesAsync(dll)).SequenceEqual(dllHash), "Original archive and DLL remain unchanged");
            Check(await File.ReadAllTextAsync(settingsPath) == "{\"fixture.nonsecret\":\"kept\"}", "Original non-secret settings preserved");
        }
        Console.WriteLine($"PASS {checks} runtime schema upgrade checks: old packages visible/not active, explicit grant review, no state rewrite, default OFF, restart, real DLL and synthetic account continuity.");
    }
}
