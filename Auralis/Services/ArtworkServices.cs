using Auralis.Artwork;
using Auralis.Artwork.Host;

namespace Auralis.Services;

/// <summary>Only the composition point names the concrete artwork implementation.
/// The bundled offline-capable player does not depend on optional provider installation.</summary>
internal static class ArtworkServices
{
    private static readonly ArtworkComponentComposition Default = new([], new(HttpArtworkSourceFactory.Metadata,
        true, static () => new HttpArtworkSourceFactory()));
    internal static IArtworkSource CreateDefault() => Default.CreateDeferred();
}
