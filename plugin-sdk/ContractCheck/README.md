# Auralis Plugin Contract Check 1.2

Host SDK 2.0 removes implicit platform credential/image tables. Current Auralis requires schema 5 for activation/import.
`--inspect` still accepts valid schema 1–4 metadata for offline upgrade preparation, with `UpgradeRequiredPlugins > 0`,
`MinimumRuntimeManifestVersion:5` and `RuntimeManifestCompatible:false`. Its exit 0 means metadata inspection passed,
**not that the player can enable the package**. `--verify` rejects such inputs with `ManifestUpgradeRequired`, exit 1,
and `Executed:false`, before starting the worker. The worker API rechecks this policy too.
`RuntimeManifestCompatible:true` is only a necessary manifest condition, never proof of installation approval,
settings/account readiness or working playback. Failed/incomplete reports cannot claim compatibility.

Schema 5 / Host 1.2 adds mandatory per-provider `commentArtworkDomains` and the `comment-artwork.v1` feature.
Both modes report `DeclaredCommentArtworkProviders` and `LegacyCommentArtworkProviders` counts, including empty deny policies.
Counts describe accepted metadata, not actual CDN reachability, removal of host compatibility code, or readiness for public release.
Malformed/missing domains fail static preflight before a trusted worker starts. Schema 1–4 requires an explicit package upgrade;
the retained `LegacyCommentArtworkProviders` field counts undeclared old providers, not granted legacy CDN access.

Independent developer CLI and reusable offline assertions. Depends only on the public Host/Abstractions,
not the player, WPF, concrete platforms, personal accounts or test media. Requires .NET 8.

## Commands

From the repository root:

```powershell
dotnet run --project plugin-sdk/ContractCheck -c Release -- --inspect <unpacked-plugin-root>
dotnet run --project plugin-sdk/ContractCheck -c Release -- --verify <unpacked-plugin-root> --trust-plugin-code
dotnet run --project plugin-sdk/ContractCheck.Tests -c Release
```

The input is an **unpacked development plugin directory**, or a root containing such directories, not a ZIP
and never an automatically selected user plugin directory. Missing/empty roots fail instead of producing a
misleading pass. The tool neither writes approval receipts nor installs/enables plugins.

- `--inspect`: production Catalog metadata checks only. No DLL activation, HTTP client or credentials.
- `--verify`: requires `--trust-plugin-code`. A fresh worker uses the production Host to load the explicitly
  selected plugin, compare descriptors, create/initialize providers, route a no-op through each declared
  capability interface and dispose. It does **not** invoke search, sign-in, sign-out, lyrics or media acquisition.
- Host services use separate temporary **in-memory** stores per plugin. Expired entries are not returned.
  Credential bytes are copied and zeroed on replacement/removal/disposal. There is no legacy-account lookup.
  HTTP client creation is allowed, but sending through the default factory is denied and counted as failure,
  even if the plugin catches the exception. This checks the no-network initialization requirement.
- The worker has a total 30-second deadline including construction and disposal; optionally pass
  `--timeout-seconds 1..120`. Only this checker's child process tree is terminated on timeout/cancellation,
  never a running Auralis process. In-process assertion helpers alone cannot kill a non-cooperative operation.
- JSON stdout contains counts, fixed diagnostics and `NotTested`. Raw worker stdout/stderr, plugin log
  messages, exceptions, display names, file paths, URLs and headers are not forwarded. Captured stdout is
  bounded to 256 KiB; oversized/native noise may produce `WorkerReportInvalid` rather than unsafe output.

Exit codes: `0` passes the selected check, `1` failure, `2` invalid arguments/missing explicit trust,
`3` worker timed out, `4` caller cancelled. Inspect success always has `Executed:false`.

**Not a security sandbox:** a trusted in-process DLL can bypass the supplied services, use its own network
or filesystem APIs, start other processes or corrupt its worker. Process separation limits ordinary hangs
and crashes, not malicious code. Run third-party code only in a separately restricted OS/VM environment.
The internal `--trusted-worker ... --trust-plugin-code` mode is for the supervisor; it has no independent
deadline and should not be invoked directly. A successful report is not a signature or a security certificate.

## Author-supplied behavioral scenarios

Reference this project from a **test project**, never from the player or distributable plugin. Use deterministic
HTTP response delegates or your own fixture context and call the real provider method directly. Do not route
these assertions through the Host: Host exception/cancellation mapping could hide a plugin contract defect.

```csharp
using Auralis.PluginContractCheck;

// provider/request are your fixture-owned provider and synthetic request.
var success = await ContractChecks.OperationAsync(
    token => provider.LookupAsync(request, token));
var cancelled = await ContractChecks.CancellationAsync(
    token => provider.LookupAsync(request, token));
// For an operation deliberately blocked in fixture HTTP, test in-flight cancellation:
var interrupted = await ContractChecks.CancellationAsync(
    token => provider.LookupAsync(request, token), cancelAfter: TimeSpan.FromMilliseconds(50));
```

`OperationAsync` takes an optional `expectedError` and a successful-payload validator (`inspect`).
Unexpected typed failures are not silently marked passed; invalid default results, unknown codes, negative
RetryAfter and uncaught exceptions produce fixed findings. Returning success after cancellation is a failure.
The delayed cancellation variant requires a fixture which cannot finish before cancellation; normal fast
success is not cancellation compliance. Timeouts cancel the token and observe late faults, but cannot stop
non-cooperative code; put the entire author test runner in a disposable bounded process in CI.

Reusable payload assertions:

- `TrackPage(page, providerId, requestedSize)`: page bounds, negative totals, null rows and foreign track IDs.
- `AudioLease(lease, clock)`: HTTPS/loopback HTTP without userinfo, expired authorization, safe header syntax,
  positive optional bitrate. `MissingExpiry` means the contract allowed unknown expiry, **not a verified TTL**.
- `VideoLease(lease, clock)`: also validates alternate URL shapes and the independent audio lease expiry.
  It does not download or validate that alternate URLs contain equivalent media.
- Use a controllable `IPlatformTimeProvider` to check valid → exact expiry → fresh renewed lease. Never
  serialize the lease to a report. No source URL, query, credential or header is needed in an assertion message.
- `OfflineContextFactory(response)` optionally supplies developer-owned canned HTTP responses; it never
  constructs a real networking handler. Such a delegate is test code, not a security enforcement boundary.

The executable examples and failure fixtures are in `../ContractCheck.Tests`. CLI structural verification
does not automatically discover or execute scenario assemblies. `NotTested` continues to list business,
cancellation and lease behavior until the author separately runs those scenarios; no arbitrary platform
query or account can be invented as a universal test.

## Verification levels

1. Manifest/schema/SDK compatibility (inert).
2. Runtime descriptor/interface/lifecycle (explicit trusted execution).
3. Provider operations with deterministic HTTP/error/cancellation/lease fixtures (author tests).
4. Real accounts, platform rights and actual media playback (separate opt-in acceptance).

No earlier level proves a later one. In particular this tool cannot catch a LibVLC output window escaping
the player's compositor; that belongs to playback-component/native UI tests.
