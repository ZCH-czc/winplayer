using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Auralis;

public partial class MainWindow
{
    private bool _micaRequested;
    private bool? _lastMaterialEnabled;
    private void ApplyMicaMaterial()
    {
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var enabled = _micaRequested && supported && !SystemParameters.HighContrast && _windowHandle != 0;
        if (_windowHandle != 0 && supported)
        {
            var backdrop = enabled ? 2 : 1;
            enabled &= DwmSetWindowAttribute(_windowHandle, 38, ref backdrop, sizeof(int)) >= 0;
        }
        var color = _nativeDarkTheme ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
        Background = Root.Background = enabled ? Brushes.Transparent : new SolidColorBrush(color);
        MaterialCaptionBacking.Background = new SolidColorBrush(color);
        MaterialCaptionBacking.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null && _lastMaterialEnabled != enabled)
        {
            var replacement = (WindowChrome)chrome.Clone();
            // Mica needs the client glass extended, otherwise transparent WPF paints black.
            // An opaque native caption backing keeps DWM glyphs from bleeding through our
            // transparent Web caption while the rest of the client uses the system material.
            replacement.GlassFrameThickness = new Thickness(enabled ? -1 : 0);
            WindowChrome.SetWindowChrome(this, replacement);
        }
        if (HwndSource.FromHwnd(_windowHandle)?.CompositionTarget is { } target)
            target.BackgroundColor = enabled ? Colors.Transparent : color;
        PlayerWebView.DefaultBackgroundColor = enabled ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        _lastMaterialEnabled = enabled;
        _ = ExecuteScriptAsync($"window.Auralis?.setWindowMaterialState?.({{mica:{(enabled ? "true" : "false")}}})");
    }
}
