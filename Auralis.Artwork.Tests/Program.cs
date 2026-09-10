using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Auralis.Artwork;

internal static class Program
{
    private static int _checks;
    private static readonly Uri Image = new("https://images.example.invalid/cover?secret=fixture-only");
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); _checks++; }
    private static async Task Failure(Func<Task> work, ArtworkFailure expected)
    {
        try { await work(); throw new InvalidOperationException("Expected fixed artwork failure"); }
        catch (ArtworkException e)
        {
            Check(e.Failure == expected, $"failure expected {expected}, actual {e.Failure}");
            Check(e.InnerException is null && !e.ToString().Contains("fixture-only"), "safe failure has no URL or inner exception");
        }
    }
    private static HttpResponseMessage Response(byte[] bytes, string? type = "image/png", bool knownLength = true)
    {
        var content = new StreamContent(new NonSeekableStream(new MemoryStream(bytes)));
        if (knownLength) content.Headers.ContentLength = bytes.Length;
        if (type is not null) content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new(HttpStatusCode.OK) { Content = content };
    }
    private static async Task<int> Main()
    {
        try
        {
            await ContractAndSuccessAsync();
            await InvalidRequestsAsync();
            await RedirectsAsync();
            await ResponsesAsync();
            await CancellationAsync();
            await RealLoopbackAsync();
            await CompositionChecks.RunAsync();
            Console.WriteLine($"PASS artwork component: {_checks} checks; independent contracts/HTTP, all existing MIME types, bounded complete reads, per-hop policy, independent streams, cancellation/disposal, sanitized failures and real loopback redirect/gzip/no-cookie behavior. No accounts, public network, UI or user files.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL artwork component after {_checks} checks: {e}"); return 1; }
    }

    private static async Task ContractAndSuccessAsync()
    {
        Check(typeof(IArtworkSource).Assembly != typeof(HttpArtworkSource).Assembly, "contracts independently compiled");
        Check(!typeof(IArtworkSource).Assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Auralis.")), "contracts no reverse dependency");
        Check(typeof(HttpArtworkSource).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Auralis.")).All(a => a.Name == "Auralis.Artwork.Abstractions"), "HTTP depends only on artwork ABI");
        var request = new ArtworkRequest(Image, int.MaxValue, _ => true);
        Check(request.MaximumBytes == 16 * 1024 * 1024 && new ArtworkRequest(Image, -1).MaximumBytes == 1, "legacy bounded clamp");
        Check(!JsonSerializer.Serialize(request).Contains("fixture-only") && !request.ToString().Contains("example.invalid"), "request JSON/log privacy");
        var bytes = new byte[] { 1, 2, 3 };
        var payload = new ArtworkPayload(bytes, ArtworkMediaType.Gif); bytes[0] = 99;
        using var one = (MemoryStream)payload.OpenRead(); using var two = payload.OpenRead();
        Check(one.ReadByte() == 1 && two.ReadByte() == 1, "owned copy and independent stream positions");
        Check(!one.CanWrite && !one.TryGetBuffer(out _), "stream cannot expose mutable payload buffer");
        one.Dispose(); Check(two.ReadByte() == 2, "disposing one stream preserves another");
        await Failure(() => Task.FromResult(new ArtworkPayload([], ArtworkMediaType.Png)), ArtworkFailure.EmptyPayload);
        await Failure(() => Task.FromResult(new ArtworkPayload(new byte[16 * 1024 * 1024 + 1], ArtworkMediaType.Png)), ArtworkFailure.TooLarge);
        await Failure(() => Task.FromResult(new ArtworkPayload([1], (ArtworkMediaType)99)), ArtworkFailure.UnsupportedMediaType);
        await Failure(() => Task.FromResult(new ArtworkRequest(null!)), ArtworkFailure.InvalidRequest);
        foreach (var (mime, normalized) in new[] { ("image/jpeg", "image/jpeg"), ("image/jpg", "image/jpeg"), ("image/png", "image/png"), ("image/webp", "image/webp"), ("image/gif", "image/gif"), ("image/bmp", "image/bmp"), ("IMAGE/PNG", "image/png") })
        {
            var handler = new Handler((r, _) =>
            {
                Check(r.Method == HttpMethod.Get && r.Headers.Accept.ToString() == "image/*", "legacy image request");
                Check(r.Headers.Authorization is null && !r.Headers.Contains("Cookie") && r.Headers.Referrer is null, "no sensitive headers");
                return Task.FromResult(Response([3, 4], mime));
            });
            var source = new HttpArtworkSource(handler);
            Check(handler.Requests == 0, "construction no network");
            var image = await source.FetchAsync(new(Image));
            source.Dispose(); source.Dispose();
            Check(image.ContentType == normalized && image.Length == 2, "MIME normalization and length");
            using var read = image.OpenRead(); Check(read.ReadByte() == 3, "payload survives source disposal");
            Check(handler.Disposals == 1, "owned handler disposed once");
        }
    }

    private static async Task InvalidRequestsAsync()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response([1])));
        using var source = new HttpArtworkSource(handler);
        foreach (var uri in new[] { new Uri("relative", UriKind.Relative), new Uri("file:///test.png"), new Uri("http://public.example.invalid/image"), new Uri("https://user:fixture-only@example.invalid/x"), new Uri("https://example.invalid/" + new string('a', 8200)) })
            await Failure(() => source.FetchAsync(new(uri)), ArtworkFailure.InvalidRequest);
        await Failure(() => source.FetchAsync(null!), ArtworkFailure.InvalidRequest);
        await Failure(() => source.FetchAsync(new(Image, destinationAllowed: _ => false)), ArtworkFailure.DestinationDenied);
        await Failure(() => source.FetchAsync(new(Image, destinationAllowed: _ => throw new Exception("fixture-only"))), ArtworkFailure.DestinationDenied);
        Check(handler.Requests == 0, "invalid or denied destinations never requested");
    }

    private static async Task RedirectsAsync()
    {
        static HttpResponseMessage Redirect(Uri? target) => new(HttpStatusCode.Found) { Headers = { Location = target }, Content = new ByteArrayContent([]) };
        var destinations = new List<string>();
        using (var source = new HttpArtworkSource(new Handler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath == "/next" ? Response([1]) : Redirect(new Uri("/next", UriKind.Relative))))))
        {
            await source.FetchAsync(new(Image, destinationAllowed: u => { destinations.Add(u.AbsolutePath); return true; }));
            Check(destinations.SequenceEqual(new[] { "/cover", "/next", "/next" }), "relative redirects reauthorize every destination and final delivery");
        }
        foreach (var target in new[] { new Uri("https://other.example.invalid/image"), new Uri("http://other.example.invalid/image"), new Uri("https://user:fixture-only@other.example.invalid/image") })
        {
            var handler = new Handler((_, _) => Task.FromResult(Redirect(target)));
            using var source = new HttpArtworkSource(handler);
            await Failure(() => source.FetchAsync(new(Image, destinationAllowed: u => u.Host == Image.Host)), target.Scheme == "https" && target.UserInfo == "" ? ArtworkFailure.DestinationDenied : ArtworkFailure.InvalidRequest);
            Check(handler.Requests == 1, "rejected redirect is not fetched");
        }
        var loop = new Handler((_, _) => Task.FromResult(Redirect(Image)));
        using (var source = new HttpArtworkSource(loop)) await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.HttpFailure);
        Check(loop.Requests == 5, "four redirects maximum");
        using (var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(Redirect(null)))))
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.HttpFailure);
    }

    private static async Task ResponsesAsync()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.PartialContent })
        {
            using var source = new HttpArtworkSource(new Handler((_, _) => { var r = Response([1]); r.StatusCode = status; return Task.FromResult(r); }));
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.HttpFailure);
        }
        foreach (var type in new string?[] { null, "image/svg+xml", "text/html", "application/octet-stream" })
        {
            using var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(Response([1], type))));
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.UnsupportedMediaType);
        }
        foreach (var knownLength in new[] { false, true })
        {
            using var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(Response([1, 2, 3], knownLength: knownLength))));
            await Failure(() => source.FetchAsync(new(Image, 2)), ArtworkFailure.TooLarge);
            var exact = await source.FetchAsync(new(Image, 3)); Check(exact.Length == 3, "exact cap accepted known/unknown length");
        }
        using (var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(Response([])))))
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.EmptyPayload);
        foreach (var declared in new long[] { 1, 3 })
        {
            using var source = new HttpArtworkSource(new Handler((_, _) => { var response = Response([1, 2]); response.Content.Headers.ContentLength = declared; return Task.FromResult(response); }));
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.IncompletePayload);
        }
        using (var source = new HttpArtworkSource(new Handler((_, _) => throw new HttpRequestException("https://fixture-only/secret"))))
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.NetworkFailure);
        var body = new ThrowingStream();
        using (var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("image/png") } } }))))
            await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.NetworkFailure);
        Check(body.Closed, "failed body released");
    }

    private static async Task CancellationAsync()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response([1])));
        using (var source = new HttpArtworkSource(handler))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await source.FetchAsync(new(Image), cancelled.Token); throw new Exception("not cancelled"); }
            catch (OperationCanceledException e) { Check(e.CancellationToken == cancelled.Token && handler.Requests == 0, "pre-cancel keeps caller token, no HTTP"); }
        }
        foreach (var dispose in new[] { false, true })
        {
            var body = new BlockingStream();
            var requestNumber = 0;
            var source = new HttpArtworkSource(new Handler((_, _) => Task.FromResult(++requestNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("image/png") } } }
                : Response([1]))));
            using var cancel = new CancellationTokenSource();
            var pending = source.FetchAsync(new(Image), cancel.Token);
            await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (dispose) source.Dispose(); else cancel.Cancel();
            if (dispose) await Failure(() => pending.WaitAsync(TimeSpan.FromSeconds(3)), ArtworkFailure.Disposed);
            else
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("not cancelled"); }
                catch (OperationCanceledException e) { Check(e.CancellationToken == cancel.Token && e.InnerException is null, "mid-body cancellation safe and identifiable"); }
                var retry = await source.FetchAsync(new(Image)); Check(retry.Length == 1, "caller cancellation does not dispose source");
            }
            Check(body.Closed, "cancelled body disposed");
            source.Dispose(); await Failure(() => source.FetchAsync(new(Image)), ArtworkFailure.Disposed);
        }
        var sharedHandler = new Handler(async (_, token) => { await Task.Delay(5, token); return Response([8]); });
        using var shared = new HttpArtworkSource(sharedHandler);
        var images = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => shared.FetchAsync(new(Image))));
        Check(sharedHandler.Requests == 12 && images.Distinct().Count() == 12, "parallel requests have independent results, no cross-request cache");
    }

    private static async Task RealLoopbackAsync()
    {
        // Real production handler, synthetic loopback only. Proves decompressed length, no cookie replay,
        // explicit redirects and headers; the injectable handler tests above do not establish these facts.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6ioAAAAASUVORK5CYII=");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true)) gzip.Write(png);
        var requests = new List<string>();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var socket = client.GetStream(); var text = new StringBuilder(); var single = new byte[1];
                while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (text.Length >= 16384 || await socket.ReadAsync(single, timeout.Token) == 0) throw new IOException("synthetic request incomplete");
                    text.Append((char)single[0]);
                }
                requests.Add(text.ToString());
                var header = i == 0 ? "HTTP/1.1 302 Found\r\nLocation: /image\r\nSet-Cookie: fixture=never-replay\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" :
                    $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Encoding: gzip\r\nContent-Length: {compressed.Length}\r\nConnection: close\r\n\r\n";
                await socket.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
                if (i == 1) await socket.WriteAsync(compressed.ToArray(), timeout.Token);
            }
        }, timeout.Token);
        await using var source = new Auralis.Artwork.Host.ArtworkComponentComposition([], new(HttpArtworkSourceFactory.Metadata,
            true, static () => new HttpArtworkSourceFactory())).CreateDeferred();
        var payload = await source.FetchAsync(new(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/start")), timeout.Token);
        await server;
        using var image = payload.OpenRead(); using var output = new MemoryStream(); image.CopyTo(output);
        Check(output.ToArray().SequenceEqual(png), "real gzip response returns unchanged decoded image bytes");
        Check(requests.Count == 2 && requests[0].StartsWith("GET /start ") && requests[1].StartsWith("GET /image "), "real relative redirect");
        Check(requests.All(r => !r.Contains("\r\nCookie:", StringComparison.OrdinalIgnoreCase) && !r.Contains("\r\nAuthorization:", StringComparison.OrdinalIgnoreCase)), "real handler does not replay cookies or credentials");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests; internal int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ThrowingStream() : NonSeekableStream(Stream.Null)
    {
        internal bool Closed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw new IOException("fixture-only");
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class BlockingStream() : NonSeekableStream(Stream.Null)
    {
        internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
