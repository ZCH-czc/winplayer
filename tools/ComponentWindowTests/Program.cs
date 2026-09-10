using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Auralis.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Auralis;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--check-isolation" })) return IsolationChecks.Run();
        if (args.Length == 0) args = ["--new"];
        // No production App, MainWindow constructor, single-instance IPC, accounts or stores.
        if (!(args.SequenceEqual(new[] { "--new" }) ||
              (args.Length == 2 && args[0] == "--resume" && Regex.IsMatch(args[1], "\\A[a-f0-9]{32}\\z")))) return 2;
        var id = args.Length == 1 ? Guid.NewGuid().ToString("N") : args[1];
        var root = Path.Combine(AppContext.BaseDirectory, "acceptance", id);
        try
        {
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return 2;
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network) return 2;
            var marker = Path.Combine(root, "fixture.txt");
            if (args.Length == 1)
            {
                if (Directory.Exists(root)) return 2;
                Directory.CreateDirectory(root);
                File.WriteAllText(marker, "Auralis isolated component acceptance v1");
            }
            else if (!File.Exists(marker) || new FileInfo(marker).Length > 100 ||
                     (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                     File.ReadAllText(marker) != "Auralis isolated component acceptance v1") return 2;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(root, id);
            app.Run(window);
            if (window.RestartRequested)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                start.ArgumentList.Add("--resume"); start.ArgumentList.Add(id);
                Process.Start(start);
            }
            return 0;
        }
        catch { return 1; } // No raw system paths or exception payloads in the WebView.
    }
}

/// <summary>Isolated diagnostic shell; only the transport bridge is real. The production partial
/// handles the native picker, preview token, explicit consent and install state unmodified.</summary>
public partial class MainWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly string _root;
    private bool _windowClosed;
    private bool _closing;
    private bool _closeReady;
    private bool _playbackComponentBusy => false;
    private object? _playbackComponentPreview => null;
    private bool _pluginManagementBusy => false;
    private bool _platformPluginPreviewOpen => false;
    private static (string ResolvedLanguage, bool Fixture) CurrentLanguageState => ("zh-CN", true);
    private static readonly JsonSerializerOptions WebJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal bool RestartRequested { get; private set; }

    internal MainWindow(string root, string id)
    {
        _root = root;
        _transportRuntime = MediaTransportServices.CreateRuntime(Path.Combine(root, "TransportComponents"));
        Title = $"Auralis 组件隔离验收 · 非真实播放器 · {id[..8]}";
        Width = 1280; Height = 900; MinWidth = 720; MinHeight = 600;
        var panel = new System.Windows.Controls.DockPanel();
        var warning = new System.Windows.Controls.TextBlock
        {
            Text = "隔离验收：仅传输管理使用真实原生桥；无账号、曲库、音视频与系统集成。重启只重开此夹具。",
            Padding = new Thickness(12), TextWrapping = TextWrapping.Wrap,
            Background = System.Windows.Media.Brushes.LightGoldenrodYellow,
            Foreground = System.Windows.Media.Brushes.Black
        };
        System.Windows.Controls.DockPanel.SetDock(warning, System.Windows.Controls.Dock.Top);
        panel.Children.Add(warning); panel.Children.Add(_web); Content = panel;
        Loaded += async (_, _) =>
        {
            try { await InitializeAsync(); }
            catch { Record("startup", "failed"); Close(); }
        };
        Closing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _windowClosed = true;
            await CloseMediaTransportComponentsAsync();
            _web.Dispose();
            Record("lifecycle", "closed");
            _closeReady = true;
            Close();
        };
    }

    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_root, "WebView2"));
        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) => { if (!Allowed(e.Uri)) e.Cancel = true; };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Allowed(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(null, 403, "Fixture network disabled", "");
        };
        core.SetVirtualHostNameToFolderMapping("app.auralis.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += async (_, e) =>
        {
            if (!Allowed(e.Source)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var json = doc.RootElement;
                if (!json.TryGetProperty("action", out var action)) return;
                switch (action.GetString())
                {
                    case "manageMediaTransportComponents": await HandleMediaTransportComponentsAsync(json, e); break;
                    case "managePlaybackComponents":
                        // Read-only placeholder. This harness does NOT test playback-component management.
                        if (json.GetProperty("operation").GetString() == "list")
                            await ExecuteScriptAsync($"window.Auralis.setPlaybackComponents({JsonSerializer.Serialize(new { requestId = json.GetProperty("requestId").GetInt64(), operation = "list", preview = (object?)null, inventory = new { items = Array.Empty<object>(), selectedId = (string?)null, restartRequired = false, current = new { displayName = "隔离夹具（不创建播放引擎）", version = "—", bundled = true } } }, WebJsonOptions)})");
                        break;
                    case "requestPluginInventory":
                        await ExecuteScriptAsync($"window.Auralis.setPluginInventory({JsonSerializer.Serialize(new { requestId = json.GetProperty("requestId").GetInt64(), items = Array.Empty<object>(), issues = Array.Empty<object>() }, WebJsonOptions)})");
                        break;
                    case "restartForPlugins":
                        if (!_transportComponentBusy && _transportComponentPreview is null)
                        { RestartRequested = true; Close(); }
                        break;
                    // All other bridge capabilities intentionally absent. No native actions escape this shell.
                }
            }
            catch { Record("bridge", "failed"); }
        };
        _web.Source = new Uri("http://app.auralis.local/index.html");
        Record("lifecycle", "ready");
    }

    private static bool Allowed(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == "http" && parsed.Host == "app.auralis.local" && parsed.IsDefaultPort && parsed.UserInfo.Length == 0;
    private async Task ExecuteScriptAsync(string script)
    {
        if (_windowClosed || _web.CoreWebView2 is null) return;
        const string prefix = "window.Auralis?.setMediaTransportComponents(";
        if (script.StartsWith(prefix, StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(script[prefix.Length..^1]);
            var value = doc.RootElement;
            Record(value.GetProperty("operation").GetString()!, value.GetProperty("error").GetString() ??
                (value.GetProperty("cancelled").GetBoolean() ? "cancelled" : value.GetProperty("preview").ValueKind == JsonValueKind.Null ? "completed" : "preview"));
        }
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }
    private void Record(string operation, string result) => File.AppendAllText(Path.Combine(_root, "native-events.jsonl"),
        JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, operation, result }) + Environment.NewLine);
}
