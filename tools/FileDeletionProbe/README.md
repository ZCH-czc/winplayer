# Synthetic deletion probe

Run `dotnet run --project tools/FileDeletionProbe -c Release -- --run` to characterize immediate
directory enumeration after 300 writes and `File.Delete` calls in one newly generated temporary folder.
The first 150 use direct files; the next 150 use async `.part` writes/flush, rename and idempotent old-name cleanup.
This standalone .NET tool has no Auralis/transport/platform dependencies. It does not inspect processes,
user profiles or caches. Event/snapshot samples are capped, include generated filenames only, and report
rename events plus files that disappear between enumeration and opening. Watcher errors are reported.

This is evidence collection, **not** a test that retries a failed production assertion until it succeeds.
Exit 0 means operations/cleanup completed, not that no transient entries occurred; inspect the JSON counts.
Exit 1 means an operation/cleanup failed; 2 is usage error. Filesystem events do not identify the actor.
Execution-environment comparisons must run the same built binary and preserve both outcomes. A result
without residual entries does not prove production cleanup correct or justify weakening its assertions.
