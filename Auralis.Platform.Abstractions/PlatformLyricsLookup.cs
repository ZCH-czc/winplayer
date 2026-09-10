namespace Auralis.Platform.Abstractions;

/// <summary>Opt-in lyric lookup metadata. Contains no local path or library ID.</summary>
public sealed record PlatformLyricsLookupRequest(string Title, string Artist, string Album, double? DurationSeconds);
/// <summary>Public lyric content returned to the host parser/cache.</summary>
public sealed record PlatformLyricsLookupResult(string Source, string Text, bool Instrumental = false);
/// <summary>Matches lyrics for a local song only after explicit online consent.</summary>
public interface IPlatformLyricsLookupCapability
{
    /// <summary>Returns content or a typed not-found/provider error.</summary>
    Task<PlatformResult<PlatformLyricsLookupResult>> LookupAsync(PlatformLyricsLookupRequest request, CancellationToken cancellationToken);
}
