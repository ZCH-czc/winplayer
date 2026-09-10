namespace Auralis.MediaTransport;

/// <summary>Shared admission budget for in-flight HTTP transfers, not a quota for committed cache files.
/// Hosts can share one instance across audio/video sessions. No I/O, timers or global process state.
/// Admission fails fast: partial transfers never wait for one another while holding reservations.</summary>
public sealed class MediaTransferBudget : IMediaTransferBudget
{
    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly long _maximumPrefetchBytes;
    private readonly int _maximumTransfers;
    private readonly int _maximumPrefetchTransfers;
    private long _bytes, _prefetchBytes;
    private int _transfers, _prefetchTransfers;

    public MediaTransferBudget(long maximumBytes = 1024L * 1024 * 1024, int maximumTransfers = 8,
        long maximumPrefetchBytes = 128L * 1024 * 1024, int maximumPrefetchTransfers = 2)
    {
        if (maximumBytes <= 0 || maximumTransfers <= 0 || maximumPrefetchBytes <= 0 ||
            maximumPrefetchBytes > maximumBytes || maximumPrefetchTransfers <= 0 || maximumPrefetchTransfers > maximumTransfers)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Invalid transfer budget limits.");
        _maximumBytes = maximumBytes; _maximumTransfers = maximumTransfers;
        _maximumPrefetchBytes = maximumPrefetchBytes; _maximumPrefetchTransfers = maximumPrefetchTransfers;
    }

    public IMediaTransferReservation Reserve(bool prefetch)
    {
        lock (_gate)
        {
            if (_transfers >= _maximumTransfers || prefetch && _prefetchTransfers >= _maximumPrefetchTransfers)
                throw new MediaTransportException(MediaTransportFailure.BudgetExceeded);
            _transfers++;
            if (prefetch) _prefetchTransfers++;
            return new Reservation(this, prefetch);
        }
    }

    private sealed class Reservation(MediaTransferBudget owner, bool prefetch) : IMediaTransferReservation
    {
        private long _reserved;
        private bool _released;
        // Total required for this file, not a delta. Reserve before file preallocation or each write.
        public void Ensure(long total)
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
                if (total <= _reserved) return;
                var extra = total - _reserved;
                if (extra > owner._maximumBytes - owner._bytes ||
                    prefetch && extra > owner._maximumPrefetchBytes - owner._prefetchBytes)
                    throw new MediaTransportException(MediaTransportFailure.BudgetExceeded);
                owner._bytes += extra;
                if (prefetch) owner._prefetchBytes += extra;
                _reserved = total;
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_released) return;
                _released = true;
                owner._bytes -= _reserved; owner._transfers--;
                if (prefetch) { owner._prefetchBytes -= _reserved; owner._prefetchTransfers--; }
            }
        }
    }
}
