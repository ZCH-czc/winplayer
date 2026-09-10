# Media transport contracts — experimental 0.4.0

`IMediaTransportSession` is a backend-only transport/cache boundary, independent of platform providers,
player engines, WPF and WebView. `MediaTransportRequest` snapshots request headers and carries a provider-
authorized URI, optional expiry, MIME type, variant identity and host-buffering preference. Never serialize
requests, returned source URIs or credential-bearing headers to UI or persistent indexes/logs.

- Prepare returns a caller-owned `IMediaTransportResource` with a backend-only `Source` URI; it never starts a decoder.
- The explicit cache identity must distinguish the authorized track/variant. Without it, the full URI
  (including query) and headers influence a memory-only hash. Expired requests are refused.
- Every successful preparation owns an independent pin, including cache hits and concurrent duplicate commits.
  Close the decoder before disposing its pin; a prepared/prefetched pin also needs disposal if not consumed.
  Keep the original audio pin when video playback may return to it. No global active/pending transport slot exists.
  Resource disposal is idempotent, including after session disposal. Never use Source after either disposal.
- Dispose is idempotent, rejects new work, cancels and drains all cooperative operations before releasing
  transport resources. Close any decoder holding returned files before disposing the session.
- Typed failures have fixed codes, not provider exceptions or URLs. The app maps codes to its existing UI text.
  0.3.0 appends `BudgetExceeded` for in-flight admission; preserve existing playback and let the user retry.
  Budget sharing/limits belong to the implementation/host composition, not platform authorization or Web UI.

0.2.0 intentionally replaces the experimental URI/Activate/Discard/Release API; rebuild consumers together.
0.4.0 adds `IMediaTransportFactory`, immutable descriptor/context and capability flags, plus shared
`IMediaTransferBudget`/reservation contracts. Context has only an absolute cache directory and budget,
not accounts, decoders or UI access. Factory metadata must remain inert; Create must return a fresh
non-playing session without downloading. Close the session before disposing its factory. The host
owns shared budget lifetime. Separate MediaTransport.Host implements explicit trusted registration.
Disk discovery, approval, lease renewal coordination and out-of-process isolation are not implemented. Signing in and
renewing provider authorization remain host/provider responsibilities. Non-cooperative code cannot be
forcibly stopped by an in-process cancellation token.
