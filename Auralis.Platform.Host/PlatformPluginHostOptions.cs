using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Creates plugin-scoped host services only when a plugin is activated.</summary>
public interface IPlatformHostContextFactory
{
    /// <summary>Creates a context whose scope belongs exclusively to <paramref name="pluginId"/>.</summary>
    PlatformHostContext CreateContext(string pluginId);
}

/// <summary>Adapts a delegate to <see cref="IPlatformHostContextFactory"/>.</summary>
public sealed class DelegatePlatformHostContextFactory : IPlatformHostContextFactory
{
    private readonly Func<string, PlatformHostContext> _factory;

    public DelegatePlatformHostContextFactory(Func<string, PlatformHostContext> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public PlatformHostContext CreateContext(string pluginId) => _factory(pluginId);
}

/// <summary>Immutable configuration for an optional platform plugin host.</summary>
public sealed class PlatformPluginHostOptions
{
    /// <summary>Creates host options. An empty directory list is a valid, fully disabled configuration.</summary>
    public PlatformPluginHostOptions(
        IEnumerable<string> pluginDirectories,
        IPlatformHostContextFactory contextFactory,
        TimeSpan? operationTimeout = null,
        Func<PlatformPluginManifest, CancellationToken, Task<bool>>? trustPolicy = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? directoryResolver = null,
        Func<PlatformPluginManifest, CancellationToken, Task<bool>>? enabledPolicy = null,
        PlatformHostCompatibility? compatibility = null,
        int minimumManifestSchemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(pluginDirectories);
        PluginDirectories = Array.AsReadOnly(pluginDirectories.ToArray());
        ContextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        TrustPolicy = trustPolicy;
        DirectoryResolver = directoryResolver;
        EnabledPolicy = enabledPolicy;
        Compatibility = compatibility ?? PlatformHostCompatibility.Current;
        if (minimumManifestSchemaVersion is < 1 or > PlatformPluginManifestSchema.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(minimumManifestSchemaVersion));
        MinimumManifestSchemaVersion = minimumManifestSchemaVersion;
        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);

        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The platform operation timeout must be greater than zero and no longer than ten minutes.");
        }
    }

    /// <summary>Gets the only roots catalog discovery may inspect.</summary>
    public IReadOnlyList<string> PluginDirectories { get; }

    /// <summary>Gets the factory invoked at activation time, never during discovery.</summary>
    public IPlatformHostContextFactory ContextFactory { get; }

    /// <summary>Optional fail-closed approval check before discovery registration and DLL activation.</summary>
    public Func<PlatformPluginManifest, CancellationToken, Task<bool>>? TrustPolicy { get; }
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? DirectoryResolver { get; }
    public Func<PlatformPluginManifest, CancellationToken, Task<bool>>? EnabledPolicy { get; }
    public PlatformHostCompatibility Compatibility { get; }
    /// <summary>Activation floor; offline legacy tooling may read older generic contracts.</summary>
    public int MinimumManifestSchemaVersion { get; }

    /// <summary>Gets the common timeout applied to plugin creation, provider initialization, and routed calls.</summary>
    public TimeSpan OperationTimeout { get; }
}

/// <summary>A provider route visible from its manifest without loading plugin code.</summary>
public sealed record PlatformProviderRegistration
{
    internal PlatformProviderRegistration(string pluginId, PlatformProviderManifest provider)
    {
        PluginId = pluginId;
        Provider = provider;
    }

    public string PluginId { get; }

    public PlatformProviderManifest Provider { get; }
}

/// <summary>An immutable view of discovered plugins, usable before any plugin activation.</summary>
public sealed record PlatformPluginHostSnapshot
{
    internal PlatformPluginHostSnapshot(
        IReadOnlyList<PlatformPluginManifest> plugins,
        IReadOnlyList<PlatformProviderRegistration> providers)
    {
        Plugins = plugins;
        Providers = providers;
    }

    public IReadOnlyList<PlatformPluginManifest> Plugins { get; }

    public IReadOnlyList<PlatformProviderRegistration> Providers { get; }
}
