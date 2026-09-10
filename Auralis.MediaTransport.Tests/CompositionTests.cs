using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

internal static class CompositionTests
{
    public static async Task<int> RunAsync(string root)
    {
        var checks = 0; int defaults = 0, optional = 0, unused = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        var descriptor = HttpMediaTransportFactory.Metadata;
        var bundled = new MediaTransportRegistration(descriptor, true, () => { defaults++; return new Factory(descriptor); });
        var selected = new MediaTransportRegistration(descriptor, true, () => { optional++; return new Factory(descriptor); });
        var extra = new MediaTransportRegistration(descriptor with { Id = "fixture.unselected" }, true, () => { unused++; throw new Exception(); });
        MediaTransportComponentComposition Create(MediaTransportStartupPlan plan) => new(plan, bundled, MediaTransportCapabilities.Full);
        var context = new MediaTransportContext(Path.Combine(root, "composition"), new MediaTransferBudget());
        var request = new MediaTransportRequest(new Uri("https://fixture.invalid/no-network"), null, null, "synthetic");
        var core = Create(new(MediaTransportInstallationIssue.None, [selected, extra], null, []));
        Check(core.UsingBundled && !core.Activated && defaults == 0 && optional == 0, "composition metadata is inert");
        await using (var idle = core.CreateDeferred(context)) { }
        Check(defaults == 0 && !core.Activated, "unused session does not activate");
        await using (var session = core.CreateDeferred(context))
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await session.PrepareAsync(request, null, cancel.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { checks++; }
            Check(defaults == 0, "pre-cancelled first prepare remains inert");
            await using var pin = await session.PrepareAsync(request, null, default);
            Check(core.Activated && defaults == 1 && optional == 0 && unused == 0 && !core.FellBack, "no preference chooses bundled even with same ID optional");
        }
        var plan = new MediaTransportStartupPlan(MediaTransportInstallationIssue.None, [selected, extra], descriptor.Id, []);
        var configured = Create(plan);
        Check(!configured.UsingBundled && !configured.Activated, "selected startup descriptor is not a claim of activation");
        var sessions = Enumerable.Range(0, 3).Select(_ => configured.CreateDeferred(context)).ToArray();
        var pins = await Task.WhenAll(sessions.Select(s => s.PrepareAsync(request, null, default)));
        Check(optional == 3 && unused == 0 && defaults == 1, "only selected same-ID optional creates independent sessions");
        foreach (var pin in pins) await pin.DisposeAsync();
        foreach (var session in sessions) await session.DisposeAsync();
        var calls = 0;
        var broken = selected with { Activate = () => { calls++; throw new Exception("private details"); } };
        var recovery = Create(plan with { Registrations = [broken] });
        sessions = Enumerable.Range(0, 3).Select(_ => recovery.CreateDeferred(context)).ToArray();
        pins = await Task.WhenAll(sessions.Select(s => s.PrepareAsync(request, null, default)));
        Check(calls == 1 && defaults == 4 && recovery.FellBack && recovery.UsingBundled, "first managed failure latches before concurrent creation retries");
        foreach (var pin in pins) await pin.DisposeAsync();
        foreach (var session in sessions) await session.DisposeAsync();
        Check(recovery.CurrentDescriptor == descriptor, "fallback presentation reports bundled descriptor");
        var invalid = Create(plan with { Issue = MediaTransportInstallationIssue.InvalidState });
        await using (var session = invalid.CreateDeferred(context)) { await using var pin = await session.PrepareAsync(request, null, default); }
        Check(invalid.UsingBundled && optional == 3, "invalid optional state cannot break bundled startup");
        var disabled = Create(plan with { Registrations = [selected with { Enabled = false }] });
        await using (var session = disabled.CreateDeferred(context)) { await using var pin = await session.PrepareAsync(request, null, default); }
        Check(disabled.FellBack && optional == 3, "disabled component never activates");
        var requestFail = selected with { Activate = () => new Factory(descriptor, true) };
        var noReplay = Create(plan with { Registrations = [requestFail] });
        var before = defaults;
        await using (var session = noReplay.CreateDeferred(context))
        {
            try { await session.PrepareAsync(request, null, default); throw new Exception("request unexpectedly succeeds"); }
            catch (MediaTransportException e) { Check(e.Failure == MediaTransportFailure.TransportFailure, "request failure remains typed"); }
        }
        Check(defaults == before && !noReplay.FellBack, "request authorization is never replayed via default");
        Check(!Directory.Exists(context.CacheDirectory), "synthetic composition never writes or downloads");
        Console.WriteLine($"PASS transport composition: {checks} assertions; inert startup, same-ID default, independent sessions, latched creation fallback and no request replay.");
        return checks;
    }
    private sealed class Factory(MediaTransportDescriptor descriptor, bool fail = false) : IMediaTransportFactory
    {
        public MediaTransportDescriptor Descriptor => descriptor;
        public IMediaTransportSession Create(MediaTransportContext context) => new Session(fail);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Session(bool fail) : IMediaTransportSession
    {
        public Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request, string? identity, CancellationToken token, bool prefetch = false)
        {
            token.ThrowIfCancellationRequested();
            if (fail) throw new MediaTransportException(MediaTransportFailure.TransportFailure);
            return Task.FromResult<IMediaTransportResource>(new Resource(request.Url));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resource(Uri source) : IMediaTransportResource
    { public Uri Source => source; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
