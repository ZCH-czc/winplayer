namespace Auralis.Artwork;

public sealed class HttpArtworkSourceFactory : IArtworkSourceFactory
{
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private int _disposed;
    public static ArtworkDescriptor Metadata { get; } = new("auralis.artwork.http", "HTTP artwork",
        new(0, 2, 1), 1, new(0, 1, 0), ArtworkCapabilities.Full);
    public ArtworkDescriptor Descriptor => Metadata;
    public HttpArtworkSourceFactory() { }
    public HttpArtworkSourceFactory(Func<HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    public IArtworkSource Create()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _handlerFactory is null ? new HttpArtworkSource() : new HttpArtworkSource(_handlerFactory());
    }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
}
