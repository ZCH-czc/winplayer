using System.IO;
using Auralis.MediaTransport;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis;

public partial class MainWindow
{
    private sealed record PreparedNextAudio(IMediaTransportResource Source, PlatformStreamLease Lease, Func<bool> IsCurrent);
    private string? _prefetchHandle;
    private SpeculativeWork<PreparedNextAudio>? _prefetchWork;
    private readonly List<Task> _prefetchCleanup = [];
    private int _prefetchVersion;

    // Dispatcher-owned slot. Never starts a decoder, changes identity, or queues an entire playlist.
    private async Task PrefetchNextAsync(string? currentId, string? handle)
    {
        if (string.IsNullOrEmpty(currentId) || currentId != _currentTrackId || handle?.Length > 128) return;
        if (handle == _prefetchHandle) return;
        var version = ++_prefetchVersion;
        await DrainPrefetchAsync(null);
        await RestoreSavedHandleAsync(handle);
        _prefetchCleanup.RemoveAll(t => t.IsCompleted);
        // Bound outstanding work even when an optional plugin ignores cancellation.
        if (_prefetchCleanup.Count >= 2 || version != _prefetchVersion || currentId != _currentTrackId || string.IsNullOrEmpty(handle)) return;
        _prefetchHandle = handle;
        var coordinator = OnlinePlatforms;
        _prefetchWork = new SpeculativeWork<PreparedNextAudio>(token => PrepareNextAsync(coordinator, handle, token), p => p.Source.DisposeAsync());
    }

    private async Task<PreparedNextAudio?> PrepareNextAsync(OnlinePlatformCoordinator coordinator, string handle, CancellationToken token)
    {
        IMediaTransportResource? source = null;
        try
        {
            var context = await coordinator.CaptureMediaContextAsync(handle, token);
            if (!context.IsCurrent()) return null;
            var result = await coordinator.AcquireStreamAsync(handle, token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess || !context.IsCurrent()) return null;
            source = await _onlinePlaybackSource.PrepareAsync(result.Value,
                $"{context.CacheKey}:{result.Value.Quality.Id}", token, bufferRemote: true);
            token.ThrowIfCancellationRequested();
            if (!context.IsCurrent()) return null;
            var prepared = new PreparedNextAudio(source, result.Value, context.IsCurrent);
            source = null;
            return prepared;
        }
        finally { if (source is not null) await source.DisposeAsync(); }
    }

    private async Task<PreparedNextAudio?> DrainPrefetchAsync(string? requestedHandle, CancellationToken token = default)
    {
        var work = _prefetchWork;
        var matches = requestedHandle is not null && requestedHandle == _prefetchHandle;
        _prefetchHandle = null;
        _prefetchWork = null;
        if (work is null) return null;
        PreparedNextAudio? prepared = null;
        try
        {
            prepared = await work.TakeAsync(matches, token);
            token.ThrowIfCancellationRequested();
            if (prepared is null || !prepared.IsCurrent() ||
                prepared.Lease.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(30) ||
                prepared.Source.Source.IsFile && !File.Exists(prepared.Source.Source.LocalPath)) return null;
            var accepted = prepared;
            prepared = null;
            return accepted;
        }
        finally
        {
            _prefetchCleanup.RemoveAll(t => t.IsCompleted);
            if (!work.Cleanup.IsCompleted) _prefetchCleanup.Add(work.Cleanup);
            if (prepared is not null) await prepared.Source.DisposeAsync();
        }
    }
}
