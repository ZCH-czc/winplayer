using System.Reflection;
using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.PluginContractCheck;

public sealed record ProbeReport(bool Passed, bool Executed, int Plugins, int Providers, int InterfaceChecks,
    int HttpRequests, string[] Diagnostics, string[] NotTested)
{
    public int LegacyCommentArtworkProviders { get; init; }
    public int DeclaredCommentArtworkProviders { get; init; }
    public int UpgradeRequiredPlugins { get; init; }
    public int MinimumRuntimeManifestVersion => PlatformPluginManifestSchema.MinimumRuntimeVersion;
    // Manifest compatibility is not install approval, activation, account or business readiness.
    public bool RuntimeManifestCompatible => Passed && Plugins > 0 && UpgradeRequiredPlugins == 0;
}

public static class PackageProbe
{
    private static readonly string[] Gaps = ["BusinessOperations", "CancellationCompliance", "LeaseLifecycle", "RealAccounts", "Playback", "NativeUI"];
    // Fixed contract types, never type names supplied by a manifest.
    private static readonly IReadOnlyDictionary<PlatformCapabilityKind, Type> Contracts = new Dictionary<PlatformCapabilityKind, Type>
    {
        [PlatformCapabilityKind.TrackSearch] = typeof(ITrackSearchCapability),
        [PlatformCapabilityKind.SearchSuggestions] = typeof(ISearchSuggestionsCapability),
        [PlatformCapabilityKind.HotSearch] = typeof(IHotSearchCapability),
        [PlatformCapabilityKind.AlbumSearch] = typeof(IAlbumSearchCapability),
        [PlatformCapabilityKind.AlbumDetails] = typeof(IAlbumDetailsCapability),
        [PlatformCapabilityKind.PlaylistBrowse] = typeof(IPlaylistBrowseCapability),
        [PlatformCapabilityKind.PlaylistSearch] = typeof(IPlaylistSearchCapability),
        [PlatformCapabilityKind.PlaylistDetails] = typeof(IPlaylistDetailsCapability),
        [PlatformCapabilityKind.Charts] = typeof(IChartCapability),
        [PlatformCapabilityKind.ArtistDetails] = typeof(IArtistDetailsCapability),
        [PlatformCapabilityKind.ArtistTracks] = typeof(IArtistTracksCapability),
        [PlatformCapabilityKind.ArtistAlbums] = typeof(IArtistAlbumsCapability),
        [PlatformCapabilityKind.Artwork] = typeof(IArtworkCapability),
        [PlatformCapabilityKind.Lyrics] = typeof(ILyricsCapability),
        [PlatformCapabilityKind.Comments] = typeof(ICommentsCapability),
        [PlatformCapabilityKind.StreamResolution] = typeof(IStreamResolutionCapability),
        [PlatformCapabilityKind.VideoResolution] = typeof(IPlatformVideoCapability),
        [PlatformCapabilityKind.Authentication] = typeof(IAuthenticationCapability),
        [PlatformCapabilityKind.MediaExtras] = typeof(IPlatformMediaExtrasCapability),
        [PlatformCapabilityKind.TrackDetails] = typeof(ITrackDetailsCapability),
        [PlatformCapabilityKind.NativeLogin] = typeof(IPlatformNativeLoginCapability),
        [PlatformCapabilityKind.LyricsLookup] = typeof(IPlatformLyricsLookupCapability)
    };

    public static async Task<ProbeReport> InspectAsync(string directory, CancellationToken token = default)
    {
        var result = await new PlatformPluginCatalog([directory]).DiscoverAsync(token).ConfigureAwait(false);
        var codes = result.Diagnostics.Select(d => d.Code.ToString()).Distinct().ToList();
        if (result.Plugins.Count == 0) codes.Add("NoPlugins");
        return new(codes.Count == 0, false, result.Plugins.Count, result.Plugins.Sum(p => p.Providers.Count), 0, 0,
            codes.ToArray(), ["RuntimeDescriptors", .. Gaps])
        {
            UpgradeRequiredPlugins = result.Plugins.Count(p => p.SchemaVersion < PlatformPluginManifestSchema.MinimumRuntimeVersion),
            LegacyCommentArtworkProviders = result.Plugins.Sum(p => p.Providers.Count(v => v.UsesLegacyCommentArtworkPolicy)),
            DeclaredCommentArtworkProviders = result.Plugins.Sum(p => p.Providers.Count(v => !v.UsesLegacyCommentArtworkPolicy))
        };
    }

    // Only the explicitly trusted worker calls this. It runs constructors/Initialize/Dispose, not login or search.
    public static async Task<ProbeReport> VerifyTrustedAsync(string directory)
    {
        var metadata = await InspectAsync(directory).ConfigureAwait(false);
        if (!metadata.Passed) return metadata;
        if (!metadata.RuntimeManifestCompatible) return RequireUpgrade(metadata);
        using var offline = new OfflineContextFactory();
        var host = new PlatformPluginHost(new([directory], offline, TimeSpan.FromSeconds(3), trustPolicy: (_, _) => Task.FromResult(true),
            minimumManifestSchemaVersion: PlatformPluginManifestSchema.MinimumRuntimeVersion));
        var snapshot = await host.DiscoverAsync().ConfigureAwait(false);
        var codes = new List<string>();
        var count = 0;
        try
        {
            foreach (var route in snapshot.Providers)
            foreach (var capability in route.Provider.Capabilities)
            {
                if (!Contracts.TryGetValue(capability, out var contract)) { codes.Add("UnknownContract"); continue; }
                var method = typeof(PackageProbe).GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(contract);
                var task = (Task<bool>)method.Invoke(null, [host.Router, route.Provider.Id])!;
                if (await task.ConfigureAwait(false)) count++;
                else codes.Add("InterfaceRouteFailed");
            }
        }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
        codes.AddRange(host.Diagnostics.Select(d => d.Code.ToString()));
        if (snapshot.Plugins.Count == 0) codes.Add("NoPlugins");
        if (offline.HttpRequests != 0) codes.Add("UnexpectedNetworkDuringActivation");
        return new(codes.Count == 0, true, snapshot.Plugins.Count, snapshot.Providers.Count, count, offline.HttpRequests,
            codes.Distinct().ToArray(), Gaps)
        {
            LegacyCommentArtworkProviders = snapshot.Providers.Count(p => p.Provider.UsesLegacyCommentArtworkPolicy),
            DeclaredCommentArtworkProviders = snapshot.Providers.Count(p => !p.Provider.UsesLegacyCommentArtworkPolicy)
        };
    }

    public static ProbeReport RequireUpgrade(ProbeReport metadata) => metadata with
    {
        Passed = false,
        Diagnostics = [.. metadata.Diagnostics, nameof(PlatformPluginDiagnosticCode.ManifestUpgradeRequired)]
    };

    private static async Task<bool> Check<T>(PlatformRouter router, string provider) where T : class =>
        (await router.RouteAsync<T, PlatformUnit>(provider, (_, _) => Task.FromResult(
            PlatformResult<PlatformUnit>.Success(PlatformUnit.Value))).ConfigureAwait(false)).IsSuccess;

    public static bool CoversAllCapabilities => Enum.GetValues<PlatformCapabilityKind>().All(Contracts.ContainsKey);
}
