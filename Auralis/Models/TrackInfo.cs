namespace Auralis.Models;

public sealed record TrackInfo(
    string Id,
    string Title,
    string Artist,
    string Album,
    string FileName,
    string Extension,
    string? CoverUrl,
    long Size,
    double? DurationSeconds,
    DateTime DateAdded);

public sealed record StoredTrack(string Path, DateTime DateAdded);
