namespace Auralis;

/// <summary>Normalizes the user-facing desktop-lyric mask brightness into a WPF alpha value.</summary>
internal static class DesktopLyricsMaskStyle
{
    internal const double MinimumBrightness = 10;
    internal const double MaximumBrightness = 90;
    internal const double DefaultBrightness = 36;

    internal static double NormalizeBrightness(double brightness) =>
        double.IsFinite(brightness)
            ? Math.Clamp(brightness, MinimumBrightness, MaximumBrightness)
            : DefaultBrightness;

    internal static byte ResolveAlpha(double brightness) =>
        (byte)Math.Round(NormalizeBrightness(brightness) / 100d * byte.MaxValue);
}
