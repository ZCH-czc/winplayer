# Independent plugin delivery gate

Stage 10, 2026-09-13. Developer tooling only; no player/Host/Web contract changes.

Stage 11 adds a [single-plugin frozen-SDK builder and actual-host compatibility matrix](PLUGIN_REVISIONS.md)
which invoke this gate. `CoreSourceListFile` is an optional JSON path array for child-process calls;
it cannot be combined with `CoreSourceDirectories`. The same source inventory rules and baseline checks apply.

The gate answers **whether an explicitly selected plugin revision can pass specified checks on an unchanged
published host**. It does not claim arbitrary future capabilities can run on every old host, and it does not
install, sign, upload or approve a release. Public tooling contains no platform-specific API or test profile.

## Inputs and defaults

Requirements: PowerShell 7.2+, .NET 8 with the repository's dependencies already restored. UI profiles additionally
need the lock-installed Playwright development dependency and installed Edge. The gate uses `--no-restore`;
it does not fetch missing dependencies automatically.

Provide both actual `.auralis-plugin` archives **and their unpacked development directories**. This first version
deliberately does not extract ZIP entries. It checks exact archive/directory file membership and SHA-256 bytes,
bounded to 256 entries / 128 MiB per package, rejects duplicate/case-colliding names, traversal, unsupported directory
entries, streams, device names and reparse/symlink inputs. Same plugin ID, a higher version and a different payload
are required. This is stricter than merely checking a ZIP extension or trusting a manifest version.

```powershell
$sources = @('Auralis','Auralis.Platform.Host','Auralis.Platform.Abstractions',
    'Auralis.Artwork.Http','Auralis.Playback.Abstractions','Auralis.Playback.Host')
$options = @{
    PreviousPackage = 'artifacts/old/example.1.0.0.auralis-plugin'
    PreviousDirectory = 'artifacts/old/platforms/example'
    UpdatedPackage = 'artifacts/new/example.1.1.0.auralis-plugin'
    UpdatedDirectory = 'artifacts/new/platforms/example'
    FrozenCore = 'artifacts/approved-core-publish'
    CoreSourceDirectories = $sources
    # Optional existing absolute-path -> SHA-256 baseline. Without it, a new run only proves before/after equality.
    CoreBaseline = 'artifacts/previous-proof/frozen-core.json'
    EvidenceDirectory = 'artifacts/delivery-example-new-run'
}
./tools/Test-PluginDelivery.ps1 @options
```

The output directory must be new, with an existing parent, and outside the frozen inputs. The old directory is
never overwritten/reused. Without a baseline, remove `CoreBaseline`; source directories are optional for public
binary-only consumers, but the report then explicitly says source coverage was not provided. Source snapshots
exclude `bin` and `obj`; the entire selected published directory is included. Source baselines are local-path-bound,
not portable signed attestations.

**Default: inert package + manifest checks only.** The checker is compiled against `FrozenCore` DLL references,
then its Host/Abstractions bytes are compared to the published files. It cannot silently fall back to the current
source SDK. No provider assembly is activated for static checks. Legacy metadata that `ContractCheck --inspect`
can read but the selected runtime cannot activate is not a gate pass.

## Explicit trusted execution

Add `-TrustPluginCode` to run actual old/new DLL descriptor, interface and lifecycle checks with the existing bounded
ContractCheck worker and in-memory services. Host-supplied HTTP sends are denied. No business method is invented as
a universal test. The worker timeout is 30 seconds; gate child commands also have a configurable 30–600 second deadline
(default 240 seconds). Only child processes created by the check are terminated. Output is drained with a bounded
retained prefix; raw plugin output, exception text and platform responses are not copied to the aggregate report.

A behavioral profile additionally requires `-Profile <reviewed-repository-script.ps1>`. This is an **explicit execution
of trusted test code**, not an automatic hook from a plugin manifest. The public gate never discovers profile commands
inside a package. A trusted DLL/profile can bypass supplied services; this is not an OS sandbox. Use a restricted
machine/VM for third-party code. Checksums establish local byte identity, not publisher trust.

Profiles are called in two independent child invocations, `-Check offline` and `-Check ui`, with:

- `PreviousDirectory`, `UpdatedDirectory`: the verified single-plugin inputs;
- `FrozenCore`: the selected unchanged published host;
- `EvidenceDirectory`: this new run's evidence directory.

Each must succeed and write `<check>-result.json` with a real boolean `Passed:true`, matching `Check`,
`Synthetic:true`, and a positive integer `Checks`. Printing PASS without a report, a partial scenario, a nonzero
exit code, or a timeout fails. The profile must supply deterministic error/cancellation/lifecycle and UI assertions
appropriate to the feature. A count alone is not a security guarantee. No old result is imported as current.

The private repository profile `private-platforms/DeliveryProfiles/QqIndependentUpgrade.ps1` reuses the actual
catalogue and discovery probes. It covers exact QQ 1.14 → 1.15 and 1.15 → 1.16 revision pairs, not every arbitrary
future QQ version. It creates isolated copies and scoped development receipts **under evidence only**, verifies copy
bytes before/after, uses identical published SDKs and unchanged production Native coordinator source, and tests the
frozen published Web UI with isolated synthetic data. It does not read user credentials or use a real media lease.

```powershell
./tools/Test-PluginDelivery.ps1 @options -TrustPluginCode `
    -Profile private-platforms/DeliveryProfiles/QqIndependentUpgrade.ps1
```

Use actual QQ paths/version pairs with that private profile; the generic example paths above are placeholders.
Other plugins need their own reviewed profiles. No platform-specific branch is required in the public gate or player.

## Evidence and decision rules

`delivery-report.json` binds a unique run ID, timestamps, old/new archive and file hashes, manifest SDK requirements,
frozen host/source inventory, actual checker runtime, selected profile hash, tooling source hashes and evidence-file
hashes. Before/after checks detect added, removed or changed host files, checker files and original package bytes.
The profiles additionally retain the fixed Native probe inventory. `delivery-report.md` is the readable summary.

| Stage | What a pass means | What it does not mean |
| --- | --- | --- |
| package | Actual archives match unpacked inputs, revision identities differ correctly | Safe publisher or working code |
| static | Both manifests compatible with the actual selected SDK | Code activation or real platform availability |
| frozenHost | Same selected source/publish inventory and bytes before/after | A Windows app was launched |
| contract | Actual DLL descriptors/interfaces/lifecycle pass on frozen SDK | Business responses work |
| offline | Explicit deterministic profile assertions pass | Real account, platform, decoder or audio success |
| ui | Explicit published-Web synthetic scenarios pass | Native DPI, hardware, video composition or live media acceptance |
| live | Reserved for separate current-candidate platform acceptance | This local tool currently does not run it |
| native | Reserved for installed-app/real playback acceptance | This local tool currently does not run it |

Statuses are `passed`, `failed`, `not-run`, or justified `not-applicable`. Required checks cannot be waived with
`not-applicable`. `LocalReady` requires the first six passes. `ReleaseReady` additionally requires live and native
passes; **this tool currently keeps both not-run, so it cannot authorize a release**. A live failure and local success
can coexist in the policy without one erasing the other. An exit code of zero means only the selected local checks
passed. CI must inspect the relevant assessment, not turn exit zero into automatic publication.

To check that recorded evidence has not become stale or gained/replaced files:

```powershell
Import-Module ./tools/PluginDelivery.psm1 -Force
Test-DeliveryEvidence 'artifacts/delivery-example-new-run'
pwsh -NoProfile -File tools/Test-PluginDeliveryGuards.ps1
```

Evidence verification reads only inside that directory and executes nothing; it checks run identity, inventory,
hashes and the derived assessment. JSON/Markdown are not digitally signed, so this detects accidental tampering/stale
results, not an attacker forging an entire report. The Markdown is a convenience view, not authoritative evidence.
Keep evidence local: it contains developer paths and explicitly trusted plugin binaries, never add it to public source.

## Stage 10 acceptance / remaining work

The gate reuses the frozen SDK 2.10 / Abstractions 1.11 baseline; no production core or Web changes are required.
Completed local checks:

- 97 gate guard assertions: status/failure policy, archive identity and unsafe entries, membership/hash changes,
  explicit execution trust, fresh evidence, stale/mutated artifacts, bounded output and child timeout.
- QQ 1.14 → 1.15: 59 actual-DLL offline assertions and six published-Web browser cases.
- QQ 1.15 → 1.16: 89 actual-DLL offline assertions and six published-Web browser cases.
- Both runs: 649 unchanged selected core source/publish files, 17 unchanged Native probe runtime files,
  byte-identical published SDKs, and validated evidence inventories.
- Default static-only run: no DLL/business/UI execution; `LocalReady:false`, `ReleaseReady:false` as intended.
- Release solution build: zero warnings/errors; core/Host suites, 89 contract-checker assertions, platform 50/50,
  split plugins 54/54, JavaScript logic 27/27, plugin boundary check and `git diff --check` passed.

Local evidence directories (not public release assets): `artifacts/plugin-delivery-stage10-catalogue-verified`,
`artifacts/plugin-delivery-stage10-discovery-verified`, and `artifacts/plugin-delivery-stage10-static-only`.
These verified runs do not reuse the initial `plugin-delivery-stage10-catalogue` failure directory.

The catalogue UI test previously printed six PASS messages without a JSON result; the gate rejected that incomplete
evidence. The test now writes a result only after all cases pass. Failure evidence is retained, not rewritten green.

The final QQ 1.16 package's **previous real search check failed with ServiceUnavailable**. This phase performs no
new live call and does not supersede that failure. Headless tests link unchanged Native coordinator sources; they
are not the installed player, a real audio output or a PerMonitorV2 test. Browser cases include light/dark, scale
1/1.5/2, small windows, reduced motion, keyboard paths, missing-plugin UI and synthetic error/retry behavior.

Next: a single-plugin build/compatibility matrix that builds only the changed plugin, records SDK/feature/permission
differences and feeds this gate. Add a reviewed Bilibili profile without adding platform branches to public tooling.
Keep real-platform and native acceptance explicit and candidate-bound; do not introduce automatic publishing or
pretend account-write/system capabilities already exist in the current read-only contract.
