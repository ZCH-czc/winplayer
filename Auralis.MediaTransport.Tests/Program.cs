using System.Net;
using System.Net.Http.Headers;
using Auralis.MediaTransport;

if (args.Length == 2 && args[0] == "--loader-worker")
{
    try { await LoaderTests.RunWorkerAsync(args[1]); return 0; }
    catch (Exception error) { Console.Error.WriteLine("Loader fixture failed: " + error); return 1; }
}

var root = Path.Combine(Path.GetTempPath(), "Auralis-transport-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
var failed = false;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
MediaTransportRequest Request(string path = "media?a=1", bool buffered = true, IReadOnlyDictionary<string,string>? headers = null) =>
    new(new Uri("https://media.invalid/" + path), DateTimeOffset.UtcNow.AddMinutes(5), "audio/mpeg", "standard", headers, buffered);
HttpResponseMessage Bytes() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3,4]) };
async Task Failure(Func<Task> operation, MediaTransportFailure expected)
{
    try { await operation(); throw new Exception("Missing typed failure"); }
    catch (MediaTransportException error) { Check(error.Failure == expected && error.InnerException is null && !error.ToString().Contains("media.invalid"), "safe typed failure " + expected); }
}
try
{
    Check(typeof(IMediaTransportSession).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.StartsWith("Auralis.Platform") && a.Name != "Auralis"), "contract independent of app/platform");
    Check(typeof(HttpMediaTransportSession).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.StartsWith("Auralis.Platform") && a.Name != "Auralis"), "implementation independent of app/platform");
    if (args.SequenceEqual(new[]{"--shared-only"}))
    {
        await SharedTransferTests.RunAsync(root);
        return 0;
    }
    if (args.SequenceEqual(new[]{"--archives-only"}))
    {
        await ArchiveTests.RunAsync();
        return 0;
    }
    var calls = 0;
    var handler = new Handler((r,t)=>{ calls++; return Task.FromResult(Bytes()); });
    var cache = Path.Combine(root,"basic");
    await using (var transport = new HttpMediaTransportSession(cache,handler))
    {
        Check(calls==0 && !Directory.Exists(cache), "construction is network/write inert");
        var direct = Request(buffered:false);
        await using var directResource = await transport.PrepareAsync(direct,null,default);
        Check(directResource.Source==direct.Url && calls==0,"direct URI preserved");
        using var cancel = new CancellationTokenSource();cancel.Cancel();
        try { await transport.PrepareAsync(direct,null,cancel.Token);throw new Exception("Precancel failed"); } catch(OperationCanceledException) { checks++; }
        var first = await transport.PrepareAsync(Request(),null,default);
        Check(first.Source.IsFile && File.ReadAllBytes(first.Source.LocalPath).SequenceEqual(new byte[]{1,2,3,4}),"complete buffered source");
        await using var repeat = await transport.PrepareAsync(Request(),null,default);
        Check(repeat.Source==first.Source && !ReferenceEquals(repeat, first) && calls==1,"cache hit has independent ownership");
        var other = await transport.PrepareAsync(Request("media?a=2"),null,default);
        Check(other.Source!=first.Source && calls==2,"query authorization not collapsed by URI path");
        var identity = await transport.PrepareAsync(Request("renew?sig=old"),"track:quality",default);
        await using var renewed = await transport.PrepareAsync(Request("renew?sig=new"),"track:quality",default);
        Check(renewed.Source==identity.Source,"explicit identity supports authorized renewal cache");
        await first.DisposeAsync(); await other.DisposeAsync(); await identity.DisposeAsync();
        Check(File.Exists(first.Source.LocalPath),"bounded session cache survives pin release");
        await Failure(()=>transport.PrepareAsync(new(new Uri("http://remote.invalid/a"),null,null,"x"),null,default),MediaTransportFailure.InvalidUri);
        await Failure(()=>transport.PrepareAsync(new(new Uri("https://media.invalid/a"),DateTimeOffset.UtcNow.AddSeconds(-1),null,"x"),null,default),MediaTransportFailure.Expired);
    }
    Check(!Directory.EnumerateFiles(cache).Any(),"disposal removes owned cached files: " + string.Join(",", Directory.EnumerateFiles(cache).Select(Path.GetFileName)));

    var pinCache = Path.Combine(root, "pins");
    var pinSession = new HttpMediaTransportSession(pinCache, new Handler((r,t)=>Task.FromResult(Bytes())));
    var pins = new List<IMediaTransportResource>();
    try
    {
        var firstPin = await pinSession.PrepareAsync(Request("pinned-first"), null, default);
        pins.Add(firstPin);
        var secondPin = await pinSession.PrepareAsync(Request("pinned-first"), null, default);
        pins.Add(secondPin);
        for (var i = 0; i < 11; i++) pins.Add(await pinSession.PrepareAsync(Request("pin-" + i), null, default));
        Check(pins.All(p=>File.Exists(p.Source.LocalPath)), "every prepared pin survives cache pressure, not just most recent");
        Check(Directory.GetFiles(pinCache).Length == 12, "protected entries may exceed soft cache count without deleting live sources");
        Check(firstPin.ToString()==nameof(IMediaTransportResource), "resource diagnostics hide backend source");
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>firstPin.DisposeAsync().AsTask()));
        Check(File.Exists(secondPin.Source.LocalPath), "idempotent release of first holder preserves second holder");
        await secondPin.DisposeAsync();
        Check(!File.Exists(secondPin.Source.LocalPath), "last release allows eviction under pressure");
        await pins[2].DisposeAsync(); await pins[3].DisposeAsync(); await pins[4].DisposeAsync();
        Check(Directory.GetFiles(pinCache).Length == 8 && pins.Skip(5).All(p=>File.Exists(p.Source.LocalPath)),
            "release trims to bound without harming remaining pins: count=" + Directory.GetFiles(pinCache).Length +
            "; retained=" + string.Join(",",pins.Select(p=>File.Exists(p.Source.LocalPath))));
        await pinSession.DisposeAsync();
        await Task.WhenAll(pins.Select(p=>p.DisposeAsync().AsTask()));
        Check(!Directory.GetFiles(pinCache).Any(), "pins released after session shutdown are harmless: " + string.Join(",",Directory.GetFiles(pinCache).Select(Path.GetFileName)));
    }
    finally { await pinSession.DisposeAsync(); }

    // Both downloads commit the same identity concurrently. A cache hit must create a second pin,
    // not return another reference to a shared disposable owner.
    var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var arrivals = 0;
    var duplicateCache = Path.Combine(root,"duplicate-pins");
    await using (var transport = new HttpMediaTransportSession(duplicateCache,new Handler(async (r,t)=>
    {
        if (Interlocked.Increment(ref arrivals)==2) rendezvous.TrySetResult();
        await rendezvous.Task.WaitAsync(t); return Bytes();
    })))
    {
        var acquired = await Task.WhenAll(transport.PrepareAsync(Request("duplicate?sig=one"),"same-identity",default),transport.PrepareAsync(Request("duplicate?sig=two"),"same-identity",default)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(acquired[0].Source == acquired[1].Source && !ReferenceEquals(acquired[0], acquired[1]), "concurrent duplicate commits return distinct pins to one cache file");
        Check(Directory.GetFiles(duplicateCache).Length==1,"concurrent duplicate candidate is removed");
        await acquired[0].DisposeAsync();
        var pressure = new List<IMediaTransportResource>();
        for(var i=0;i<9;i++) pressure.Add(await transport.PrepareAsync(Request("pressure-"+i),null,default));
        Check(File.Exists(acquired[1].Source.LocalPath), "concurrent duplicate pin remains protected after sibling release");
        await Task.WhenAll(pressure.Append(acquired[1]).Select(p=>p.DisposeAsync().AsTask()));
        Check(Directory.GetFiles(duplicateCache).Length==8,"concurrent release restores soft count budget");
    }

    var headers = new Dictionary<string,string>{{"X-Access","original"}};
    var snapshot = Request(headers:headers);headers["X-Access"]="changed";
    Check(snapshot.ToString()==nameof(MediaTransportRequest),"request diagnostic hides URL and credentials");
    await using (var transport = new HttpMediaTransportSession(Path.Combine(root,"headers"),new Handler((r,t)=>
    {
        Check(r.Headers.GetValues("X-Access").Single()=="original","headers snapshotted before async work");return Task.FromResult(Bytes());
    }))) await transport.PrepareAsync(snapshot,null,default);
    var redirects = 0;
    await using (var transport = new HttpMediaTransportSession(Path.Combine(root,"redirect"),new Handler((r,t)=>
    {
        if (++redirects==1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect){Headers={Location=new Uri("https://cdn.invalid/a")}});
        Check(!r.Headers.Contains("X-Access"),"cross-origin removes authorization headers");return Task.FromResult(Bytes());
    }))) await transport.PrepareAsync(snapshot,null,default);
    Check(redirects==2,"authorized HTTPS redirect works");
    await using (var transport = new HttpMediaTransportSession(Path.Combine(root,"bad-header"),new Handler((r,t)=>throw new Exception("Unexpected HTTP"))))
        await Failure(()=>transport.PrepareAsync(Request(headers:new Dictionary<string,string>{{"Host","injected"}}),null,default),MediaTransportFailure.InvalidHeaders);

    foreach (var mode in new[]{"range","length","empty","large"})
    {
        var folder=Path.Combine(root,mode);
        await using var transport=new HttpMediaTransportSession(folder,new Handler((r,t)=>
        {
            var response=Bytes();
            if(mode=="range") {response.StatusCode=HttpStatusCode.PartialContent;response.Content.Headers.ContentRange=new ContentRangeHeaderValue(0,3,10);}
            if(mode=="length") response.Content.Headers.ContentLength=20;
            if(mode=="empty") response.Content=new ByteArrayContent([]);
            if(mode=="large") response.Content.Headers.ContentLength=513L*1024*1024;
            return Task.FromResult(response);
        }));
        await Failure(()=>transport.PrepareAsync(Request(),null,default),mode=="large"?MediaTransportFailure.TooLarge:mode=="range"?MediaTransportFailure.IncompleteRange:MediaTransportFailure.Incomplete);
        Check(!Directory.EnumerateFiles(folder).Any(),"failed download leaves no committed or partial file");
    }
    var clock=new Clock(DateTimeOffset.UtcNow);
    var expiredCache=Path.Combine(root,"expires-in-flight");
    await using(var transport=new HttpMediaTransportSession(expiredCache,new Handler((r,t)=>{clock.Now=clock.Now.AddMinutes(10);return Task.FromResult(Bytes());}),clock))
    {
        await Failure(()=>transport.PrepareAsync(Request(),null,default),MediaTransportFailure.Expired);
        Check(!Directory.EnumerateFiles(expiredCache).Any(),"expired completed download never enters cache");
    }

    for(var run=0;run<12;run++)
    {
        var body=new ClosingStream();
        var folder=Path.Combine(root,"drain-"+run);
        var transport=new HttpMediaTransportSession(folder,new Handler((r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(body)})));
        var pending=transport.PrepareAsync(Request(),null,default);
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close=transport.DisposeAsync().AsTask();var closeAgain=transport.DisposeAsync().AsTask();
        await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!close.IsCompleted && !closeAgain.IsCompleted,"dispose waits for cooperative stream cleanup");
        body.Finish.TrySetResult();
        try {await pending;throw new Exception("Pending download survived shutdown");} catch(OperationCanceledException) {checks++;}
        await Task.WhenAll(close,closeAgain).WaitAsync(TimeSpan.FromSeconds(5));
        Check(body.Closed && !Directory.EnumerateFiles(folder).Any(),"dispose completion guarantees stream released and partial removed");
        try {await transport.PrepareAsync(Request(),null,default);throw new Exception("Disposed session accepted work");} catch(ObjectDisposedException) {checks++;}
        // This previously raced an open .part stream when Dispose returned before the downloader.
        Directory.Delete(folder);
    }
    var bodies=new[]{new ClosingStream(),new ClosingStream()};
    var parallelFolder=Path.Combine(root,"parallel-drain");
    // Workers may reach the handler in either order. Bind each body to the request identity,
    // not arrival order: otherwise releasing body[0] can leave pendingBoth[0] waiting on body[1].
    var parallel=new HttpMediaTransportSession(parallelFolder,new Handler((r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(bodies[r.RequestUri!.AbsolutePath == "/one" ? 0 : 1])})));
    // Deliberately start the second request first; drain order must not assume worker arrival order.
    var secondPending=parallel.PrepareAsync(Request("two"),null,default);
    await bodies[1].Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var pendingBoth=new[]{parallel.PrepareAsync(Request("one"),null,default),secondPending};
    await Task.WhenAll(bodies.Select(b=>b.Reading.Task)).WaitAsync(TimeSpan.FromSeconds(5));
    var parallelClose=parallel.DisposeAsync().AsTask();
    await Task.WhenAll(bodies.Select(b=>b.Cancelled.Task)).WaitAsync(TimeSpan.FromSeconds(5));
    bodies[0].Finish.TrySetResult();
    try {await pendingBoth[0].WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
    Check(!parallelClose.IsCompleted,"shutdown drains every operation, not just first");
    bodies[1].Finish.TrySetResult();
    try {await pendingBoth[1].WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
    await parallelClose.WaitAsync(TimeSpan.FromSeconds(5));
    Check(bodies.All(b=>b.Closed) && !Directory.EnumerateFiles(parallelFolder).Any(),"concurrent pending files are released");
    checks += await BudgetTests.RunAsync(root);
    checks += await SharedTransferTests.RunAsync(root);
    checks += await RegistryTests.RunAsync(root);
    checks += await CompositionTests.RunAsync(root);
    checks += await CatalogTests.RunAsync();
    checks += await InstallationTests.RunAsync();
    checks += await ArchiveTests.RunAsync();
    await LoaderTests.RunAsync();
    Console.WriteLine($"PASS media transport: {checks} in-process assertions plus the separately reported loader checks; independent assemblies, safe redirects/headers, complete cache, independent pins/pressure/duplicate commits, query isolation, expiry, cancellation, 12 shutdown drains and concurrent drain. No external network/accounts/native playback.");
    return 0;
}
catch (Exception error) { failed=true; Console.Error.WriteLine("FAIL transport fixture: " + error); return 1; }
finally
{
    // Only this generated GUID fixture tree is removed; no user media or profiles.
    try { Directory.Delete(root,true); }
    catch(IOException) when(failed) { Console.Error.WriteLine("Fixture cleanup incomplete; preserving original failure."); }
}

sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token); }
sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now=now; public override DateTimeOffset GetUtcNow()=>Now; }
sealed class ClosingStream : Stream
{
    public TaskCompletionSource Reading {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finish {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Closed {get;private set;}
    private bool _first=true;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    {
        if(_first){_first=false;buffer.Span[0]=1;return 1;}
        Reading.TrySetResult();
        try {await Task.Delay(Timeout.Infinite,token);} catch(OperationCanceledException){Cancelled.TrySetResult();await Finish.Task;throw;}
        return 0;
    }
    protected override void Dispose(bool disposing){Closed=true;base.Dispose(disposing);}
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
    public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();
    public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
