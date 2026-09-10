using System.Security.Cryptography;

namespace Auralis.Platform.Abstractions;

/// <summary>
/// Creates host-managed HTTP clients for a plugin. Clients may enforce proxy, timeout, certificate,
/// destination allow-list, user-agent, and telemetry policy; plugins should not create their own handlers.
/// </summary>
public interface IPlatformHttpClientFactory
{
    /// <summary>Creates or obtains a host-managed client for a logical purpose within the current plugin scope.</summary>
    HttpClient CreateClient(string clientName);
}

/// <summary>
/// A disposable secret retrieved from the host credential vault. Its bytes are zeroed on disposal;
/// callers should keep the lifetime short and must never log, cache, or expose the value to a frontend.
/// </summary>
public sealed class PlatformCredential : IDisposable
{
    private byte[]? _secret;

    /// <summary>Creates a credential by defensively copying secret bytes.</summary>
    public PlatformCredential(ReadOnlySpan<byte> secret, DateTimeOffset? expiresAt = null)
    {
        _secret = secret.ToArray();
        ExpiresAt = expiresAt;
    }

    /// <summary>Gets a read-only view of the secret while this object is alive.</summary>
    public ReadOnlyMemory<byte> Secret => _secret is { } secret
        ? secret
        : throw new ObjectDisposedException(nameof(PlatformCredential));

    /// <summary>Gets the optional expiration instant.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Returns whether the credential has expired according to the supplied clock.</summary>
    public bool IsExpired(IPlatformTimeProvider timeProvider) =>
        ExpiresAt is { } expiry && expiry <= (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow();

    /// <summary>Zeros the in-memory bytes and releases this credential.</summary>
    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}

/// <summary>
/// Provides a plugin-scoped credential vault. The host must namespace keys by plugin so one plugin
/// cannot read another plugin's credentials. Implementations must copy incoming bytes before returning.
/// </summary>
public interface IPlatformCredentialStore
{
    /// <summary>Retrieves a credential, or <see langword="null"/> when the scoped key is absent.</summary>
    ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores secret bytes in the host credential vault.</summary>
    ValueTask SetAsync(
        string key,
        ReadOnlyMemory<byte> secret,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Deletes a credential in the current plugin scope.</summary>
    ValueTask DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Represents non-sensitive bytes read from a plugin-scoped cache.</summary>
public sealed record PlatformCacheEntry
{
    /// <summary>Creates a cache entry by defensively copying its data.</summary>
    public PlatformCacheEntry(ReadOnlySpan<byte> data, DateTimeOffset? expiresAt = null)
    {
        Data = data.ToArray();
        ExpiresAt = expiresAt;
    }

    /// <summary>Gets cached non-sensitive bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the optional expiration instant.</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>
/// Provides a plugin-scoped cache for non-sensitive provider data. Credentials, cookies, signed URLs,
/// stream headers, and authentication challenges must never be placed in this cache.
/// </summary>
public interface IPlatformCache
{
    /// <summary>Retrieves a non-sensitive cache entry.</summary>
    ValueTask<PlatformCacheEntry?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores non-sensitive bytes in the current plugin scope.</summary>
    ValueTask SetAsync(
        string key,
        ReadOnlyMemory<byte> data,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Removes an entry in the current plugin scope.</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Exposes plugin-scoped, read-only, non-sensitive settings such as an administrator-approved gateway
/// address or per-source feature switches. Secrets must instead use <see cref="IPlatformCredentialStore"/>.
/// </summary>
public interface IPlatformSettings
{
    /// <summary>Gets a non-sensitive string setting, or <see langword="null"/> when it is not configured.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Defines platform log severity.</summary>
public enum PlatformLogLevel
{
    /// <summary>Detailed diagnostic information.</summary>
    Trace,

    /// <summary>Ordinary diagnostic information.</summary>
    Debug,

    /// <summary>Normal lifecycle information.</summary>
    Information,

    /// <summary>A recoverable or degraded condition.</summary>
    Warning,

    /// <summary>An operation failure.</summary>
    Error,

    /// <summary>A provider-level failure that prevents continued operation.</summary>
    Critical
}

/// <summary>
/// Receives sanitized plugin diagnostics. Plugins must not include credentials, cookies, signed URLs,
/// stream headers, authorization callbacks, or raw response bodies in messages or exceptions.
/// </summary>
public interface IPlatformLogger
{
    /// <summary>Writes a sanitized event to the host logger.</summary>
    void Log(PlatformLogLevel level, string eventName, string message, Exception? exception = null);
}

/// <summary>Provides deterministic host time and cancellation-aware delays.</summary>
public interface IPlatformTimeProvider
{
    /// <summary>Gets the current UTC instant.</summary>
    DateTimeOffset GetUtcNow();

    /// <summary>Waits through the host time source while observing cancellation.</summary>
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// The only host service surface supplied to an in-process platform plugin. Each instance is scoped to
/// one verified plugin ID. This contract reduces accidental coupling and secret leakage, but it is not
/// an operating-system security sandbox: the host must load only trusted plugin assemblies.
/// </summary>
public sealed class PlatformHostContext
{
    /// <summary>Creates a plugin-scoped host context.</summary>
    public PlatformHostContext(
        string pluginId,
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformCredentialStore credentialStore,
        IPlatformCache cache,
        IPlatformSettings settings,
        IPlatformLogger logger,
        IPlatformTimeProvider timeProvider)
    {
        PluginId = PlatformIdRules.EnsureScopedId(pluginId, nameof(pluginId));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        CredentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Gets the stable API version implemented by this host.</summary>
    public int HostApiVersion => PlatformContract.HostApiVersion;

    /// <summary>Gets the plugin ID whose scope owns all services in this context.</summary>
    public string PluginId { get; }

    /// <summary>Gets the host-managed HTTP client factory.</summary>
    public IPlatformHttpClientFactory HttpClientFactory { get; }

    /// <summary>Gets the plugin-scoped secret vault.</summary>
    public IPlatformCredentialStore CredentialStore { get; }

    /// <summary>Gets the plugin-scoped, non-sensitive cache.</summary>
    public IPlatformCache Cache { get; }

    /// <summary>Gets plugin-scoped, read-only, non-sensitive settings.</summary>
    public IPlatformSettings Settings { get; }

    /// <summary>Gets the sanitized host logger.</summary>
    public IPlatformLogger Logger { get; }

    /// <summary>Gets the host time provider.</summary>
    public IPlatformTimeProvider TimeProvider { get; }
}
