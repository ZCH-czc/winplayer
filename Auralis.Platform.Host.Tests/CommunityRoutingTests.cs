using System.Text.Json;
using Auralis.Platform.Abstractions;
using Auralis.Services;

namespace Auralis.Platform.Host.Tests;

internal static class CommunityRoutingTests
{
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    internal static async Task RunAsync(OnlinePlatformCoordinator coordinator,string track)
    {
        var search = await coordinator.SearchCreatorsAsync("custom.public", "Creator", null, default);
        Check(search.IsSuccess && search.Value.Items.Count == 1 && search.Value.NextPageHandle is not null, "Creator search through generic Host");
        var found = search.Value.Items[0];
        Check((await coordinator.GetCreatorAsync(found.Handle, default)).Value.DisplayName == "Search creator", "Search creator opens without fake media");
        Check(coordinator.GetBackendTrack(found.Handle) is null, "Creator is not a playable track");
        Check((await coordinator.GetCreatorPostsAsync(found.Handle, null, default)).IsSuccess, "Search creator opens feed");
        Check((await coordinator.SearchCreatorsAsync("custom.public", "Creator", search.Value.NextPageHandle, default)).IsSuccess, "Creator search continuation");
        Check(!(await coordinator.SearchCreatorsAsync("custom.public", "Different", search.Value.NextPageHandle, default)).IsSuccess, "Search cursor bound to query");
        Check(!(await coordinator.SearchCreatorsAsync("custom.public", "Creator", found.Handle, default)).IsSuccess, "Creator is not a search cursor");
        var creator = await coordinator.GetCreatorAsync(track,default);
        Check(creator.IsSuccess,"Creator through generic Host");
        var posts = await coordinator.GetCreatorPostsAsync(creator.Value.Handle,null,default);
        Check(posts.IsSuccess && posts.Value.Items.Count==1 && posts.Value.NextPageHandle is not null,"Activity pagination");
        Check((await coordinator.GetCreatorPostsAsync(creator.Value.Handle,posts.Value.NextPageHandle,default)).IsSuccess,"Continue same creator");
        Check(!(await coordinator.GetCreatorPostsAsync(track,posts.Value.NextPageHandle,default)).IsSuccess,"Media cannot impersonate creator");
        var post=posts.Value.Items[0].DiscussionHandle!;
        var roots=await coordinator.GetCommunityCommentsAsync(post,null,null,false,default);
        Check(roots.IsSuccess && roots.Value.Items[0].ReplyCount==2 && roots.Value.Items[0].PublishedAt is not null,"Comment metadata projection");
        var root=roots.Value.Items[0].Handle;
        var replies=await coordinator.GetCommunityCommentsAsync(post,null,root,false,default);
        Check(replies.IsSuccess && replies.Value.Items[0].ReplyToAuthor=="Reply recipient","Thread metadata");
        Check((await coordinator.GetCommunityCommentsAsync(post,replies.Value.NextPageHandle,root,false,default)).IsSuccess,"Next reply page");
        Check(!(await coordinator.GetCommunityCommentsAsync(track,null,root,false,default)).IsSuccess,"Root cannot cross discussions");
        Check(!(await coordinator.GetCommunityCommentsAsync(post,roots.Value.NextPageHandle,root,false,default)).IsSuccess,"Root page cannot become reply page");
        Check(!(await coordinator.GetCommunityCommentsAsync(post,roots.Value.NextPageHandle,null,true,default)).IsSuccess,"Cursor bound to sort");
        Check(!(await coordinator.GetCommunityCommentsAsync(post,posts.Value.NextPageHandle,null,false,default)).IsSuccess,"Feed cursor cannot become comment cursor");
        var projection=JsonSerializer.Serialize(new{creator=creator.Value,posts=posts.Value,roots=roots.Value,replies=replies.Value});
        Check(!projection.Contains("backend-") && !projection.Contains("unapproved.test"),"IDs, cursors, unapproved artwork remain backend-only");
        var clock=new CommunityClock();
        var registry=new CommunityHandleRegistry(clock);
        var handle=registry.Add("creator",new("custom.public","backend-creator"));
        Check(handle==registry.Add("creator",new("custom.public","backend-creator")),"Stable deduplication handle");
        Check(registry.Get(handle,"comment") is null,"Reference kinds cannot mix");
        clock.Now+=TimeSpan.FromHours(3);
        Check(registry.Get(handle,"creator") is null,"References expire");
        Console.WriteLine("PASS community routing: creator/feed/thread paging, dates, opaque handles, context/sort ownership and expiry and creator search (23 checks).");
    }
    private sealed class CommunityClock : TimeProvider
    {
        internal DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}

public sealed partial class RoutingProvider : ICreatorFeedCapability, ICreatorProfileCapability, ICommentsCapability, ICommentRepliesCapability, ICreatorSearchCapability
{
    public Task<PlatformResult<PlatformPage<PlatformCreatorProfile>>> SearchCreatorsAsync(PlatformSearchRequest request, CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformPage<PlatformCreatorProfile>>.Success(new([new(new(id,"backend-searched-creator"),"Search creator","Public biography",null)],request.Page.Cursor is null ? "backend-creator-search-page" : null)));
    public Task<PlatformResult<PlatformCreatorProfile>> GetCreatorAsync(PlatformEntityId mediaId,CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformCreatorProfile>.Success(new(new(id,"backend-creator"),"Creator","Biography",new Uri("https://unapproved.test/avatar.png"))));
    public Task<PlatformResult<PlatformPage<PlatformCreatorPost>>> GetCreatorPostsAsync(PlatformEntityId creatorId,PlatformPageRequest page,CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformPage<PlatformCreatorPost>>.Success(new([new(){Id=new(id,"backend-post"),DiscussionId=new(id,"backend-discussion"),Text="Post",Images=[new Uri("https://unapproved.test/post.png")]}],page.Cursor is null ? "backend-feed-cursor" : null)));
    public Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentsAsync(PlatformCommentsRequest request,CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Success(new([CommunityComment(false)],request.Page.Cursor is null ? "backend-root-cursor" : null)));
    public Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentRepliesAsync(PlatformEntityId entityId,PlatformEntityId root,PlatformPageRequest page,CancellationToken token) =>
        Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Success(new([CommunityComment(true)],page.Cursor is null ? "backend-reply-cursor" : null)));
    private PlatformComment CommunityComment(bool reply)=>new(){Id=new(id,reply ? "backend-reply" : "backend-root"),Author=new(){DisplayName="Author"},Text="Text",ReplyCount=reply ? 0 : 2,PublishedAt=DateTimeOffset.FromUnixTimeSeconds(1720000000),ReplyToAuthor=reply ? "Reply recipient" : null};
}
