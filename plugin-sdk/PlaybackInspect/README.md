# Playback package inspection (experimental 0.2)

This separate developer CLI depends only on Playback.Host. It never loads a candidate DLL, creates a
decoder, reads user accounts, installs/enables a component or modifies the player. It accepts an explicitly
selected unpacked component directory, a directory containing up to 64 component directories, or a
playback ZIP with the command below. It is not the platform importer; platform manifests are rejected.

```powershell
dotnet run --project plugin-sdk/PlaybackInspect -c Release -- --inspect <directory>
dotnet run --project plugin-sdk/PlaybackInspect -c Release -- --check-files <directory>
dotnet run --project plugin-sdk/PlaybackInspect -c Release -- --check-archive <component.auralis-playback.zip>
```

- `--inspect` reads only the bounded manifest and reports metadata compatibility for win-x64 and the
  capabilities required by the existing complete player. It does not hash or validate the DLL payload.
- `--check-files` additionally hashes every declared file, rejects unlisted/missing files and directories,
  checks the inspected manifest has not changed, and supports bounded nested native dependencies.
- `--check-archive` checks bounded ZIP metadata, extracts and hashes a candidate in a fresh diagnostic
  temporary store, then removes its preview and empty scaffolding. It does not write approval/enable state.
  Archive and manifest SHA-256 values are reported separately. The original ZIP is never modified.
- All emit a JSON report with `Executed:false` and `Approved:false`. IDs, fixed issue codes, counts and
  manifest hashes are included, but not filesystem paths, raw exceptions or plugin-supplied display text.
- Exit codes: 0 passes the selected checks; 1 invalid/incompatible/empty input; 2 command usage; 4 cancelled.
  Ctrl+C requests cancellation during enumeration/hashing. Local blocking filesystem I/O has no deadline.

Success does not prove the factory exists, runtime descriptors match, DLL/native dependencies can load,
playback works, or a publisher is authentic. Checksums are not signatures, approval or a security sandbox.
Paths and reparse points are checked before reads, but another same-user process can race filesystem
changes. A future loader must consume an approved immutable revision and revalidate before loading.

## Schema 1: playback.component.json

All fields are required, case-sensitive; unknown and duplicate JSON fields are rejected.

| Field | Meaning |
| --- | --- |
| `schemaVersion`, `kind` | Exactly `1`, `"playback"`; separate from platform schemas |
| `id`, `displayName` | Stable lowercase identifier and bounded plain display text |
| `version`, `minimumHostVersion` | Three nonnegative numeric version parts |
| `contractApiVersion` | Playback factory/session API; currently 1 |
| `runtimeIdentifier` | Exact target, currently the player requires `win-x64` |
| `capabilities` | Unique names: audio, videoFrames, separateAudio, seek, rate, outputDevices, mute |
| `entryAssembly`, `entryType` | Declared DLL relative path and non-generic public factory type name |
| `files` | Every payload file except the manifest: `{path,length,sha256}` |

Limits: manifest 1 MiB, 1..4096 payload files, total 512 MiB. Relative paths use `/`; reject traversal,
absolute/UNC/device paths, ADS, reserved Windows names, empty segments, case collisions and file/directory
prefix collisions. Paths are ASCII letters/digits/`.`/`-`/`_`, at most 240 characters and 100 per segment.
Native subdirectories are allowed; reparse points and undeclared empty directories are not.
The manifest digest and immutable file list form an inspection snapshot, not a trust receipt.
Duplicate component IDs reject all corresponding candidates; enumeration overflow rejects the entire set.

## Build the default implementation as a candidate and optional ZIP

```powershell
pwsh -File tools/Build-AuralisPlaybackComponent.ps1 `
  -PublishedDirectory <verified-core-publish-directory> `
  -OutputDirectory <new-component-directory> `
  -ArchivePath <new-component.auralis-playback.zip>
```

The build helper allowlists the implementation DLL, LibVLCSharp, native win-x64 subtree and notices.
It excludes the application, Web UI, platform plugins, Playback.Host and contract copies; the future loader
must share the host contract identity, not load a second contract assembly. It refuses to overwrite any
existing directory and validates the generated manifest/files using this inspector.

The optional archive must be new, outside both input/output directories, and use `.auralis-playback.zip`.
It is an ordinary single-disk ZIP containing `playback.component.json` at its root and exactly the declared
nested payload. No wrapper directory, encryption, ZIP64, archive comment/trailer or non-stored/deflate
compression is supported. ZIP bytes ≤512 MiB, central metadata ≤8 MiB, total entries ≤8193,
payload files ≤4096 and implicit/explicit directories ≤4096. Each file's expansion ratio ≤1000;
manifest/payload limits above still apply. Optional directory entries must match actual declared prefixes;
links, case aliases, duplicate/extra files and undeclared empty directories are rejected.

**Application integration requires Playback.Host 0.6.0 or later.** The playback component card in plugin
settings uses native single-file ZIP selection, explicit trust, disabled import and next-start selection.
The player retains an independent bundled default. The current test deployment has not been updated.
Do not copy playback candidates into the platform-plugin directory or platform batch importer.
The helper does not sign the package. This inspector NEVER executes code; see the explicit
trusted archive install/decode diagnostic in tools/PlaybackPackageSmoke for a different level of evidence.
