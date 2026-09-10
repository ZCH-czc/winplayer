using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Auralis.Platform.Host;

/// <summary>Bounded plain-text, non-secret settings declared by a plugin, never executable UI.</summary>
public sealed record PlatformSettingManifest
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("defaultValue")] public string DefaultValue { get; init; } = "";
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<PlatformSettingChoice> Choices { get; init; } = [];
    // Migration aliases are non-secret keys from the old settings file, not paths or credentials.
    [JsonPropertyName("legacyKeys")] public IReadOnlyList<string> LegacyKeys { get; init; } = [];

    public static bool IsValidList(IReadOnlyList<PlatformSettingManifest>? items) => items is null ||
        (items.Count <= 16 && items.All(s => s is not null && s.Key is not null && s.Label is not null &&
             s.Description is not null && s.DefaultValue is not null && s.Choices is not null && s.LegacyKeys is not null &&
             s.LegacyKeys.Count <= 4 && s.LegacyKeys.All(k => k is not null && Regex.IsMatch(k, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}\z")) &&
             s.LegacyKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == s.LegacyKeys.Count &&
             s.Choices.All(c => c is not null && c.Value is not null && c.Label is not null)) &&
         items.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count &&
         items.All(s => Regex.IsMatch(s.Key, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}\z") &&
             s.Label.Length is > 0 and <= 120 && s.Description.Length <= 400 &&
             !s.Label.Any(char.IsControl) && !s.Description.Any(char.IsControl) &&
             s.Kind is "choice" or "endpoint" && s.Choices.Count <= 16 &&
             (s.Kind != "choice" || s.Choices.Count > 0) &&
             s.Choices.All(c => c.Value.Length is > 0 and <= 80 && c.Label.Length is > 0 and <= 120 &&
                 !c.Value.Any(char.IsControl) && !c.Label.Any(char.IsControl)) &&
             s.Choices.Select(c => c.Value).Distinct().Count() == s.Choices.Count && s.TryNormalize(s.DefaultValue, out _)));

    public bool TryNormalize(string? input, out string value)
    {
        value = input?.Trim() ?? "";
        if (value.Length > 2048 || value.Any(char.IsControl)) return false;
        if (Kind == "choice") { var candidate = value; return Choices.Any(c => c.Value == candidate); }
        if (Kind != "endpoint") return false;
        if (value.Length == 0) return true; // Empty is saveable; Required gates readiness, not editing.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) return false;
        value = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }
}

public sealed record PlatformSettingChoice([property:JsonPropertyName("value")] string Value, [property:JsonPropertyName("label")] string Label);
