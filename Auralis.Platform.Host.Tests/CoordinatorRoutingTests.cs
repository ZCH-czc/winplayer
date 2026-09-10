using System.Security.Cryptography;
using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class CoordinatorRoutingTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var plugin = Path.Combine(root, "tests.routing");
        Directory.CreateDirectory(plugin);
        File.Copy(typeof(RoutingPlugin).Assembly.Location, Path.Combine(plugin, "Fixture.dll"));
        var providers = new[] { "custom.public", "custom.account", "custom.configured", "custom.authonly" }.Select(id => new
        {
            id, displayName = "Declared " + id, commentArtworkDomains = Array.Empty<string>(),
            capabilities = RoutingProvider.Capabilities(id).Select(c => c.ToString()).ToArray(),
            settings = id == "custom.account"
                ? new[] { new PlatformSettingManifest { Key="mode",Label="Mode",Kind="choice",DefaultValue="signedin",Choices=[new("signedin","In"),new("signedout","Out"),new("failed","Failure")] } }
                : id == "custom.configured" ? new[] {new PlatformSettingManifest {Key="endpoint",Label="Endpoint",Kind="endpoint",Required=true}}
                : id == "custom.public" ? new[] {new PlatformSettingManifest {Key="payload",Label="Payload",Kind="choice",DefaultValue="normal",
                    Choices=[new("normal","Normal"),new("foreign","Foreign"),new("video","Foreign video"),new("default","Default ID"),new("null","Null row")]}} : []
        });
        await File.WriteAllTextAsync(Path.Combine(plugin, "platform.plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion=5,id="tests.routing",displayName="Routing",version="1.0.0", minimumHostApiVersion=1,maximumHostApiVersion=1,
            hostRequirements = new PlatformHostRequirements { MinimumHostSdkVersion = PlatformHostCompatibility.SdkVersion, RequiredFeatures = ["comment-artwork.v1", "settings.v1", "track-details.v1"] },
            entryAssembly="Fixture.dll",entryType="Auralis.Platform.Host.Tests.RoutingPlugin", providers
        }));
        var hashes = Directory.GetFiles(plugin).ToDictionary(f => Path.GetFileName(f)!, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        Directory.CreateDirectory(Path.Combine(root,".approvals"));
        await File.WriteAllTextAsync(Path.Combine(root,".approvals","tests.routing.json"), JsonSerializer.Serialize(new PluginInstallReceipt(1,"tests.routing",hashes)));
        var settings = new PlatformSettingsStore(Path.Combine(root,"preferences.json"));
        await using var backend = new PlatformBackendService([root], NullAppLogger.Instance, settings:settings);
        var coordinator = new OnlinePlatformCoordinator(backend);
        var snapshot = await backend.DiscoverAsync();
        Check(snapshot.Providers.Count == 4, "All synthetic providers discovered");
        Check((await coordinator.SearchAsync("", "query", 20, null, default)).Error?.Code == PlatformErrorCode.InvalidRequest,
            "Missing provider is rejected instead of silently choosing a built-in platform");
        var result = await coordinator.SearchAsync("custom.public", "query", 20, null, default);
        Check(result.IsSuccess && result.Value.Items.Single().SourceName == "Declared custom.public", "Arbitrary provider searches via real Native coordinator and manifest display name");
        Check(result.Value.NextPageHandle is not null && result.Value.NextPageHandle != "private-cursor", "Opaque pagination");
        var page = await coordinator.SearchAsync("custom.public", "query", 20, result.Value.NextPageHandle, default);
        Check(page.IsSuccess && page.Value.Items.Single().Title == "Second", "Unknown provider continuation routes");
        Check(!(await coordinator.SearchAsync("custom.account", "query", 20, result.Value.NextPageHandle, default)).IsSuccess, "Pagination cannot cross providers");
        var lease = await coordinator.AcquireStreamAsync(result.Value.Items.Single().Handle, default);
        Check(lease.IsSuccess && lease.Value.Quality.Id == "fixture", "Arbitrary provider track routes to stream capability");
        var originalTrack = coordinator.GetBackendTrack(result.Value.Items.Single().Handle)!;
        var savedStore = new SavedPlaylistStore(Path.Combine(root,"saved-playlists.json"));
        await savedStore.ChangeAsync("create",name:"Plugin-independent references");
        var savedList = (await savedStore.LoadAsync()).Single().Id;
        var savedEntry = new SavedPlaylistEntry("generic",null,originalTrack.Id.ProviderId,originalTrack.Id.Value,
            originalTrack.Title,"Artist","Album",90,originalTrack.MusicVideo!.Id.Value);
        await savedStore.ChangeAsync("add",savedList,entry:savedEntry);
        var savedBytes = await File.ReadAllBytesAsync(Path.Combine(root,"saved-playlists.json"));
        var sameSession = coordinator.RestoreSavedTrack(savedEntry);
        Check(sameSession.CoverUrl is not null && coordinator.GetBackendTrack(sameSession.Handle)?.ArtworkUrl == originalTrack.ArtworkUrl,
            "Saving arbitrary provider retains full existing cover metadata");
        await using (var freshBackend = new PlatformBackendService([root],NullAppLogger.Instance,
            settings:new PlatformSettingsStore(Path.Combine(root,"preferences.json"))))
        {
            var freshCoordinator = new OnlinePlatformCoordinator(freshBackend);
            var reloaded = (await new SavedPlaylistStore(Path.Combine(root,"saved-playlists.json")).LoadAsync()).Single().Entries.Single();
            var refreshed = await freshCoordinator.RefreshSavedTrackAsync(reloaded,default);
            var freshTrack = freshCoordinator.GetBackendTrack(refreshed.Handle)!;
            Check(refreshed.CoverUrl is not null && freshTrack.Id == originalTrack.Id && freshTrack.MusicVideo?.Id == originalTrack.MusicVideo.Id,
                "Fresh coordinator restores original arbitrary-provider cover/video identity through exact-ID fallback");
            Check((await freshCoordinator.AcquireStreamAsync(refreshed.Handle,default)).IsSuccess,
                "Saved arbitrary provider plays through actual host routing after restart");
        }
        Check(!JsonSerializer.Serialize(result.Value).Contains("private-cursor") && !JsonSerializer.Serialize(result.Value).Contains("media.example.test"), "No cursor or media URL in UI DTO");
        var publicProvider = snapshot.Providers.Single(p => p.Provider.Id == "custom.public").Provider;
        var state = await coordinator.ReadProviderCollectionsAsync(publicProvider, true, default);
        Check(state.Error is null && state.Authentication is null && state.Playlists.Count == 1, "Public playlist projection does not demand login");
        var handle = state.Playlists.Single().Handle;
        var detail = await coordinator.GetPlaylistAsync("custom.public", handle, default);
        Check(detail.IsSuccess && detail.Value.Tracks.Single().ProviderId == "custom.public", "Public details bypass absent authentication only");
        Check((await coordinator.GetPlaylistAsync("custom.account", handle, default)).Error?.Code == PlatformErrorCode.InvalidRequest, "Playlist handle ownership checked");
        var accountProvider = snapshot.Providers.Single(p => p.Provider.Id == "custom.account").Provider;
        var account = await coordinator.ReadProviderCollectionsAsync(accountProvider,true,default);
        Check(account.Authentication?.Status == PlatformAuthenticationStatus.SignedIn && account.Playlists.Count == 1, "Account collections still authenticate");
        var authOnly = await coordinator.ReadProviderCollectionsAsync(snapshot.Providers.Single(p => p.Provider.Id == "custom.authonly").Provider,true,default);
        Check(authOnly.Authentication?.Status == PlatformAuthenticationStatus.SignedIn && authOnly.Error is null && authOnly.Playlists.Count == 0,"Authentication does not invent a playlist capability");
        var accountHandle = account.Playlists.Single().Handle;
        Check((await coordinator.GetPlaylistAsync("custom.account",accountHandle,default)).IsSuccess,"Authenticated detail initially works");
        await settings.SetScopedAsync("tests.routing","mode","signedout",default);
        Check((await coordinator.GetPlaylistAsync("custom.account",accountHandle,default)).Error?.Code == PlatformErrorCode.AuthenticationRequired,"Cached detail does not bypass expired account");
        Check((await coordinator.ReadProviderCollectionsAsync(accountProvider,true,default)).Playlists.Count == 0,"Signed-out account hides collections");
        await settings.SetScopedAsync("tests.routing","mode","failed",default);
        Check((await coordinator.ReadProviderCollectionsAsync(accountProvider,true,default)).Error?.Code == PlatformErrorCode.NetworkUnavailable,"Account errors are retained");
        Check((await coordinator.SearchAsync("custom.configured","query",20,null,default)).Error?.Code == PlatformErrorCode.ConfigurationRequired,"Native enforces required settings before search");
        await settings.SetScopedAsync("tests.routing","endpoint","https://example.test",default);
        Check((await coordinator.SearchAsync("custom.configured","query",20,null,default)).IsSuccess,"Configured plugin can search");
        Check((await coordinator.GetPlaylistsAsync("custom.configured",default)).Error?.Code == PlatformErrorCode.Unsupported,"Search does not invent a playlist capability");
        Check((await coordinator.SearchAsync("not.installed","query",20,null,default)).Error?.Code == PlatformErrorCode.Unsupported,"Unknown plugin remains unavailable");
        Check((await coordinator.ReadProviderCollectionsAsync(publicProvider,false,default)).Playlists.Count == 0,"Unconfigured projection is inert");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await coordinator.SearchAsync("custom.public","query",20,null,cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        await using var empty = new PlatformBackendService([],NullAppLogger.Instance,settings:new PlatformSettingsStore(Path.Combine(root,"empty.json")));
        Check((await empty.DiscoverAsync()).Providers.Count == 0 && !(await new OnlinePlatformCoordinator(empty).SearchAsync("custom.public","query",20,null,default)).IsSuccess,"Core-only without plugins");
        var missingCoordinator = new OnlinePlatformCoordinator(empty);
        var missingView = await missingCoordinator.RefreshSavedTrackAsync(savedEntry,default);
        Check(missingView.Title == savedEntry.Title && !(await missingCoordinator.AcquireStreamAsync(missingView.Handle,default)).IsSuccess,
            "Missing plugin preserves saved title but cannot obtain a playback lease");
        Check((await File.ReadAllBytesAsync(Path.Combine(root,"saved-playlists.json"))).SequenceEqual(savedBytes),
            "Missing provider does not rewrite or delete saved references");
        var other = Path.Combine(root,"tests.approved"); Directory.CreateDirectory(other);
        File.Copy(typeof(FixturePlugin).Assembly.Location,Path.Combine(other,"Fixture.dll"));
        await File.WriteAllTextAsync(Path.Combine(other,"platform.plugin.json"), """
            {"schemaVersion":5,"id":"tests.approved","displayName":"Only lyrics","version":"1.0.0","minimumHostApiVersion":1,"maximumHostApiVersion":1,
             "hostRequirements":{"minimumHostSdkVersion":"1.2.0","requiredFeatures":["comment-artwork.v1","lyrics-lookup.v1"]},
             "entryAssembly":"Fixture.dll","entryType":"Auralis.Platform.Host.Tests.FixturePlugin","providers":[{"id":"fixture","displayName":"Fixture","commentArtworkDomains":[],"capabilities":["LyricsLookup"]}]}
            """);
        var otherHashes=Directory.GetFiles(other).ToDictionary(f=>Path.GetFileName(f)!,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        await File.WriteAllTextAsync(Path.Combine(root,".approvals","tests.approved.json"),JsonSerializer.Serialize(new PluginInstallReceipt(1,"tests.approved",otherHashes)));
        var manager=new PlatformPluginManager(Path.Combine(root,"managed-state"),[root]);
        await manager.SetEnabledAsync("tests.approved",true);
        await using var partial=new PlatformBackendService([root],NullAppLogger.Instance,manager,settings);
        Check((await partial.DiscoverAsync()).Providers.Select(p=>p.Provider.Id).SequenceEqual(["fixture"]),"Only explicitly enabled plugin appears in session");
        Check(!(await new OnlinePlatformCoordinator(partial).SearchAsync("custom.public","query",20,null,default)).IsSuccess,"Installed but disabled search plugin cannot route");
        var disabledCoordinator = new OnlinePlatformCoordinator(partial);
        var disabledSaved = disabledCoordinator.RestoreSavedTrack(savedEntry);
        Check(!(await disabledCoordinator.AcquireStreamAsync(disabledSaved.Handle,default)).IsSuccess,
            "Stored reference cannot bypass a disabled provider");
        await manager.SetEnabledAsync("tests.routing",true);
        Check((await partial.DiscoverAsync()).Providers.Count==1,"Enabling does not hot-load into current session");
        await using var restarted=new PlatformBackendService([root],NullAppLogger.Instance,manager,settings);
        Check((await new OnlinePlatformCoordinator(restarted).SearchAsync("custom.public","query",20,null,default)).IsSuccess,"Explicit enable takes effect after restart");
        var enabledCoordinator = new OnlinePlatformCoordinator(restarted);
        var enabledSaved = await enabledCoordinator.RefreshSavedTrackAsync(savedEntry,default);
        Check(enabledSaved.CoverUrl is not null && (await enabledCoordinator.AcquireStreamAsync(enabledSaved.Handle,default)).IsSuccess,
            "Re-enabled provider restores the same saved track without re-adding it");
        // Test all projection paths through a real loaded DLL, not just a standalone validator.
        // A bad row follows a valid row: reject the entire response before issuing any UI handles.
        foreach (var mode in new[] { "foreign", "video", "default", "null" })
        {
            await settings.SetScopedAsync("tests.routing","payload",mode,default);
            Check((await coordinator.SearchAsync("custom.public","query",20,null,default)).Error?.Code == PlatformErrorCode.InvalidResponse,
                $"Search rejects {mode} identity before projection");
            Check((await coordinator.GetPlaylistAsync("custom.public",handle,default)).Error?.Code == PlatformErrorCode.InvalidResponse,
                $"Playlist tracks reject {mode} identity without serving cached detail");
            Check((await coordinator.GetPartsAsync(result.Value.Items.Single().Handle,default)).Error?.Code == PlatformErrorCode.InvalidResponse,
                $"Parts reject {mode} identity before registering a playback handle");
            if (mode != "video")
                Check((await coordinator.GetPlaylistsAsync("custom.public",default)).Error?.Code == PlatformErrorCode.InvalidResponse,
                    $"Playlist listing rejects {mode} identity");
        }
        await settings.SetScopedAsync("tests.routing","payload","video",default);
        await using (var restoreBackend = new PlatformBackendService([root],NullAppLogger.Instance,
            settings:new PlatformSettingsStore(Path.Combine(root,"preferences.json"))))
        {
            var restore = new OnlinePlatformCoordinator(restoreBackend);
            var invalidMetadata = await restore.RefreshSavedTrackAsync(savedEntry,default);
            Check(invalidMetadata.CoverUrl is null && restore.GetBackendTrack(invalidMetadata.Handle)!.MusicVideo!.Id == originalTrack.MusicVideo!.Id,
                "Saved exact-ID search fallback cannot replace metadata with a foreign video reference");
            var directEntry=savedEntry with {Id="direct-details",ProviderId="custom.configured"};
            var direct=await restore.RefreshSavedTrackAsync(directEntry,default);
            Check(direct.CoverUrl is null && restore.GetBackendTrack(direct.Handle)!.MusicVideo!.Id.IsForProvider("custom.configured"),
                "Saved TrackDetails cannot replace the original video identity with a foreign reference");
        }
        await settings.SetScopedAsync("tests.routing","payload","normal",default);
        Check((await coordinator.SearchAsync("CUSTOM.PUBLIC","query",20,null,default)).IsSuccess,
            "Valid provider identity remains case insensitive");
        Check((await coordinator.GetPlaylistAsync("custom.public",handle,default)).IsSuccess &&
            (await coordinator.GetPartsAsync(result.Value.Items.Single().Handle,default)).IsSuccess,
            "Valid data still works after invalid responses without restarting plugins");
        Check((await File.ReadAllBytesAsync(Path.Combine(root,"saved-playlists.json"))).SequenceEqual(savedBytes),
            "Rejected metadata never rewrites saved references");
        Console.WriteLine("PASS coordinator result ownership: 20 checks; search, playlists, parts, nested video, saved hydration, null/default IDs and recovery.");
        Console.WriteLine("PASS Native coordinator: arbitrary IDs, manifest names, pagination, stream routing, public/account collections, cache auth, required settings, cancellation, missing plugins; no HTTP or real credentials.");
    }
}

// Loaded through the actual host/collectible DLL path. Never reads credentials or creates HTTP clients.
public sealed class RoutingPlugin : IAuralisPlatformPlugin
{
    public PlatformPluginDescriptor Descriptor {get;} = new("tests.routing","Routing",new Version(1,0,0),1,1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context,CancellationToken token) =>
        ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new RoutingProvider("custom.public",context),new RoutingProvider("custom.account",context),new RoutingProvider("custom.configured",context),new RoutingProvider("custom.authonly",context)]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class RoutingProvider(string id, PlatformHostContext context) : IPlatformProvider, ITrackSearchCapability, IPlaylistBrowseCapability, IPlaylistDetailsCapability, IAuthenticationCapability, IStreamResolutionCapability, IPlatformMediaExtrasCapability, ITrackDetailsCapability
{
    public static PlatformCapabilityKind[] Capabilities(string id) => id == "custom.configured" ? [PlatformCapabilityKind.TrackSearch,PlatformCapabilityKind.TrackDetails]
        : id == "custom.authonly" ? [PlatformCapabilityKind.Authentication] : id == "custom.account"
        ? [PlatformCapabilityKind.TrackSearch,PlatformCapabilityKind.PlaylistBrowse,PlatformCapabilityKind.PlaylistDetails,PlatformCapabilityKind.StreamResolution,PlatformCapabilityKind.Authentication]
        : [PlatformCapabilityKind.TrackSearch,PlatformCapabilityKind.PlaylistBrowse,PlatformCapabilityKind.PlaylistDetails,PlatformCapabilityKind.StreamResolution,PlatformCapabilityKind.MediaExtras];
    public PlatformProviderDescriptor Descriptor {get;} = new(id,"Declared " + id,new Version(1,0,0),Capabilities(id));
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token) => ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private PlatformTrack Track(string title) => new() {Id=new(id,"track"),Title=title,Duration=TimeSpan.FromSeconds(90),
        ArtworkUrl=new Uri("https://images.example.test/fixture.png"),MusicVideo=new(new(id,"video:part2"),title)};
    private PlatformPlaylist Playlist() => new() {Id=new(id,"collection"),Title="Collection"};
    private async Task<IReadOnlyList<PlatformTrack>> Tracks(string title,CancellationToken token)
    {
        var track=Track(title);
        return await context.Settings.GetAsync("payload",token) switch
        {
            "foreign" => [track,track with {Id=new("other.source","track")}],
            "video" => [track with {MusicVideo=new(new("other.source","video"),"Video")}],
            "default" => [track,track with {Id=default}],
            "null" => [track,null!],
            _ => [track]
        };
    }
    public async Task<PlatformResult<PlatformPage<PlatformTrack>>> SearchTracksAsync(PlatformSearchRequest request,CancellationToken token) =>
        PlatformResult<PlatformPage<PlatformTrack>>.Success(new(await Tracks(request.Page.Cursor is null ? "First" : "Second",token),request.Page.Cursor is null ? "private-cursor" : null,2));
    public async Task<PlatformResult<PlatformPage<PlatformPlaylist>>> BrowsePlaylistsAsync(PlatformPageRequest page,CancellationToken token)
    {
        var playlist=Playlist();
        IReadOnlyList<PlatformPlaylist> rows=await context.Settings.GetAsync("payload",token) switch
        {
            "foreign" => [playlist,playlist with {Id=new("other.source","collection")}],
            "default" => [playlist,playlist with {Id=default}],
            "null" => [playlist,null!],
            _ => [playlist]
        };
        return PlatformResult<PlatformPage<PlatformPlaylist>>.Success(new(rows));
    }
    public Task<PlatformResult<PlatformPlaylist>> GetPlaylistAsync(PlatformEntityId entity,CancellationToken token) => Task.FromResult(PlatformResult<PlatformPlaylist>.Success(Playlist()));
    public async Task<PlatformResult<PlatformPage<PlatformTrack>>> GetPlaylistTracksAsync(PlatformEntityId entity,PlatformPageRequest page,CancellationToken token) => PlatformResult<PlatformPage<PlatformTrack>>.Success(new(await Tracks("Collection song",token)));
    public async Task<PlatformResult<IReadOnlyList<PlatformTrack>>> GetPartsAsync(PlatformEntityId entity,CancellationToken token) => PlatformResult<IReadOnlyList<PlatformTrack>>.Success(await Tracks("Part",token));
    public async Task<PlatformResult<PlatformTrack>> GetTrackAsync(PlatformEntityId entity,string? titleHint,CancellationToken token) => PlatformResult<PlatformTrack>.Success((await Tracks("Details",token))[0]);
    public Task<PlatformResult<IReadOnlyList<PlatformTimedComment>>> GetDanmakuAsync(PlatformEntityId entity,CancellationToken token) => Task.FromResult(PlatformResult<IReadOnlyList<PlatformTimedComment>>.Success([]));
    public async Task<PlatformResult<PlatformAuthenticationState>> GetAuthenticationStateAsync(CancellationToken token)
    {
        if(id is not "custom.account" and not "custom.authonly") throw new InvalidOperationException("Public provider must not authenticate");
        var mode=await context.Settings.GetAsync("mode",token);
        return mode == "failed" ? PlatformResult<PlatformAuthenticationState>.Failure(PlatformErrorCode.NetworkUnavailable,"Fixture failure")
            : PlatformResult<PlatformAuthenticationState>.Success(new() {Status=mode == "signedout" ? PlatformAuthenticationStatus.SignedOut : PlatformAuthenticationStatus.SignedIn});
    }
    public Task<PlatformResult<PlatformAuthenticationChallenge>> BeginAuthenticationAsync(PlatformAuthenticationRequest request,CancellationToken token) => throw new NotSupportedException();
    public Task<PlatformResult<PlatformAuthenticationState>> CompleteAuthenticationAsync(PlatformAuthenticationCompletion completion,CancellationToken token) => throw new NotSupportedException();
    public Task<PlatformResult<PlatformUnit>> SignOutAsync(CancellationToken token) => Task.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(PlatformPlaybackRequest request,CancellationToken token) => Task.FromResult(PlatformResult<PlatformStreamLease>.Success(new(new Uri("https://media.example.test/audio"),DateTimeOffset.UtcNow.AddMinutes(1),"audio/mpeg",new("fixture","Fixture"))));
    public Task<PlatformResult<IReadOnlyList<PlatformAudioQuality>>> GetAvailableQualitiesAsync(PlatformEntityId entity,CancellationToken token) => Task.FromResult(PlatformResult<IReadOnlyList<PlatformAudioQuality>>.Success([new("fixture","Fixture")]));
}
