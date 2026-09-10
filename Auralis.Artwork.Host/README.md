# Artwork host 0.1.0

Explicit trusted factory composition, depending only on Artwork.Abstractions 0.2.0 (contract API 1).
No filesystem discovery, archive installation, HTTP, credentials, platform IDs, UI or decoder dependencies.

`ArtworkComponentComposition` accepts a bounded set of explicit registrations, an independent bundled
registration, one immutable preferred ID and required capabilities. Inspection is metadata-only. An empty
preference always selects the bundled registration, even if an optional registration has the same ID.
Metadata before first use represents the startup choice, not proof of a loaded factory.

`CreateDeferred()` returns an independently owned source without invoking factories. The first non-cancelled
Fetch creates its factory/source on the pool, outside the UI thread. Concurrent callers share that creation,
but each has independent cancellation. One caller leaving does not cancel another or dispose the source.
Creation is serialized across sources within a composition so managed optional failures latch once.
Version/capability/descriptor/ownership failures fall back to the bundled source **before** any image request;
network or payload failure is never replayed through another component.

Sources and factories cannot be returned to two owners, including across compositions. A rejected reused
instance is not disposed by the rejecting caller. Factories must not dispose sources they returned: source
ownership has transferred to the host. All implementations are trusted in-process code, not a security
sandbox; a malicious factory can violate that ownership contract.

DisposeAsync rejects new work, cancels requests, waits for any late creation, closes the source, drains active
calls, then disposes the factory. Every close caller shares completion. Fixed diagnostics replace raw creation,
request and disposal exception messages. Synchronous Dispose schedules the same cleanup and observes faults;
it does not synchronously wait for a slow factory on the UI thread. Payload streams remain independently
owned and usable after source closure.

A non-cooperative factory or source can hang; no thread termination/native-crash isolation is claimed.
The real app waits at most two seconds for artwork cleanup before continuing its existing shutdown path.
That waiting budget does not prove the component has stopped. It preserves application exit instead of
claiming forced cancellation succeeded.

Tests add 51 composition assertions to the existing 138 artwork checks. The real production-handler loopback
test now runs through this host and `HttpArtworkSourceFactory`, not direct construction. No real user state,
accounts, physical DPI or native user interface is exercised by those tests.

External image factory discovery/approval/selection is not installed into the app yet. The current production
composition supplies only its independent bundled default. Shared memory budget, cache extraction, local
image decode, resource-handle registry and platform-specific destination rules remain separate follow-ups.
