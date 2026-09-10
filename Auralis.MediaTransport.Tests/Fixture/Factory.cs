using Auralis.MediaTransport;

namespace SyntheticTransport;

public class Factory : IMediaTransportFactory
{
    public Factory() => Record("activate;");
    public virtual MediaTransportDescriptor Descriptor => new("fixture.transport", "Synthetic", new(1, 0, 0), 1, new(0, 1, 0), MediaTransportCapabilities.Full);
    public IMediaTransportSession Create(MediaTransportContext context) { Record("create;"); return new Session(); }
    public async ValueTask DisposeAsync()
    {
        Record("factoryDispose;");
        if (AppContext.GetData("Auralis.Transport.Test.CloseEntered") is TaskCompletionSource entered) entered.TrySetResult();
        if (AppContext.GetData("Auralis.Transport.Test.CloseGate") is Task gate) await gate.ConfigureAwait(false);
        Record("factoryClosed;");
        if (AppContext.GetData("Auralis.Transport.Test.CloseThrows") is true) throw new Exception("private fixture error");
    }
    internal static void Record(string value) => AppContext.SetData("Auralis.Transport.Test.Events", (string?)AppContext.GetData("Auralis.Transport.Test.Events") + value);
}
public sealed class WrongDescriptor : Factory
{ public override MediaTransportDescriptor Descriptor => base.Descriptor with { Id = "wrong.transport" }; }
public sealed class BrokenConstructor : Factory
{ public BrokenConstructor() => throw new Exception("private fixture error"); }
public sealed class NotAFactory { }
internal sealed class PrivateFactory : Factory { }
internal sealed class Session : IMediaTransportSession
{
    private bool _closed;
    public Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request, string? identity, CancellationToken token, bool prefetch = false)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_closed, this);
        Factory.Record("prepare;");
        return Task.FromResult<IMediaTransportResource>(new Resource());
    }
    public ValueTask DisposeAsync() { _closed = true; Factory.Record("sessionDispose;"); return ValueTask.CompletedTask; }
}
internal sealed class Resource : IMediaTransportResource
{
    public Uri Source => new(typeof(Factory).Assembly.Location);
    public ValueTask DisposeAsync() { Factory.Record("resourceDispose;"); return ValueTask.CompletedTask; }
}
