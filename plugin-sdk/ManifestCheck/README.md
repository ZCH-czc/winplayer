# Frozen-host metadata probe

`ManifestCheck` is the static half of `tools/Test-PluginCompatibilityMatrix.ps1`.
It compiles against the **caller-selected published host DLLs**, not current SDK projects.
The wrapper verifies the copied DLL hashes and both the host and probe inventories before/after use.

The selected host is trusted executable SDK code. Provider discovery remains inert: the probe does not
load provider assemblies, invoke a provider, approve packages, create provider HTTP clients or use accounts.
Do not select an untrusted host directory. This is not a security sandbox or a signature verifier.

It reports `compatible`, `incompatible` or `check-failed`. A known minimum-SDK/feature/schema mismatch is
an incompatibility, not a broken test. An unsupported host inspection API, missing DLL, malformed output
or tool failure is a failed check, never proof that a plugin is compatible. No fallback to a newer host occurs.

Use the wrapper for actual ZIP-versus-unpacked verification, exact input binding and a readable matrix.
See [independent revision workflow](../../docs/PLUGIN_REVISIONS.md). The probe alone accepts one unpacked
plugin directory; its process exit codes are 0 (metadata compatible), 1 (incompatible), 2 (check failed).
All results are metadata-only. Runtime contract, offline behavior, real platform service and installed-app
acceptance remain separate checks.
