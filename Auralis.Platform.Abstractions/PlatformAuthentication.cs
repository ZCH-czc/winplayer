namespace Auralis.Platform.Abstractions;

/// <summary>Describes the authentication flow selected by a provider.</summary>
public enum PlatformAuthenticationMode
{
    /// <summary>The host opens an authorization page in the system browser.</summary>
    ExternalBrowser,

    /// <summary>The provider supplies a short user code and verification page.</summary>
    DeviceCode,

    /// <summary>The provider supplies an authorization URI intended to be represented as a QR code.</summary>
    QrCode,

    /// <summary>The provider completes authentication through another host-approved interaction.</summary>
    ProviderManaged
}

/// <summary>Describes current authentication state without exposing tokens or account secrets.</summary>
public enum PlatformAuthenticationStatus
{
    /// <summary>No authenticated account exists.</summary>
    SignedOut,

    /// <summary>An authentication flow is waiting for user action.</summary>
    Pending,

    /// <summary>The provider has a usable authenticated account.</summary>
    SignedIn,

    /// <summary>Stored authentication exists but must be refreshed or repeated.</summary>
    Expired
}

/// <summary>Contains sanitized account state suitable for display by the host.</summary>
public sealed record PlatformAuthenticationState
{
    /// <summary>Gets the current state.</summary>
    public required PlatformAuthenticationStatus Status { get; init; }

    /// <summary>Gets an optional provider account display name.</summary>
    public string? AccountDisplayName { get; init; }

    /// <summary>Gets an optional provider-owned, non-secret account ID.</summary>
    public string? AccountId { get; init; }
}

/// <summary>Requests a host-compatible authentication flow.</summary>
public sealed record PlatformAuthenticationRequest
{
    /// <summary>Gets the host-preferred flow, or <see langword="null"/> to let the provider select.</summary>
    public PlatformAuthenticationMode? PreferredMode { get; init; }

    /// <summary>Gets an optional host-approved callback URI for external-browser flows.</summary>
    public Uri? CallbackUri { get; init; }
}

/// <summary>
/// Describes user action needed to authenticate. It must never contain access tokens, cookies, passwords,
/// or client secrets. Sensitive results belong directly in the scoped credential store.
/// </summary>
public sealed record PlatformAuthenticationChallenge
{
    /// <summary>Gets an opaque identifier used only to complete this challenge.</summary>
    public required string ChallengeId { get; init; }

    /// <summary>Gets the selected flow.</summary>
    public required PlatformAuthenticationMode Mode { get; init; }

    /// <summary>Gets an authorization or verification URI safe to open or encode as a QR code.</summary>
    public Uri? AuthorizationUri { get; init; }

    /// <summary>Gets an optional short user code.</summary>
    public string? UserCode { get; init; }

    /// <summary>Gets the challenge expiration instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets a sanitized instruction suitable for display.</summary>
    public string? Instruction { get; init; }
}

/// <summary>Supplies an external-browser completion signal without carrying provider tokens.</summary>
public sealed record PlatformAuthenticationCompletion
{
    /// <summary>Gets the opaque challenge identifier returned by the provider.</summary>
    public required string ChallengeId { get; init; }

    /// <summary>
    /// Gets an optional callback URI received by the host. Providers must sanitize it before logging and
    /// persist any resulting secret only through the credential store.
    /// </summary>
    public Uri? CallbackUri { get; init; }
}
