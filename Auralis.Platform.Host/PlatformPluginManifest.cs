using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>The current schema understood by <c>platform.plugin.json</c>.</summary>
public static class PlatformPluginManifestSchema
{
    /// <summary>Gets the supported manifest schema version.</summary>
    public const int CurrentVersion = 5;

    /// <summary>Auralis runtime/import floor: account and artwork access must be explicitly declared.</summary>
    public const int MinimumRuntimeVersion = 5;

    /// <summary>Gets the conventional manifest file name.</summary>
    public const string FileName = "platform.plugin.json";
}

/// <summary>
/// Validated, manifest-only metadata for an optional platform plugin. Discovery builds this object by
/// reading JSON and checking paths; it never loads or inspects the entry assembly.
/// </summary>
public sealed record PlatformPluginManifest
{
    internal PlatformPluginManifest(
        int schemaVersion,
        string id,
        string displayName,
        Version version,
        int minimumHostApiVersion,
        int maximumHostApiVersion,
        string entryAssembly,
        string entryAssemblyPath,
        string entryType,
        string manifestPath,
        string pluginDirectory,
        IReadOnlyList<PlatformProviderManifest> providers,
        IReadOnlyList<PlatformCredentialAlias>? credentialAliases = null,
        PlatformHostRequirements? hostRequirements = null)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        DisplayName = displayName;
        Version = version;
        MinimumHostApiVersion = minimumHostApiVersion;
        MaximumHostApiVersion = maximumHostApiVersion;
        EntryAssembly = entryAssembly;
        EntryAssemblyPath = entryAssemblyPath;
        EntryType = entryType;
        ManifestPath = manifestPath;
        PluginDirectory = pluginDirectory;
        Providers = providers;
        CredentialAliases = Array.AsReadOnly((credentialAliases ?? []).ToArray());
        HostRequirements = hostRequirements is null ? null : hostRequirements with
        { RequiredFeatures = Array.AsReadOnly(hostRequirements.RequiredFeatures.ToArray()) };
    }

    /// <summary>Gets the manifest schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable plugin ID.</summary>
    public string Id { get; }

    /// <summary>Gets the user-facing plugin name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the plugin package version declared by the manifest.</summary>
    public Version Version { get; }

    /// <summary>Gets the oldest host contract accepted by the plugin.</summary>
    public int MinimumHostApiVersion { get; }

    /// <summary>Gets the newest host contract accepted by the plugin.</summary>
    public int MaximumHostApiVersion { get; }

    /// <summary>Gets the original relative entry-assembly path from the manifest.</summary>
    public string EntryAssembly { get; }

    /// <summary>Gets the validated absolute entry-assembly path.</summary>
    public string EntryAssemblyPath { get; }

    /// <summary>Gets the assembly-qualified namespace/type name of the plugin entry point.</summary>
    public string EntryType { get; }

    /// <summary>Gets the absolute manifest path.</summary>
    public string ManifestPath { get; }

    /// <summary>Gets the absolute directory that bounds plugin-owned paths.</summary>
    public string PluginDirectory { get; }

    /// <summary>Gets the providers declared without activating the plugin.</summary>
    public IReadOnlyList<PlatformProviderManifest> Providers { get; }

    /// <summary>Exact legacy vault addresses requiring explicit approval at import (schema 3).</summary>
    public IReadOnlyList<PlatformCredentialAlias> CredentialAliases { get; }

    /// <summary>Schema 4 host compatibility requirements, validated before any plugin code runs.</summary>
    public PlatformHostRequirements? HostRequirements { get; }
}

/// <summary>Manifest-only metadata used to route a provider before its assembly is activated.</summary>
public sealed record PlatformProviderManifest
{
    internal PlatformProviderManifest(
        string id,
        string displayName,
        IReadOnlyCollection<PlatformCapabilityKind> capabilities,
        IReadOnlyList<PlatformSettingManifest>? settings = null,
        PlatformCommentArtworkPolicy? commentArtworkPolicy = null,
        bool usesLegacyCommentArtworkPolicy = false)
    {
        Id = id;
        DisplayName = displayName;
        Capabilities = capabilities;
        Settings = settings ?? [];
        CommentArtworkPolicy = commentArtworkPolicy ?? PlatformCommentArtworkPolicy.DenyAll;
        UsesLegacyCommentArtworkPolicy = usesLegacyCommentArtworkPolicy;
    }

    /// <summary>Gets the globally unique, case-insensitive provider ID.</summary>
    public string Id { get; }

    /// <summary>Gets the provider display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the capabilities declared for manifest-only routing.</summary>
    public IReadOnlyCollection<PlatformCapabilityKind> Capabilities { get; }

    public IReadOnlyList<PlatformSettingManifest> Settings { get; }
    public PlatformCommentArtworkPolicy CommentArtworkPolicy { get; }
    /// <summary>Historical field: true means schema 1–4 omitted explicit policy. The policy is deny-all, not implicit CDN access.</summary>
    public bool UsesLegacyCommentArtworkPolicy { get; }
}

/// <summary>Contains accepted manifests and discovery diagnostics.</summary>
public sealed record PlatformPluginDiscoveryResult
{
    internal PlatformPluginDiscoveryResult(
        IReadOnlyList<PlatformPluginManifest> plugins,
        IReadOnlyList<PlatformPluginDiagnostic> diagnostics)
    {
        Plugins = plugins;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets manifests that survived validation and duplicate-ID rejection.</summary>
    public IReadOnlyList<PlatformPluginManifest> Plugins { get; }

    /// <summary>Gets non-fatal and fatal discovery diagnostics.</summary>
    public IReadOnlyList<PlatformPluginDiagnostic> Diagnostics { get; }
}
