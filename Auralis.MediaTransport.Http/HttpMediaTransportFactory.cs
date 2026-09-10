namespace Auralis.MediaTransport;

/// <summary>Stateless bundled factory. Constructing/reading metadata performs no disk or network I/O.
/// A handler supplier is only invoked at explicit Create; the resulting session owns that handler.</summary>
public sealed class HttpMediaTransportFactory(Func<HttpMessageHandler>? handlerFactory = null) : IMediaTransportFactory
{
    // Reflection-based packages require a real zero-argument constructor; an optional parameter
    // alone only provides source-level new Factory() syntax, not that CLR constructor signature.
    public HttpMediaTransportFactory() : this(null) { }
    private int _disposed;
    public static MediaTransportDescriptor Metadata { get; } = new("auralis.transport.http", "HTTP media transport",
        new Version(0,6,0), 1, new Version(0,1,0), MediaTransportCapabilities.Full);
    public MediaTransportDescriptor Descriptor => Metadata;
    public IMediaTransportSession Create(MediaTransportContext context)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        ArgumentNullException.ThrowIfNull(context);
        var handler=handlerFactory?.Invoke();
        try { return new HttpMediaTransportSession(context.CacheDirectory,handler,budget:context.Budget); }
        catch { handler?.Dispose(); throw; }
    }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed,1);return ValueTask.CompletedTask; }
}
