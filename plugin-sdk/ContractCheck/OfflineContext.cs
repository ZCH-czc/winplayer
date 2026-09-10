using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.PluginContractCheck;

/// <summary>Fresh per-plugin synthetic services. No Windows vault, profile, files or network handler.</summary>
public sealed class OfflineContextFactory : IPlatformHostContextFactory, IDisposable
{
    private readonly List<OfflineContext> _contexts = [];
    private readonly object _gate = new();
    private bool _disposed;
    private readonly Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _response;
    public OfflineContextFactory(Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response = null) => _response = response;
    public int Contexts { get { lock (_gate) return _contexts.Count; } }
    public int HttpRequests { get { lock (_gate) return _contexts.Sum(c => Volatile.Read(ref c.HttpRequests)); } }
    public PlatformHostContext CreateContext(string pluginId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var context = new OfflineContext(pluginId, _response);
            _contexts.Add(context);
            return new(pluginId, context, context, context, context, context, context);
        }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; foreach (var context in _contexts) context.Dispose(); } }
    private sealed class OfflineContext(string id, Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response)
        : IPlatformHttpClientFactory, IPlatformCredentialStore, IPlatformCache, IPlatformSettings, IPlatformLogger, IPlatformTimeProvider, IDisposable
    {
        internal int HttpRequests;
        private readonly object _gate = new();
        private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset? Expiry)> _secrets = [];
        private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset? Expiry)> _cache = [];
        private readonly List<HttpClient> _clients = [];
        private bool _disposed;
        // Creation may be eager; sending is forbidden and counted even if a plugin swallows the error.
        public HttpClient CreateClient(string name)
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (_clients.Count >= 64) throw new InvalidOperationException("Fixture client limit.");
                var client = new HttpClient(new DenyHandler(this)); _clients.Add(client); return client; }
        }
        private sealed class DenyHandler(OfflineContext owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref owner.HttpRequests);
                return owner.Respond(request, token);
            }
        }
        private Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token) => response is not null
            ? response(id, request, token) : throw new HttpRequestException("Contract check denies network access through host services.");
        private (byte[] Bytes, DateTimeOffset? Expiry)? Read(Dictionary<string, (byte[] Bytes, DateTimeOffset? Expiry)> store, string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return store.TryGetValue(key, out var value) && (value.Expiry is null || value.Expiry > GetUtcNow()) ? (value.Bytes.ToArray(), value.Expiry) : null; }
        }
        private ValueTask Put(Dictionary<string, (byte[] Bytes, DateTimeOffset? Expiry)> store, string key, ReadOnlyMemory<byte> bytes, DateTimeOffset? expiry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (key.Length > 256 || bytes.Length > 1024 * 1024) throw new ArgumentException("Fixture storage limit.");
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (!store.ContainsKey(key) && store.Count >= 64) throw new InvalidOperationException("Fixture storage limit.");
                if (store.Remove(key, out var old)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(old.Bytes);
                store[key] = (bytes.ToArray(), expiry); }
            return ValueTask.CompletedTask;
        }
        private ValueTask Remove(Dictionary<string, (byte[] Bytes, DateTimeOffset? Expiry)> store, string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (store.Remove(key, out var old)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(old.Bytes); }
            return ValueTask.CompletedTask;
        }
        public ValueTask<PlatformCredential?> GetAsync(string key, CancellationToken token)
        {
            if (Read(_secrets, key, token) is not { } value) return ValueTask.FromResult<PlatformCredential?>(null);
            try { return ValueTask.FromResult<PlatformCredential?>(new PlatformCredential(value.Bytes, value.Expiry)); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(value.Bytes); }
        }
        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset? expiry, CancellationToken token) => Put(_secrets, key, value, expiry, token);
        public ValueTask DeleteAsync(string key, CancellationToken token) => Remove(_secrets, key, token);
        ValueTask<PlatformCacheEntry?> IPlatformCache.GetAsync(string key, CancellationToken token) => ValueTask.FromResult(Read(_cache, key, token) is { } value ? new PlatformCacheEntry(value.Bytes, value.Expiry) : null);
        ValueTask IPlatformCache.SetAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset? expiry, CancellationToken token) => Put(_cache, key, value, expiry, token);
        public ValueTask RemoveAsync(string key, CancellationToken token) => Remove(_cache, key, token);
        ValueTask<string?> IPlatformSettings.GetAsync(string key, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<string?>(null); }
        public void Log(PlatformLogLevel level, string name, string message, Exception? exception = null) { }
        public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken token) => await Task.Delay(delay, token).ConfigureAwait(false);
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true;
                foreach (var client in _clients) client.Dispose();
                foreach (var value in _secrets.Values) System.Security.Cryptography.CryptographicOperations.ZeroMemory(value.Bytes);
                _secrets.Clear(); _cache.Clear(); }
        }
    }
}
