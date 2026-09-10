using System.Security.Cryptography;
using Auralis.Platform.Host;

namespace Auralis.Services;

// Called under the coordinator gate; keeps remote author URLs out of WebView DTOs.
internal sealed class CommentAvatarRegistry
{
    private sealed record Entry(Uri Uri, DateTimeOffset Created, string ProviderId,
        PlatformCommentArtworkPolicy Policy, Func<bool> IsActive)
    {
        internal bool Allows(Uri uri)
        {
            try { return IsActive() && Policy.Allows(uri); }
            catch { return false; }
        }
    }
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly int _capacity;
    internal CommentAvatarRegistry(Func<DateTimeOffset>? now = null, int capacity = 2000)
    { _now = now ?? (() => DateTimeOffset.UtcNow); _capacity = Math.Clamp(capacity, 1, 2000); }

    internal string? Register(string providerId, Uri? uri, PlatformCommentArtworkPolicy policy, Func<bool> isActive)
    {
        if (uri is null) return null;
        var entry = new Entry(uri, _now(), providerId, policy, isActive);
        if (!entry.Allows(uri)) return null;
        Purge();
        var existing = _entries.FirstOrDefault(pair => pair.Value.Uri == uri &&
            pair.Value.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(pair.Value.Policy, policy) && pair.Value.Allows(uri));
        if (existing.Key is not null) return Proxy(existing.Key);
        if (_entries.Count >= _capacity) _entries.Remove(_entries.MinBy(pair => pair.Value.Created).Key);
        var handle = "avatar-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _entries[handle] = entry;
        return Proxy(handle);
    }

    internal bool TryResolve(string handle, out Uri? uri, out Func<Uri, bool>? destinationAllowed)
    {
        Purge();
        uri = null;
        destinationAllowed = null;
        if (!_entries.TryGetValue(handle, out var entry)) return false;
        if (!entry.Allows(entry.Uri)) { _entries.Remove(handle); return false; }
        uri = entry.Uri;
        destinationAllowed = entry.Allows;
        return true;
    }

    private static string Proxy(string handle) => "https://platform-art.auralis.local/" + handle;
    private void Purge()
    {
        var cutoff = _now() - TimeSpan.FromHours(2);
        foreach (var key in _entries.Where(pair => pair.Value.Created <= cutoff).Select(pair => pair.Key).ToArray()) _entries.Remove(key);
    }
}
