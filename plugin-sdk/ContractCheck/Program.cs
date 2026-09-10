using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Auralis.PluginContractCheck;

// JSON stdout is for automation. Never forward raw plugin stdout/stderr or exception text.
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Auralis plugin contract check (developer tool)\n" +
        "--inspect <unpacked-plugin-root>\n" +
        "--verify <unpacked-plugin-root> --trust-plugin-code [--timeout-seconds 1..120]\n" +
        "Inspect reads manifests only. Verify EXECUTES TRUSTED CODE in a disposable process.\n" +
        "Host HTTP is denied; this is NOT an OS security sandbox. Do not run untrusted DLLs.\n" +
        "It does not install plugins, read Auralis accounts, log in, search or play media.\n" +
        "Exit: 0 pass, 1 failure, 2 arguments/trust missing, 3 timeout, 4 cancelled.");
    return args.Length == 0 ? 2 : 0;
}
var worker = args.Length == 3 && args[0] == "--trusted-worker" && args[2] == "--trust-plugin-code";
if (worker)
{
    var output = Console.Out;
    Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
    try
    {
        var report = await PackageProbe.VerifyTrustedAsync(Path.GetFullPath(args[1]));
        await output.WriteLineAsync(JsonSerializer.Serialize(report));
        return report.Passed ? 0 : 1;
    }
    catch { await output.WriteLineAsync(JsonSerializer.Serialize(Failure("WorkerFailed", true))); return 1; }
}
var inspect = args.Length == 2 && args[0] == "--inspect";
var verify = (args.Length == 3 || args.Length == 5) && args[0] == "--verify" && args[2] == "--trust-plugin-code";
var seconds = 30;
if ((!inspect && !verify) || (args.Length == 5 && (args[3] != "--timeout-seconds" || !int.TryParse(args[4], out seconds) || seconds is < 1 or > 120)))
{
    Console.WriteLine(JsonSerializer.Serialize(Failure("InvalidArgumentsOrTrustMissing", false))); return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root)) { Console.WriteLine(JsonSerializer.Serialize(Failure("DirectoryMissing", false))); return 1; }
    var manifest = await PackageProbe.InspectAsync(root, cancel.Token);
    if (inspect || !manifest.Passed) { Console.WriteLine(JsonSerializer.Serialize(manifest)); return manifest.Passed ? 0 : 1; }
    if (!manifest.RuntimeManifestCompatible)
    {
        Console.WriteLine(JsonSerializer.Serialize(PackageProbe.RequireUpgrade(manifest))); return 1;
    }
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--trusted-worker"); info.ArgumentList.Add(root); info.ArgumentList.Add("--trust-plugin-code");
    using var child = Process.Start(info) ?? throw new InvalidOperationException();
    var stdout = ReadBoundedAsync(child.StandardOutput, 256 * 1024);
    var stderr = ReadBoundedAsync(child.StandardError, 0);
    try { await child.WaitForExitAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(seconds), cancel.Token); }
    catch (Exception e) when (e is TimeoutException or OperationCanceledException)
    {
        // This exact Process object was created above by this checker, never the user's player.
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine(JsonSerializer.Serialize(Failure(e is TimeoutException ? "WorkerTimedOut" : "Cancelled", true)));
        return e is TimeoutException ? 3 : 4;
    }
    var captured = await stdout.WaitAsync(TimeSpan.FromSeconds(2));
    await stderr.WaitAsync(TimeSpan.FromSeconds(2));
    ProbeReport? result = null;
    // Only deserialize the fixed report shape and allowlisted diagnostics. Native output is discarded.
    foreach (var line in captured.Split('\n').Reverse())
    {
        try { result = JsonSerializer.Deserialize<ProbeReport>(line); } catch (JsonException) { continue; }
        if (result is not null && Safe(result)) break;
        result = null;
    }
    result ??= Failure("WorkerReportInvalid", true);
    if (child.ExitCode != 0) result = result with { Passed = false };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Passed ? 0 : 1;
}
catch (OperationCanceledException) { Console.WriteLine(JsonSerializer.Serialize(Failure("Cancelled", false))); return 4; }
catch { Console.WriteLine(JsonSerializer.Serialize(Failure("CheckFailed", false))); return 1; }

static ProbeReport Failure(string code, bool executed) => new(false, executed, 0, 0, 0, 0, [code], ["NotCompleted"]);
static bool Safe(ProbeReport r)
{
    var allowed = Enum.GetNames<Auralis.Platform.Host.PlatformPluginDiagnosticCode>().Concat(
        ["UnknownContract", "InterfaceRouteFailed", "NoPlugins", "UnexpectedNetworkDuringActivation", "WorkerFailed"]);
    var gaps = new[] { "RuntimeDescriptors", "BusinessOperations", "CancellationCompliance", "LeaseLifecycle", "RealAccounts", "Playback", "NativeUI", "NotCompleted" };
    return (r.Executed || (!r.Passed && r.InterfaceChecks == 0 && r.HttpRequests == 0)) &&
        r.Plugins is >= 0 and <= 1024 && r.Providers is >= 0 and <= 32768 && r.InterfaceChecks is >= 0 and <= 1000000 &&
        r.UpgradeRequiredPlugins >= 0 && r.UpgradeRequiredPlugins <= r.Plugins &&
        r.LegacyCommentArtworkProviders >= 0 && r.DeclaredCommentArtworkProviders >= 0 &&
        (long)r.LegacyCommentArtworkProviders + r.DeclaredCommentArtworkProviders == r.Providers &&
        r.HttpRequests >= 0 && r.Diagnostics is { Length: <= 128 } && r.Diagnostics.All(allowed.Contains) &&
        r.NotTested is { Length: <= 16 } && r.NotTested.All(gaps.Contains) && (!r.Passed || (r.Diagnostics.Length == 0 && r.Plugins > 0));
}
static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum)
{
    var result = new StringBuilder();
    var buffer = new char[4096];
    int read;
    while ((read = await reader.ReadAsync(buffer)) != 0)
        if (result.Length < maximum) result.Append(buffer, 0, Math.Min(read, maximum - result.Length));
    return result.ToString();
}
