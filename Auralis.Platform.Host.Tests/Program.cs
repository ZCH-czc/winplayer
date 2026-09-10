using System.Security.Cryptography;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;
using Auralis.Platform.Host.Tests;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string InstallFixture(string root)
{
    var folder = Path.Combine(root, "tests.approved");
    Directory.CreateDirectory(folder);
    File.Copy(typeof(FixturePlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
    File.WriteAllText(Path.Combine(folder, "platform.plugin.json"), """
        {"schemaVersion":1,"id":"tests.approved","displayName":"Fixture","version":"1.0.0",
         "minimumHostApiVersion":1,"maximumHostApiVersion":1,"entryAssembly":"Fixture.dll",
         "entryType":"Auralis.Platform.Host.Tests.FixturePlugin",
         "providers":[{"id":"fixture","displayName":"Fixture","capabilities":["LyricsLookup"]}]}
        """);
    return folder;
}
static void Approve(string root, string folder)
{
    var hashes = Directory.GetFiles(folder).ToDictionary(file => Path.GetFileName(file)!,
        file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    Directory.CreateDirectory(Path.Combine(root, ".approvals"));
    File.WriteAllText(Path.Combine(root, ".approvals", "tests.approved.json"),
        JsonSerializer.Serialize(new PluginInstallReceipt(1, "tests.approved", hashes!)));
}
static PlatformPluginHost NewHost(string root, FixtureServices services) =>
    new(new PlatformPluginHostOptions([root], services, trustPolicy: PlatformPluginIntegrity.VerifyAsync));
static Task<PlatformResult<PlatformLyricsLookupResult>> Lookup(PlatformPluginHost host) =>
    host.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("fixture",
        (capability, token) => capability.LookupAsync(new("title", "artist", "album", 30), token));

var root = Path.Combine(Path.GetTempPath(), "Auralis-plugin-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var choice = new PlatformSettingManifest { Key="quality",Label="Quality",Kind="choice",DefaultValue="auto",
        Choices=[new("auto","Automatic"),new("high","High")] };
    var endpoint = new PlatformSettingManifest { Key="server",Label="Server",Kind="endpoint",Required=true };
    Check(PlatformSettingManifest.IsValidList([choice,endpoint]),"Declared settings validate");
    Check(!PlatformSettingManifest.IsValidList([choice,choice]),"Duplicate settings rejected");
    Check(!PlatformSettingManifest.IsValidList([choice with { Kind="script" }]),"Executable settings rejected");
    Check(!PlatformSettingManifest.IsValidList([choice with { Choices=null! }]),"Null settings rejected safely");
    Check(!choice.TryNormalize("not-declared",out _),"Choice must belong to manifest");
    Check(!endpoint.TryNormalize("https://user:password@example.com/",out _) &&
          !endpoint.TryNormalize("https://example.com/?token=secret",out _) &&
          !endpoint.TryNormalize("http://example.com/",out _),"Endpoint excludes secret-bearing and unsafe addresses");
    Check(endpoint.TryNormalize("http://127.0.0.1:1234/",out _),"Explicit local endpoint allowed");
    var settingsFolder=Path.Combine(root,"settings-discovery");
    var settingsFixture=InstallFixture(settingsFolder);
    var settingsManifest=Path.Combine(settingsFixture,"platform.plugin.json");
    var manifestText=File.ReadAllText(settingsManifest).Replace("\"schemaVersion\":1","\"schemaVersion\":2")
        .Replace("\"capabilities\":[\"LyricsLookup\"]", "\"capabilities\":[\"LyricsLookup\"],\"settings\":"+JsonSerializer.Serialize(new[]{choice,endpoint}));
    File.WriteAllText(settingsManifest,manifestText);
    var settingDiscovery=await new PlatformPluginCatalog([settingsFolder]).DiscoverAsync();
    Check(settingDiscovery.Plugins.Single().Providers.Single().Settings.Count==2,"Schema 2 parses settings without loading assemblies");
    File.WriteAllText(settingsManifest,manifestText.Replace("\"kind\":\"choice\"","\"kind\":\"script\""));
    Check((await new PlatformPluginCatalog([settingsFolder]).DiscoverAsync()).Plugins.Count==0,"Invalid schema rejected at discovery");
    var settingsPath=Path.Combine(root,"settings.json");
    await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new Dictionary<string,string> {
        ["old.preference"]="high", ["old.endpoint"]="https://example.com/", ["unrelated"]="kept"
    }));
    var declaredChoice = choice with { LegacyKeys=["old.preference"] };
    var declaredEndpoint = endpoint with { LegacyKeys=["old.endpoint"] };
    Task<IReadOnlyList<PlatformSettingManifest>> Definitions(string id, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<PlatformSettingManifest>>(id == "example.a"
            ? [declaredChoice, declaredEndpoint, declaredEndpoint with {Key="second"}]
            : id == "example.b" ? [choice, endpoint] : []);
    var settingsStore=new Auralis.Services.PlatformSettingsStore(settingsPath) { ResolveDeclarationsAsync=Definitions };
    Check(await settingsStore.ForPlugin("example.a").GetAsync("quality",default)=="high","Plugin-declared legacy preference");
    Check(await settingsStore.ForPlugin("example.a").GetAsync("server",default)=="https://example.com","Legacy endpoints normalized");
    Check(await settingsStore.ForPlugin("example.b").GetAsync("quality",default)=="auto","Another plugin cannot inherit undeclared legacy settings");
    Check(await settingsStore.ForPlugin("example.a").GetAsync("old.endpoint",default) is null,"Raw legacy keys are not exposed");
    Check(await settingsStore.ForPlugin("absent").GetAsync("quality",default) is null,"Absent plugin has no settings");
    await settingsStore.SetScopedAsync("example.a","quality","auto",default);
    await settingsStore.SetScopedAsync("example.a","server","",default);
    var settingsReload=new Auralis.Services.PlatformSettingsStore(settingsPath) { ResolveDeclarationsAsync=Definitions };
    Check(await settingsReload.ForPlugin("example.a").GetAsync("quality",default)=="auto","Explicit default overrides legacy on restart");
    Check(await settingsReload.ForPlugin("example.a").GetAsync("server",default)=="","Explicit clear overrides legacy on restart");
    Check(await settingsReload.ForPlugin("example.a").GetAsync("second",default)=="https://example.com","Clearing one provider does not clear another");
    Check(JsonSerializer.Deserialize<Dictionary<string,string>>(await File.ReadAllTextAsync(settingsPath))!["unrelated"]=="kept","Save preserves unrelated non-secret legacy data");
    await settingsReload.SetScopedAsync("example.b","quality","high",default);
    Check(await settingsReload.ForPlugin("example.a").GetAsync("quality",default)=="auto","Writes remain plugin scoped");
    await File.WriteAllTextAsync(settingsPath, "{\"old.preference\":\"invalid\",\"old.endpoint\":\"https://example.com/?token=secret\"}");
    var invalidLegacy=new Auralis.Services.PlatformSettingsStore(settingsPath) { ResolveDeclarationsAsync=Definitions };
    Check(await invalidLegacy.ForPlugin("example.a").GetAsync("quality",default)=="auto" &&
          await invalidLegacy.ForPlugin("example.a").GetAsync("server",default)=="","Invalid legacy values never reach UI or provider");
    Check(!PlatformSettingManifest.IsValidList([choice with {LegacyKeys=null!}]) &&
          !PlatformSettingManifest.IsValidList([choice with {LegacyKeys=["plugin:other:quality"]}]) &&
          !PlatformSettingManifest.IsValidList([choice with {LegacyKeys=["old","OLD"]}]),"Migration aliases are bounded plain keys");
    var scopeRejected=false;
    try { await settingsStore.ForPlugin("example.a").GetAsync("plugin:example.b:quality",default); }
    catch (ArgumentException) { scopeRejected=true; }
    Check(scopeRejected,"Keys cannot escape the plugin scope");
    Console.WriteLine("PASS declared settings: inert discovery, validation, isolation, declared migration and persistence");
    var services = new FixtureServices();
    await using var emptySessionHost = NewHost(Path.Combine(root, "empty-session"), services);
    var emptySession = await emptySessionHost.DiscoverAsync();
    Check((await PlatformPluginInventory.ReadAsync([Path.Combine(root, "absent")], emptySession)).Items.Count == 0,
        "Inventory with no plugin directory is an empty state, not a failed operation");
    await using (var missing = NewHost(Path.Combine(root, "missing"), services))
    {
        Check((await missing.DiscoverAsync()).Providers.Count == 0, "Missing plugin root");
        Check(!(await Lookup(missing)).IsSuccess, "Missing provider must fail without affecting the host");
    }
    var folder = InstallFixture(root);
    var inventory = await PlatformPluginInventory.ReadAsync([root], emptySession);
    Check(inventory.Items.Single().State == "untrusted", "Unapproved package remains visible but never executable");
    await using (var unapproved = NewHost(root, services))
    {
        Check((await unapproved.DiscoverAsync()).Providers.Count == 0, "Unapproved DLL must not be discoverable as trusted");
        Check(unapproved.Diagnostics.Any(d => d.Code == PlatformPluginDiagnosticCode.PluginNotTrusted), "Typed trust diagnostic");
        Check(services.Contexts == 0 && services.HttpClients == 0, "Discovery is inert");
    }
    Approve(root, folder);
    inventory = await PlatformPluginInventory.ReadAsync([root], emptySession);
    Check(inventory.Items.Single().State == "restartRequired", "New package is not silently hot loaded");
    Check(services.Contexts == 0 && services.HttpClients == 0, "Inventory never activates or accesses a platform");
    await using (var approved = NewHost(root, services))
    {
        Check((await approved.DiscoverAsync()).Providers.Count == 1, "Approved manifest");
        inventory = await PlatformPluginInventory.ReadAsync([root], await approved.DiscoverAsync());
        Check(inventory.Items.Single().State == "verified", "Current approved package displayed accurately");
        var safeJson = JsonSerializer.Serialize(inventory);
        Check(!safeJson.Contains(root, StringComparison.OrdinalIgnoreCase) && !safeJson.Contains("Fixture.dll"), "Inventory exposes no filesystem or assembly paths");
        var removedInventory = await PlatformPluginInventory.ReadAsync([Path.Combine(root, "absent")], await approved.DiscoverAsync());
        Check(removedInventory.Items.Single().State == "unavailable", "Missing disk package is not shown as available from cached runtime");
        Check(services.Contexts == 0 && services.HttpClients == 0, "Approved discovery is still inert");
        Check((await Lookup(approved)).Value.Text == "[00:01]test", "Real DLL loads and routes");
        Check((await Lookup(approved)).IsSuccess && services.Contexts == 1, "Repeated calls reuse a single plugin");
        Check(services.HttpClients == 0, "No implicit HTTP");
    }
    await using (var changedAfterDiscovery = NewHost(root, services))
    {
        await changedAfterDiscovery.DiscoverAsync();
        File.AppendAllText(Path.Combine(folder, "platform.plugin.json"), " ");
        Check(!(await Lookup(changedAfterDiscovery)).IsSuccess, "Changed payload rejected again at activation");
        Check(services.Contexts == 1, "Tampered payload never creates a host context");
    }
    Approve(root, folder);
    File.WriteAllText(Path.Combine(folder, "unexpected.dll"), "not approved");
    Check((await PlatformPluginInventory.ReadAsync([root], emptySession)).Items.Single().State == "untrusted", "Refresh detects added files");
    await using (var extra = NewHost(root, services))
        Check((await extra.DiscoverAsync()).Plugins.Count == 0, "Unlisted payload rejected");
    File.Delete(Path.Combine(folder, "unexpected.dll"));
    // A loaded DLL can remain memory mapped after ALC disposal on Windows. Use a fresh fixture.
    var tamperedRoot = Path.Combine(root, "tampered");
    var tamperedFolder = InstallFixture(tamperedRoot);
    Approve(tamperedRoot, tamperedFolder);
    File.WriteAllBytes(Path.Combine(tamperedFolder, "Fixture.dll"), [0, 1, 2]);
    await using (var tampered = NewHost(tamperedRoot, services))
        Check((await tampered.DiscoverAsync()).Plugins.Count == 0, "Changed DLL rejected before load");
    await using (var cancelled = NewHost(root, services))
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await cancelled.DiscoverAsync(cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("PASS host: missing/approved/unapproved, zero-network discovery, lazy real DLL, reuse, manifest/DLL/extra-file tampering, cancellation.");
    Console.WriteLine("PASS inventory: empty/untrusted/restart/verified/missing, refresh tampering, path-free, zero activation/network.");

    foreach (var key in new[] { "account.session", "service.token" })
    {
        var oldStore = new MemoryStore();
        var newStore = new MemoryStore();
        oldStore.Values[key] = [1, 2, 3]; // Synthetic bytes only.
        var adapter = new DeclaredPlatformCredentialStore(newStore,
            [new PlatformCredentialAlias(key, "fixture.old", key)], _ => oldStore);
        Check(oldStore.Reads == 0 && newStore.Reads == 0, "No eager credential reads/migration");
        using (var legacy = await adapter.GetAsync(key, default))
            Check(legacy is not null && legacy.Secret.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "Legacy key preserved");
        await adapter.SetAsync(key, new byte[] { 4 }, null, default);
        Check(oldStore.Values[key][0] == 4 && newStore.Values.Count == 0, "Refresh updates original key");
        await adapter.SetAsync("pending.challenge", new byte[] { 5 }, null, default);
        Check(newStore.Values.ContainsKey("pending.challenge") && !oldStore.Values.ContainsKey("pending.challenge"), "Temporary challenges remain isolated");
        await adapter.DeleteAsync(key, default);
        Check(!oldStore.Values.ContainsKey(key), "Sign-out clears the same legacy address");
    }
    var deniedLegacy = new MemoryStore();
    deniedLegacy.Values["account.session"] = [7];
    var stranger = new DeclaredPlatformCredentialStore(new MemoryStore(), [], _ => deniedLegacy);
    Check(await stranger.GetAsync("account.session", default) is null && deniedLegacy.Reads == 0, "No wildcard credential alias");
    Console.WriteLine("PASS compatibility: stable account aliases, refresh, sign-out, pending isolation, unknown plugin denied.");
    var revokeInner = new MemoryStore();
    var revocable = new RevocablePlatformCredentialStore(revokeInner);
    await revocable.SetAsync("session", new byte[] { 1 }, null, default);
    await revocable.RevokeAsync(default);
    await revocable.DeleteAsync("session", default);
    Check(await revocable.GetAsync("session", default) is null && !revokeInner.Values.ContainsKey("session"), "Disable clears stored credentials");
    try { await revocable.SetAsync("session", new byte[] { 2 }, null, default); throw new Exception("Late write accepted"); }
    catch (InvalidOperationException) { }
    Check(!revokeInner.Values.ContainsKey("session"), "Late login cannot resurrect cookie");
    await using (var revokedHost = NewHost(root, services))
    {
        await revokedHost.DiscoverAsync();
        revokedHost.DisablePlugin("tests.approved");
        Check(!(await Lookup(revokedHost)).IsSuccess, "Disabled plugin cannot route capabilities");
    }
    Console.WriteLine("PASS revocation: deleted session, denied late write, disabled capability route.");
    await PluginManagerTests.RunAsync(Path.Combine(root, "management"));
    await CredentialAliasTests.RunAsync(Path.Combine(root, "credential-aliases"));
    await HostCompatibilityTests.RunAsync(Path.Combine(root, "host-compatibility"));
    await CommentArtworkPolicyTests.RunAsync(Path.Combine(root, "comment-artwork"));
    await CoordinatorRoutingTests.RunAsync(Path.Combine(root, "native-routing"));
    await LyricsCapabilityRoutingTests.RunAsync(Path.Combine(root, "lyrics-routing"));
    await RuntimeManifestUpgradeTests.RunAsync(Path.Combine(root, "runtime-upgrade"));
    await DisableRevisionTests.RunAsync(Path.Combine(root, "disable-revision"));
}
catch (Exception error)
{
    // These are isolated developer fixtures. Return failure normally instead of leaving an unhandled
    // exception in a Windows crash dialog that holds the test apphost open and blocks the next build.
    Console.Error.WriteLine($"FAIL platform host fixtures: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
    Environment.ExitCode = 1;
}
finally
{
    // The exact randomly created test folder only; never touches real plugins, vaults, or library data.
    if (Path.GetDirectoryName(root) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(root).StartsWith("Auralis-plugin-tests-", StringComparison.Ordinal))
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("INFO: memory-mapped synthetic fixture retained in the temporary test directory until process exit."); }
    }
}
