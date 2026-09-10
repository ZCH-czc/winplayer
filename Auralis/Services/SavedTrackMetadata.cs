using Auralis.Platform.Abstractions;

namespace Auralis.Services;

// Display metadata can be recovered; identity may never be guessed from a title.
internal static class SavedTrackMetadata
{
    internal static PlatformTrack? Match(PlatformEntityId id, IEnumerable<PlatformTrack> tracks) =>
        tracks.Where(t => t.Id == id).OrderByDescending(t => t.ArtworkUrl is not null).FirstOrDefault();

    internal static PlatformTrack Fallback(SavedPlaylistEntry entry)
    {
        var id = new PlatformEntityId(entry.ProviderId!, entry.EntityId!);
        return new PlatformTrack
        {
            Id = id, Title = entry.Title,
            Artists = [new PlatformArtistReference(id, entry.Artist)],
            Album = new PlatformAlbumReference(id, entry.Album),
            Duration = TimeSpan.FromSeconds(entry.DurationSeconds),
            MusicVideo = entry.VideoId is { Length: > 0 } video
                ? new PlatformMusicVideoReference(new PlatformEntityId(id.ProviderId, video), entry.Title) : null
        };
    }
}
