using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageQueryTests
{
    internal static PlatformPageQuery Schema => new(new("Search", "backend-query-submit", "backend-secret-state"), [
        new() { Key="query", Label="Creator", MinLength=1, MaxLength=32 },
        new() { Key="kind", Label="Type", Kind="choice", Value="all", Options=[new("All","all"),new("Music","music")] }
    ]);
    private static void Check(bool value, string text) { if(!value) throw new InvalidOperationException(text); }
    internal static async Task RunAsync(OnlinePlatformCoordinator coordinator, PlatformBackendService backend, string track)
    {
        var schema=Schema;
        Check(PlatformPageQueryValidation.IsValid(schema),"Valid query schema");
        Check(!PlatformPageValidation.IsValid(new(){Version=3,Title="Bad",Query=schema}),"V3 cannot smuggle query");
        Check(!PlatformPageQueryValidation.IsValid(schema with {Fields=[]}),"Empty query rejected");
        Check(!PlatformPageQueryValidation.IsValid(schema with {Fields=Enumerable.Repeat(schema.Fields[0],5).ToArray()}),"Field budget and uniqueness");
        foreach(var field in new[] {
            schema.Fields[0] with {Kind="password"},schema.Fields[0] with {Key="../route"},
            schema.Fields[0] with {MaxLength=257},schema.Fields[0] with {MinLength=-1},
            schema.Fields[0] with {Value="a\nb"},schema.Fields[0] with {Options=[new("Unexpected","x")]},
            schema.Fields[1] with {Value="unknown"},schema.Fields[1] with {Options=[new("A","all"),new("B","all")]},
            schema.Fields[1] with {Options=null!}
        }) Check(!PlatformPageQueryValidation.IsValid(schema with {Fields=[field]}),"Malformed field rejected");
        Check(!PlatformPageQueryValidation.IsValid(schema with {Submit=new("Run","../exec")}),"No executable path");
        var values=new Dictionary<string,string>{{"query","yousa"},{"kind","music"}};
        Check(PlatformPageQueryValidation.Accepts(schema,values),"Valid submission");
        foreach(var invalid in new Dictionary<string,string>[] {
            [],new(){{"query","yousa"}},new(){{"query",""},{"kind","all"}},
            new(){{"query","a\nb"},{"kind","all"}},new(){{"query",new('x',33)},{"kind","all"}},
            new(){{"query","yousa"},{"kind","forged"}},new(){{"query","yousa"},{"kind","music"},{"extra","x"}}
        }) Check(!PlatformPageQueryValidation.Accepts(schema,invalid),"Reject invalid input values");
        var home=await coordinator.ReadPluginGlobalPageAsync("custom.public","query",null,"en-US",default);
        Check(home.IsSuccess && home.Value.Query is not null,"Read-only query projected");
        var query=home.Value.Query!;
        var projection=JsonSerializer.Serialize(query);
        Check(!projection.Contains("backend-"),"Query submit state and route remain backend-only");
        var before=RoutingProvider.QueryReads;
        foreach(var size in new[]{-1,0,5,21,int.MaxValue})
            Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",null,"en-US",default,preferredPageSize:size)).IsSuccess,"Reject invalid viewport batch before plugin invocation");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",null,"en-US",default,values)).IsSuccess,"No implicit query on initial read");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",query.Submit.Handle,"en-US",default)).IsSuccess,"Explicit form submission required");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","hub",query.Submit.Handle,"en-US",default,values)).IsSuccess,"Cannot cross entries");
        Check(!(await coordinator.ReadPluginPageAsync(track,"start",query.Submit.Handle,"en-US",default,values)).IsSuccess,"Cannot cross contexts");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",query.Submit.Handle,"en-US",default,new Dictionary<string,string>{{"query","yousa"},{"kind","invalid"}})).IsSuccess,"Invalid option denied");
        Check(RoutingProvider.QueryReads==before,"Invalid submissions never call plugin");
        var result=await coordinator.ReadPluginGlobalPageAsync("custom.public","query",query.Submit.Handle,"en-US",default,values);
        Check(result.IsSuccess && result.Value.Cards.Single().Text=="yousa/music","Submitted input routed");
        var handle=result.Value.Actions.Single().Handle;
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",handle,"en-US",default,values)).IsSuccess,"Ordinary navigation cannot replace inputs");
        values["query"]="mutated";
        var detail=await coordinator.ReadPluginGlobalPageAsync("custom.public","query",handle,"en-US",default);
        Check(detail.IsSuccess && detail.Value.Cards.Single().Text=="yousa/music","Navigation inherits immutable query snapshot");
        var next=await coordinator.ReadPluginGlobalPageAsync("custom.public","query",result.Value.Next!.Handle,"en-US",default);
        Check(next.IsSuccess && next.Value.Append && next.Value.Cards.Single().Text=="yousa/music","Append inherits exact submitted criteria");
        var bad=await coordinator.ReadPluginGlobalPageAsync("custom.public","query",next.Value.Next!.Handle,"en-US",default);
        Check(bad.Error?.Code==PlatformErrorCode.InvalidResponse,"Append cannot replace query form");
        backend.InvalidateMediaContext("custom.public");
        Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public","query",query.Submit.Handle,"en-US",default,values)).IsSuccess,"Revision revokes forms");
        Console.WriteLine("PASS query pages: schema, exact inputs, opaque handles, immutable navigation, append and revision.");
    }
}
