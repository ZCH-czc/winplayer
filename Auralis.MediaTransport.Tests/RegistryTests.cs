using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

internal static class RegistryTests
{
    internal static async Task<int> RunAsync(string root)
    {
        var checks=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
        var descriptor=new MediaTransportDescriptor("fixture.transport","Fixture",new(1,0),1,new(0,1),MediaTransportCapabilities.Full);
        var context=new MediaTransportContext(Path.Combine(root,"registry"),new MediaTransferBudget());
        var request=new MediaTransportRequest(new Uri("https://fixture.invalid/media"),null,null,"x");
        Check(context.ToString()==nameof(MediaTransportContext),"creation context diagnostic hides path");
        Check(typeof(MediaTransportRegistry).Assembly.GetReferencedAssemblies().All(a=>a.Name!="Auralis.MediaTransport.Http"&&a.Name!="Auralis"&&!a.Name!.StartsWith("Auralis.Platform")),"host is independent of concrete transport/app/platform");
        try{_=new MediaTransportContext("relative",context.Budget);throw new Exception("Relative path accepted");}catch(ArgumentException){checks++;}
        var defaultCalls=0;var optionalCalls=0;var order=new List<string>();
        MediaTransportRegistration bundled=new(descriptor,true,()=>{defaultCalls++;return new Factory(descriptor,()=>new Session(order),order);});
        MediaTransportRegistration optional=new(descriptor,true,()=>{optionalCalls++;return new Factory(descriptor,()=>new Session(order),order);});
        var registry=new MediaTransportRegistry([optional],bundled);
        Check(registry.Inspect(MediaTransportCapabilities.Full).Count==2&&defaultCalls==0&&optionalCalls==0,"same-ID optional/default metadata inspection is inert");
        await using(var unused=registry.CreateDeferred(null,context,MediaTransportCapabilities.Full)){}
        Check(defaultCalls==0&&optionalCalls==0,"disposing unused deferred session never activates factory");
        await using(var lazy=registry.CreateDeferred(null,context,MediaTransportCapabilities.Full))
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{await lazy.PrepareAsync(request,null,cancelled.Token);throw new Exception("Precancel ignored");}catch(OperationCanceledException){checks++;}
            Check(defaultCalls==0,"precancel does not activate deferred factory");
            var resources=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>lazy.PrepareAsync(request,null,default)));
            Check(defaultCalls==1&&optionalCalls==0&&resources.Distinct().Count()==12,"concurrent first use creates only bundled factory/session");
            foreach(var resource in resources)await resource.DisposeAsync();
            await lazy.DisposeAsync();await lazy.DisposeAsync();
            Check(order.SequenceEqual(new[]{"session","factory"}),"owned closure runs session before factory exactly once");
            try{await lazy.PrepareAsync(request,null,default);throw new Exception("Closed lazy session accepted work");}catch(ObjectDisposedException){checks++;}
        }
        var chosen=await registry.CreateAsync(descriptor.Id,context,MediaTransportCapabilities.Full);
        Check(optionalCalls==1&&!chosen.UsedFallback,"explicit same-ID selection chooses optional registration");await chosen.Session.DisposeAsync();

        foreach(var issue in new[]{MediaTransportIssue.Disabled,MediaTransportIssue.ApiMismatch,MediaTransportIssue.HostTooOld,MediaTransportIssue.MissingCapability})
        {
            var d=issue switch
            {
                MediaTransportIssue.ApiMismatch=>descriptor with{ApiVersion=99},
                MediaTransportIssue.HostTooOld=>descriptor with{MinimumHostVersion=new(99,0)},
                MediaTransportIssue.MissingCapability=>descriptor with{Capabilities=MediaTransportCapabilities.AuthorizedHttp},
                _=>descriptor
            };
            var bad=new MediaTransportRegistration(d,issue!=MediaTransportIssue.Disabled,()=>throw new Exception("Must stay inert"));
            var result=await new MediaTransportRegistry([bad],bundled).CreateAsync(d.Id,context,MediaTransportCapabilities.Full);
            Check(result.UsedFallback&&result.Issues.SequenceEqual(new[]{issue}),"metadata rejection before activation: "+issue);await result.Session.DisposeAsync();
        }
        var missing=await registry.CreateAsync("missing",context,MediaTransportCapabilities.Full);
        Check(missing.UsedFallback&&missing.Issues.Contains(MediaTransportIssue.NotRegistered),"unknown selection retains bundled default");await missing.Session.DisposeAsync();

        var failures=0;var failedFactoryCloses=0;
        var broken=new MediaTransportRegistration(descriptor,true,()=>
        {
            failures++;return new Factory(descriptor,()=>throw new Exception("SECRET creation failure"),onClose:()=>failedFactoryCloses++);
        });
        var failingRegistry=new MediaTransportRegistry([broken],bundled);
        for(var i=0;i<2;i++)
        {
            var result=await failingRegistry.CreateAsync(descriptor.Id,context,MediaTransportCapabilities.Full);
            Check(result.UsedFallback&&result.Issues.Contains(MediaTransportIssue.CreationFailed),"creation failure fallback has safe fixed issue");await result.Session.DisposeAsync();
        }
        Check(failures==1&&failedFactoryCloses==1,"managed optional creation failure is latched and owned factory disposed");
        var mismatchCloses=0;
        var mismatch=new MediaTransportRegistration(descriptor,true,()=>new Factory(descriptor with{ComponentVersion=new(2,0)},()=>throw new Exception("Must not create"),onClose:()=>mismatchCloses++));
        var mismatched=await new MediaTransportRegistry([mismatch],bundled).CreateAsync(descriptor.Id,context,MediaTransportCapabilities.Full);
        Check(mismatched.Issues.Contains(MediaTransportIssue.DescriptorMismatch)&&mismatchCloses==1,"factory descriptor verified before session creation");await mismatched.Session.DisposeAsync();

        var sharedSession=new Session();
        var reuseRegistry=new MediaTransportRegistry([],new(descriptor,true,()=>new Factory(descriptor,()=>sharedSession)));
        var owner=await reuseRegistry.CreateAsync(null,context,MediaTransportCapabilities.Full);
        try{await reuseRegistry.CreateAsync(null,context,MediaTransportCapabilities.Full);throw new Exception("Session reuse accepted");}
        catch(MediaTransportComponentException error){Check(error.Issues.Contains(MediaTransportIssue.ReusedSession)&&sharedSession.Closes==0,"reused session rejected without disposing other owner");}
        await owner.Session.DisposeAsync();Check(sharedSession.Closes==1,"original session owner still controls disposal");
        var sharedFactory=new Factory(descriptor,()=>new Session());
        var reusedFactoryRegistry=new MediaTransportRegistry([],new(descriptor,true,()=>sharedFactory));
        var factoryOwner=await reusedFactoryRegistry.CreateAsync(null,context,MediaTransportCapabilities.Full);
        try{await reusedFactoryRegistry.CreateAsync(null,context,MediaTransportCapabilities.Full);throw new Exception("Factory reuse accepted");}
        catch(MediaTransportComponentException error){Check(error.Issues.Contains(MediaTransportIssue.ReusedFactory)&&sharedFactory.Closes==0,"reused factory rejected without disposing other owner");}
        await factoryOwner.Session.DisposeAsync();

        var beforeRuntimeFailure=defaultCalls;
        var runtimeFailure=await new MediaTransportRegistry([new(descriptor,true,()=>new Factory(descriptor,()=>new Session(failPrepare:true)))],bundled)
            .CreateAsync(descriptor.Id,context,MediaTransportCapabilities.Full);
        try{await runtimeFailure.Session.PrepareAsync(request,null,default);throw new Exception("Prepare failure ignored");}
        catch(MediaTransportException error){Check(error.Failure==MediaTransportFailure.TransportFailure&&error.InnerException is null&&!error.ToString().Contains("SECRET")&&defaultCalls==beforeRuntimeFailure,
            "request-time failure is sanitized without replaying authorization to another implementation");}
        await runtimeFailure.Session.DisposeAsync();

        var disposalFactoryCloses=0;
        var disposal=new MediaTransportRegistry([],new(descriptor,true,()=>new Factory(descriptor,()=>new Session(failClose:true),onClose:()=>{disposalFactoryCloses++;throw new Exception("SECRET");})));
        var disposalSelection=await disposal.CreateAsync(null,context,MediaTransportCapabilities.Full);
        for(var i=0;i<2;i++)
        {
            try{await disposalSelection.Session.DisposeAsync();throw new Exception("Dispose failure hidden");}
            catch(MediaTransportComponentException error){Check(error.Issues.SequenceEqual(new[]{MediaTransportIssue.SessionDisposeFailed,MediaTransportIssue.FactoryDisposeFailed})&&!error.ToString().Contains("SECRET"),"disposal reports fixed issues and still closes factory");}
        }
        Check(disposalFactoryCloses==1,"failed disposal is also idempotent");

        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        var slowSession=new Session();
        var slowRegistry=new MediaTransportRegistry([],new(descriptor,true,()=>new Factory(descriptor,()=>
        {entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(5));return slowSession;})));
        var deferred=slowRegistry.CreateDeferred(null,context,MediaTransportCapabilities.Full);
        var pending=deferred.PrepareAsync(request,null,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var close=deferred.DisposeAsync().AsTask();
            Check(!close.IsCompleted,"close waits for in-flight factory creation");release.Set();
            try{await pending;throw new Exception("Closed creation started request");}catch(ObjectDisposedException){checks++;}
            await close.WaitAsync(TimeSpan.FromSeconds(5));Check(slowSession.Prepares==0&&slowSession.Closes==1,"late-created session disposed without preparing media");
        }
        finally{release.Set();await deferred.DisposeAsync();}

        var networkCalls=0;var factoryCalls=0;
        var realFactory=new HttpMediaTransportFactory(()=>{factoryCalls++;return new Handler((r,t)=>{networkCalls++;return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent([1,2,3,4])});});});
        Check(factoryCalls==0&&networkCalls==0&&realFactory.Descriptor==HttpMediaTransportFactory.Metadata,"default factory construction/metadata is inert");
        await using(var real=realFactory.Create(context))
        {
            Check(factoryCalls==1&&networkCalls==0&&!Directory.Exists(context.CacheDirectory),"default factory creation does not download/create cache");
            await using var pin=await real.PrepareAsync(new(request.Url,null,"audio/mpeg","test",useHostTransport:true),null,default);
            Check(networkCalls==1&&File.Exists(pin.Source.LocalPath),"factory forwards host context into real transfer");
        }
        await realFactory.DisposeAsync();
        Console.WriteLine($"PASS transport registry: {checks} assertions; inert metadata, deferred creation, independent fallback, capability/version checks, failure latch, ownership and safe disposal.");
        return checks;
    }
    private sealed class Factory(MediaTransportDescriptor descriptor,Func<IMediaTransportSession> create,List<string>? order=null,Action? onClose=null) : IMediaTransportFactory
    {
        public MediaTransportDescriptor Descriptor=>descriptor;public int Closes;
        public IMediaTransportSession Create(MediaTransportContext context)=>create();
        public ValueTask DisposeAsync(){Closes++;order?.Add("factory");onClose?.Invoke();return ValueTask.CompletedTask;}
    }
    private sealed class Session(List<string>? order=null,bool failClose=false,bool failPrepare=false) : IMediaTransportSession
    {
        public int Closes,Prepares;
        public Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request,string? identity,CancellationToken token,bool prefetch=false)
        {Interlocked.Increment(ref Prepares);if(failPrepare)throw new Exception("SECRET request failure");return Task.FromResult<IMediaTransportResource>(new Resource());}
        public ValueTask DisposeAsync(){Closes++;order?.Add("session");if(failClose)throw new Exception("SECRET");return ValueTask.CompletedTask;}
    }
    private sealed class Resource : IMediaTransportResource
    {public Uri Source=>new("https://fixture.invalid/test");public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}
