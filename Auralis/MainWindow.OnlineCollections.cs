using System.Text.Json;
using Auralis.Platform.Abstractions;

namespace Auralis;

public partial class MainWindow
{
    private async Task SaveOnlineProviderSettingAsync(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var r) || !r.TryGetInt64(out var requestId) || requestId is <= 0 or > 9007199254740991L) return;
        var providerId = JsonText(root, "providerId");
        var key = JsonText(root, "key");
        var value = JsonText(root, "value");
        if (providerId is null || providerId.Length > 80 || key is null || key.Length > 80 || value is null || value.Length > 2048) return;
        string? error = null;
        object? settings = null;
        bool configured = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var backend = ((App)System.Windows.Application.Current).PlatformBackend;
            await backend.SaveSettingAsync(providerId, key, value, timeout.Token);
            var registration = (await backend.DiscoverAsync(timeout.Token)).Providers.First(p => p.Provider.Id == providerId);
            var actual = await backend.ReadSettingsAsync(registration, timeout.Token);
            settings = actual;
            configured = actual.All(s => !s.Required || s.Value.Length > 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        { error = "无法保存插件设置，请检查输入或插件状态后重试。"; }
        var payload = JsonSerializer.Serialize(new { requestId, providerId, key, error, settings, configured }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setOnlineProviderSettingResult({payload})");
    }

    private async Task HandleOnlineCollectionAsync(string action, JsonElement root)
    {
        var providerId = JsonText(root, "providerId");
        var backend = ((App)System.Windows.Application.Current).PlatformBackend;
        var registration = (await backend.DiscoverAsync()).Providers.FirstOrDefault(p => p.Provider.Id == providerId);
        if (registration is null || backend.IsPluginDisabled(registration.PluginId)) return;
        if (action == "manageOnlineAccount")
        {
            switch (JsonText(root, "operation"))
            {
                case "login": await OpenPlatformLoginAsync(providerId!); break;
                case "signout": await SignOutPlatformAsync(providerId!); break;
                case "refresh": await SendPlatformConfigurationAsync(); break;
            }
            return;
        }
        if (!root.TryGetProperty("requestId", out var r) || !r.TryGetInt64(out var requestId) || requestId is <= 0 or > 9007199254740991L) return;
        var handle = JsonText(root, "handle");
        if (string.IsNullOrEmpty(handle) || handle.Length > 128) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var result = await OnlinePlatforms.GetPlaylistAsync(providerId!, handle, timeout.Token);
        if (backend.IsPluginDisabled(registration.PluginId)) return;
        var payload = JsonSerializer.Serialize(new { providerId, requestId, handle,
            detail = result.IsSuccess ? result.Value : null, error = result.IsSuccess ? null : ToPlatformError(result.Error) }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setOnlineCollection({payload})");
    }

    private async Task RestartForPluginsAsync()
    {
        if (_pluginManagementBusy || _platformPluginPreviewOpen || _playbackComponentBusy || _playbackComponentPreview is not null || _transportComponentBusy || _transportComponentPreview is not null || _exitRequested) return;
        try
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException();
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--restart-after");
            start.ArgumentList.Add(current.Id.ToString());
            start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString());
            System.Diagnostics.Process.Start(start);
            _exitRequested = true;
            _trayIcon.Visible = false;
            Close();
        }
        catch { await ShowPlatformLoginErrorAsync("无法重启，请从托盘退出后重新打开应用。"); }
    }
}
