namespace Auralis.Platform.Host;

/// <summary>Classifies plugin-host diagnostics without retaining plugin exception objects.</summary>
public enum PlatformPluginDiagnosticCode
{
    PluginDirectoryInvalid,
    PluginDirectoryNotFound,
    ManifestReadFailed,
    ManifestTooLarge,
    ManifestInvalid,
    ManifestSchemaUnsupported,
    ManifestPathEscapesPluginDirectory,
    EntryAssemblyMissing,
    HostApiIncompatible,
    DuplicatePluginId,
    DuplicateProviderId,
    AssemblyLoadFailed,
    EntryTypeMissing,
    EntryTypeInvalid,
    PluginConstructionFailed,
    PluginDescriptorInvalid,
    PluginDescriptorMismatch,
    ContextCreationFailed,
    ProviderCreationFailed,
    ProviderDescriptorInvalid,
    ProviderDescriptorMismatch,
    ProviderMissing,
    ProviderInitializationFailed,
    CapabilityMismatch,
    PluginOperationFailed,
    PluginDisposalFailed,
    HostDisposed,
    PluginNotTrusted,
    HostSdkIncompatible,
    HostFeatureUnsupported,
    ManifestUpgradeRequired
}

/// <summary>Indicates diagnostic impact.</summary>
public enum PlatformPluginDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>
/// A sanitized plugin diagnostic. Exception instances are deliberately converted to strings so a
/// plugin-defined exception type cannot keep its collectible load context alive.
/// </summary>
public sealed record PlatformPluginDiagnostic
{
    internal PlatformPluginDiagnostic(
        PlatformPluginDiagnosticCode code,
        PlatformPluginDiagnosticSeverity severity,
        string message,
        string? pluginId = null,
        string? providerId = null,
        string? path = null,
        Exception? exception = null)
    {
        Code = code;
        Severity = severity;
        Message = message;
        PluginId = pluginId;
        ProviderId = providerId;
        Path = path;
        ExceptionType = exception?.GetType().FullName;
    }

    public PlatformPluginDiagnosticCode Code { get; }

    public PlatformPluginDiagnosticSeverity Severity { get; }

    public string Message { get; }

    public string? PluginId { get; }

    public string? ProviderId { get; }

    public string? Path { get; }

    public string? ExceptionType { get; }
}
