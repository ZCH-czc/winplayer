using Auralis.Services;

internal static class ShutdownContextTests
{
    // WPF OnExit no longer pumps the dispatcher. A service teardown must not post back to it.
    private sealed class NonPumpingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }

    internal static void DisposeWithoutDispatcher(LanMusicSharingService service)
    {
        Exception? failure = null;
        var context = new NonPumpingContext();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { failure = exception; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("LAN shutdown deadlocked on a non-pumping UI context.");
        if (failure is not null) throw new InvalidOperationException("LAN shutdown failed.", failure);
        if (context.Posts != 0) throw new InvalidOperationException("LAN shutdown captured the UI context.");
        Console.WriteLine("PASS LAN shutdown: running server exits without dispatcher continuations.");
    }
}
