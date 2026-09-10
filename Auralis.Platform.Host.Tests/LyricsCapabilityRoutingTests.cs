using System.Security.Cryptography;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class LyricsCapabilityRoutingTests
{
    internal static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var request = new PlatformLyricsLookupRequest("first", "Artist", "Album", 90);
        await using (var missing = new PlatformBackendService([Path.Combine(root, "absent")], NullAppLogger.Instance,
                         settings: new PlatformSettingsStore(Path.Combine(root, "missing-settings.json"))))
        {
            Check(await missing.LookupLyricsAsync(request, default) is null, "Missing plugins cannot block local lyrics");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await missing.LookupLyricsAsync(request, cancelled.Token); throw new InvalidOperationException("Cancellation swallowed"); }
            catch (OperationCanceledException) { checks++; }
        }
        var folder = Path.Combine(root, "tests.lyrics-routing");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(LyricsRoutingPlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
        await File.WriteAllTextAsync(Path.Combine(folder, "platform.plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 5, id = "tests.lyrics-routing", displayName = "Lookup fixture", version = "1.0.0",
            minimumHostApiVersion = 1, maximumHostApiVersion = 1, entryAssembly = "Fixture.dll",
            entryType = typeof(LyricsRoutingPlugin).FullName,
            hostRequirements = new PlatformHostRequirements { MinimumHostSdkVersion = PlatformHostCompatibility.SdkVersion,
                RequiredFeatures = ["comment-artwork.v1", "settings.v1", "lyrics-lookup.v1"] },
            providers = new[] { "lookup.a-configured", "lookup.b-fallback", "other.search" }.Select(id => new
            {
                id, displayName = id, commentArtworkDomains = Array.Empty<string>(),
                capabilities = new[] { id == "other.search" ? "TrackSearch" : "LyricsLookup" },
                settings = id == "lookup.a-configured" ? new[] { new PlatformSettingManifest
                    { Key = "endpoint", Label = "Endpoint", Kind = "endpoint", Required = true } } : []
            })
        }));
        Directory.CreateDirectory(Path.Combine(root, ".approvals"));
        var hashes = Directory.GetFiles(folder).ToDictionary(path => Path.GetFileName(path)!,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await File.WriteAllTextAsync(Path.Combine(root, ".approvals", "tests.lyrics-routing.json"),
            JsonSerializer.Serialize(new PluginInstallReceipt(2, "tests.lyrics-routing", hashes!, [])));
        var settings = new PlatformSettingsStore(Path.Combine(root, "settings.json"));
        var manager = new PlatformPluginManager(Path.Combine(root, "managed"), [root]);
        await manager.SetEnabledAsync("tests.lyrics-routing", true);
        await using var backend = new PlatformBackendService([root], NullAppLogger.Instance, manager, settings);
        Check((await backend.DiscoverAsync()).Providers.Count == 3, "Actual approved fixture discovered");
        Check((await backend.LookupLyricsAsync(request, default))?.Source == "lookup.b-fallback", "Arbitrary capability discovered; unconfigured candidate skipped");
        await backend.SaveSettingAsync("lookup.a-configured", "endpoint", "https://example.test/", default);
        Check((await backend.LookupLyricsAsync(request, default))?.Source == "lookup.a-configured", "Declared setting activates first candidate without hard-coded IDs");
        foreach (var title in new[] { "not-found", "empty", "throws" })
            Check((await backend.LookupLyricsAsync(request with { Title = title }, default))?.Source == "lookup.b-fallback", "Failed/empty candidate falls through: " + title);
        Check((await backend.LookupLyricsAsync(request with { Title = "instrumental" }, default)) is { Instrumental: true, Source: "lookup.a-configured" }, "Instrumental is a successful match without text");
        Check(await backend.LookupLyricsAsync(request with { Title = "route-timeout" }, default) is null,
            "A routed timeout must not fall through before the overall budget timer fires");
        Check(await backend.LookupLyricsAsync(request with { Title = "hang" }, default, TimeSpan.FromMilliseconds(100)) is null, "Entire lookup budget is bounded");
        using (var cancelled = new CancellationTokenSource(100))
        {
            try { await backend.LookupLyricsAsync(request with { Title = "hang" }, cancelled.Token); throw new InvalidOperationException("Cancellation swallowed"); }
            catch (OperationCanceledException) { checks++; }
        }
        await backend.SaveSettingAsync("lookup.a-configured", "endpoint", "", default);
        Check((await backend.LookupLyricsAsync(request, default))?.Source == "lookup.b-fallback", "Cleared required endpoint does not invoke provider");
        await backend.SaveSettingAsync("lookup.a-configured", "endpoint", "https://example.test/", default);
        var pending = backend.LookupLyricsAsync(request with { Title = "delayed" }, default);
        var started = false;
        for (var i = 0; i < 100; i++)
        {
            var probe = await backend.Router.RouteAsync<IPlatformLyricsLookupCapability, PlatformLyricsLookupResult>("lookup.a-configured",
                (capability, token) => capability.LookupAsync(request with { Title = "probe" }, token));
            if (probe.IsSuccess && probe.Value.Text == "started") { started = true; break; }
            await Task.Delay(10);
        }
        Check(started, "Observed in-flight provider before disable");
        Check(await backend.DisableAndClearAsync("tests.lyrics-routing", 0, default), "Synthetic plugin disabled");
        Check(await pending is null, "Late result from disabled plugin is not delivered");
        Check(await backend.LookupLyricsAsync(request, default) is null, "Disabled providers cannot be selected again");
        Check((await File.ReadAllTextAsync(Path.Combine(root, "settings.json"))).Contains("example.test"), "Disabling leaves non-secret user settings intact");
        Console.WriteLine($"PASS {checks} lyrics capability routing checks: arbitrary IDs, fallback, declared settings, one budget, cancellation, missing/disabled, late result. No HTTP or real accounts.");
    }
}

public sealed class LyricsRoutingPlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor { get; } = new("tests.lyrics-routing", "Lookup fixture", new Version(1, 0, 0), 1, 1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success(
            [new LyricsRoutingProvider("lookup.a-configured"), new LyricsRoutingProvider("lookup.b-fallback"), new LyricsRoutingSearchOnly()]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class LyricsRoutingProvider(string id) : IPlatformProvider, IPlatformLyricsLookupCapability
{
    private int _started;
    public PlatformProviderDescriptor Descriptor { get; } = new(id, id, new Version(1, 0, 0), [PlatformCapabilityKind.LyricsLookup]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public async Task<PlatformResult<PlatformLyricsLookupResult>> LookupAsync(PlatformLyricsLookupRequest request, CancellationToken token)
    {
        if (id == "lookup.a-configured")
        {
            if (request.Title == "probe") return PlatformResult<PlatformLyricsLookupResult>.Success(new(id, Volatile.Read(ref _started) != 0 ? "started" : "idle"));
            if (request.Title == "not-found") return PlatformResult<PlatformLyricsLookupResult>.Failure(PlatformErrorCode.NotFound, "Fixture miss");
            if (request.Title == "empty") return PlatformResult<PlatformLyricsLookupResult>.Success(new(id, " "));
            if (request.Title == "throws") throw new InvalidOperationException("Fixture failure");
            if (request.Title == "instrumental") return PlatformResult<PlatformLyricsLookupResult>.Success(new(id, "", true));
            // Deterministically reproduce the router winning the two-timer race. The outer
            // budget is still live, but a timeout must not start another lookup candidate.
            if (request.Title == "route-timeout") return PlatformResult<PlatformLyricsLookupResult>.Failure(PlatformErrorCode.Timeout, "Fixture routed timeout");
            if (request.Title == "hang") await Task.Delay(Timeout.Infinite, token);
            if (request.Title == "delayed")
            {
                Volatile.Write(ref _started, 1);
                await Task.Delay(300); // Deliberate non-cooperation; late bytes must not be accepted.
            }
        }
        return PlatformResult<PlatformLyricsLookupResult>.Success(new(id, "[00:01]" + request.Title));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class LyricsRoutingSearchOnly : IPlatformProvider, ITrackSearchCapability
{
    public PlatformProviderDescriptor Descriptor { get; } = new("other.search", "Other", new Version(1, 0, 0), [PlatformCapabilityKind.TrackSearch]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => throw new InvalidOperationException("Unrelated provider must not initialize");
    public Task<PlatformResult<PlatformPage<PlatformTrack>>> SearchTracksAsync(PlatformSearchRequest request, CancellationToken token) => throw new InvalidOperationException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
