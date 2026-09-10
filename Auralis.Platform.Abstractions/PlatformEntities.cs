namespace Auralis.Platform.Abstractions;

/// <summary>Describes whether a track can currently be resolved for playback.</summary>
public enum PlatformTrackAvailability
{
    /// <summary>The provider did not report availability.</summary>
    Unknown,

    /// <summary>The complete track is available to the current user.</summary>
    Available,

    /// <summary>Only a preview is available.</summary>
    PreviewOnly,

    /// <summary>The track is unavailable.</summary>
    Unavailable
}

/// <summary>A compact online artist reference embedded in other online entities.</summary>
public sealed record PlatformArtistReference(
    PlatformEntityId Id,
    string Name,
    Uri? ArtworkUrl = null);

/// <summary>A compact online album reference embedded in tracks.</summary>
public sealed record PlatformAlbumReference(
    PlatformEntityId Id,
    string Title,
    Uri? ArtworkUrl = null);

/// <summary>A compact music-video reference attached to an online track.</summary>
public sealed record PlatformMusicVideoReference(
    PlatformEntityId Id,
    string Title,
    TimeSpan? Duration = null,
    Uri? ArtworkUrl = null);

/// <summary>
/// Represents an online track independently from Auralis' local-library track model. Hosts must not
/// insert this object into the local library or treat its opaque ID as a file path.
/// </summary>
public sealed record PlatformTrack
{
    /// <summary>Gets the provider-qualified track ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the display title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets provider-qualified artist references.</summary>
    public IReadOnlyList<PlatformArtistReference> Artists { get; init; } = Array.Empty<PlatformArtistReference>();

    /// <summary>Gets an optional provider-qualified album reference.</summary>
    public PlatformAlbumReference? Album { get; init; }

    /// <summary>Gets the provider-reported duration.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Gets an artwork URL suitable for the host's image pipeline.</summary>
    public Uri? ArtworkUrl { get; init; }

    /// <summary>Gets current playback availability.</summary>
    public PlatformTrackAvailability Availability { get; init; } = PlatformTrackAvailability.Unknown;

    /// <summary>Gets whether the provider marks the track as explicit.</summary>
    public bool IsExplicit { get; init; }

    /// <summary>Gets the provider music video associated with this track, when one is available.</summary>
    public PlatformMusicVideoReference? MusicVideo { get; init; }
}

/// <summary>Represents an online album.</summary>
public sealed record PlatformAlbum
{
    /// <summary>Gets the provider-qualified album ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the album title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets album artists.</summary>
    public IReadOnlyList<PlatformArtistReference> Artists { get; init; } = Array.Empty<PlatformArtistReference>();

    /// <summary>Gets the release date when supplied by the provider.</summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>Gets the provider-reported track count.</summary>
    public int? TrackCount { get; init; }

    /// <summary>Gets an artwork URL.</summary>
    public Uri? ArtworkUrl { get; init; }

    /// <summary>Gets an optional provider description.</summary>
    public string? Description { get; init; }
}

/// <summary>Represents an online artist.</summary>
public sealed record PlatformArtist
{
    /// <summary>Gets the provider-qualified artist ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the artist's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets an artwork URL.</summary>
    public Uri? ArtworkUrl { get; init; }

    /// <summary>Gets an optional provider biography.</summary>
    public string? Biography { get; init; }
}

/// <summary>Represents an online playlist.</summary>
public sealed record PlatformPlaylist
{
    /// <summary>Gets the provider-qualified playlist ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the playlist title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the provider-reported owner name.</summary>
    public string? OwnerName { get; init; }

    /// <summary>Gets the provider-reported track count.</summary>
    public int? TrackCount { get; init; }

    /// <summary>Gets an artwork URL.</summary>
    public Uri? ArtworkUrl { get; init; }

    /// <summary>Gets an optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets whether the current authenticated user may edit the playlist.</summary>
    public bool IsEditable { get; init; }
}

/// <summary>Represents a provider chart or ranking.</summary>
public sealed record PlatformChart
{
    /// <summary>Gets the provider-qualified chart ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the chart title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets an optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets an artwork URL.</summary>
    public Uri? ArtworkUrl { get; init; }

    /// <summary>Gets the time at which the provider last updated the chart.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Represents one lyric line and optional synchronized timing.</summary>
public sealed record PlatformLyricLine
{
    /// <summary>Gets the line text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the line start time, or <see langword="null"/> for unsynchronized lyrics.</summary>
    public TimeSpan? Start { get; init; }

    /// <summary>Gets an optional end time.</summary>
    public TimeSpan? End { get; init; }

    /// <summary>Gets an optional translated rendering of the same line.</summary>
    public string? Translation { get; init; }
}

/// <summary>Represents online lyric data for a track.</summary>
public sealed record PlatformLyrics
{
    /// <summary>Gets the provider-qualified track ID.</summary>
    public required PlatformEntityId TrackId { get; init; }

    /// <summary>Gets the non-sensitive display name of the lyric source.</summary>
    public required string SourceName { get; init; }

    /// <summary>Gets an optional BCP-47 language tag.</summary>
    public string? Language { get; init; }

    /// <summary>Gets plain lyrics when the provider supplies them.</summary>
    public string? PlainText { get; init; }

    /// <summary>Gets parsed lyric lines in chronological order.</summary>
    public IReadOnlyList<PlatformLyricLine> Lines { get; init; } = Array.Empty<PlatformLyricLine>();

    /// <summary>Gets whether at least one line contains timing information.</summary>
    public bool IsSynchronized => Lines.Any(static line => line.Start.HasValue);
}
