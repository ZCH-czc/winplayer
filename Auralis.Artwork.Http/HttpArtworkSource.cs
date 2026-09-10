using System.Net;
using System.Net.Http.Headers;

namespace Auralis.Artwork;

/// <summary>Bounded public-image HTTP component. No platform IDs, account stores, UI or disk caches.
/// Production redirects and cookies are disabled on the handler; each hop is authorized explicitly.</summary>
public sealed class HttpArtworkSource : IArtworkSource
{
    private const int MaximumRedirects = 4;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public HttpArtworkSource() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.Deflate | DecompressionMethods.GZip,
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 4,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1), UseCookies = false
    }) { }

    /// <summary>Explicit transport injection for owned offline fixtures. A supplied handler must not
    /// auto-follow redirects, attach cookies/credentials or change request destinations.</summary>
    public HttpArtworkSource(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Auralis", "0.5"));
    }

    public async Task<ArtworkPayload> FetchAsync(ArtworkRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            if (_disposed) throw new ArtworkException(ArtworkFailure.Disposed);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
        using (lifetime)
        {
            try
            {
                if (request is null) throw new ArtworkException(ArtworkFailure.InvalidRequest);
                return await FetchCoreAsync(request, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (OperationCanceledException)
            { throw new ArtworkException(IsDisposed() ? ArtworkFailure.Disposed : ArtworkFailure.NetworkFailure); }
            catch (ArtworkException) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or ObjectDisposedException)
            { throw new ArtworkException(IsDisposed() ? ArtworkFailure.Disposed : ArtworkFailure.NetworkFailure); }
        }
    }

    private async Task<ArtworkPayload> FetchCoreAsync(ArtworkRequest request, CancellationToken token)
    {
        var current = request.Source;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            token.ThrowIfCancellationRequested();
            ValidateUri(current);
            Authorize(request, current);
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            {
                if (redirect == MaximumRedirects || response.Headers.Location is not { } location)
                    throw new ArtworkException(ArtworkFailure.HttpFailure);
                try { current = location.IsAbsoluteUri ? location : new Uri(current, location); }
                catch (UriFormatException) { throw new ArtworkException(ArtworkFailure.InvalidRequest); }
                continue;
            }
            // No Range was requested. Never publish a partial image as a complete cover.
            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
                throw new ArtworkException(ArtworkFailure.HttpFailure);
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > request.MaximumBytes) throw new ArtworkException(ArtworkFailure.TooLarge);
            var mediaType = ParseMediaType(response.Content.Headers.ContentType?.MediaType);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > request.MaximumBytes) throw new ArtworkException(ArtworkFailure.TooLarge);
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            if (output.Length == 0) throw new ArtworkException(ArtworkFailure.EmptyPayload);
            if (declaredLength.HasValue && declaredLength != output.Length) throw new ArtworkException(ArtworkFailure.IncompletePayload);
            // A provider may have been disabled while the body was downloading. Do not hand out a late image.
            Authorize(request, current);
            return new ArtworkPayload(output.GetBuffer().AsSpan(0, checked((int)output.Length)), mediaType);
        }
        throw new ArtworkException(ArtworkFailure.HttpFailure);
    }

    private static void Authorize(ArtworkRequest request, Uri destination)
    {
        if (request.DestinationAllowed is not { } allowed) return;
        bool accepted;
        try { accepted = allowed(destination); }
        catch { throw new ArtworkException(ArtworkFailure.DestinationDenied); }
        if (!accepted) throw new ArtworkException(ArtworkFailure.DestinationDenied);
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 8192 ||
            (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
            throw new ArtworkException(ArtworkFailure.InvalidRequest);
    }
    private static ArtworkMediaType ParseMediaType(string? type) => type?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ArtworkMediaType.Jpeg, "image/png" => ArtworkMediaType.Png,
        "image/webp" => ArtworkMediaType.WebP, "image/gif" => ArtworkMediaType.Gif, "image/bmp" => ArtworkMediaType.Bmp,
        _ => throw new ArtworkException(ArtworkFailure.UnsupportedMediaType)
    };
    private bool IsDisposed() { lock (_gate) return _disposed; }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        try { _shutdown.Cancel(); }
        finally { try { _http.Dispose(); } finally { _shutdown.Dispose(); } }
    }
}
