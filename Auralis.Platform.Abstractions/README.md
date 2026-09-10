# Auralis.Platform.Abstractions

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
