namespace Auralis.Platform.Abstractions;

/// <summary>Optional read-only search for creators, separate from playable media search.</summary>
public interface ICreatorSearchCapability
{
    /// <summary>Returns provider-owned creator identities and public profile metadata.</summary>
    Task<PlatformResult<PlatformPage<PlatformCreatorProfile>>> SearchCreatorsAsync(PlatformSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Optional creator profile without requiring an activity feed.</summary>
public interface ICreatorProfileCapability
{
    /// <summary>Resolves the principal credited creator of this media item.</summary>
    Task<PlatformResult<PlatformCreatorProfile>> GetCreatorAsync(PlatformEntityId mediaId, CancellationToken cancellationToken);
}

/// <summary>Optional, read-only creator profile and activity browsing. IDs and cursors remain backend-only.</summary>
public interface ICreatorFeedCapability
{
    /// <summary>Resolves the creator of a media item, not the currently authenticated user.</summary>
    Task<PlatformResult<PlatformCreatorProfile>> GetCreatorAsync(PlatformEntityId mediaId, CancellationToken cancellationToken);
    /// <summary>Returns one page of activity visible to the current session.</summary>
    Task<PlatformResult<PlatformPage<PlatformCreatorPost>>> GetCreatorPostsAsync(PlatformEntityId creatorId, PlatformPageRequest page, CancellationToken cancellationToken);
}

/// <summary>Optional paginated replies, including replies to other replies within the root thread.</summary>
public interface ICommentRepliesCapability
{
    /// <summary>The entity and root comment must belong to the same provider and discussion.</summary>
    Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentRepliesAsync(PlatformEntityId entityId, PlatformEntityId rootCommentId, PlatformPageRequest page, CancellationToken cancellationToken);
}

/// <summary>Public creator metadata. Artwork is validated and proxied by the host.</summary>
public sealed record PlatformCreatorProfile(PlatformEntityId Id, string DisplayName, string Description, Uri? AvatarUrl);

/// <summary>One read-only activity card. Null DiscussionId means comments are not available.</summary>
public sealed record PlatformCreatorPost
{
    /// <summary>Stable provider-qualified post identity.</summary>
    public required PlatformEntityId Id { get; init; }
    /// <summary>Optional comment subject, which need not equal the post identity.</summary>
    public PlatformEntityId? DiscussionId { get; init; }
    /// <summary>Plain display title, never HTML.</summary>
    public string Title { get; init; } = "";
    /// <summary>Plain text, including a forwarded post's attribution when supplied.</summary>
    public string Text { get; init; } = "";
    /// <summary>Provider publication instant, or null when unknown.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Public image addresses; not navigation or playback URLs.</summary>
    public IReadOnlyList<Uri> Images { get; init; } = [];
    /// <summary>Optional normalized work attached to this post, never a resolved stream.
    /// Declarative page adapters may expose it through a version-six media card.</summary>
    public PlatformTrack? Media { get; init; }
    /// <summary>Visible comment count, or null when unknown.</summary>
    public long? CommentCount { get; init; }
}
