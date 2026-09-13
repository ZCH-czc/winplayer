using Auralis.Platform.Abstractions;

namespace Auralis.Services;

internal sealed partial class OnlinePlatformCoordinator
{
    private readonly CommunityHandleRegistry _community = new();
    private static PlatformResult<T> ExpiredCommunity<T>() => PlatformResult<T>.Failure(PlatformErrorCode.NotFound, "入口已过期，请重新打开作者或评论。");

    internal async Task<PlatformResult<OnlineCreatorView>> GetCreatorAsync(string handle, CancellationToken token)
    {
        lock (_gate)
        {
            PurgeCreatorSearch();
            if (_creatorSearch.TryGetValue(handle, out var found) && found.Profile is { } profile && _community.Get(handle, "creator") is not null)
                return PlatformResult<OnlineCreatorView>.Success(profile);
        }
        var track = GetBackendTrack(handle);
        if (track is null) return ExpiredCommunity<OnlineCreatorView>();
        var context = await _backend.CaptureMediaContextAsync(track.Id.ProviderId, token).ConfigureAwait(false);
        var standalone = await ResolveProviderAsync(track.Id.ProviderId, PlatformCapabilityKind.CreatorProfile, token).ConfigureAwait(false);
        var result = standalone.IsSuccess
            ? await _backend.Router.RouteAsync<ICreatorProfileCapability, PlatformCreatorProfile>(track.Id.ProviderId,
                (c, ct) => c.GetCreatorAsync(track.Id, ct), token).ConfigureAwait(false)
            : await _backend.Router.RouteAsync<ICreatorFeedCapability, PlatformCreatorProfile>(track.Id.ProviderId,
                (c, ct) => c.GetCreatorAsync(track.Id, ct), token).ConfigureAwait(false);
        if (!result.IsSuccess) return PlatformResult<OnlineCreatorView>.Failure(result.Error!);
        if (result.Value is null || !OwnsEntity(track.Id.ProviderId,result.Value.Id)) return InvalidResultIdentity<OnlineCreatorView>();
        var auth = await _backend.GetCommentArtworkAuthorizationAsync(track.Id.ProviderId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!context.IsCurrent()) return ExpiredCommunity<OnlineCreatorView>();
        lock (_gate) return PlatformResult<OnlineCreatorView>.Success(new(_community.Add("creator", result.Value.Id, context.Revision.ToString(), isActive: context.IsCurrent),
            result.Value.DisplayName, result.Value.Description, RegisterCommentAvatar(track.Id.ProviderId, result.Value.AvatarUrl, auth.Policy, () => auth.IsActive() && context.IsCurrent())));
    }

    internal async Task<PlatformResult<OnlineCreatorPostPage>> GetCreatorPostsAsync(string handle, string? pageHandle, CancellationToken token)
    {
        CommunityHandleRegistry.Entry? creator, page;
        lock (_gate) { creator = _community.Get(handle, "creator"); page = _community.Get(pageHandle, "feed-page", handle); }
        if (creator is null || (!string.IsNullOrEmpty(pageHandle) && page is null)) return ExpiredCommunity<OnlineCreatorPostPage>();
        if (creator.IsActive?.Invoke() == false) return ExpiredCommunity<OnlineCreatorPostPage>();
        var result = await _backend.Router.RouteAsync<ICreatorFeedCapability, PlatformPage<PlatformCreatorPost>>(creator.Entity.ProviderId,
            (c, ct) => c.GetCreatorPostsAsync(creator.Entity, new(20, page?.Cursor), ct), token).ConfigureAwait(false);
        if (creator.IsActive?.Invoke() == false) return ExpiredCommunity<OnlineCreatorPostPage>();
        if (!result.IsSuccess) return PlatformResult<OnlineCreatorPostPage>.Failure(result.Error!);
        if (result.Value.Items.Count > 100 || result.Value.Items.Any(p => p is null || !OwnsEntity(creator.Entity.ProviderId,p.Id) ||
            (p.DiscussionId is { } discussion && !OwnsEntity(creator.Entity.ProviderId,discussion)))) return InvalidResultIdentity<OnlineCreatorPostPage>();
        var auth = await _backend.GetCommentArtworkAuthorizationAsync(creator.Entity.ProviderId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (creator.IsActive?.Invoke() == false) return ExpiredCommunity<OnlineCreatorPostPage>();
        lock (_gate)
        {
            var items = result.Value.Items.Select(p => new OnlineCreatorPostView(_community.Add("post", p.Id, handle, isActive: creator.IsActive),
                p.DiscussionId is { } subject ? _community.Add("discussion", subject, handle, isActive: creator.IsActive) : null,
                p.Title, p.Text, p.PublishedAt, p.CommentCount,
                p.Images.Take(9).Select(u => RegisterCommentAvatar(creator.Entity.ProviderId, u, auth.Policy, auth.IsActive)).Where(u => u is not null).Cast<string>().ToArray())).ToArray();
            return PlatformResult<OnlineCreatorPostPage>.Success(new(items, NextCommunityPage("feed-page", creator.Entity, handle, result.Value.NextCursor, page?.Cursor, creator.IsActive)));
        }
    }

    private string? NextCommunityPage(string kind, PlatformEntityId entity, string parent, string? next, string? previous, Func<bool>? isActive = null) =>
        !string.IsNullOrEmpty(next) && next != previous && next.Length <= 8192 ? _community.Add(kind, entity, parent, cursor: next, isActive: isActive) : null;

    internal async Task<PlatformResult<OnlineCommentPage>> GetCommunityCommentsAsync(string handle, string? pageHandle,
        string? rootHandle, bool newest, CancellationToken token)
    {
        PlatformEntityId? subject;
        CommunityHandleRegistry.Entry? root, page, discussion;
        var binding = handle + "|" + rootHandle + "|" + newest;
        lock (_gate)
        {
            discussion = _community.Get(handle, "discussion");
            subject = GetBackendTrack(handle)?.Id ?? discussion?.Entity;
            root = _community.Get(rootHandle, "comment", handle);
            page = _community.Get(pageHandle, "comment-page", binding);
        }
        if (subject is null || (!string.IsNullOrEmpty(rootHandle) && (root is null || root.Entity.ProviderId != subject.Value.ProviderId)) ||
            (!string.IsNullOrEmpty(pageHandle) && page is null)) return ExpiredCommunity<OnlineCommentPage>();
        var entity = subject.Value;
        var context = await _backend.CaptureMediaContextAsync(entity.ProviderId, token).ConfigureAwait(false);
        bool IsCurrent() => context.IsCurrent() && discussion?.IsActive?.Invoke() != false && root?.IsActive?.Invoke() != false && page?.IsActive?.Invoke() != false;
        if (!IsCurrent()) return ExpiredCommunity<OnlineCommentPage>();
        var result = root is not null
            ? await _backend.Router.RouteAsync<ICommentRepliesCapability, PlatformPage<PlatformComment>>(entity.ProviderId,
                (c, ct) => c.GetCommentRepliesAsync(entity, root.Entity, new(20, page?.Cursor), ct), token).ConfigureAwait(false)
            : await _backend.Router.GetCommentsAsync(entity.ProviderId, new(entity, new(20, page?.Cursor), newest ? PlatformCommentSort.Newest : PlatformCommentSort.Recommended), token).ConfigureAwait(false);
        if (!result.IsSuccess) return PlatformResult<OnlineCommentPage>.Failure(result.Error!);
        if (result.Value.Items.Count > 100 || result.Value.Items.Any(c => c is null || !OwnsEntity(entity.ProviderId,c.Id) || c.Author is null ||
            (c.Author.Id is { } author && !OwnsEntity(entity.ProviderId,author)))) return InvalidResultIdentity<OnlineCommentPage>();
        var auth = await _backend.GetCommentArtworkAuthorizationAsync(entity.ProviderId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent()) return ExpiredCommunity<OnlineCommentPage>();
        lock (_gate)
        {
            var items = result.Value.Items.Select(c => new OnlineCommentView(c.Author.DisplayName, c.Text, c.LikeCount ?? 0, c.PublishedAt,
                RegisterCommentAvatar(entity.ProviderId, c.Author.AvatarUrl, auth.Policy, auth.IsActive),
                c.Emotes.Take(64).Where(e => e.Text.Length is > 0 and <= 100).Select(e => new OnlineCommentEmote(e.Text,
                    RegisterCommentAvatar(entity.ProviderId, e.Url, auth.Policy, auth.IsActive))).Where(e => e.Url is not null).ToArray()) {
                    Handle = _community.Add(root is null ? "comment" : "reply", c.Id, handle, isActive: IsCurrent),
                    ReplyCount = c.ReplyCount, ReplyToAuthor = c.ReplyToAuthor
                }).ToArray();
            return PlatformResult<OnlineCommentPage>.Success(new(items, NextCommunityPage("comment-page", entity, binding, result.Value.NextCursor, page?.Cursor, IsCurrent)));
        }
    }
}

internal sealed record OnlineCreatorView(string Handle, string DisplayName, string Description, string? AvatarUrl);
internal sealed record OnlineCreatorPostView(string Handle, string? DiscussionHandle, string Title, string Text, DateTimeOffset? PublishedAt, long? CommentCount, IReadOnlyList<string> Images);
internal sealed record OnlineCreatorPostPage(IReadOnlyList<OnlineCreatorPostView> Items, string? NextPageHandle);
