namespace Auralis.Platform.Host;

/// <summary>Display-only metadata. No paths, exception messages, credentials or executable handles.</summary>
public sealed record PlatformPluginInventoryItem(string Id, string DisplayName, string Version,
    IReadOnlyList<string> Providers, string State);

public sealed record PlatformPluginInventoryResult(IReadOnlyList<PlatformPluginInventoryItem> Items,
    IReadOnlyList<string> Issues);

/// <summary>Rescans package metadata and integrity without loading any provider or creating a context.</summary>
public static class PlatformPluginInventory
{
    public static async Task<PlatformPluginInventoryResult> ReadAsync(IEnumerable<string> roots,
        PlatformPluginHostSnapshot session, CancellationToken token = default, PlatformHostCompatibility? compatibility = null)
    {
        var discovery = await new PlatformPluginCatalog(roots, compatibility).DiscoverAsync(token).ConfigureAwait(false);
        var items = new List<PlatformPluginInventoryItem>();
        foreach (var manifest in discovery.Plugins)
        {
            token.ThrowIfCancellationRequested();
            var trusted = await PlatformPluginIntegrity.VerifyAsync(manifest, token).ConfigureAwait(false);
            var registered = session.Plugins.Any(p => p.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)
                && p.Version == manifest.Version && p.ManifestPath.Equals(manifest.ManifestPath, StringComparison.OrdinalIgnoreCase));
            items.Add(new(manifest.Id, manifest.DisplayName, manifest.Version.ToString(),
                manifest.Providers.Select(p => p.DisplayName).ToArray(),
                !trusted ? "untrusted" : registered ? "verified" : "restartRequired"));
        }
        foreach (var diagnostic in discovery.Diagnostics.Where(d => PlatformHostCompatibility.ImportError(d.Code) is not null))
            if (diagnostic.PluginId is { } id && !items.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                items.Add(new(id, id, "", [], "incompatible"));
        // Do not keep displaying a removed package as available just because the runtime is cached.
        foreach (var missing in session.Plugins.Where(p => !items.Any(m => m.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))))
            items.Add(new(missing.Id, missing.DisplayName, missing.Version.ToString(),
                missing.Providers.Select(p => p.DisplayName).ToArray(), "unavailable"));
        var issues = discovery.Diagnostics.Where(d => d.Code != PlatformPluginDiagnosticCode.PluginDirectoryNotFound)
            .Select(d => d.Code.ToString()).Distinct().Order().ToArray();
        return new(items.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(), issues);
    }
}
