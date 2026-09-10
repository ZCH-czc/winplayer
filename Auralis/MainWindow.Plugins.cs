using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Auralis.Platform.Host;
using Microsoft.Web.WebView2.Core;

namespace Auralis;

public partial class MainWindow
{
    private bool _pluginInventoryBusy;
    private bool _pluginManagementBusy;
    private bool _platformPluginPreviewOpen;

    private async Task SendPluginInventoryAsync(long requestId)
    {
        if (_pluginInventoryBusy) return;
        _pluginInventoryBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var backend = ((App)System.Windows.Application.Current).PlatformBackend;
            var session = await backend.DiscoverAsync(timeout.Token);
            // Hashing and disk enumeration must not stall playback/window interaction.
            var result = await Task.Run(() => backend.PluginManager!.ReadInventoryAsync(session, timeout.Token));
            var payload = JsonSerializer.Serialize(new { requestId, items = result.Items, issues = result.Issues }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPluginInventory({payload})");
        }
        catch
        {
            var payload = JsonSerializer.Serialize(new { requestId, error = "inventoryUnavailable" }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPluginInventory({payload})");
        }
        finally { _pluginInventoryBusy = false; }
    }

    private async Task HandlePluginManagementAsync(string action, JsonElement root, CoreWebView2WebMessageReceivedEventArgs message)
    {
        if (!Uri.TryCreate(message.Source, UriKind.Absolute, out var source) || source.Scheme != "http" ||
            source.Host != "app.auralis.local" || !source.IsDefaultPort || source.UserInfo.Length != 0) return;
        if (_pluginManagementBusy || !root.TryGetProperty("requestId", out var request) ||
            !request.TryGetInt64(out var requestId) || requestId <= 0 || requestId > 9007199254740991L) return;
        if (_playbackComponentBusy || _playbackComponentPreview is not null || _transportComponentBusy || _transportComponentPreview is not null)
        {
            var busyPayload = JsonSerializer.Serialize(new { requestId, action, error = "pluginManagementFailed" }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPluginManagementResult({busyPayload})");
            return;
        }
        _pluginManagementBusy = true;
        try
        {
            var backend = ((App)System.Windows.Application.Current).PlatformBackend;
            // Freeze the old routing plan before a preference/import changes the desired next-session state.
            using (var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                await backend.DiscoverAsync(discoveryTimeout.Token);
            var manager = backend.PluginManager!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            object? preview = null;
            object? batch = null;
            object? results = null;
            bool? credentialsCleared = null;
            var cancelled = false;
            if (action == "pickPluginPackage")
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入平台插件", Filter = "Auralis 插件包 (*.auralis-plugin;*.zip)|*.auralis-plugin;*.zip",
                    CheckFileExists = true, Multiselect = true
                };
                if (picker.ShowDialog(this) != true) cancelled = true;
                else
                {
                    // Time spent deciding in the native picker is not a package parsing timeout.
                    using var importTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    batch = await Task.Run(() => manager.PrepareBatchAsync(picker.FileNames, importTimeout.Token));
                    _platformPluginPreviewOpen = true;
                }
            }
            else if (action == "dropPluginPackages")
            {
                var objects = message.AdditionalObjects;
                if (objects is null || objects.Count is < 1 or > PlatformPluginManager.MaximumBatchCount ||
                    objects.Any(o => o is not CoreWebView2File)) throw new InvalidDataException();
                // Only native-backed File objects supply paths. Never read a path from JSON/HTML.
                var paths = objects.Cast<CoreWebView2File>().Select(file => file.Path).ToArray();
                if (paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p))) throw new InvalidDataException();
                batch = await Task.Run(() => manager.PrepareBatchAsync(paths, timeout.Token));
                _platformPluginPreviewOpen = true;
            }
            else if (action == "confirmPluginImport")
            {
                if (!root.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String ||
                    tokenElement.GetString()?.Length != 32 || !root.TryGetProperty("trust", out var trust) || trust.ValueKind != JsonValueKind.True)
                    throw new InvalidDataException();
                results = await Task.Run(() => manager.ConfirmBatchAsync(tokenElement.GetString()!, true, timeout.Token));
                _platformPluginPreviewOpen = false;
            }
            else if (action == "cancelPluginImport")
            {
                await manager.CancelImportAsync(timeout.Token);
                _platformPluginPreviewOpen = false;
            }
            else if (action == "setPluginEnabled")
            {
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString()?.Length > 64 ||
                    !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException();
                if (enabled.GetBoolean()) await Task.Run(() => manager.SetEnabledAsync(id.GetString()!, true, timeout.Token));
                else
                {
                    var manifest = await backend.FindPluginForDisableAsync(id.GetString()!, timeout.Token);
                    if (manifest is not null) foreach (var provider in manifest.Providers)
                    {
                        _platformLoginGenerations.Remove(provider.Id);
                        if (_openingPlatformLogins.Remove(provider.Id, out var pendingLogin)) CancelPlatformLogin(pendingLogin);
                        if (_platformLogins.Remove(provider.Id, out var login)) ClosePlatformLoginSession(login);
                    }
                    credentialsCleared = await backend.DisableAndClearAsync(id.GetString()!, new System.Windows.Interop.WindowInteropHelper(this).Handle, timeout.Token);
                    await SendPlatformConfigurationAsync();
                }
            }
            var payload = JsonSerializer.Serialize(new { requestId, action, preview, batch, results, cancelled, credentialsCleared }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPluginManagementResult({payload})");
        }
        catch
        {
            // Never return package paths, exception text, ZIP names or local state contents.
            var payload = JsonSerializer.Serialize(new { requestId, action, error = "pluginManagementFailed" }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPluginManagementResult({payload})");
        }
        finally { _pluginManagementBusy = false; }
    }

    private async Task OpenPluginFolderAsync()
    {
        try
        {
            // Never accept a path or shell argument from the WebView.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Auralis", "Plugins");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('无法打开插件文件夹')");
        }
    }
}
