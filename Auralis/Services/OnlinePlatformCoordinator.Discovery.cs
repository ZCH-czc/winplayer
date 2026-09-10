using Auralis.Platform.Abstractions;
using Auralis.Platform.Host;

namespace Auralis.Services;

internal sealed partial class OnlinePlatformCoordinator
{
    // Plugin metadata may be malformed even when the DLL implements the right interfaces.
    // Never let a result manufacture handles which route a later user action to another source.
    private static bool OwnsEntity(string providerId, PlatformEntityId id) =>
        id.IsForProvider(providerId) && !string.IsNullOrWhiteSpace(id.Value);

    private static bool OwnsTrack(string providerId, PlatformTrack? track) =>
        track is not null && OwnsEntity(providerId, track.Id) &&
        (track.MusicVideo is null || OwnsEntity(providerId, track.MusicVideo.Id));

    private static PlatformResult<T> InvalidResultIdentity<T>() =>
        PlatformResult<T>.Failure(PlatformErrorCode.InvalidResponse, "插件返回的内容标识无效或与当前来源不一致。");

    private async Task<PlatformResult<PlatformProviderManifest>> ResolveProviderAsync(
        string providerId, PlatformCapabilityKind capability, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var registration = (await _backend.DiscoverAsync(token).ConfigureAwait(false)).Providers
            .FirstOrDefault(p => p.Provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (registration is null || _backend.IsPluginDisabled(registration.PluginId) || !registration.Provider.Capabilities.Contains(capability))
            return PlatformResult<PlatformProviderManifest>.Failure(PlatformErrorCode.Unsupported, "插件未启用或未提供此功能。");
        var settings = await _backend.ReadSettingsAsync(registration, token).ConfigureAwait(false);
        if (settings.Any(s => s.Required && s.Value.Length == 0))
            return PlatformResult<PlatformProviderManifest>.Failure(PlatformErrorCode.ConfigurationRequired, "请先完成此插件的必填设置。");
        return PlatformResult<PlatformProviderManifest>.Success(registration.Provider);
    }

    // Shared by configuration projection and tests, without WPF or platform-specific account assumptions.
    internal async Task<OnlineProviderCollections> ReadProviderCollectionsAsync(PlatformProviderManifest provider, bool configured, CancellationToken token)
    {
        if (!configured) return new(null, [], null);
        PlatformAuthenticationState? authentication = null;
        if (provider.Capabilities.Contains(PlatformCapabilityKind.Authentication))
        {
            var auth = await _backend.Router.GetAuthenticationStateAsync(provider.Id, token).ConfigureAwait(false);
            if (!auth.IsSuccess) return new(null, [], auth.Error);
            authentication = auth.Value;
            if (authentication.Status != PlatformAuthenticationStatus.SignedIn) return new(authentication, [], null);
        }
        if (!provider.Capabilities.Contains(PlatformCapabilityKind.PlaylistBrowse)) return new(authentication, [], null);
        var collections = await GetPlaylistsAsync(provider.Id, token).ConfigureAwait(false);
        return collections.IsSuccess ? new(authentication, collections.Value, null) : new(authentication, [], collections.Error);
    }
}

internal sealed record OnlineProviderCollections(PlatformAuthenticationState? Authentication, IReadOnlyList<OnlinePlaylistView> Playlists, PlatformError? Error);
