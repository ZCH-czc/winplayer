using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>
/// Discovers platform plugins from explicitly supplied directories by reading manifests only.
/// No assembly is loaded, reflected over, or executed during this phase.
/// </summary>
public sealed class PlatformPluginCatalog
{
    private const long MaximumManifestBytes = 256 * 1024;

    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly IReadOnlyList<string> _pluginDirectories;
    private readonly PlatformHostCompatibility _compatibility;

    /// <summary>Creates a manifest-only catalog from explicit roots. An empty list disables plugins.</summary>
    public PlatformPluginCatalog(IEnumerable<string> pluginDirectories, PlatformHostCompatibility? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(pluginDirectories);
        _pluginDirectories = pluginDirectories.ToArray();
        _compatibility = compatibility ?? PlatformHostCompatibility.Current;
    }

    /// <summary>
    /// Reads <c>platform.plugin.json</c> at each root and in each root's immediate child directories.
    /// A directory tree is intentionally not recursively traversed: every plugin is one bounded folder.
    /// </summary>
    public async Task<PlatformPluginDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<PlatformPluginDiagnostic>();
        var manifestPaths = new HashSet<string>(PathComparer);

        foreach (var configuredDirectory in _pluginDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(configuredDirectory))
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.PluginDirectoryInvalid,
                    "A configured plugin directory was empty."));
                continue;
            }

            string root;
            try
            {
                root = Path.GetFullPath(configuredDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.PluginDirectoryInvalid,
                    "A configured plugin directory path was invalid.",
                    path: configuredDirectory,
                    exception: exception));
                continue;
            }

            if (!Directory.Exists(root))
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.PluginDirectoryNotFound,
                    "A configured plugin directory does not exist; online plugins remain disabled for that directory.",
                    PlatformPluginDiagnosticSeverity.Warning,
                    path: root));
                continue;
            }

            var resolvedRoot = ResolveLinkTarget(root, isDirectory: true) ?? root;
            AddManifestIfPresent(root, manifestPaths);

            try
            {
                foreach (var child in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var absoluteChild = Path.GetFullPath(child);
                    var resolvedChild = ResolveLinkTarget(absoluteChild, isDirectory: true) ?? absoluteChild;

                    if (!IsStrictlyWithin(resolvedRoot, resolvedChild))
                    {
                        diagnostics.Add(Diagnostic(
                            PlatformPluginDiagnosticCode.ManifestPathEscapesPluginDirectory,
                            "A plugin directory link resolves outside its explicitly configured root.",
                            path: absoluteChild));
                        continue;
                    }

                    AddManifestIfPresent(absoluteChild, manifestPaths);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.ManifestReadFailed,
                    "The plugin directory could not be enumerated completely.",
                    path: root,
                    exception: exception));
            }
        }

        var parsed = new List<PlatformPluginManifest>();
        foreach (var manifestPath in manifestPaths.OrderBy(path => path, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(manifestPath, diagnostics, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                parsed.Add(manifest);
            }
        }

        var duplicatePluginIds = parsed
            .GroupBy(manifest => manifest.Id, IdComparer)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToArray(), IdComparer);

        foreach (var conflict in duplicatePluginIds.Values)
        {
            foreach (var manifest in conflict)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.DuplicatePluginId,
                    $"Plugin ID '{manifest.Id}' is declared by more than one manifest; every conflicting plugin was rejected.",
                    pluginId: manifest.Id,
                    path: manifest.ManifestPath));
            }
        }

        var acceptedPlugins = parsed
            .Where(manifest => !duplicatePluginIds.ContainsKey(manifest.Id))
            .ToArray();

        var duplicateProviderIds = acceptedPlugins
            .SelectMany(plugin => plugin.Providers.Select(provider => (Plugin: plugin, Provider: provider)))
            .GroupBy(item => item.Provider.Id, IdComparer)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToArray(), IdComparer);

        foreach (var conflict in duplicateProviderIds.Values)
        {
            foreach (var item in conflict)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.DuplicateProviderId,
                    $"Provider ID '{item.Provider.Id}' is declared more than once; every conflicting provider was rejected.",
                    pluginId: item.Plugin.Id,
                    providerId: item.Provider.Id,
                    path: item.Plugin.ManifestPath));
            }
        }

        var finalPlugins = acceptedPlugins
            .Select(plugin => CopyWithProviders(
                plugin,
                plugin.Providers
                    .Where(provider => !duplicateProviderIds.ContainsKey(provider.Id))
                    .ToArray()))
            .ToArray();

        return new PlatformPluginDiscoveryResult(
            new ReadOnlyCollection<PlatformPluginManifest>(finalPlugins),
            new ReadOnlyCollection<PlatformPluginDiagnostic>(diagnostics));
    }

    private static void AddManifestIfPresent(string pluginDirectory, ISet<string> manifestPaths)
    {
        var path = Path.GetFullPath(Path.Combine(pluginDirectory, PlatformPluginManifestSchema.FileName));
        if (File.Exists(path))
        {
            manifestPaths.Add(path);
        }
    }

    private async Task<PlatformPluginManifest?> ReadManifestAsync(
        string manifestPath,
        ICollection<PlatformPluginDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(manifestPath);
            if (fileInfo.Length > MaximumManifestBytes)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.ManifestTooLarge,
                    "The platform plugin manifest exceeds the 256 KiB safety limit.",
                    path: manifestPath));
                return null;
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > MaximumManifestBytes) throw new JsonException("Manifest grew beyond its limit.");
            using var envelope = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (envelope.RootElement.ValueKind == JsonValueKind.Object &&
                envelope.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var schemaVersion) &&
                (schemaVersion < 1 || schemaVersion > _compatibility.MaximumManifestSchema))
            {
                var id = envelope.RootElement.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String
                    ? identifier.GetString() : null;
                diagnostics.Add(Diagnostic(PlatformPluginDiagnosticCode.ManifestSchemaUnsupported,
                    "The plugin manifest schema is unsupported by this host.", pluginId: IsValidId(id) ? id : null, path: manifestPath));
                return null;
            }
            var document = envelope.RootElement.Deserialize<ManifestDocument>(SerializerOptions);

            if (document is null)
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.ManifestInvalid,
                    "The platform plugin manifest was empty.",
                    path: manifestPath));
                return null;
            }

            return ValidateDocument(document, manifestPath, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "The platform plugin manifest is not valid schema-versioned JSON.",
                path: manifestPath,
                exception: exception));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestReadFailed,
                "The platform plugin manifest could not be read.",
                path: manifestPath,
                exception: exception));
            return null;
        }
    }

    private PlatformPluginManifest? ValidateDocument(
        ManifestDocument document,
        string manifestPath,
        ICollection<PlatformPluginDiagnostic> diagnostics)
    {
        var pluginDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var entryAssembly = SelectAlias(document.EntryAssembly, document.Assembly);
        var entryType = SelectAlias(document.EntryType, document.EntryPoint);

        if ((document.EntryAssembly is not null && document.Assembly is not null &&
             !string.Equals(document.EntryAssembly, document.Assembly, StringComparison.Ordinal)) ||
            (document.EntryType is not null && document.EntryPoint is not null &&
             !string.Equals(document.EntryType, document.EntryPoint, StringComparison.Ordinal)))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "Manifest entry-field aliases contain conflicting values.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        if (document.SchemaVersion < 1 || document.SchemaVersion > _compatibility.MaximumManifestSchema)
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestSchemaUnsupported,
                "The plugin manifest schema is unsupported by this host.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        if (!IsValidId(document.Id) || !IsValidDisplayName(document.DisplayName) ||
            !Version.TryParse(document.Version, out var pluginVersion))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "The plugin ID, display name, or version is invalid.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        if (!PlatformContract.IsCompatible(document.MinimumHostApiVersion, document.MaximumHostApiVersion))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.HostApiIncompatible,
                $"Plugin '{document.Id}' does not support host API {PlatformContract.HostApiVersion}.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        if ((document.SchemaVersion >= 4 && document.HostRequirements is null) ||
            (document.SchemaVersion < 4 && document.HostRequirements is not null) ||
            document.HostRequirements?.IsValid() == false)
        {
            diagnostics.Add(Diagnostic(PlatformPluginDiagnosticCode.ManifestInvalid,
                "Host requirements must be valid and declared in schema 4.", pluginId: document.Id, path: manifestPath));
            return null;
        }
        if (document.HostRequirements is { } requirements && _compatibility.Check(requirements) is { } incompatibility)
        {
            diagnostics.Add(Diagnostic(incompatibility, "The plugin requires a different host SDK or an unsupported host feature.",
                pluginId: document.Id, path: manifestPath));
            return null;
        }

        if (string.IsNullOrWhiteSpace(entryAssembly) ||
            Path.IsPathRooted(entryAssembly) ||
            !string.Equals(Path.GetExtension(entryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "entryAssembly must be a relative DLL path inside the plugin directory.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        if (!IsValidEntryType(entryType))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "entryType must be a non-qualified CLR type name without whitespace or an assembly suffix.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        string assemblyPath;
        try
        {
            assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, entryAssembly));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "entryAssembly is not a valid path.",
                pluginId: document.Id,
                path: manifestPath,
                exception: exception));
            return null;
        }

        if (!IsStrictlyWithin(pluginDirectory, assemblyPath))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestPathEscapesPluginDirectory,
                "entryAssembly resolves outside the plugin directory.",
                pluginId: document.Id,
                path: assemblyPath));
            return null;
        }

        if (!File.Exists(assemblyPath))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.EntryAssemblyMissing,
                "The declared entry assembly does not exist.",
                pluginId: document.Id,
                path: assemblyPath));
            return null;
        }

        var resolvedAssemblyPath = ResolveLinkTarget(assemblyPath, isDirectory: false);
        var resolvedPluginDirectory = ResolveLinkTarget(pluginDirectory, isDirectory: true) ?? pluginDirectory;
        if (resolvedAssemblyPath is not null && !IsStrictlyWithin(resolvedPluginDirectory, resolvedAssemblyPath))
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestPathEscapesPluginDirectory,
                "The entry assembly link resolves outside the plugin directory.",
                pluginId: document.Id,
                path: assemblyPath));
            return null;
        }

        if (document.Providers is null || document.Providers.Count == 0)
        {
            diagnostics.Add(Diagnostic(
                PlatformPluginDiagnosticCode.ManifestInvalid,
                "A platform plugin manifest must declare at least one provider.",
                pluginId: document.Id,
                path: manifestPath));
            return null;
        }

        var providers = new List<PlatformProviderManifest>(document.Providers.Count);
        var settingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in document.Providers)
        {
            if (!IsValidId(provider.Id) || !IsValidDisplayName(provider.DisplayName) ||
                provider.Capabilities is null || provider.Capabilities.Count == 0 ||
                !PlatformSettingManifest.IsValidList(provider.Settings) ||
                (provider.Settings?.Any(s => !settingKeys.Add(s.Key)) == true) ||
                (provider.Settings?.Count > 0 && document.SchemaVersion < 2))
            {
                diagnostics.Add(Diagnostic(
                    PlatformPluginDiagnosticCode.ManifestInvalid,
                    "A provider ID, display name, or capability list is invalid.",
                    pluginId: document.Id,
                    providerId: provider.Id,
                    path: manifestPath));
                return null;
            }

            var capabilities = provider.Capabilities.Distinct().ToArray();
            var legacyArtwork = document.SchemaVersion < 5;
            var artworkPolicy = PlatformCommentArtworkPolicy.DenyAll;
            if ((legacyArtwork && provider.CommentArtworkDomains is not null) ||
                (!legacyArtwork && !PlatformCommentArtworkPolicy.TryCreate(provider.CommentArtworkDomains, out artworkPolicy)))
            {
                diagnostics.Add(Diagnostic(PlatformPluginDiagnosticCode.ManifestInvalid,
                    "Comment artwork domains must be valid and explicitly declared in schema 5.",
                    pluginId: document.Id, providerId: provider.Id, path: manifestPath));
                return null;
            }
            if (legacyArtwork) artworkPolicy = PlatformCommentArtworkPolicy.DenyAll;
            providers.Add(new PlatformProviderManifest(
                provider.Id!,
                provider.DisplayName!,
                new ReadOnlyCollection<PlatformCapabilityKind>(capabilities), provider.Settings, artworkPolicy, legacyArtwork));
        }

        if (!PlatformCredentialAlias.IsValidList(document.CredentialAliases) ||
            (document.CredentialAliases is not null && document.SchemaVersion < 3))
        {
            diagnostics.Add(Diagnostic(PlatformPluginDiagnosticCode.ManifestInvalid,
                "Credential alias declarations are invalid or require schema 3.", pluginId: document.Id, path: manifestPath));
            return null;
        }

        if (document.HostRequirements is { } declared)
        {
            var needed = new List<string>();
            if (document.SchemaVersion >= 5) needed.Add("comment-artwork.v1");
            if (providers.Any(p => p.Settings.Count > 0)) needed.Add("settings.v1");
            if (document.CredentialAliases?.Count > 0) needed.Add("credential-aliases.v1");
            foreach (var (kind, feature) in new[] {
                (PlatformCapabilityKind.NativeLogin, "native-login.v1"), (PlatformCapabilityKind.TrackDetails, "track-details.v1"),
                (PlatformCapabilityKind.LyricsLookup, "lyrics-lookup.v1"), (PlatformCapabilityKind.VideoResolution, "video-lease.v2") })
                if (providers.Any(p => p.Capabilities.Contains(kind))) needed.Add(feature);
            if (needed.Any(f => !declared.RequiredFeatures.Contains(f, StringComparer.Ordinal)))
            {
                diagnostics.Add(Diagnostic(PlatformPluginDiagnosticCode.ManifestInvalid,
                    "A declared provider capability or setting lacks its required host feature.", pluginId: document.Id, path: manifestPath));
                return null;
            }
        }

        return new PlatformPluginManifest(
            document.SchemaVersion,
            document.Id!,
            document.DisplayName!,
            pluginVersion,
            document.MinimumHostApiVersion,
            document.MaximumHostApiVersion,
            entryAssembly,
            assemblyPath,
            entryType!,
            manifestPath,
            pluginDirectory,
            new ReadOnlyCollection<PlatformProviderManifest>(providers), document.CredentialAliases, document.HostRequirements);
    }

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    private static bool IsValidDisplayName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static bool IsValidEntryType(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 512 &&
        !value.Contains(',') &&
        !value.Any(char.IsWhiteSpace) &&
        !value.Any(char.IsControl);

    private static string? SelectAlias(string? preferred, string? alias) =>
        string.IsNullOrWhiteSpace(preferred) ? alias : preferred;

    private static bool IsStrictlyWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !string.Equals(relative, ".", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string? ResolveLinkTarget(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static PlatformPluginManifest CopyWithProviders(
        PlatformPluginManifest source,
        IReadOnlyList<PlatformProviderManifest> providers) =>
        new(
            source.SchemaVersion,
            source.Id,
            source.DisplayName,
            source.Version,
            source.MinimumHostApiVersion,
            source.MaximumHostApiVersion,
            source.EntryAssembly,
            source.EntryAssemblyPath,
            source.EntryType,
            source.ManifestPath,
            source.PluginDirectory,
            new ReadOnlyCollection<PlatformProviderManifest>(providers.ToArray()), source.CredentialAliases, source.HostRequirements);

    private static PlatformPluginDiagnostic Diagnostic(
        PlatformPluginDiagnosticCode code,
        string message,
        PlatformPluginDiagnosticSeverity severity = PlatformPluginDiagnosticSeverity.Error,
        string? pluginId = null,
        string? providerId = null,
        string? path = null,
        Exception? exception = null) =>
        new(code, severity, message, pluginId, providerId, path, exception);

    private sealed class ManifestDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("minimumHostApiVersion")]
        public int MinimumHostApiVersion { get; init; }

        [JsonPropertyName("maximumHostApiVersion")]
        public int MaximumHostApiVersion { get; init; }

        [JsonPropertyName("entryAssembly")]
        public string? EntryAssembly { get; init; }

        [JsonPropertyName("assembly")]
        public string? Assembly { get; init; }

        [JsonPropertyName("entryType")]
        public string? EntryType { get; init; }

        [JsonPropertyName("entryPoint")]
        public string? EntryPoint { get; init; }

        [JsonPropertyName("providers")]
        public List<ProviderDocument>? Providers { get; init; }

        [JsonPropertyName("credentialAliases")]
        public List<PlatformCredentialAlias>? CredentialAliases { get; init; }

        [JsonPropertyName("hostRequirements")]
        public PlatformHostRequirements? HostRequirements { get; init; }
    }

    private sealed class ProviderDocument
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("capabilities")]
        public List<PlatformCapabilityKind>? Capabilities { get; init; }

        [JsonPropertyName("settings")]
        public List<PlatformSettingManifest>? Settings { get; init; }

        [JsonPropertyName("commentArtworkDomains")]
        public List<string>? CommentArtworkDomains { get; init; }
    }
}
