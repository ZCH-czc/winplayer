using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.Services;

/// <summary>
/// Supplies narrowly scoped, host-owned services to trusted platform plugins. Non-sensitive settings
/// stay in the Auralis settings store, optional gateway credentials stay in Windows Credential Manager, and ordinary
/// response caching remains process-local.
/// </summary>
internal sealed class DefaultPlatformHostContextFactory : IManifestPlatformHostContextFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, PluginScope> _scopes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scopeGate = new();
    private readonly HashSet<string> _revokedScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger _logger;
    private readonly PlatformSettingsStore _settings;
    private readonly Func<string, IPlatformCredentialStore> _credentialStores;
    private int _disposed;

    internal DefaultPlatformHostContextFactory(PlatformSettingsStore settings)
        : this(settings, NullAppLogger.Instance)
    {
    }

    internal DefaultPlatformHostContextFactory(PlatformSettingsStore settings, IAppLogger logger,
        Func<string, IPlatformCredentialStore>? credentialStores = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credentialStores = credentialStores ?? (scope => new WindowsPlatformCredentialStore(scope));
    }

    public PlatformHostContext CreateContext(string pluginId)
        => GetScope(pluginId, null).Context;

    public PlatformHostContext CreateContext(PlatformPluginManifest manifest)
        => GetScope(manifest.Id, manifest).Context;

    private PluginScope GetScope(string pluginId, PlatformPluginManifest? manifest)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        // The host supplies only an integrity-approved manifest at activation. Keep the first
        // session's routes pinned, including during revocation/cleanup of an updated package.
        lock (_scopeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_scopes.TryGetValue(pluginId, out var existing)) return existing;
            if (_revokedScopes.Contains(pluginId)) throw new InvalidOperationException("Plugin context revoked until restart.");
            return _scopes.GetOrAdd(pluginId, id => new PluginScope(id, manifest, _settings, _logger, _credentialStores));
        }
    }

    internal Task RevokeCredentialsAsync(string pluginId, PlatformPluginManifest? manifest, CancellationToken token)
    {
        lock (_scopeGate)
        {
            var scope = GetScope(pluginId, manifest);
            _revokedScopes.Add(pluginId);
            return scope.RevokeAndClearAsync(token);
        }
    }

    internal async Task<bool> RevokeExistingCredentialsAsync(string pluginId, CancellationToken token)
    {
        Task revoke;
        lock (_scopeGate)
        {
            // A provider activation already in flight must not create a fresh writable scope later.
            _revokedScopes.Add(pluginId);
            if (!_scopes.TryGetValue(pluginId, out var scope)) return false;
            revoke = scope.RevokeAndClearAsync(token);
        }
        await revoke.ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var scope in _scopes.Values)
        {
            scope.Dispose();
        }

        _scopes.Clear();
    }

    private sealed class PluginScope : IDisposable
    {
        private readonly HostManagedHttpClientFactory _httpClientFactory;
        private readonly IPlatformCredentialStore _credentialStore;
        internal RevocablePlatformCredentialStore Credentials { get; }
        private readonly string[] _declaredCredentialKeys;
        private readonly InMemoryPlatformCache _cache;

        internal PluginScope(string pluginId, PlatformPluginManifest? manifest, PlatformSettingsStore settings, IAppLogger logger,
            Func<string, IPlatformCredentialStore> credentialStores)
        {
            _httpClientFactory = new HostManagedHttpClientFactory(pluginId);
            var current = credentialStores(pluginId);
            IPlatformCredentialStore routed = new DeclaredPlatformCredentialStore(current, manifest?.CredentialAliases ?? [], credentialStores);
            Credentials = new RevocablePlatformCredentialStore(routed);
            _declaredCredentialKeys = (manifest?.CredentialAliases ?? []).Select(a => a.Key).ToArray();
            _credentialStore = Credentials;
            _cache = new InMemoryPlatformCache(SystemPlatformTimeProvider.Instance);

            Context = new PlatformHostContext(
                pluginId,
                _httpClientFactory,
                _credentialStore,
                _cache,
                settings.ForPlugin(pluginId),
                new PlatformAppLoggerAdapter(pluginId, logger),
                SystemPlatformTimeProvider.Instance);
        }

        internal PlatformHostContext Context { get; }

        internal async Task RevokeAndClearAsync(CancellationToken token)
        {
            await Credentials.RevokeAsync(token).ConfigureAwait(false);
            var failed = false;
            // Clear approved, pinned addresses even for plugins without an Authentication capability
            // (e.g. credentials for a configured service). Do not enumerate any credential store.
            foreach (var key in _declaredCredentialKeys)
            {
                try { await Credentials.DeleteAsync(key, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { failed = true; }
            }
            if (failed) throw new InvalidOperationException("Plugin credential cleanup incomplete.");
        }

        public void Dispose()
        {
            _httpClientFactory.Dispose();
            if (_credentialStore is IDisposable disposableCredentialStore)
            {
                disposableCredentialStore.Dispose();
            }
            _cache.Dispose();
        }
    }
}

/// <summary>
/// Creates shared, host-configured clients for one plugin scope. Handler construction is local and inert;
/// a socket is opened only if a plugin later sends an explicitly requested operation.
/// </summary>
internal sealed class HostManagedHttpClientFactory : IPlatformHttpClientFactory, IDisposable
{
    private const int MaximumNamedClients = 32;

    private readonly object _gate = new();
    private readonly string _pluginId;
    private readonly SocketsHttpHandler _handler;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal HostManagedHttpClientFactory(string pluginId)
    {
        _pluginId = pluginId;
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 8,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false
        };
    }

    public HttpClient CreateClient(string clientName)
    {
        ValidateKey(clientName, nameof(clientName));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(clientName, out var existing))
            {
                return existing;
            }

            if (_clients.Count >= MaximumNamedClients)
            {
                throw new InvalidOperationException(
                    $"Platform plugin '{_pluginId}' exceeded its host-managed HTTP client-name limit.");
            }

            var client = new HttpClient(_handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Auralis", "0.5"));
            _clients.Add(clientName, client);
            return client;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
            _handler.Dispose();
        }
    }

    internal static void ValidateKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        if (key.Length > 256)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A platform-scoped key cannot exceed 256 characters.");
        }
    }
}

/// <summary>
/// An ephemeral credential vault. Secrets never leave process memory, are copied on every boundary, and
/// are zeroed when replaced, deleted, expired, or when the application shuts down.
/// </summary>
internal sealed class InMemoryPlatformCredentialStore : IPlatformCredentialStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SecretEntry> _entries = new(StringComparer.Ordinal);
    private readonly IPlatformTimeProvider _timeProvider;
    private bool _disposed;

    internal InMemoryPlatformCredentialStore(IPlatformTimeProvider timeProvider) =>
        _timeProvider = timeProvider;

    public ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return ValueTask.FromResult<PlatformCredential?>(null);
            }

            if (entry.ExpiresAt is { } expiry && expiry <= _timeProvider.GetUtcNow())
            {
                RemoveAndZero(key, entry);
                return ValueTask.FromResult<PlatformCredential?>(null);
            }

            return ValueTask.FromResult<PlatformCredential?>(
                new PlatformCredential(entry.Secret, entry.ExpiresAt));
        }
    }

    public ValueTask SetAsync(
        string key,
        ReadOnlyMemory<byte> secret,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));
        if (secret.IsEmpty)
        {
            throw new ArgumentException("A credential cannot be empty.", nameof(secret));
        }

        var ownedSecret = secret.ToArray();
        lock (_gate)
        {
            if (_disposed)
            {
                CryptographicOperations.ZeroMemory(ownedSecret);
                throw new ObjectDisposedException(nameof(InMemoryPlatformCredentialStore));
            }

            if (_entries.Remove(key, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous.Secret);
            }

            _entries.Add(key, new SecretEntry(ownedSecret, expiresAt));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Remove(key, out var entry))
            {
                CryptographicOperations.ZeroMemory(entry.Secret);
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Secret);
            }

            _entries.Clear();
        }
    }

    private void RemoveAndZero(string key, SecretEntry entry)
    {
        _entries.Remove(key);
        CryptographicOperations.ZeroMemory(entry.Secret);
    }

    private sealed record SecretEntry(byte[] Secret, DateTimeOffset? ExpiresAt);
}

/// <summary>A bounded, process-local cache for non-sensitive plugin data.</summary>
internal sealed class InMemoryPlatformCache : IPlatformCache, IDisposable
{
    private const int MaximumEntryBytes = 2 * 1024 * 1024;
    private const int MaximumTotalBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheRecord> _entries = new(StringComparer.Ordinal);
    private readonly IPlatformTimeProvider _timeProvider;
    private long _sequence;
    private int _totalBytes;
    private bool _disposed;

    internal InMemoryPlatformCache(IPlatformTimeProvider timeProvider) => _timeProvider = timeProvider;

    public ValueTask<PlatformCacheEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return ValueTask.FromResult<PlatformCacheEntry?>(null);
            }

            if (entry.ExpiresAt is { } expiry && expiry <= _timeProvider.GetUtcNow())
            {
                RemoveAndClear(key, entry);
                return ValueTask.FromResult<PlatformCacheEntry?>(null);
            }

            entry.LastAccess = ++_sequence;
            return ValueTask.FromResult<PlatformCacheEntry?>(
                new PlatformCacheEntry(entry.Data, entry.ExpiresAt));
        }
    }

    public ValueTask SetAsync(
        string key,
        ReadOnlyMemory<byte> data,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));
        if (data.Length > MaximumEntryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "A platform cache entry cannot exceed 2 MiB.");
        }

        var ownedData = data.ToArray();
        lock (_gate)
        {
            if (_disposed)
            {
                CryptographicOperations.ZeroMemory(ownedData);
                throw new ObjectDisposedException(nameof(InMemoryPlatformCache));
            }

            if (_entries.Remove(key, out var previous))
            {
                _totalBytes -= previous.Data.Length;
                CryptographicOperations.ZeroMemory(previous.Data);
            }

            RemoveExpiredEntries();
            while (_entries.Count > 0 && _totalBytes + ownedData.Length > MaximumTotalBytes)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastAccess);
                RemoveAndClear(oldest.Key, oldest.Value);
            }

            _entries.Add(key, new CacheRecord(ownedData, expiresAt, ++_sequence));
            _totalBytes += ownedData.Length;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostManagedHttpClientFactory.ValidateKey(key, nameof(key));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var entry))
            {
                RemoveAndClear(key, entry);
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Data);
            }

            _entries.Clear();
            _totalBytes = 0;
        }
    }

    private void RemoveExpiredEntries()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _entries
                     .Where(pair => pair.Value.ExpiresAt is { } expiry && expiry <= now)
                     .ToArray())
        {
            RemoveAndClear(pair.Key, pair.Value);
        }
    }

    private void RemoveAndClear(string key, CacheRecord entry)
    {
        _entries.Remove(key);
        _totalBytes -= entry.Data.Length;
        CryptographicOperations.ZeroMemory(entry.Data);
    }

    private sealed class CacheRecord
    {
        internal CacheRecord(byte[] data, DateTimeOffset? expiresAt, long lastAccess)
        {
            Data = data;
            ExpiresAt = expiresAt;
            LastAccess = lastAccess;
        }

        internal byte[] Data { get; }

        internal DateTimeOffset? ExpiresAt { get; }

        internal long LastAccess { get; set; }
    }
}

/// <summary>
/// Bridges plugin diagnostics into the host log. Free-form provider text is deliberately discarded;
/// only normalized host-owned identifiers plus exception type/HRESULT may cross the persistence boundary.
/// </summary>
internal sealed class PlatformAppLoggerAdapter : IPlatformLogger
{
    private readonly IAppLogger _logger;
    private readonly string _pluginId;

    internal PlatformAppLoggerAdapter(string pluginId, IAppLogger logger)
    {
        _pluginId = AppLogSanitizer.NormalizeIdentifier(pluginId, "unknown-plugin");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Log(PlatformLogLevel level, string eventName, string message, Exception? exception = null)
    {
        // `message` belongs to an untrusted optional plugin and may contain opaque credentials or account data
        // that no generic redactor can identify reliably. Keep it available to the interface contract but never
        // persist it in the host log.
        _ = message;
        _logger.Log(
            MapLevel(level),
            "platform.plugin",
            $"provider.{AppLogSanitizer.NormalizeIdentifier(eventName, "event")}",
            new Dictionary<string, object?>
            {
                [AppLogFieldNames.PluginId] = _pluginId
            },
            exception);
    }

    private static AppLogLevel MapLevel(PlatformLogLevel level) => level switch
    {
        PlatformLogLevel.Trace => AppLogLevel.Trace,
        PlatformLogLevel.Debug => AppLogLevel.Debug,
        PlatformLogLevel.Information => AppLogLevel.Information,
        PlatformLogLevel.Warning => AppLogLevel.Warning,
        PlatformLogLevel.Error => AppLogLevel.Error,
        PlatformLogLevel.Critical => AppLogLevel.Critical,
        _ => AppLogLevel.Information
    };
}

internal sealed class SystemPlatformTimeProvider : IPlatformTimeProvider
{
    internal static SystemPlatformTimeProvider Instance { get; } = new();

    private SystemPlatformTimeProvider()
    {
    }

    public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, TimeProvider.System, cancellationToken));
}
