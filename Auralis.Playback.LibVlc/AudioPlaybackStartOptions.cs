using System.Globalization;

namespace Auralis.Services;

internal static class AudioPlaybackStartOptions
{
    internal static string[] Create(int bufferMilliseconds, double rate, bool autoplay, double positionSeconds)
    {
        var buffer = Math.Clamp(bufferMilliseconds, 100, 5000);
        var speed = double.IsFinite(rate) ? Math.Clamp(rate, .5, 2) : 1;
        var position = double.IsFinite(positionSeconds) ? Math.Max(0, positionSeconds) : 0;
        var options = new List<string> { $":file-caching={buffer}", $":network-caching={buffer}",
            $":live-caching={buffer}", $":rate={speed.ToString(CultureInfo.InvariantCulture)}" };
        if (position > 0) options.Add($":start-time={position.ToString(CultureInfo.InvariantCulture)}");
        if (!autoplay) options.Add(":start-paused");
        return options.ToArray();
    }
}
