using System.Collections.ObjectModel;

namespace Auralis.Platform.Abstractions;

/// <summary>Requests a short-lived stream for one provider-owned music video.</summary>
public sealed record PlatformVideoPlaybackRequest
{
    /// <summary>Creates a video playback request.</summary>
    public PlatformVideoPlaybackRequest(PlatformEntityId videoId)
    {
        VideoId = videoId;
    }

    /// <summary>Gets the provider-qualified video ID.</summary>
    public PlatformEntityId VideoId { get; }
}

/// <summary>
/// A short-lived backend authorization to play a music video. The URL and headers are sensitive and
/// must never be serialized into a WebView, diagnostics export, clipboard, or ordinary application log.
/// </summary>
public sealed record PlatformVideoLease
{
    /// <summary>Provider-approved alternate CDN URLs for the same representation, backend-only.</summary>
    public IReadOnlyList<Uri> AlternateUrls { get; init; } = Array.Empty<Uri>();
    /// <summary>Requests host transport without platform-name checks in the player.</summary>
    public bool UseHostTransport { get; init; } = true;
    /// <summary>Optional independent audio representation, consumed only by the native backend.</summary>
    public PlatformStreamLease? AudioStream { get; init; }
    /// <summary>Creates a backend-only music-video lease.</summary>
    public PlatformVideoLease(
        Uri url,
        DateTimeOffset? expiresAt,
        string? mimeType,
        string? formatLabel = null,
        int? width = null,
        int? height = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("A video URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        Url = url;
        ExpiresAt = expiresAt;
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
        FormatLabel = string.IsNullOrWhiteSpace(formatLabel) ? null : formatLabel;
        Width = width is > 0 ? width : null;
        Height = height is > 0 ? height : null;
        RequestHeaders = new ReadOnlyDictionary<string, string>(
            requestHeaders is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gets the signed backend-only video URL.</summary>
    public Uri Url { get; }

    /// <summary>Gets the instant after which this lease must be refreshed.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the provider MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Gets a non-sensitive provider format label.</summary>
    public string? FormatLabel { get; }

    /// <summary>Gets the reported video width.</summary>
    public int? Width { get; }

    /// <summary>Gets the reported video height.</summary>
    public int? Height { get; }

    /// <summary>Gets backend-only request headers required for playback.</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }
}
