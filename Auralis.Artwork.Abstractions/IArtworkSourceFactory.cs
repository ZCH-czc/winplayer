namespace Auralis.Artwork;

[Flags]
public enum ArtworkCapabilities
{
    None = 0, PublicHttp = 1, RedirectAuthorization = 2, BoundedPayload = 4,
    Cancellation = 8, IndependentResources = 16, Full = 31
}

public sealed record ArtworkDescriptor(string Id, string DisplayName, Version ComponentVersion,
    int ApiVersion, Version MinimumHostVersion, ArtworkCapabilities Capabilities);

/// <summary>Explicit, trusted native registration. Creating a source must not fetch images, inspect
/// accounts or access user files. Each returned source is independently owned by the requesting host.</summary>
public interface IArtworkSourceFactory : IAsyncDisposable
{
    ArtworkDescriptor Descriptor { get; }
    IArtworkSource Create();
}
