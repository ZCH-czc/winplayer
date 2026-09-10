using System.Globalization;
using System.Text.RegularExpressions;

namespace Auralis.Services;

public sealed record LyricLine(double? TimeSeconds, string Text);

public static class LyricsParser
{
    private static readonly Regex TimestampPattern = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{1,2}(?:[.:]\d{1,3})?)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MetadataPattern = new(
        @"^\[(?:ar|ti|al|by|re|ve|length|offset):.*\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LyricLine> Parse(string lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return [];
        }

        var offsetSeconds = 0d;
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();

        foreach (var rawLine in lyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
            {
                var value = line[8..^1];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetMilliseconds))
                {
                    offsetSeconds = offsetMilliseconds / 1000d;
                }
                continue;
            }

            if (MetadataPattern.IsMatch(line))
            {
                continue;
            }

            var matches = TimestampPattern.Matches(line);
            var text = TimestampPattern.Replace(line, string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (matches.Count == 0)
            {
                plain.Add(new LyricLine(null, text));
                continue;
            }

            foreach (Match match in matches)
            {
                if (!double.TryParse(match.Groups["minutes"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                    !double.TryParse(match.Groups["seconds"].Value.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    continue;
                }

                timed.Add(new LyricLine(Math.Max(0, minutes * 60 + seconds + offsetSeconds), text));
            }
        }

        return timed.Count > 0
            ? timed.OrderBy(line => line.TimeSeconds).ToArray()
            : plain;
    }
}
