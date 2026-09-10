using System.IO.Compression;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class DisableRevisionTests
{
    internal static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        foreach (var scenario in new[] { "discovered", "activated", "tampered-active", "tampered-unactivated" })
        {
            var activated = scenario is "activated" or "tampered-active";
            var tampered = scenario.StartsWith("tampered", StringComparison.Ordinal);
            var test = Path.Combine(root, scenario);
            Directory.CreateDirectory(test);
            async Task<string> Package(bool next)
            {
                var folder = Path.Combine(test, next ? "new" : "old"); Directory.CreateDirectory(folder);
                File.Copy(typeof(RevisionOldPlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
                await File.WriteAllTextAsync(Path.Combine(folder, "platform.plugin.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 5,
                    id = "tests.revision",
                    displayName = "Revision",
                    version = next ? "2.0.0" : "1.0.0",
                    minimumHostApiVersion = 1,
                    maximumHostApiVersion = 1,
                    entryAssembly = "Fixture.dll",
                    entryType = next ? typeof(RevisionNewPlugin).FullName : typeof(RevisionOldPlugin).FullName,
                    hostRequirements = new { minimumHostSdkVersion = "1.2.0", requiredFeatures = new[] { "native-login.v1", "credential-aliases.v1", "comment-artwork.v1" } },
                    credentialAliases = new[] { new { key = "session", scope = next ? "fixture.new" : "fixture.old", legacyKey = "account" } },
                    providers = new[] { new { id = next ? "revision.new" : "revision.old", displayName = "Revision", capabilities = new[] { "NativeLogin" }, commentArtworkDomains = Array.Empty<string>() } }
                }));
                var zip = folder + ".auralis-plugin"; ZipFile.CreateFromDirectory(folder, zip); return zip;
            }
            var manager = new PlatformPluginManager(Path.Combine(test, "managed"), []);
            async Task Install(bool next)
            {
                var preview = await manager.PrepareImportAsync(await Package(next));
                await manager.ConfirmImportAsync(preview.Token, true);
            }
            await Install(false);
            await manager.SetEnabledAsync("tests.revision", true);
            var stores = new Dictionary<string, MemoryStore>();
            IPlatformCredentialStore Store(string scope)
            {
                if (!stores.TryGetValue(scope, out var value)) stores[scope] = value = new MemoryStore();
                return value;
            }
            ((MemoryStore)Store("fixture.old")).Values["account"] = [1];
            ((MemoryStore)Store("fixture.new")).Values["account"] = [2];
            var own = (MemoryStore)Store("tests.revision");
            own.Values["old-profile"] = [3]; own.Values["new-profile"] = [4];
            var settings = new PlatformSettingsStore(Path.Combine(test, "settings.json"));
            await using var backend = new PlatformBackendService([], NullAppLogger.Instance, manager, settings, Store);
            Check((await backend.DiscoverAsync()).Providers.Single().Provider.Id == "revision.old", "Old revision discovered");
            IPlatformNativeLoginSession? session = null;
            if (activated)
            {
                var login = await backend.Router.RouteAsync<IPlatformNativeLoginCapability, IPlatformNativeLoginSession>("revision.old",
                    (provider, token) => provider.CreateLoginSessionAsync(token));
                Check(login.IsSuccess, "Old revision activated with synthetic session"); session = login.Value;
            }
            await Install(true);
            Check((await manager.FindManagedPluginAsync("tests.revision", default))!.Version.Major == 2, "Pending revision is new");
            Check((await backend.DiscoverAsync()).Providers.Single().Provider.Id == "revision.old", "Running plan remains old");
            var disabling = (await backend.FindPluginForDisableAsync("tests.revision", default))!;
            Check(disabling.Providers.Single().Id == "revision.old", "Window closure uses running provider IDs");
            if (tampered) await File.AppendAllTextAsync(disabling.ManifestPath, "\n "); // Synthetic immutable-revision integrity failure.
            Check(await backend.DisableAndClearAsync("tests.revision", 0, default) == !tampered, "Cleanup reports damaged code without executing it");
            Check(stores["fixture.old"].Values.ContainsKey("account") == (tampered && !activated), "Only approved running grants are cleared, never guessed after damage");
            Check(stores["fixture.new"].Values.ContainsKey("account"), "Pending update account remains untouched");
            Check(own.Values.ContainsKey("old-profile") == tampered && own.Values.ContainsKey("new-profile"), "Native cleanup runs only trusted old provider");
            Check(!(await manager.CreateSessionPlanAsync()).EnabledIds.Contains("tests.revision"), "OFF persists across restart");
            var after = await backend.Router.RouteAsync<IPlatformNativeLoginCapability, IPlatformNativeLoginSession>("revision.old",
                (provider, token) => provider.CreateLoginSessionAsync(token));
            Check(!after.IsSuccess, "Disabled provider cannot route");
            if (session is not null)
            {
                var rejected = false;
                try { session.Show(0, false); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && !stores["fixture.old"].Values.ContainsKey("account"), "Late login cannot restore revoked credentials");
            }
        }
        var creations = 0;
        using (var factory = new DefaultPlatformHostContextFactory(new PlatformSettingsStore(Path.Combine(root, "late-context.json")),
            NullAppLogger.Instance, _ => { creations++; return new MemoryStore(); }))
        {
            Check(!await factory.RevokeExistingCredentialsAsync("tests.late", default), "Unactivated revocation creates no scope");
            var rejected = false;
            try { factory.CreateContext("tests.late"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && creations == 0, "Activation racing after disable cannot create a fresh credential scope");
        }
        Console.WriteLine($"PASS {checks} revision-aware disable checks: real immutable import/update, pinned session cleanup, synthetic vaults, no HTTP or native windows.");
    }
}

public sealed class RevisionOldPlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor { get; } = new("tests.revision", "Revision", new(1, 0, 0), 1, 1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new RevisionProvider(context, false)]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class RevisionNewPlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor { get; } = new("tests.revision", "Revision", new(2, 0, 0), 1, 1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new RevisionProvider(context, true)]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class RevisionProvider(PlatformHostContext context, bool next) : IPlatformProvider, IPlatformNativeLoginCapability
{
    public PlatformProviderDescriptor Descriptor { get; } = new(next ? "revision.new" : "revision.old", "Revision", new(1, 0, 0), [PlatformCapabilityKind.NativeLogin]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<PlatformResult<IPlatformNativeLoginSession>> CreateLoginSessionAsync(CancellationToken token) => Task.FromResult(PlatformResult<IPlatformNativeLoginSession>.Success(new RevisionLoginSession(context.CredentialStore)));
    public async Task<PlatformResult<PlatformUnit>> ClearLoginDataAsync(nint ownerWindow, CancellationToken token)
    {
        await context.CredentialStore.DeleteAsync(next ? "new-profile" : "old-profile", token);
        return PlatformResult<PlatformUnit>.Success(PlatformUnit.Value);
    }
}
public sealed class RevisionLoginSession(IPlatformCredentialStore store) : IPlatformNativeLoginSession
{
    public event EventHandler? Authenticated { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    // A deliberately late synthetic authentication completion, never an actual native window.
    public void Show(nint ownerWindow, bool dark) => store.SetAsync("session", new byte[] { 9 }, null, default).AsTask().GetAwaiter().GetResult();
    public void Activate() { }
    public void Close() { }
}
