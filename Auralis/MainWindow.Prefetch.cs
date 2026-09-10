using System.IO;
using Auralis.MediaTransport;
using Auralis.Platform.Abstractions;

namespace Auralis;

public partial class MainWindow
{
    private sealed record PreparedNextAudio(IMediaTransportResource Source, PlatformStreamLease Lease);
    private string? _prefetchHandle;
    private CancellationTokenSource? _prefetchCancellation;
    private Task<PreparedNextAudio?>? _prefetchTask;
    private int _prefetchVersion;

    // Dispatcher-owned slot. Downloads never touch MediaPlayer or change the current identity.
    private async Task PrefetchNextAsync(string? currentId, string? handle)
    {
        if (string.IsNullOrEmpty(currentId) || currentId != _currentTrackId || handle?.Length > 128) return;
        if (handle == _prefetchHandle) return;
        var version = ++_prefetchVersion;
        await DrainPrefetchAsync(null);
        if (version != _prefetchVersion || currentId != _currentTrackId || string.IsNullOrEmpty(handle) ||
            !OnlinePlatforms.TryGetTrack(handle, out var track) || track is null) return;
        _prefetchHandle = handle;
        _prefetchCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _prefetchTask = PrepareNextAsync(handle, track, _prefetchCancellation.Token);
    }

    private async Task<PreparedNextAudio?> PrepareNextAsync(string handle, Services.OnlineTrackView track, CancellationToken token)
    {
        IMediaTransportResource? source = null;
        try
        {
            var result = await OnlinePlatforms.AcquireStreamAsync(handle, token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess) return null;
            source = await _onlinePlaybackSource.PrepareAsync(result.Value,
                $"{track.ProviderId}:{track.Id}:{result.Value.Quality.Id}", token, bufferRemote: true);
            token.ThrowIfCancellationRequested();
            var prepared = new PreparedNextAudio(source, result.Value);
            source = null; // Transfer ownership to the prefetch slot.
            return prepared;
        }
        catch (Exception e) when (e is OperationCanceledException or Services.OnlinePlaybackException or
            IOException or InvalidOperationException) { return null; }
        finally { if (source is not null) await source.DisposeAsync(); }
    }

    private async Task<PreparedNextAudio?> DrainPrefetchAsync(string? requestedHandle, CancellationToken token = default)
    {
        var task = _prefetchTask;
        var cancellation = _prefetchCancellation;
        var matches = requestedHandle is not null && requestedHandle == _prefetchHandle;
        _prefetchHandle = null;
        _prefetchTask = null;
        _prefetchCancellation = null;
        if (task is null || cancellation is null) return null;
        PreparedNextAudio? prepared = null;
        try
        {
            if (!matches) cancellation.Cancel();
            using var registration = token.Register(cancellation.Cancel);
            prepared = await task;
            token.ThrowIfCancellationRequested();
            if (!matches || prepared is null ||
                prepared.Lease.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(30) ||
                prepared.Source.Source.IsFile && !File.Exists(prepared.Source.Source.LocalPath)) return null;
            var accepted = prepared;
            prepared = null; // Transfer to the playback request, not the transport's global state.
            return accepted;
        }
        finally
        {
            if (prepared is not null) await prepared.Source.DisposeAsync();
            cancellation.Dispose();
        }
    }
}
