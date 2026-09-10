using Auralis.Services;

// Test-only decorator observes the raw session for decoder track assertions.
// Every verification/creation call still delegates to the production factory.
internal sealed class CapturingPlaybackFactory : IPlaybackComponentFactory
{
    private readonly LibVlcPlaybackFactory _factory = new();
    public PlaybackComponentDescriptor Descriptor => _factory.Descriptor;
    internal IPlaybackSession? Session { get; private set; }
    public void VerifyRuntime() => _factory.VerifyRuntime();
    public IPlaybackSession Create(SynchronizationContext? context) => Session = _factory.Create(context);
    public void Dispose() => _factory.Dispose();
}
