using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Bounded display schema; navigation data is backend-only, never executable.</summary>
public static class PlatformPageValidation
{
    public static bool IsValid(PlatformPageDocument? page)
    {
        if (page is null || page.Version is < 1 or > 9 || page.Layout is not ("cards" or "list" or "feed" or "detail") ||
            page.Title is not { Length: > 0 and <= 200 } || page.Description is not { Length: <= 16000 } ||
            page.Cards is not { Count: <= 100 } || !ValidActions(page.Actions) || !ValidImage(page.Image) ||
            (page.Next is not null && (!ValidActions([page.Next]) || page.Next.Target is not null))) return false;
        if (page.Navigation is not { Count: <= 24 } || page.Navigation.Any(n => n is null ||
            !ValidActions([n.Action]) || n.Action.Target is not null || !ValidImage(n.Image)) ||
            page.Navigation.Count > 0 && (page.Version < 9 || page.Navigation.Count(n => n.Selected) != 1 ||
                page.Navigation.Select(n => (n.Action.Route, n.Action.State)).Distinct().Count() != page.Navigation.Count)) return false;
        if (page.Updates is { } u && (page.Version < 9 || page.Layout != "feed" ||
            !ValidActions([u.Check, u.Reload]) || u.Check.Target is not null || u.Reload.Target is not null ||
            u.IntervalSeconds is < 60 or > 900 || u.Revision is not { Length: > 0 and <= 512 } || u.Revision.Any(char.IsControl))) return false;
        if (page.Layout is "feed" or "detail" && page.Version < 7) return false;
        if (page.Layout == "detail" && (page.Cards.Count != 1 || page.Next is not null || page.Query is not null)) return false;
        if (page.Version < 5 && page.Actions.Any(a => a.Target is not null)) return false;
        if (page.Version == 1 && (page.Image is not null || page.Next is not null)) return false;
        if (page.Query is not null && (page.Version < 4 || !PlatformPageQueryValidation.IsValid(page.Query))) return false;
        if (page.Tabs is not { Count: <= 8 } || page.Tabs.Any(t => t is null || t.Action is null || t.Action.Target is not null) ||
            !ValidActions(page.Tabs.Select(t => t.Action).ToArray()) ||
            (page.Tabs.Count > 0 && (page.Version < 3 || page.Tabs.Count < 2 ||
                page.Tabs.Count(t => t.Selected) != 1 ||
                page.Tabs.Select(t => (t.Action.Route, t.Action.State)).Distinct().Count() != page.Tabs.Count))) return false;
        var size = page.Title.Length + page.Description.Length;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var imageCount = 0;
        foreach (var card in page.Cards)
        {
            if (card is null || card.Title is not { Length: <= 256 } || card.Text is not { Length: <= 32000 } ||
                card.Author is not { Length: <= 512 } || !ValidActions(card.Actions) ||
                (card.Open is not null && (page.Version < 7 || !ValidActions([card.Open]))) ||
                !ValidBody(card.Body, card.Text) ||
                (card.AuthorAction is not null && !ValidActions([card.AuthorAction])) ||
                (card.Quote is not null && !ValidQuote(card.Quote)) ||
                (page.Version < 8 && (card.Body.Count > 0 || card.AuthorAction is not null || card.Quote is not null)) ||
                (card.Discussion is { } subject && !ValidEntity(subject)) ||
                (card.Media is not null && (page.Version < 6 || !ValidMedia(card.Media))) ||
                !ValidImage(card.Image) || !ValidImage(card.Avatar) || card.Images is not { Count: <= 9 } ||
                card.Images.Any(i => i is null || !ValidImage(i)) || card.CommentCount < 0 ||
                card.LikeCount < 0 || card.RepostCount < 0 ||
                (page.Version < 5 && card.Actions.Any(a => a.Target is not null))) return false;
            if (page.Version == 1 && (card.Id is not null || card.Images.Count > 0 || card.Author.Length > 0 ||
                card.Avatar is not null || card.Discussion is not null || card.CommentCount is not null || card.Text.Length > 16000)) return false;
            if (page.Version >= 2 && (card.Id is not { Length: > 0 and <= 512 } || !ids.Add(card.Id) ||
                card.Id.Any(char.IsControl))) return false;
            size += card.Media is { } media ? media.Title.Length + media.Artists.Sum(a => a.Name.Length) +
                (media.Album?.Title.Length ?? 0) + (media.MusicVideo?.Title.Length ?? 0) : 0;
            size += card.Title.Length + card.Text.Length + card.Author.Length;
            imageCount += card.Images.Count + card.Body.Count(r => r.Image is not null);
            if (card.Quote is { } quote)
            {
                size += quote.Title.Length + quote.Text.Length + quote.Author.Length;
                imageCount += quote.Images.Count + quote.Body.Count(r => r.Image is not null);
            }
        }
        return size <= 128000 && imageCount + page.Navigation.Count <= 300 &&
            page.Cards.Sum(c => c.Actions.Count + (c.Open is null ? 0 : 1) + (c.AuthorAction is null ? 0 : 1) + (c.Quote?.AuthorAction is null ? 0 : 1)) + page.Actions.Count + page.Tabs.Count + page.Navigation.Count + (page.Updates is null ? 0 : 2) + (page.Next is null ? 0 : 1) + (page.Query is null ? 0 : 1) <= 128;
    }
    private static bool ValidBody(IReadOnlyList<PlatformPageTextRun>? body, string text) =>
        body is { Count: <= 128 } && body.All(r => r is not null && r.Text is { Length: > 0 and <= 32000 } && ValidImage(r.Image)) &&
        (body.Count == 0 || body.Sum(r => (long)r.Text.Length) <= 32000 && string.Concat(body.Select(r => r.Text)) == text);
    private static bool ValidQuote(PlatformPageQuote q) => q.Status is "available" or "unavailable" &&
        q.Title is { Length: <= 256 } && q.Text is { Length: <= 32000 } && q.Author is { Length: <= 512 } &&
        ValidBody(q.Body, q.Text) && ValidImage(q.Avatar) && q.Images is { Count: <= 9 } && q.Images.All(i => i is not null && ValidImage(i)) &&
        (q.AuthorAction is null || ValidActions([q.AuthorAction])) && (q.Discussion is null || ValidEntity(q.Discussion.Value)) && q.CommentCount is not < 0 &&
        (q.Status != "unavailable" || q.AuthorAction is null && q.Discussion is null && q.CommentCount is null &&
            q.Avatar is null && q.Images.Count == 0 && q.Body.Count == 0);
    private static bool ValidEntity(PlatformEntityId id) => PlatformPageEntry.ValidId(id.ProviderId) &&
        id.Value is { Length: > 0 and <= 512 } value && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool ValidDuration(TimeSpan? value) => value is null || value >= TimeSpan.Zero && value <= TimeSpan.FromDays(7);
    private static bool ValidMedia(PlatformTrack media) =>
        ValidEntity(media.Id) && media.Title is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(media.Title) &&
        Enum.IsDefined(media.Availability) && ValidDuration(media.Duration) && ValidImage(media.ArtworkUrl) && media.ViewCount is not < 0 &&
        media.Artists is { Count: <= 32 } && media.Artists.All(a => a is not null && ValidEntity(a.Id) &&
            a.Id.IsForProvider(media.Id.ProviderId) && a.Name is { Length: > 0 and <= 512 } && ValidImage(a.ArtworkUrl)) &&
        (media.Album is null || ValidEntity(media.Album.Id) && media.Album.Id.IsForProvider(media.Id.ProviderId) &&
            media.Album.Title is { Length: <= 512 } && ValidImage(media.Album.ArtworkUrl)) &&
        (media.MusicVideo is null || ValidEntity(media.MusicVideo.Id) && media.MusicVideo.Id.IsForProvider(media.Id.ProviderId) &&
            media.MusicVideo.Title is { Length: > 0 and <= 512 } && ValidDuration(media.MusicVideo.Duration) && ValidImage(media.MusicVideo.ArtworkUrl));
    private static bool ValidImage(Uri? image) => image is null || (image.IsAbsoluteUri &&
        image.Scheme == "https" && image.UserInfo.Length == 0 && image.AbsoluteUri.Length <= 2048);
    private static bool ValidActions(IReadOnlyList<PlatformPageAction>? actions) =>
        actions is { Count: <= 8 } && actions.All(a => a is not null &&
            a.Label is { Length: > 0 and <= 100 } && PlatformPageEntry.ValidId(a.Route) &&
            (a.State is null || a.State.Length <= 8192) && (a.Target is null || ValidTarget(a.Target)));
    private static bool ValidTarget(PlatformPageTarget target) =>
        PlatformPageEntry.ValidId(target.EntryId) && PlatformPageEntry.ValidId(target.Entity.ProviderId) &&
        target.Entity.Value is { Length: > 0 and <= 512 } value && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl) && target.ContextKind is "media" or "creator";
}
