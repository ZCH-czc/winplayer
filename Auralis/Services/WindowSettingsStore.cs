using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Auralis.Services;

/// <summary>
/// Persists the small set of native-window preferences that must be available before the WebView UI is ready.
/// Web-only presentation preferences continue to be owned by the Web UI.
/// </summary>
internal sealed class WindowSettingsStore
{
    internal const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    internal WindowSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis",
            "window-settings.json"))
    {
    }

    internal WindowSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal NativeWindowSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return NativeWindowSettings.Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NativeWindowSettings.Default;
            }

            var closeToTray = root.TryGetProperty("closeToTray", out var closeToTrayProperty) &&
                              closeToTrayProperty.ValueKind == JsonValueKind.True;
            var uiLanguage = root.TryGetProperty("uiLanguage", out var uiLanguageProperty) &&
                             uiLanguageProperty.ValueKind == JsonValueKind.String
                ? UiLanguagePreference.Normalize(uiLanguageProperty.GetString())
                : UiLanguagePreference.System;

            return new NativeWindowSettings(CurrentSchemaVersion, closeToTray, uiLanguage);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           InvalidOperationException)
        {
            return NativeWindowSettings.Default;
        }
    }

    internal void Save(NativeWindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings with
        {
            SchemaVersion = CurrentSchemaVersion,
            UiLanguage = UiLanguagePreference.Normalize(settings.UiLanguage)
        };
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Each writer owns a unique sibling file. This preserves atomic replacement without allowing
        // two simultaneously running Auralis processes to move or delete one another's temporary file.
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale, writer-specific temporary file is harmless and can be cleaned up later.
            }
        }
    }
}

internal sealed record NativeWindowSettings(int SchemaVersion, bool CloseToTray, string UiLanguage)
{
    internal static NativeWindowSettings Default { get; } =
        new(WindowSettingsStore.CurrentSchemaVersion, false, UiLanguagePreference.System);
}

internal sealed record UiLanguageState(string Preference, string ResolvedLanguage);

internal static class UiLanguagePreference
{
    internal const string System = "system";
    internal const string SimplifiedChinese = "zh-CN";
    internal const string EnglishUnitedStates = "en-US";

    internal static bool IsSupported(string? preference) => preference is
        System or SimplifiedChinese or EnglishUnitedStates;

    internal static string Normalize(string? preference) => preference switch
    {
        SimplifiedChinese => SimplifiedChinese,
        EnglishUnitedStates => EnglishUnitedStates,
        _ => System
    };

    internal static UiLanguageState Resolve(string? preference, CultureInfo? systemCulture = null)
    {
        var normalized = Normalize(preference);
        if (normalized != System)
        {
            return new UiLanguageState(normalized, normalized);
        }

        var culture = systemCulture ?? CultureInfo.CurrentUICulture;
        var resolved = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : EnglishUnitedStates;
        return new UiLanguageState(System, resolved);
    }
}
