using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Auralis.Platform.Host;

/// <summary>Pure metadata requirements; no code, URLs, credentials or executable probes.</summary>
public sealed record PlatformHostRequirements
{
    [JsonPropertyName("minimumHostSdkVersion")]
    public string MinimumHostSdkVersion { get; init; } = "";
    [JsonPropertyName("requiredFeatures")]
    public IReadOnlyList<string> RequiredFeatures { get; init; } = [];

    internal bool IsValid() => PlatformHostCompatibility.TryParseVersion(MinimumHostSdkVersion, out _) &&
        RequiredFeatures is { Count: <= 32 } && RequiredFeatures.All(PlatformHostCompatibility.ValidFeature) &&
        RequiredFeatures.Distinct(StringComparer.Ordinal).Count() == RequiredFeatures.Count;
}

/// <summary>Immutable, host-owned compatibility profile. Never inferred from installed plugin code.</summary>
public sealed class PlatformHostCompatibility
{
    public const string SdkVersion = "2.0.0";
    public static PlatformHostCompatibility Current { get; } = new(new Version(SdkVersion),
        ["settings.v1", "credential-aliases.v1", "native-login.v1", "track-details.v1", "lyrics-lookup.v1", "video-lease.v2", "comment-artwork.v1"], 5);

    public PlatformHostCompatibility(Version version, IEnumerable<string> features, int maximumManifestSchema = 5)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(features);
        if (!TryParseVersion(version.ToString(), out _) || maximumManifestSchema is < 1 or > 5)
            throw new ArgumentException("Invalid host compatibility profile.");
        var names = features.ToArray();
        if (names.Length > 32 || names.Any(f => !ValidFeature(f))) throw new ArgumentException("Invalid host feature list.");
        Version = version;
        Features = names.ToFrozenSet(StringComparer.Ordinal);
        MaximumManifestSchema = maximumManifestSchema;
    }

    public Version Version { get; }
    public IReadOnlySet<string> Features { get; }
    public int MaximumManifestSchema { get; }

    public PlatformPluginDiagnosticCode? Check(PlatformHostRequirements requirements)
    {
        if (!requirements.IsValid()) return PlatformPluginDiagnosticCode.ManifestInvalid;
        if (Version < new Version(requirements.MinimumHostSdkVersion)) return PlatformPluginDiagnosticCode.HostSdkIncompatible;
        if (requirements.RequiredFeatures.Any(f => !Features.Contains(f))) return PlatformPluginDiagnosticCode.HostFeatureUnsupported;
        return null;
    }

    internal static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        return text is { Length: <= 32 } && text.Split('.').Length == 3 &&
            System.Version.TryParse(text, out version) && version.Major >= 1 && version.ToString() == text;
    }
    internal static bool ValidFeature(string? value) => value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterLower(value[0]) && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');

    internal static string? ImportError(PlatformPluginDiagnosticCode code) => code switch
    {
        PlatformPluginDiagnosticCode.HostSdkIncompatible => "hostSdkIncompatible",
        PlatformPluginDiagnosticCode.HostFeatureUnsupported => "hostFeatureUnsupported",
        PlatformPluginDiagnosticCode.HostApiIncompatible => "hostApiIncompatible",
        PlatformPluginDiagnosticCode.ManifestSchemaUnsupported => "manifestSchemaUnsupported",
        PlatformPluginDiagnosticCode.ManifestUpgradeRequired => "manifestUpgradeRequired",
        _ => null
    };
}

/// <summary>A safe import failure code, not an exception/path forwarded to the UI.</summary>
public sealed class PlatformPluginCompatibilityException(string code) : IOException("Plugin compatibility requirements are not met.")
{
    public string Code { get; } = code;
}
