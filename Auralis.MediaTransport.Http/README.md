# HTTP media transport — experimental 0.6.0 (contracts 0.4.0)

Independent default implementation of `IMediaTransportSession`; references only MediaTransport.Abstractions.
The app's OnlinePlaybackSourceService is a platform-lease/error-message adapter. MediaTransportServices
is the sole concrete composition point, using the separate Host registry and deferred bundled factory.
MainWindow, audio/video switching and Web assets retain their existing protocol.

`HttpMediaTransportFactory` exposes inert metadata (API 1/minimum Host 0.1.0/Full capabilities), creates
fresh sessions from the host's context and shared IMediaTransferBudget, and does not own that budget.
An optional diagnostic handler supplier runs only at explicit factory Create. Factory and session
versions must be rebuilt together; metadata is a declaration, not evidence of arbitrary plugin compliance.
0.6.0 adds a genuine public zero-argument CLR constructor for approved external-package activation.
The handler-supplier constructor remains available; existing production transport behavior is unchanged.

Host supplies an explicit cache directory; the default constructor creates a proxy-aware HTTP handler
with automatic redirects and cookie storage disabled. Injected handlers are owned/disposed by this session
and must obey the same redirect/cookie policy. Construction performs best-effort old-cache maintenance but
does not download or create the cache directory. No personal account store is read.

Preserved limits: complete foreground response <=512 MiB, speculative buffering <=64 MiB, session cache
8 entries/768 MiB soft eviction target (all pinned entries protected), <=5 validated redirect hops, HTTPS or loopback HTTP.
When every remaining entry is pinned this target can be exceeded; this is not an aggregate memory/disk quota.
Each successful preparation increments its own pin under the cache gate. Releasing one cache-hit handle
does not release other callers' pins. Last release allows eviction, but does not eagerly delete reusable cache.

`MediaTransferBudget` adds shared in-flight admission (no I/O/global state inside the component).
Default: 8 transfers / 1 GiB reserved bytes; speculative subset: 2 transfers / 128 MiB. The app's default
audio, prefetch, embedded video and audio-slave sessions share one instance; independently injected sessions
get their own budget unless a host explicitly shares it. These are backend policies, not new UI settings.
Reserve a transfer slot before HTTP, known Content-Length bytes before preallocation, and unknown-length
growth before writing. A failed reservation returns fixed `BudgetExceeded`, never waits while holding
partial reservations, never cancels another caller or evicts a pinned source. No automatic retry loop.
Commit, failure, cancellation and shutdown release the reservation after transfer/file cleanup. Committed
cache bytes then follow pin/LRU ownership, not the in-flight budget. Failed OS deletion, old files, direct
decoder networking, third-party handlers and other processes are not covered by this admission bound.
Cross-origin redirects drop lease headers. Incomplete 206 ranges, empty/truncated and oversized responses
are rejected. No private-platform URL guessing, sign-in or unauthorized retry is implemented.

Lifecycle improvement: every operation enters a shutdown scope. Dispose cancels/drains all scopes before
disposing HTTP/semaphore and removing owned cache files. Failure/cancellation after the final rename also
removes the uncommitted file. Expiry is checked at entry, cached return and after buffering; raw query/header
values never become filenames. Repeated disposal awaits the same completion task.

0.4.0 coalesces identical in-flight preparations within one session. Matching is deliberately stricter
than completed-cache reuse: authorized full URI, request headers, expiry, MIME/variant, cache identity,
host-transport flag and foreground/prefetch policy must match. Different policies/authorizations and
different sessions are not merged. Keys are memory-only hashes, never raw credentials in logs/indexes.
The shared worker owns a separate shutdown scope/token and a bridging cache pin. Cancelling one waiter
only removes that waiter; the last departing waiter removes the entry, cancels and drains the worker,
then releases its bridging pin. Successful waiters acquire independent cache pins before leaving.
Failed/abandoned work is not reused by a later explicit retry. Expiry is checked before joining and
before issuing each result. No automatic retry, authorization refresh or cross-session cache ownership.

Run `dotnet run --project Auralis.MediaTransport.Tests -c Release` for 174 offline assertions, including
mutable input snapshots, redirect/header boundaries, range/length/expiry failures, 12 cancellation drains
and concurrent drains, independent pins under eviction pressure, duplicate commits and idempotent/post-shutdown
release. Existing Auralis.Tests separately verifies the platform adapter's ownership transfer and authorized video
alternatives. These do not prove live network/CDN, decoder file locks or acoustic playback.
For the generated cache-to-decoder lifecycle, run `dotnet run --project tools/MediaEngineSmoke -c Release -- --run --transport`.
This uses the actual decoder with cached synthetic WAV, video slave, repeat and A/V switching, then verifies
source removal after decoder disposal. It remains silent/offline and is not real account/device acceptance.

BudgetTests adds 24 deterministic assertions for cross-session admission, known/unknown lengths,
prefetch slots/bytes, cancellation, delayed shutdown drain and HTTP failure recovery.

SharedTransferTests adds 28 assertions for 20 identical waiters, independent pins/cancellation,
last-waiter cleanup, safe shared failures/retry, authorization/policy boundaries, expiry and shutdown.
`--shared-only` runs this focused fixture for diagnosis; it does not replace the full suite.

Remaining: cross-session download coordination and a hard committed-cache/global disk quota,
manifest discovery/approval and user-selection integration, image transport extraction, and bounded history cleanup. A sharing violation
from a decoder/external process can still leave a file for later cleanup; this fix does not prove the old
intermittent service-test `.tmp` failure had the same cause. No installable transport ZIP or hot swap yet.
