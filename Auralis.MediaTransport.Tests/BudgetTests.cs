using System.Net;
using Auralis.MediaTransport;

internal static class BudgetTests
{
    internal static async Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        MediaTransportRequest Request(string id = "item", bool buffered = true) =>
            new(new Uri("https://budget.invalid/" + id), DateTimeOffset.UtcNow.AddMinutes(5), "audio/mpeg", "test", useHostTransport: buffered);
        async Task Rejected(Task<IMediaTransportResource> task)
        {
            try { await task; throw new Exception("Missing budget rejection"); }
            catch (MediaTransportException error) { Check(error.Failure == MediaTransportFailure.BudgetExceeded && error.InnerException is null, "typed budget failure"); }
        }
        try { _ = new MediaTransferBudget(1, 1, 2, 1); throw new Exception("Invalid limits accepted"); }
        catch (ArgumentOutOfRangeException) { checks++; }

        // Reservation spans different sessions, not merely different calls on one session.
        var budget = new MediaTransferBudget(8, 2, 4, 1);
        var body = new GatedBody(6);
        var firstFolder = Path.Combine(root,"budget-known-first");
        var secondFolder = Path.Combine(root,"budget-known-second");
        await using (var first = new HttpMediaTransportSession(firstFolder, new Handler((r,t)=>Task.FromResult(Response(body, 6))), budget:budget))
        await using (var second = new HttpMediaTransportSession(secondFolder, new Handler((r,t)=>Task.FromResult(Bytes(4))), budget:budget))
        {
            var pending = first.PrepareAsync(Request(),null,default);
            try
            {
                await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Rejected(second.PrepareAsync(Request(),null,default));
                Check(!Directory.GetFiles(secondFolder).Any(), "known length rejects before preallocation/file write");
                body.Complete.TrySetResult();
                await using var heldCache = await pending;
                await using var next = await second.PrepareAsync(Request(),null,default);
                Check(File.Exists(heldCache.Source.LocalPath) && File.Exists(next.Source.LocalPath), "commit frees transfer budget but retains live cache pin");
            }
            finally { body.Complete.TrySetResult(); }
        }

        var slotBudget = new MediaTransferBudget(100,2,20,1);
        var prefetchBody = new GatedBody(4); var foregroundBody = new GatedBody(4);
        var calls = 0;
        await using (var session = new HttpMediaTransportSession(Path.Combine(root,"budget-slots"), new Handler((r,t)=>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Response(r.RequestUri!.AbsolutePath=="/prefetch"?prefetchBody:foregroundBody,4));
        }), budget:slotBudget))
        {
            var speculative = session.PrepareAsync(Request("prefetch"),null,default,true);
            Task<IMediaTransportResource>? foreground = null;
            try
            {
                await prefetchBody.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Rejected(session.PrepareAsync(Request("second-prefetch"),null,default,true));
                Check(calls==1,"speculative slot cap checked before HTTP");
                foreground = session.PrepareAsync(Request("foreground"),null,default);
                await foregroundBody.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Rejected(session.PrepareAsync(Request("third"),null,default));
                await using var direct = await session.PrepareAsync(Request("direct",false),null,default);
                Check(calls==2 && !direct.Source.IsFile,"direct sources bypass transfer admission without HTTP");
                prefetchBody.Complete.TrySetResult(); foregroundBody.Complete.TrySetResult();
                await using var speculativePin = await speculative;
                await using var foregroundPin = await foreground;
                Check(File.Exists(foregroundPin.Source.LocalPath),"foreground can prepare beside capped prefetch");
            }
            finally { prefetchBody.Complete.TrySetResult(); foregroundBody.Complete.TrySetResult(); }
        }

        // Unknown response size grows the reservation before writes. Cancellation returns both bytes
        // and slot only after cooperative stream cleanup, even across a separate session.
        var unknownBudget = new MediaTransferBudget(8,2,4,1);
        var unknownBody = new GatedBody(6);
        var unknownFolder = Path.Combine(root,"budget-unknown");
        using var cancel = new CancellationTokenSource();
        await using (var unknown = new HttpMediaTransportSession(unknownFolder, new Handler((r,t)=>Task.FromResult(Response(unknownBody,null))),budget:unknownBudget))
        await using (var sibling = new HttpMediaTransportSession(Path.Combine(root,"budget-unknown-sibling"),new Handler((r,t)=>Task.FromResult(Bytes(4))),budget:unknownBudget))
        {
            var pending = unknown.PrepareAsync(Request(),null,cancel.Token);
            try
            {
                await unknownBody.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Rejected(sibling.PrepareAsync(Request(),null,default));
                cancel.Cancel();
                try { await pending; throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) { checks++; }
                Check(unknownBody.Closed && !Directory.GetFiles(unknownFolder).Any(),"cancel cleans unknown-length partial before releasing budget");
                await using var ready = await sibling.PrepareAsync(Request(),null,default);
                Check(File.Exists(ready.Source.LocalPath),"cancel returns shared reservation");
            }
            finally { unknownBody.Complete.TrySetResult(); }
        }

        var growthFolder = Path.Combine(root,"budget-growth");
        var growthBudget = new MediaTransferBudget(8,1,4,1);
        await using (var session = new HttpMediaTransportSession(growthFolder,new Handler((r,t)=>Task.FromResult(
            r.RequestUri!.AbsolutePath=="/too-big"?Response(new GatedBody(10, complete:true),null):Bytes(8))),budget:growthBudget))
        {
            await Rejected(session.PrepareAsync(Request("too-big"),null,default));
            Check(!Directory.GetFiles(growthFolder).Any(),"unknown-length overflow leaves no partial or cache entry");
            await using var valid = await session.PrepareAsync(Request("valid"),null,default);
            await using var hit = await session.PrepareAsync(Request("valid"),null,default);
            Check(valid.Source==hit.Source,"failed growth restores capacity and cached hit remains usable");
        }
        var prefetchBudget = new MediaTransferBudget(8,2,4,2);
        await using (var session = new HttpMediaTransportSession(Path.Combine(root,"budget-prefetch-bytes"),
            new Handler((r,t)=>Task.FromResult(Bytes(6))),budget:prefetchBudget))
        {
            await Rejected(session.PrepareAsync(Request(),null,default,true));
            await using var foreground = await session.PrepareAsync(Request(),null,default);
            Check(new FileInfo(foreground.Source.LocalPath).Length==6,"foreground retains full limit after speculative byte rejection");
        }

        // Shutdown must not lend still-owned bytes to another session until the stream really closes.
        var drainBudget = new MediaTransferBudget(8,2,4,1);
        var slowClose = new ClosingStream();
        var closing = new HttpMediaTransportSession(Path.Combine(root,"budget-drain"),
            new Handler((r,t)=>Task.FromResult(Response(slowClose,6))),budget:drainBudget);
        await using (var sibling = new HttpMediaTransportSession(Path.Combine(root,"budget-drain-sibling"),
            new Handler((r,t)=>Task.FromResult(Bytes(4))),budget:drainBudget))
        {
            var pending = closing.PrepareAsync(Request(),null,default);
            try
            {
                await slowClose.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var close = closing.DisposeAsync().AsTask();
                await slowClose.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Rejected(sibling.PrepareAsync(Request(),null,default));
                Check(!close.IsCompleted,"draining request retains budget until stream cleanup");
                slowClose.Finish.TrySetResult();
                try { await pending; throw new Exception("Shutdown cancellation ignored"); } catch(OperationCanceledException) { checks++; }
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                await using var ready = await sibling.PrepareAsync(Request(),null,default);
                Check(File.Exists(ready.Source.LocalPath),"shutdown returns shared reservation after drain");
            }
            finally { slowClose.Finish.TrySetResult(); await closing.DisposeAsync(); }
        }

        var recoveryCalls = 0;
        await using (var session = new HttpMediaTransportSession(Path.Combine(root,"budget-http-recovery"),
            new Handler((r,t)=>Task.FromResult(++recoveryCalls==1?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable):Bytes(4))),
            budget:new MediaTransferBudget(8,1,4,1)))
        {
            try { await session.PrepareAsync(Request(),null,default); throw new Exception("HTTP failure ignored"); }
            catch(MediaTransportException error) { Check(error.Failure==MediaTransportFailure.Unavailable,"HTTP failure remains typed"); }
            await using var ready = await session.PrepareAsync(Request(),null,default);
            Check(File.Exists(ready.Source.LocalPath),"HTTP failure returns transfer slot");
        }
        Console.WriteLine($"PASS transfer budget: {checks} assertions; shared sessions, known/unknown length, admission, prefetch separation, cancellation/shutdown/HTTP recovery, cache pin independence.");
        return checks;
    }

    private static HttpResponseMessage Bytes(int size) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) };
    private static HttpResponseMessage Response(Stream body, int? length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        if (length is not null) response.Content.Headers.ContentLength = length;
        return response;
    }
    private sealed class GatedBody(int bytes, bool complete = false) : Stream
    {
        private bool _read;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_read) { _read=true; buffer.Span[..bytes].Clear(); return bytes; }
            Waiting.TrySetResult();
            if (!complete) await Complete.Task.WaitAsync(token);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed=true; base.Dispose(disposing); }
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long l)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
