using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Auralis.Services;

/// <summary>
/// Registers Auralis as a per-user candidate for supported audio file types.
/// This service deliberately never writes an extension's default value or a UserChoice key:
/// Windows and the user remain the only parties that choose the actual default application.
/// </summary>
internal static class DefaultMusicAppRegistrationService
{
    internal const string RegisteredApplicationName = "Auralis";
    internal const string ProgId = "Auralis.Audio";
    internal const string CapabilitiesPath = @"Software\Auralis\Capabilities";

    private const string PreferencePath = @"Software\Auralis\FileAssociations";
    private const string PreferenceValueName = "RegistrationEnabled";
    private const int AssociationChanged = 0x08000000;
    private const int NotifyIdList = 0x0000;

    internal static readonly IReadOnlyList<string> SupportedExtensions =
    [
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    ];

    /// <summary>
    /// On a fresh profile registration is enabled because it only makes Auralis a candidate.
    /// A persisted opt-out prevents a later app start or update from re-registering it.
    /// </summary>
    internal static RegistrationChange EnsureRegisteredOnStartup(string? executablePath = null)
    {
        EnsureWindows();
        if (PackageIdentityService.IsPackaged)
        {
            return RegistrationChange.None;
        }

        using var preference = Registry.CurrentUser.CreateSubKey(PreferencePath, writable: true)
            ?? throw new InvalidOperationException("无法创建 Auralis 当前用户注册设置。");

        var rawPreference = preference.GetValue(PreferenceValueName);
        if (rawPreference is null)
        {
            preference.SetValue(PreferenceValueName, 1, RegistryValueKind.DWord);
        }
        else if (rawPreference is int enabled && enabled == 0)
        {
            return RegistrationChange.None;
        }

        return ApplyRegistrationPlan(BuildRegistrationPlan(ResolveExecutablePath(executablePath)));
    }

    /// <summary>
    /// Enables or disables candidate registration for this user. Disabling is persistent and
    /// removes only Auralis-owned values/keys; it never changes another application's association.
    /// </summary>
    internal static RegistrationChange SetRegistrationEnabledForCurrentUser(
        bool enabled,
        string? executablePath = null)
    {
        EnsureWindows();
        if (PackageIdentityService.IsPackaged)
        {
            return RegistrationChange.None;
        }

        using (var preference = Registry.CurrentUser.CreateSubKey(PreferencePath, writable: true)
               ?? throw new InvalidOperationException("无法创建 Auralis 当前用户注册设置。"))
        {
            preference.SetValue(PreferenceValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }

        return enabled
            ? ApplyRegistrationPlan(BuildRegistrationPlan(ResolveExecutablePath(executablePath)))
            : ApplyUnregistrationPlan(BuildUnregistrationPlan());
    }

    internal static bool IsRegistrationEnabledForCurrentUser()
    {
        EnsureWindows();
        if (PackageIdentityService.IsPackaged)
        {
            return true;
        }

        using var preference = Registry.CurrentUser.OpenSubKey(PreferencePath, writable: false);
        return preference?.GetValue(PreferenceValueName) is not int enabled || enabled != 0;
    }

    /// <summary>
    /// Opens the Windows-owned Default apps page. On supported Windows 11 builds the URI selects
    /// Auralis directly; earlier builds safely fall back to the top-level Default apps page.
    /// </summary>
    internal static void OpenWindowsDefaultAppsSettings()
    {
        EnsureWindows();
        var appName = Uri.EscapeDataString(RegisteredApplicationName);
        Process.Start(new ProcessStartInfo
        {
            FileName = $"ms-settings:defaultapps?registeredAppUser={appName}",
            UseShellExecute = true
        });
    }

    internal static RegistrationPlan BuildRegistrationPlan(string executablePath)
    {
        var normalizedExecutablePath = NormalizeExecutablePath(executablePath);
        var quotedExecutable = QuoteCommandArgument(normalizedExecutablePath);
        var icon = $"{quotedExecutable},0";
        var openCommand = $"{quotedExecutable} --open \"%1\"";
        var values = new List<RegistryValuePlan>();

        var progIdPath = $@"Software\Classes\{ProgId}";
        values.Add(StringValue(progIdPath, null, "Auralis 音频"));
        values.Add(StringValue(progIdPath, "FriendlyTypeName", "Auralis 音频"));
        values.Add(StringValue(progIdPath, RegistrationPlan.OwnerMarkerName, RegistrationPlan.OwnerMarkerValue));
        values.Add(StringValue($@"{progIdPath}\DefaultIcon", null, icon));
        values.Add(StringValue($@"{progIdPath}\shell", null, "open"));
        values.Add(StringValue($@"{progIdPath}\shell\open", null, "使用 Auralis 播放"));
        values.Add(StringValue($@"{progIdPath}\shell\open\command", null, openCommand));

        var applicationPath = @"Software\Classes\Applications\Auralis.exe";
        values.Add(StringValue(applicationPath, "FriendlyAppName", RegisteredApplicationName));
        values.Add(StringValue(applicationPath, RegistrationPlan.OwnerMarkerName, RegistrationPlan.OwnerMarkerValue));
        values.Add(StringValue($@"{applicationPath}\DefaultIcon", null, icon));
        values.Add(StringValue($@"{applicationPath}\shell\open\command", null, openCommand));

        foreach (var extension in SupportedExtensions)
        {
            values.Add(NoneValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId));
            values.Add(StringValue($@"{applicationPath}\SupportedTypes", extension, string.Empty));
            values.Add(StringValue($@"{CapabilitiesPath}\FileAssociations", extension, ProgId));
        }

        values.Add(StringValue(CapabilitiesPath, "ApplicationName", RegisteredApplicationName));
        values.Add(StringValue(
            CapabilitiesPath,
            "ApplicationDescription",
            "本地音乐优先的 Windows Fluent 音乐播放器"));
        values.Add(StringValue(CapabilitiesPath, "ApplicationIcon", icon));
        values.Add(StringValue(CapabilitiesPath, RegistrationPlan.OwnerMarkerName, RegistrationPlan.OwnerMarkerValue));
        values.Add(StringValue(@"Software\RegisteredApplications", RegisteredApplicationName, CapabilitiesPath));

        return new RegistrationPlan(normalizedExecutablePath, openCommand, values);
    }

    internal static UnregistrationPlan BuildUnregistrationPlan()
    {
        var valueDeletions = SupportedExtensions
            .Select(extension => new RegistryValueDeletionPlan(
                $@"Software\Classes\{extension}\OpenWithProgids",
                ProgId,
                ExpectedStringValue: null))
            .Append(new RegistryValueDeletionPlan(
                @"Software\RegisteredApplications",
                RegisteredApplicationName,
                CapabilitiesPath))
            .ToArray();

        var ownedTrees = new[]
        {
            new OwnedRegistryTreePlan($@"Software\Classes\{ProgId}"),
            new OwnedRegistryTreePlan(@"Software\Classes\Applications\Auralis.exe"),
            new OwnedRegistryTreePlan(CapabilitiesPath)
        };

        return new UnregistrationPlan(valueDeletions, ownedTrees);
    }

    internal static ShellActivation ParseShellActivation(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = arguments.ToArray();
        if (values.Length == 0)
        {
            return ShellActivation.None;
        }

        if (values.Length == 1)
        {
            if (string.Equals(values[0], "--register-file-associations", StringComparison.OrdinalIgnoreCase))
            {
                return new ShellActivation(ShellMaintenanceAction.Register, null);
            }

            if (string.Equals(values[0], "--unregister-file-associations", StringComparison.OrdinalIgnoreCase))
            {
                return new ShellActivation(ShellMaintenanceAction.Unregister, null);
            }

            if (string.Equals(values[0], "--default-apps-settings", StringComparison.OrdinalIgnoreCase))
            {
                return new ShellActivation(ShellMaintenanceAction.OpenSettings, null);
            }

            return new ShellActivation(ShellMaintenanceAction.None, NormalizeSupportedAudioPath(values[0]));
        }

        if (values.Length == 2 && string.Equals(values[0], "--open", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellActivation(ShellMaintenanceAction.None, NormalizeSupportedAudioPath(values[1]));
        }

        return ShellActivation.None;
    }

    private static RegistrationChange ApplyRegistrationPlan(RegistrationPlan plan)
    {
        var changedValues = 0;
        foreach (var value in plan.Values)
        {
            using var key = Registry.CurrentUser.CreateSubKey(value.KeyPath, writable: true)
                ?? throw new InvalidOperationException($"无法创建当前用户注册表项：{value.KeyPath}");
            if (RegistryValueEquals(key, value))
            {
                continue;
            }

            key.SetValue(value.ValueName ?? string.Empty, value.Value, value.ValueKind);
            changedValues += 1;
        }

        if (changedValues > 0)
        {
            NotifyAssociationChanged();
        }

        return new RegistrationChange(changedValues, 0, 0);
    }

    private static RegistrationChange ApplyUnregistrationPlan(UnregistrationPlan plan)
    {
        var deletedValues = 0;
        var deletedTrees = 0;

        foreach (var deletion in plan.ValueDeletions)
        {
            using var key = Registry.CurrentUser.OpenSubKey(deletion.KeyPath, writable: true);
            if (key is null || !key.GetValueNames().Contains(deletion.ValueName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (deletion.ExpectedStringValue is { } expected &&
                !string.Equals(
                    key.GetValue(deletion.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            key.DeleteValue(deletion.ValueName, throwOnMissingValue: false);
            deletedValues += 1;
        }

        foreach (var ownedTree in plan.OwnedTrees)
        {
            bool isOwned;
            using (var key = Registry.CurrentUser.OpenSubKey(ownedTree.KeyPath, writable: false))
            {
                isOwned = string.Equals(
                    key?.GetValue(
                            RegistrationPlan.OwnerMarkerName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    RegistrationPlan.OwnerMarkerValue,
                    StringComparison.Ordinal);
            }

            if (!isOwned)
            {
                continue;
            }

            Registry.CurrentUser.DeleteSubKeyTree(ownedTree.KeyPath, throwOnMissingSubKey: false);
            deletedTrees += 1;
        }

        if (deletedValues > 0 || deletedTrees > 0)
        {
            NotifyAssociationChanged();
        }

        return new RegistrationChange(0, deletedValues, deletedTrees);
    }

    private static bool RegistryValueEquals(RegistryKey key, RegistryValuePlan value)
    {
        var valueName = value.ValueName ?? string.Empty;
        if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        RegistryValueKind existingKind;
        try
        {
            existingKind = key.GetValueKind(valueName);
        }
        catch (IOException)
        {
            return false;
        }

        var existing = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return PlannedValueEquals(existingKind, existing, value);
    }

    internal static bool PlannedValueEquals(
        RegistryValueKind existingKind,
        object? existing,
        RegistryValuePlan value)
    {
        if (existingKind != value.ValueKind)
        {
            return false;
        }

        return value.Value switch
        {
            byte[] bytes => existing is byte[] existingBytes && bytes.SequenceEqual(existingBytes),
            string text => string.Equals(existing as string, text, StringComparison.Ordinal),
            int number => existing is int existingNumber && number == existingNumber,
            _ => Equals(value.Value, existing)
        };
    }

    private static string? NormalizeSupportedAudioPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate.Trim());
            return SupportedExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase) &&
                   File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ResolveExecutablePath(string? executablePath)
    {
        var candidate = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("无法确定 Auralis.exe 的路径。");
        }

        return NormalizeExecutablePath(candidate);
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var normalized = Path.GetFullPath(executablePath.Trim());
        if (!string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("文件关联命令必须指向 Windows 可执行文件。", nameof(executablePath));
        }

        return normalized;
    }

    private static string QuoteCommandArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static RegistryValuePlan StringValue(string keyPath, string? valueName, string value) =>
        new(keyPath, valueName, value, RegistryValueKind.String);

    private static RegistryValuePlan NoneValue(string keyPath, string valueName) =>
        new(keyPath, valueName, Array.Empty<byte>(), RegistryValueKind.None);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("默认音乐应用注册仅适用于 Windows。");
        }
    }

    private static void NotifyAssociationChanged() =>
        SHChangeNotify(AssociationChanged, NotifyIdList, nint.Zero, nint.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, nint item1, nint item2);
}

internal sealed record RegistryValuePlan(
    string KeyPath,
    string? ValueName,
    object Value,
    RegistryValueKind ValueKind);

internal sealed record RegistrationPlan(
    string ExecutablePath,
    string OpenCommand,
    IReadOnlyList<RegistryValuePlan> Values)
{
    internal const string OwnerMarkerName = "AuralisRegistrationOwner";
    internal const string OwnerMarkerValue = "Auralis";
}

internal sealed record RegistryValueDeletionPlan(
    string KeyPath,
    string ValueName,
    string? ExpectedStringValue);

internal sealed record OwnedRegistryTreePlan(string KeyPath);

internal sealed record UnregistrationPlan(
    IReadOnlyList<RegistryValueDeletionPlan> ValueDeletions,
    IReadOnlyList<OwnedRegistryTreePlan> OwnedTrees);

internal readonly record struct RegistrationChange(int WrittenValues, int DeletedValues, int DeletedTrees)
{
    internal static RegistrationChange None => new(0, 0, 0);
    internal bool Changed => WrittenValues > 0 || DeletedValues > 0 || DeletedTrees > 0;
}

internal enum ShellMaintenanceAction
{
    None,
    Register,
    Unregister,
    OpenSettings
}

internal readonly record struct ShellActivation(ShellMaintenanceAction MaintenanceAction, string? AudioFilePath)
{
    internal static ShellActivation None => new(ShellMaintenanceAction.None, null);
}
