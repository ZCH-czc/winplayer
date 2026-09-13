using Auralis.Platform.Abstractions;

namespace Auralis.Services;

internal sealed partial class OnlinePlatformCoordinator
{
    private sealed record CreatorSearchReference(string Provider, string Query, string? Cursor,
        OnlineCreatorView? Profile, Func<bool> IsCurrent, DateTimeOffset Created);
    private readonly Dictionary<string, CreatorSearchReference> _creatorSearch = new(StringComparer.Ordinal);

    private void PurgeCreatorSearch()
    {
        foreach (var key in _creatorSearch.Where(p => !p.Value.IsCurrent() || DateTimeOffset.UtcNow - p.Value.Created > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray())
            _creatorSearch.Remove(key);
        while (_creatorSearch.Count >= 512) _creatorSearch.Remove(_creatorSearch.MinBy(p => p.Value.Created).Key);
    }

    internal async Task<PlatformResult<OnlineCreatorSearchPage>> SearchCreatorsAsync(string providerId, string query, string? pageHandle, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512 || providerId is not { Length: > 0 and <= 128 })
            return PlatformResult<OnlineCreatorSearchPage>.Failure(PlatformErrorCode.InvalidRequest, "无效的作者搜索请求。");
        query = query.Trim();
        var provider = await ResolveProviderAsync(providerId, PlatformCapabilityKind.CreatorSearch, token).ConfigureAwait(false);
        if (!provider.IsSuccess) return PlatformResult<OnlineCreatorSearchPage>.Failure(provider.Error!);
        var context = await _backend.CaptureMediaContextAsync(providerId, token).ConfigureAwait(false);
        CreatorSearchReference? page = null;
        lock (_gate)
        {
            PurgeCreatorSearch();
            if (pageHandle is not null && (!_creatorSearch.TryGetValue(pageHandle, out page) || page.Profile is not null ||
                page.Provider != providerId || page.Query != query)) return ExpiredCommunity<OnlineCreatorSearchPage>();
        }
        var result = await _backend.Router.RouteAsync<ICreatorSearchCapability, PlatformPage<PlatformCreatorProfile>>(providerId,
            (c, ct) => c.SearchCreatorsAsync(new(query, new(20, page?.Cursor)), ct), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!context.IsCurrent() || page is not null && !page.IsCurrent()) return ExpiredCommunity<OnlineCreatorSearchPage>();
        if (!result.IsSuccess) return PlatformResult<OnlineCreatorSearchPage>.Failure(result.Error!);
        if (result.Value.Items.Count > 100 || result.Value.Items.Any(p => p is null || !OwnsEntity(providerId, p.Id) ||
            p.DisplayName is not { Length: > 0 and <= 512 } || p.Description is not { Length: <= 32000 }) || result.Value.NextCursor?.Length > 8192)
            return InvalidResultIdentity<OnlineCreatorSearchPage>();
        var art = await _backend.GetCommentArtworkAuthorizationAsync(providerId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!context.IsCurrent()) return ExpiredCommunity<OnlineCreatorSearchPage>();
        lock (_gate)
        {
            var items = new List<OnlineCreatorView>();
            foreach (var p in result.Value.Items.DistinctBy(p => p.Id))
            {
                PurgeCreatorSearch();
                var handle = _community.Add("creator", p.Id, context.Revision.ToString(), isActive: context.IsCurrent);
                var view = new OnlineCreatorView(handle, p.DisplayName, p.Description,
                    RegisterCommentAvatar(providerId, p.AvatarUrl, art.Policy, () => art.IsActive() && context.IsCurrent()));
                _creatorSearch[handle] = new(providerId, query, null, view, context.IsCurrent, DateTimeOffset.UtcNow);
                items.Add(view);
            }
            string? next = null;
            if (!string.IsNullOrEmpty(result.Value.NextCursor) && result.Value.NextCursor != page?.Cursor && items.Count > 0)
            {
                PurgeCreatorSearch();
                next = "creator-page-" + Guid.NewGuid().ToString("N");
                _creatorSearch[next] = new(providerId, query, result.Value.NextCursor, null, context.IsCurrent, DateTimeOffset.UtcNow);
            }
            return PlatformResult<OnlineCreatorSearchPage>.Success(new(items, next, result.Value.TotalCount));
        }
    }
}

internal sealed record OnlineCreatorSearchPage(IReadOnlyList<OnlineCreatorView> Items, string? NextPageHandle, long? TotalCount);
