using System.Text.Json.Serialization;

namespace Auralis.Artwork;

/// <summary>Backend-only request. Neither the URI nor its destination policy belongs in a WebView DTO.
/// The host retains handle authorization and any stricter per-resource destination policy.</summary>
public sealed class ArtworkRequest
{
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;
    [JsonIgnore] public Uri Source { get; }
    [JsonIgnore] public Func<Uri, bool>? DestinationAllowed { get; }
    public int MaximumBytes { get; }

    public ArtworkRequest(Uri source, int maximumBytes = MaximumPayloadBytes, Func<Uri, bool>? destinationAllowed = null)
    {
        Source = source ?? throw new ArtworkException(ArtworkFailure.InvalidRequest);
        // Preserve the previous proxy's bounded limit semantics.
        MaximumBytes = Math.Clamp(maximumBytes, 1, MaximumPayloadBytes);
        DestinationAllowed = destinationAllowed;
    }
    public override string ToString() => "ArtworkRequest [backend resource]";
}

public enum ArtworkMediaType { Jpeg, Png, WebP, Gif, Bmp }

/// <summary>Complete, independently owned image bytes. Streams are read-only, non-publicly-visible,
/// independently positioned, and remain valid after the source is disposed. No URL/HTTP headers escape.</summary>
public sealed class ArtworkPayload
{
    private readonly byte[] _bytes;
    public int Length => _bytes.Length;
    public ArtworkMediaType MediaType { get; }
    public string ContentType => MediaType switch
    {
        ArtworkMediaType.Jpeg => "image/jpeg", ArtworkMediaType.Png => "image/png",
        ArtworkMediaType.WebP => "image/webp", ArtworkMediaType.Gif => "image/gif",
        ArtworkMediaType.Bmp => "image/bmp", _ => throw new ArtworkException(ArtworkFailure.UnsupportedMediaType)
    };
    public ArtworkPayload(ReadOnlySpan<byte> bytes, ArtworkMediaType mediaType)
    {
        if (bytes.Length == 0) throw new ArtworkException(ArtworkFailure.EmptyPayload);
        if (bytes.Length > ArtworkRequest.MaximumPayloadBytes) throw new ArtworkException(ArtworkFailure.TooLarge);
        if (!Enum.IsDefined(mediaType)) throw new ArtworkException(ArtworkFailure.UnsupportedMediaType);
        _bytes = bytes.ToArray();
        MediaType = mediaType;
    }
    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);
    public override string ToString() => $"ArtworkPayload [{ContentType}; {Length} bytes]";
}

public enum ArtworkFailure
{
    InvalidRequest, DestinationDenied, HttpFailure, TooLarge, UnsupportedMediaType,
    EmptyPayload, IncompletePayload, NetworkFailure, Disposed, ComponentUnavailable
}

/// <summary>Fixed diagnostic code; never wrap raw network exceptions, URIs or headers.</summary>
public sealed class ArtworkException(ArtworkFailure failure) : Exception($"Artwork request failed: {failure}.")
{
    public ArtworkFailure Failure { get; } = failure;
}

/// <summary>Fetch is explicit and cancellable. Disposal rejects new work and cancels in-flight reads.
/// Implementations supply no cookies, credentials, disk paths or UI types.</summary>
public interface IArtworkSource : IDisposable, IAsyncDisposable
{
    Task<ArtworkPayload> FetchAsync(ArtworkRequest request, CancellationToken cancellationToken = default);
    ValueTask IAsyncDisposable.DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
