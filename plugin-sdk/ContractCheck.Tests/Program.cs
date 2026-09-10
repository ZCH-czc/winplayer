using System.Diagnostics;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;
using Auralis.PluginContractCheck;
using Auralis.PluginContractCheck.Tests;

var assertions = 0;
void Check(bool condition, string label) { assertions++; if (!condition) throw new InvalidOperationException(label); }
var temp = Path.Combine(Path.GetTempPath(), "Auralis-contract-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Check(PackageProbe.CoversAllCapabilities, "Every capability has an interface probe");
    Check(ContractChecks.Result(default(PlatformResult<string>)) == Finding.InvalidResult, "Default result rejected");
    Check(ContractChecks.Result(PlatformResult<string>.Failure((PlatformErrorCode)999, "hidden")) == Finding.InvalidErrorCode, "Unknown error rejected");
    Check(ContractChecks.Result(PlatformResult<string>.Failure(PlatformErrorCode.RateLimited, "hidden", retryAfter: TimeSpan.FromSeconds(-1))) == Finding.InvalidRetryAfter, "Negative retry rejected");
    var provider = new Provider();
    Check(await ContractChecks.OperationAsync(t => provider.LookupAsync(new("title", "artist", "album", 30), t)) == Finding.Passed, "Real fixture method success");
    Check(await ContractChecks.OperationAsync(t => provider.LookupAsync(new("missing", "artist", "album", 30), t), PlatformErrorCode.NotFound) == Finding.Passed, "Expected typed failure");
    Check(await ContractChecks.CancellationAsync(t => provider.LookupAsync(new("title", "artist", "album", 30), t)) == Finding.Passed, "Direct provider cancellation");
    Check(await ContractChecks.CancellationAsync(_ => Task.FromResult(PlatformResult<int>.Success(1))) == Finding.CancellationIgnored, "Ignoring cancellation rejected");
    Check(await ContractChecks.CancellationAsync<int>(async t => { await Task.Delay(Timeout.Infinite, t); return PlatformResult<int>.Success(1); },
        cancelAfter: TimeSpan.FromMilliseconds(20)) == Finding.Passed, "Mid-operation cancellation acknowledged");
    Check(await ContractChecks.OperationAsync<int>(_ => throw new Exception("DO_NOT_REPORT_COOKIE_SECRET")) == Finding.ThrewException, "Throw classified");
    Check(await ContractChecks.OperationAsync(async t => { await Task.Delay(10000, t); return PlatformResult<int>.Success(1); }, timeout: TimeSpan.FromMilliseconds(20)) == Finding.TimedOut, "Bounded operation");
    using var clockFactory = new OfflineContextFactory();
    var a = clockFactory.CreateContext("test.a"); var b = clockFactory.CreateContext("test.b");
    await a.CredentialStore.SetAsync("session", new byte[] { 1, 2 }, null, default);
    using var credential = await a.CredentialStore.GetAsync("session", default);
    Check(credential?.Secret.Span[0] == 1 && await b.CredentialStore.GetAsync("session", default) is null, "Memory credentials isolated");
    await a.CredentialStore.SetAsync("expired", new byte[] { 9 }, DateTimeOffset.UtcNow.AddSeconds(-1), default);
    Check(await a.CredentialStore.GetAsync("expired", default) is null, "Expired synthetic credentials not returned");
    var future = DateTimeOffset.UtcNow.AddMinutes(1);
    await a.CredentialStore.SetAsync("future", new byte[] { 1 }, future, default);
    using var futureCredential = await a.CredentialStore.GetAsync("future", default);
    Check(futureCredential?.ExpiresAt == future, "Credential expiry retained");
    await a.Cache.SetAsync("session", new byte[] { 3 }, future, default);
    Check((await a.Cache.GetAsync("session", default))?.ExpiresAt == future && (await a.CredentialStore.GetAsync("session", default))?.Secret.Span[0] == 1,
        "Cache expiry and credential/cache namespace isolation");
    var clock = new ManualClock();
    var audio = new PlatformStreamLease(new("https://example.invalid/media?token=DO_NOT_REPORT"), clock.Now.AddSeconds(10), "audio/aac", new("auto", "Auto", 128), new Dictionary<string,string> { ["Authorization"] = "DO_NOT_REPORT" });
    Check(ContractChecks.AudioLease(audio, clock) == Finding.Passed, "Future audio lease");
    clock.Now = clock.Now.AddSeconds(10);
    Check(ContractChecks.AudioLease(audio, clock) == Finding.ExpiredLease && audio.IsExpired(clock), "Expiry boundary exact");
    var renewed = new PlatformStreamLease(audio.Url, clock.Now.AddSeconds(10), audio.MimeType, audio.Quality);
    Check(ContractChecks.AudioLease(renewed, clock) == Finding.Passed, "Renewal is a fresh lease");
    var video = new PlatformVideoLease(audio.Url, clock.Now.AddSeconds(10), "video/mp4") { AudioStream = audio };
    Check(ContractChecks.VideoLease(video, clock) == Finding.ExpiredLease, "Independent audio expiry checked");
    Check(ContractChecks.VideoLease(video with { AudioStream = renewed, AlternateUrls = [new("file:///secret")] }, clock) == Finding.InvalidVideoAlternates, "Alternate URL checked");
    Check(ContractChecks.AudioLease(new(new("http://example.invalid/media"), clock.Now.AddMinutes(1), null, audio.Quality), clock) == Finding.InvalidLeaseUrl, "Non-loopback HTTP rejected");
    Check(ContractChecks.AudioLease(new(audio.Url, null, null, audio.Quality), clock) == Finding.MissingExpiry, "Unknown expiry not called passed");
    Check(ContractChecks.AudioLease(new(audio.Url, clock.Now.AddSeconds(10), null, audio.Quality, new Dictionary<string,string>{["X-Test"]="bad\r\nheader"}), clock) == Finding.InvalidHeader, "Header injection rejected");
    Check(ContractChecks.TrackPage(new([], totalCount: -1), "fixture", 20) == Finding.InvalidPage, "Invalid page totals");
    var foreign = new PlatformTrack { Id = new("other", "track"), Title = "Synthetic" };
    Check(ContractChecks.TrackPage(new([foreign]), "fixture", 20) == Finding.WrongProvider, "Cross-provider entity rejected");
    Check(ContractChecks.TrackPage(new([foreign, foreign]), "other", 1) == Finding.InvalidPage, "Page size respected");
    using var httpFixture = new OfflineContextFactory((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)));
    using var client = httpFixture.CreateContext("test.http").HttpClientFactory.CreateClient("fixture");
    using var response = await client.GetAsync("https://example.invalid");
    Check(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && httpFixture.HttpRequests == 1, "HTTP fixture never needs network");

    foreach (var (type, expected, diagnostic) in new[] {
        (typeof(GoodPlugin), 0, ""), (typeof(WrongDescriptorPlugin), 1, "PluginDescriptorMismatch"),
        (typeof(MissingInterfacePlugin), 1, "CapabilityMismatch"), (typeof(NetworkPlugin), 1, "UnexpectedNetworkDuringActivation"),
        (typeof(ThrowPlugin), 1, "PluginConstructionFailed"), (typeof(HangPlugin), 3, "WorkerTimedOut"),
        (typeof(HangDisposePlugin), 3, "WorkerTimedOut") })
    {
        var folder = Path.Combine(temp, type.Name); Directory.CreateDirectory(folder);
        File.Copy(typeof(GoodPlugin).Assembly.Location, Path.Combine(folder, "Fixture.dll"));
        await File.WriteAllTextAsync(Path.Combine(folder, "platform.plugin.json"), JsonSerializer.Serialize(new {
            schemaVersion = 5, id = "tests.contract", displayName = "Contract fixture", version = "1.0.0",
            minimumHostApiVersion = 1, maximumHostApiVersion = 1,
            hostRequirements = new PlatformHostRequirements { MinimumHostSdkVersion = "1.2.0", RequiredFeatures = ["lyrics-lookup.v1", "comment-artwork.v1"] },
            entryAssembly = "Fixture.dll", entryType = type.FullName,
            providers = new[] { new { id = "fixture", displayName = "Fixture", capabilities = new[] { "LyricsLookup" }, commentArtworkDomains = Array.Empty<string>() } }
        }));
        var inspected = await Cli("--inspect", folder);
        Check(inspected.Code == 0 && !inspected.Report.Executed, "Inspect inert even for throwing/hanging constructors");
        Check(inspected.Report.LegacyCommentArtworkProviders == 0 && inspected.Report.DeclaredCommentArtworkProviders == 1 &&
            inspected.Report.RuntimeManifestCompatible && inspected.Report.MinimumRuntimeManifestVersion == 5,
            "Static report separates runtime manifest compatibility from unexecuted interfaces");
        var denied = await Cli("--verify", folder);
        Check(denied.Code == 2, "No implicit execution approval");
        var verified = await Cli("--verify", folder, "--trust-plugin-code", "--timeout-seconds", "3");
        Check(verified.Code == expected, type.Name + " exit code: " + verified.Code);
        Check(diagnostic.Length == 0 ? verified.Report.InterfaceChecks == 1 && verified.Report.Passed : verified.Report.Diagnostics.Contains(diagnostic), type.Name + " diagnostic");
        Check(verified.Report.NotTested.Contains("BusinessOperations") || verified.Report.NotTested.Contains("NotCompleted"), "Report admits untested behavior");
        if (type == typeof(GoodPlugin))
        {
            var manifestPath = Path.Combine(folder, "platform.plugin.json");
            var next = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            next["schemaVersion"] = 5;
            next["hostRequirements"]!["minimumHostSdkVersion"] = "1.2.0";
            next["providers"]![0]!["commentArtworkDomains"] = new System.Text.Json.Nodes.JsonArray("cdn.example.test");
            await File.WriteAllTextAsync(manifestPath, next.ToJsonString());
            var declared = await Cli("--inspect", folder);
            Check(declared.Code == 0 && !declared.Report.Executed && declared.Report.DeclaredCommentArtworkProviders == 1 &&
                declared.Report.LegacyCommentArtworkProviders == 0, "Schema 5 self-check reports explicit policy");
            var executed = await Cli("--verify", folder, "--trust-plugin-code");
            Check(executed.Code == 0 && executed.Report.Executed && executed.Report.DeclaredCommentArtworkProviders == 1,
                "Trusted worker returns validated policy counts");
            next["providers"]![0]!["commentArtworkDomains"] = new System.Text.Json.Nodes.JsonArray("*");
            await File.WriteAllTextAsync(manifestPath, next.ToJsonString());
            var invalid = await Cli("--verify", folder, "--trust-plugin-code");
            Check(invalid.Code == 1 && !invalid.Report.Executed && invalid.Report.Diagnostics.Contains("ManifestInvalid"),
                "Invalid domain rejected by preflight before trusted worker execution");
            next["providers"]![0]!.AsObject().Remove("commentArtworkDomains");
            for (var schema = 4; schema >= 1; schema--)
            {
                next["schemaVersion"] = schema;
                if (schema < 4) next.AsObject().Remove("hostRequirements");
                // A constructor which hangs proves old-package checks do not activate the DLL.
                next["entryType"] = typeof(HangPlugin).FullName;
                await File.WriteAllTextAsync(manifestPath, next.ToJsonString());
                var bytes = await File.ReadAllBytesAsync(manifestPath);
                var legacy = await Cli("--inspect", folder);
                Check(legacy.Code == 0 && legacy.Report.Passed && !legacy.Report.Executed &&
                    !legacy.Report.RuntimeManifestCompatible && legacy.Report.UpgradeRequiredPlugins == 1 &&
                    legacy.Report.LegacyCommentArtworkProviders == 1, "Old metadata can be inspected but is not runtime-compatible");
                var blocked = await Cli("--verify", folder, "--trust-plugin-code", "--timeout-seconds", "1");
                Check(blocked.Code == 1 && !blocked.Report.Executed && blocked.Report.InterfaceChecks == 0 &&
                    blocked.Report.Diagnostics.Contains("ManifestUpgradeRequired"), "Upgrade required before worker activation");
                var direct = await PackageProbe.VerifyTrustedAsync(folder);
                Check(!direct.Executed && !direct.Passed && direct.Diagnostics.Contains("ManifestUpgradeRequired"),
                    "Direct worker API cannot bypass runtime schema preflight");
                Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)), "Self-check never rewrites old metadata");
            }
        }
    }
    var absent = await Cli("--inspect", Path.Combine(temp, "missing"));
    Check(absent.Code == 1 && absent.Report.Diagnostics.Contains("DirectoryMissing"), "Missing folder is not a successful empty check");
    Console.WriteLine($"PASS contract checker: {assertions} assertions; manifest-only, trusted real DLL/interface mismatch, constructor/disposal timeout, safe reports, offline fixtures, cancellation, results and lease expiry. No real platform playback tested.");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine($"FAIL contract checker: {e.Message}"); return 1; }
finally { Directory.Delete(temp, recursive: true); } // This invocation's generated fixtures only.

static async Task<(int Code, ProbeReport Report)> Cli(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
    start.ArgumentList.Add(typeof(PackageProbe).Assembly.Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
    catch { if (!process.HasExited) process.Kill(true); throw; }
    var output = await outputTask; var error = await errorTask;
    if (output.Contains("DO_NOT_REPORT") || error.Contains("DO_NOT_REPORT")) throw new InvalidOperationException("Output must not contain fixture secrets");
    return (process.ExitCode, JsonSerializer.Deserialize<ProbeReport>(output) ?? throw new InvalidOperationException("Missing JSON report"));
}
internal sealed class ManualClock : IPlatformTimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset GetUtcNow() => Now;
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Now += delay; return ValueTask.CompletedTask; }
}
