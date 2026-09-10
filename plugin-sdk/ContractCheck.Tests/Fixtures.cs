using Auralis.Platform.Abstractions;

namespace Auralis.PluginContractCheck.Tests;

public class GoodPlugin : IAuralisPlatformPlugin
{
    public virtual PlatformPluginDescriptor Descriptor => new("tests.contract", "Contract fixture", new(1, 0, 0), 1, 1);
    public virtual ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new Provider()]));
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class WrongDescriptorPlugin : GoodPlugin
{
    public override PlatformPluginDescriptor Descriptor => new("tests.other", "Wrong", new(1, 0, 0), 1, 1);
}
public sealed class ThrowPlugin : GoodPlugin
{
    public ThrowPlugin() { Console.WriteLine("DO_NOT_REPORT_COOKIE_SECRET"); throw new InvalidOperationException("DO_NOT_REPORT_COOKIE_SECRET"); }
}
public sealed class HangPlugin : GoodPlugin
{
    public HangPlugin() { Thread.Sleep(Timeout.Infinite); }
}
public sealed class HangDisposePlugin : GoodPlugin
{
    public override ValueTask DisposeAsync() { Thread.Sleep(Timeout.Infinite); return ValueTask.CompletedTask; }
}
public sealed class MissingInterfacePlugin : GoodPlugin
{
    public override ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new MissingInterfaceProvider()]));
}
public sealed class NetworkPlugin : GoodPlugin
{
    public override async ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context, CancellationToken token)
    {
        using var client = context.HttpClientFactory.CreateClient("test");
        try { using var response = await client.GetAsync("https://example.invalid", token); } catch (HttpRequestException) { }
        return await base.CreateProvidersAsync(context, token);
    }
}
public class MissingInterfaceProvider : IPlatformProvider
{
    public PlatformProviderDescriptor Descriptor => new("fixture", "Fixture", new(1, 0, 0), [PlatformCapabilityKind.LyricsLookup]);
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class Provider : MissingInterfaceProvider, IPlatformLyricsLookupCapability
{
    public Task<PlatformResult<PlatformLyricsLookupResult>> LookupAsync(PlatformLyricsLookupRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Title == "missing") return Task.FromResult(PlatformResult<PlatformLyricsLookupResult>.Failure(PlatformErrorCode.NotFound, "Fixture only"));
        return Task.FromResult(PlatformResult<PlatformLyricsLookupResult>.Success(new("fixture", "[00:01]Synthetic")));
    }
}
