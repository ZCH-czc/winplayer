using Auralis.Services;

namespace Auralis;

public static class TaskbarLyricProjection
{
    public static string ResolvePrimaryText(LyricsResponse? lyrics, double positionSeconds, string fallback)
    {
        if (lyrics is null || lyrics.Instrumental || lyrics.Lines.Count == 0)
        {
            return fallback;
        }

        if (!lyrics.IsSynced)
        {
            return lyrics.Lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line.Text))?.Text ?? fallback;
        }

        var activeIndex = -1;
        for (var index = 0; index < lyrics.Lines.Count; index++)
        {
            var timestamp = lyrics.Lines[index].TimeSeconds;
            if (!timestamp.HasValue)
            {
                continue;
            }
            if (timestamp.Value > positionSeconds + .02)
            {
                break;
            }
            activeIndex = index;
        }

        if (activeIndex < 0)
        {
            return fallback;
        }

        // Same-timestamp rows are commonly translation pairs. The compact taskbar surface keeps
        // the first/original row; desktop lyrics can continue to display both rows.
        var activeTime = lyrics.Lines[activeIndex].TimeSeconds;
        while (activeIndex > 0 && activeTime.HasValue &&
               Math.Abs((lyrics.Lines[activeIndex - 1].TimeSeconds ?? double.MinValue) - activeTime.Value) < .01)
        {
            activeIndex--;
        }

        return string.IsNullOrWhiteSpace(lyrics.Lines[activeIndex].Text)
            ? fallback
            : lyrics.Lines[activeIndex].Text;
    }
}
