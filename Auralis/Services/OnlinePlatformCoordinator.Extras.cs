using Auralis.Platform.Abstractions;

namespace Auralis.Services;

internal sealed partial class OnlinePlatformCoordinator
{
    private readonly CommentAvatarRegistry _commentAvatars = new();

    private string? RegisterCommentAvatar(string providerId, Uri? uri,
        Auralis.Platform.Host.PlatformCommentArtworkPolicy policy, Func<bool> isActive, bool fullImage = false)
    {
        lock (_gate) return _commentAvatars.Register(providerId, uri, policy, isActive, fullImage);
    }

    internal PlatformTrack? GetBackendTrack(string handle)
    {
        lock (_gate)
        {
            PurgeExpiredHandles();
            return _tracks.TryGetValue(handle, out var entry) ? entry.Track : null;
        }
    }

    internal OnlineTrackView RestoreSavedTrack(SavedPlaylistEntry entry)
    {
        var id = new PlatformEntityId(entry.ProviderId!, entry.EntityId!);
        // Keep the full provider object already obtained by search/playlists. A refresh must
        // not replace it with the deliberately minimal persistent reference.
        lock (_gate)
        {
            PurgeExpiredHandles();
            var known = SavedTrackMetadata.Match(id, _tracks.Values.OrderByDescending(t => t.CreatedAt).Select(t => t.Track));
            if (known is not null) return RegisterTrack(known, "saved-" + entry.Id);
        }
        return RegisterTrack(SavedTrackMetadata.Fallback(entry), "saved-" + entry.Id);
    }

    private readonly SemaphoreSlim _savedMetadataGate = new(2, 2);
    private readonly Dictionary<string, DateTimeOffset> _savedMetadataAttempts = new();

    internal async Task<OnlineTrackView> RefreshSavedTrackAsync(SavedPlaylistEntry entry, CancellationToken token)
    {
        var fallback = RestoreSavedTrack(entry);
        if (fallback.CoverUrl is not null) return fallback;
        await _savedMetadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            fallback = RestoreSavedTrack(entry);
            lock (_gate)
            {
                foreach (var key in _savedMetadataAttempts.Where(p => DateTimeOffset.UtcNow - p.Value > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) _savedMetadataAttempts.Remove(key);
                if (fallback.CoverUrl is not null || _savedMetadataAttempts.ContainsKey(entry.Id)) return fallback;
                if (_savedMetadataAttempts.Count >= 1600) return fallback;
                _savedMetadataAttempts[entry.Id] = DateTimeOffset.UtcNow;
            }
            var id = new PlatformEntityId(entry.ProviderId!, entry.EntityId!);
            PlatformTrack? resolved = null;
            var result = await _backend.Router.RouteAsync<ITrackDetailsCapability, PlatformTrack>(
                id.ProviderId, (capability, ct) => capability.GetTrackAsync(id, entry.Title, ct), token).ConfigureAwait(false);
            if (result.IsSuccess && result.Value.Id == id && OwnsTrack(id.ProviderId, result.Value)) resolved = result.Value;
            else if (result.Error?.Code == PlatformErrorCode.Unsupported)
            {
                // Legacy/gateway plugins retain their previous exact-ID title-search fallback.
                var search = await _backend.Router.SearchTracksAsync(id.ProviderId,
                    new PlatformSearchRequest(entry.Title, new PlatformPageRequest(50)), token).ConfigureAwait(false);
                if (search.IsSuccess) resolved = SavedTrackMetadata.Match(id, search.Value.Items.Where(t => OwnsTrack(id.ProviderId, t)));
            }
            token.ThrowIfCancellationRequested();
            return resolved is null ? fallback : RegisterTrack(resolved, fallback.Handle);
        }
        finally { _savedMetadataGate.Release(); }
    }

    internal async Task<PlatformResult<IReadOnlyList<OnlineTrackView>>> GetPartsAsync(string handle, CancellationToken token)
    {
        var track = GetBackendTrack(handle);
        if (track is null) return PlatformResult<IReadOnlyList<OnlineTrackView>>.Failure(PlatformErrorCode.NotFound, "歌曲入口已过期，请重新打开歌单。");
        var result = await _backend.Router.RouteAsync<IPlatformMediaExtrasCapability, IReadOnlyList<PlatformTrack>>(
            track.Id.ProviderId, (capability, ct) => capability.GetPartsAsync(track.Id, ct), token).ConfigureAwait(false);
        if (!result.IsSuccess) return PlatformResult<IReadOnlyList<OnlineTrackView>>.Failure(result.Error!);
        var returnedTracks = result.Value.ToArray();
        if (returnedTracks.Any(part => !OwnsTrack(track.Id.ProviderId, part)))
            return InvalidResultIdentity<IReadOnlyList<OnlineTrackView>>();
        return PlatformResult<IReadOnlyList<OnlineTrackView>>.Success(returnedTracks.Select(RegisterTrack).ToArray());
    }

    internal Task<PlatformResult<IReadOnlyList<PlatformTimedComment>>> GetDanmakuAsync(string handle, CancellationToken token)
    {
        var track = GetBackendTrack(handle);
        return track is null
            ? Task.FromResult(PlatformResult<IReadOnlyList<PlatformTimedComment>>.Failure(PlatformErrorCode.NotFound, "歌曲入口已过期，请重新打开歌单。"))
            : _backend.Router.RouteAsync<IPlatformMediaExtrasCapability, IReadOnlyList<PlatformTimedComment>>(
                track.Id.ProviderId, (capability, ct) => capability.GetDanmakuAsync(track.Id, ct), token);
    }

}

internal sealed record OnlineCommentEmote(string Text, string? Url);
internal sealed record OnlineCommentView(string Author, string Text, long LikeCount, DateTimeOffset? PublishedAt, string? AvatarUrl, IReadOnlyList<OnlineCommentEmote> Emotes)
{
    public string? Handle { get; init; }
    public long? ReplyCount { get; init; }
    public string? ReplyToAuthor { get; init; }
}
internal sealed record OnlineCommentPage(IReadOnlyList<OnlineCommentView> Items, string? NextPageHandle);
