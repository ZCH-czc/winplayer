using System.Collections.Concurrent;
using System.Text.Json;

// Standalone filesystem characterization, deliberately no transport/app/plugin dependency.
// Fixed iterations collect evidence; this is not a retry-until-green regression test.
if (args.Length != 1 || args[0] != "--run")
{
    Console.Error.WriteLine("Usage: FileDeletionProbe --run (300 synthetic write/delete operations)");
    return 2;
}
var root = Path.Combine(Path.GetTempPath(), "Auralis-delete-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var renames = new ConcurrentQueue<string>();
var renameCount = 0;
var watcherErrors = 0;
var samples = new List<object>();
var residualSnapshots = 0;
var missingAfterEnumeration = 0;
var originalStillExists = 0;
var operationFailures = 0;
var cleanupIssue = "None";
try
{
    using var watcher = new FileSystemWatcher(root) { NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 64 * 1024 };
    watcher.Renamed += (_, e) =>
    {
        if (Interlocked.Increment(ref renameCount) <= 8) renames.Enqueue(e.OldName + " -> " + e.Name);
    };
    watcher.Error += (_, _) => Interlocked.Increment(ref watcherErrors);
    watcher.EnableRaisingEvents = true;
    for (var i = 0; i < 300; i++)
    {
        var file = Path.Combine(root, "stream-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            if (i < 150) File.WriteAllBytes(file, [1, 2, 3, 4]);
            else
            {
                // Match completed transport-file handoff without running any transport code.
                var partial = file + ".part";
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                { await output.WriteAsync(new byte[] { 1, 2, 3, 4 }); await output.FlushAsync(); }
                File.Move(partial, file);
                File.Delete(partial); // Idempotent cleanup of the old name after rename.
            }
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            operationFailures++;
            if (samples.Count < 8) samples.Add(new { Operation = "WriteDelete", Issue = e.GetType().Name, HResult = e.HResult });
        }
        if (File.Exists(file)) originalStillExists++;
        var remaining = Directory.EnumerateFiles(root).ToArray();
        if (remaining.Length > 0) residualSnapshots++;
        foreach (var path in remaining)
        {
            string issue;
            try { using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); issue = "Readable"; }
            catch (FileNotFoundException) { missingAfterEnumeration++; issue = "GoneAfterEnumeration"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { issue = e.GetType().Name + ":" + e.HResult; }
            if (samples.Count < 8) samples.Add(new { Operation = i < 150 ? "DirectDelete" : "MoveAndDelete", Name = Path.GetFileName(path), Issue = issue });
        }
    }
}
finally
{
    // Only this invocation's generated GUID directory; no caller-selected/user directories.
    try { Directory.Delete(root, recursive: true); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { cleanupIssue = e.GetType().Name + ":" + e.HResult; }
}
Console.WriteLine(JsonSerializer.Serialize(new { Iterations = 300, OperationFailures = operationFailures,
    OriginalStillExists = originalStillExists, ResidualSnapshots = residualSnapshots, MissingAfterEnumeration = missingAfterEnumeration,
    RenameEvents = renameCount, WatcherErrors = watcherErrors, RenameSamples = renames.ToArray(), Samples = samples, CleanupIssue = cleanupIssue,
    Conclusion = "ObservationOnly_NoTransportCode_NoClaimOfFilesystemActorIdentity" }));
return operationFailures == 0 && cleanupIssue == "None" ? 0 : 1;
