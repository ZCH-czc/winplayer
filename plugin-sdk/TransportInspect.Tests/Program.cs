using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-cli-" + Guid.NewGuid().ToString("N"));
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TransportInspect/bin", configuration,
    "net8.0/Auralis.TransportInspect.dll"));
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
Directory.CreateDirectory(root);
try
{
    var bytes = new byte[] { 1, 2, 3 }; // Intentionally not an executable DLL.
    var manifest = new JsonObject
    {
        ["schemaVersion"] = 1, ["kind"] = "mediaTransport", ["id"] = "fixture.transport",
        ["displayName"] = "Untrusted label", ["version"] = "1.0.0", ["contractApiVersion"] = 1,
        ["minimumHostVersion"] = "0.1.0", ["runtimeIdentifier"] = "win-x64",
        ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Factory",
        ["capabilities"] = new JsonArray("authorizedHttp", "completeBuffering", "prefetch", "independentResources", "sharedBudget", "sharedRequests"),
        ["files"] = new JsonArray(new JsonObject { ["path"] = "Fixture.dll", ["length"] = bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) })
    };
    var file = Path.Combine(root, "transport.component.json");
    void Save() => File.WriteAllText(file, manifest.ToJsonString());
    Save(); File.WriteAllBytes(Path.Combine(root, "Fixture.dll"), bytes);
    string? diagnosticTemp = null;
    async Task<(int Exit, string Output)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(cli);
        if (diagnosticTemp is not null) { start.Environment["TEMP"] = diagnosticTemp; start.Environment["TMP"] = diagnosticTemp; }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var stdout = await output; var stderr = await error;
        Check(!stdout.Contains(root, StringComparison.OrdinalIgnoreCase) && !stdout.Contains("Untrusted label"), "diagnostics omit paths and labels");
        Check(process.ExitCode == 2 || string.IsNullOrWhiteSpace(stderr), "no exception details in diagnostics");
        return (process.ExitCode, stdout);
    }
    foreach (var mode in new[] { "--inspect", "--check-files" })
    {
        var result = await Run(mode, root);
        var report = JsonNode.Parse(result.Output)!;
        Check(result.Exit == 0 && report["Success"]!.GetValue<bool>(), "success exit/report");
        Check(!report["Executed"]!.GetValue<bool>() && !report["Approved"]!.GetValue<bool>(), "inert/no approval");
        Check(report["Findings"]![0]!["CheckedFiles"]!.GetValue<bool>() == (mode == "--check-files"), "metadata and hashing distinguished");
        Check(report["NotTested"]!.AsArray().Any(n => n!.GetValue<string>() == "BudgetCompliance"), "runtime behavior not falsely tested");
    }
    File.WriteAllBytes(Path.Combine(root, "Fixture.dll"), [3, 2, 1]);
    var changed = await Run("--check-files", root);
    Check(changed.Exit == 1 && changed.Output.Contains("PayloadChanged"), "tamper rejects");
    Check((await Run("--inspect", root)).Exit == 0, "metadata mode does not inspect DLL bytes");
    manifest["contractApiVersion"] = 99; Save();
    var future = await Run("--check-files", root);
    Check(future.Exit == 1 && future.Output.Contains("ApiMismatch") && future.Output.Contains("\"CheckedFiles\":false"), "incompatible before hashing");
    File.WriteAllText(file, "not json");
    Check((await Run("--inspect", root)).Exit == 1, "invalid manifest exit");
    Check((await Run("--inspect", "relative")).Exit == 1, "relative path rejected");
    Check((await Run("--inspect", Path.Combine(root, "absent"))).Exit == 1, "missing root exit");
    var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
    Check((await Run("--inspect", empty)).Exit == 1, "empty root not success");
    Check((await Run()).Exit == 2, "usage exit");
    Check((await Run("--verify", root)).Exit == 2, "no hidden code execution option");
    diagnosticTemp = Path.Combine(root, "cli-temporary"); Directory.CreateDirectory(diagnosticTemp);
    var archivePath = Path.Combine(root, "candidate.auralis-transport.zip");
    manifest["contractApiVersion"] = 1;
    void Zip(bool tampered = false, bool traversal = false)
    {
        using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("transport.component.json").Open())) writer.Write(manifest.ToJsonString());
        using (var writer = zip.CreateEntry("Fixture.dll").Open()) writer.Write(tampered ? new byte[] { 3, 2, 1 } : bytes);
        if (traversal) { using var writer = zip.CreateEntry("../escape.dll").Open(); writer.WriteByte(0); }
    }
    Zip();
    var archiveResult = await Run("--check-archive", archivePath);
    var archiveReport = JsonNode.Parse(archiveResult.Output)!;
    Check(archiveResult.Exit == 0 && archiveReport["Success"]!.GetValue<bool>(), "valid archive checked");
    Check(!archiveReport["Executed"]!.GetValue<bool>() && !archiveReport["Approved"]!.GetValue<bool>(), "archive checks do not activate or approve fake DLL");
    Check(archiveReport["Findings"]![0]!["CheckedArchive"]!.GetValue<bool>() && archiveReport["Findings"]![0]!["CheckedFiles"]!.GetValue<bool>(), "archive payload hashing explicit");
    Check(archiveReport["Findings"]![0]!["ArchiveSha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))), "exact archive digest");
    Check(!Directory.EnumerateFileSystemEntries(diagnosticTemp).Any(), "CLI preview/scaffolding cleaned without receipt");
    Zip(tampered: true); var tamperArchive = await Run("--check-archive", archivePath);
    Check(tamperArchive.Exit == 1 && tamperArchive.Output.Contains("ManifestMismatch"), "archive tamper fixed report");
    Zip(traversal: true); var unsafeArchive = await Run("--check-archive", archivePath);
    Check(unsafeArchive.Exit == 1 && unsafeArchive.Output.Contains("UnsafeEntry"), "archive traversal fixed report");
    manifest["contractApiVersion"] = 99; Zip();
    Check((await Run("--check-archive", archivePath)).Exit == 1, "archive incompatible contract rejected");
    Check((await Run("--check-archive", "relative.zip")).Exit == 1, "archive requires absolute local path");
    Check((await Run("--check-archive", Path.Combine(root, "missing.zip"))).Exit == 1, "missing archive typed exit");
    Check(!Directory.EnumerateFileSystemEntries(diagnosticTemp).Any() && !File.Exists(Path.Combine(root, "escape.dll")), "failed CLI archives clean only own scaffolding, no traversal");
    Console.WriteLine($"PASS transport inspector CLI: {checks} assertions; actual child processes, fixed reports, no activation or user state.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine("FAIL CLI fixture: " + error); return 1; }
finally { Directory.Delete(root, recursive: true); } // Only the test-created GUID directory.
