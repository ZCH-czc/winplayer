# Media transport host — experimental 0.6.0

References only MediaTransport.Abstractions 0.4.0. No default implementation, platform account,
HTTP client, WPF or native decoder. Explicit-directory installation receipts, approved-package loading and
filesystem discovery are supported without implicitly reading application installation/user state.

`MediaTransportRegistry` consumes explicitly trusted registrations and an independent bundled default.
Metadata validation covers bounded IDs/labels/counts, duplicate optional IDs, API 1, current host 0.6.0
and required capabilities. `Inspect` never invokes activation delegates. A null preference selects the
bundled registration even when an optional registration has the same ID.

`CreateAsync` creates a new factory/session, verifies the factory descriptor matches the registered
snapshot, and claims each object once. It never disposes an object already returned to another owner.
Rejected metadata/managed optional creation failures attempt the bundled default; optional activation,
descriptor and creation failures are latched for this registry's lifetime. Already-running concurrent
creation attempts are not forcibly stopped. Failures are fixed issue codes, not third-party exception text.
Runtime request failures are **not** replayed through fallback: authorization must not silently move to
another implementation. Unknown request exceptions are normalized; cancellation/typed transport errors
remain typed. The wrapper does not validate arbitrary provider behavior or create a security sandbox.

The returned session owns disposal order: session (including cooperative transfer drain) before factory.
Repeated disposal shares completion/failure. Both disposal failures are reported with fixed issue codes;
a failing session cleanup does not skip factory cleanup. Close decoders before disposing transport pins
or sessions. The shared context budget belongs to the host, not an individual session/factory.

`CreateDeferred` does not activate anything until a non-precancelled Prepare. Concurrent first use shares
one creation task. Disposal without use is inert; disposal during creation awaits and closes the result
without starting a request. Factory execution is trusted, in-process and not forcibly time-bounded.
Noncooperative code can still hang; creation-time fallback is not native crash or request-time isolation.

Production `MediaTransportServices` is the only concrete application composition point. It currently
captures the explicit TransportComponents startup selection and keeps an independent bundled HTTP
factory, with the existing shared admission budget and cache path. No choice means bundled-only.
Diagnostic handler injection is isolated and immediately owned, without touching user cache/preferences.
ZIP preview/import, explicit trust UI and restart-only selection are wired; no hot replacement is offered.

Tests: `dotnet run --project Auralis.MediaTransport.Tests -c Release` (394 in-process assertions, 30 loader and 14 installed-loader assertions).
The offline registry fixtures cover inert/default selection, version/capability failure, failed activation
paths, ownership/reuse, disposal faults, deferred shutdown and no request-time fallback. Generated real
decoder smoke also uses this registry's deferred path. Neither proves live accounts or acoustic output.

## Schema 1 package inspection

`MediaTransportComponentCatalog.Discover` accepts only an explicit absolute local package/root path.
It reads at most 64 immediate package directories; duplicate IDs all fail rather than selecting a winner.
No registry registration, factory activation, HTTP, approval or user-directory discovery is performed.
`VerifyPayloadAsync` separately hashes every declared file and checks missing/unexpected files/directories,
changed lengths/digests, reparse-point paths and changed manifest bytes before/after hashing. Network
roots and mapped network drives are rejected. File checks are not race-free activation guarantees:
future loading must bind explicit approval to a digest and revalidate immutable installed revisions.
SHA-256 is integrity, not publisher authentication. In-process components are not a security sandbox.

`transport.component.json` must have exactly these fields (no unknown/duplicate keys):

```json
{
  "schemaVersion": 1,
  "kind": "mediaTransport",
  "id": "auralis.transport.http",
  "displayName": "HTTP media transport",
  "version": "0.6.0",
  "contractApiVersion": 1,
  "minimumHostVersion": "0.1.0",
  "runtimeIdentifier": "win-x64",
  "capabilities": ["authorizedHttp", "completeBuffering", "prefetch", "independentResources", "sharedBudget", "sharedRequests"],
  "entryAssembly": "Auralis.MediaTransport.Http.dll",
  "entryType": "Auralis.MediaTransport.HttpMediaTransportFactory",
  "files": [{"path": "Auralis.MediaTransport.Http.dll", "length": 123, "sha256": "<actual-64-hex-digest>"}]
}
```

The example length/digest are placeholders, not an accepted package. All capabilities are declarations,
not proven behaviors. Required capabilities are supplied by the caller; current production replacement
requirements are `Full`. Versions have three numeric components. ID: lower-case ASCII letters/digits,
dot/hyphen, alphanumeric ends, max 100. Display name max 80; type max 240, dot-separated CLR identifiers.
Manifest max 256 KiB/depth 12; max 256 files, 256 traversed directories (including root) and 64 MiB total payload; relative paths max 240 characters,
ASCII path segments max 100, no leading dots/trailing dots/traversal/ADS/reserved Windows names or case
aliases. Entry DLL must be declared with positive length. Native/dependency files may be listed, but
inspection does not resolve dependencies or verify managed types. File-prefix collisions are rejected.

`tools/Build-AuralisTransportComponent.ps1 -PublishedDirectory <publish> -OutputDirectory <new-directory>`
builds a development candidate for the current bundled HTTP factory using only its published DLL.
Optional `-ArchivePath <new-file.auralis-transport.zip>` adds a statically verified ZIP outside the input
and candidate directories. The shared Abstractions assembly belongs to the host, not the package.
`plugin-sdk/TransportInspect` supplies metadata/file CLI checks and `TransportInspect.Tests` verifies
62 assertions against actual tool child processes, including archive checks. No UI/account or current entry is touched.

## Explicit trusted activation

`MediaTransportPackageLoader.CreateRegistration(snapshot, approval, enabled)` requires a caller-owned
grant binding component ID and the exact manifest digest. Do not construct grants automatically from
discovery. Registration remains inert; normal Registry selection controls when activation occurs.
Activation verifies runtime/API/Host compatibility and full payload, obtains Windows read-only leases
for manifest/all declared files, then verifies again before loading the entry assembly in a collectible
context. Metadata/type/managed creation failures use the existing independent default fallback.

The factory must be public/nonabstract/nongeneric, implement the shared IMediaTransportFactory and have
a real public zero-parameter CLR constructor. Http 0.6.0 adds this constructor; the earlier optional
handler parameter alone did not supply it. Registry still checks the returned descriptor exactly.
The load context shares the host contract, rejects privately bundled contract/host/platform/playback
implementations and undeclared managed dependencies; only actual core framework names may resolve from
Default. Native imports must be declared (unambiguous) or from the narrow Windows OS import allowlist.
This is dependency hygiene, not containment: trusted code can invoke OS/file/network APIs itself.

The session owner closes its session before the factory. Async factory disposal keeps the read leases
until it completes/fails, then releases handles and requests context unload. Repeated disposal shares
completion/failure. CLR/native mappings may remain until process exit: update requires restart, not
in-place overwrite. Constructors and noncooperative disposal have no forced timeout or crash sandbox.

30 child-process assertions cover real fixture DLLs, exact grants, no activation when unused/disabled,
type/descriptor/constructor failures, private contract/Host refusal, read leases, deferred close and
sanitized disposal faults. A separately loaded real Http DLL downloads four synthetic bytes from a
temporary loopback TCP server and consumes the host budget; no external service/account is contacted.
Children are bounded and their generated trees are cleaned only after process exit. Production now
uses restart-only explicit selection; tests never read real user directories, receipts or accounts.

## Explicit-directory installation (restart-only)

`MediaTransportInstallationStore(root, required, runtime)` accepts a dedicated absolute local child
directory, never a drive root, relative path, UNC, mapped network drive or reparse path. Construction,
empty `ReadState`/`ReadStartupPlan` and `PreviewAsync(unpackedDirectory)` do not create a store. Preview
checks metadata and full payload, does not load a DLL, and is bound to this store instance and exact bytes.

After the caller obtains explicit user trust, `ImportApprovedAsync(preview, approval)` requires exact
ID/digest and verifies again. It copies bounded files into random private staging, verifies the copy,
then moves into `revisions/<manifest-sha256>` without overwrite. A new or replaced revision defaults
OFF and clears selection if replacing the selected component. Identical re-import preserves state.
Old revisions, including DLLs still in use, remain untouched; failed staging and state temporary files
are retained for explicit maintenance. There is not yet a historical-revision/disk-quota cleanup policy.

`SetEnabledAsync` and `SelectAsync` reverify before enabling/selecting; disabling clears next-start
selection even if the package is broken. They do not close or replace existing sessions. `SelectAsync(null)`
selects the independent bundled default for next composition. Receipts persist schema 1, selected ID
and at most 64 unique component ID/digest/enabled rows in `transport-installations.json` (max 64 KiB).
Unknown/duplicate fields, malformed IDs/digests and invalid selections fail closed. No URLs, headers,
account credentials or arbitrary diagnostic text are written. This is local trust state, not a signature
or tamper protection from software already running with the same user's rights.

Writes take an exclusive `install.lock`, flush a new state file and replace at a single commit point.
Competing writers get `Busy`; cancelled or failed commits leave the preceding receipt intact. There is
no silent reset of damaged receipts. `ReadStartupPlan` is an inert metadata/receipt snapshot for a NEW
registry, not hot reload and not full payload hashing; loader revalidation catches post-plan tampering.
Missing/changed/incompatible manifests are reported in `RejectedComponentIds`. Caller retains bundled
fallback composition and passes the plan's SelectedId to Registry, preserving same-ID fallback semantics.

64 directory-store assertions cover approvals, state persistence, immutable locked updates, corruption,
concurrency/cancellation, atomic replacement and reparse/unsafe paths. 14 real DLL child assertions prove
installed paths are actually executed, old/new revisions can coexist and disabling affects only future
composition. The real Http loopback test now also installs, enables and reads a fresh startup plan before
loading. All fixtures use generated directories; no real user profiles or platform accounts are changed.
Native picker/confirmation/restart integration is in source; actual user upgrade/device acceptance remains pending.

## Bounded ZIP preview and import

Host 0.5.0 adds `PreviewArchiveAsync(absoluteLocalZip, token)` and an overload of `ImportApprovedAsync`
for the returned `MediaTransportArchivePreview`. ZIP is only a container for the same schema 1 manifest,
not a new trust contract. There is no arbitrary script execution or automatic approval. Caller owns
the preview lifetime and must await `DisposeAsync` after confirmation/cancel/close. Only one pending
archive preview per store instance is allowed; import and disposal are serialized. Import still copies
into the independent immutable revision, defaults off, and requires exact component ID/manifest digest.
The original ZIP is no longer used after preview, so replacing it cannot replace the approved payload.

Input max 80 MiB; payload max 64 MiB plus 256 KiB manifest; central metadata max 8 MiB, max 513 entries;
at most 255 nested directories plus root. Central-directory bounds are checked before allocating ZIP
entries. Only ordinary single-volume stored/deflated ZIPs are supported; encrypted, ZIP64, trailing
comments/data, unsupported methods, unsafe/link/special entries, duplicate/case-alias paths and excessive
ratios are rejected. Explicit/implicit directories must match declared payload prefixes. Length/hash
checks run while extracting, independent of CRC, followed by normal catalog compatibility/integrity checks.

Extraction uses a random private `previews` directory under the explicit store root, never ExtractToDirectory.
Disposal deletes only recorded files and empty directories, without recursive deletion, link traversal,
or removing unknown contents; store/revisions remain. Cleanup failure is reported rather than claiming
success; no forced filesystem timeout or hostile same-user process isolation is provided. Directory
installation's retained failed staging policy remains separate from owned archive-preview cleanup.

85 archive assertions use synthetic ZIPs for bounds/path/metadata/content/approval/cancel/disposal/update.
Real fixture and HTTP packages now pass through ZIP preview/import before installed DLL/loopback tests.
`--archives-only` is a focused diagnostic mode, not a substitute for the complete transport regression.
Application management has a dedicated transport import card; do not place this kind in the platform importer.

## Startup composition and native management

Host 0.6.0 `MediaTransportComponentComposition` snapshots a startup plan and considers only its explicitly
selected optional registration, plus the independent bundled default. `CreateDeferred` uses Registry's
existing owned lazy session, preserving pre-cancel/unused-close behavior. Creation is serialized; managed
optional creation failures latch for subsequent sessions. No request-time fallback, account replay, forced
unload, hot replacement or native crash isolation is added. CurrentDescriptor describes the selected
startup path before first use; Activated reports whether a session has actually been created.

The application freezes the plan from LocalAppData/Auralis/TransportComponents before management
mutations or first transport use. Invalid/unavailable optional state falls back to bundled composition.
The new native handler validates main-WebView origin/request IDs, uses only a native picker for paths,
and requires native-held preview token plus explicit trust. Confirmation explains media URL/header access;
only safe metadata/digests cross the main bridge. Imported revisions remain OFF, changes require restart.
Platform/playback/transport mutations and restart are mutually exclusive; inert list requests can run
concurrently without startup deadlock. Web callbacks reject stale replies and do not optimistically enable.

14 composition assertions cover inert metadata, selected-only/same-ID default, concurrent fallback latch,
invalid/disabled state and no request replay. Real ZIP-installed DLL tests use the composition, and real
decoder smoke uses a bundled generated-media composition without real user state. Web tests cover 150
transport/144 playback assertions plus platform settings, with light/dark, three scale factors, keyboard,
reduced motion and narrow/wide/empty/populated fixtures. They are not native picker/restart or full Windows
fullscreen/device acceptance; those remain explicitly outstanding. Historical cache/cleanup issues remain.
