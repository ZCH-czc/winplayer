# Playback component host (experimental 0.6.0 / API 1)

Production integration (2026-09-08): `PlaybackComponentComposition` consumes one inert startup plan.
Only an explicitly selected registration may execute. The bundled registration is an independent source,
even when it has the same component ID. A managed preflight/creation fallback is latched for this process;
later windows do not retry the failed optional component. Preferences never switch existing sessions.
MainWindow uses the existing settings layout for native ZIP preview, explicit trust, disabled import,
enable/next-start selection and restart. The dedicated store is `%LOCALAPPDATA%/Auralis/PlaybackComponents`;
it does not scan platform directories. Native picker only in this stage; platform batch/drop remains separate.

The registry accepts explicit, trusted factory registrations. Metadata inspection does not invoke the
activation callback. Disabled, incompatible API/host-version or insufficient-capability registrations are
rejected before activation. Runtime descriptors must match the registered metadata exactly.

`VerifyRuntime` checks a fresh factory and disposes it without creating a session or opening an audio
device. `Create` returns a stopped session wrapped with ownership of its factory. Every activation must
return a fresh factory and every creation a fresh session; reused instances are rejected without disposing
another caller's owned instance. Disposal attempts session cleanup before factory cleanup, once each.

If the preferred candidate fails a managed check before a session is handed to the caller, the registry
tries the registered bundled default once. Both failures produce fixed diagnostic codes and a typed error,
not raw plugin exceptions or media URLs. Diagnostic callbacks cannot break playback. This does not switch
engines after playback starts: network errors, expired leases and decoder failures remain with the caller.
Replaying a source automatically could lose position or repeat audible content.

The application composition point is `Auralis/Services/PlaybackServices.cs`. It retains bundled LibVLC,
reads the approved startup selection once, and requires all existing player capabilities. Main and MV windows do not select engine IDs or call
concrete constructors. Platform plugins cannot replace the playback registration table.

This is a trusted in-process playback plugin system, **not a security sandbox or hot replacement system**.
Schema-1 disk manifests, inert discovery and explicit payload inspection are now provided by
PlaybackComponentManifest/PlaybackComponentCatalog. Discovery reads only bounded metadata; full file
hashing is a separate explicit operation. Neither creates registrations or approves/enables code.
The developer CLI and schema are documented in ../plugin-sdk/PlaybackInspect/README.md.
PlaybackPackageLoader now creates lazy registrations from an exact caller-supplied ID/manifest-digest
approval. Creating a registration does not load code. Activation rechecks the payload before and after
taking Windows read leases, shares the host contract assembly, checks the factory type and keeps factory
and load-context lifetime attached to the returned session via the registry. Undeclared dependencies,
host implementation references and private contract copies are refused. The activation wrapper does not
forward raw exceptions. Use the registry, not the activation delegate directly, for compatibility,
runtime-descriptor checks and pre-creation fallback.

This loader is an execution mechanism, not a persistent approval/import manager. A grant must follow
explicit user/developer trust; it must never be synthesized merely because discovery succeeded. Existing
application selection remains the bundled default. Persistent revisions and restart-only preferences now
exist in the backend store described below; archive preview/import is implemented, while application UI/composition wiring remain
future work. File leases limit ordinary same-user writes on Windows,
not malicious process behavior or all filesystem races. Unload requests do not promise timely CLR/native
unmapping. Do not update package files in place or promise live engine-version replacement.
Factories are trusted in-process code: callbacks may hang or crash natively, and this registry provides
neither a timeout/process boundary nor a security sandbox. Capability declarations are not proof that a
decoder implements every format. The experimental playback API is separate from the platform plugin SDK.

Verification:

```powershell
dotnet run --project Auralis.Playback.Host.Tests -c Release
dotnet run --project tools/MediaEngineSmoke -c Release -- --run
```

Host tests use synthetic sessions only; the smoke test routes actual LibVLC through this registry with
silent generated WAV/AVI. Neither is a real-account, acoustic synchronization or complete UI acceptance.

Host tests additionally build a separate synthetic DLL and activate it in a bounded child process to test
contract identity, exact grants, file leases, dependency rejection, fallback and disposal. The parent
removes the synthetic directory after the child exits; native/CLR unmapping is not assumed immediate.
For an actual dynamically loaded engine with no decoder in the test executable output, see
../tools/PlaybackPackageSmoke/README.md.

## Restart-only installation store

`PlaybackInstallationStore` takes an explicit dedicated local directory, required capabilities and RID.
Construction/empty reads do not create directories. It has no default user-profile path or main-UI bridge.

1. `PreviewAsync(unpackedDirectory)` reads and verifies one exact schema-1 package, without reflection or
   execution. The immutable preview belongs to that store instance and exposes identity, digest and sizes.
2. After explicit trust, `ImportApprovedAsync(preview, exactApproval)` rechecks source, copies bounded
   declared files to a random staging directory, checks the copy and moves it to `revisions/<manifest SHA256>`.
   It never overwrites an existing revision. A changed revision requires a new exact grant and starts OFF;
   identical re-imports preserve enabled/selected state. Payload hashes are not publisher authentication.
3. `SetEnabledAsync` and `SelectAsync` verify the approved revision before saving. Disabling also clears
   selection if applicable, including for a broken package. Updates leave previous revisions intact.
4. A new host calls `ReadStartupPlan()` once and routes returned lazy registrations through the registry.
   This does not load factories; full payload revalidation remains mandatory at activation. Missing or
   changed manifests are rejected with IDs, while corrupt state yields an empty plan with a typed issue.
   The app composition layer must preserve its bundled fallback and explicitly resolve duplicate IDs;
   the current app intentionally does not consume this store yet.

`playback-installations.json` schema 1 is the local approval receipt and restart preference transaction:
each ID pins one approved manifest digest plus enabled state, and selection must identify an enabled row.
It contains no signed media URLs, cookies, arbitrary entry paths or type names. Strict bounded parsing
rejects duplicate/unknown/missing fields. Writers use an exclusive store lock and one flushed temporary-file
rename; cancellation/storage failure before commit preserves the previous receipt. Competing writers fail
with Busy and must retry; read-modify-write does not silently lose another manager's preferences.

Changes do not affect already returned plans or sessions. A failed update may leave an unreferenced
immutable revision/staging directory or state temporary file, but never an approved partial package.
There is intentionally no automatic recursive cleanup/uninstall of installed revisions, rollback UI, disk
quota across retained revisions or hot-unload guarantee yet. Future maintenance must use explicit owned
revision references and account for CLR/native mappings. Local receipts are editable by the same user;
link checks/file leases do not turn trusted in-process extensions into a security sandbox.

Tests cover synthetic store files (including an actual reparse lock), corruption, cancellation, concurrent
writers, source/revision tampering and a real fixture DLL through import → reopening → registry → session.
The dynamic engine smoke's explicit `--install-root <NEW isolated directory>` additionally verifies real
LibVLC decoding and native module paths from the persisted installed revision, without touching Auralis.

## Archive preview lifetime

`PreviewArchiveAsync(zipPath)` accepts one ordinary playback ZIP and returns `PlaybackArchivePreview`.
It uses the SAME manifest/catalog and `ImportApprovedAsync` installation policy, not a second trust model.
The archive is opened read-only during extraction, its digest recorded, and exact payload hashes checked.
Approval binds the displayed manifest digest; replacing the original ZIP after preview cannot replace the
already verified extracted content. Import revalidates that preview again before writing its revision.

The caller must `await DisposeAsync()` when cancelling/closing the confirmation surface or after import.
Only one preview per store instance may remain open. Imports and disposal serialize so closing cannot
delete a preview while installation reads it; disposal is idempotent and a disposed preview cannot import.
Preview cleanup deletes only recorded files/directories beneath its generated GUID root, non-recursively,
after path/link checks. It never traverses links, deletes unexpected files or removes installed revisions.
Cleanup failure is typed and leaves leftovers for explicit maintenance; process crashes may also leave
unreferenced previews. This is not a same-user malicious-process/TOCTOU sandbox or global disk quota.

Before `ZipArchive` reads Entries, central-directory size/count are bounded to prevent excessive metadata
allocation. Path, file/directory collisions, methods, encryption, links, ZIP64/multi-volume/comment/trailer,
expanded byte and directory counts are checked before extraction. Byte limits/hashes are enforced again
while streaming, independently of ZIP CRCs. Full format limits and the inert `--check-archive` developer
command are documented in ../plugin-sdk/PlaybackInspect/README.md.

The current app still does not call this API or show engine-selection controls. Native smoke supports
`--run-archive <ZIP> --trust-plugin-code --install-root <NEW isolated directory>` to prove the archive →
receipt → lazy registration → actual decoder path without changing production UI or user installations.
