using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageMediaTests
{
    internal static async Task RunAsync(OnlinePlatformCoordinator coordinator, PlatformBackendService backend)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var fixture = System.Runtime.Loader.AssemblyLoadContext.All.Where(c => c.Name?.StartsWith("Auralis.Platform:tests.routing:") == true)
            .SelectMany(c => c.Assemblies).Select(a => a.GetType(typeof(RoutingProvider).FullName!)).OfType<Type>().Single();
        System.Reflection.FieldInfo Field(string name) => fixture.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        void Set(string name, object? value) => Field(name).SetValue(null, value);
        T Get<T>(string name) => (T)Field(name).GetValue(null)!;
        var media = new PlatformTrack { Id = new("custom.public", "backend-media"), Title = "Original work",
            Artists = [new(new("custom.public","backend-author"),"Original author")],
            ArtworkUrl = new("https://art.example.test/cover"), Duration = TimeSpan.FromSeconds(204),
            Availability = PlatformTrackAvailability.Available,
            MusicVideo = new(new("custom.public","backend-video"),"Original video") };
        var doc = new PlatformPageDocument { Version = 6, Title = "Media page", Cards = [new() { Id = "card", Media = media }] };
        Check(PlatformPageValidation.IsValid(doc),"V6 normalized media accepted");
        Check(!OnlinePlatformCoordinator.AcceptsPageMedia("custom.public",[PlatformCapabilityKind.Pages],media),"Read-only provider cannot publish playable media");
        for (var v = 1; v < 6; v++) Check(!PlatformPageValidation.IsValid(doc with { Version = v }),"Old docs cannot carry media");
        foreach (var invalid in new[] {
            media with { Id = default }, media with { Title = new string('x',513) },
            media with { Id = new("custom.public","bad\nidentity") },
            media with { Duration = TimeSpan.FromTicks(-1) }, media with { Duration = TimeSpan.FromDays(8) },
            media with { Availability = (PlatformTrackAvailability)99 }, media with { Artists = null! },
            media with { Artists = [new(new("foreign","author"),"Bad")] },
            media with { Album = new(new("foreign","album"),"Bad") },
            media with { MusicVideo = new(new("foreign","video"),"Bad") },
            media with { ArtworkUrl = new("file:///private") }, media with { ArtworkUrl = new("https://user:pass@example.test/") },
            media with { ViewCount = -1 }
        }) Check(!PlatformPageValidation.IsValid(doc with { Cards = [new() { Id = "bad", Media = invalid }] }),"Invalid media denied");
        Task<PlatformResult<OnlinePageView>> Read() => coordinator.ReadPluginGlobalPageAsync("custom.public","query",null,"en-US",default,forceRefresh:true);
        try
        {
            var before = Get<int>("MediaLeaseCalls");
            Set("TargetDocument", doc);
            var first = await Read(); Check(first.IsSuccess,"Media page routes through actual plugin");
            var view = first.Value.Cards.Single().Media!;
            Check(view is { Kind: "online", IsPlayable: true, HasMusicVideo: true } && view.Handle.StartsWith("track-"),"Host-owned track view");
            Check(Get<int>("MediaLeaseCalls") == before,"Browsing performs zero lease requests");
            Check(!JsonSerializer.Serialize(first.Value).Contains("backend-") && !JsonSerializer.Serialize(first.Value).Contains("art.example.test"),"IDs/remote artwork stay native");
            Check(coordinator.TryGetArtworkUri(view.Handle,out var art,out var policy) && art == media.ArtworkUrl &&
                policy!(new("https://art.example.test/redirect")) && !policy(new("https://evil.test/redirect")),"Artwork redirects obey page manifest");
            Check((await Read()).Value.Cards.Single().Media!.Handle == view.Handle,"Repeated page media has stable queue identity");
            Check(coordinator.GetBackendTrack(view.Handle)?.Artists.Single().Id == media.Artists.Single().Id,"Backend metadata preserves artist context");
            var context = await coordinator.CaptureMediaContextAsync(view.Handle,default);
            Check(context.IsCurrent() && context.CacheKey.Contains("backend-media"),"Shared prefetch context is available only in backend");
            Check((await coordinator.AcquireStreamAsync(view.Handle,default)).IsSuccess && Get<int>("MediaLeaseCalls") == before+1,"Explicit play uses existing lease resolver");
            Set("TargetDocument",doc with { Cards = [new() { Id="preview",Media=media with { Id=new("custom.public","preview"),Availability=PlatformTrackAvailability.PreviewOnly } }] });
            var preview=(await Read()).Value.Cards.Single().Media!;
            Check(preview.IsPlayable && (await coordinator.AcquireStreamAsync(preview.Handle,default)).IsSuccess &&
                Get<PlatformPlaybackRequest>("LastMediaRequest").AllowPreview,"Preview permission preserved by existing resolver");
            before++;
            Set("TargetDocument",doc with { Cards = [new() { Id="unavailable",Media=media with { Id=new("custom.public","blocked"),Availability=PlatformTrackAvailability.Unavailable } }] });
            var unavailable = (await Read()).Value.Cards.Single().Media!;
            Check(!unavailable.IsPlayable && (await coordinator.AcquireStreamAsync(unavailable.Handle,default)).Error?.Code == PlatformErrorCode.ContentUnavailable,"Unavailable card cannot resolve");
            Check(Get<int>("MediaLeaseCalls") == before+1,"Unavailable card does not call provider");
            Set("TargetDocument",doc with { Cards = [new() { Id="denied-art",Media=media with { Id=new("custom.public","denied-art"),ArtworkUrl=new("https://unapproved.test/private") } }] });
            var deniedArt = (await Read()).Value.Cards.Single().Media!;
            Check(deniedArt.CoverUrl is null && !coordinator.TryGetArtworkUri(deniedArt.Handle,out _,out _),"Denied art cannot bypass policy using track handle");
            Set("TargetDocument",doc with { Cards = [new() { Id="foreign",Media=media with { Id=new("custom.account","foreign"),Artists=[],MusicVideo=null } }] });
            Check((await Read()).Error?.Code == PlatformErrorCode.InvalidResponse,"Foreign provider media rejected before projection");
            Set("TargetDocument",doc);
            Set("MediaLeaseGate",new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            Set("MediaLeaseEntered",new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            var late = coordinator.AcquireStreamAsync(view.Handle,default);
            await Get<TaskCompletionSource<bool>>("MediaLeaseEntered").Task.WaitAsync(TimeSpan.FromSeconds(5));
            backend.InvalidateMediaContext("custom.public");
            Get<TaskCompletionSource<bool>>("MediaLeaseGate").SetResult(true);
            Check(!(await late).IsSuccess,"Late lease cannot escape revoked page revision");
            Check(!context.IsCurrent() && coordinator.GetBackendTrack(view.Handle) is null,"Revision revokes playback and prefetch handle");
            var reading = new PlatformPageDocument { Version=7,Title="Reading",Layout="feed",Cards=[new() {
                Id="backend-post",Title="Post",Open=new("Read","about","backend-state") }] };
            Set("TargetDocument",reading);
            var projected=await Read();
            Check(projected.IsSuccess && projected.Value.Version==7 && projected.Value.Cards[0].Open?.Handle.StartsWith("page-")==true,"V7 projects primary read as opaque navigation");
            Check(!JsonSerializer.Serialize(projected.Value).Contains("backend-"),"V7 state/identity stay native");
            Set("TargetDocument",reading with {Cards=[reading.Cards[0] with {Open=new("Foreign","about") {
                Target=new("feed",new("foreign","creator"),"creator") }}]});
            Check((await Read()).Error?.Code==PlatformErrorCode.InvalidResponse,"V7 primary target rejects foreign provider");
            var photo=new Uri("https://art.example.test/photo.png");
            var quoted=new PlatformPageQuote {Author="Original",Text="Quoted",Body=[new("Quoted",new("https://unapproved.test/image"))],Images=[photo],
                Discussion=new("custom.public","backend-original"),AuthorAction=new("Author","creator","backend-state") {
                    Target=new("feed",new("custom.public","backend-author"),"creator") }};
            var rich=reading with {Version=8,Cards=[reading.Cards[0] with {Text="[smile]",Body=[new("[smile]",new("https://art.example.test/e.png"))],
                Avatar=photo,Image=photo,Images=[photo],Quote=quoted,Discussion=new("custom.public","backend-own")}]};
            Set("TargetDocument",rich);var richView=await Read();
            Check(richView.IsSuccess&&richView.Value.Version==8,"V8 Native projection succeeds");
            var richCard=richView.Value.Cards[0];
            Check(richCard.Body[0].Image?.StartsWith("https://platform-art.auralis.local/")==true&&richCard.Quote!.Body[0].Image is null,"Inline artwork obeys policy and opaque proxy");
            Check(richCard.Avatar?.StartsWith("https://platform-art.auralis.local/avatar-")==true &&
                richCard.Image?.StartsWith("https://platform-art.auralis.local/image-")==true && richCard.Images.Single()==richCard.Image &&
                richCard.Quote!.Images.Single()==richCard.Image,"Native separates avatar and full-image grants even for the same source URI");
            Check(coordinator.TryGetArtworkUri(new Uri(richCard.Images.Single()).AbsolutePath.TrimStart('/'), out var resolvedPhoto, out var photoAllowed) &&
                resolvedPhoto==photo && photoAllowed!(photo),"Projected reading photo resolves under the declared provider policy");
            Check(richCard.Quote!.DiscussionHandle!=richCard.DiscussionHandle&&richCard.Quote.AuthorAction!.Handle.StartsWith("page-"),"Quote has separate opaque subject and navigation");
            Check(!JsonSerializer.Serialize(richView.Value).Contains("backend-")&&!JsonSerializer.Serialize(richView.Value).Contains("art.example.test"),"No backend identity, state or artwork origin reaches Web");
            Set("TargetDocument",rich with {Cards=[rich.Cards[0] with {Quote=quoted with {Discussion=new("foreign","subject")}}]});
            Check((await Read()).Error?.Code==PlatformErrorCode.InvalidResponse,"Quote cannot cross comment providers");
            Set("TargetDocument",rich with {Cards=[rich.Cards[0] with {AuthorAction=new("Author","creator") {Target=new("feed",new("foreign","author"),"creator")}}]});
            Check((await Read()).Error?.Code==PlatformErrorCode.InvalidResponse,"Attributed author cannot cross providers");
            var live=reading with {Version=9,Navigation=[new(new("All","about","backend-filter"),new("https://art.example.test/portrait"),true)],
                Updates=new(new("Check","about","backend-check"),new("Reload","about","backend-reload"),"backend-revision")};
            Set("TargetDocument",live);var liveView=(await Read()).Value;
            Check(liveView.Navigation[0].Image!.StartsWith("https://platform-art.auralis.local/")&&liveView.Navigation[0].Selected,"V9 authorized portrait and selection");
            Check(liveView.Updates!.Fingerprint.Length==64&&!JsonSerializer.Serialize(liveView).Contains("backend-"),"V9 revision/state stay native");
            Check((await Read()).Value.Updates!.Fingerprint==liveView.Updates.Fingerprint,"Same revision has stable session fingerprint");
            Set("TargetDocument",live with {Updates=live.Updates! with {Revision="backend-changed"}});
            Check((await Read()).Value.Updates!.Fingerprint!=liveView.Updates.Fingerprint,"Changed revision projects different fingerprint");
            Set("TargetDocument",live with {Navigation=[live.Navigation[0] with {Image=new("https://unapproved.test/avatar")}]});
            Check((await Read()).Value.Navigation[0].Image is null,"Rail follows declared artwork domains");
            Set("TargetDocument",live with {Next=new("Next","about","backend-next")});
            var appendHandle=(await Read()).Value.Next!.Handle;
            Check((await coordinator.ReadPluginGlobalPageAsync("custom.public","query",appendHandle,"en-US",default)).Error?.Code==PlatformErrorCode.InvalidResponse,"Appended batches cannot replace rail or start a new poll");
            Set("TargetDocument",doc);
            Check(!(await coordinator.AcquireStreamAsync(view.Handle,default)).IsSuccess,"Old media cannot replay");
            Check((await Read()).Value.Cards.Single().Media!.Handle != view.Handle,"Fresh revision gets a fresh media handle");
        }
        finally { Set("TargetDocument",null); Set("MediaLeaseGate",null); }
        Console.WriteLine($"PASS page media: {checks} checks; metadata only, existing resolver, artwork, ownership, stable identity and revocation.");
    }
}

public sealed partial class RoutingProvider
{
    internal static int MediaLeaseCalls;
    internal static PlatformPlaybackRequest? LastMediaRequest;
    internal static TaskCompletionSource<bool>? MediaLeaseGate = null;
    internal static TaskCompletionSource<bool>? MediaLeaseEntered = null;
    public async Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(PlatformPlaybackRequest request,CancellationToken token)
    {
        MediaLeaseCalls++; LastMediaRequest=request;
        if (MediaLeaseGate is { } gate) { MediaLeaseEntered!.TrySetResult(true); await gate.Task; }
        return PlatformResult<PlatformStreamLease>.Success(new(new Uri("https://media.example.test/audio"),
            DateTimeOffset.UtcNow.AddMinutes(1),"audio/mpeg",new("fixture","Fixture")));
    }
}
