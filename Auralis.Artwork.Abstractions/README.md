# Artwork contracts 0.2.0

Adds `IArtworkSourceFactory`, immutable component metadata/capabilities and asynchronous source disposal.
The default async implementation preserves disposal for simple existing sources; the Host wrapper adds
creation/request drainage and factory ownership. Factory creation receives no request/credentials/UI context.

Public .NET 8 contracts with no player, platform, Windows, decoder or HTTP implementation dependency.
`IArtworkSource.FetchAsync` explicitly resolves a backend-only `ArtworkRequest` to a complete owned
`ArtworkPayload`. The request URI and optional per-hop destination predicate are excluded from JSON and
diagnostic formatting. They must never be forwarded to the main WebView, even in another wrapper DTO.

The default payload cap is 16 MiB; caller limits retain the previous proxy's clamp to 1..16 MiB. The host
currently supplies 2 MiB plus its existing destination predicate for comment artwork. No cookies, signed
stream headers, provider ID, host window, application service container or file path belongs in this contract.

Payloads clone their input bytes once and expose independent read-only, non-public-buffer streams. A stream
outlives the fetching source; disposing it cannot dispose another reader. MIME strings are derived from a
closed enum: JPEG (including incoming image/jpg), PNG, WebP, GIF and BMP. This is not image decoding or image
signature validation: a platform claiming a MIME type may still supply undecodable data. Existing browser/OS
decoders retain that responsibility.

Caller cancellation is identified by the caller token. `ArtworkException` provides fixed failure codes,
without raw URI/network inner exceptions. Disposal rejects future work and signals current requests; the
interface does not promise synchronous worker drainage or isolation from malicious in-process code.

This is **not yet** a user-installable image plugin system. Explicit factories and lifecycle composition are
implemented in Artwork.Host; discovery/approval of external implementations, global in-flight memory budget
and cache-service extraction remain follow-up work.
