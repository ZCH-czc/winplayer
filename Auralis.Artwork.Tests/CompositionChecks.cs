using Auralis.Artwork;
using Auralis.Artwork.Host;

internal static class CompositionChecks
{
    private static int _checks;
    private static readonly ArtworkDescriptor Metadata = new("fixture.images", "Fixture images", new(1, 0, 0), 1, new(0, 1, 0), ArtworkCapabilities.Full);
    private static readonly ArtworkRequest Request = new(new Uri("https://example.invalid/image?fixture-secret"));
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(4);
    private static void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); _checks++; }
    private static ArtworkPayload Payload(byte value = 1) => new([value], ArtworkMediaType.Png);
    private static ArtworkRegistration Register(Func<IArtworkSourceFactory> create, ArtworkDescriptor? descriptor = null, bool enabled = true) => new(descriptor ?? Metadata, enabled, create);
    private static ArtworkRegistration Default(Action? activated = null) => Register(() => { activated?.Invoke(); return new Factory(Metadata, () => new Source()); });
    private static async Task<byte> Fetch(IArtworkSource source)
    { using var stream = (await source.FetchAsync(Request).WaitAsync(Deadline)).OpenRead(); return checked((byte)stream.ReadByte()); }
    private static async Task Fail(Task task, ArtworkFailure expected)
    {
        try { await task.WaitAsync(Deadline); throw new InvalidOperationException("expected failure"); }
        catch (ArtworkException e) { Check(e.Failure == expected && e.InnerException is null && !e.ToString().Contains("fixture-secret"), $"fixed failure {expected}"); }
    }
    internal static async Task RunAsync()
    {
        var activations = 0;
        var composition = new ArtworkComponentComposition([Register(() => throw new Exception("must stay dormant"))], Default(() => activations++));
        Check(composition.Inspect().Count == 2 && !composition.Activated && composition.UsingBundled, "metadata no activation");
        var unused = composition.CreateDeferred(); await unused.DisposeAsync();
        Check(activations == 0, "disposing unused wrapper does not create anything");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); await using var source = composition.CreateDeferred();
            try { await source.FetchAsync(Request, cancelled.Token); throw new Exception("not cancelled"); }
            catch (OperationCanceledException e) { Check(e.CancellationToken == cancelled.Token && activations == 0, "pre-cancel no activation"); }
        }
        await using (var source = composition.CreateDeferred()) Check(await Fetch(source) == 1 && activations == 1, "null selection chooses bundled even same optional ID");
        foreach (var (descriptor, enabled, issue) in new[]
        {
            (Metadata, false, ArtworkComponentIssue.Disabled),
            (Metadata with { ApiVersion = 2 }, true, ArtworkComponentIssue.ApiMismatch),
            (Metadata with { MinimumHostVersion = new(99, 0, 0) }, true, ArtworkComponentIssue.HostTooOld),
            (Metadata with { Capabilities = ArtworkCapabilities.PublicHttp }, true, ArtworkComponentIssue.MissingCapability)
        })
        {
            var calls = 0;
            var plan = new ArtworkComponentComposition([Register(() => { calls++; throw new Exception(); }, descriptor, enabled)], Default(), Metadata.Id);
            Check(plan.Inspect()[0].Issue == issue, "inert compatibility rejection");
            await using var source = plan.CreateDeferred(); Check(await Fetch(source) == 1 && calls == 0 && plan.FellBack, "incompatible candidate never constructed");
        }
        await using (var missing = new ArtworkComponentComposition([], Default(), "missing").CreateDeferred()) Check(await Fetch(missing) == 1, "missing explicit selection uses default");
        foreach (var invalid in new Action[] {
            () => _ = new ArtworkComponentComposition([Default(), Default()], Default()),
            () => _ = new ArtworkComponentComposition(Enumerable.Range(0, 65).Select(i => Register(() => throw new Exception(), Metadata with { Id = "id-"+i })), Default()),
            () => _ = new ArtworkComponentComposition([], Default(), required: ArtworkCapabilities.None),
            () => _ = new ArtworkComponentComposition([], Register(() => throw new Exception(), Metadata with { Id = "../bad" }))
        })
        { try { invalid(); throw new Exception("accepted invalid registration"); } catch (ArgumentException) { Check(true, "registration bounded and valid"); } }
        await SelectedAndFallbackAsync();
        await OwnershipAsync();
        await CreatingAndClosingAsync();
        await RequestsAndDrainAsync();
        Console.WriteLine($"PASS artwork composition: {_checks} checks; inert metadata/deferred creation, capability gates, independent bundled fallback, ownership, cancellation isolation, close-during-create/drain, fixed errors and no request replay.");
    }

    private static async Task SelectedAndFallbackAsync()
    {
        var optional = 0; var bundled = 0;
        var selected = new ArtworkComponentComposition([Register(() => { optional++; return new Factory(Metadata, () => new Source((_, _) => Task.FromResult(Payload(2)))); })], Default(() => bundled++), Metadata.Id);
        await using (var source = selected.CreateDeferred())
        { Check(await Fetch(source) == 2 && optional == 1 && bundled == 0 && selected.Activated && !selected.UsingBundled, "selected same-ID component actually supplies result"); }
        var brokenCalls = 0; var defaultCalls = 0;
        var broken = new ArtworkComponentComposition([Register(() => { brokenCalls++; throw new Exception("fixture-secret"); })], Default(() => Interlocked.Increment(ref defaultCalls)), Metadata.Id);
        var sources = Enumerable.Range(0, 12).Select(_ => broken.CreateDeferred()).ToArray();
        var results = await Task.WhenAll(sources.Select(Fetch));
        Check(results.All(x => x == 1) && brokenCalls == 1 && defaultCalls == 12, "concurrent creation failure latched once, independent defaults");
        Check(broken.Inspect()[0].Issue == ArtworkComponentIssue.ActivationFailed && broken.FellBack, "failure visible without raw exception");
        foreach (var source in sources) await source.DisposeAsync();
        var disposed = 0;
        foreach (var mismatch in new[] { true, false })
        {
            var bad = new ArtworkComponentComposition([Register(() => new Factory(mismatch ? Metadata with { DisplayName = "wrong" } : Metadata,
                () => throw new Exception("fixture-secret"), () => { disposed++; return ValueTask.CompletedTask; }))], Default(), Metadata.Id);
            await using var source = bad.CreateDeferred(); Check(await Fetch(source) == 1, "descriptor/create failure falls back");
            Check(bad.Inspect()[0].Issue == (mismatch ? ArtworkComponentIssue.DescriptorMismatch : ArtworkComponentIssue.CreationFailed), "exact fixed creation issue");
        }
        Check(disposed == 2, "failed owned factories disposed");
        var unavailable = new ArtworkComponentComposition([], Register(() => throw new Exception("fixture-secret"))).CreateDeferred();
        await Fail(unavailable.FetchAsync(Request), ArtworkFailure.ComponentUnavailable); await unavailable.DisposeAsync();
    }

    private static async Task OwnershipAsync()
    {
        var source = new Source(); var factory = new Factory(Metadata, () => source);
        var plan = new ArtworkComponentComposition([Register(() => factory)], Default(), Metadata.Id);
        var first = plan.CreateDeferred(); Check(await Fetch(first) == 1, "first owns factory/source");
        await using (var second = plan.CreateDeferred())
        { Check(await Fetch(second) == 1 && factory.Disposed == 0 && source.Disposed == 0, "reused factory rejected without disposing other owner"); }
        await first.DisposeAsync(); Check(source.Disposed == 1 && factory.Disposed == 1, "first owner closes both once");
        var shared = new Source(); var factories = new List<Factory>();
        var duplicate = new ArtworkComponentComposition([Register(() => { var f = new Factory(Metadata, () => shared); factories.Add(f); return f; })], Default(), Metadata.Id);
        var owner = duplicate.CreateDeferred(); await Fetch(owner);
        await using (var denied = duplicate.CreateDeferred())
        { await Fetch(denied); Check(shared.Disposed == 0 && factories[1].Disposed == 1 && factories[0].Disposed == 0, "reused source rejected, only new factory closed"); }
        await owner.DisposeAsync(); Check(shared.Disposed == 1 && factories[0].Disposed == 1, "source closes with its original owner");
    }

    private static async Task CreatingAndClosingAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var source = new Source(); var factory = new Factory(Metadata, () => source);
        var plan = new ArtworkComponentComposition([], Register(() => { calls++; entered.TrySetResult(); if (!release.Wait(Deadline)) throw new Exception("fixture creation deadline"); return factory; }));
        var lazy = plan.CreateDeferred();
        using var cancel = new CancellationTokenSource();
        var cancelled = lazy.FetchAsync(Request, cancel.Token);
        await entered.Task.WaitAsync(Deadline); var survivor = lazy.FetchAsync(Request); cancel.Cancel();
        try { await cancelled.WaitAsync(Deadline); throw new Exception("not cancelled"); }
        catch (OperationCanceledException e) { Check(e.CancellationToken == cancel.Token, "one creation waiter cancelled independently"); }
        release.Set(); await survivor.WaitAsync(Deadline);
        Check(calls == 1 && source.Calls == 1, "shared deferred creation, cancelled waiter sends no fetch");
        await lazy.DisposeAsync();

        using var closeRelease = new ManualResetEventSlim();
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var lateSource = new Source(close: () => { events.Add("source"); return ValueTask.CompletedTask; });
        var late = new ArtworkComponentComposition([], Register(() => { closeEntered.TrySetResult(); if (!closeRelease.Wait(Deadline)) throw new Exception("fixture creation deadline"); return new Factory(Metadata, () => lateSource, () => { events.Add("factory"); return ValueTask.CompletedTask; }); })).CreateDeferred();
        var fetch = late.FetchAsync(Request); await closeEntered.Task.WaitAsync(Deadline);
        late.Dispose(); var closing = late.DisposeAsync().AsTask();
        Check(!closing.IsCompleted, "synchronous dispose nonblocking, async close awaits owned creation");
        await Fail(fetch, ArtworkFailure.Disposed);
        closeRelease.Set(); await closing.WaitAsync(Deadline);
        Check(lateSource.Calls == 0 && events.SequenceEqual(new[] { "source", "factory" }), "late-created resource closed before factory without request");
        Check(ReferenceEquals(closing, late.DisposeAsync().AsTask()), "all close callers share completion");
    }

    private static async Task RequestsAndDrainAsync()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var independentlyCancelled = new ArtworkComponentComposition([], Register(() => new Factory(Metadata,
            () => new Source(async (_, token) =>
            {
                if (Interlocked.Increment(ref requestCount) == 1) { reading.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return Payload(2);
            })))).CreateDeferred();
        using (var cancel = new CancellationTokenSource())
        {
            var first = independentlyCancelled.FetchAsync(Request, cancel.Token);
            await reading.Task.WaitAsync(Deadline);
            Check(await Fetch(independentlyCancelled) == 2, "parallel request succeeds while another reads");
            cancel.Cancel();
            try { await first.WaitAsync(Deadline); throw new Exception("not cancelled"); }
            catch (OperationCanceledException e) { Check(e.CancellationToken == cancel.Token, "one body cancellation preserves caller identity"); }
            Check(await Fetch(independentlyCancelled) == 2, "body cancellation preserves shared source usability");
        }
        await independentlyCancelled.DisposeAsync();
        var fallback = 0;
        foreach (var badFetch in new Func<ArtworkRequest, CancellationToken, Task<ArtworkPayload>>[] {
            (_, _) => throw new Exception("fixture-secret"), (_, _) => throw new OperationCanceledException("fixture-secret")
        })
        {
            var plan = new ArtworkComponentComposition([Register(() => new Factory(Metadata, () => new Source(badFetch)))], Default(() => fallback++), Metadata.Id);
            await using var source = plan.CreateDeferred(); await Fail(source.FetchAsync(Request), ArtworkFailure.NetworkFailure);
            Check(fallback == 0 && !plan.FellBack, "request failure is never replayed on bundled implementation");
        }
        await using (var big = new ArtworkComponentComposition([], Register(() => new Factory(Metadata, () => new Source((_, _) => Task.FromResult(new ArtworkPayload([1,2,3], ArtworkMediaType.Png)))))).CreateDeferred())
            await Fail(big.FetchAsync(new(Request.Source, 2)), ArtworkFailure.TooLarge);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ArtworkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryClosed = 0;
        var pendingSource = new Source((_, _) => { started.TrySetResult(); return finish.Task; }, () => { sourceClosed.TrySetResult(); return ValueTask.CompletedTask; });
        var lazy = new ArtworkComponentComposition([], Register(() => new Factory(Metadata, () => pendingSource, () => { factoryClosed++; return ValueTask.CompletedTask; }))).CreateDeferred();
        var pending = lazy.FetchAsync(Request); await started.Task.WaitAsync(Deadline);
        var closing = lazy.DisposeAsync().AsTask(); await sourceClosed.Task.WaitAsync(Deadline);
        Check(!closing.IsCompleted && factoryClosed == 0, "factory retained while request ignores cancellation");
        finish.SetResult(Payload()); await Fail(pending, ArtworkFailure.Disposed); await closing.WaitAsync(Deadline);
        Check(factoryClosed == 1, "factory released after inflight request drained");
        await Fail(lazy.FetchAsync(Request), ArtworkFailure.Disposed);

        var badClose = new ArtworkComponentComposition([], Register(() => new Factory(Metadata,
            () => new Source(close: () => throw new Exception("fixture-secret")), () => { factoryClosed++; throw new Exception("fixture-secret"); }))).CreateDeferred();
        await Fetch(badClose);
        try { await badClose.DisposeAsync(); throw new Exception("close succeeded"); }
        catch (ArtworkComponentException e)
        {
            Check(e.Issues.SequenceEqual(new[] { ArtworkComponentIssue.SourceDisposeFailed, ArtworkComponentIssue.FactoryDisposeFailed }) && e.InnerException is null && !e.ToString().Contains("fixture-secret"), "both close failures fixed and sanitized");
            Check(factoryClosed == 2, "factory close attempted even when source close fails");
        }
    }

    private sealed class Factory(ArtworkDescriptor descriptor, Func<IArtworkSource> create, Func<ValueTask>? close = null) : IArtworkSourceFactory
    {
        public ArtworkDescriptor Descriptor => descriptor;
        public int Disposed;
        public IArtworkSource Create() => create();
        public ValueTask DisposeAsync() { Disposed++; return close?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private sealed class Source(Func<ArtworkRequest, CancellationToken, Task<ArtworkPayload>>? fetch = null, Func<ValueTask>? close = null) : IArtworkSource
    {
        public int Disposed, Calls;
        public Task<ArtworkPayload> FetchAsync(ArtworkRequest request, CancellationToken token = default)
        { Calls++; return fetch?.Invoke(request, token) ?? Task.FromResult(Payload()); }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() { Disposed++; return close?.Invoke() ?? ValueTask.CompletedTask; }
    }
}
