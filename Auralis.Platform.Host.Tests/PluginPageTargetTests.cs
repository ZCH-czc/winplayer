using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageTargetTests
{
    private static int _checks;
    private static void Check(bool value, string text) { _checks++; if (!value) throw new InvalidOperationException(text); }
    internal static async Task RunAsync(OnlinePlatformCoordinator coordinator, PlatformBackendService backend, string track)
    {
        // Control the actual collectible fixture DLL, not this test assembly's separate static fields.
        var fixture = System.Runtime.Loader.AssemblyLoadContext.All.Where(c => c.Name?.StartsWith("Auralis.Platform:tests.routing:") == true)
            .SelectMany(c => c.Assemblies).Select(a => a.GetType(typeof(RoutingProvider).FullName!)).OfType<Type>().Single();
        System.Reflection.FieldInfo Field(string name) => fixture.GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        void Set(string name, object? value) => Field(name).SetValue(null,value);
        T? Get<T>(string name) => (T?)Field(name).GetValue(null);
        var target = new PlatformPageTarget("feed", new("custom.public", "backend-creator-9007199254740993"), "creator");
        var action = new PlatformPageAction("Open creator", "backend-target-profile") { Target = target };
        var doc = new PlatformPageDocument { Version = 5, Title = "Discovery", Actions = [action] };
        Check(PlatformPageValidation.IsValid(doc), "Typed navigation is a bounded v5 action");
        Check(!PlatformPageValidation.IsValid(doc with { Version = 4 }), "Old documents cannot smuggle entity targets");
        Check(!PlatformPageValidation.IsValid(doc with { Version = 4, Actions = [], Cards = [new() { Id = "card", Actions = [action] }] }), "Old card actions cannot smuggle targets");
        foreach (var invalid in new[] {
            target with { EntryId = "../exec" }, target with { Entity = default },
            target with { Entity = new("custom.public", new string('a', 513)) },
            target with { Entity = new("custom.public", "bad\nidentity") },
            target with { ContextKind = "play" }, target with { ContextKind = "global" }
        }) Check(!PlatformPageValidation.IsValid(doc with { Actions = [action with { Target = invalid }] }), "Malformed or executable target denied");
        Check(!PlatformPageValidation.IsValid(doc with { Next = action }), "Auto append cannot switch entities");
        Check(!PlatformPageValidation.IsValid(doc with { Tabs = [new(action, true), new(new("Other", "other"))] }), "Tabs cannot switch contexts");
        Check(!PlatformPageQueryValidation.IsValid(PluginPageQueryTests.Schema with { Submit = action }), "Query cannot implicitly navigate to another entity");

        async Task<PlatformResult<OnlinePageView>> Root() =>
            await coordinator.ReadPluginGlobalPageAsync("custom.public", "query", null, "en-US", default, forceRefresh: true);
        try
        {
            // Every invalid document must fail before emitting any usable opaque action.
            foreach (var invalid in new[] {
                target with { Entity = new("custom.account", "foreign") },
                target with { EntryId = "missing" }, target with { EntryId = "start" },
                target with { EntryId = "hub" }, target with { EntryId = "work" },
                target with { ContextKind = "media" }
            })
            {
                Set("TargetDocument", doc with { Actions = [action with { Target = invalid }] });
                Check((await Root()).Error?.Code == PlatformErrorCode.InvalidResponse, "Undeclared, foreign, dialog or mismatched target denied");
            }
            Set("TargetDocument", doc);
            var root = await Root();
            Check(root.IsSuccess, "Global page publishes explicit entity link");
            var handle = root.Value.Actions.Single().Handle;
            Check(!JsonSerializer.Serialize(root.Value).Contains("backend-"), "Entity ID, target entry, route and state never reach WebView");
            var before = Get<int>("TargetReads");
            Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.account", "query", handle, "en-US", default)).IsSuccess, "Cross provider handle replay denied");
            Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public", "hub", handle, "en-US", default)).IsSuccess, "Cross root entry replay denied");
            Check(!(await coordinator.ReadPluginPageAsync(track, "feed", handle, "en-US", default)).IsSuccess, "Global handle cannot be replayed as entity handle");
            Check(!(await coordinator.ReadPluginGlobalPageAsync("custom.public", "query", handle, "en-US", default, new Dictionary<string,string>())).IsSuccess, "Explicit target cannot inject query values");
            Check(Get<int>("TargetReads") == before, "Invalid routing never invokes entity page");

            async Task<PlatformResult<OnlinePageView>> Read(string nav, CancellationToken ct = default) =>
                await coordinator.ReadPluginGlobalPageAsync("custom.public", "query", nav, "en-US", ct, forceRefresh: true);
            Set("TargetDocument", doc with { Actions = [new("Old schema", "backend-target-work") { Target = new("legacy-work",new("custom.public","backend-work"),"media") }] });
            var legacy = await Root();
            Check(legacy.IsSuccess, "Targets may declare a lower document schema");
            Check((await Read(legacy.Value.Actions.Single().Handle)).Error?.Code == PlatformErrorCode.InvalidResponse, "Target result is bounded by target entry, not source v5");
            Set("TargetDocument", doc);
            var profile = await Read(handle);
            Check(profile.IsSuccess && profile.Value.Title == "Creator", "Global discovery enters typed creator context");
            var cachedReads = Get<int>("TargetReads");
            var cached = await coordinator.ReadPluginGlobalPageAsync("custom.public", "query", handle, "en-US", default);
            Check(cached.IsSuccess && ReferenceEquals(cached.Value, profile.Value) && Get<int>("TargetReads") == cachedReads,
                "Returning to a valid page reuses its short-lived projection without another provider read");
            Check(Get<PlatformPageReadRequest>("LastTargetRequest") is { ContextKind: "creator", MediaId.Value: "backend-creator-9007199254740993", InputValues.Count: 0 }, "Exact ID retained; search inputs not leaked");
            Check(coordinator.GetBackendTrack(handle) is null, "Read-only navigation never mints playable media");
            var about = await Read(profile.Value.Tabs[1].Action.Handle);
            Check(about.IsSuccess && about.Value.Title == "About", "Tabs inherit target context without plugin remapping");
            var more = await Read(profile.Value.Next!.Handle);
            Check(more.IsSuccess && more.Value.Append && more.Value.CollectionHandle == profile.Value.CollectionHandle, "Feed continuation inherits target and collection");
            var discussion = more.Value.Cards.Single().DiscussionHandle!;
            Check((await coordinator.GetCommunityCommentsAsync(discussion, null, null, false, default)).IsSuccess, "Target page comments use existing guarded discussion flow");
            var work = await Read(profile.Value.Actions.Single().Handle);
            Check(work.IsSuccess && work.Value.Title == "Work" && Get<PlatformPageReadRequest>("LastTargetRequest")!.ContextKind == "media", "Creator to declared work page is metadata-only");
            Check((await Read(handle)).Value.Title == "Creator", "Old source link remains readable for back history");
            Check((await Root()).Value.Title == "Discovery", "Root global context is not overwritten");

            Set("TargetDocument", doc with { Actions = [], Query = PluginPageQueryTests.Schema with { Submit = new("Search", "backend-target-search") } });
            var form = await Root();
            var found = await coordinator.ReadPluginGlobalPageAsync("custom.public", "query", form.Value.Query!.Submit.Handle, "en-US", default,
                new Dictionary<string,string> { ["query"] = "private search text", ["kind"] = "music" });
            Check(found.IsSuccess, "Query returns typed results");
            Check((await Read(found.Value.Actions.Single().Handle)).IsSuccess && Get<PlatformPageReadRequest>("LastTargetRequest")!.InputValues.Count == 0, "Explicit target resets inherited source inputs");

            Set("TargetGate",new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            Set("TargetEntered",new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            var pending = Read(handle);
            await Get<TaskCompletionSource<bool>>("TargetEntered")!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            backend.InvalidateMediaContext("custom.public");
            Get<TaskCompletionSource<bool>>("TargetGate")!.SetResult(true);
            Check(!(await pending).IsSuccess, "Late entity reply cannot survive revision change");
            Check(!(await Read(handle)).IsSuccess && !(await Read(more.Value.Next!.Handle)).IsSuccess, "Revision revokes root, target and continuation handles");
            Check(!(await coordinator.GetCommunityCommentsAsync(discussion,null,null,false,default)).IsSuccess, "Revision also revokes target discussion");
            Set("TargetGate",null);
            Set("TargetDocument", doc);
            var fresh = (await Root()).Value.Actions.Single().Handle;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await Read(fresh, cancelled.Token); Check(false, "Cancellation lost"); } catch (OperationCanceledException) { Check(true, "Cancelled read rejected"); }
        }
        finally { Set("TargetDocument", null); Set("TargetGate",null); }
        Console.WriteLine($"PASS typed page navigation: {_checks} checks; ownership, exact context, inputs, tabs/feed/comments, cancellation and revocation.");
    }
}

public sealed partial class RoutingProvider
{
    internal static PlatformPageDocument? TargetDocument = null;
    internal static TaskCompletionSource<bool>? TargetGate = null;
    internal static TaskCompletionSource<bool>? TargetEntered = null;
    internal static PlatformPageReadRequest? LastTargetRequest;
    internal static int TargetReads;
    private async Task<PlatformResult<PlatformPageDocument>> ReadTargetPageAsync(PlatformPageReadRequest request)
    {
        TargetReads++; LastTargetRequest = request;
        if (TargetGate is { } gate) {
            TargetEntered!.TrySetResult(true); await gate.Task;
        }
        var work = request.Route == "backend-target-work";
        return PlatformResult<PlatformPageDocument>.Success(new() {
            Version = 5, Title = work ? "Work" : request.Route == "backend-target-about" ? "About" : "Creator",
            Cards = [new() { Id = "backend-target-card", Text = "Post", Discussion = new("custom.public", "backend-discussion") }],
            Tabs = request.Route == "backend-target-profile" ? [new(new("Activity","backend-target-profile"),true),new(new("About","backend-target-about"))] : [],
            Next = work ? null : new("More","backend-target-more",request.State is null ? "next" : "last"),
            Actions = work ? [] : [new("Read work","backend-target-work") { Target = new("work",new("custom.public","backend-work"),"media") }]
        });
    }
}
