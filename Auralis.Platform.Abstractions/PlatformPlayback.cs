using System.Collections.ObjectModel;

namespace Auralis.Platform.Abstractions;

/// <summary>Describes a provider-specific audio quality without locking the contract to one service's names.</summary>
public sealed record PlatformAudioQuality
{
    /// <summary>Creates an audio quality descriptor.</summary>
    public PlatformAudioQuality(
        string id,
        string displayName,
        int? bitrateKbps = null,
        string? codec = null,
        bool isLossless = false)
    {
        Id = PlatformIdRules.EnsureScopedId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName;
        BitrateKbps = bitrateKbps;
        Codec = codec;
        IsLossless = isLossless;
    }

    /// <summary>Gets the stable provider-specific quality ID.</summary>
    public string Id { get; }

    /// <summary>Gets the localized or user-friendly quality name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the nominal bitrate in kilobits per second.</summary>
    public int? BitrateKbps { get; }

    /// <summary>Gets an optional codec name.</summary>
    public string? Codec { get; }

    /// <summary>Gets whether the provider describes the quality as lossless.</summary>
    public bool IsLossless { get; }
}

/// <summary>
/// A short-lived backend authorization to fetch an audio stream.
/// </summary>
/// <remarks>
/// <para><b>Backend only:</b> this object, its URL, and its headers must never be serialized to a
/// WebView, frontend JavaScript, diagnostics export, clipboard, or ordinary application log.</para>
/// <para>The host should consume the lease in its trusted media/network backend, discard it after
/// expiry, and request a new lease when necessary. A plugin must not use this contract to bypass DRM,
/// payment, subscription, account, or regional restrictions.</para>
/// </remarks>
public sealed record PlatformStreamLease
{
    /// <summary>Creates a backend-only stream lease.</summary>
    public PlatformStreamLease(
        Uri url,
        DateTimeOffset? expiresAt,
        string? mimeType,
        PlatformAudioQuality quality,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("A stream URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        Url = url;
        ExpiresAt = expiresAt;
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
        Quality = quality ?? throw new ArgumentNullException(nameof(quality));
        RequestHeaders = new ReadOnlyDictionary<string, string>(
            requestHeaders is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the signed or otherwise authorized stream URL. This value is backend-only and may be sensitive.
    /// </summary>
    public Uri Url { get; }

    /// <summary>Gets the instant after which the lease must not be reused.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the provider-reported MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Gets the granted quality.</summary>
    public PlatformAudioQuality Quality { get; }

    /// <summary>
    /// Gets the provider-specific quality ID that was requested before any permitted fallback.
    /// This is optional for adaptive providers that do not expose discrete quality tiers.
    /// </summary>
    public string? RequestedQualityId { get; init; }

    /// <summary>Gets a user-facing name for the requested quality, when the provider can supply one.</summary>
    public string? RequestedQualityDisplayName { get; init; }

    /// <summary>
    /// Gets whether the provider granted a lower quality after the requested tier was unavailable.
    /// </summary>
    public bool UsedQualityFallback { get; init; }

    /// <summary>
    /// Requests the host's bounded HTTP transport even without custom headers. The host must not
    /// hand this URL directly to a decoder with a different proxy/network configuration.
    /// Backend-only; does not grant additional access or relax download/redirect limits.
    /// </summary>
    public bool UseHostTransport { get; init; }

    /// <summary>
    /// Gets request headers required to consume the stream. These may contain credentials and are backend-only.
    /// </summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }

    /// <summary>Returns whether the lease is expired according to the supplied host clock.</summary>
    public bool IsExpired(IPlatformTimeProvider timeProvider) =>
        ExpiresAt is { } expiry && expiry <= (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow();
}
