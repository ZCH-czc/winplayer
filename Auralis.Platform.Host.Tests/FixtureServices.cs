using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;
namespace Auralis.Platform.Host.Tests;

internal sealed class FixtureServices : IPlatformHostContextFactory, IPlatformHttpClientFactory,
    IPlatformCache, IPlatformSettings, IPlatformLogger, IPlatformTimeProvider
{
    internal int Contexts;
    internal int HttpClients;
    internal MemoryStore Credentials { get; } = new();
    public PlatformHostContext CreateContext(string id)
    {
        Contexts++;
        return new(id, this, Credentials, this, this, this, this);
    }
    public HttpClient CreateClient(string name) { HttpClients++; throw new InvalidOperationException("Fixture must not access HTTP"); }
    ValueTask<PlatformCacheEntry?> IPlatformCache.GetAsync(string key, CancellationToken token) => ValueTask.FromResult<PlatformCacheEntry?>(null);
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> data, DateTimeOffset? expiry, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask RemoveAsync(string key, CancellationToken token) => ValueTask.CompletedTask;
    ValueTask<string?> IPlatformSettings.GetAsync(string key, CancellationToken token) => ValueTask.FromResult<string?>(null);
    public void Log(PlatformLogLevel level, string name, string message, Exception? exception = null) { }
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken token) => await Task.Delay(delay, token);
}
internal sealed class MemoryStore : IPlatformCredentialStore
{
    internal readonly Dictionary<string, byte[]> Values = new();
    internal int Reads;
    public ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Reads++;
        return ValueTask.FromResult(Values.TryGetValue(key, out var bytes) ? new PlatformCredential(bytes) : null);
    }
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> data, DateTimeOffset? expiry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Values[key] = data.ToArray(); return ValueTask.CompletedTask;
    }
    public ValueTask DeleteAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Values.Remove(key); return ValueTask.CompletedTask;
    }
}

// The test executable doubles as a synthetic plugin: no platform code, account, HTTP or music required.
public sealed class FixturePlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor { get; } = new("tests.approved", "Fixture", new Version(1, 0, 0), 1, 1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new FixtureProvider()]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class FixtureProvider : IPlatformProvider, IPlatformLyricsLookupCapability
{
    public PlatformProviderDescriptor Descriptor { get; } = new("fixture", "Fixture", new Version(1, 0, 0), [PlatformCapabilityKind.LyricsLookup]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public Task<PlatformResult<PlatformLyricsLookupResult>> LookupAsync(PlatformLyricsLookupRequest request, CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformLyricsLookupResult>.Success(new("fixture", "[00:01]test")));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
