namespace Auralis.Services;

/// <summary>A speculative resource has exactly one consumer or one asynchronous disposer.
/// Cancellation of optional plugin work must never block a foreground playback request.</summary>
internal sealed class SpeculativeWork<T> where T : class
{
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(2));
    private readonly Task<T?> _task;
    private readonly Func<T, ValueTask> _release;
    private int _claimed;
    internal SpeculativeWork(Func<CancellationToken, Task<T?>> prepare, Func<T, ValueTask> release)
    {
        _release = release;
        // Even a plugin's synchronous pre-await work stays off the window dispatcher.
        _task = Task.Run(() => prepare(_cancellation.Token));
    }

    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    internal async Task<T?> TakeAsync(bool matches, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) return null;
        if (!matches) { Cleanup = Task.Run(DiscardAsync); return null; }
        try
        {
            var value = await _task.WaitAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _cancellation.Dispose();
            return value;
        }
        catch (TimeoutException) { Cleanup = Task.Run(DiscardAsync); return null; }
        catch (OperationCanceledException) { Cleanup = Task.Run(DiscardAsync); token.ThrowIfCancellationRequested(); return null; }
        catch { Cleanup = Task.Run(DiscardAsync); return null; }
    }

    private async Task DiscardAsync()
    {
        try
        {
            // CancelAsync doesn't run arbitrary plugin callbacks on the dispatcher.
            var cancelled = _cancellation.CancelAsync();
            try { if (await _task.ConfigureAwait(false) is { } value) await _release(value).ConfigureAwait(false); }
            finally { await cancelled.ConfigureAwait(false); }
        }
        catch { /* Observe plugin faults; speculative failure never fails local playback. */ }
        finally { _cancellation.Dispose(); }
    }
}
