using System.Collections.Concurrent;

namespace Auralis.Services;

/// <summary>Session-only revisions, not secrets. A setting/account change invalidates old leases.</summary>
internal sealed class MediaContextRevision
{
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    internal long Read(string provider) => _revisions.GetOrAdd(provider, 0);
    internal void Invalidate(string provider) => _revisions.AddOrUpdate(provider, 1, (_, value) => value + 1);
}
