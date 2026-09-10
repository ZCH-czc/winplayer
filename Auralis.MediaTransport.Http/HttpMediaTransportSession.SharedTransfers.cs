using System.Security.Cryptography;
using System.Text;

namespace Auralis.MediaTransport;

public sealed partial class HttpMediaTransportSession
{
    private readonly object _transferGate = new();
    private readonly Dictionary<string, SharedTransfer> _transfers = new(StringComparer.Ordinal);

    private async Task<IMediaTransportResource> PrepareSharedAsync(MediaTransportRequest request,
        string? identity, CancellationToken token, bool prefetch)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateStreamUri(request.Url);
        EnsureCurrentAuthorization(request);
        if (request.RequestHeaders.Count == 0 && !request.UseHostTransport && !prefetch)
            return await PrepareCoreAsync(request, identity, token, prefetch).ConfigureAwait(false);

        // More restrictive than completed-cache identity: never merge different signed URLs, expiry,
        // header sets, or foreground/speculative policies even when track identity matches.
        var material = CreateCacheKey(request, identity) + "\n" + request.Url.AbsoluteUri + "\n" +
            request.ExpiresAt?.UtcTicks + "\n" + request.UseHostTransport + "\n" + prefetch;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        SharedTransfer shared;
        lock (_transferGate)
        {
            token.ThrowIfCancellationRequested();
            if (!_transfers.TryGetValue(key, out shared!))
            {
                shared = new SharedTransfer();
                Operation worker;
                try { worker = Enter(shared.Cancellation.Token); }
                catch { shared.Cancellation.Dispose(); throw; }
                _transfers.Add(key, shared);
                var started = shared;
                // Worker has its own shutdown scope, never the first waiter's cancellation token.
                shared.Worker = Task.Run(() => RunSharedAsync(started, request, identity, prefetch, worker));
            }
            shared.Waiters++;
        }
        try
        {
            var prepared = await shared.Result.Task.WaitAsync(token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                EnsureCurrentAuthorization(request);
                // The worker's pin bridges completion to each waiter's acquisition. Each waiter
                // returns a new pin, so releasing one result cannot invalidate another result.
                return prepared.CacheEntry is { } entry ? Pin(entry) : new Resource(this, prepared.Source, null);
            }
            finally { _gate.Release(); }
        }
        finally { await LeaveSharedAsync(key, shared).ConfigureAwait(false); }
    }

    private void EnsureCurrentAuthorization(MediaTransportRequest request)
    {
        if (request.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow().AddSeconds(2))
            throw new MediaTransportException(MediaTransportFailure.Expired);
    }

    private async Task RunSharedAsync(SharedTransfer shared, MediaTransportRequest request, string? identity,
        bool prefetch, Operation worker)
    {
        using (worker)
        {
            try
            {
                shared.Source = (Resource)await PrepareCoreAsync(request, identity, worker.Token, prefetch).ConfigureAwait(false);
                shared.Result.TrySetResult(shared.Source);
            }
            catch (OperationCanceledException error) { shared.Result.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { shared.Result.TrySetException(error); }
        }
    }

    private async Task LeaveSharedAsync(string key, SharedTransfer shared)
    {
        lock (_transferGate)
        {
            if (--shared.Waiters != 0) return;
            _transfers.Remove(key); // A later request cannot join an abandoned/failed transfer.
        }
        try
        {
            // No callbacks under the dictionary monitor. Last waiter drains the worker before
            // releasing the bridging pin/CTS; shutdown also tracks that worker independently.
            await shared.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException) { /* Still drain cooperative cleanup. */ }
        finally
        {
            await shared.Worker.ConfigureAwait(false);
            _ = shared.Result.Task.Exception; // Observe a failure even if all waiters cancelled.
            if (shared.Source is { } source) await source.DisposeAsync().ConfigureAwait(false);
            shared.Cancellation.Dispose();
        }
    }

    private sealed class SharedTransfer
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource<Resource> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Worker { get; set; } = Task.CompletedTask;
        internal Resource? Source { get; set; }
        internal int Waiters { get; set; }
    }
}
