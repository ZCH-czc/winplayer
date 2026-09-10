using System.Diagnostics;
using System.IO;
using System.Windows;
using Auralis.Services;

namespace Auralis;

public partial class App
{
    private static readonly object ShellActivationLock = new();
    private static string? _pendingShellAudioFile;
    private SingleInstanceCoordinator? _singleInstance;

    /// <summary>
    /// Returns a startup audio path exactly once. MainWindow should consume this after its persisted
    /// library has loaded, add it through the normal local-file path, and then request native playback.
    /// </summary>
    internal static string? TakePendingShellAudioFile()
    {
        lock (ShellActivationLock)
        {
            var path = _pendingShellAudioFile;
            _pendingShellAudioFile = null;
            return path;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0] == "--restart-after")
        {
            if (!int.TryParse(e.Args[1], out var parentId) || !long.TryParse(e.Args[2], out var parentTicks)) { Shutdown(); return; }
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentTicks ||
                    !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                    !parent.WaitForExit(45000)) { Shutdown(); return; }
            }
            catch (ArgumentException) { /* Parent already exited. */ }
            catch { Shutdown(); return; }
        }
        var activation = DefaultMusicAppRegistrationService.ParseShellActivation(e.Args);
        if (activation.MaintenanceAction != ShellMaintenanceAction.None)
        {
            HandleFileAssociationMaintenance(activation.MaintenanceAction);
            Shutdown();
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            var forwarded = _singleInstance.ForwardAsync(new(activation.AudioFilePath, RuntimeBuildIdentity.Label))
                .GetAwaiter().GetResult();
            if (!forwarded)
                System.Windows.MessageBox.Show("Auralis 已在运行，但暂时未能响应。请在托盘中打开或退出已有应用后重试。为避免多份应用争用音乐库，本次不会另开播放器。",
                    "Auralis 已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Older builds cannot receive IPC. Do not quietly start another host beside one.
        if (HasLegacyHost())
        {
            System.Windows.MessageBox.Show("检测到仍在运行的旧版 Auralis。旧版尚不支持单实例转交，请先从它的托盘菜单选择“退出”，再打开此版本。无需删除或重新登录账号。",
                $"Auralis {RuntimeBuildIdentity.Label}", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _singleInstance.Listen(request => Dispatcher.BeginInvoke(new Action(async () =>
        {
            // Dispatcher messages can arrive before StartupUri creates the main window.
            while (MainWindow is null && !Dispatcher.HasShutdownStarted) await Task.Delay(50);
            if (MainWindow is MainWindow window) await window.ActivateExistingInstanceAsync(request);
        })));

        try
        {
            DefaultMusicAppRegistrationService.EnsureRegisteredOnStartup();
        }
        catch (Exception exception)
        {
            // Candidate registration must never prevent the local player from opening.
            Debug.WriteLine($"Auralis file-association registration was skipped: {exception.Message}");
        }

        if (activation.AudioFilePath is { } audioFilePath)
        {
            lock (ShellActivationLock)
            {
                _pendingShellAudioFile = audioFilePath;
            }
        }

        try
        {
            PlaybackServices.VerifyRuntime();
        }
        catch (Exception exception) when (exception is FileNotFoundException or PlatformNotSupportedException or Auralis.Playback.Host.PlaybackComponentUnavailableException)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Auralis 音频组件不完整",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 2;
            Shutdown(2);
            return;
        }

        base.OnStartup(e);
        // Auxiliary windows (lyrics/tray) are constructed by MainWindow field initializers;
        // explicitly select the real shell instead of WPF's first-created-window default.
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private bool HasLegacyHost()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("Auralis"))
        {
            using (process)
            {
                try
                {
                    // Include hidden/tray-only old hosts. New secondary launches advertise their
                    // participation before competing for ownership, avoiding cold-start false positives.
                    if (process.Id != current.Id && process.SessionId == current.SessionId &&
                        _singleInstance?.IsParticipatingProcess(process.Id) != true)
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }

    private static void HandleFileAssociationMaintenance(ShellMaintenanceAction action)
    {
        try
        {
            switch (action)
            {
                case ShellMaintenanceAction.Register:
                    DefaultMusicAppRegistrationService.SetRegistrationEnabledForCurrentUser(enabled: true);
                    break;
                case ShellMaintenanceAction.Unregister:
                    DefaultMusicAppRegistrationService.SetRegistrationEnabledForCurrentUser(enabled: false);
                    break;
                case ShellMaintenanceAction.OpenSettings:
                    DefaultMusicAppRegistrationService.SetRegistrationEnabledForCurrentUser(enabled: true);
                    DefaultMusicAppRegistrationService.OpenWindowsDefaultAppsSettings();
                    break;
                case ShellMaintenanceAction.None:
                default:
                    return;
            }

            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Auralis file-association maintenance failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }
}
