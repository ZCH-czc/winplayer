namespace Auralis.Platform.Host;

/// <summary>Immutable, manifest-declared HTTPS destinations for comment avatars and emotes.
/// Domain declarations authorize the domain and its subdomains, not redirects to other domains.
/// This is an application request policy, not DNS rebinding protection or a plugin sandbox.</summary>
public sealed class PlatformCommentArtworkPolicy
{
    private readonly string[] _domains;
    public static PlatformCommentArtworkPolicy DenyAll { get; } = new([]);
    private PlatformCommentArtworkPolicy(string[] domains) => _domains = domains;
    public IReadOnlyList<string> Domains => Array.AsReadOnly(_domains);

    public static bool TryCreate(IReadOnlyList<string>? domains, out PlatformCommentArtworkPolicy policy)
    {
        policy = DenyAll;
        if (domains is null || domains.Count > 16) return false;
        var copy = domains.ToArray();
        if (copy.Any(d => !ValidDomain(d)) || copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            return false;
        policy = new(copy);
        return true;
    }

    private static bool ValidDomain(string? domain)
    {
        if (domain is not { Length: >= 3 and <= 253 } || domain != domain.ToLowerInvariant() ||
            !domain.Contains('.') || domain.EndsWith('.') ||
            System.Net.IPAddress.TryParse(domain, out _) ||
            domain.EndsWith(".localhost", StringComparison.Ordinal) || domain.EndsWith(".local", StringComparison.Ordinal)) return false;
        return domain.Split('.').All(label => label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));
    }

    public bool Allows(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.AbsoluteUri.Length <= 8192 && _domains.Any(domain =>
            uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
}
