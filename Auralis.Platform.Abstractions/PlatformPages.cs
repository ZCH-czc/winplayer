namespace Auralis.Platform.Abstractions;

/// <summary>Optional provider-level pages, independent of any media or creator entity.</summary>
public interface IPlatformGlobalPagesCapability
{
    /// <summary>User navigation or a previously declared v9 visible-page update check; never invoked during discovery.</summary>
    Task<PlatformResult<PlatformPageDocument>> ReadGlobalPageAsync(PlatformGlobalPageReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Backend-only global navigation. No fabricated media ID or credentials.</summary>
public sealed record PlatformGlobalPageReadRequest(string ProviderId, string Route, string? State, string Language)
{
    /// <summary>Optional viewport-sized batch hint (6–20). Continuations must retain their original page size.</summary>
    public int? PreferredPageSize { get; init; }
    /// <summary>Host-validated, read-only query values; never credentials or executable commands.</summary>
    public IReadOnlyDictionary<string, string> InputValues { get; init; } = new Dictionary<string, string>();
}

/// <summary>Version-three read-only tab. Exactly one tab in a nonempty group must be selected.</summary>
public sealed record PlatformPageTab(PlatformPageAction Action, bool Selected = false);

/// <summary>Version-nine bounded, horizontal identity filter. Images use the host artwork policy.</summary>
public sealed record PlatformPageNavigationItem(PlatformPageAction Action, Uri? Image = null, bool Selected = false);

/// <summary>Version-nine read-only updates. Check returns a document with the current revision;
/// Reload is an explicit navigation. Revision stays native and is projected as a session fingerprint.
/// The host only checks a visible, idle page, stops on errors, and never replaces reading content automatically.</summary>
public sealed record PlatformPageUpdates(PlatformPageAction Check, PlatformPageAction Reload, string Revision, int IntervalSeconds = 90);

/// <summary>Optional read-only, declarative pages. No HTML, scripts or native commands.</summary>
public interface IPlatformPagesCapability
{
    /// <summary>Reads a page for a host-owned media context. Route and state never go to the WebView.</summary>
    Task<PlatformResult<PlatformPageDocument>> ReadPageAsync(PlatformPageReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Backend-only navigation. State is an opaque, non-credential continuation owned by this provider.</summary>
public sealed record PlatformPageReadRequest(PlatformEntityId MediaId, string Route, string? State, string Language)
{
    /// <summary>Optional viewport-sized batch hint (6–20), not permission to alter an existing cursor.</summary>
    public int? PreferredPageSize { get; init; }
    /// <summary>Host-resolved context: media (legacy default) or creator. Never supplied by the WebView.</summary>
    public string ContextKind { get; init; } = "media";
    /// <summary>Host-validated read-only query values, retained across opaque navigation.</summary>
    public IReadOnlyDictionary<string, string> InputValues { get; init; } = new Dictionary<string, string>();
}

/// <summary>One explicit, read-only search/filter form. Submitting only reads another page.</summary>
public sealed record PlatformPageQuery(PlatformPageAction Submit, IReadOnlyList<PlatformPageQueryField> Fields);

/// <summary>Public choice label and non-secret value; no scripts or platform entity identities.</summary>
public sealed record PlatformPageQueryOption(string Label, string Value);

/// <summary>Version-four bounded single-line text or explicit choice. No password/file/HTML fields.</summary>
public sealed record PlatformPageQueryField
{
    /// <summary>Bounded ASCII identifier, unique within this form.</summary>
    public string Key { get; init; } = "";
    /// <summary>text or choice; never an executable or sensitive input type.</summary>
    public string Kind { get; init; } = "text";
    /// <summary>Public plain-text field label.</summary>
    public string Label { get; init; } = "";
    /// <summary>Optional plain-text hint for a text field.</summary>
    public string Placeholder { get; init; } = "";
    /// <summary>Public initial/current field value; never credentials or entity identities.</summary>
    public string Value { get; init; } = "";
    /// <summary>Submitted text minimum, zero for choices. Empty initial text remains allowed.</summary>
    public int MinLength { get; init; }
    /// <summary>At most 256 UTF-16 code units, enforced by the host.</summary>
    public int MaxLength { get; init; } = 128;
    /// <summary>One to sixteen explicit choices, empty for text.</summary>
    public IReadOnlyList<PlatformPageQueryOption> Options { get; init; } = [];
}

/// <summary>A read-only navigation action; it cannot request playback, account writes or native execution.</summary>
public sealed record PlatformPageAction(string Label, string Route, string? State = null)
{
    /// <summary>Version-five explicit same-provider entity navigation. Backend-only; no playback authority.</summary>
    public PlatformPageTarget? Target { get; init; }
}

/// <summary>A declared main-page entry and typed entity. Never serialized to the main WebView.
/// Route/state on the containing action belong to the target page. Query values are not inherited.</summary>
public sealed record PlatformPageTarget(string EntryId, PlatformEntityId Entity, string ContextKind);

/// <summary>Version-eight plain text or inline artwork with required text fallback. Never markup or a link.</summary>
public sealed record PlatformPageTextRun(string Text, Uri? Image = null);

/// <summary>Version-eight, one-level quotation. No recursive quote, media playback or inferred comment ownership.</summary>
public sealed record PlatformPageQuote
{
    /// <summary>available or unavailable; unavailable content has no images or actions.</summary>
    public string Status { get; init; } = "available";
    /// <summary>Optional plain-text heading.</summary>
    public string Title { get; init; } = "";
    /// <summary>Complete plain-text fallback, at most 32000 code units.</summary>
    public string Text { get; init; } = "";
    /// <summary>At most 128 runs; concatenated fallback must equal Text.</summary>
    public IReadOnlyList<PlatformPageTextRun> Body { get; init; } = [];
    /// <summary>Original author's display name, not the sharer.</summary>
    public string Author { get; init; } = "";
    /// <summary>Public author artwork, subject to host authorization.</summary>
    public Uri? Avatar { get; init; }
    /// <summary>Explicit read-only navigation to the original author.</summary>
    public PlatformPageAction? AuthorAction { get; init; }
    /// <summary>Original publication instant.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Original public gallery, at most nine items.</summary>
    public IReadOnlyList<Uri> Images { get; init; } = [];
    /// <summary>Explicit original comment subject, never inherited from the sharing post.</summary>
    public PlatformEntityId? Discussion { get; init; }
    /// <summary>Optional nonnegative comment count.</summary>
    public long? CommentCount { get; init; }
}

/// <summary>Public display content. All text is plain text; images go through the approved artwork policy.</summary>
public sealed record PlatformPageCard
{
    /// <summary>Version-eight structured body; when present its concatenated text must equal Text.</summary>
    public IReadOnlyList<PlatformPageTextRun> Body { get; init; } = [];
    /// <summary>Version-eight read-only navigation associated with the attributed author.</summary>
    public PlatformPageAction? AuthorAction { get; init; }
    /// <summary>Version-eight optional single-level quotation.</summary>
    public PlatformPageQuote? Quote { get; init; }
    /// <summary>Version-seven explicit primary read-only navigation. Never inferred from action order or labels.</summary>
    public PlatformPageAction? Open { get; init; }
    /// <summary>Version-two stable provider-local key, projected as an opaque host handle.</summary>
    public string? Id { get; init; }
    /// <summary>Optional author attribution and public avatar.</summary>
    public string Author { get; init; } = "";
    /// <summary>Optional public author avatar.</summary>
    public Uri? Avatar { get; init; }
    /// <summary>Version-two public gallery, at most nine images.</summary>
    public IReadOnlyList<Uri> Images { get; init; } = [];
    /// <summary>Optional read-only comment subject; must belong to this provider.</summary>
    public PlatformEntityId? Discussion { get; init; }
    /// <summary>Optional nonnegative visible-comment count.</summary>
    public long? CommentCount { get; init; }
    /// <summary>Optional nonnegative public like count.</summary>
    public long? LikeCount { get; init; }
    /// <summary>Optional nonnegative public repost count.</summary>
    public long? RepostCount { get; init; }
    /// <summary>Display heading.</summary>
    public string Title { get; init; } = "";
    /// <summary>Plain text with paragraph breaks.</summary>
    public string Text { get; init; } = "";
    /// <summary>Optional public artwork, never a media lease.</summary>
    public Uri? Image { get; init; }
    /// <summary>Optional publication instant.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Version-six normalized media metadata. Browsing never resolves audio; only explicit host
    /// Play / Add to queue controls use this reference. Must belong to this provider.</summary>
    public PlatformTrack? Media { get; init; }
    /// <summary>Read-only navigation buttons.</summary>
    public IReadOnlyList<PlatformPageAction> Actions { get; init; } = [];
}

/// <summary>Version-one Fluent document; supports cards/list layout and navigation/pagination.</summary>
public sealed record PlatformPageDocument
{
    /// <summary>At most 24 identity filters, with exactly one selected; absent on append.</summary>
    public IReadOnlyList<PlatformPageNavigationItem> Navigation { get; init; } = [];
    /// <summary>Optional visible-page update checks. Not a background subscription or account write.</summary>
    public PlatformPageUpdates? Updates { get; init; }
    /// <summary>Version-four read-only query; absent on incremental batches.</summary>
    public PlatformPageQuery? Query { get; init; }
    /// <summary>Version-three section navigation, at most eight tabs; absent on incremental batches.</summary>
    public IReadOnlyList<PlatformPageTab> Tabs { get; init; } = [];
    /// <summary>Version-two public introductory artwork/avatar.</summary>
    public Uri? Image { get; init; }
    /// <summary>Read-only continuation appended to this collection; never an executable command.</summary>
    public PlatformPageAction? Next { get; init; }
    /// <summary>Renderer schema, independent of the provider package version.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Display title.</summary>
    public string Title { get; init; } = "";
    /// <summary>Optional plain-text introduction.</summary>
    public string Description { get; init; } = "";
    /// <summary>cards/list; version seven also allows chronological feed and single-item detail.
    /// Detail presents the card's discussion in-page, not as a modal. No implicit playback or account writes.</summary>
    public string Layout { get; init; } = "cards";
    /// <summary>Bounded page contents; long collections use navigation actions.</summary>
    public IReadOnlyList<PlatformPageCard> Cards { get; init; } = [];
    /// <summary>Page-level navigation, including provider-defined next-page/filter links.</summary>
    public IReadOnlyList<PlatformPageAction> Actions { get; init; } = [];
}
