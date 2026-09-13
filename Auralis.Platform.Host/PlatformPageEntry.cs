using System.Text.Json.Serialization;

namespace Auralis.Platform.Host;

/// <summary>Inert manifest entry. Labels are bilingual; route names carry no runtime state.</summary>
public sealed record PlatformPageEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("labelEn")] public string LabelEn { get; init; } = "";
    [JsonPropertyName("placement")] public string Placement { get; init; } = "media";
    [JsonPropertyName("presentation")] public string Presentation { get; init; } = "dialog";
    [JsonPropertyName("acceptsCreatorContext")] public bool AcceptsCreatorContext { get; init; }
    [JsonPropertyName("documentVersion")] public int DocumentVersion { get; init; } = 1;
    public static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    public static bool IsValidList(IReadOnlyList<PlatformPageEntry>? entries) => entries is null ||
        (entries.Count <= 8 && entries.All(e => e is not null && ValidId(e.Id) &&
            e.Label is { Length: > 0 and <= 80 } && e.LabelEn is { Length: > 0 and <= 80 } &&
            e.Placement is "media" or "creator" or "global" && e.Presentation is "dialog" or "page" &&
            (!e.AcceptsCreatorContext || e.Placement == "creator") && e.DocumentVersion is 1 or 2 or 3 or 4 or 5 or 6 &&
            (e.Placement != "global" || e.Presentation == "page" && e.DocumentVersion >= 3) &&
            (!(e.Presentation == "page" || e.AcceptsCreatorContext) || e.DocumentVersion >= 2)) &&
         entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count &&
         entries.Count(e => e.Placement == "creator") <= 1 && entries.Count(e => e.Placement == "global") <= 2);
}
