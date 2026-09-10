namespace Auralis.Platform.Abstractions;

/// <summary>Searches tracks on one provider.</summary>
public interface ITrackSearchCapability
{
    /// <summary>Searches tracks while observing caller cancellation.</summary>
    Task<PlatformResult<PlatformPage<PlatformTrack>>> SearchTracksAsync(
        PlatformSearchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Returns provider search suggestions without conflating them with track results.</summary>
public interface ISearchSuggestionsCapability
{
    /// <summary>Gets provider suggestions for a partial query.</summary>
    Task<PlatformResult<IReadOnlyList<PlatformSearchSuggestion>>> GetSearchSuggestionsAsync(
        PlatformSearchSuggestionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Returns provider-curated hot search terms.</summary>
public interface IHotSearchCapability
{
    /// <summary>Gets a page of hot search entries.</summary>
    Task<PlatformResult<PlatformPage<PlatformHotSearchItem>>> GetHotSearchesAsync(
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Searches albums on one provider.</summary>
public interface IAlbumSearchCapability
{
    /// <summary>Searches albums.</summary>
    Task<PlatformResult<PlatformPage<PlatformAlbum>>> SearchAlbumsAsync(
        PlatformSearchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves album metadata and tracks on one provider.</summary>
public interface IAlbumDetailsCapability
{
    /// <summary>Gets one album. IDs owned by another provider must return <see cref="PlatformErrorCode.InvalidRequest"/>.</summary>
    Task<PlatformResult<PlatformAlbum>> GetAlbumAsync(
        PlatformEntityId albumId,
        CancellationToken cancellationToken);

    /// <summary>Gets a cursor-based page of tracks in an album.</summary>
    Task<PlatformResult<PlatformPage<PlatformTrack>>> GetAlbumTracksAsync(
        PlatformEntityId albumId,
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Convenience aggregate for providers that support both album search and details.</summary>
public interface IAlbumCapability : IAlbumSearchCapability, IAlbumDetailsCapability
{
}

/// <summary>Searches and resolves artist metadata.</summary>
public interface IArtistDetailsCapability
{
    /// <summary>Searches artists.</summary>
    Task<PlatformResult<PlatformPage<PlatformArtist>>> SearchArtistsAsync(
        PlatformSearchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Gets one artist.</summary>
    Task<PlatformResult<PlatformArtist>> GetArtistAsync(
        PlatformEntityId artistId,
        CancellationToken cancellationToken);
}

/// <summary>Browses albums belonging to an artist.</summary>
public interface IArtistAlbumsCapability
{
    /// <summary>Gets a page of the artist's albums.</summary>
    Task<PlatformResult<PlatformPage<PlatformAlbum>>> GetArtistAlbumsAsync(
        PlatformEntityId artistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Browses tracks belonging to an artist.</summary>
public interface IArtistTracksCapability
{
    /// <summary>Gets a page of provider-ranked artist tracks.</summary>
    Task<PlatformResult<PlatformPage<PlatformTrack>>> GetArtistTopTracksAsync(
        PlatformEntityId artistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Convenience aggregate for providers that support artist details, tracks, and albums.</summary>
public interface IArtistCapability : IArtistDetailsCapability, IArtistAlbumsCapability, IArtistTracksCapability
{
}

/// <summary>Browses provider-curated playlists.</summary>
public interface IPlaylistBrowseCapability
{
    /// <summary>Gets a page of featured, recommended, or category-default playlists.</summary>
    Task<PlatformResult<PlatformPage<PlatformPlaylist>>> BrowsePlaylistsAsync(
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Searches playlists on one provider.</summary>
public interface IPlaylistSearchCapability
{
    /// <summary>Searches public or user-visible playlists.</summary>
    Task<PlatformResult<PlatformPage<PlatformPlaylist>>> SearchPlaylistsAsync(
        PlatformSearchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves playlist metadata and tracks.</summary>
public interface IPlaylistDetailsCapability
{
    /// <summary>Gets one playlist.</summary>
    Task<PlatformResult<PlatformPlaylist>> GetPlaylistAsync(
        PlatformEntityId playlistId,
        CancellationToken cancellationToken);

    /// <summary>Gets a cursor-based page of tracks in a playlist.</summary>
    Task<PlatformResult<PlatformPage<PlatformTrack>>> GetPlaylistTracksAsync(
        PlatformEntityId playlistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Convenience aggregate for providers that support all playlist operations.</summary>
public interface IPlaylistCapability : IPlaylistBrowseCapability, IPlaylistSearchCapability, IPlaylistDetailsCapability
{
}

/// <summary>Browses provider charts and their tracks.</summary>
public interface IChartCapability
{
    /// <summary>Gets a page of charts.</summary>
    Task<PlatformResult<PlatformPage<PlatformChart>>> GetChartsAsync(
        PlatformPageRequest page,
        CancellationToken cancellationToken);

    /// <summary>Gets a page of ranked chart tracks.</summary>
    Task<PlatformResult<PlatformPage<PlatformTrack>>> GetChartTracksAsync(
        PlatformEntityId chartId,
        PlatformPageRequest page,
        CancellationToken cancellationToken);
}

/// <summary>Requests lyrics for a provider-qualified track.</summary>
public sealed record PlatformLyricsRequest
{
    /// <summary>Creates a lyric request.</summary>
    public PlatformLyricsRequest(
        PlatformEntityId trackId,
        string? preferredLanguage = null,
        bool includeTranslation = true)
    {
        TrackId = trackId;
        PreferredLanguage = preferredLanguage;
        IncludeTranslation = includeTranslation;
    }

    /// <summary>Gets the provider-qualified track ID.</summary>
    public PlatformEntityId TrackId { get; }

    /// <summary>Gets an optional preferred BCP-47 language tag.</summary>
    public string? PreferredLanguage { get; }

    /// <summary>Gets whether translated lines should be requested when available.</summary>
    public bool IncludeTranslation { get; }
}

/// <summary>Resolves provider lyrics.</summary>
public interface ILyricsCapability
{
    /// <summary>Gets lyrics for a track.</summary>
    Task<PlatformResult<PlatformLyrics>> GetLyricsAsync(
        PlatformLyricsRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves artwork through the trusted backend image pipeline.</summary>
public interface IArtworkCapability
{
    /// <summary>Acquires an artwork resource for a provider entity.</summary>
    Task<PlatformResult<PlatformArtworkResource>> AcquireArtworkAsync(
        PlatformArtworkRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Gets public or authenticated comments for a provider entity.</summary>
public interface ICommentsCapability
{
    /// <summary>Gets a cursor-based comment page.</summary>
    Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentsAsync(
        PlatformCommentsRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Requests a lawful backend stream lease for a provider track.</summary>
public sealed record PlatformPlaybackRequest
{
    /// <summary>Creates a playback request.</summary>
    public PlatformPlaybackRequest(
        PlatformEntityId trackId,
        string? preferredQualityId = null,
        bool allowQualityFallback = true,
        bool allowPreview = false)
    {
        TrackId = trackId;
        PreferredQualityId = preferredQualityId;
        AllowQualityFallback = allowQualityFallback;
        AllowPreview = allowPreview;
    }

    /// <summary>Gets the provider-qualified track ID.</summary>
    public PlatformEntityId TrackId { get; }

    /// <summary>Gets an optional provider-specific preferred quality ID.</summary>
    public string? PreferredQualityId { get; }

    /// <summary>Gets whether the provider may choose another quality.</summary>
    public bool AllowQualityFallback { get; }

    /// <summary>Gets whether a preview is acceptable when full playback is not authorized.</summary>
    public bool AllowPreview { get; }
}

/// <summary>
/// Resolves qualities and backend-only stream leases. Implementations must honor provider access rules
/// and must not bypass DRM, subscription, payment, account, or regional restrictions.
/// </summary>
public interface IStreamResolutionCapability
{
    /// <summary>Gets qualities currently available to the authenticated user.</summary>
    Task<PlatformResult<IReadOnlyList<PlatformAudioQuality>>> GetAvailableQualitiesAsync(
        PlatformEntityId trackId,
        CancellationToken cancellationToken);

    /// <summary>Acquires a short-lived stream lease for trusted backend consumption.</summary>
    Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(
        PlatformPlaybackRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Compatibility-friendly name for providers that expose complete stream resolution. New capability
/// discovery should use <see cref="IStreamResolutionCapability"/> and <see cref="PlatformCapabilityKind.StreamResolution"/>.
/// </summary>
public interface IPlaybackCapability : IStreamResolutionCapability
{
}

/// <summary>Resolves short-lived, backend-only music-video streams.</summary>
public interface IPlatformVideoCapability
{
    /// <summary>Acquires a playable video lease without exposing its signed URL to UI code.</summary>
    Task<PlatformResult<PlatformVideoLease>> AcquireVideoAsync(
        PlatformVideoPlaybackRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Authenticates a provider without exposing tokens to the host UI.</summary>
public interface IAuthenticationCapability
{
    /// <summary>Gets sanitized account state.</summary>
    Task<PlatformResult<PlatformAuthenticationState>> GetAuthenticationStateAsync(
        CancellationToken cancellationToken);

    /// <summary>Begins an authentication flow.</summary>
    Task<PlatformResult<PlatformAuthenticationChallenge>> BeginAuthenticationAsync(
        PlatformAuthenticationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Completes or polls a previously created challenge.</summary>
    Task<PlatformResult<PlatformAuthenticationState>> CompleteAuthenticationAsync(
        PlatformAuthenticationCompletion completion,
        CancellationToken cancellationToken);

    /// <summary>Clears provider authentication from the scoped credential store.</summary>
    Task<PlatformResult<PlatformUnit>> SignOutAsync(CancellationToken cancellationToken);
}
