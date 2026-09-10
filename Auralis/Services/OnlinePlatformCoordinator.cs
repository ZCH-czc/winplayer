using System.Security.Cryptography;
using Auralis.Platform.Abstractions;

namespace Auralis.Services;

/// <summary>
/// Keeps online discovery objects out of the local-library model. The WebView receives only short-lived
/// random handles and public display metadata; provider IDs needed for playback remain native-side.
/// </summary>
internal sealed partial class OnlinePlatformCoordinator
{
    private const int MaximumTrackHandles = 1600;
    private const int MaximumPlaylistHandles = 400;
    private const int MaximumSearchPageHandles = 400;
    private static readonly TimeSpan HandleLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan PlaylistDetailCacheLifetime = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly PlatformBackendService _backend;
    private readonly Dictionary<string, TrackHandleEntry> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlaylistHandleEntry> _playlists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SearchPageHandleEntry> _searchPages = new(StringComparer.Ordinal);

    internal OnlinePlatformCoordinator(PlatformBackendService backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    internal async Task<PlatformResult<OnlineTrackPageView>> SearchAsync(
        string providerId,
        string query,
        int pageSize,
        string? pageHandle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 64 || providerId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') || string.IsNullOrWhiteSpace(query) || query.Length > 512 ||
            pageSize is < 1 or > 100 || pageHandle?.Length > 128)
        {
            return PlatformResult<OnlineTrackPageView>.Failure(
                PlatformErrorCode.InvalidRequest,
                "在线搜索请求无效。");
        }

        var provider = await ResolveProviderAsync(providerId, PlatformCapabilityKind.TrackSearch, cancellationToken).ConfigureAwait(false);
        if (!provider.IsSuccess) return PlatformResult<OnlineTrackPageView>.Failure(provider.Error!);
        var normalizedQuery = query.Trim();
        string? providerCursor = null;
        if (!string.IsNullOrWhiteSpace(pageHandle))
        {
            SearchPageHandleEntry? pageEntry;
            lock (_gate)
            {
                PurgeExpiredHandles();
                _searchPages.TryGetValue(pageHandle, out pageEntry);
            }

            if (pageEntry is null ||
                !string.Equals(pageEntry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pageEntry.Query, normalizedQuery, StringComparison.Ordinal))
            {
                return PlatformResult<OnlineTrackPageView>.Failure(
                    PlatformErrorCode.NotFound,
                    "在线搜索分页入口已过期，请重新搜索。");
            }

            providerCursor = pageEntry.ProviderCursor;
        }

        var result = await _backend.Router.SearchTracksAsync(
            providerId,
            new PlatformSearchRequest(normalizedQuery, new PlatformPageRequest(pageSize, providerCursor)),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return PlatformResult<OnlineTrackPageView>.Failure(result.Error!);
        }

        var returnedTracks = result.Value.Items.ToArray();
        if (returnedTracks.Any(track => !OwnsTrack(providerId, track)))
            return InvalidResultIdentity<OnlineTrackPageView>();
        var views = returnedTracks.Select(RegisterTrack).ToArray();
        string? nextPageHandle = null;
        if (!string.IsNullOrWhiteSpace(result.Value.NextCursor))
        {
            nextPageHandle = CreateHandle("searchpage");
            lock (_gate)
            {
                PurgeExpiredHandles();
                TrimOldest(_searchPages, MaximumSearchPageHandles - 1);
                _searchPages[nextPageHandle] = new SearchPageHandleEntry(
                    providerId,
                    normalizedQuery,
                    result.Value.NextCursor,
                    DateTimeOffset.UtcNow);
            }
        }

        return PlatformResult<OnlineTrackPageView>.Success(new OnlineTrackPageView(
            views,
            nextPageHandle,
            result.Value.TotalCount));
    }

    internal async Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        TrackHandleEntry? entry;
        lock (_gate)
        {
            PurgeExpiredHandles();
            _tracks.TryGetValue(handle, out entry);
        }

        if (entry is null)
        {
            return PlatformResult<PlatformStreamLease>.Failure(
                PlatformErrorCode.NotFound,
                "在线歌曲结果已过期，请重新搜索。");
        }

        if (entry.Track.Availability == PlatformTrackAvailability.Unavailable)
        {
            return PlatformResult<PlatformStreamLease>.Failure(
                PlatformErrorCode.ContentUnavailable,
                "这首在线歌曲当前不可播放。");
        }

        return await _backend.Router.AcquireStreamAsync(
            entry.Track.Id.ProviderId,
            new PlatformPlaybackRequest(
                entry.Track.Id,
                allowQualityFallback: true,
                allowPreview: entry.Track.Availability == PlatformTrackAvailability.PreviewOnly),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PlatformResult<PlatformVideoLease>> AcquireVideoAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        TrackHandleEntry? entry;
        lock (_gate)
        {
            PurgeExpiredHandles();
            _tracks.TryGetValue(handle, out entry);
        }

        if (entry is null)
        {
            return PlatformResult<PlatformVideoLease>.Failure(
                PlatformErrorCode.NotFound,
                "在线歌曲结果已过期，请重新搜索。");
        }

        if (entry.Track.MusicVideo is null)
        {
            return PlatformResult<PlatformVideoLease>.Failure(
                PlatformErrorCode.NotFound,
                "这首歌没有可播放的 MV。");
        }

        return await _backend.Router.AcquireVideoAsync(
            entry.Track.Id.ProviderId,
            new PlatformVideoPlaybackRequest(entry.Track.MusicVideo.Id),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PlatformResult<PlatformLyrics>> GetLyricsAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        TrackHandleEntry? entry;
        lock (_gate)
        {
            PurgeExpiredHandles();
            _tracks.TryGetValue(handle, out entry);
        }

        if (entry is null)
        {
            return PlatformResult<PlatformLyrics>.Failure(
                PlatformErrorCode.NotFound,
                "在线歌曲结果已过期，请重新搜索。");
        }

        return await _backend.Router.GetLyricsAsync(
            entry.Track.Id.ProviderId,
            new PlatformLyricsRequest(entry.Track.Id),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PlatformResult<IReadOnlyList<OnlinePlaylistView>>> GetPlaylistsAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderAsync(providerId, PlatformCapabilityKind.PlaylistBrowse, cancellationToken).ConfigureAwait(false);
        if (!provider.IsSuccess) return PlatformResult<IReadOnlyList<OnlinePlaylistView>>.Failure(provider.Error!);
        var result = await _backend.Router.BrowsePlaylistsAsync(
            providerId,
            new PlatformPageRequest(200),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return PlatformResult<IReadOnlyList<OnlinePlaylistView>>.Failure(result.Error!);
        }

        var returnedPlaylists = result.Value.Items.ToArray();
        if (returnedPlaylists.Any(playlist => playlist is null || !OwnsEntity(providerId, playlist.Id)))
            return InvalidResultIdentity<IReadOnlyList<OnlinePlaylistView>>();
        var views = returnedPlaylists.Select(RegisterPlaylist).ToArray();
        return PlatformResult<IReadOnlyList<OnlinePlaylistView>>.Success(views);
    }

    internal async Task<PlatformResult<OnlinePlaylistDetailView>> GetPlaylistAsync(
        string providerId,
        string handle,
        CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderAsync(providerId, PlatformCapabilityKind.PlaylistDetails, cancellationToken).ConfigureAwait(false);
        if (!provider.IsSuccess) return PlatformResult<OnlinePlaylistDetailView>.Failure(provider.Error!);
        var providerName = provider.Value.DisplayName;

        PlaylistHandleEntry? entry;
        lock (_gate)
        {
            PurgeExpiredHandles();
            _playlists.TryGetValue(handle, out entry);
        }

        if (entry is null)
        {
            return PlatformResult<OnlinePlaylistDetailView>.Failure(
                PlatformErrorCode.NotFound,
                $"{providerName} 歌单入口已过期，请刷新个人歌单。");
        }

        if (!entry.Playlist.Id.IsForProvider(providerId))
        {
            return PlatformResult<OnlinePlaylistDetailView>.Failure(
                PlatformErrorCode.InvalidRequest,
                $"这个歌单入口不属于 {providerName}。");
        }

        // Public collection plugins must not be required to implement a login capability.
        // For account-backed collections, still revalidate before serving even a cached detail.
        if (provider.Value.Capabilities.Contains(PlatformCapabilityKind.Authentication))
        {
            var authentication = await _backend.Router.GetAuthenticationStateAsync(providerId, cancellationToken).ConfigureAwait(false);
            if (!authentication.IsSuccess) return PlatformResult<OnlinePlaylistDetailView>.Failure(authentication.Error!);
            if (authentication.Value.Status != PlatformAuthenticationStatus.SignedIn)
                return PlatformResult<OnlinePlaylistDetailView>.Failure(PlatformErrorCode.AuthenticationRequired, $"{providerName} 登录已失效，请重新登录。");
        }

        var tracksResult = await _backend.Router.GetPlaylistTracksAsync(
            providerId,
            entry.Playlist.Id,
            new PlatformPageRequest(200),
            cancellationToken).ConfigureAwait(false);
        if (!tracksResult.IsSuccess)
        {
            if (CanUseCachedPlaylistDetail(tracksResult.Error) &&
                entry.CachedDetail is { } cachedDetail &&
                entry.CachedAt is { } cachedAt &&
                DateTimeOffset.UtcNow - cachedAt <= PlaylistDetailCacheLifetime)
            {
                return PlatformResult<OnlinePlaylistDetailView>.Success(cachedDetail);
            }

            return PlatformResult<OnlinePlaylistDetailView>.Failure(tracksResult.Error!);
        }

        var returnedTracks = tracksResult.Value.Items.ToArray();
        if (returnedTracks.Any(track => !OwnsTrack(providerId, track)))
            return InvalidResultIdentity<OnlinePlaylistDetailView>();
        var playlist = entry.Playlist;
        var totalCount = (int)Math.Clamp(
            Math.Max(playlist.TrackCount ?? 0, tracksResult.Value.TotalCount ?? returnedTracks.Length),
            0,
            int.MaxValue);
        playlist = playlist with
        {
            TrackCount = totalCount,
            ArtworkUrl = playlist.ArtworkUrl ?? returnedTracks
                .Select(static track => track.ArtworkUrl)
                .FirstOrDefault(static uri => uri is not null)
        };
        var playlistView = new OnlinePlaylistView(
            handle,
            playlist.Title,
            playlist.OwnerName ?? providerName,
            totalCount,
            CreateArtworkProxyUrl(handle, playlist.ArtworkUrl),
            playlist.Description ?? $"{providerName} 个人歌单",
            playlist.IsEditable);
        var tracks = returnedTracks.Select(RegisterTrack).ToArray();
        var detail = new OnlinePlaylistDetailView(playlistView, tracks);
        lock (_gate)
        {
            PurgeExpiredHandles();
            _playlists[handle] = new PlaylistHandleEntry(
                playlist,
                entry.CreatedAt,
                detail,
                DateTimeOffset.UtcNow);
        }
        return PlatformResult<OnlinePlaylistDetailView>.Success(detail);
    }

    internal bool TryGetTrack(string handle, out OnlineTrackView? view)
    {
        lock (_gate)
        {
            PurgeExpiredHandles();
            if (_tracks.TryGetValue(handle, out var entry))
            {
                view = entry.View;
                return true;
            }
        }

        view = null;
        return false;
    }

    internal bool TryGetArtworkUri(string handle, out Uri? uri, out Func<Uri, bool>? destinationAllowed)
    {
        destinationAllowed = null;
        lock (_gate)
        {
            PurgeExpiredHandles();
            if (_commentAvatars.TryResolve(handle, out uri, out destinationAllowed)) return true;
            if (_tracks.TryGetValue(handle, out var trackEntry) &&
                GetSafeArtworkUri(trackEntry.Track.ArtworkUrl) is { } trackArtwork)
            {
                uri = trackArtwork;
                return true;
            }

            if (_playlists.TryGetValue(handle, out var playlistEntry) &&
                GetSafeArtworkUri(playlistEntry.Playlist.ArtworkUrl) is { } playlistArtwork)
            {
                uri = playlistArtwork;
                return true;
            }

        }

        uri = null;
        return false;
    }

    private OnlineTrackView RegisterTrack(PlatformTrack track) => RegisterTrack(track, null);

    private OnlineTrackView RegisterTrack(PlatformTrack track, string? savedHandle)
    {
        var handle = savedHandle ?? CreateHandle("track");
        var view = new OnlineTrackView(
            handle,
            handle,
            "online",
            track.Id.ProviderId,
            _backend.ProviderDisplayName(track.Id.ProviderId),
            track.Title,
            track.Artists.Count == 0
                ? "未知艺术家"
                : string.Join("、", track.Artists.Select(static artist => artist.Name)),
            track.Album?.Title ?? "在线单曲",
            CreateArtworkProxyUrl(handle, track.ArtworkUrl),
            track.Duration?.TotalSeconds ?? 0,
            track.Availability.ToString().ToLowerInvariant(),
            track.Availability != PlatformTrackAvailability.Unavailable,
            track.MusicVideo is not null);

        lock (_gate)
        {
            PurgeExpiredHandles();
            TrimOldest(_tracks, MaximumTrackHandles - 1);
            _tracks[handle] = new TrackHandleEntry(track, view, DateTimeOffset.UtcNow);
        }

        return view;
    }

    private OnlinePlaylistView RegisterPlaylist(PlatformPlaylist playlist)
    {
        var handle = CreateHandle("playlist");
        var providerName = _backend.ProviderDisplayName(playlist.Id.ProviderId);
        var view = new OnlinePlaylistView(
            handle,
            playlist.Title,
            playlist.OwnerName ?? providerName,
            playlist.TrackCount ?? 0,
            CreateArtworkProxyUrl(handle, playlist.ArtworkUrl),
            playlist.Description ?? $"{providerName} 个人歌单",
            playlist.IsEditable);
        lock (_gate)
        {
            PurgeExpiredHandles();
            TrimOldest(_playlists, MaximumPlaylistHandles - 1);
            _playlists[handle] = new PlaylistHandleEntry(playlist, DateTimeOffset.UtcNow, null, null);
        }

        return view;
    }

    private void PurgeExpiredHandles()
    {
        var cutoff = DateTimeOffset.UtcNow - HandleLifetime;
        foreach (var key in _tracks.Where(pair => pair.Value.CreatedAt < cutoff)
                     .Select(static pair => pair.Key).ToArray())
        {
            _tracks.Remove(key);
        }

        foreach (var key in _playlists.Where(pair => pair.Value.CreatedAt < cutoff)
                     .Select(static pair => pair.Key).ToArray())
        {
            _playlists.Remove(key);
        }

        foreach (var key in _searchPages.Where(pair => pair.Value.CreatedAt < cutoff)
                     .Select(static pair => pair.Key).ToArray())
        {
            _searchPages.Remove(key);
        }

    }

    private static void TrimOldest<T>(Dictionary<string, T> entries, int desiredMaximum)
        where T : IHandleEntry
    {
        while (entries.Count > desiredMaximum && entries.Count > 0)
        {
            var oldest = entries.MinBy(static pair => pair.Value.CreatedAt);
            entries.Remove(oldest.Key);
        }
    }

    private static string CreateHandle(string kind) =>
        $"{kind}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";

    private static string? CreateArtworkProxyUrl(string handle, Uri? uri) =>
        GetSafeArtworkUri(uri) is null
            ? null
            : $"https://platform-art.auralis.local/{Uri.EscapeDataString(handle)}";

    private static Uri? GetSafeArtworkUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsoluteUri.Length > 8192 ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
        {
            return null;
        }

        return uri;
    }

    private interface IHandleEntry
    {
        DateTimeOffset CreatedAt { get; }
    }

    private sealed record TrackHandleEntry(
        PlatformTrack Track,
        OnlineTrackView View,
        DateTimeOffset CreatedAt) : IHandleEntry;

    private sealed record PlaylistHandleEntry(
        PlatformPlaylist Playlist,
        DateTimeOffset CreatedAt,
        OnlinePlaylistDetailView? CachedDetail,
        DateTimeOffset? CachedAt) : IHandleEntry;

    private sealed record SearchPageHandleEntry(
        string ProviderId,
        string Query,
        string ProviderCursor,
        DateTimeOffset CreatedAt) : IHandleEntry;

    private static bool CanUseCachedPlaylistDetail(PlatformError? error) =>
        error?.Code is PlatformErrorCode.NotFound or
            PlatformErrorCode.InvalidResponse or
            PlatformErrorCode.ServiceUnavailable or
            PlatformErrorCode.NetworkUnavailable or
            PlatformErrorCode.Timeout;

}

internal sealed record OnlineTrackView(
    string Handle,
    string Id,
    string Kind,
    string ProviderId,
    string SourceName,
    string Title,
    string Artist,
    string Album,
    string? CoverUrl,
    double DurationSeconds,
    string Availability,
    bool IsPlayable,
    bool HasMusicVideo);

internal sealed record OnlineTrackPageView(
    IReadOnlyList<OnlineTrackView> Items,
    string? NextPageHandle,
    long? TotalCount);

internal sealed record OnlinePlaylistView(
    string Handle,
    string Title,
    string Owner,
    int TrackCount,
    string? CoverUrl,
    string Description,
    bool IsEditable);

internal sealed record OnlinePlaylistDetailView(
    OnlinePlaylistView Playlist,
    IReadOnlyList<OnlineTrackView> Tracks);
