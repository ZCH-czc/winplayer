using System.Collections.ObjectModel;

namespace Auralis.MediaTransport;

/// <summary>Backend-only authorized resource. Never serialize this request to UI, logs or disk indexes.
/// Providers authorize acquisition; transport neither signs in nor renews access on its own.</summary>
public sealed class MediaTransportRequest
{
    public Uri Url { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public string? MimeType { get; }
    public string VariantKey { get; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }
    public bool UseHostTransport { get; }
    public MediaTransportRequest(Uri url, DateTimeOffset? expiresAt, string? mimeType, string variantKey,
        IReadOnlyDictionary<string,string>? requestHeaders = null, bool useHostTransport = false)
    {
        ArgumentNullException.ThrowIfNull(url); ArgumentNullException.ThrowIfNull(variantKey);
        Url = url; ExpiresAt = expiresAt; MimeType = mimeType; VariantKey = variantKey; UseHostTransport = useHostTransport;
        RequestHeaders = new ReadOnlyDictionary<string,string>(requestHeaders is null ? new Dictionary<string,string>() : new Dictionary<string,string>(requestHeaders, StringComparer.OrdinalIgnoreCase));
    }
    public override string ToString() => nameof(MediaTransportRequest);
}

/// <summary>One backend transport/cache owner. Does not own the player or queue. A prepared resource is
/// backend-only and must not be exposed to Web UI. Each successful preparation owns an independent pin.
/// Prepare does not start playback. Dispose cancels and drains all cooperative in-flight operations before
/// deleting owned cache files; callers must first close any decoder reading them. No hot replacement.</summary>
public interface IMediaTransportSession : IAsyncDisposable
{
    Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request, string? cacheIdentity, CancellationToken cancellationToken, bool prefetch = false);
}

/// <summary>A caller-owned pin. Close the decoder before releasing it. Release is idempotent, including
/// after session disposal. Concurrent preparations may share a file, but never share pin ownership.
/// Source is only valid until release/session disposal; it must not enter UI payloads or logs.</summary>
public interface IMediaTransportResource : IAsyncDisposable
{
    Uri Source { get; }
}

public enum MediaTransportFailure { Expired, InvalidUri, Unavailable, Incomplete, IncompleteRange, TooLarge, InvalidRedirect, InvalidHeaders, TransportFailure, BudgetExceeded }
public sealed class MediaTransportException(MediaTransportFailure failure) : Exception("Media transport failed: " + failure)
{
    public MediaTransportFailure Failure { get; } = failure;
}
