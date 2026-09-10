namespace Auralis.Localization;

/// <summary>
/// Small native-shell catalog used before the Web localization runtime is available. User metadata and
/// provider content must never be passed through this catalog.
/// </summary>
internal static class NativeText
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["splash.subtitle"] = "Your local music",
            ["startup.failure.title"] = "Startup failed",
            ["startup.webview.failure"] =
                "Auralis could not start WebView2. Make sure Microsoft Edge WebView2 Runtime is installed.",
            ["tray.tooltip"] = "Auralis — Local music",
            ["tray.show"] = "Show Auralis",
            ["tray.exit"] = "Exit",
            ["logs.unavailable"] = "Application logs are currently unavailable.",
            ["logs.open.failed"] = "The application log folder could not be opened.",
            ["audio.output.fallback"] =
                "The selected audio device settings could not be applied. Windows default output is still active."
        };

    private static readonly IReadOnlyDictionary<string, string> SimplifiedChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["splash.subtitle"] = "你的本地音乐",
            ["startup.failure.title"] = "启动失败",
            ["startup.webview.failure"] =
                "Auralis 无法启动 WebView2。请确认系统已安装 Microsoft Edge WebView2 Runtime。",
            ["tray.tooltip"] = "Auralis — 本地音乐",
            ["tray.show"] = "显示 Auralis",
            ["tray.exit"] = "退出",
            ["logs.unavailable"] = "应用日志当前不可用。",
            ["logs.open.failed"] = "无法打开应用日志文件夹。",
            ["audio.output.fallback"] = "当前音频设备设置无法应用，已继续使用 Windows 默认输出"
        };

    internal static string Get(string resolvedLanguage, string key)
    {
        var catalog = string.Equals(resolvedLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
        return catalog.TryGetValue(key, out var value) ? value : key;
    }
}
