using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Non-secret, exact vault address compatibility metadata. Never a wildcard or a secret value.</summary>
public sealed record PlatformCredentialAlias(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("legacyKey")] string LegacyKey)
{
    public static bool IsValidList(IReadOnlyList<PlatformCredentialAlias>? aliases)
    {
        if (aliases is null) return true;
        if (aliases.Count > 16) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<(string, string)>();
        return aliases.All(a => a is not null && Valid(a.Key, 96) && Valid(a.Scope, 64) &&
            Valid(a.LegacyKey, 96) && keys.Add(a.Key) && targets.Add((a.Scope, a.LegacyKey)));
    }

    private static bool Valid(string? value, int limit) => !string.IsNullOrEmpty(value) &&
        value.Length <= limit && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

/// <summary>
/// Routes only explicitly approved aliases. Creation is inert; ordinary and temporary keys stay in
/// the plugin's own scope. Sign-in, refresh and sign-out all use the same address; no copying occurs.
/// This is a host-service boundary, not a sandbox for trusted in-process plugin code.
/// </summary>
public sealed class DeclaredPlatformCredentialStore : IPlatformCredentialStore
{
    private readonly IPlatformCredentialStore _current;
    private readonly IReadOnlyDictionary<string, PlatformCredentialAlias> _aliases;
    private readonly Func<string, IPlatformCredentialStore> _createScope;
    private readonly ConcurrentDictionary<string, IPlatformCredentialStore> _scopes = new(StringComparer.Ordinal);

    public DeclaredPlatformCredentialStore(IPlatformCredentialStore current,
        IReadOnlyList<PlatformCredentialAlias> approvedAliases, Func<string, IPlatformCredentialStore> createScope)
    {
        ArgumentNullException.ThrowIfNull(approvedAliases);
        if (!PlatformCredentialAlias.IsValidList(approvedAliases)) throw new ArgumentException("Invalid credential aliases.", nameof(approvedAliases));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _createScope = createScope ?? throw new ArgumentNullException(nameof(createScope));
        _aliases = approvedAliases.ToDictionary(a => a.Key, StringComparer.Ordinal);
    }

    private (IPlatformCredentialStore Store, string Key) Resolve(string key) =>
        _aliases.TryGetValue(key, out var alias)
            ? (_scopes.GetOrAdd(alias.Scope, _createScope), alias.LegacyKey) : (_current, key);

    public ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var route = Resolve(key);
        return route.Store.GetAsync(route.Key, token);
    }
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> secret, DateTimeOffset? expiry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var route = Resolve(key);
        return route.Store.SetAsync(route.Key, secret, expiry, token);
    }
    public ValueTask DeleteAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var route = Resolve(key);
        return route.Store.DeleteAsync(route.Key, token);
    }
}

/// <summary>Optional host-side context factory receiving the validated, trusted activation manifest.</summary>
public interface IManifestPlatformHostContextFactory : IPlatformHostContextFactory
{
    PlatformHostContext CreateContext(PlatformPluginManifest manifest);
}
