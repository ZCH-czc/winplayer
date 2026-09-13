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
            media with { ArtworkUrl = new("file:///private") }, media with { ArtworkUrl = new("https://user:pass@example.test/") }
        }) Check(!PlatformPageValidation.IsValid(doc with { Cards = [new() { Id = "bad", Media = invalid }] }),"Invalid media denied");
        Task<PlatformResult<OnlinePageView>> Read() => coordinator.ReadPluginGlobalPageAsync("custom.public","query",null,"en-US",default);
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
