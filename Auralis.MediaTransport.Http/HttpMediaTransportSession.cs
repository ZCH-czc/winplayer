using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;


namespace Auralis.MediaTransport;

/// <summary>
/// Consumes backend-only resource requests without player, platform or UI dependencies. Header-free requests stay as
/// remote URIs unless they opt into host transport; those and header-bearing leases use bounded,
/// short-lived session files, keeping system-proxy handling out of the decoder's network stack.
/// Neither path exposes the signed URL or headers to WebView JavaScript.
/// </summary>
public sealed partial class HttpMediaTransportSession : IMediaTransportSession
{

    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private Task? _disposeTask;

    public async Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request, string? cacheIdentity, CancellationToken cancellationToken, bool prefetch = false)
    {
        using var operation = Enter(cancellationToken);
        return await PrepareSharedAsync(request, cacheIdentity, operation.Token, prefetch).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = 1;
                if (_operations == 0) _drained.TrySetResult();
                // Cancellation callbacks run outside the lifecycle monitor.
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new(_disposeTask);
        }
    }
    private Operation Enter(CancellationToken token)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            token.ThrowIfCancellationRequested();
            _operations++;
        }
        try { return new(this, CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token)); }
        catch { Exit(); throw; }
    }
    private void Exit()
    {
        lock (_lifecycleGate)
        {
            if (--_operations == 0 && _disposed != 0) _drained.TrySetResult();
        }
    }
    private sealed class Operation(HttpMediaTransportSession owner, CancellationTokenSource cancellation) : IDisposable
    {
        private HttpMediaTransportSession? _owner = owner;
        public CancellationToken Token => cancellation.Token;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            cancellation.Dispose(); current.Exit();
        }
    }

    private const long MaximumBufferedBytes = 512L * 1024 * 1024;
    private const long MaximumSessionCacheBytes = 768L * 1024 * 1024;
    private const int MaximumSessionCacheFiles = 8;
    private const int DownloadBufferBytes = 256 * 1024;
    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Transfer-Encoding", "Upgrade", "Proxy-Authorization"
    };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly IMediaTransferBudget _budget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, SessionCacheEntry> _sessionCache = new(StringComparer.Ordinal);
    private int _disposed;

    public HttpMediaTransportSession(string cacheDirectory, HttpMessageHandler? handler = null, TimeProvider? clock = null,
        IMediaTransferBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _clock = clock ?? TimeProvider.System;
        _budget = budget ?? new MediaTransferBudget();
        _httpClient = new HttpClient(handler ?? CreateHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Auralis", "0.5"));
        CleanupStaleCacheFiles();
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(12),
        MaxConnectionsPerServer = 2,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        UseCookies = false
    };

    private async Task<IMediaTransportResource> PrepareCoreAsync(
        MediaTransportRequest lease,
        string? trackCacheKey,
        CancellationToken cancellationToken,
        bool bufferRemote = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (lease.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow().AddSeconds(2))
        {
            throw new MediaTransportException(MediaTransportFailure.Expired);
        }

        ValidateStreamUri(lease.Url);
        // Speculative work must not fill the cache with a multi-hour concert. Foreground
        // playback retains its existing limit and is never rejected by this smaller budget.
        var maximumBytes = bufferRemote ? 64L * 1024 * 1024 : MaximumBufferedBytes;

        if (lease.RequestHeaders.Count == 0 && !lease.UseHostTransport && !bufferRemote)
        {
            return new Resource(this, lease.Url, null);
        }

        var cacheKey = CreateCacheKey(lease, trackCacheKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (lease.ExpiresAt is { } cachedExpiry && cachedExpiry <= _clock.GetUtcNow().AddSeconds(2))
                throw new MediaTransportException(MediaTransportFailure.Expired);
            if (_sessionCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached.Path))
            {
                cached.LastAccessUtc = DateTimeOffset.UtcNow;
                return Pin(cached);
            }
        }
        finally
        {
            _gate.Release();
        }

        using var reservation = _budget.Reserve(bufferRemote);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_cacheDirectory);
        var temporaryPath = Path.Combine(
            _cacheDirectory,
            $"stream-{Guid.NewGuid():N}{ChooseExtension(lease)}");
        var partialPath = temporaryPath + ".part";
        var committed = false;

        try
        {
            using var response = await SendWithSafeRedirectsAsync(lease, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MediaTransportException(MediaTransportFailure.Unavailable);
            }

            // A server may cap an open-ended range. Never cache a fragment as a complete song.
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || range.Unit != "bytes" || range.From != 0 ||
                    range.Length is not > 0 || range.To != range.Length - 1 ||
                    (response.Content.Headers.ContentLength is { } length && length != range.Length))
                    throw new MediaTransportException(MediaTransportFailure.IncompleteRange);
            }

            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new MediaTransportException(MediaTransportFailure.TooLarge);
            }

            reservation.Ensure(Math.Max(0, response.Content.Headers.ContentLength ?? 0));

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var outputOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = DownloadBufferBytes,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                PreallocationSize = Math.Max(0, response.Content.Headers.ContentLength ?? 0)
            };
            await using (var output = new FileStream(partialPath, outputOptions))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferBytes);
                try
                {
                    var total = 0L;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0, DownloadBufferBytes), cancellationToken)
                            .ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        total += count;
                        if (total > maximumBytes)
                        {
                            throw new MediaTransportException(MediaTransportFailure.TooLarge);
                        }

                        reservation.Ensure(total);
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (total == 0 || (response.Content.Headers.ContentLength is { } expected && total != expected))
                        throw new MediaTransportException(MediaTransportFailure.Incomplete);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            File.Move(partialPath, temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (lease.ExpiresAt is { } completedExpiry && completedExpiry <= _clock.GetUtcNow().AddSeconds(2))
                throw new MediaTransportException(MediaTransportFailure.Expired);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_sessionCache.TryGetValue(cacheKey, out var existing) && File.Exists(existing.Path))
                {
                    TryDelete(temporaryPath);
                    existing.LastAccessUtc = DateTimeOffset.UtcNow;
                    return Pin(existing);
                }

                var size = new FileInfo(temporaryPath).Length;
                var entry = new SessionCacheEntry(temporaryPath, size, DateTimeOffset.UtcNow);
                _sessionCache[cacheKey] = entry;
                committed = true;
                var resource = Pin(entry);
                TrimSessionCache();
                return resource;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (MediaTransportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new MediaTransportException(MediaTransportFailure.TransportFailure);
        }
        finally
        {
            TryDelete(partialPath);
            if (!committed) TryDelete(temporaryPath);
        }
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        MediaTransportRequest lease,
        CancellationToken cancellationToken)
    {
        var current = lease.Url;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (HasSameOrigin(lease.Url, current))
            {
                AddLeaseHeaders(request, lease.RequestHeaders);
            }
            // Google media endpoints can delay/throttle non-range bodies even after HTTP 200.
            // Request the whole representation, not a truncated startup sample.
            if (lease.UseHostTransport) request.Headers.Range = new RangeHeaderValue(0, null);

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (redirect == 5 || location is null)
            {
                throw new MediaTransportException(MediaTransportFailure.InvalidRedirect);
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            ValidateStreamUri(current);
        }

        throw new MediaTransportException(MediaTransportFailure.InvalidRedirect);
    }

    private static void AddLeaseHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key) ||
                ForbiddenRequestHeaders.Contains(header.Key) ||
                header.Key.Length > 128 || header.Value.Length > 8192 ||
                header.Key.Any(char.IsControl) ||
                header.Value.Any(static character => character is '\r' or '\n' or '\0') ||
                !request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new MediaTransportException(MediaTransportFailure.InvalidHeaders);
            }
        }
    }

    private static void ValidateStreamUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsoluteUri.Length > 8192 ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
        {
            throw new MediaTransportException(MediaTransportFailure.InvalidUri);
        }
    }

    private static bool HasSameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    // Called under the cache gate. Every acquisition, including a cache hit, gets its own pin.
    private Resource Pin(SessionCacheEntry entry)
    {
        entry.Pins++;
        return new Resource(this, new Uri(Path.GetFullPath(entry.Path)), entry);
    }

    private async Task UnpinAsync(SessionCacheEntry? entry)
    {
        if (entry is null) return;
        Operation operation;
        try { operation = Enter(default); }
        catch (ObjectDisposedException) { return; } // Session shutdown owns all remaining files.
        using (operation)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { entry.Pins--; TrimSessionCache(); }
            finally { _gate.Release(); }
        }
    }

    private sealed class Resource(HttpMediaTransportSession owner, Uri source, SessionCacheEntry? entry) : IMediaTransportResource
    {
        internal SessionCacheEntry? CacheEntry => entry;
        private readonly object _releaseGate = new();
        private Task? _release;
        public Uri Source { get; } = source;
        public ValueTask DisposeAsync()
        {
            lock (_releaseGate) return new(_release ??= owner.UnpinAsync(entry));
        }
        public override string ToString() => nameof(IMediaTransportResource);
    }

    private async Task DisposeCoreAsync()
    {
        try { _shutdown.Cancel(); } catch (AggregateException) { /* Drain still owns operation cleanup. */ }
        await _drained.Task.ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cached in _sessionCache.Values)
            {
                paths.Add(cached.Path);
            }

            _sessionCache.Clear();
            foreach (var path in paths)
            {
                TryDelete(path);
            }
            _httpClient.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private static string ChooseExtension(MediaTransportRequest lease)
    {
        var pathExtension = Path.GetExtension(lease.Url.AbsolutePath).ToLowerInvariant();
        if (pathExtension is ".mp3" or ".m4a" or ".aac" or ".flac" or ".wav" or ".wma" or ".ogg" or ".opus" or ".webm")
        {
            return pathExtension;
        }

        return lease.MimeType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/aac" => ".aac",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/webm" => ".webm",
            _ => ".media"
        };
    }

    private static string CreateCacheKey(MediaTransportRequest lease, string? trackCacheKey)
    {
        var builder = new StringBuilder(512)
            .Append(string.IsNullOrWhiteSpace(trackCacheKey) ? lease.Url.AbsoluteUri : trackCacheKey.Trim())
            .Append('\n')
            .Append(lease.VariantKey)
            .Append('\n')
            .Append(lease.MimeType ?? string.Empty);

        // Header values may contain short-lived credentials. They influence the in-memory key so two
        // different authorizations cannot accidentally share data, but the raw values never become a
        // filename, log entry, WebView payload, or persistent index.
        foreach (var header in lease.RequestHeaders.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('\n').Append(header.Key).Append(':').Append(header.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private void CleanupStaleCacheFiles()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(12));
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "stream-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another Auralis process may still own the file; it will be reconsidered later.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache maintenance must never prevent the player from starting.
        }
    }

    private void TrimSessionCache()
    {
        while (_sessionCache.Count > MaximumSessionCacheFiles ||
               _sessionCache.Values.Sum(static entry => entry.SizeBytes) > MaximumSessionCacheBytes)
        {
            var victim = _sessionCache
                .Where(item => item.Value.Pins == 0)
                .OrderBy(static item => item.Value.LastAccessUtc)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(victim.Key))
            {
                break;
            }

            _sessionCache.Remove(victim.Key);
            TryDelete(victim.Value.Path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The OS will release a stale temporary stream file after the media pipeline closes it.
        }
    }

    private sealed class SessionCacheEntry(string path, long sizeBytes, DateTimeOffset lastAccessUtc)
    {
        internal int Pins { get; set; }
        internal string Path { get; } = path;

        internal long SizeBytes { get; } = sizeBytes;

        internal DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
