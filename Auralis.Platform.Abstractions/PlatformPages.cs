namespace Auralis.Platform.Abstractions;

/// <summary>Optional provider-level pages, independent of any media or creator entity.</summary>
public interface IPlatformGlobalPagesCapability
{
    /// <summary>Explicit user navigation only; never invoked during discovery.</summary>
    Task<PlatformResult<PlatformPageDocument>> ReadGlobalPageAsync(PlatformGlobalPageReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Backend-only global navigation. No fabricated media ID or credentials.</summary>
public sealed record PlatformGlobalPageReadRequest(string ProviderId, string Route, string? State, string Language)
{
    /// <summary>Host-validated, read-only query values; never credentials or executable commands.</summary>
    public IReadOnlyDictionary<string, string> InputValues { get; init; } = new Dictionary<string, string>();
}

/// <summary>Version-three read-only tab. Exactly one tab in a nonempty group must be selected.</summary>
public sealed record PlatformPageTab(PlatformPageAction Action, bool Selected = false);

/// <summary>Optional read-only, declarative pages. No HTML, scripts or native commands.</summary>
public interface IPlatformPagesCapability
{
    /// <summary>Reads a page for a host-owned media context. Route and state never go to the WebView.</summary>
    Task<PlatformResult<PlatformPageDocument>> ReadPageAsync(PlatformPageReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Backend-only navigation. State is an opaque, non-credential continuation owned by this provider.</summary>
public sealed record PlatformPageReadRequest(PlatformEntityId MediaId, string Route, string? State, string Language)
{
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

/// <summary>Public display content. All text is plain text; images go through the approved artwork policy.</summary>
public sealed record PlatformPageCard
{
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
    /// <summary>Either cards or list.</summary>
    public string Layout { get; init; } = "cards";
    /// <summary>Bounded page contents; long collections use navigation actions.</summary>
    public IReadOnlyList<PlatformPageCard> Cards { get; init; } = [];
    /// <summary>Page-level navigation, including provider-defined next-page/filter links.</summary>
    public IReadOnlyList<PlatformPageAction> Actions { get; init; } = [];
}
