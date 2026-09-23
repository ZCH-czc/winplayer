using Auralis.Platform.Abstractions;

namespace Auralis.Services;

/// <summary>Current-session display projection. Never passes provider IDs, URLs or headers to WebView.</summary>
internal sealed class VideoQualitySelection
{
    private string? _owner;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    internal IReadOnlyList<VideoQualityView> Options { get; private set; } = [];
    internal string? Selected { get; private set; }
    internal void Clear() { _owner = null; _ids.Clear(); Options = []; Selected = null; }
    internal bool TryResolve(string? owner, string? handle, out string? quality)
    {
        quality = null;
        return handle is null || owner == _owner && _ids.TryGetValue(handle, out quality);
    }
    internal void Update(string owner, PlatformVideoLease lease)
    {
        Clear(); _owner = owner;
        var list = lease.Qualities;
        if (list is null || list.Count > 24 || list.Any(q => q is null ||
            q.Id is not { Length: > 0 and <= 64 } || !q.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            q.Label is not { Length: > 0 and <= 64 } || q.Label.Any(char.IsControl)) ||
            list.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != list.Count ||
            !list.Any(q => q.Id == lease.SelectedQualityId)) return;
        Options = list.Select(q => {
            var handle = "vq-" + Guid.NewGuid().ToString("N"); _ids[handle] = q.Id;
            if (q.Id == lease.SelectedQualityId) Selected = handle;
            return new VideoQualityView(handle, q.Label);
        }).ToArray();
    }
}
internal sealed record VideoQualityView(string Handle, string Label);
