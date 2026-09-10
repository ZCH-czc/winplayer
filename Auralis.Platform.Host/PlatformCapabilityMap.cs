using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

internal static class PlatformCapabilityMap
{
    private static readonly IReadOnlyDictionary<Type, PlatformCapabilityKind> Types =
        new Dictionary<Type, PlatformCapabilityKind>
        {
            [typeof(IPlatformLyricsLookupCapability)] = PlatformCapabilityKind.LyricsLookup,
            [typeof(ITrackDetailsCapability)] = PlatformCapabilityKind.TrackDetails,
            [typeof(IPlatformNativeLoginCapability)] = PlatformCapabilityKind.NativeLogin,
            [typeof(ITrackSearchCapability)] = PlatformCapabilityKind.TrackSearch,
            [typeof(ISearchSuggestionsCapability)] = PlatformCapabilityKind.SearchSuggestions,
            [typeof(IHotSearchCapability)] = PlatformCapabilityKind.HotSearch,
            [typeof(IAlbumSearchCapability)] = PlatformCapabilityKind.AlbumSearch,
            [typeof(IAlbumDetailsCapability)] = PlatformCapabilityKind.AlbumDetails,
            [typeof(IPlaylistBrowseCapability)] = PlatformCapabilityKind.PlaylistBrowse,
            [typeof(IPlaylistSearchCapability)] = PlatformCapabilityKind.PlaylistSearch,
            [typeof(IPlaylistDetailsCapability)] = PlatformCapabilityKind.PlaylistDetails,
            [typeof(IChartCapability)] = PlatformCapabilityKind.Charts,
            [typeof(IArtistDetailsCapability)] = PlatformCapabilityKind.ArtistDetails,
            [typeof(IArtistTracksCapability)] = PlatformCapabilityKind.ArtistTracks,
            [typeof(IArtistAlbumsCapability)] = PlatformCapabilityKind.ArtistAlbums,
            [typeof(IArtworkCapability)] = PlatformCapabilityKind.Artwork,
            [typeof(ILyricsCapability)] = PlatformCapabilityKind.Lyrics,
            [typeof(ICommentsCapability)] = PlatformCapabilityKind.Comments,
            [typeof(IPlatformMediaExtrasCapability)] = PlatformCapabilityKind.MediaExtras,
            [typeof(IStreamResolutionCapability)] = PlatformCapabilityKind.StreamResolution,
            [typeof(IPlatformVideoCapability)] = PlatformCapabilityKind.VideoResolution,
            [typeof(IAuthenticationCapability)] = PlatformCapabilityKind.Authentication
        };

    internal static bool TryGetKind(Type capabilityType, out PlatformCapabilityKind kind) =>
        Types.TryGetValue(capabilityType, out kind);

    internal static bool Implements(IPlatformProvider provider, PlatformCapabilityKind kind) => kind switch
    {
        PlatformCapabilityKind.LyricsLookup => provider is IPlatformLyricsLookupCapability,
        PlatformCapabilityKind.TrackDetails => provider is ITrackDetailsCapability,
        PlatformCapabilityKind.NativeLogin => provider is IPlatformNativeLoginCapability,
        PlatformCapabilityKind.TrackSearch => provider is ITrackSearchCapability,
        PlatformCapabilityKind.SearchSuggestions => provider is ISearchSuggestionsCapability,
        PlatformCapabilityKind.HotSearch => provider is IHotSearchCapability,
        PlatformCapabilityKind.AlbumSearch => provider is IAlbumSearchCapability,
        PlatformCapabilityKind.AlbumDetails => provider is IAlbumDetailsCapability,
        PlatformCapabilityKind.PlaylistBrowse => provider is IPlaylistBrowseCapability,
        PlatformCapabilityKind.PlaylistSearch => provider is IPlaylistSearchCapability,
        PlatformCapabilityKind.PlaylistDetails => provider is IPlaylistDetailsCapability,
        PlatformCapabilityKind.Charts => provider is IChartCapability,
        PlatformCapabilityKind.ArtistDetails => provider is IArtistDetailsCapability,
        PlatformCapabilityKind.ArtistTracks => provider is IArtistTracksCapability,
        PlatformCapabilityKind.ArtistAlbums => provider is IArtistAlbumsCapability,
        PlatformCapabilityKind.Artwork => provider is IArtworkCapability,
        PlatformCapabilityKind.Lyrics => provider is ILyricsCapability,
        PlatformCapabilityKind.Comments => provider is ICommentsCapability,
        PlatformCapabilityKind.MediaExtras => provider is IPlatformMediaExtrasCapability,
        PlatformCapabilityKind.StreamResolution => provider is IStreamResolutionCapability,
        PlatformCapabilityKind.VideoResolution => provider is IPlatformVideoCapability,
        PlatformCapabilityKind.Authentication => provider is IAuthenticationCapability,
        _ => false
    };
}
