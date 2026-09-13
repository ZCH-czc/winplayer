# Plugin-aware next-track preparation

2026-09-13 Pages v6: page reads only register bounded metadata, never acquire/prepare a lease.
Explicit card selection uses the same pending playback commit and mixed queue. Enqueue triggers only the normal
actual-successor planner after the foreground clock advances, not speculative traversal of a page/feed.
The coordinator binds page media to plugin/account/settings revisions, checks validity in the shared prefetch
context and rejects late leases after revocation. Repeated active references deduplicate by provider/entity.
Native rights and preview availability still apply at preparation/playback; UI enqueue is not a rights grant.

Development revision: 2026-09-12; not installed or released.

## Behavior and ownership

- Prepare only the planned next online item, after the current playback clock advances two seconds.
  Shuffle uses the same stable plan for preparation and actual next-track selection. Local playback
  never needs a provider; saved online references are restored before preparation.
- A speculative operation runs away from the window dispatcher and never opens a decoder or changes
  the current track. Foreground handoff waits at most 200 ms for the matching work. A timeout means
  the normal explicit playback path resolves its own source, not that playback has failed.
- Cancelling unmatched work returns without waiting for optional plugin callbacks. A late result has
  exactly one disposer; a successful handoff has exactly one owner. At most two unfinished discarded
  operations are retained; new speculation is skipped while those operations remain outstanding.
  The two-minute preparation timeout is cooperative, not a sandbox for a noncompliant plugin.
- Pause, queue changes, settings edits, login/sign-out and disable retire speculative work. Decoder
  end-of-track and a request for the matching prepared successor preserve it for handoff.
- Provider-scoped session revisions invalidate prepared leases after settings/account changes.
  Disabled providers and disposed hosts fail the active predicate. Capture, post-preparation,
  consumption and pre-activation checks reject stale work; current audio is not forcibly stopped.
- Cache identity includes the stable backend entity, provider, quality and session revision, plus a
  hash of the full authorized resource URL. A changed preview/authorization URL cannot reuse a
  previously downloaded full resource just because the song and quality IDs match. Transport also
  separates headers/variants. No URL, cookie or cache path is added to the WebView protocol.
- A prepared lease within 30 seconds of expiry, or whose temporary file disappeared, is not consumed.
  Existing transport limits remain: 64 MiB speculative object, bounded shared admission, eight cached
  files / 768 MiB per session. These are not a new persistent download feature or a global disk quota.

## Verification

SpeculativeWorkTests exercises successful transfer, duplicate take, ignored cancellation, delayed
cleanup, 200 ms join timeout, consumer cancellation, provider faults and background expiry.
Coordinator tests check independent provider revisions, setting invalidation and new cache identities.
PlaybackPlanning tests check pause/resume, replacement, natural end, repeat and shuffle planning.
Service tests distinguish same-entity resources with different authorized URLs.

These checks do not measure real CDN throughput, acoustic gaps, native device latency or authenticated
playback. External account revocation can only be observed when the provider/platform reports it.

2026-09-12 validation: core Release build (zero warnings/errors), service tests, Host tests, 23 UI
logic tests, 89 contract-checker assertions and transport suite passed. Source and published-output
plugin-boundary checks passed. The service fixture reproduced its previously recorded intermittent
temporary-file enumeration/cleanup failure once; an isolated rerun passed. This is not claimed fixed
by the prefetch work. Native DPI/device/CDN acceptance remains separate.
