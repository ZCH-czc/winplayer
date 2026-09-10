using System.Collections.ObjectModel;

namespace Auralis.Platform.Abstractions;

/// <summary>Classifies a provider search suggestion.</summary>
public enum PlatformSearchSuggestionKind
{
    /// <summary>The provider did not classify the suggestion.</summary>
    Query,

    /// <summary>The suggestion identifies a track.</summary>
    Track,

    /// <summary>The suggestion identifies an album.</summary>
    Album,

    /// <summary>The suggestion identifies an artist.</summary>
    Artist,

    /// <summary>The suggestion identifies a playlist.</summary>
    Playlist
}

/// <summary>Requests provider suggestions for a partial query.</summary>
public sealed record PlatformSearchSuggestionRequest
{
    /// <summary>Creates a suggestion request.</summary>
    public PlatformSearchSuggestionRequest(string query, int limit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Suggestion limit must be between 1 and 50.");
        }

        Query = query;
        Limit = limit;
    }

    /// <summary>Gets the user's partial query.</summary>
    public string Query { get; }

    /// <summary>Gets the requested maximum result count.</summary>
    public int Limit { get; }
}

/// <summary>Represents a provider search suggestion.</summary>
public sealed record PlatformSearchSuggestion
{
    /// <summary>Gets the suggested text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the provider classification.</summary>
    public PlatformSearchSuggestionKind Kind { get; init; } = PlatformSearchSuggestionKind.Query;

    /// <summary>Gets an optional provider-qualified entity target.</summary>
    public PlatformEntityId? EntityId { get; init; }
}

/// <summary>Represents one provider-curated hot search entry.</summary>
public sealed record PlatformHotSearchItem
{
    /// <summary>Gets the search text.</summary>
    public required string Query { get; init; }

    /// <summary>Gets the one-based provider rank when available.</summary>
    public int? Rank { get; init; }

    /// <summary>Gets an optional relative provider score.</summary>
    public double? Score { get; init; }

    /// <summary>Gets whether the provider marks the entry as rising.</summary>
    public bool IsTrending { get; init; }
}

/// <summary>Classifies requested artwork usage.</summary>
public enum PlatformArtworkKind
{
    /// <summary>The entity's ordinary primary image.</summary>
    Primary,

    /// <summary>A compact thumbnail.</summary>
    Thumbnail,

    /// <summary>A wide or ambient background image.</summary>
    Background,

    /// <summary>An artist avatar or portrait.</summary>
    ArtistAvatar
}

/// <summary>Requests a provider artwork resource.</summary>
public sealed record PlatformArtworkRequest
{
    /// <summary>Creates an artwork request.</summary>
    public PlatformArtworkRequest(
        PlatformEntityId entityId,
        PlatformArtworkKind kind = PlatformArtworkKind.Primary,
        int? preferredWidth = null,
        int? preferredHeight = null)
    {
        if (preferredWidth is <= 0 || preferredHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredWidth), "Preferred dimensions must be positive when supplied.");
        }

        EntityId = entityId;
        Kind = kind;
        PreferredWidth = preferredWidth;
        PreferredHeight = preferredHeight;
    }

    /// <summary>Gets the provider-qualified entity ID.</summary>
    public PlatformEntityId EntityId { get; }

    /// <summary>Gets the requested artwork usage.</summary>
    public PlatformArtworkKind Kind { get; }

    /// <summary>Gets an optional preferred pixel width.</summary>
    public int? PreferredWidth { get; }

    /// <summary>Gets an optional preferred pixel height.</summary>
    public int? PreferredHeight { get; }
}

/// <summary>
/// Represents an artwork resource for the trusted backend image pipeline. URLs and headers can be signed
/// or account-bound, so the object must not be serialized directly to frontend JavaScript or ordinary logs.
/// The backend should fetch, validate, decode, and expose only safe image bytes or a host-owned asset URL.
/// </summary>
public sealed record PlatformArtworkResource
{
    /// <summary>Creates a backend artwork resource.</summary>
    public PlatformArtworkResource(
        Uri url,
        string? mimeType = null,
        int? pixelWidth = null,
        int? pixelHeight = null,
        DateTimeOffset? expiresAt = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("An artwork URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        if (pixelWidth is <= 0 || pixelHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Artwork dimensions must be positive when supplied.");
        }

        Url = url;
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ExpiresAt = expiresAt;
        RequestHeaders = new ReadOnlyDictionary<string, string>(
            requestHeaders is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gets a backend-only provider URL.</summary>
    public Uri Url { get; }

    /// <summary>Gets an optional provider MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Gets an optional pixel width.</summary>
    public int? PixelWidth { get; }

    /// <summary>Gets an optional pixel height.</summary>
    public int? PixelHeight { get; }

    /// <summary>Gets an optional expiration instant.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets backend-only request headers.</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }
}

/// <summary>Specifies provider comment ordering.</summary>
public enum PlatformCommentSort
{
    /// <summary>Provider-recommended ordering.</summary>
    Recommended,

    /// <summary>Newest comments first.</summary>
    Newest
}

/// <summary>Requests comments for a provider entity.</summary>
public sealed record PlatformCommentsRequest
{
    /// <summary>Creates a comment request.</summary>
    public PlatformCommentsRequest(
        PlatformEntityId entityId,
        PlatformPageRequest? page = null,
        PlatformCommentSort sort = PlatformCommentSort.Recommended)
    {
        EntityId = entityId;
        Page = page ?? new PlatformPageRequest();
        Sort = sort;
    }

    /// <summary>Gets the provider-qualified entity ID.</summary>
    public PlatformEntityId EntityId { get; }

    /// <summary>Gets cursor and page-size options.</summary>
    public PlatformPageRequest Page { get; }

    /// <summary>Gets requested ordering.</summary>
    public PlatformCommentSort Sort { get; }
}

/// <summary>Represents a provider comment author without authentication data.</summary>
public sealed record PlatformCommentAuthor
{
    /// <summary>Gets an optional provider-qualified user or artist ID.</summary>
    public PlatformEntityId? Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets a public avatar URL when supplied by the provider.</summary>
    public Uri? AvatarUrl { get; init; }
}

/// <summary>Represents one sanitized provider comment.</summary>
public sealed record PlatformComment
{
    /// <summary>Optional public emoji artwork keyed by an exact plain-text token; never HTML.</summary>
    public IReadOnlyList<PlatformCommentEmote> Emotes { get; init; } = [];
    /// <summary>Gets the provider-qualified comment ID.</summary>
    public required PlatformEntityId Id { get; init; }

    /// <summary>Gets the comment author.</summary>
    public required PlatformCommentAuthor Author { get; init; }

    /// <summary>Gets plain comment text. The UI must still apply normal text escaping.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the provider-reported publication time.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Gets an optional provider like count.</summary>
    public long? LikeCount { get; init; }

    /// <summary>Gets an optional provider reply count.</summary>
    public long? ReplyCount { get; init; }
}

/// <summary>Provider supplied public emoji artwork. Hosts must proxy and validate the URL.</summary>
public sealed record PlatformCommentEmote(string Text, Uri Url);
