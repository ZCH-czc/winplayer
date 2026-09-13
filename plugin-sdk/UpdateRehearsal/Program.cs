using System.Security.Cryptography;
using System.Text.Json;
using Auralis.Platform.Host;
using Auralis.Services;

// Metadata-only rehearsal of the actual frozen manager, not a player/credentials/plugin runtime.
if(args.Length!=2)return 2;
var inputFile=Path.GetFullPath(args[0]);var root=Path.GetFullPath(args[1]);
if(Directory.Exists(root)||File.Exists(root)||Path.GetDirectoryName(root)!=Path.GetDirectoryName(inputFile)||Path.GetFileName(root)!="rehearsal")return 2;
for(var dir=Path.GetDirectoryName(root);dir is not null;dir=Path.GetDirectoryName(dir))
    if(Directory.Exists(dir)&&(File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)return 2;
if(new FileInfo(inputFile).Length>1024*1024)return 2;
var input=JsonSerializer.Deserialize<RehearsalInput>(await File.ReadAllTextAsync(inputFile))!;
Directory.CreateDirectory(root);
var checks=0;var events=new List<object>();var passed=false;var loaded=-1;
var reviewProperty=typeof(PluginPackagePreview).GetProperty("Review");
var recoveryMethod=typeof(PlatformPluginManager).GetMethod("PrepareRecoveryAsync");
void Check(bool ok,string label){checks++;if(!ok)throw new InvalidOperationException(label);}
static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static Dictionary<string,string> Tree(string dir)=>Directory.GetFiles(dir,"*",SearchOption.AllDirectories)
    .ToDictionary(f=>Path.GetRelativePath(dir,f),Hash,StringComparer.Ordinal);
static bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(p=>b.GetValueOrDefault(p.Key)==p.Value);
static string Digest(Dictionary<string,string> files)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
    string.Join("\n",files.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}\t{p.Value}")))));
try{
    Check(input.Previous.Id==input.Candidate.Id&&Version.Parse(input.Previous.Version)<Version.Parse(input.Candidate.Version),"Revision identity");
    Check(Hash(input.Previous.Archive)==input.Previous.ArchiveSha256&&Hash(input.Candidate.Archive)==input.Candidate.ArchiveSha256,"Exact input archives");
    var managerRoot=Path.Combine(root,"isolated-manager");var stateRoot=Path.Combine(root,"synthetic-state");Directory.CreateDirectory(stateRoot);
    var savedFile=Path.Combine(stateRoot,"saved-playlists.json");var saved=new SavedPlaylistStore(savedFile);
    await saved.ChangeAsync("create",name:"Original rehearsal playlist");var playlist=(await saved.LoadAsync()).Single();
    await saved.ChangeAsync("add",playlist.Id,entry:new("local-fixture", "local-track-1",null,null,"Original local track","Fixture","",120,null));
    var index=0;
    foreach(var provider in input.ProviderIds){
        await saved.ChangeAsync("add",playlist.Id,entry:new($"online-{index++}",null,provider,"fixture:track-one","Original saved reference","Fixture","",125,"fixture:video-one"));
    }
    await saved.ChangeAsync("add",playlist.Id,entry:new("missing-fixture",null,"fixture.missing","fixture:absent","Missing plugin reference","Fixture","",90,null));
    await File.WriteAllTextAsync(Path.Combine(stateRoot,"library.json"),"[{\"id\":\"local-track-1\",\"title\":\"Original local track\"}]");
    await File.WriteAllTextAsync(Path.Combine(stateRoot,"preferences.json"),"{\"theme\":\"dark\",\"synthetic\":true}");
    var originalState=Tree(stateRoot);var originalEntries=(await saved.LoadAsync()).Single().Entries.ToArray();
    Check(originalEntries.Length==input.ProviderIds.Length+2,"Mixed local/online/missing references created");
    var manager=new PlatformPluginManager(managerRoot,[]);
    async Task AssertState(string phase){
        Check(Same(originalState,Tree(stateRoot)),phase+" state bytes retained");
        Check((await new SavedPlaylistStore(savedFile).LoadAsync()).Single().Entries.SequenceEqual(originalEntries),phase+" stable entity references");
    }
    async Task<PlatformPluginManifest> Current(string version){
        var plan=await new PlatformPluginManager(managerRoot,[]).CreateSessionPlanAsync();
        Check(plan.Roots.Count==1&&plan.EnabledIds.Contains(input.Previous.Id),"Only explicitly selected plugin");
        var manifest=(await new PlatformPluginCatalog(plan.Roots).DiscoverAsync()).Plugins.Single();
        Check(manifest.Id==input.Previous.Id&&manifest.Version.ToString()==version,"Selected revision exact");
        Check(await PlatformPluginIntegrity.VerifyAsync(manifest,default),"Selected payload approved and intact");
        return manifest;
    }
    async Task<PluginPackagePreview> Prepare(Revision revision){
        var preview=await manager.PrepareImportAsync(revision.Archive);
        Check(preview.Id==revision.Id&&preview.Version==revision.Version&&preview.Sha256==revision.ArchiveSha256,"Preview bound to archive");
        return preview;
    }
    async Task Confirm(PluginPackagePreview preview){await manager.ConfirmImportAsync(preview.Token,true);}
    async Task Record(string phase){
        var manifest=await manager.FindManagedPluginAsync(input.Previous.Id,default);
        var plan=await new PlatformPluginManager(managerRoot,[]).CreateSessionPlanAsync();
        events.Add(new{Phase=phase,Version=manifest?.Version.ToString(),Enabled=plan.EnabledIds.Contains(input.Previous.Id),
            SelectedRoots=plan.Roots.Count,StateSha256=Digest(Tree(stateRoot))});await AssertState(phase);
    }
    async Task Reject(Func<Task> action,string label){var refused=false;try{await action();}catch(InvalidDataException){refused=true;}Check(refused,label);}
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Empty installation has no fabricated plugin");
    var oldPreview=await Prepare(input.Previous);
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Preview does not select plugin");
    await Reject(()=>manager.ConfirmImportAsync(oldPreview.Token,false),"Unapproved import rejected");
    await Confirm(oldPreview);
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Old import defaults off");
    await manager.SetEnabledAsync(input.Previous.Id,true);var oldManifest=await Current(input.Previous.Version);
    var oldBytes=Tree(oldManifest.PluginDirectory);var oldPlan=await manager.CreateSessionPlanAsync();
    await Record("old-enabled");
    var registry=Path.Combine(managerRoot,"platform-state.json");var oldRegistry=Hash(registry);
    var cancelPreview=await Prepare(input.Candidate);await manager.CancelImportAsync();
    if(reviewProperty is not null){
        var review=JsonSerializer.SerializeToElement(reviewProperty.GetValue(cancelPreview));
        Check(review.GetProperty("Kind").GetString()=="update","Published manager identifies real package upgrade");
        Check(review.GetProperty("PreviousVersion").GetString()==input.Previous.Version&&review.GetProperty("PreviousVerified").GetBoolean(),"Review binds approved previous revision");
        await File.WriteAllTextAsync(Path.Combine(root,"update-review.json"),JsonSerializer.Serialize(cancelPreview,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
    }
    Check(Hash(registry)==oldRegistry,"Cancelled candidate leaves selection unchanged");
    await Reject(()=>manager.ConfirmImportAsync(cancelPreview.Token,true),"Cancelled preview cannot replay");
    var changedPreview=await Prepare(input.Candidate);
    var staged=Directory.GetDirectories(Path.Combine(managerRoot,"Packages"))
        .Select(p=>Path.Combine(p,input.Candidate.Id)).Single(p=>Directory.Exists(p)&&p!=oldManifest.PluginDirectory);
    Check(Path.GetFullPath(staged).StartsWith(managerRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Owned rehearsal staging only");
    await File.AppendAllTextAsync(Path.Combine(staged,"platform.plugin.json")," ");
    await Reject(()=>manager.ConfirmImportAsync(changedPreview.Token,true),"Changed preview rejected before selection");
    Check(Hash(registry)==oldRegistry,"Rejected candidate preserves old registry");await manager.CancelImportAsync();
    await Record("candidate-rejected-old-retained");
    using(var cancel=new CancellationTokenSource()){
        cancel.Cancel();var cancelled=false;try{await manager.PrepareImportAsync(input.Candidate.Archive,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled&&Hash(registry)==oldRegistry,"Pre-cancelled import is inert");
    }
    var newPreview=await Prepare(input.Candidate);await Confirm(newPreview);
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Update never inherits enablement");
    Check(Same(oldBytes,Tree(oldManifest.PluginDirectory))&&oldPlan.Roots.Single()==oldManifest.PluginDirectory,"Existing session plan and old payload preserved");
    await Record("candidate-imported-disabled");
    await manager.SetEnabledAsync(input.Candidate.Id,true);var newManifest=await Current(input.Candidate.Version);
    var newBytes=Tree(newManifest.PluginDirectory);
    Check(newManifest.PluginDirectory!=oldManifest.PluginDirectory,"Candidate is separate immutable revision");
    await Record("candidate-enabled-new-session");
    // Simulated rejection after candidate selection. No provider method is invoked; no fake runtime success.
    events.Add(new{Phase="synthetic-candidate-failure",Kind="injected-marker-not-business-execution"});
    if(recoveryMethod is not null){
        async Task<PluginImportBatchPreview> Recovery()=>await (Task<PluginImportBatchPreview>)recoveryMethod.Invoke(manager,[input.Previous.Archive,input.Previous.Id,CancellationToken.None])!;
        var recovery=await Recovery();var item=recovery.Items.Single().Preview!;
        var review=JsonSerializer.SerializeToElement(reviewProperty!.GetValue(item));
        Check(review.GetProperty("Kind").GetString()=="recovery"&&review.GetProperty("PreviousVersion").GetString()==input.Candidate.Version,"Published recovery API binds real old/new pair");
        Check(item.Sha256==input.Previous.ArchiveSha256,"Recovery archive checksum exact");
        await File.WriteAllTextAsync(Path.Combine(root,"recovery-review.json"),JsonSerializer.Serialize(recovery,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        await Reject(()=>manager.ConfirmBatchAsync(recovery.Token,false),"Recovery batch needs explicit trust");
        await manager.CancelImportAsync();
        Check((await Current(input.Candidate.Version)).PluginDirectory==newManifest.PluginDirectory,"Cancelled recovery retains candidate");
        recovery=await Recovery();var results=await manager.ConfirmBatchAsync(recovery.Token,true);
        Check(results.Count==1&&results[0].Id==input.Previous.Id&&results[0].Error is null,"Recovery uses production batch commit");
    }else{
        var rollbackPreview=await Prepare(input.Previous);await Confirm(rollbackPreview);
    }
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Rollback reimport also defaults off");
    await Record("rollback-imported-disabled");
    await manager.SetEnabledAsync(input.Previous.Id,true);var restored=await Current(input.Previous.Version);
    Check(Same(oldBytes,Tree(restored.PluginDirectory)),"Rollback restores exact old payload");
    Check(Same(oldBytes,Tree(oldManifest.PluginDirectory))&&Same(newBytes,Tree(newManifest.PluginDirectory)),"Both prior immutable revisions remain intact");
    await Record("rollback-enabled-new-session");
    await manager.SetEnabledAsync(input.Previous.Id,false);
    Check((await manager.CreateSessionPlanAsync()).Roots.Count==0,"Disabled plugin has no selected root");await AssertState("disabled");
    await manager.SetEnabledAsync(input.Previous.Id,true);await Current(input.Previous.Version);await AssertState("restored");
    loaded=AppDomain.CurrentDomain.GetAssemblies().Count(a=>input.EntryAssemblies.Contains(a.GetName().Name,StringComparer.OrdinalIgnoreCase));
    Check(loaded==0,"No provider assembly loaded");
    Check(Hash(input.Previous.Archive)==input.Previous.ArchiveSha256&&Hash(input.Candidate.Archive)==input.Candidate.ArchiveSha256,"Archives unchanged after transitions");
    passed=true;
}catch{
    // Do not leak exception text, paths or manifest-controlled content into errors.
    Console.Error.WriteLine("Rehearsal assertion failed.");
}finally{
    await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{
        Passed=passed,Checks=checks,Previous=input.Previous,Candidate=input.Candidate,ProviderAssembliesLoaded=loaded,
        PersonalStateAccessed=false,RealPlatformTested=false,NativeAppTested=false,
        ManagementReviewTested=reviewProperty is not null,ExplicitRecoveryApiTested=recoveryMethod is not null,
        Scope="frozen-manager-import-reimport-and-synthetic-reference-preservation",Events=events},new JsonSerializerOptions{WriteIndented=true}));
}
Console.WriteLine($"Rehearsal: {(passed?"passed":"failed")} ({checks} checks)");return passed?0:1;
sealed record Revision(string Id,string Version,string ArchiveSha256,string PayloadSha256,string Archive);
sealed record RehearsalInput(Revision Previous,Revision Candidate,string[] ProviderIds,string[] EntryAssemblies);
