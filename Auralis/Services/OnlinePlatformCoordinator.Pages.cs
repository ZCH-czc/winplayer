using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.Services;

internal sealed partial class OnlinePlatformCoordinator
{
    private sealed record PageNavigation(string MediaHandle, string Entry, string Route, string? State,
        Func<bool> IsCurrent, DateTimeOffset Created, bool Append, string Collection, IReadOnlyList<string> Visited,
        PlatformPageQuery? Query, IReadOnlyDictionary<string, string> InputValues, PageContext Context);
    // The Web keeps the original owner/entry throughout its bounded history. Only this backend context
    // changes across a typed link. It cannot mint a playable track or switch providers.
    private sealed record PageContext(string Entry, PlatformEntityId? Entity, string Kind);
    private readonly Dictionary<string, PageNavigation> _pageNavigation = new(StringComparer.Ordinal);

    internal async Task<PlatformResult<OnlinePageView>> ReadPluginPageAsync(string mediaHandle, string entryId,
        string? navigationHandle, string language, CancellationToken token, IReadOnlyDictionary<string, string>? inputValues = null)
        => await ReadPageCoreAsync(mediaHandle, null, entryId, navigationHandle, language, token, inputValues).ConfigureAwait(false);

    internal async Task<PlatformResult<OnlinePageView>> ReadPluginGlobalPageAsync(string providerId, string entryId,
        string? navigationHandle, string language, CancellationToken token, IReadOnlyDictionary<string, string>? inputValues = null)
        => await ReadPageCoreAsync(null, providerId, entryId, navigationHandle, language, token, inputValues).ConfigureAwait(false);

    private async Task<PlatformResult<OnlinePageView>> ReadPageCoreAsync(string? mediaHandle, string? globalProvider,
        string entryId, string? navigationHandle, string language, CancellationToken token, IReadOnlyDictionary<string, string>? inputValues)
    {
        token.ThrowIfCancellationRequested();
        var global = globalProvider is not null;
        if (global && !PlatformPageEntry.ValidId(globalProvider)) return PageUnavailable();
        var track = mediaHandle is null ? null : GetBackendTrack(mediaHandle);
        CommunityHandleRegistry.Entry? creator;
        lock (_gate) creator = track is null && mediaHandle is not null ? _community.Get(mediaHandle, "creator") : null;
        var entity = track?.Id ?? creator?.Entity;
        if (!global && entity is null) return PageUnavailable();
        var providerId = globalProvider ?? entity!.Value.ProviderId;
        var owner = global ? "provider:" + providerId : "entity:" + mediaHandle;
        var provider = await ResolveProviderAsync(providerId, global ? PlatformCapabilityKind.GlobalPages : PlatformCapabilityKind.Pages, token).ConfigureAwait(false);
        if (!provider.IsSuccess) return PlatformResult<OnlinePageView>.Failure(provider.Error!);
        var entry = provider.Value.Pages.FirstOrDefault(p => p.Id == entryId);
        if (entry is null || (entry.Placement == "global") != global || (creator is not null && !entry.AcceptsCreatorContext)) return PageUnavailable();
        var context = await _backend.CaptureMediaContextAsync(providerId, token).ConfigureAwait(false);
        PageNavigation? navigation = null;
        lock (_gate)
        {
            foreach (var key in _pageNavigation.Where(p => !p.Value.IsCurrent() ||
                DateTimeOffset.UtcNow - p.Value.Created > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray()) _pageNavigation.Remove(key);
            if (navigationHandle is not null && (!_pageNavigation.TryGetValue(navigationHandle, out navigation) ||
                navigation.MediaHandle != owner || navigation.Entry != entryId)) return PageUnavailable();
        }
        bool IsCurrent() => context.IsCurrent() && creator?.IsActive?.Invoke() != false;
        if (!IsCurrent() || navigation?.IsCurrent() == false) return PageUnavailable();
        var pageContext = navigation?.Context ?? new(entryId, entity, global ? "global" : creator is null ? "media" : "creator");
        var activeEntry = provider.Value.Pages.FirstOrDefault(p => p.Id == pageContext.Entry);
        if (activeEntry is null || (pageContext.Kind == "global" ? activeEntry.Placement != "global" :
            pageContext.Entity is not { } activeEntity || !OwnsEntity(providerId, activeEntity) ||
            !provider.Value.Capabilities.Contains(PlatformCapabilityKind.Pages) ||
            activeEntry.Placement == "global" || pageContext.Kind == "creator" && !activeEntry.AcceptsCreatorContext))
            return PageUnavailable();
        if (navigation?.Query is { } schema
            ? !PlatformPageQueryValidation.Accepts(schema, inputValues)
            : inputValues is not null)
            return PlatformResult<OnlinePageView>.Failure(PlatformErrorCode.InvalidRequest, "查询条件无效，请检查输入后重试。");
        var values = PlatformPageQueryValidation.Snapshot(inputValues ?? navigation?.InputValues ?? new Dictionary<string, string>());
        var route = navigation?.Route ?? entryId;
        var state = navigation?.State;
        var locale = language == "en-US" ? "en-US" : "zh-CN";
        var result = pageContext.Kind == "global"
            ? await _backend.Router.RouteAsync<IPlatformGlobalPagesCapability, PlatformPageDocument>(providerId,
                (c, ct) => c.ReadGlobalPageAsync(new(providerId, route, state, locale) { InputValues = values }, ct), token).ConfigureAwait(false)
            : await _backend.Router.RouteAsync<IPlatformPagesCapability, PlatformPageDocument>(providerId,
                (c, ct) => c.ReadPageAsync(new(pageContext.Entity!.Value, route, state, locale) {
                    ContextKind = pageContext.Kind, InputValues = values }, ct), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent() || (navigation is not null && !navigation.IsCurrent())) return PageUnavailable();
        if (!result.IsSuccess) return PlatformResult<OnlinePageView>.Failure(result.Error!);
        if (!PlatformPageValidation.IsValid(result.Value) || result.Value.Version > activeEntry.DocumentVersion ||
            (navigation?.Append == true && (result.Value.Tabs.Count > 0 || result.Value.Query is not null)) || result.Value.Cards.Any(c =>
            c.Media is { } media && !AcceptsPageMedia(providerId, provider.Value.Capabilities, media) ||
            c.Discussion is { } discussion && (!OwnsEntity(providerId, discussion) ||
                !provider.Value.Capabilities.Contains(PlatformCapabilityKind.Comments))))
            return PlatformResult<OnlinePageView>.Failure(PlatformErrorCode.InvalidResponse, "插件页面格式不受支持或超出限制。");
        // Validate the complete document before registering any link or authorizing artwork.
        bool AcceptsTarget(PlatformPageTarget target) => activeEntry.Presentation == "page" &&
            OwnsEntity(providerId, target.Entity) && provider.Value.Capabilities.Contains(PlatformCapabilityKind.Pages) &&
            provider.Value.Pages.Any(p => p.Id == target.EntryId && p.Presentation == "page" &&
                (target.ContextKind == "creator" ? p.Placement == "creator" && p.AcceptsCreatorContext : p.Placement == "media"));
        if (result.Value.Actions.Concat(result.Value.Cards.SelectMany(c => c.Actions))
            .Any(a => a.Target is { } target && !AcceptsTarget(target)))
            return PlatformResult<OnlinePageView>.Failure(PlatformErrorCode.InvalidResponse, "插件页面格式不受支持或超出限制。");
        var artwork = await _backend.GetCommentArtworkAuthorizationAsync(providerId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent()) return PageUnavailable();
        var doc = result.Value;
        var append = navigation?.Append == true;
        var collection = append ? navigation!.Collection : "collection-" + Guid.NewGuid().ToString("N");
        IReadOnlyList<string> visited = append ? navigation!.Visited : [];
        // Compare backend states, not random Web handles. Detect stalled/cyclic continuations.
        var currentKey = System.Text.Json.JsonSerializer.Serialize(new[] { route, state });
        var chain = visited.Append(currentKey).ToArray();
        if (doc.Next is { } next && (chain.Length >= 128 || chain.Contains(
            System.Text.Json.JsonSerializer.Serialize(new[] { next.Route, next.State }), StringComparer.Ordinal)))
            return PlatformResult<OnlinePageView>.Failure(PlatformErrorCode.InvalidResponse, "插件返回了重复的分页入口，请重新打开。");
        lock (_gate)
        {
            string? Art(Uri? uri) => RegisterCommentAvatar(providerId, uri, artwork.Policy, () => artwork.IsActive() && IsCurrent());
            OnlinePageAction ProjectAction(PlatformPageAction action, bool isAppend, PlatformPageQuery? query = null)
            {
                while (_pageNavigation.Count >= 512) _pageNavigation.Remove(_pageNavigation.MinBy(p => p.Value.Created).Key);
                var handle = "page-" + Guid.NewGuid().ToString("N");
                var targetContext = action.Target is { } target
                    ? new PageContext(target.EntryId, target.Entity, target.ContextKind) : pageContext;
                // Query input belongs to the source form, not to a different entity page.
                var targetValues = action.Target is null ? values : PlatformPageQueryValidation.Snapshot(new Dictionary<string, string>());
                _pageNavigation.Add(handle, new(owner, entryId, action.Route, action.State, IsCurrent,
                    DateTimeOffset.UtcNow, isAppend, collection, isAppend ? chain : [], query, targetValues, targetContext));
                return new(action.Label, handle);
            }
            return PlatformResult<OnlinePageView>.Success(new(doc.Title, doc.Description, doc.Layout,
                doc.Cards.Select(c => new OnlinePageCard(c.Title, c.Text, Art(c.Image), c.PublishedAt,
                    c.Actions.Select(a => ProjectAction(a, false)).ToArray()) {
                    Handle = c.Id is null ? null : _community.Add("page-card", new(providerId, c.Id), collection, isActive: IsCurrent),
                    Author = c.Author, Avatar = Art(c.Avatar), Images = c.Images.Select(Art).OfType<string>().ToArray(),
                    DiscussionHandle = c.Discussion is { } subject ? _community.Add("discussion", subject, collection, isActive: IsCurrent) : null,
                    CommentCount = c.CommentCount,
                    Media = c.Media is null ? null : RegisterPageMedia(c.Media, IsCurrent, artwork.Policy, artwork.IsActive)
                }).ToArray(), doc.Actions.Select(a => ProjectAction(a, false)).ToArray()) {
                    Image = Art(doc.Image), Append = append, CollectionHandle = collection,
                    Tabs = doc.Tabs.Select(t => new OnlinePageTab(ProjectAction(t.Action, false), t.Selected)).ToArray(),
                    Query = doc.Query is null ? null : ProjectQuery(doc.Query),
                    Next = doc.Next is null ? null : ProjectAction(doc.Next, true)
                });
            OnlinePageQuery ProjectQuery(PlatformPageQuery query)
            {
                var snapshot = PlatformPageQueryValidation.Snapshot(query);
                return new(ProjectAction(snapshot.Submit, false, snapshot), snapshot.Fields);
            }
        }
    }
    internal static bool AcceptsPageMedia(string providerId, IReadOnlyCollection<PlatformCapabilityKind> capabilities, PlatformTrack media) =>
        OwnsTrack(providerId, media) && capabilities.Contains(PlatformCapabilityKind.StreamResolution);

    private OnlineTrackView RegisterPageMedia(PlatformTrack media, Func<bool> isCurrent,
        PlatformCommentArtworkPolicy policy, Func<bool> artworkActive)
    {
        // Metadata only: no lease, decoder, cache preparation or local-library mutation here.
        // Stable within a revision, so rereading/back/pinned cards do not duplicate queue identities.
        lock (_gate)
        {
            PurgeExpiredHandles();
            var previous = _tracks.FirstOrDefault(p => p.Value.IsCurrent is not null && p.Value.Track.Id == media.Id);
            var snapshot = media with { Artists = media.Artists.ToArray(),
                ArtworkUrl = policy.Allows(media.ArtworkUrl) ? media.ArtworkUrl : null };
            return RegisterTrack(snapshot, previous.Key, isCurrent, uri => artworkActive() && isCurrent() && policy.Allows(uri));
        }
    }
    private static PlatformResult<OnlinePageView> PageUnavailable() => PlatformResult<OnlinePageView>.Failure(
        PlatformErrorCode.NotFound, "页面入口已过期或插件不可用，请重新打开。");
}

internal sealed record OnlinePageAction(string Label, string Handle);
internal sealed record OnlinePageTab(OnlinePageAction Action, bool Selected);
internal sealed record OnlinePageQuery(OnlinePageAction Submit, IReadOnlyList<PlatformPageQueryField> Fields);
internal sealed record OnlinePageCard(string Title, string Text, string? Image, DateTimeOffset? PublishedAt, IReadOnlyList<OnlinePageAction> Actions)
{
    public string? Handle { get; init; }
    public string Author { get; init; } = "";
    public string? Avatar { get; init; }
    public IReadOnlyList<string> Images { get; init; } = [];
    public string? DiscussionHandle { get; init; }
    public long? CommentCount { get; init; }
    public OnlineTrackView? Media { get; init; }
}
internal sealed record OnlinePageView(string Title, string Description, string Layout, IReadOnlyList<OnlinePageCard> Cards, IReadOnlyList<OnlinePageAction> Actions)
{
    public IReadOnlyList<OnlinePageTab> Tabs { get; init; } = [];
    public OnlinePageQuery? Query { get; init; }
    public string? Image { get; init; }
    public bool Append { get; init; }
    public string? CollectionHandle { get; init; }
    public OnlinePageAction? Next { get; init; }
}
