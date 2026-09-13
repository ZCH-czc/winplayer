using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageTests
{
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    internal static async Task RunAsync(OnlinePlatformCoordinator coordinator,PlatformBackendService backend,string track)
    {
        await PluginPageQueryTests.RunAsync(coordinator,backend,track);
        await PluginPageTargetTests.RunAsync(coordinator,backend,track);
        await PluginPageMediaTests.RunAsync(coordinator,backend);
        Check(!PlatformPageEntry.IsValidList([new(){Id="../file",Label="Bad",LabelEn="Bad"}]),"Manifest route is not a path");
        Check(!PlatformPageEntry.IsValidList([new(){Id="a",Label="A",LabelEn="A",Placement="script"}]),"No executable placements");
        Check(!PlatformPageValidation.IsValid(new(){Title="Bad",Version=7}),"Future renderer schema fails closed");
        Check(!PlatformPageValidation.IsValid(new(){Title="Bad",Layout="html"}),"No arbitrary layout");
        Check(!PlatformPageValidation.IsValid(new(){Title="Bad",Cards=null!}),"Null cards rejected");
        Check(!PlatformPageValidation.IsValid(new(){Title="Bad",Cards=[new(){Image=new Uri("file:///secret")}]}),"File image rejected");
        Check(!PlatformPageValidation.IsValid(new(){Title="Bad",Cards=Enumerable.Range(0,101).Select(_=>new PlatformPageCard()).ToArray()}),"Card budget");
        Check(!PlatformPageEntry.IsValidList([new(){Id="bad",Label="Bad",LabelEn="Bad",Presentation="page"}]),"Main presentation requires v2 declaration");
        Check(!PlatformPageValidation.IsValid(new(){Version=2,Title="Bad",Cards=[new(){Id="same"},new(){Id="same"}]}),"Duplicate card keys rejected");
        Check(!PlatformPageValidation.IsValid(new(){Version=2,Title="Bad",Cards=[new(){Id="ok",Images=Enumerable.Repeat(new Uri("https://example.test/a"),10).ToArray()}]}),"Gallery budget");
        Check(!PlatformPageValidation.IsValid(new(){Version=1,Title="Bad",Next=new("More","feed")}),"V1 cannot silently carry V2 fields");
        Check(!PlatformPageValidation.IsValid(new(){Version=2,Title="Bad",Cards=[new(){Id="ok",CommentCount=-1}]}),"Negative count rejected");
        Check(!PlatformPageEntry.IsValidList([new(){Id="global",Label="G",LabelEn="G",Placement="global",DocumentVersion=2,Presentation="page"}]),"Global requires v3");
        Check(!PlatformPageValidation.IsValid(new(){Version=2,Title="Bad",Tabs=[new(new("A","a"),true),new(new("B","b"))]}),"V2 cannot carry tabs");
        Check(!PlatformPageValidation.IsValid(new(){Version=3,Title="Bad",Tabs=[new(new("A","a")),new(new("B","b"))]}),"Tabs require selected section");
        Check(!PlatformPageValidation.IsValid(new(){Version=3,Title="Bad",Tabs=[new(new("A","a"),true),new(new("B","a"))]}),"Duplicate tab routes rejected");
        Check(!PlatformPageValidation.IsValid(new(){Version=3,Title="Bad",Tabs=null!}),"Null tabs rejected");
        var home=await coordinator.ReadPluginGlobalPageAsync("custom.public","hub",null,"en-US",default);
        Check(home.IsSuccess && home.Value.Tabs.Count==2 && home.Value.Tabs[0].Selected,"Provider page needs no media handle");
        var tab=home.Value.Tabs[1].Action.Handle;
        var selected=await coordinator.ReadPluginGlobalPageAsync("custom.public","hub",tab,"en-US",default);
        Check(selected.IsSuccess && selected.Value.Tabs[1].Selected && !selected.Value.Append,"Tab uses opaque backend navigation");
        Check(!(await coordinator.ReadPluginPageAsync(track,"hub",null,"en-US",default)).IsSuccess,"Global entry rejects media context");
        Check(!(await coordinator.ReadPluginPageAsync(track,"feed",tab,"en-US",default)).IsSuccess,"Global navigation rejects media reader");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","feed",null,"en-US",default)).IsSuccess,"Media entry rejects global context");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.account","hub",tab,"en-US",default)).IsSuccess,"Global navigation cannot cross provider");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("missing","hub",null,"en-US",default)).IsSuccess,"Missing global plugin rejected");
        Check((await coordinator.ReadPluginGlobalPageAsync("custom.public","hub",home.Value.Next!.Handle,"en-US",default)).Error?.Code==PlatformErrorCode.InvalidResponse,"Append cannot replace tab group");
        Check(!JsonSerializer.Serialize(home.Value).Contains("backend-tab"),"Tab routes/states stay backend-only");
        var feed=await coordinator.ReadPluginPageAsync(track,"feed",null,"en-US",default);
        Check(feed.IsSuccess && !feed.Value.Append && feed.Value.Next is not null,"V2 collection start");
        var more=await coordinator.ReadPluginPageAsync(track,"feed",feed.Value.Next!.Handle,"en-US",default);
        Check(more.IsSuccess && more.Value.Append && more.Value.CollectionHandle==feed.Value.CollectionHandle,"Continuation appends only to its collection");
        Check(more.Value.Cards[0].Handle==feed.Value.Cards[0].Handle,"Stable host card handles deduplicate repeated/pinned entries");
        var discussion=feed.Value.Cards[0].DiscussionHandle!;
        Check((await coordinator.GetCommunityCommentsAsync(discussion,null,null,false,default)).IsSuccess,"Declarative discussion opens existing comments");
        var foreign=await coordinator.ReadPluginPageAsync(track,"feed",feed.Value.Actions[0].Handle,"en-US",default);
        Check(foreign.Error?.Code==PlatformErrorCode.InvalidResponse,"Foreign discussion cannot cross provider");
        var cycle=await coordinator.ReadPluginPageAsync(track,"feed",more.Value.Next!.Handle,"en-US",default);
        Check(cycle.Error?.Code==PlatformErrorCode.InvalidResponse,"Backend repeated cursor rejected despite random navigation handles");
        Check(!(await coordinator.ReadPluginPageAsync(track,"start",feed.Value.Next.Handle,"en-US",default)).IsSuccess,"Append cannot cross entries");
        var creators=await coordinator.SearchCreatorsAsync("custom.public","Query",null,default);
        var creatorHandle=creators.Value.Items[0].Handle;
        var creatorPage=await coordinator.ReadPluginPageAsync(creatorHandle,"feed",null,"en-US",default);
        Check(creatorPage.IsSuccess && creatorPage.Value.Description=="creator","Search creator reaches declared page without media impersonation");
        Check(!(await coordinator.ReadPluginPageAsync(creatorHandle,"start",null,"en-US",default)).IsSuccess,"V1 media entry rejects creator context");
        Check(coordinator.GetBackendTrack(creatorHandle) is null,"Creator never becomes playable track");
        var feedProjection=JsonSerializer.Serialize(feed.Value);
        Check(!feedProjection.Contains("backend-")&&!feedProjection.Contains("unapproved.test"),"Gallery, card IDs, discussion IDs and cursor stay backend-only");
        var first=await coordinator.ReadPluginPageAsync(track,"start",null,"en-US",default);
        Check(first.IsSuccess && first.Value.Title=="Plugin title en-US","Manifest entry routes through lazy DLL");
        var action=first.Value.Actions.Single().Handle;
        var second=await coordinator.ReadPluginPageAsync(track,"start",action,"en-US",default);
        Check(second.IsSuccess && second.Value.Title=="A page unknown to the core","New plugin-defined route without core branch");
        var projection=JsonSerializer.Serialize(first.Value);
        Check(!projection.Contains("backend-")&&!projection.Contains("unapproved.test"),"State, routes and external image URLs not projected");
        Check(!(await coordinator.ReadPluginPageAsync(track,"other",action,"en-US",default)).IsSuccess,"Actions bound to entry");
        Check(!(await coordinator.ReadPluginPageAsync("forged","start",action,"en-US",default)).IsSuccess,"Actions bound to media");
        Check(!(await coordinator.ReadPluginPageAsync(track,"start","page-forged","en-US",default)).IsSuccess,"Forged action denied");
        Check(!(await coordinator.ReadPluginPageAsync(track,"missing",null,"en-US",default)).IsSuccess,"Undeclared entry denied");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try {await coordinator.ReadPluginPageAsync(track,"start",null,"en-US",cancelled.Token);throw new InvalidOperationException("Cancellation lost");}
        catch(OperationCanceledException) { }
        backend.InvalidateMediaContext("custom.public");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","hub",tab,"en-US",default)).IsSuccess,"Revision revokes global tab");
        Check(!(await coordinator.ReadPluginPageAsync(track,"feed",feed.Value.Next.Handle,"en-US",default)).IsSuccess,"Revision revokes append");
        Check(!(await coordinator.GetCommunityCommentsAsync(discussion,null,null,false,default)).IsSuccess,"Revision revokes declarative comments");
        Check(!(await coordinator.ReadPluginPageAsync(creatorHandle,"feed",null,"en-US",default)).IsSuccess,"Revision revokes creator context");
        Check(!(await coordinator.ReadPluginPageAsync(track,"start",action,"en-US",default)).IsSuccess,"Account change revokes navigation");
        Console.WriteLine("PASS declarative pages: validation, lazy routing, unknown page, opaque actions, ownership, cancellation and revocation.");
    }
}

public sealed partial class RoutingProvider : IPlatformPagesCapability, IPlatformGlobalPagesCapability
{
    internal static int QueryReads;
    public Task<PlatformResult<PlatformPageDocument>> ReadGlobalPageAsync(PlatformGlobalPageReadRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Route == "query" && TargetDocument is { } document)
            return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(document));
        if (request.Route == "backend-target-search")
            return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new() {
                Version=5,Title="Results",Actions=[new("Read","backend-target-profile"){Target=new("feed",new("custom.public","backend-creator-9007199254740993"),"creator")}]
            }));
        if(request.Route is "query" or "backend-query-submit" or "backend-query-detail" or "backend-query-more" or "backend-query-bad")
        {
            QueryReads++;
            var form=PluginPageQueryTests.Schema;
            var first=request.Route=="query";
            return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new(){
                Version=4,Title="Query",Query=first || request.Route=="backend-query-bad" ? form : null,
                Cards=first ? [] : [new(){Id="result",Text=request.InputValues["query"]+"/"+request.InputValues["kind"]}],
                Actions=request.Route=="backend-query-submit"?[new("Detail","backend-query-detail")]:[],
                Next=request.Route=="backend-query-submit"?new("More","backend-query-more"):
                    request.Route=="backend-query-more"?new("Bad form","backend-query-bad"):null
            }));
        }
        return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new(){
            Version=3,Title="Global page",Tabs=[
                new(new("Overview","hub"),request.Route=="hub"),
                new(new("Other","backend-tab","backend-tab-state"),request.Route!="hub")],
            Next=new("Bad appended tabs","backend-tab","append")
        }));
    }
    public Task<PlatformResult<PlatformPageDocument>> ReadPageAsync(PlatformPageReadRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Route.StartsWith("backend-target-",StringComparison.Ordinal)) return ReadTargetPageAsync(request);
        if(request.Route is "feed" or "backend-feed" or "backend-foreign")
            return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new() {
                Version=2,Title="Feed",Description=request.ContextKind,
                Cards=[new(){Id="backend-card",Text="Post",Images=[new Uri("https://unapproved.test/gallery")],
                    Discussion=new(request.Route=="backend-foreign"?"foreign":"custom.public","backend-discussion")}],
                Next=new("More","backend-feed",request.State is null?"one":request.State=="one"?"two":"two"),
                Actions=request.Route=="feed"?[new("Bad owner","backend-foreign")]:[]
            }));
        return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new()
        {
            Title=request.Route=="start"?"Plugin title "+request.Language:"A page unknown to the core",
            Cards=[new(){Title="Card",Text="Plain <script> content",Image=new Uri("https://unapproved.test/image")}],
            Actions=request.Route=="start"?[new("Next","backend-new-page","backend-state")]:[]
        }));
    }
}
