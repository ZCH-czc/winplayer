using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class SettingsV2Tests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Rejected(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Check(true, message); return; }
        Check(false, message);
    }
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var group = new PlatformSettingGroup { Id="connection", Label="连接", LabelEn="Connection", Description="由插件声明" };
        var mode = new PlatformSettingManifest { Key="mode", Label="方式", LabelEn="Mode", Kind="choice", DefaultValue="automatic",
            Group=group, Choices=[new("automatic","自动") {LabelEn="Automatic"},new("custom","自定义") {LabelEn="Custom"}] };
        var endpoint = new PlatformSettingManifest { Key="server", Label="服务", LabelEn="Server", Kind="endpoint",
            Required=true, Group=group, LegacyKeys=["old.server"],
            When=new() { Key="mode",Value="custom",Hint="选择自定义后填写地址",HintEn="Choose Custom to enter an address." } };
        Check(PlatformSettingManifest.IsValidList([mode,endpoint]), "V2 groups, translations and condition validate");
        foreach(var bad in new[] {
            endpoint with {When=endpoint.When! with {Key="missing"}},
            endpoint with {When=endpoint.When! with {Key="server"}},
            endpoint with {When=endpoint.When! with {Value="unknown"}},
            endpoint with {When=endpoint.When! with {Value=null!}},
            endpoint with {When=endpoint.When! with {Hint=""}},
            endpoint with {When=endpoint.When! with {HintEn=null!}},
            endpoint with {Group=group with {Label="inconsistent"}},
            endpoint with {Group=group with {Id="../unsafe"}},
            endpoint with {DescriptionEn="line\nbreak"},
            endpoint with {LabelEn=null!} })
            Check(!PlatformSettingManifest.IsValidList([mode,bad]), "Malformed metadata/condition rejected");
        Check(!PlatformSettingManifest.IsValidList([mode with {When=endpoint.When},endpoint]), "Dependency chain/cycle rejected");
        Check(!PlatformSettingManifest.IsValidList([mode with {Kind="endpoint",DefaultValue="",Choices=[]},endpoint]), "Endpoint parent rejected");
        Check(!PlatformSettingManifest.IsValidList([mode with {Choices=[new("automatic","Auto"){LabelEn=null!}]},endpoint]), "Null translation rejected");

        var plugin=Path.Combine(root,"tests.approved");
        Directory.CreateDirectory(plugin);
        File.Copy(typeof(FixturePlugin).Assembly.Location,Path.Combine(plugin,"Fixture.dll"));
        var manifest=JsonNode.Parse("""
            {"schemaVersion":5,"id":"tests.approved","displayName":"Fixture","version":"1.0.0",
             "minimumHostApiVersion":1,"maximumHostApiVersion":1,"entryAssembly":"Fixture.dll",
             "entryType":"Auralis.Platform.Host.Tests.FixturePlugin",
             "hostRequirements":{"minimumHostSdkVersion":"2.8.0","requiredFeatures":["comment-artwork.v1","settings.v1","settings.v2","lyrics-lookup.v1"]},
             "providers":[{"id":"fixture","displayName":"Fixture","capabilities":["LyricsLookup"],"commentArtworkDomains":[]}]}
            """)!.AsObject();
        manifest["providers"]![0]!["settings"]=JsonSerializer.SerializeToNode(new[]{mode,endpoint});
        var path=Path.Combine(plugin,"platform.plugin.json");
        async Task<PlatformPluginDiscoveryResult> Discover(JsonObject json,PlatformHostCompatibility? profile=null)
        {
            await File.WriteAllTextAsync(path,json.ToJsonString());
            return await new PlatformPluginCatalog([plugin],profile).DiscoverAsync();
        }
        var discovery=await Discover(manifest);
        Check(discovery.Plugins.Count==1,"V2 discovered without DLL activation");
        var settings=discovery.Plugins.Single().Providers.Single().Settings;
        Check(settings[0].Group==group&&settings[1].When==endpoint.When,"Metadata preserved");
        Check(settings is not PlatformSettingManifest[] && settings[0].Choices is not PlatformSettingChoice[],"Settings and choices are frozen");
        var missing=(JsonObject)manifest.DeepClone();
        missing["hostRequirements"]!["requiredFeatures"]=new JsonArray("comment-artwork.v1","settings.v1","lyrics-lookup.v1");
        Check((await Discover(missing)).Plugins.Count==0,"Underdeclared v2 feature rejected");
        var under=(JsonObject)manifest.DeepClone();under["hostRequirements"]!["minimumHostSdkVersion"]="2.7.0";
        Check((await Discover(under)).Plugins.Count==0,"V2 cannot pretend to target older SDK");
        var old=new PlatformHostCompatibility(new Version(2,7,0),PlatformHostCompatibility.Current.Features.Where(f=>f!="settings.v2"));
        Check((await Discover(manifest,old)).Diagnostics.Any(d=>d.Code==PlatformPluginDiagnosticCode.HostSdkIncompatible),"Older Host gets a typed compatibility error");
        var absentFeature=new PlatformHostCompatibility(new Version(2,8,0),old.Features);
        Check((await Discover(manifest,absentFeature)).Diagnostics.Any(d=>d.Code==PlatformPluginDiagnosticCode.HostFeatureUnsupported),"Missing runtime feature rejected");
        var cross=(JsonObject)manifest.DeepClone();
        cross["providers"]![0]!["settings"]=JsonSerializer.SerializeToNode(new[]{endpoint});
        Check((await Discover(cross)).Plugins.Count==0,"Cross-provider setting reference rejected");

        await Discover(manifest);
        var hashes=Directory.GetFiles(plugin).ToDictionary(f=>Path.GetFileName(f)!,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        Directory.CreateDirectory(Path.Combine(root,".approvals"));
        await File.WriteAllTextAsync(Path.Combine(root,".approvals","tests.approved.json"),JsonSerializer.Serialize(new PluginInstallReceipt(1,"tests.approved",hashes)));
        var services=new FixtureServices();
        await using(var inert=new PlatformPluginHost(new([root],services,trustPolicy:PlatformPluginIntegrity.VerifyAsync)))
        {
            Check((await inert.DiscoverAsync()).Providers.Count==1 && services.Contexts==0 && services.HttpClients==0,"Trusted discovery remains inert");
        }
        var storePath=Path.Combine(root,"settings.json");
        var store=new PlatformSettingsStore(storePath);
        await using var backend=new PlatformBackendService([root],NullAppLogger.Instance,settings:store);
        var registration=(await backend.DiscoverAsync()).Providers.Single();
        var initial=await backend.ReadSettingsAsync(registration);
        Check(!initial.Single(s=>s.Key=="server").Enabled && initial.Single(s=>s.Key=="server").Required,"Inactive required setting explicitly marked");
        Check(await store.ForPlugin("tests.approved").GetAsync("server",default) is null,"Plugin cannot read inactive value");
        await Rejected(()=>backend.SaveSettingAsync("fixture","server","https://example.test",default),"Native rejects inactive save");
        Check(!File.Exists(storePath),"Rejected save writes no preferences");
        Check(await backend.LookupLyricsAsync(new("Title","Artist","Album",20),default) is not null,"Inactive required field allows real synthetic capability routing");
        await backend.SaveSettingAsync("fixture","mode","custom",default);
        var custom=await backend.ReadSettingsAsync(registration);
        Check(custom.Single(s=>s.Key=="server").Enabled && custom.Single(s=>s.Key=="server").Value=="","Active empty required field gates readiness");
        Check(await backend.LookupLyricsAsync(new("Title","Artist","Album",20),default) is null,"Required active setting gates automatic lookup");
        await backend.SaveSettingAsync("fixture","server","https://example.test/service/",default);
        Check(await store.ForPlugin("tests.approved").GetAsync("server",default)=="https://example.test/service","Plugin and UI use normalized persisted value");
        await backend.SaveSettingAsync("fixture","mode","automatic",default);
        Check(await store.ForPlugin("tests.approved").GetAsync("server",default) is null,"Inactive saved endpoint not exposed to plugin");
        var inactive=await backend.ReadSettingsAsync(registration);
        Check(inactive.Single(s=>s.Key=="server").Value=="https://example.test/service","Inactive preference retained for editing, not erased");
        Check(inactive.All(s=>!s.Enabled||!s.Required||s.Value.Length>0),"Inactive required field does not block provider");
        await Rejected(()=>store.SetScopedAsync("tests.approved","server","https://example.test/late",default),"Direct scoped save also enforces dependency");
        await backend.SaveSettingAsync("fixture","mode","custom",default);
        Check(await store.ForPlugin("tests.approved").GetAsync("server",default)=="https://example.test/service","Reactivation restores last committed preference");
        await Rejected(()=>backend.SaveSettingAsync("fixture","server","https://example.test/?token=secret",default),"V2 retains endpoint privacy validation");
        await Rejected(()=>store.SetScopedAsync("tests.approved","undeclared","x",default),"Direct scoped save rejects undeclared key");
        var saved=await File.ReadAllTextAsync(storePath);
        var reload=new PlatformSettingsStore(storePath) { ResolveDeclarationsAsync=store.ResolveDeclarationsAsync };
        Check(await reload.ForPlugin("tests.approved").GetAsync("server",default)=="https://example.test/service","Restart restores matching dependency and value");
        Check(!saved.Contains("late")&&!saved.Contains("token"),"Rejected values never persisted");
        // Saving a dependency and dependent value is serialized with condition validation under the same lock.
        await Task.WhenAll(store.SetScopedAsync("tests.approved","mode","automatic",default),
            Rejected(()=>store.SetScopedAsync("tests.approved","server","https://example.test/race",default),"Queued stale dependent save rejected"));
        Check((await store.ReadValuesAsync("tests.approved",default))["server"]=="https://example.test/service","Queued condition change cannot overwrite dependent value");

        var failurePath=Path.Combine(root,"blocked");Directory.CreateDirectory(failurePath);
        var failure=new PlatformSettingsStore(failurePath) { ResolveDeclarationsAsync=store.ResolveDeclarationsAsync };
        await Rejected(()=>failure.SetScopedAsync("tests.approved","mode","custom",default),"Persistence failure returned");
        Check(await failure.ForPlugin("tests.approved").GetAsync("mode",default)=="automatic","Failed save rolls back dependency in memory");
        var active=true;
        var revocable=new PlatformSettingsStore(storePath) { ResolveDeclarationsAsync=(_,_)=>Task.FromResult<IReadOnlyList<PlatformSettingManifest>>(active?[mode,endpoint]:[]) };
        active=false;
        Check(await revocable.ForPlugin("tests.approved").GetAsync("mode",default) is null,"Revoked plugin reads nothing");
        await Rejected(()=>revocable.SetScopedAsync("tests.approved","mode","custom",default),"Revoked plugin cannot save");
        Console.WriteLine($"PASS {_checks} settings v2 checks: inert metadata, grouping, bilingual labels, dependencies, atomic save, persistence, rollback and revocation.");
    }
}
