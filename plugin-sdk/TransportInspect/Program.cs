using System.Text.Json;
using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

if (args.Length != 2 || args[0] is not ("--inspect" or "--check-files" or "--check-archive"))
{
    Console.Error.WriteLine("Usage: Auralis.TransportInspect --inspect|--check-files <absolute-local-directory>, or --check-archive <absolute-local-ZIP>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    if (args[0] == "--check-archive")
    {
        var finding = await CheckArchiveAsync(args[1], cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Executed = false, Approved = false,
            Findings = new[] { finding }, NotTested = Untested() }));
        return 0;
    }
    var findings = MediaTransportComponentCatalog.Discover(args[1], MediaTransportCapabilities.Full,
        cancellationToken: cancellation.Token);
    var rows = new List<object>();
    var success = findings.Count > 0;
    foreach (var finding in findings)
    {
        var issue = finding.Issue;
        var checkedFiles = false;
        if (issue == MediaTransportPackageIssue.None && args[0] == "--check-files" && finding.Package is not null)
        {
            issue = await MediaTransportComponentCatalog.VerifyPayloadAsync(finding.Package, cancellation.Token);
            checkedFiles = true;
        }
        success &= issue == MediaTransportPackageIssue.None;
        rows.Add(new { Issue = issue.ToString(), Id = finding.Package?.Manifest.Descriptor.Id,
            ManifestSha256 = finding.Package?.ManifestSha256, DeclaredFiles = finding.Package?.Manifest.Files.Count,
            CheckedFiles = checkedFiles });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Success = success, Executed = false, Approved = false,
        Findings = rows, NotTested = Untested() }));
    return success ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Executed = false, Approved = false,
        Issue = "Cancelled", NotTested = Untested() }));
    return 4;
}
catch (Exception error) when (error is MediaTransportArchiveException or MediaTransportInstallationException or IOException or UnauthorizedAccessException)
{
    var issue = error switch { MediaTransportArchiveException archive => archive.Issue.ToString(),
        MediaTransportInstallationException install => install.Issue.ToString(), _ => "StorageFailure" };
    Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Executed = false, Approved = false,
        Issue = issue, NotTested = Untested() }));
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }

static string[] Untested() => ["FactoryType", "RuntimeDescriptor", "Dependencies", "RequestSafety",
    "ResourceLifetime", "BudgetCompliance", "Network", "Playback", "UI", "PublisherIdentity"];

static async Task<object> CheckArchiveAsync(string path, CancellationToken token)
{
    // Never use the application's installation directory; no receipt or enabled state is written.
    var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-inspect-" + Guid.NewGuid().ToString("N"));
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException();
    try
    {
        var store = new MediaTransportInstallationStore(root, MediaTransportCapabilities.Full);
        await using var preview = await store.PreviewArchiveAsync(path, token);
        return new { Issue = "None", Id = preview.Descriptor.Id, preview.ManifestSha256, preview.ArchiveSha256,
            DeclaredFiles = preview.FileCount, CheckedFiles = true, CheckedArchive = true };
    }
    finally
    {
        // Non-recursive removal of exact diagnostic-owned scaffolding only. Any unexpected contents
        // or links cause failure, never traversal or removal of an arbitrary tree.
        var previewRoot = Path.Combine(root, "previews"); var lockFile = Path.Combine(root, "install.lock");
        foreach (var current in new[] { root, previewRoot, lockFile })
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException();
        if (Directory.Exists(previewRoot)) Directory.Delete(previewRoot, recursive: false);
        if (File.Exists(lockFile)) File.Delete(lockFile);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: false);
    }
}
