using System.IO.Compression;
using System.Text.Json;

namespace Auralis.Platform.Host.Tests;

internal static class PluginUpdateReviewTests
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException(label); }
        async Task Reject(Func<Task> action) {
            var rejected = false; try { await action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unreviewed or wrong recovery rejected");
        }
        string Package(string version, bool extra = false, string id = "fixture.review", string sdk = "2.10.0") {
            var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".auralis-plugin");
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("platform.plugin.json").Open())) writer.Write(JsonSerializer.Serialize(new {
                schemaVersion=5,id,displayName="Original review fixture",version,minimumHostApiVersion=1,maximumHostApiVersion=1,
                entryAssembly="ReviewFixture.dll",entryType="Fixture.NeverLoaded",
                hostRequirements=new {minimumHostSdkVersion=sdk,requiredFeatures=new[]{"lyrics-lookup.v1","comment-artwork.v1","settings.v1","credential-aliases.v1","declarative-pages.v1"}},
                credentialAliases=extra ? new[]{new {key="session",scope="fixture.legacy",legacyKey="account"}} : [],
                providers=new[]{new {id="fixture.source",displayName="Original source",capabilities=extra ? new[]{"LyricsLookup","Pages"} : ["LyricsLookup"],
                    commentArtworkDomains=extra ? new[]{"assets.example.com"} : [],
                    settings=extra ? new[]{new{key="sort",label="Order",kind="choice",defaultValue="new",choices=new[]{new{value="new",label="Newest"}},legacyKeys=new[]{"fixture.sort"}}} : [],
                    pages=extra ? new[]{new{id="catalog",label="目录",labelEn="Catalogue",documentVersion=1}} : []}}
            }));
            using (var writer = new StreamWriter(zip.CreateEntry("ReviewFixture.dll").Open())) writer.Write("Original inert fixture; not executable.");
            return file;
        }
        var manager = new PlatformPluginManager(Path.Combine(root,"managed"), []);
        var first = Package("1.0.0"); var second = Package("2.0.0", true);
        var preview = await manager.PrepareImportAsync(first);
        Check(preview.Review is {Kind:"install",PreviousVersion:null,PreviousVerified:false},"First install is not an upgrade");
        Check(preview.Review!.Changes.Any(c=>c.Kind=="providers" && c.Added.Single()=="fixture.source"),"First declarations visible");
        await manager.ConfirmImportAsync(preview.Token,true);
        await manager.SetEnabledAsync(preview.Id,true);
        var original = await manager.FindManagedPluginAsync(preview.Id,default);
        var oldPayload = File.ReadAllBytes(original!.ManifestPath);
        var update = await manager.PrepareBatchAsync([second]);
        var review = update.Items.Single().Preview!.Review!;
        Check(review is {Kind:"update",PreviousVersion:"1.0.0",PreviousVerified:true,AccessReviewRequired:true},"Actual previous metadata and trust projected");
        Check(review.PreviousPayloadSha256?.Length==64 && review.HostSdkVersion=="2.10.0","Bound checksum and actual SDK");
        foreach(var kind in new[]{"capabilities","pages","settings","credentials","settingAliases","artworkDomains"})
            Check(review.Changes.Any(c=>c.Kind==kind && c.Added.Count>0),kind+" actual delta");
        var serialized=JsonSerializer.Serialize(review);
        Check(!serialized.Contains(root,StringComparison.OrdinalIgnoreCase) && !serialized.Contains("entryAssembly"),"No native path or executable details projected");
        // A changed selected preference invalidates the approval, even if the version number is identical.
        await manager.SetEnabledAsync(preview.Id,false);
        Check((await manager.ConfirmBatchAsync(update.Token,true)).Single().Error=="invalidPackage","Stale batch review cannot commit");
        Check((await manager.FindManagedPluginAsync(preview.Id,default))!.Version.ToString()=="1.0.0","Stale review preserves selection");
        update=await manager.PrepareBatchAsync([second]);
        await Reject(()=>manager.ConfirmBatchAsync(update.Token,false));
        Check((await manager.ConfirmBatchAsync(update.Token,true)).Single().Error is null,"Fresh explicitly approved update commits");
        Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Updated revision defaults off");
        Check(oldPayload.SequenceEqual(File.ReadAllBytes(original.ManifestPath)),"Prior immutable revision untouched");
        var before=File.ReadAllBytes(Path.Combine(root,"managed","platform-state.json"));
        await Reject(()=>manager.PrepareRecoveryAsync(first,"fixture.other"));
        await Reject(()=>manager.PrepareRecoveryAsync(second,preview.Id));
        await Reject(()=>manager.PrepareRecoveryAsync(Package("3.0.0"),preview.Id));
        await Reject(()=>manager.PrepareRecoveryAsync(first,"../outside"));
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"managed","platform-state.json"))),"Wrong recovery never changes selection");
        var recovery=await manager.PrepareRecoveryAsync(first,preview.Id);
        Check(recovery.Items.Single().Preview!.Review is {Kind:"recovery",PreviousVersion:"2.0.0",PreviousVerified:true,AccessReviewRequired:true},"Recovery is labelled explicitly");
        Check(recovery.Items.Single().Preview!.Review!.Changes.Any(c=>c.Kind=="pages"&&c.Removed.Count==1),"Removed entry shown during downgrade");
        await manager.CancelImportAsync(); await Reject(()=>manager.ConfirmBatchAsync(recovery.Token,true));
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"managed","platform-state.json"))),"Cancelled recovery preserves selection");
        recovery=await manager.PrepareRecoveryAsync(first,preview.Id);
        Check((await manager.ConfirmBatchAsync(recovery.Token,true)).Single().Error is null,"Recovery uses normal batch commit");
        Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Recovered revision defaults off");
        await manager.SetEnabledAsync(preview.Id,true);
        Check((await manager.FindManagedPluginAsync(preview.Id,default))!.Version.ToString()=="1.0.0","Explicit enable selects recovered version");
        var same=await manager.PrepareImportAsync(first);
        Check(same.Review!.Kind=="reinstall" && same.Review.Changes.Count==0,"Same revision does not invent differences");
        var current=await manager.FindManagedPluginAsync(preview.Id,default);
        await File.AppendAllTextAsync(current!.ManifestPath," ");
        await Reject(()=>manager.ConfirmImportAsync(same.Token,true));
        await manager.CancelImportAsync();
        // No payload assembly can be loaded: it is intentionally not a DLL.
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="ReviewFixture"),"Review and recovery remain metadata-only");
        Check(File.Exists(first)&&File.Exists(second),"Original archives retained");
        Console.WriteLine($"PASS {checks} update review checks: real manager, inert fixtures, declarations, stale approval, cancel, recovery identity and default OFF.");
    }
}
