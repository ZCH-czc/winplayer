using Auralis.Services;
using System.Diagnostics;

internal static class SpeculativeWorkTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task RunAsync()
    {
        var released = 0;
        ValueTask Release(object value) { Interlocked.Increment(ref released); return ValueTask.CompletedTask; }
        var value = new object();
        var ready = new SpeculativeWork<object>(_ => Task.FromResult<object?>(value), Release);
        Check(ReferenceEquals(value, await ready.TakeAsync(true)), "Ready result transfers to exactly one consumer");
        Check(await ready.TakeAsync(true) is null && released == 0, "No double transfer or premature release");
        await Release(value);

        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignoredCancellation = new SpeculativeWork<object>(_ => pending.Task, Release);
        var watch = Stopwatch.StartNew();
        Check(await ignoredCancellation.TakeAsync(false) is null && watch.Elapsed < TimeSpan.FromSeconds(1), "Discard never awaits a slow plugin");
        pending.SetResult(new object()); await ignoredCancellation.Cleanup;
        Check(released == 2, "Late resource released exactly once");

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new SpeculativeWork<object>(_ => pending.Task, Release);
        watch.Restart();
        Check(await slow.TakeAsync(true) is null && watch.Elapsed < TimeSpan.FromSeconds(2), "Foreground join has a finite deadline");
        pending.SetResult(new object()); await slow.Cleanup;
        Check(released == 3, "Join timeout releases late result");

        using var cancelled = new CancellationTokenSource();
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new SpeculativeWork<object>(_ => pending.Task, Release);
        cancelled.Cancel();
        try { await cancel.TakeAsync(true, cancelled.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { }
        pending.SetResult(new object()); await cancel.Cleanup;
        Check(released == 4, "Cancelled consumer releases late result");
        var failed = new SpeculativeWork<object>(_ => Task.FromException<object?>(new IOException("fixture")), Release);
        Check(await failed.TakeAsync(true) is null, "Background faults don't fail foreground");
        await failed.Cleanup;
        var internalCancellation = new SpeculativeWork<object>(_ => Task.FromCanceled<object?>(new CancellationToken(true)), Release);
        Check(await internalCancellation.TakeAsync(true) is null, "Expired background token does not cancel current playback");
        await internalCancellation.Cleanup;

        var revisions = new MediaContextRevision();
        var original = revisions.Read("source.one");
        revisions.Invalidate("SOURCE.ONE");
        Check(revisions.Read("source.one") != original && revisions.Read("source.two") == 0, "Provider scoped, case insensitive invalidation");
        Console.WriteLine("PASS speculative work: ready, discard, slow plugin, cancellation, fault, expiry, context isolation");
    }
}
