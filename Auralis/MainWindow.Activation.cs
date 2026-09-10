using System.Text.Json;
using Auralis.Services;

namespace Auralis;

public partial class MainWindow
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);

    internal async Task ActivateExistingInstanceAsync(SingleInstanceCoordinator.Activation request)
    {
        RestoreFromTray();
        await _localStartupReady.Task;
        if (_windowClosed) return;
        await _activationGate.WaitAsync();
        try
        {
            // Treat IPC paths exactly like Shell file activations, never as commands/URLs.
            var activation = DefaultMusicAppRegistrationService.ParseShellActivation(
                request.AudioFile is null ? [] : ["--open", request.AudioFile]);
            if (activation.AudioFilePath is { } file) await HandleShellAudioFileAsync(file);
            var message = request.Build == RuntimeBuildIdentity.Label
                ? $"已打开正在运行的 Auralis {RuntimeBuildIdentity.Label}"
                : $"正在运行 Auralis {RuntimeBuildIdentity.Label}。本次启动的是另一构建，未重复打开；切换版本请先从托盘退出当前应用。";
            await ExecuteScriptAsync($"window.Auralis?.showToast({JsonSerializer.Serialize(message)})");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('无法打开传入的音乐文件，请确认文件仍存在且可读取。')");
        }
        finally { _activationGate.Release(); }
    }
}
