namespace Auralis.MediaTransport;

[Flags]
public enum MediaTransportCapabilities
{
    None = 0, AuthorizedHttp = 1, CompleteBuffering = 2, Prefetch = 4,
    IndependentResources = 8, SharedBudget = 16, SharedRequests = 32,
    Full = AuthorizedHttp | CompleteBuffering | Prefetch | IndependentResources | SharedBudget | SharedRequests
}

public sealed record MediaTransportDescriptor(string Id, string DisplayName, Version ComponentVersion,
    int ApiVersion, Version MinimumHostVersion, MediaTransportCapabilities Capabilities);

/// <summary>Host-owned admission service shared across sessions. Reserve never waits for other
/// reservations. Ensure is the total bytes needed by this transfer, not a delta. Dispose is idempotent.</summary>
public interface IMediaTransferBudget
{
    IMediaTransferReservation Reserve(bool prefetch);
}
public interface IMediaTransferReservation : IDisposable
{
    void Ensure(long totalBytes);
}

/// <summary>Backend-only creation context. No platform account, decoder, window, or UI bridge access.</summary>
public sealed class MediaTransportContext
{
    public string CacheDirectory { get; }
    public IMediaTransferBudget Budget { get; }
    public MediaTransportContext(string cacheDirectory, IMediaTransferBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Path.IsPathFullyQualified(cacheDirectory)) throw new ArgumentException("Cache directory must be absolute.", nameof(cacheDirectory));
        CacheDirectory = Path.GetFullPath(cacheDirectory); Budget = budget;
    }
    public override string ToString() => nameof(MediaTransportContext);
}

/// <summary>Trusted in-process factory. Descriptor inspection is inert. Create must return a new
/// non-playing session without making network requests. Caller owns session then factory disposal.
/// Neither factory nor session owns the context's shared budget. No hot replacement or sandbox.</summary>
public interface IMediaTransportFactory : IAsyncDisposable
{
    MediaTransportDescriptor Descriptor { get; }
    IMediaTransportSession Create(MediaTransportContext context);
}
