namespace Auralis.Platform.Abstractions;

/// <summary>Enumerates capabilities a provider can advertise.</summary>
public enum PlatformCapabilityKind
{
    /// <summary>Track search via <see cref="ITrackSearchCapability"/>.</summary>
    TrackSearch,

    /// <summary>Query suggestions via <see cref="ISearchSuggestionsCapability"/>.</summary>
    SearchSuggestions,

    /// <summary>Hot search terms via <see cref="IHotSearchCapability"/>.</summary>
    HotSearch,

    /// <summary>Album search via <see cref="IAlbumSearchCapability"/>.</summary>
    AlbumSearch,

    /// <summary>Album metadata and tracks via <see cref="IAlbumDetailsCapability"/>.</summary>
    AlbumDetails,

    /// <summary>Provider-curated playlist browsing via <see cref="IPlaylistBrowseCapability"/>.</summary>
    PlaylistBrowse,

    /// <summary>Playlist search via <see cref="IPlaylistSearchCapability"/>.</summary>
    PlaylistSearch,

    /// <summary>Playlist metadata and tracks via <see cref="IPlaylistDetailsCapability"/>.</summary>
    PlaylistDetails,

    /// <summary>Charts via <see cref="IChartCapability"/>.</summary>
    Charts,

    /// <summary>Artist search and metadata via <see cref="IArtistDetailsCapability"/>.</summary>
    ArtistDetails,

    /// <summary>Artist tracks via <see cref="IArtistTracksCapability"/>.</summary>
    ArtistTracks,

    /// <summary>Artist albums via <see cref="IArtistAlbumsCapability"/>.</summary>
    ArtistAlbums,

    /// <summary>Backend artwork resolution via <see cref="IArtworkCapability"/>.</summary>
    Artwork,

    /// <summary>Lyrics via <see cref="ILyricsCapability"/>.</summary>
    Lyrics,

    /// <summary>Comment browsing via <see cref="ICommentsCapability"/>.</summary>
    Comments,

    /// <summary>Playback resolution via <see cref="IStreamResolutionCapability"/>.</summary>
    StreamResolution,

    /// <summary>Music-video playback resolution via <see cref="IPlatformVideoCapability"/>.</summary>
    VideoResolution,

    /// <summary>Authentication via <see cref="IAuthenticationCapability"/>.</summary>
    Authentication,

    /// <summary>Parts and timed comments via <see cref="IPlatformMediaExtrasCapability"/>.</summary>
    MediaExtras,

    /// <summary>Exact saved track restoration via <see cref="ITrackDetailsCapability"/>.</summary>
    TrackDetails,

    /// <summary>Isolated desktop login via <see cref="IPlatformNativeLoginCapability"/>.</summary>
    NativeLogin,

    /// <summary>Opt-in local-song lyric matching via <see cref="IPlatformLyricsLookupCapability"/>.</summary>
    LyricsLookup
}

/// <summary>Describes one independently addressable online provider.</summary>
public sealed record PlatformProviderDescriptor
{
    /// <summary>Creates a provider descriptor.</summary>
    public PlatformProviderDescriptor(
        string id,
        string displayName,
        Version version,
        IReadOnlyCollection<PlatformCapabilityKind> capabilities,
        Uri? website = null)
    {
        Id = PlatformIdRules.EnsureScopedId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Capabilities = capabilities?.Distinct().ToArray()
            ?? throw new ArgumentNullException(nameof(capabilities));
        Website = website;
    }

    /// <summary>Gets the stable provider ID used in every <see cref="PlatformEntityId"/> it owns.</summary>
    public string Id { get; }

    /// <summary>Gets the provider display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the provider implementation version.</summary>
    public Version Version { get; }

    /// <summary>
    /// Gets advertised capabilities. The host should also verify that the provider implements each
    /// corresponding capability interface before calling it.
    /// </summary>
    public IReadOnlyCollection<PlatformCapabilityKind> Capabilities { get; }

    /// <summary>Gets an optional public provider website.</summary>
    public Uri? Website { get; }
}

/// <summary>Describes an in-process Auralis platform plugin and its compatible host range.</summary>
public sealed record PlatformPluginDescriptor
{
    /// <summary>Creates a plugin descriptor.</summary>
    public PlatformPluginDescriptor(
        string id,
        string displayName,
        Version version,
        int minimumHostApiVersion,
        int maximumHostApiVersion)
    {
        Id = PlatformIdRules.EnsureScopedId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (minimumHostApiVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumHostApiVersion));
        }

        if (maximumHostApiVersion < minimumHostApiVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHostApiVersion));
        }

        DisplayName = displayName;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        MinimumHostApiVersion = minimumHostApiVersion;
        MaximumHostApiVersion = maximumHostApiVersion;
    }

    /// <summary>Gets the stable plugin ID used to scope host services.</summary>
    public string Id { get; }

    /// <summary>Gets the plugin display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the plugin implementation version.</summary>
    public Version Version { get; }

    /// <summary>Gets the oldest supported host API.</summary>
    public int MinimumHostApiVersion { get; }

    /// <summary>Gets the newest supported host API.</summary>
    public int MaximumHostApiVersion { get; }

    /// <summary>Gets whether this descriptor supports the current host API.</summary>
    public bool IsCompatibleWithCurrentHost =>
        PlatformContract.IsCompatible(MinimumHostApiVersion, MaximumHostApiVersion);
}

/// <summary>
/// Base lifecycle for a single provider. Providers expose optional functionality only by implementing
/// the corresponding capability interfaces; hosts must not assume every provider supports every feature.
/// </summary>
public interface IPlatformProvider : IAsyncDisposable
{
    /// <summary>Gets immutable provider metadata.</summary>
    PlatformProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Initializes provider state without changing local-library state. Implementations should not perform
    /// user-visible network work until explicitly asked for an online capability.
    /// </summary>
    ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Entry point implemented once by an optional online platform plugin assembly. Plugins run in-process and
/// therefore must be trusted; this interface is a compatibility/isolation boundary, not a code sandbox.
/// A plugin must not mutate the Auralis local library, local playback queue, or application settings directly.
/// </summary>
public interface IAuralisPlatformPlugin : IAsyncDisposable
{
    /// <summary>Gets immutable plugin and host-compatibility metadata.</summary>
    PlatformPluginDescriptor Descriptor { get; }

    /// <summary>
    /// Creates providers using only plugin-scoped host services. The context's <see cref="PlatformHostContext.PluginId"/>
    /// must match <see cref="PlatformPluginDescriptor.Id"/>; implementations should reject a mismatch. A successful
    /// result transfers lifetime ownership of every returned provider to the host. The host disposes providers before
    /// disposing the plugin entry point, so the plugin must not dispose the same provider instances a second time.
    /// </summary>
    ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(
        PlatformHostContext context,
        CancellationToken cancellationToken);
}
