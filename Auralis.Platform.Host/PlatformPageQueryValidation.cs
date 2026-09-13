using System.Collections.ObjectModel;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Read-only query vocabulary. Validate before routing any submitted input to a plugin.</summary>
public static class PlatformPageQueryValidation
{
    public static bool IsValid(PlatformPageQuery? query) => query is not null &&
        query.Submit is { Label.Length: > 0 and <= 100 } action && ValidText(action.Label, 100) && PlatformPageEntry.ValidId(action.Route) &&
        action.Target is null && (action.State is null || action.State.Length <= 8192) && query.Fields is { Count: > 0 and <= 4 } &&
        query.Fields.All(f => f is not null && PlatformPageEntry.ValidId(f.Key) &&
            f.Label is { Length: > 0 and <= 100 } && ValidText(f.Label, 100) && ValidText(f.Placeholder, 200) &&
            f.MaxLength is >= 1 and <= 256 && f.MinLength >= 0 && f.MinLength <= f.MaxLength &&
            ValidText(f.Value, f.MaxLength) && f.Options is { Count: <= 16 } &&
            (f.Kind == "text" && f.Options.Count == 0 ||
             f.Kind == "choice" && f.MinLength == 0 && f.Options.Count > 0 &&
             f.Options.All(o => o is not null && o.Label is { Length: > 0 and <= 100 } && ValidText(o.Label, 100) &&
                 ValidText(o.Value, Math.Min(64, f.MaxLength)) && o.Value.Length > 0) &&
             f.Options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() == f.Options.Count &&
             f.Options.Any(o => o.Value == f.Value))) &&
        query.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() == query.Fields.Count;

    public static bool Accepts(PlatformPageQuery schema, IReadOnlyDictionary<string, string>? values) =>
        IsValid(schema) && values is not null && values.Count == schema.Fields.Count &&
        schema.Fields.All(f => values.TryGetValue(f.Key, out var value) &&
            ValidText(value, f.MaxLength) && value.Length >= f.MinLength &&
            (f.Kind != "choice" || f.Options.Any(o => o.Value == value)));

    public static PlatformPageQuery Snapshot(PlatformPageQuery query) => query with
    {
        Fields = Array.AsReadOnly(query.Fields.Select(f => f with {
            Options = Array.AsReadOnly(f.Options.ToArray()) }).ToArray())
    };

    public static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string> values) =>
        new ReadOnlyDictionary<string, string>(values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private static bool ValidText(string? text, int max) =>
        text is not null && text.Length <= max && !text.Any(char.IsControl);
}
