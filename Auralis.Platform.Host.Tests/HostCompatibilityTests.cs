using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host.Tests;

internal static class HostCompatibilityTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "tests.approved");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(FixturePlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
        var path = Path.Combine(folder, "platform.plugin.json");
        var valid = JsonNode.Parse("""
            {"schemaVersion":4,"id":"tests.approved","displayName":"Fixture","version":"1.0.0",
             "minimumHostApiVersion":1,"maximumHostApiVersion":1,"entryAssembly":"Fixture.dll",
             "entryType":"Auralis.Platform.Host.Tests.FixturePlugin",
             "hostRequirements":{"minimumHostSdkVersion":"1.1.0","requiredFeatures":["lyrics-lookup.v1"]},
             "providers":[{"id":"fixture","displayName":"Fixture","capabilities":["LyricsLookup"]}]}
            """)!.AsObject();
        async Task<PlatformPluginDiscoveryResult> Discover(JsonObject json, PlatformHostCompatibility? profile = null)
        {
            await File.WriteAllTextAsync(path, json.ToJsonString());
            return await new PlatformPluginCatalog([folder], profile).DiscoverAsync();
        }
        async Task Reject(JsonObject json, PlatformPluginDiagnosticCode expected, PlatformHostCompatibility? profile = null)
        {
            var found = await Discover(json, profile);
            Check(found.Plugins.Count == 0 && found.Diagnostics.Any(d => d.Code == expected), "Expected safe compatibility rejection: " + expected);
        }
        var current = PlatformHostCompatibility.Current;
        var oldSdk = new PlatformHostCompatibility(new Version(1, 0, 0), current.Features);
        var noFeatures = new PlatformHostCompatibility(new Version(1, 1, 0), []);
        var oldSchema = new PlatformHostCompatibility(new Version(1, 0, 0), [], 3);
        Check(typeof(PlatformPluginHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] == PlatformHostCompatibility.SdkVersion,
            "Published Host SDK version matches the compatibility profile");
        Check((await Discover(valid)).Plugins.Single().HostRequirements?.RequiredFeatures.SequenceEqual(["lyrics-lookup.v1"]) == true,
            "New format preserves validated requirements");
        await Reject(valid, PlatformPluginDiagnosticCode.HostSdkIncompatible, oldSdk);
        await Reject(valid, PlatformPluginDiagnosticCode.HostFeatureUnsupported, noFeatures);
        await Reject(valid, PlatformPluginDiagnosticCode.ManifestSchemaUnsupported, oldSchema);

        foreach (var version in new[] { "", "1.1", "1.1.0.0", "01.1.0", "1.1.0-preview", " 1.1.0", "0.1.0", "https://example.test", new string('9', 40) })
        {
            var test = (JsonObject)valid.DeepClone();
            test["hostRequirements"]!["minimumHostSdkVersion"] = version;
            await Reject(test, PlatformPluginDiagnosticCode.ManifestInvalid);
        }
        foreach (var mutation in new Action<JsonObject>[] {
            j => j.Remove("hostRequirements"),
            j => j["hostRequirements"] = null,
            j => j["hostRequirements"]!["requiredFeatures"] = null,
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray("lyrics-lookup.v1", "lyrics-lookup.v1"),
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray("*"),
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray("Lyrics-Lookup.v1"),
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray(), // underdeclared new interface
            j => j["hostRequirements"]!["execute"] = "not-allowed",
            j => j["schemaVersion"] = 3,
            j => j["schemaVersion"] = "4",
            j => j["hostRequirements"]!["requiredFeatures"] = new JsonArray(Enumerable.Range(0,33).Select(i => JsonValue.Create("feature.v" + i)).ToArray()) })
        {
            var test = (JsonObject)valid.DeepClone(); mutation(test);
            await Reject(test, PlatformPluginDiagnosticCode.ManifestInvalid);
        }
        var futureFeature = (JsonObject)valid.DeepClone();
        futureFeature["hostRequirements"]!["requiredFeatures"] = new JsonArray("lyrics-lookup.v1", "future-decoder.v9");
        await Reject(futureFeature, PlatformPluginDiagnosticCode.HostFeatureUnsupported);
        var futureSdk = (JsonObject)valid.DeepClone(); futureSdk["hostRequirements"]!["minimumHostSdkVersion"] = "9.0.0";
        await Reject(futureSdk, PlatformPluginDiagnosticCode.HostSdkIncompatible);
        var futureSchema = (JsonObject)valid.DeepClone(); futureSchema["schemaVersion"] = 99; futureSchema["unknownFutureField"] = true;
        await Reject(futureSchema, PlatformPluginDiagnosticCode.ManifestSchemaUnsupported);
        var futureApi = (JsonObject)valid.DeepClone(); futureApi["minimumHostApiVersion"] = 2; futureApi["maximumHostApiVersion"] = 2;
        await Reject(futureApi, PlatformPluginDiagnosticCode.HostApiIncompatible);
        for (var schema = 1; schema <= 3; schema++)
        {
            var legacy = (JsonObject)valid.DeepClone(); legacy["schemaVersion"] = schema; legacy.Remove("hostRequirements");
            Check((await Discover(legacy)).Plugins.Single().HostRequirements is null, "Old schemas stay compatible without fabricated requirements");
        }

        await Discover(valid);
        var hashes = Directory.GetFiles(folder).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        Directory.CreateDirectory(Path.Combine(root, ".approvals"));
        await File.WriteAllTextAsync(Path.Combine(root, ".approvals", "tests.approved.json"), JsonSerializer.Serialize(new PluginInstallReceipt(2, "tests.approved", hashes)));
        var services = new FixtureServices();
        foreach (var profile in new[] { oldSdk, noFeatures, oldSchema })
        {
            await using var denied = new PlatformPluginHost(new PlatformPluginHostOptions([folder], services, trustPolicy: PlatformPluginIntegrity.VerifyAsync, compatibility: profile));
            Check((await denied.DiscoverAsync()).Plugins.Count == 0, "Incompatible plugin never registers capabilities");
            var result = await denied.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("fixture", (p, ct) => p.LookupAsync(new("x", "y", "z", 1), ct));
            Check(!result.IsSuccess && services.Contexts == 0 && services.HttpClients == 0, "Rejected before DLL activation/context/network");
        }
        await using var approved = new PlatformPluginHost(new PlatformPluginHostOptions([folder], services, trustPolicy: PlatformPluginIntegrity.VerifyAsync));
        var success = await approved.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("fixture", (p, ct) => p.LookupAsync(new("x", "y", "z", 1), ct));
        Check(success.IsSuccess && services.Contexts == 1, "Compatible real DLL remains lazy and callable");

        async Task<string> Package(JsonObject json)
        {
            var package = Path.Combine(root, Guid.NewGuid().ToString("N") + ".auralis-plugin");
            using var zip = ZipFile.Open(package, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("platform.plugin.json").Open())) await writer.WriteAsync(json.ToJsonString());
            zip.CreateEntryFromFile(typeof(FixturePlugin).Assembly.Location, "Fixture.dll");
            return package;
        }
        var managerRoot = Path.Combine(root, "managed");
        // Simulate the prior schema 4 host contract, not the current application's import policy.
        var manager = new PlatformPluginManager(managerRoot, [], minimumManifestSchemaVersion: 1);
        var goodPackage = await Package(valid);
        var batch = await manager.PrepareBatchAsync([goodPackage, await Package(futureSdk), await Package(futureFeature), await Package(futureSchema), await Package(futureApi)]);
        Check(batch.Items.Select(i => i.Error).SequenceEqual(new string?[] { null, "hostSdkIncompatible", "hostFeatureUnsupported", "manifestSchemaUnsupported", "hostApiIncompatible" }),
            "Batch reports safe distinct compatibility errors alongside valid items");
        Check(batch.Items[0].Preview?.HostRequirements?.MinimumHostSdkVersion == "1.1.0", "Preview shows minimum SDK");
        var results = await manager.ConfirmBatchAsync(batch.Token, true);
        Check(results.Count(r => r.Error is null) == 1, "Only the compatible package installs");
        await manager.SetEnabledAsync("tests.approved", true);
        var downgraded = new PlatformPluginManager(managerRoot, [], oldSdk, minimumManifestSchemaVersion: 1);
        await using var emptyHost = new PlatformPluginHost(new PlatformPluginHostOptions([], services));
        var empty = await emptyHost.DiscoverAsync();
        var inventory = await downgraded.ReadInventoryAsync(empty);
        Check(inventory.Items.Single() is { State: "incompatible", CanEnable: false, Enabled: true, CompatibilityIssue: "hostSdkIncompatible" },
            "Downgraded host explains incompatible installed preference without erasing it");
        Check((await downgraded.CreateSessionPlanAsync()).Roots.Count == 0, "Incompatible preference never creates a route");
        try { await downgraded.SetEnabledAsync("tests.approved", true); throw new Exception("Incompatible enable accepted"); }
        catch (InvalidDataException) { }
        await downgraded.SetEnabledAsync("tests.approved", false);
        Check((await downgraded.ReadInventoryAsync(empty)).Items.Single().Enabled == false, "Incompatible item can still be disabled");
        Check((await manager.ReadInventoryAsync(empty)).Items.Single().CanEnable, "Compatible host recovers without reinstall or account changes");
        var readonlyInventory = await PlatformPluginInventory.ReadAsync([folder], empty, compatibility: oldSdk);
        Check(readonlyInventory.Items.Single().State == "incompatible", "Read-only inventory also distinguishes incompatibility");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await manager.PrepareBatchAsync([goodPackage], cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS Host SDK compatibility: schema 1-4, strict requirements, older SDK/missing features/future schema/API, zero activation/network, real DLL success, mixed batch, preview, disabled-by-incompatibility recovery, cancellation.");
    }
}
