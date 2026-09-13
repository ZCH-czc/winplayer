using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host;

/// <summary>Bounded display schema; navigation data is backend-only, never executable.</summary>
public static class PlatformPageValidation
{
    public static bool IsValid(PlatformPageDocument? page)
    {
        if (page is null || page.Version is not (1 or 2 or 3 or 4 or 5 or 6) || page.Layout is not ("cards" or "list") ||
            page.Title is not { Length: > 0 and <= 200 } || page.Description is not { Length: <= 16000 } ||
            page.Cards is not { Count: <= 100 } || !ValidActions(page.Actions) || !ValidImage(page.Image) ||
            (page.Next is not null && (!ValidActions([page.Next]) || page.Next.Target is not null))) return false;
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
                (card.Media is not null && (page.Version < 6 || !ValidMedia(card.Media))) ||
                !ValidImage(card.Image) || !ValidImage(card.Avatar) || card.Images is not { Count: <= 9 } ||
                card.Images.Any(i => i is null || !ValidImage(i)) || card.CommentCount < 0 ||
                (page.Version < 5 && card.Actions.Any(a => a.Target is not null))) return false;
            if (page.Version == 1 && (card.Id is not null || card.Images.Count > 0 || card.Author.Length > 0 ||
                card.Avatar is not null || card.Discussion is not null || card.CommentCount is not null || card.Text.Length > 16000)) return false;
            if (page.Version >= 2 && (card.Id is not { Length: > 0 and <= 512 } || !ids.Add(card.Id) ||
                card.Id.Any(char.IsControl))) return false;
            size += card.Media is { } media ? media.Title.Length + media.Artists.Sum(a => a.Name.Length) +
                (media.Album?.Title.Length ?? 0) + (media.MusicVideo?.Title.Length ?? 0) : 0;
            size += card.Title.Length + card.Text.Length + card.Author.Length;
            imageCount += card.Images.Count;
        }
        return size <= 128000 && imageCount <= 300 &&
            page.Cards.Sum(c => c.Actions.Count) + page.Actions.Count + page.Tabs.Count + (page.Next is null ? 0 : 1) + (page.Query is null ? 0 : 1) <= 128;
    }
    private static bool ValidEntity(PlatformEntityId id) => PlatformPageEntry.ValidId(id.ProviderId) &&
        id.Value is { Length: > 0 and <= 512 } value && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool ValidDuration(TimeSpan? value) => value is null || value >= TimeSpan.Zero && value <= TimeSpan.FromDays(7);
    private static bool ValidMedia(PlatformTrack media) =>
        ValidEntity(media.Id) && media.Title is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(media.Title) &&
        Enum.IsDefined(media.Availability) && ValidDuration(media.Duration) && ValidImage(media.ArtworkUrl) &&
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
