using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Auralis.Services;

public sealed record LocalLyricsFileMatch(
    string Path,
    string MatchKind,
    int Confidence);

/// <summary>
/// Locates a sidecar LRC without relying on the current file-system case-sensitivity mode.
/// Exact names always win. More tolerant matches are accepted only when the best result is
/// unique, so an album folder containing multiple lyric editions cannot silently choose one.
/// </summary>
public static partial class LocalLyricsLocator
{
    private const int ExactOrdinalScore = 1000;
    private const int ExactCanonicalScore = 980;
    private const int NormalizedFileNameScore = 940;
    private const int NumberPrefixScore = 900;
    private const int VersionSuffixScore = 860;
    private const int MetadataTitleScore = 820;
    private const int MetadataCombinationScore = 790;

    public static LocalLyricsFileMatch? Find(
        string audioPath,
        string? title = null,
        string? artist = null)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var audioStem = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(audioStem))
        {
            return null;
        }

        string[] lyricFiles;
        try
        {
            lyricFiles = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".lrc", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (lyricFiles.Length == 0)
        {
            return null;
        }

        var titleKeys = BuildMetadataKeys(title, artist);
        var scored = lyricFiles
            .Select(path => Score(path, audioStem, titleKeys))
            .Where(match => match is not null)
            .Cast<LocalLyricsFileMatch>()
            .OrderByDescending(match => match.Confidence)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scored.Length == 0)
        {
            return null;
        }

        var bestScore = scored[0].Confidence;
        var best = scored.Where(match => match.Confidence == bestScore).ToArray();
        return best.Length == 1 ? best[0] : null;
    }

    private static LocalLyricsFileMatch? Score(
        string path,
        string audioStem,
        MetadataKeys metadataKeys)
    {
        var lyricStem = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(lyricStem, audioStem, StringComparison.Ordinal))
        {
            return new LocalLyricsFileMatch(path, "exact", ExactOrdinalScore);
        }

        if (string.Equals(
                CanonicalText(lyricStem),
                CanonicalText(audioStem),
                StringComparison.OrdinalIgnoreCase))
        {
            return new LocalLyricsFileMatch(path, "canonical", ExactCanonicalScore);
        }

        var lyricKey = MatchKey(lyricStem);
        var audioKey = MatchKey(audioStem);
        if (lyricKey.Length > 0 && lyricKey == audioKey)
        {
            return new LocalLyricsFileMatch(path, "normalized", NormalizedFileNameScore);
        }

        var lyricWithoutNumber = MatchKey(StripTrackNumberPrefix(lyricStem));
        var audioWithoutNumber = MatchKey(StripTrackNumberPrefix(audioStem));
        if (lyricWithoutNumber.Length > 0 && lyricWithoutNumber == audioWithoutNumber)
        {
            return new LocalLyricsFileMatch(path, "track-number", NumberPrefixScore);
        }

        var lyricWithoutSuffix = StripCompatibleSuffixes(lyricStem);
        var suffixKey = MatchKey(lyricWithoutSuffix);
        if (suffixKey.Length > 0 &&
            (suffixKey == audioKey || suffixKey == audioWithoutNumber ||
             MatchKey(StripTrackNumberPrefix(lyricWithoutSuffix)) == audioWithoutNumber))
        {
            return new LocalLyricsFileMatch(path, "edition-suffix", VersionSuffixScore);
        }

        if (metadataKeys.Title.Count > 0)
        {
            var lyricKeys = new[]
            {
                lyricKey,
                lyricWithoutNumber,
                suffixKey,
                MatchKey(StripTrackNumberPrefix(lyricWithoutSuffix))
            };
            if (lyricKeys.Any(key => key.Length > 0 && metadataKeys.Title.Contains(key)))
            {
                return new LocalLyricsFileMatch(path, "metadata-title", MetadataTitleScore);
            }

            if (lyricKeys.Any(key => key.Length > 0 && metadataKeys.Combinations.Contains(key)))
            {
                return new LocalLyricsFileMatch(path, "metadata-title-artist", MetadataCombinationScore);
            }
        }

        return null;
    }

    private static MetadataKeys BuildMetadataKeys(string? title, string? artist)
    {
        var titleKeys = new HashSet<string>(StringComparer.Ordinal);
        var combinationKeys = new HashSet<string>(StringComparer.Ordinal);
        var cleanTitle = CanonicalText(title ?? string.Empty);
        var cleanArtist = CanonicalText(artist ?? string.Empty);
        AddKey(titleKeys, cleanTitle);
        AddKey(titleKeys, StripTrackNumberPrefix(cleanTitle));
        AddKey(titleKeys, StripCompatibleSuffixes(cleanTitle));

        if (cleanTitle.Length > 0 && cleanArtist.Length > 0)
        {
            foreach (var separator in new[] { " - ", " – ", "_", " ", "·" })
            {
                AddKey(combinationKeys, cleanArtist + separator + cleanTitle);
                AddKey(combinationKeys, cleanTitle + separator + cleanArtist);
            }
            AddKey(combinationKeys, $"{cleanTitle} ({cleanArtist})");
            AddKey(combinationKeys, $"{cleanTitle} [{cleanArtist}]");
        }

        return new MetadataKeys(titleKeys, combinationKeys);
    }

    private static void AddKey(HashSet<string> keys, string value)
    {
        var key = MatchKey(value);
        if (key.Length > 0)
        {
            keys.Add(key);
        }
    }

    private static string StripTrackNumberPrefix(string value) =>
        TrackNumberPrefixPattern().Replace(value, string.Empty, 1).Trim();

    private static string StripCompatibleSuffixes(string value)
    {
        var result = CanonicalText(value);
        while (true)
        {
            var withoutDecorativeSuffix = DecorativeSuffixPattern().Replace(result, string.Empty).Trim();
            var withoutVersionSuffix = VersionSuffixPattern().Replace(withoutDecorativeSuffix, string.Empty).Trim();
            if (withoutVersionSuffix == result)
            {
                return result;
            }
            result = withoutVersionSuffix;
        }
    }

    private static string MatchKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in CanonicalText(value).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToUpperInvariant(rune));
            }
        }
        return builder.ToString();
    }

    private static string CanonicalText(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormKC).Trim();
        }
        catch (ArgumentException)
        {
            // Malformed UTF-16 file names should be ignored by the scorer instead of breaking a scan.
            return value.Trim();
        }
    }

    private sealed record MetadataKeys(HashSet<string> Title, HashSet<string> Combinations);

    [GeneratedRegex(
        @"^\s*(?:(?:disc|disk|cd)\s*)?\d{1,3}\s*(?:[.．。\-–—_、/\\]+\s*|\s+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackNumberPrefixPattern();

    [GeneratedRegex(
        @"(?:\s*[\[(（【]\s*(?:lrc|lyrics?|歌词|逐字|滚动|双语|翻译)\s*[\])）】])+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecorativeSuffixPattern();

    [GeneratedRegex(
        @"(?:[\s\-_.·]+|[\[(（【]\s*)(?:ver(?:sion)?|v)\s*[\s._-]*\d[\p{L}\p{N}\s._-]*(?:[\])）】])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixPattern();
}
