using System.Net;
using Auralis.MediaTransport;

internal static class SharedTransferTests
{
    internal static async Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value,string name) { if(!value) throw new Exception(name);checks++; }
        var expiry=DateTimeOffset.UtcNow.AddMinutes(5);
        MediaTransportRequest Request(string path="same",bool hosted=true,DateTimeOffset? until=null) =>
            new(new Uri("https://shared.invalid/"+path),until??expiry,"audio/mpeg","test",useHostTransport:hosted);
        async Task Cancelled(Task<IMediaTransportResource> task)
        {
            try { await task;throw new Exception("Missing waiter cancellation"); }
            catch(OperationCanceledException) { checks++; }
        }

        var calls=0;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancel=new CancellationTokenSource();
        await using(var session=new HttpMediaTransportSession(Path.Combine(root,"shared-independent"),new Handler(async(r,t)=>
        {
            Interlocked.Increment(ref calls);entered.TrySetResult();await complete.Task.WaitAsync(t);return Bytes();
        }),budget:new MediaTransferBudget(16,1,8,1)))
        {
            var request=Request();
            var first=session.PrepareAsync(request,null,firstCancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second=session.PrepareAsync(request,null,default);
                firstCancel.Cancel();
                await Cancelled(first).WaitAsync(TimeSpan.FromSeconds(5));
                Check(calls==1&&!second.IsCompleted,"one waiter cancels without consuming another slot or cancelling shared HTTP");
                complete.TrySetResult();
                await using var secondPin=await second.WaitAsync(TimeSpan.FromSeconds(5));
                Check(File.ReadAllBytes(secondPin.Source.LocalPath).SequenceEqual(new byte[]{1,2,3,4}),"surviving waiter receives complete bytes");
            }
            finally { complete.TrySetResult(); }
        }

        var manyEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manyComplete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manyCalls=0;
        await using(var session=new HttpMediaTransportSession(Path.Combine(root,"shared-many"),new Handler(async(r,t)=>
        {
            Interlocked.Increment(ref manyCalls);manyEntered.TrySetResult();await manyComplete.Task.WaitAsync(t);return Bytes();
        }),budget:new MediaTransferBudget(16,1,8,1)))
        {
            var request=Request();
            var pending=Enumerable.Range(0,20).Select(_=>session.PrepareAsync(request,null,default)).ToArray();
            try
            {
                await manyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(manyCalls==1,"twenty identical waiters share one HTTP operation and budget slot");
                manyComplete.TrySetResult();
                var resources=await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
                Check(resources.Select(p=>p.Source).Distinct().Count()==1&&resources.Distinct().Count()==20,"shared result gives twenty independent pins");
                await Task.WhenAll(resources.Take(19).Select(p=>p.DisposeAsync().AsTask()));
                for(var i=0;i<10;i++)
                {
                    await using var pressure=await session.PrepareAsync(Request("pressure-"+i),null,default);
                }
                Check(File.Exists(resources[19].Source.LocalPath),"remaining shared pin survives sibling release and cache pressure");
                await resources[19].DisposeAsync();
            }
            finally { manyComplete.TrySetResult(); }
        }

        var cancelledBody=new ClosingStream();var cancellationCalls=0;
        var cancelledFolder=Path.Combine(root,"shared-all-cancel");
        using var cancelA=new CancellationTokenSource();using var cancelB=new CancellationTokenSource();
        await using(var session=new HttpMediaTransportSession(cancelledFolder,new Handler((r,t)=>Task.FromResult(
            Interlocked.Increment(ref cancellationCalls)==1?new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(cancelledBody)}:Bytes()))))
        {
            var request=Request();
            var first=session.PrepareAsync(request,null,cancelA.Token);
            var second=session.PrepareAsync(request,null,cancelB.Token);
            try
            {
                await cancelledBody.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancelA.Cancel();await Cancelled(first).WaitAsync(TimeSpan.FromSeconds(5));
                Check(!cancelledBody.Cancelled.Task.IsCompleted,"first cancelled waiter leaves shared transfer running");
                cancelB.Cancel();await cancelledBody.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!second.IsCompleted,"last waiter waits for cooperative partial cleanup");
                cancelledBody.Finish.TrySetResult();await Cancelled(second).WaitAsync(TimeSpan.FromSeconds(5));
                Check(cancelledBody.Closed&&!Directory.GetFiles(cancelledFolder).Any(),"last cancellation drains worker and deletes partial");
                await using var fresh=await session.PrepareAsync(request,null,default);
                Check(cancellationCalls==2&&File.Exists(fresh.Source.LocalPath),"new request never rejoins abandoned worker");
            }
            finally { cancelledBody.Finish.TrySetResult(); }
        }

        var failureEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureComplete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCalls=0;
        await using(var session=new HttpMediaTransportSession(Path.Combine(root,"shared-failure"),new Handler(async(r,t)=>
        {
            if(Interlocked.Increment(ref failureCalls)>1)return Bytes();
            failureEntered.TrySetResult();await failureComplete.Task.WaitAsync(t);return new(HttpStatusCode.ServiceUnavailable);
        })))
        {
            var request=Request();var first=session.PrepareAsync(request,null,default);
            try
            {
                await failureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second=session.PrepareAsync(request,null,default);failureComplete.TrySetResult();
                foreach(var pending in new[]{first,second})
                {
                    try{await pending;throw new Exception("Missing shared failure");}
                    catch(MediaTransportException error){Check(error.Failure==MediaTransportFailure.Unavailable&&error.InnerException is null,"each waiter receives safe shared failure");}
                }
                await using var retry=await session.PrepareAsync(request,null,default);
                Check(failureCalls==2,"failed transfer removed so explicit retry can succeed");
            }
            finally { failureComplete.TrySetResult(); }
        }

        var closeBody=new ClosingStream();var closeFolder=Path.Combine(root,"shared-shutdown");
        var closing=new HttpMediaTransportSession(closeFolder,new Handler((r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(closeBody)})));
        try
        {
            var request=Request();var one=closing.PrepareAsync(request,null,default);var two=closing.PrepareAsync(request,null,default);
            await closeBody.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close=closing.DisposeAsync().AsTask();await closeBody.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted,"session shutdown tracks shared worker after waiters cancel");
            closeBody.Finish.TrySetResult();await Cancelled(one);await Cancelled(two);await close.WaitAsync(TimeSpan.FromSeconds(5));
            Check(closeBody.Closed&&!Directory.GetFiles(closeFolder).Any(),"shared shutdown drains every owner and partial");
        }
        finally { closeBody.Finish.TrySetResult();await closing.DisposeAsync(); }

        foreach(var boundary in new[]{"url","expiry","headers","prefetch","identity","variant"})
        {
            var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests=0;
            await using var session=new HttpMediaTransportSession(Path.Combine(root,"shared-boundary-"+boundary),new Handler(async(r,t)=>
            {
                if(Interlocked.Increment(ref requests)==2)arrived.TrySetResult();
                await finish.Task.WaitAsync(t);return Bytes();
            }));
            var firstRequest=Request();
            var secondRequest=new MediaTransportRequest(boundary=="url"?new Uri("https://shared.invalid/different"):firstRequest.Url,
                boundary=="expiry"?expiry.AddMinutes(1):expiry,"audio/mpeg",boundary=="variant"?"other":"test",
                boundary=="headers"?new Dictionary<string,string>{{"X-Access","different"}}:null,useHostTransport:true);
            var first=session.PrepareAsync(firstRequest,"track",default);
            var second=session.PrepareAsync(secondRequest,boundary=="identity"?"other-track":"track",default,boundary=="prefetch");
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(requests==2,"different "+boundary+" must not coalesce authorization/policy");
            }
            finally { finish.TrySetResult(); }
            await using var one=await first;await using var two=await second;
        }

        var clock=new Clock(expiry.AddMinutes(-5));
        var expiryEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiryFinish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiryFolder=Path.Combine(root,"shared-expiry");
        await using(var session=new HttpMediaTransportSession(expiryFolder,new Handler(async(r,t)=>
        {
            expiryEntered.TrySetResult();await expiryFinish.Task.WaitAsync(t);return Bytes();
        }),clock))
        {
            var request=Request();var first=session.PrepareAsync(request,null,default);var second=session.PrepareAsync(request,null,default);
            try
            {
                await expiryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Now=expiry.AddSeconds(1);expiryFinish.TrySetResult();
                foreach(var pending in new[]{first,second})
                {
                    try{await pending;throw new Exception("Expired shared result accepted");}
                    catch(MediaTransportException error){Check(error.Failure==MediaTransportFailure.Expired,"shared result preserves expiry boundary for each waiter");}
                }
                Check(!Directory.GetFiles(expiryFolder).Any(),"expired shared response cannot populate cache");
            }
            finally { expiryFinish.TrySetResult(); }
        }
        Console.WriteLine($"PASS shared transfers: {checks} assertions; independent cancellation/pins, twenty waiters, failed retry, authorization boundaries, expiry, abandonment and shutdown drain.");
        return checks;
    }
    private static HttpResponseMessage Bytes()=>new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3,4])};
}
