# Bundled HTTP artwork component 0.2.0

Adds `HttpArtworkSourceFactory` (contract API 1, minimum Host 0.1.0). Metadata/constructing the factory does
not create an HTTP source. The app uses the deferred Artwork.Host composition; first Fetch creates an
independently owned source. Disposing a factory does not close sources whose ownership was transferred.

Independent implementation of `Auralis.Artwork.Abstractions`; no platform/provider, UI, LibVLC, account or disk
storage dependency. `Auralis/Services/ArtworkServices.cs` is the only app composition point that names it.
The former app-local `OnlineArtworkProxyService` implementation has been removed, not retained behind a flag.

Production consumers are the WebView's opaque platform-art resource endpoint (covers, avatars, comment
emotes) and Windows system-media/taskbar artwork fetching. Their existing local handle authorization, cache
headers, cache filenames, file-length reuse and revision checks remain in the host. Local cover/artist/
background files are not routed through this HTTP component.

Behavior carried over: HTTPS or HTTP loopback only, no URI userinfo, maximum URI length 8192, four redirects,
image Accept header, existing User-Agent, two-MiB avatar/sixteen-MiB cover bounds, MIME allowlist, gzip/Brotli/
deflate, no cookies, per-server connection cap and caller timeout. Every redirect repeats generic and
host-supplied destination checks; automatic redirects are disabled. Injected handlers are only for explicit
owned fixtures and must honor the same no-cookie/no-automatic-redirect contract.

New integrity guard: unsolicited partial responses and mismatched content lengths are rejected instead of
publishing incomplete image bytes. The real-loopback test includes gzip to verify comparison uses decoded
content semantics. Cancellation and disposal stop in-flight body reads; failures map to fixed codes with no
network-message disclosure. No new HTTP retries or caching have been added.

`dotnet run --project Auralis.Artwork.Tests -c Release` covers 138 HTTP/contracts checks plus 51 Host composition
checks, including a real production-handler loopback redirect with Set-Cookie and gzip through the factory/Host.
All other HTTP tests use owned synthetic handlers. No real
accounts, public endpoints or user files are involved. This does not prove OS/browser visual rendering,
physical DPI or live-provider availability.

Still coupled: host comment-image destination domains, local image decode/metadata extraction, system-media
cover cache writes, WebView response projection and resource-handle registries. The current default is
explicitly composed via an independently versioned Host but not yet selectable/importable as an external image component.
