using System.Security.Cryptography;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageUpgradeTests
{
    internal static async Task RunAsync(string first, string second)
    {
        var hostPath=typeof(PlatformPluginHost).Assembly.Location;
        var before=SHA256.HashData(await File.ReadAllBytesAsync(hostPath));
        for(var revision=1;revision<=2;revision++)
        {
            var root=revision==1?first:second;
            var services=new FixtureServices();
            await using(var host=new PlatformPluginHost(new([root],services,trustPolicy:PlatformPluginIntegrity.VerifyAsync)))
            {
                var snapshot=await host.DiscoverAsync();
                if(snapshot.Providers.Count!=1||snapshot.Providers.Single().Provider.Pages.Count!=(revision==1?1:3)||services.Contexts!=0)
                    throw new InvalidOperationException("Inert discovery did not reflect the plugin revision");
            }
            await using var backend=new PlatformBackendService([root],NullAppLogger.Instance,
                settings:new PlatformSettingsStore(Path.Combine(root,"sample-preferences.json")));
            var coordinator=new OnlinePlatformCoordinator(backend);
            var registration=(await backend.DiscoverAsync()).Providers.Single();
            var preferences=await backend.ReadSettingsAsync(registration);
            if(preferences.Count!=(revision==1?0:2))throw new InvalidOperationException("Only plugin v2 adds grouped settings");
            if(revision==2 && (preferences[0].Group?.LabelEn!="Catalogue" || preferences[1].Enabled))
                throw new InvalidOperationException("Plugin-only group and dependency not projected");
            var global=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",null,"en-US",default);
            if(global.IsSuccess!=(revision==2))throw new InvalidOperationException("Only updated plugin declares a global page");
            if(revision==2){
                if(global.Value.Query is not { } query) throw new InvalidOperationException("Plugin v2 query was not projected");
                var found=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",query.Submit.Handle,"en-US",default,
                    new Dictionary<string,string>{{"query","Geometry"},{"kind","albums"}});
                if(!found.IsSuccess||found.Value.Cards.Single().Text!="Geometry/albums")
                    throw new InvalidOperationException("Plugin-only query/filter failed in frozen host");
                var catalogue=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",found.Value.Cards.Single().Actions.Single().Handle,"en-US",default);
                if(!catalogue.IsSuccess||!catalogue.Value.Title.StartsWith("Discography"))
                    throw new InvalidOperationException("Plugin-only typed entity target failed in frozen host");
                var playable=catalogue.Value.Cards[0].Media;
                if(playable is null || !(await coordinator.AcquireStreamAsync(playable.Handle,default)).IsSuccess)
                    throw new InvalidOperationException("Plugin-only media card did not reach existing lease resolution");
                var targetMore=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",catalogue.Value.Next!.Handle,"en-US",default);
                if(!targetMore.IsSuccess||!targetMore.Value.Append||targetMore.Value.CollectionHandle!=catalogue.Value.CollectionHandle)
                    throw new InvalidOperationException("Typed target lost context during continuation");
                var tab=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",global.Value.Tabs[1].Action.Handle,"en-US",default);
                if(!tab.IsSuccess||tab.Value.Tabs.Count!=3||!tab.Value.Tabs[1].Selected||tab.Value.Cards[0].Title!="Release notes")
                    throw new InvalidOperationException("Plugin-only global tabs failed");
                var archive=await coordinator.ReadPluginGlobalPageAsync("sample.pages","hub",tab.Value.Tabs[2].Action.Handle,"en-US",default);
                if(!archive.IsSuccess||!archive.Value.Tabs[2].Selected||archive.Value.Cards[0].Title!="Archive")
                    throw new InvalidOperationException("Third plugin-owned section failed");
            }
            var result=await coordinator.SearchAsync("sample.pages","Demo",20,null,default);
            if(!result.IsSuccess)throw new InvalidOperationException("Sample search failed");
            var media=result.Value.Items.Single().Handle;
            var profile=await coordinator.ReadPluginPageAsync(media,"profile",null,"en-US",default);
            if(!profile.IsSuccess)throw new InvalidOperationException("Profile page failed");
            var extra=await coordinator.ReadPluginPageAsync(media,"discography",null,"en-US",default);
            if(extra.IsSuccess!=(revision==2))throw new InvalidOperationException("Only v2 may expose its new entry");
            if(revision==2)
            {
                var nested=await coordinator.ReadPluginPageAsync(media,"profile",profile.Value.Actions.Single().Handle,"en-US",default);
                if(!nested.IsSuccess||nested.Value.Title!=extra.Value.Title)throw new InvalidOperationException("New plugin navigation failed");
                var more=await coordinator.ReadPluginPageAsync(media,"discography",extra.Value.Next!.Handle,"en-US",default);
                if(!more.IsSuccess||!more.Value.Append||more.Value.CollectionHandle!=extra.Value.CollectionHandle||
                    more.Value.Cards[0].Handle!=extra.Value.Cards[0].Handle||more.Value.Next is not null)
                    throw new InvalidOperationException("Plugin-only incremental list failed");
                var discussion=extra.Value.Cards[0].DiscussionHandle!;
                var comments=await coordinator.GetCommunityCommentsAsync(discussion,null,null,false,default);
                if(!comments.IsSuccess)throw new InvalidOperationException("Plugin-only discussion failed");
                var replies=await coordinator.GetCommunityCommentsAsync(discussion,null,comments.Value.Items[0].Handle,false,default);
                if(!replies.IsSuccess||replies.Value.NextPageHandle is null)throw new InvalidOperationException("Plugin-only thread failed");
                var end=await coordinator.GetCommunityCommentsAsync(discussion,replies.Value.NextPageHandle,comments.Value.Items[0].Handle,false,default);
                if(!end.IsSuccess||end.Value.Items[0].PublishedAt is null||end.Value.NextPageHandle is not null)
                    throw new InvalidOperationException("Plugin-only thread continuation failed");
                var mediaBefore=extra.Value.Cards[0].Media!;
                await backend.SaveSettingAsync("sample.pages","ordering","custom",default);
                if((await coordinator.AcquireStreamAsync(mediaBefore.Handle,default)).IsSuccess)
                    throw new InvalidOperationException("Plugin setting change did not revoke page media");
                if(!(await backend.ReadSettingsAsync(registration)).Single(s=>s.Key=="direction").Enabled)
                    throw new InvalidOperationException("Plugin-only condition did not activate");
                await backend.SaveSettingAsync("sample.pages","direction","descending",default);
                var reordered=await coordinator.ReadPluginPageAsync(media,"discography",null,"en-US",default);
                if(!reordered.IsSuccess||reordered.Value.Cards[0].Title!="Evening Colors")
                    throw new InvalidOperationException("Plugin did not consume its new preference");
                await backend.SaveSettingAsync("sample.pages","ordering","default",default);
                var restored=await coordinator.ReadPluginPageAsync(media,"discography",null,"en-US",default);
                if(!restored.IsSuccess||restored.Value.Cards[0].Title!="Quiet Geometry")
                    throw new InvalidOperationException("Inactive preference leaked into plugin behavior");
            }
            Console.WriteLine($"PASS standalone plugin v{revision}: inert entries, actual DLL, native coordinator, page navigation.");
        }
        if(!before.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(hostPath))))
            throw new InvalidOperationException("Host changed between plugin revisions");
        Console.WriteLine("PASS SAME HOST BINARY: plugin v2 adds a new page without rebuilding or changing the host.");
    }
}
