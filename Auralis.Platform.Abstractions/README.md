# Auralis.Platform.Abstractions

Development 1.11.0 adds optional PlatformPageCard.Media and PlatformCreatorPost.Media using existing PlatformTrack metadata.
Pages v6 / Host SDK 2.10 explicitly separates inert page reading from host-owned Play / Add to queue.
No raw lease, command or UI code is added. API 1 / assembly 1.1.0.0 remain stable; earlier constructors stay compatible.
See [page contract](../docs/PLUGIN_PAGES.md).

Development 1.10.0 adds optional PlatformPageAction.Target and PlatformPageTarget for bounded,
same-provider read-only media/creator main-page navigation. Existing constructors and assembly 1.1.0.0
remain unchanged. Require Host SDK 2.9 / Pages v5 on source entries; no playback or account-write authority.
See [page contract](../docs/PLUGIN_PAGES.md).

Development 1.9.0 adds PlatformPageQuery/Field/Option and optional Query/InputValues properties.
The vocabulary supports read-only text and explicit choices, not account writes or arbitrary controls.
Existing constructors, API 1 and assembly identity 1.1.0.0 are unchanged. Require Host SDK 2.7 / Pages v4.

Development 1.8.0 adds IPlatformGlobalPagesCapability and PlatformGlobalPageReadRequest without changing
the existing media request constructor. Pages v3 adds bounded PlatformPageTab records. Global pages do not
fabricate a media entity. API 1 and assembly version 1.1.0.0 remain unchanged; require Host SDK 2.6 features.

Development 1.7.0 adds optional Pages v2 fields: creator context, stable cards, galleries, attribution,
discussion subjects and incremental continuation. Existing constructors/assembly identity remain stable.
Plugins must declare documentVersion 2 and declarative-pages.v2; details are in docs/PLUGIN_PAGES.md.

Development 1.6.0 adds optional ICreatorSearchCapability. Search results are creator profiles, not playable tracks.
Its new capability enum is appended; assembly identity and existing interface members are unchanged.

Development 1.5.0 adds IPlatformPagesCapability and bounded read-only page documents/actions.
See [page contract](../docs/PLUGIN_PAGES.md); no UI scripts or native commands are accepted.

Development 1.4.0 adds independent CreatorProfile, alongside 1.3.0 CreatorFeed and CommentReplies.
Profile-only plugins need not implement a pretend activity feed. Assembly identity remains 1.1.0.0.

Version 1.2.0 adds `ITrackDetailsCapability`, `IPlatformNativeLoginCapability` /
`IPlatformNativeLoginSession`, and `IPlatformLyricsLookupCapability`. API 1 and assembly identity
1.1.0.0 are retained. Native login owns only a lifecycle and native owner handle, not WPF types or credentials.
Local-song lyric matching receives title/artist/album/duration only, never a library path.

Dependency-free contracts shared by the Auralis backend and trusted optional online-platform plugins.
The package defines provider-qualified online entities, fine-grained capability interfaces, typed results
and errors, backend-only stream/artwork leases, and the narrow host-service context available to plugins.

These models are intentionally separate from Auralis' local `TrackInfo`, library file, queue, and native
playback messages. A plugin must never reinterpret an online entity ID as a local path or expose signed
URLs, credentials, cookies, or stream request headers to WebView code or ordinary logs.

`PlatformStreamLease.Quality` reports the granted stream quality. Providers with discrete tiers can also
populate `RequestedQualityId`, `RequestedQualityDisplayName`, and `UsedQualityFallback`; the host may expose
only this non-sensitive quality summary to the UI while keeping the lease URL and headers backend-only.

Authentication state and challenges intentionally contain no token or cookie fields. Provider credentials remain
behind `IPlatformCredentialStore` and are never part of online entity models.

When `CreateProvidersAsync` succeeds, ownership of the returned providers transfers to the host. The host
disposes each provider before disposing the plugin entry point.
