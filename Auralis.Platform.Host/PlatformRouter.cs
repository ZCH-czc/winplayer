using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>
/// Routes online calls to a provider only when its manifest advertises the requested capability and
/// its runtime type implements the corresponding contract. Local-library code is never part of this path.
/// </summary>
public sealed class PlatformRouter
{
    private readonly PlatformPluginHost _host;
    private readonly TimeSpan _defaultTimeout;

    internal PlatformRouter(PlatformPluginHost host, TimeSpan defaultTimeout)
    {
        _host = host;
        _defaultTimeout = defaultTimeout;
    }

    /// <summary>
    /// Routes a fine-grained capability call. Unknown capability interfaces and capabilities omitted from
    /// the manifest fail before plugin activation.
    /// </summary>
    public async Task<PlatformResult<TResult>> RouteAsync<TCapability, TResult>(
        string providerId,
        Func<TCapability, CancellationToken, Task<PlatformResult<TResult>>> operation,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
        where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        var effectiveTimeout = timeout ?? _defaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The platform operation timeout must be greater than zero and no longer than ten minutes.");
        }

        if (!PlatformCapabilityMap.TryGetKind(typeof(TCapability), out var capabilityKind))
        {
            return PlatformResult<TResult>.Failure(
                PlatformErrorCode.Unsupported,
                $"Capability contract '{typeof(TCapability).Name}' is not routable by this host.");
        }

        if (!_host.TryBeginOperation(out var lease))
        {
            return PlatformResult<TResult>.Failure(
                PlatformErrorCode.ServiceUnavailable,
                "The platform plugin host is shutting down.");
        }

        using (lease)
        {
            try
            {
                await _host.DiscoverForOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PlatformResult<TResult>.Failure(
                    PlatformErrorCode.Cancelled,
                    "The platform operation was cancelled by the caller.");
            }
            catch (Exception exception)
            {
                _host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.ManifestReadFailed,
                    PlatformPluginDiagnosticSeverity.Error,
                    "Platform discovery failed unexpectedly while routing a request.",
                    providerId: providerId,
                    exception: exception));
                return PlatformResult<TResult>.Failure(
                    PlatformErrorCode.ConfigurationRequired,
                    "Installed online platform plugins could not be discovered.");
            }

            if (!_host.TryGetProviderManifest(providerId, out var manifest) || manifest is null)
            {
                return PlatformResult<TResult>.Failure(
                    PlatformErrorCode.NotFound,
                    $"Online provider '{providerId}' is not installed or was rejected during discovery.");
            }

            if (!manifest.Capabilities.Contains(capabilityKind))
            {
                return PlatformResult<TResult>.Failure(
                    PlatformErrorCode.Unsupported,
                    $"Provider '{providerId}' does not advertise {capabilityKind}.");
            }

            var providerResult = await _host.GetProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
            if (!providerResult.IsSuccess)
            {
                return PlatformResult<TResult>.Failure(providerResult.Error!);
            }

            if (providerResult.Value is not TCapability capability)
            {
                _host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.CapabilityMismatch,
                    PlatformPluginDiagnosticSeverity.Error,
                    "A provider advertised a capability but did not implement its shared contract interface.",
                    providerId: providerId));
                return PlatformResult<TResult>.Failure(
                    PlatformErrorCode.Unsupported,
                    $"Provider '{providerId}' has an invalid {capabilityKind} implementation.");
            }

            return await PlatformPluginInvocation.InvokeAsync(
                token => operation(capability, token),
                effectiveTimeout,
                cancellationToken,
                exception => _host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.PluginOperationFailed,
                    PlatformPluginDiagnosticSeverity.Error,
                    "A provider capability threw; the exception was isolated and converted to PlatformResult.",
                    providerId: providerId,
                    exception: exception)),
                "The online provider failed while handling this operation.").ConfigureAwait(false);
        }
    }

    public Task<PlatformResult<PlatformPage<PlatformTrack>>> SearchTracksAsync(
        string providerId,
        PlatformSearchRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<ITrackSearchCapability, PlatformPage<PlatformTrack>>(
            providerId,
            request,
            (capability, token) => capability.SearchTracksAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<IReadOnlyList<PlatformSearchSuggestion>>> GetSearchSuggestionsAsync(
        string providerId,
        PlatformSearchSuggestionRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<ISearchSuggestionsCapability, IReadOnlyList<PlatformSearchSuggestion>>(
            providerId,
            request,
            (capability, token) => capability.GetSearchSuggestionsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformHotSearchItem>>> GetHotSearchesAsync(
        string providerId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IHotSearchCapability, PlatformPage<PlatformHotSearchItem>>(
            providerId,
            page,
            (capability, token) => capability.GetHotSearchesAsync(page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformAlbum>>> SearchAlbumsAsync(
        string providerId,
        PlatformSearchRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IAlbumSearchCapability, PlatformPage<PlatformAlbum>>(
            providerId,
            request,
            (capability, token) => capability.SearchAlbumsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformAlbum>> GetAlbumAsync(
        string providerId,
        PlatformEntityId albumId,
        CancellationToken cancellationToken = default) =>
        RouteEntityAsync<IAlbumDetailsCapability, PlatformAlbum>(
            providerId,
            albumId,
            (capability, token) => capability.GetAlbumAsync(albumId, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformTrack>>> GetAlbumTracksAsync(
        string providerId,
        PlatformEntityId albumId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IAlbumDetailsCapability, PlatformPage<PlatformTrack>>(
            providerId,
            albumId,
            page,
            (capability, token) => capability.GetAlbumTracksAsync(albumId, page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformArtist>>> SearchArtistsAsync(
        string providerId,
        PlatformSearchRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IArtistDetailsCapability, PlatformPage<PlatformArtist>>(
            providerId,
            request,
            (capability, token) => capability.SearchArtistsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformArtist>> GetArtistAsync(
        string providerId,
        PlatformEntityId artistId,
        CancellationToken cancellationToken = default) =>
        RouteEntityAsync<IArtistDetailsCapability, PlatformArtist>(
            providerId,
            artistId,
            (capability, token) => capability.GetArtistAsync(artistId, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformAlbum>>> GetArtistAlbumsAsync(
        string providerId,
        PlatformEntityId artistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IArtistAlbumsCapability, PlatformPage<PlatformAlbum>>(
            providerId,
            artistId,
            page,
            (capability, token) => capability.GetArtistAlbumsAsync(artistId, page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformTrack>>> GetArtistTopTracksAsync(
        string providerId,
        PlatformEntityId artistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IArtistTracksCapability, PlatformPage<PlatformTrack>>(
            providerId,
            artistId,
            page,
            (capability, token) => capability.GetArtistTopTracksAsync(artistId, page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformPlaylist>>> BrowsePlaylistsAsync(
        string providerId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IPlaylistBrowseCapability, PlatformPage<PlatformPlaylist>>(
            providerId,
            page,
            (capability, token) => capability.BrowsePlaylistsAsync(page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformPlaylist>>> SearchPlaylistsAsync(
        string providerId,
        PlatformSearchRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IPlaylistSearchCapability, PlatformPage<PlatformPlaylist>>(
            providerId,
            request,
            (capability, token) => capability.SearchPlaylistsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPlaylist>> GetPlaylistAsync(
        string providerId,
        PlatformEntityId playlistId,
        CancellationToken cancellationToken = default) =>
        RouteEntityAsync<IPlaylistDetailsCapability, PlatformPlaylist>(
            providerId,
            playlistId,
            (capability, token) => capability.GetPlaylistAsync(playlistId, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformTrack>>> GetPlaylistTracksAsync(
        string providerId,
        PlatformEntityId playlistId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IPlaylistDetailsCapability, PlatformPage<PlatformTrack>>(
            providerId,
            playlistId,
            page,
            (capability, token) => capability.GetPlaylistTracksAsync(playlistId, page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformChart>>> GetChartsAsync(
        string providerId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IChartCapability, PlatformPage<PlatformChart>>(
            providerId,
            page,
            (capability, token) => capability.GetChartsAsync(page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformTrack>>> GetChartTracksAsync(
        string providerId,
        PlatformEntityId chartId,
        PlatformPageRequest page,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IChartCapability, PlatformPage<PlatformTrack>>(
            providerId,
            chartId,
            page,
            (capability, token) => capability.GetChartTracksAsync(chartId, page, token),
            cancellationToken);

    public Task<PlatformResult<PlatformLyrics>> GetLyricsAsync(
        string providerId,
        PlatformLyricsRequest request,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<ILyricsCapability, PlatformLyrics>(
            providerId,
            request.TrackId,
            request,
            (capability, token) => capability.GetLyricsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformArtworkResource>> AcquireArtworkAsync(
        string providerId,
        PlatformArtworkRequest request,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IArtworkCapability, PlatformArtworkResource>(
            providerId,
            request.EntityId,
            request,
            (capability, token) => capability.AcquireArtworkAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentsAsync(
        string providerId,
        PlatformCommentsRequest request,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<ICommentsCapability, PlatformPage<PlatformComment>>(
            providerId,
            request.EntityId,
            request,
            (capability, token) => capability.GetCommentsAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<IReadOnlyList<PlatformAudioQuality>>> GetAvailableQualitiesAsync(
        string providerId,
        PlatformEntityId trackId,
        CancellationToken cancellationToken = default) =>
        RouteEntityAsync<IStreamResolutionCapability, IReadOnlyList<PlatformAudioQuality>>(
            providerId,
            trackId,
            (capability, token) => capability.GetAvailableQualitiesAsync(trackId, token),
            cancellationToken);

    public Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(
        string providerId,
        PlatformPlaybackRequest request,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IStreamResolutionCapability, PlatformStreamLease>(
            providerId,
            request.TrackId,
            request,
            (capability, token) => capability.AcquireStreamAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformVideoLease>> AcquireVideoAsync(
        string providerId,
        PlatformVideoPlaybackRequest request,
        CancellationToken cancellationToken = default) =>
        RouteEntityRequiredAsync<IPlatformVideoCapability, PlatformVideoLease>(
            providerId,
            request.VideoId,
            request,
            (capability, token) => capability.AcquireVideoAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformAuthenticationState>> GetAuthenticationStateAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        RouteAsync<IAuthenticationCapability, PlatformAuthenticationState>(
            providerId,
            (capability, token) => capability.GetAuthenticationStateAsync(token),
            cancellationToken);

    public Task<PlatformResult<PlatformAuthenticationChallenge>> BeginAuthenticationAsync(
        string providerId,
        PlatformAuthenticationRequest request,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IAuthenticationCapability, PlatformAuthenticationChallenge>(
            providerId,
            request,
            (capability, token) => capability.BeginAuthenticationAsync(request, token),
            cancellationToken);

    public Task<PlatformResult<PlatformAuthenticationState>> CompleteAuthenticationAsync(
        string providerId,
        PlatformAuthenticationCompletion completion,
        CancellationToken cancellationToken = default) =>
        RouteRequiredAsync<IAuthenticationCapability, PlatformAuthenticationState>(
            providerId,
            completion,
            (capability, token) => capability.CompleteAuthenticationAsync(completion, token),
            cancellationToken);

    public Task<PlatformResult<PlatformUnit>> SignOutAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        RouteAsync<IAuthenticationCapability, PlatformUnit>(
            providerId,
            (capability, token) => capability.SignOutAsync(token),
            cancellationToken);

    private Task<PlatformResult<TResult>> RouteRequiredAsync<TCapability, TResult>(
        string providerId,
        object? request,
        Func<TCapability, CancellationToken, Task<PlatformResult<TResult>>> operation,
        CancellationToken cancellationToken)
        where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return RouteAsync(providerId, operation, cancellationToken);
    }

    private Task<PlatformResult<TResult>> RouteEntityAsync<TCapability, TResult>(
        string providerId,
        PlatformEntityId entityId,
        Func<TCapability, CancellationToken, Task<PlatformResult<TResult>>> operation,
        CancellationToken cancellationToken)
        where TCapability : class
    {
        if (!entityId.IsForProvider(providerId))
        {
            return Task.FromResult(PlatformResult<TResult>.Failure(
                PlatformErrorCode.InvalidRequest,
                "The entity ID belongs to a different online provider."));
        }

        return RouteAsync(providerId, operation, cancellationToken);
    }

    private Task<PlatformResult<TResult>> RouteEntityRequiredAsync<TCapability, TResult>(
        string providerId,
        PlatformEntityId entityId,
        object? request,
        Func<TCapability, CancellationToken, Task<PlatformResult<TResult>>> operation,
        CancellationToken cancellationToken)
        where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return RouteEntityAsync(providerId, entityId, operation, cancellationToken);
    }
}
