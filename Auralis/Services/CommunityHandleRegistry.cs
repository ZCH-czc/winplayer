using Auralis.Platform.Abstractions;

namespace Auralis.Services;

/// <summary>Backend-only, bounded community references. Never accepts Web-supplied entity IDs/cursors.</summary>
internal sealed class CommunityHandleRegistry
{
    internal sealed record Entry(string Kind, PlatformEntityId Entity, string Parent, PlatformEntityId? Root, string? Cursor, DateTimeOffset Created, Func<bool>? IsActive);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    internal CommunityHandleRegistry(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal string Add(string kind, PlatformEntityId entity, string parent = "", PlatformEntityId? root = null, string? cursor = null, Func<bool>? isActive = null)
    {
        Purge();
        // Stable in-session handles let the UI deduplicate pinned/repeated comments and posts.
        var existing = _entries.FirstOrDefault(p => p.Value.Kind == kind && p.Value.Entity == entity && p.Value.Parent == parent && p.Value.Root == root && p.Value.Cursor == cursor);
        if (existing.Key is not null) return existing.Key;
        if (_entries.Count >= 8192) _entries.Remove(_entries.MinBy(p => p.Value.Created).Key);
        var handle = "community-" + Guid.NewGuid().ToString("N");
        _entries[handle] = new(kind, entity, parent, root, cursor, _clock.GetUtcNow(), isActive);
        return handle;
    }
    internal Entry? Get(string? handle, string kind, string? parent = null)
    {
        Purge();
        return handle is { Length: <= 128 } && _entries.TryGetValue(handle, out var entry) && entry.Kind == kind &&
            (parent is null || entry.Parent == parent) ? entry : null;
    }
    private void Purge()
    {
        foreach (var key in _entries.Where(p => _clock.GetUtcNow() - p.Value.Created > TimeSpan.FromHours(2) || p.Value.IsActive?.Invoke() == false).Select(p => p.Key).ToArray()) _entries.Remove(key);
    }
}
