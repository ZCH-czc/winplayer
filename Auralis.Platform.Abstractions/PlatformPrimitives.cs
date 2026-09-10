using System.Diagnostics.CodeAnalysis;

namespace Auralis.Platform.Abstractions;

/// <summary>
/// Uniquely identifies an online entity within its provider. A provider-qualified identifier must
/// be kept intact when it is cached or passed between capabilities so IDs from different services
/// can never collide.
/// </summary>
public readonly record struct PlatformEntityId
{
    /// <summary>
    /// Initializes a provider-qualified entity identifier.
    /// </summary>
    /// <param name="providerId">Stable provider identifier.</param>
    /// <param name="value">Provider-owned opaque identifier.</param>
    public PlatformEntityId(string providerId, string value)
    {
        ProviderId = PlatformIdRules.EnsureScopedId(providerId, nameof(providerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An entity identifier cannot exceed 4096 characters.");
        }

        Value = value;
    }

    /// <summary>Gets the provider that owns the entity.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the opaque identifier understood only by the owning provider.</summary>
    public string Value { get; }

    /// <summary>Returns whether this entity belongs to <paramref name="providerId"/>.</summary>
    public bool IsForProvider(string providerId) =>
        string.Equals(ProviderId, providerId, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => $"{ProviderId}:{Value}";
}

/// <summary>Describes a cursor-based page request.</summary>
public sealed record PlatformPageRequest
{
    /// <summary>Creates a page request.</summary>
    /// <param name="pageSize">Requested number of items, from 1 through 200.</param>
    /// <param name="cursor">Opaque cursor returned by the preceding request, or <see langword="null"/> for the first page.</param>
    public PlatformPageRequest(int pageSize = 50, string? cursor = null)
    {
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 200.");
        }

        PageSize = pageSize;
        Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor;
    }

    /// <summary>Gets the requested maximum number of items.</summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets an opaque provider cursor. Hosts must not parse, concatenate, or reuse it with another provider.
    /// </summary>
    public string? Cursor { get; }
}

/// <summary>Represents a provider page and the cursor for continuing it.</summary>
/// <typeparam name="T">Entity type in the page.</typeparam>
public sealed record PlatformPage<T>
{
    /// <summary>Creates a platform page.</summary>
    /// <param name="items">Items in provider-defined order.</param>
    /// <param name="nextCursor">Opaque cursor for the next page, if one exists.</param>
    /// <param name="totalCount">Optional provider-reported total.</param>
    public PlatformPage(IReadOnlyList<T> items, string? nextCursor = null, long? totalCount = null)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        NextCursor = string.IsNullOrWhiteSpace(nextCursor) ? null : nextCursor;
        TotalCount = totalCount;
    }

    /// <summary>Gets the items in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Gets the provider-owned cursor for the next page.</summary>
    public string? NextCursor { get; }

    /// <summary>Gets the optional total count. Providers may omit it.</summary>
    public long? TotalCount { get; }

    /// <summary>Gets whether another page is available.</summary>
    public bool HasMore => NextCursor is not null;
}

/// <summary>Describes a text search and its page.</summary>
public sealed record PlatformSearchRequest
{
    /// <summary>Creates a search request.</summary>
    public PlatformSearchRequest(string query, PlatformPageRequest? page = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        Query = query;
        Page = page ?? new PlatformPageRequest();
    }

    /// <summary>Gets the user-entered query.</summary>
    public string Query { get; }

    /// <summary>Gets cursor and page-size options.</summary>
    public PlatformPageRequest Page { get; }
}

/// <summary>Enumerates stable, host-understood failure categories.</summary>
public enum PlatformErrorCode
{
    /// <summary>The request was malformed or contained an ID belonging to another provider.</summary>
    InvalidRequest,

    /// <summary>The provider does not implement the requested operation.</summary>
    Unsupported,

    /// <summary>The requested entity does not exist.</summary>
    NotFound,

    /// <summary>User authentication is required or has expired.</summary>
    AuthenticationRequired,

    /// <summary>The authenticated user is not allowed to perform the operation.</summary>
    Forbidden,

    /// <summary>The content is unavailable in the user's region.</summary>
    RegionRestricted,

    /// <summary>The provider requires a subscription or purchase.</summary>
    SubscriptionRequired,

    /// <summary>The content is known but cannot currently be played or retrieved.</summary>
    ContentUnavailable,

    /// <summary>The provider rejected the request because a rate limit was exceeded.</summary>
    RateLimited,

    /// <summary>No usable network connection was available.</summary>
    NetworkUnavailable,

    /// <summary>The operation exceeded its allowed time.</summary>
    Timeout,

    /// <summary>The remote provider is temporarily unavailable.</summary>
    ServiceUnavailable,

    /// <summary>The provider returned data that could not be safely interpreted.</summary>
    InvalidResponse,

    /// <summary>Required non-sensitive plugin configuration is missing or invalid.</summary>
    ConfigurationRequired,

    /// <summary>The operation was cancelled by the caller.</summary>
    Cancelled,

    /// <summary>The operation conflicted with current remote state.</summary>
    Conflict,

    /// <summary>An uncategorized provider failure occurred.</summary>
    Unknown
}

/// <summary>
/// Carries a sanitized platform failure. Messages and provider codes must never contain credentials,
/// signed stream URLs, request headers, cookies, or other secrets because hosts may display or log them.
/// </summary>
public sealed record PlatformError
{
    /// <summary>Creates a sanitized error.</summary>
    public PlatformError(
        PlatformErrorCode code,
        string message,
        bool isTransient = false,
        TimeSpan? retryAfter = null,
        string? providerCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
        ProviderCode = providerCode;
    }

    /// <summary>Gets the stable host-understood code.</summary>
    public PlatformErrorCode Code { get; }

    /// <summary>Gets a sanitized, user-safe description.</summary>
    public string Message { get; }

    /// <summary>Gets whether retrying later may succeed without user action.</summary>
    public bool IsTransient { get; }

    /// <summary>Gets an optional provider-recommended retry delay.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets an optional non-sensitive provider-specific diagnostic code.</summary>
    public string? ProviderCode { get; }
}

/// <summary>Represents either a successful value or a sanitized platform failure.</summary>
/// <typeparam name="T">Successful value type.</typeparam>
public readonly struct PlatformResult<T>
{
    private readonly T? _value;

    private PlatformResult(T value)
    {
        _value = value;
        Error = null;
        IsSuccess = true;
    }

    private PlatformResult(PlatformError error)
    {
        _value = default;
        Error = error ?? throw new ArgumentNullException(nameof(error));
        IsSuccess = false;
    }

    /// <summary>Gets whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the successful value, throwing <see cref="InvalidOperationException"/> when the result failed.
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed platform result does not contain a value.");

    /// <summary>Gets the failure, or <see langword="null"/> on success.</summary>
    public PlatformError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static PlatformResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new PlatformResult<T>(value);
    }

    /// <summary>Creates a failed result.</summary>
    public static PlatformResult<T> Failure(PlatformError error) => new(error);

    /// <summary>Creates a failed result from individual error values.</summary>
    public static PlatformResult<T> Failure(
        PlatformErrorCode code,
        string message,
        bool isTransient = false,
        TimeSpan? retryAfter = null,
        string? providerCode = null) =>
        new(new PlatformError(code, message, isTransient, retryAfter, providerCode));

    /// <summary>Attempts to retrieve the successful value.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess;
    }
}

/// <summary>Represents a successful operation that has no payload.</summary>
public readonly record struct PlatformUnit
{
    /// <summary>Gets the single unit value.</summary>
    public static PlatformUnit Value { get; } = new();
}
