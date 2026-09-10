using System.Collections.ObjectModel;
using System.Reflection;
using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>
/// Owns manifest discovery, lazy plugin activation, provider lifetimes, and collectible load contexts.
/// Constructing this type performs no file access, assembly loading, or network work.
/// </summary>
public sealed class PlatformPluginHost : IAsyncDisposable
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    private readonly PlatformPluginHostOptions _options;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly object _diagnosticGate = new();
    private readonly List<PlatformPluginDiagnostic> _diagnostics = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    public void DisablePlugin(string id) => _disabledPlugins.TryAdd(id, 0);
    private readonly object _lifetimeGate = new();

    private Dictionary<string, PluginSlot>? _plugins;
    private Dictionary<string, ProviderRoute>? _routes;
    private PlatformPluginHostSnapshot? _snapshot;
    private bool _disposeStarted;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;

    /// <summary>Creates a dormant plugin host.</summary>
    public PlatformPluginHost(PlatformPluginHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Router = new PlatformRouter(this, options.OperationTimeout);
    }

    /// <summary>Gets the capability router. Calling it is what can trigger lazy plugin activation.</summary>
    public PlatformRouter Router { get; }

    /// <summary>Gets a stable copy of every diagnostic reported so far.</summary>
    public IReadOnlyList<PlatformPluginDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnosticGate)
            {
                return Array.AsReadOnly(_diagnostics.ToArray());
            }
        }
    }

    /// <summary>
    /// Performs manifest-only discovery once and returns a cached snapshot thereafter. This method never
    /// loads a DLL and never creates a plugin context, HTTP client, plugin, or provider.
    /// </summary>
    public async Task<PlatformPluginHostSnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
        => await DiscoverCoreAsync(allowWhileDisposing: false, cancellationToken).ConfigureAwait(false);

    internal Task<PlatformPluginHostSnapshot> DiscoverForOperationAsync(CancellationToken cancellationToken) =>
        DiscoverCoreAsync(allowWhileDisposing: true, cancellationToken);

    private async Task<PlatformPluginHostSnapshot> DiscoverCoreAsync(
        bool allowWhileDisposing,
        CancellationToken cancellationToken)
    {
        if (!allowWhileDisposing)
        {
            ThrowIfDisposing();
        }

        if (_snapshot is { } snapshot)
        {
            return snapshot;
        }

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!allowWhileDisposing)
            {
                ThrowIfDisposing();
            }

            if (_snapshot is not null)
            {
                return _snapshot;
            }

            var roots = _options.DirectoryResolver is null ? _options.PluginDirectories
                : await _options.DirectoryResolver(cancellationToken).ConfigureAwait(false);
            var catalog = new PlatformPluginCatalog(roots, _options.Compatibility);
            var discovery = await catalog.DiscoverAsync(cancellationToken).ConfigureAwait(false);

            foreach (var diagnostic in discovery.Diagnostics)
            {
                RecordDiagnostic(diagnostic);
            }

            var plugins = new Dictionary<string, PluginSlot>(IdComparer);
            var routes = new Dictionary<string, ProviderRoute>(IdComparer);
            var registrations = new List<PlatformProviderRegistration>();

            foreach (var manifest in discovery.Plugins)
            {
                if (manifest.SchemaVersion < _options.MinimumManifestSchemaVersion)
                {
                    RecordDiagnostic(new PlatformPluginDiagnostic(PlatformPluginDiagnosticCode.ManifestUpgradeRequired,
                        PlatformPluginDiagnosticSeverity.Warning, "Plugin declarations need an explicit package upgrade.", pluginId: manifest.Id));
                    continue;
                }
                if (_options.EnabledPolicy is not null &&
                    !await _options.EnabledPolicy(manifest, cancellationToken).ConfigureAwait(false)) continue;
                if (!await IsTrustedAsync(manifest, cancellationToken).ConfigureAwait(false)) continue;
                var slot = new PluginSlot(this, manifest, _options);
                plugins.Add(manifest.Id, slot);

                foreach (var provider in manifest.Providers)
                {
                    // Catalog duplicate elimination guarantees Add cannot silently choose a winner.
                    routes.Add(provider.Id, new ProviderRoute(slot, provider));
                    registrations.Add(new PlatformProviderRegistration(manifest.Id, provider));
                }
            }

            _plugins = plugins;
            _routes = routes;
            _snapshot = new PlatformPluginHostSnapshot(
                new ReadOnlyCollection<PlatformPluginManifest>(discovery.Plugins.Where(m => plugins.ContainsKey(m.Id)).ToArray()),
                new ReadOnlyCollection<PlatformProviderRegistration>(registrations));
            return _snapshot;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task<bool> IsTrustedAsync(PlatformPluginManifest manifest, CancellationToken token)
    {
        if (_options.TrustPolicy is null) return true;
        try { if (await _options.TrustPolicy(manifest, token).ConfigureAwait(false)) return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* A broken trust store fails closed without breaking the local player. */ }
        RecordDiagnostic(new PlatformPluginDiagnostic(PlatformPluginDiagnosticCode.PluginNotTrusted,
            PlatformPluginDiagnosticSeverity.Error, "Plugin has no matching local approval or its payload changed.", pluginId: manifest.Id));
        return false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    internal bool TryBeginOperation(out IDisposable? lease)
    {
        lock (_lifetimeGate)
        {
            if (_disposeStarted)
            {
                lease = null;
                return false;
            }

            checked
            {
                _activeOperations++;
            }

            lease = new OperationLease(this);
            return true;
        }
    }

    internal async Task<PlatformResult<IPlatformProvider>> GetProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return PlatformResult<IPlatformProvider>.Failure(
                PlatformErrorCode.InvalidRequest,
                "A provider ID is required.");
        }

        try
        {
            await DiscoverForOperationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult<IPlatformProvider>.Failure(
                PlatformErrorCode.Cancelled,
                "The platform operation was cancelled by the caller.");
        }
        catch (ObjectDisposedException)
        {
            return PlatformResult<IPlatformProvider>.Failure(
                PlatformErrorCode.ServiceUnavailable,
                "The platform plugin host is shutting down.");
        }

        if (_routes is null || !_routes.TryGetValue(providerId, out var route))
        {
            return PlatformResult<IPlatformProvider>.Failure(
                PlatformErrorCode.NotFound,
                $"Online provider '{providerId}' is not installed or was rejected during discovery.");
        }

        if (_disabledPlugins.ContainsKey(route.Plugin.Id))
            return PlatformResult<IPlatformProvider>.Failure(PlatformErrorCode.ContentUnavailable, "插件已停用，请重启应用。");
        var result = await route.Plugin.GetProviderAsync(route.Provider, cancellationToken).ConfigureAwait(false);
        return _disabledPlugins.ContainsKey(route.Plugin.Id)
            ? PlatformResult<IPlatformProvider>.Failure(PlatformErrorCode.ContentUnavailable, "插件已停用，请重启应用。") : result;
    }

    internal bool TryGetProviderManifest(string providerId, out PlatformProviderManifest? manifest)
    {
        if (_routes is not null && _routes.TryGetValue(providerId, out var route))
        {
            manifest = route.Provider;
            return true;
        }

        manifest = null;
        return false;
    }

    internal void RecordDiagnostic(PlatformPluginDiagnostic diagnostic)
    {
        lock (_diagnosticGate)
        {
            _diagnostics.Add(diagnostic);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? waitForOperations = null;
        lock (_lifetimeGate)
        {
            _disposeStarted = true;
            if (_activeOperations > 0)
            {
                _operationsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitForOperations = _operationsDrained.Task;
            }
        }

        if (waitForOperations is not null)
        {
            await waitForOperations.ConfigureAwait(false);
        }

        await _discoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var slots = _plugins?.Values.ToArray() ?? Array.Empty<PluginSlot>();
            _routes?.Clear();
            _plugins?.Clear();

            foreach (var slot in slots)
            {
                await slot.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private void ThrowIfDisposing()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
        }
    }

    private sealed record ProviderRoute(PluginSlot Plugin, PlatformProviderManifest Provider);

    private sealed class OperationLease : IDisposable
    {
        private PlatformPluginHost? _owner;

        internal OperationLease(PlatformPluginHost owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperation();
    }

    private sealed class PluginSlot : IAsyncDisposable
    {
        internal string Id => _manifest.Id;
        private readonly PlatformPluginHost _host;
        private readonly PlatformPluginManifest _manifest;
        private readonly PlatformPluginHostOptions _options;
        private readonly SemaphoreSlim _activationGate = new(1, 1);

        private LoadedPlugin? _loaded;
        private PlatformError? _terminalFailure;
        private bool _disposed;

        internal PluginSlot(
            PlatformPluginHost host,
            PlatformPluginManifest manifest,
            PlatformPluginHostOptions options)
        {
            _host = host;
            _manifest = manifest;
            _options = options;
        }

        internal async Task<PlatformResult<IPlatformProvider>> GetProviderAsync(
            PlatformProviderManifest providerManifest,
            CancellationToken cancellationToken)
        {
            var activation = await EnsureActivatedAsync(cancellationToken).ConfigureAwait(false);
            if (!activation.IsSuccess)
            {
                return PlatformResult<IPlatformProvider>.Failure(activation.Error!);
            }

            if (!activation.Value.TryGetProvider(providerManifest.Id, out var providerSlot))
            {
                return PlatformResult<IPlatformProvider>.Failure(
                    PlatformErrorCode.ContentUnavailable,
                    $"Provider '{providerManifest.Id}' was rejected while its plugin was activated.");
            }

            return await providerSlot.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _activationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var loaded = _loaded;
                _loaded = null;
                if (loaded is not null)
                {
                    await loaded.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _activationGate.Release();
            }
        }

        private async Task<PlatformResult<LoadedPlugin>> EnsureActivatedAsync(CancellationToken cancellationToken)
        {
            if (_loaded is not null)
            {
                return PlatformResult<LoadedPlugin>.Success(_loaded);
            }

            if (_terminalFailure is not null)
            {
                return PlatformResult<LoadedPlugin>.Failure(_terminalFailure);
            }

            try
            {
                await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PlatformResult<LoadedPlugin>.Failure(
                    PlatformErrorCode.Cancelled,
                    "The platform operation was cancelled by the caller.");
            }

            try
            {
                if (_disposed)
                {
                    return PlatformResult<LoadedPlugin>.Failure(
                        PlatformErrorCode.ServiceUnavailable,
                        "The platform plugin host is shutting down.");
                }

                if (_loaded is not null)
                {
                    return PlatformResult<LoadedPlugin>.Success(_loaded);
                }

                if (_terminalFailure is not null)
                {
                    return PlatformResult<LoadedPlugin>.Failure(_terminalFailure);
                }

                return await ActivateCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activationGate.Release();
            }
        }

        private async Task<PlatformResult<LoadedPlugin>> ActivateCoreAsync(CancellationToken cancellationToken)
        {
            if (!await _host.IsTrustedAsync(_manifest, cancellationToken).ConfigureAwait(false))
                return PlatformResult<LoadedPlugin>.Failure(PlatformErrorCode.Forbidden, "Plugin approval is missing or the installed payload changed.");
            PlatformPluginLoadContext? loadContext = null;
            IAuralisPlatformPlugin? plugin = null;
            IReadOnlyList<IPlatformProvider>? createdProviders = null;

            try
            {
                try
                {
                    loadContext = new PlatformPluginLoadContext(_manifest.EntryAssemblyPath, _manifest.Id);
                    var assembly = loadContext.LoadFromAssemblyPath(_manifest.EntryAssemblyPath);
                    var entryType = assembly.GetType(_manifest.EntryType, throwOnError: false, ignoreCase: false)
                        ?? throw new PluginActivationException(
                            PlatformPluginDiagnosticCode.EntryTypeMissing,
                            "The entry type declared by the manifest does not exist in the entry assembly.");

                    if (!typeof(IAuralisPlatformPlugin).IsAssignableFrom(entryType) ||
                        entryType.IsAbstract ||
                        entryType.IsInterface)
                    {
                        throw new PluginActivationException(
                            PlatformPluginDiagnosticCode.EntryTypeInvalid,
                            "The entry type must be a concrete IAuralisPlatformPlugin implementation.");
                    }

                    object? instance;
                    try
                    {
                        instance = Activator.CreateInstance(entryType);
                    }
                    catch (Exception exception)
                    {
                        throw new PluginActivationException(
                            PlatformPluginDiagnosticCode.PluginConstructionFailed,
                            "The plugin entry type could not be constructed with a public parameterless constructor.",
                            exception);
                    }

                    plugin = instance as IAuralisPlatformPlugin
                        ?? throw new PluginActivationException(
                            PlatformPluginDiagnosticCode.EntryTypeInvalid,
                            "The entry type did not preserve the shared platform contract type identity.");
                }
                catch (PluginActivationException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new PluginActivationException(
                        PlatformPluginDiagnosticCode.AssemblyLoadFailed,
                        "The plugin entry assembly or one of its dependencies could not be loaded.",
                        exception);
                }

                PlatformPluginDescriptor descriptor;
                try
                {
                    descriptor = plugin.Descriptor
                        ?? throw new InvalidOperationException("The plugin descriptor was null.");
                }
                catch (Exception exception)
                {
                    throw new PluginActivationException(
                        PlatformPluginDiagnosticCode.PluginDescriptorInvalid,
                        "The plugin descriptor could not be read.",
                        exception);
                }

                if (!DescriptorMatchesManifest(descriptor, _manifest))
                {
                    throw new PluginActivationException(
                        PlatformPluginDiagnosticCode.PluginDescriptorMismatch,
                        "The runtime plugin descriptor does not match platform.plugin.json.");
                }

                PlatformHostContext context;
                try
                {
                    context = (_options.ContextFactory is IManifestPlatformHostContextFactory manifestFactory
                        ? manifestFactory.CreateContext(_manifest) : _options.ContextFactory.CreateContext(_manifest.Id))
                        ?? throw new InvalidOperationException("The context factory returned null.");
                    if (!string.Equals(context.PluginId, _manifest.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The context belongs to a different plugin scope.");
                    }
                }
                catch (Exception exception)
                {
                    throw new PluginActivationException(
                        PlatformPluginDiagnosticCode.ContextCreationFailed,
                        "The host could not create correctly scoped services for the plugin.",
                        exception);
                }

                var creation = await PlatformPluginInvocation.InvokeAsync(
                    token => plugin.CreateProvidersAsync(context, token),
                    _options.OperationTimeout,
                    cancellationToken,
                    exception => _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.PluginOperationFailed,
                        "The plugin threw while creating its providers.",
                        exception: exception)),
                    "The plugin failed while creating its providers.").ConfigureAwait(false);

                if (!creation.IsSuccess)
                {
                    _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.ProviderCreationFailed,
                        $"Plugin provider creation failed: {creation.Error!.Code}."));
                    CacheFailureWhenAppropriate(creation.Error);
                    return PlatformResult<LoadedPlugin>.Failure(creation.Error);
                }

                createdProviders = creation.Value ?? Array.Empty<IPlatformProvider>();
                var loaded = await LoadedPlugin.CreateAsync(
                    _host,
                    _manifest,
                    loadContext,
                    plugin,
                    createdProviders,
                    _options.OperationTimeout).ConfigureAwait(false);

                _loaded = loaded;
                loadContext = null;
                plugin = null;
                createdProviders = null;
                return PlatformResult<LoadedPlugin>.Success(loaded);
            }
            catch (PluginActivationException exception)
            {
                var error = new PlatformError(
                    PlatformErrorCode.ConfigurationRequired,
                    "The platform plugin could not be activated.");
                _terminalFailure = error;
                _host.RecordDiagnostic(Diagnostic(exception.Code, exception.Message, exception.InnerException));
                return PlatformResult<LoadedPlugin>.Failure(error);
            }
            catch (Exception exception)
            {
                var error = new PlatformError(
                    PlatformErrorCode.Unknown,
                    "The platform plugin failed during activation.");
                _terminalFailure = error;
                _host.RecordDiagnostic(Diagnostic(
                    PlatformPluginDiagnosticCode.PluginConstructionFailed,
                    "An unexpected activation failure was isolated to this plugin.",
                    exception));
                return PlatformResult<LoadedPlugin>.Failure(error);
            }
            finally
            {
                if (loadContext is not null)
                {
                    await DisposePartialActivationAsync(createdProviders, plugin, loadContext).ConfigureAwait(false);
                }
            }
        }

        private async Task DisposePartialActivationAsync(
            IReadOnlyList<IPlatformProvider>? providers,
            IAuralisPlatformPlugin? plugin,
            PlatformPluginLoadContext loadContext)
        {
            if (providers is not null)
            {
                foreach (var provider in providers.Distinct<IPlatformProvider>(ReferenceEqualityComparer.Instance))
                {
                    await DisposeOneAsync(provider, providerId: null).ConfigureAwait(false);
                }
            }

            if (plugin is not null)
            {
                try
                {
                    await plugin.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.PluginDisposalFailed,
                        "The plugin threw while a failed activation was being cleaned up.",
                        exception));
                }
            }

            loadContext.Unload();
        }

        private async Task DisposeOneAsync(IPlatformProvider provider, string? providerId)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.PluginDisposalFailed,
                    PlatformPluginDiagnosticSeverity.Warning,
                    "A provider threw while a failed activation was being cleaned up.",
                    _manifest.Id,
                    providerId,
                    _manifest.EntryAssemblyPath,
                    exception));
            }
        }

        private void CacheFailureWhenAppropriate(PlatformError error)
        {
            if (error.Code != PlatformErrorCode.Cancelled &&
                (error.Code == PlatformErrorCode.Timeout || !error.IsTransient))
            {
                _terminalFailure = error;
            }
        }

        private PlatformPluginDiagnostic Diagnostic(
            PlatformPluginDiagnosticCode code,
            string message,
            Exception? exception = null,
            string? providerId = null,
            PlatformPluginDiagnosticSeverity severity = PlatformPluginDiagnosticSeverity.Error) =>
            new(
                code,
                severity,
                message,
                _manifest.Id,
                providerId,
                _manifest.EntryAssemblyPath,
                exception);

        private static bool DescriptorMatchesManifest(
            PlatformPluginDescriptor descriptor,
            PlatformPluginManifest manifest) =>
            string.Equals(descriptor.Id, manifest.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(descriptor.DisplayName, manifest.DisplayName, StringComparison.Ordinal) &&
            descriptor.Version == manifest.Version &&
            descriptor.MinimumHostApiVersion == manifest.MinimumHostApiVersion &&
            descriptor.MaximumHostApiVersion == manifest.MaximumHostApiVersion &&
            descriptor.IsCompatibleWithCurrentHost;
    }

    private sealed class LoadedPlugin : IAsyncDisposable
    {
        private readonly PlatformPluginHost _host;
        private readonly PlatformPluginManifest _manifest;
        private readonly PlatformPluginLoadContext _loadContext;
        private readonly IAuralisPlatformPlugin _plugin;
        private readonly IReadOnlyDictionary<string, LoadedProviderSlot> _providers;
        private int _disposed;

        private LoadedPlugin(
            PlatformPluginHost host,
            PlatformPluginManifest manifest,
            PlatformPluginLoadContext loadContext,
            IAuralisPlatformPlugin plugin,
            IReadOnlyDictionary<string, LoadedProviderSlot> providers)
        {
            _host = host;
            _manifest = manifest;
            _loadContext = loadContext;
            _plugin = plugin;
            _providers = providers;
        }

        internal static async Task<LoadedPlugin> CreateAsync(
            PlatformPluginHost host,
            PlatformPluginManifest manifest,
            PlatformPluginLoadContext loadContext,
            IAuralisPlatformPlugin plugin,
            IReadOnlyList<IPlatformProvider> providers,
            TimeSpan operationTimeout)
        {
            var declared = manifest.Providers.ToDictionary(provider => provider.Id, IdComparer);
            var candidates = new List<ProviderCandidate>();

            foreach (var provider in providers)
            {
                try
                {
                    var descriptor = provider?.Descriptor
                        ?? throw new InvalidOperationException("The provider or its descriptor was null.");
                    candidates.Add(new ProviderCandidate(provider, descriptor));
                }
                catch (Exception exception)
                {
                    host.RecordDiagnostic(new PlatformPluginDiagnostic(
                        PlatformPluginDiagnosticCode.ProviderDescriptorInvalid,
                        PlatformPluginDiagnosticSeverity.Error,
                        "A provider descriptor could not be read; that provider was rejected.",
                        manifest.Id,
                        path: manifest.EntryAssemblyPath,
                        exception: exception));
                    if (provider is not null)
                    {
                        await DisposeRejectedProviderAsync(host, manifest, provider, null).ConfigureAwait(false);
                    }
                }
            }

            var duplicates = candidates
                .GroupBy(candidate => candidate.Descriptor.Id, IdComparer)
                .Where(group => group.Count() > 1)
                .ToDictionary(group => group.Key, group => group.ToArray(), IdComparer);

            foreach (var group in duplicates.Values)
            {
                foreach (var candidate in group)
                {
                    host.RecordDiagnostic(new PlatformPluginDiagnostic(
                        PlatformPluginDiagnosticCode.DuplicateProviderId,
                        PlatformPluginDiagnosticSeverity.Error,
                        $"Runtime provider ID '{candidate.Descriptor.Id}' appeared more than once; every conflicting provider was rejected.",
                        manifest.Id,
                        candidate.Descriptor.Id,
                        manifest.EntryAssemblyPath));
                }

                foreach (var provider in group
                             .Select(candidate => candidate.Provider)
                             .Distinct<IPlatformProvider>(ReferenceEqualityComparer.Instance))
                {
                    await DisposeRejectedProviderAsync(host, manifest, provider, group[0].Descriptor.Id).ConfigureAwait(false);
                }
            }

            var accepted = new Dictionary<string, LoadedProviderSlot>(IdComparer);
            foreach (var candidate in candidates.Where(candidate => !duplicates.ContainsKey(candidate.Descriptor.Id)))
            {
                if (!declared.TryGetValue(candidate.Descriptor.Id, out var providerManifest))
                {
                    host.RecordDiagnostic(new PlatformPluginDiagnostic(
                        PlatformPluginDiagnosticCode.ProviderDescriptorMismatch,
                        PlatformPluginDiagnosticSeverity.Error,
                        "The plugin returned a provider that is not declared by its manifest.",
                        manifest.Id,
                        candidate.Descriptor.Id,
                        manifest.EntryAssemblyPath));
                    await DisposeRejectedProviderAsync(
                        host,
                        manifest,
                        candidate.Provider,
                        candidate.Descriptor.Id).ConfigureAwait(false);
                    continue;
                }

                var manifestCapabilities = providerManifest.Capabilities.ToHashSet();
                var runtimeCapabilities = candidate.Descriptor.Capabilities.ToHashSet();
                var metadataMatches = string.Equals(
                                          candidate.Descriptor.DisplayName,
                                          providerManifest.DisplayName,
                                          StringComparison.Ordinal) &&
                                      manifestCapabilities.SetEquals(runtimeCapabilities);
                var interfacesMatch = manifestCapabilities.All(
                    capability => PlatformCapabilityMap.Implements(candidate.Provider, capability));

                if (!metadataMatches || !interfacesMatch)
                {
                    host.RecordDiagnostic(new PlatformPluginDiagnostic(
                        interfacesMatch
                            ? PlatformPluginDiagnosticCode.ProviderDescriptorMismatch
                            : PlatformPluginDiagnosticCode.CapabilityMismatch,
                        PlatformPluginDiagnosticSeverity.Error,
                        "The runtime provider metadata or capability interfaces do not match its manifest; the provider was rejected.",
                        manifest.Id,
                        candidate.Descriptor.Id,
                        manifest.EntryAssemblyPath));
                    await DisposeRejectedProviderAsync(
                        host,
                        manifest,
                        candidate.Provider,
                        candidate.Descriptor.Id).ConfigureAwait(false);
                    continue;
                }

                accepted.Add(
                    candidate.Descriptor.Id,
                    new LoadedProviderSlot(
                        host,
                        manifest,
                        providerManifest,
                        candidate.Provider,
                        operationTimeout));
            }

            foreach (var missing in declared.Keys.Where(id => !accepted.ContainsKey(id)))
            {
                host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.ProviderMissing,
                    PlatformPluginDiagnosticSeverity.Error,
                    "A provider declared by the manifest was not returned as a valid runtime provider.",
                    manifest.Id,
                    missing,
                    manifest.EntryAssemblyPath));
            }

            return new LoadedPlugin(
                host,
                manifest,
                loadContext,
                plugin,
                new ReadOnlyDictionary<string, LoadedProviderSlot>(accepted));
        }

        internal bool TryGetProvider(string providerId, out LoadedProviderSlot slot) =>
            _providers.TryGetValue(providerId, out slot!);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var provider in _providers.Values)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await _plugin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.PluginDisposalFailed,
                    PlatformPluginDiagnosticSeverity.Warning,
                    "The plugin threw during disposal; the failure was isolated.",
                    _manifest.Id,
                    path: _manifest.EntryAssemblyPath,
                    exception: exception));
            }
            finally
            {
                _loadContext.Unload();
            }
        }

        private static async Task DisposeRejectedProviderAsync(
            PlatformPluginHost host,
            PlatformPluginManifest manifest,
            IPlatformProvider provider,
            string? providerId)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                host.RecordDiagnostic(new PlatformPluginDiagnostic(
                    PlatformPluginDiagnosticCode.PluginDisposalFailed,
                    PlatformPluginDiagnosticSeverity.Warning,
                    "A rejected provider threw during disposal; the failure was isolated.",
                    manifest.Id,
                    providerId,
                    manifest.EntryAssemblyPath,
                    exception));
            }
        }

        private sealed record ProviderCandidate(
            IPlatformProvider Provider,
            PlatformProviderDescriptor Descriptor);
    }

    private sealed class LoadedProviderSlot : IAsyncDisposable
    {
        private readonly PlatformPluginHost _host;
        private readonly PlatformPluginManifest _pluginManifest;
        private readonly PlatformProviderManifest _providerManifest;
        private readonly IPlatformProvider _provider;
        private readonly TimeSpan _operationTimeout;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);

        private PlatformError? _initializationFailure;
        private bool _initialized;
        private bool _disposed;

        internal LoadedProviderSlot(
            PlatformPluginHost host,
            PlatformPluginManifest pluginManifest,
            PlatformProviderManifest providerManifest,
            IPlatformProvider provider,
            TimeSpan operationTimeout)
        {
            _host = host;
            _pluginManifest = pluginManifest;
            _providerManifest = providerManifest;
            _provider = provider;
            _operationTimeout = operationTimeout;
        }

        internal async Task<PlatformResult<IPlatformProvider>> EnsureInitializedAsync(
            CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return PlatformResult<IPlatformProvider>.Success(_provider);
            }

            if (_initializationFailure is not null)
            {
                return PlatformResult<IPlatformProvider>.Failure(_initializationFailure);
            }

            try
            {
                await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PlatformResult<IPlatformProvider>.Failure(
                    PlatformErrorCode.Cancelled,
                    "The platform operation was cancelled by the caller.");
            }

            try
            {
                if (_disposed)
                {
                    return PlatformResult<IPlatformProvider>.Failure(
                        PlatformErrorCode.ServiceUnavailable,
                        "The provider is shutting down.");
                }

                if (_initialized)
                {
                    return PlatformResult<IPlatformProvider>.Success(_provider);
                }

                if (_initializationFailure is not null)
                {
                    return PlatformResult<IPlatformProvider>.Failure(_initializationFailure);
                }

                var initialization = await PlatformPluginInvocation.InvokeAsync(
                    token => _provider.InitializeAsync(token),
                    _operationTimeout,
                    cancellationToken,
                    exception => _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.PluginOperationFailed,
                        "The provider threw during initialization.",
                        exception)),
                    "The provider failed during initialization.").ConfigureAwait(false);

                if (!initialization.IsSuccess)
                {
                    _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.ProviderInitializationFailed,
                        $"Provider initialization failed: {initialization.Error!.Code}."));

                    if (initialization.Error.Code != PlatformErrorCode.Cancelled &&
                        (initialization.Error.Code == PlatformErrorCode.Timeout || !initialization.Error.IsTransient))
                    {
                        _initializationFailure = initialization.Error;
                    }

                    return PlatformResult<IPlatformProvider>.Failure(initialization.Error);
                }

                _initialized = true;
                return PlatformResult<IPlatformProvider>.Success(_provider);
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _initializationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    await _provider.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _host.RecordDiagnostic(Diagnostic(
                        PlatformPluginDiagnosticCode.PluginDisposalFailed,
                        "The provider threw during disposal; the failure was isolated.",
                        exception,
                        PlatformPluginDiagnosticSeverity.Warning));
                }
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private PlatformPluginDiagnostic Diagnostic(
            PlatformPluginDiagnosticCode code,
            string message,
            Exception? exception = null,
            PlatformPluginDiagnosticSeverity severity = PlatformPluginDiagnosticSeverity.Error) =>
            new(
                code,
                severity,
                message,
                _pluginManifest.Id,
                _providerManifest.Id,
                _pluginManifest.EntryAssemblyPath,
                exception);
    }

    private sealed class PluginActivationException : Exception
    {
        internal PluginActivationException(
            PlatformPluginDiagnosticCode code,
            string message,
            Exception? innerException = null)
            : base(message, innerException) => Code = code;

        internal PlatformPluginDiagnosticCode Code { get; }
    }
}
