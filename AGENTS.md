# Auralis public core contributor rules

Read README.md, docs/PLUGIN_SEPARATION.md, docs/UI_AND_INTERACTION.md and
docs/NATIVE_WEBVIEW_PROTOCOL.md before changes.

- The core must build and run without commercial provider code or installed plugins.
- Discovery reads manifests/integrity metadata only. Never load DLLs or create HTTP clients during discovery.
- Plugin implementation, credentials, user state, WebView profiles, personal media and private Git history
  do not belong in this repository.
- Never expose cookies, tokens, stream headers or signed URLs to the main WebView.
- Do not bypass DRM, rights, regional limits, challenges or platform authentication.
- Preserve local playback, Windows integration, keyboard access, DPI and reduced motion.
- Trusted in-process plugins are not a sandbox. Local checksum approval is not publisher signing.
- Keep provider IDs and saved identities stable; missing plugins must not delete playlist entries.
- Use apply_patch for edits; preserve unrelated user changes.

Run dotnet build Auralis.sln -c Release --no-restore, dotnet run --project Auralis.Tests -c Release,
and dotnet run --project Auralis.Platform.Host.Tests -c Release. Validate release changes in clean publish
output. NativeTests is an interactive diagnostic CLI, not a no-argument test runner.

For Web UI changes, install development dependencies with `npm ci --ignore-scripts`, then run
`npm run test:logic` and `npm run test:ui` from the repository root (Node.js >=20 and installed Edge).
The local lock-installed Playwright version is required; the runner does not use personal NODE_PATH.
Use `npm run test:ui -- --help` for individual suites. Set AURALIS_UI_ROOT to a published wwwroot to
check shipped resources. Fixtures use synthetic messages and isolated browser profiles, not private
plugins or accounts. Do not treat these browser checks as native playback/login acceptance.
Keep package.json and package-lock.json together; node_modules is development-only and must not be committed.

Run `pwsh -NoProfile -File tools/Test-PluginBoundary.ps1` for bundled runtime boundaries and
`pwsh -NoProfile -File tools/Test-PluginBoundaryGuards.ps1` when changing those checks.
The latter retains an isolated source fixture under artifacts and exercises 21 positive/negative cases;
it does not execute injected code, access accounts or install plugins. These targeted guards are not a
complete source/license/security audit. Built-in playback, transport and artwork must not conceal provider
implementations, but local-only components do not need to become separately installed plugins.

Also run dotnet run --project plugin-sdk/ContractCheck.Tests -c Release for SDK/tool changes.
Run dotnet run --project Auralis.Playback.Host.Tests -c Release for playback contract/registry changes;
see Auralis.Playback.Host/README.md for the real-decoder smoke test and current loading limitations.
See plugin-sdk/ContractCheck/README.md for static inspection versus explicit trusted execution.
Contract-check success is not real-account or playback acceptance; the tool is not a sandbox.
See docs/COMPONENTIZATION_PLAN.md for current ownership boundaries and acceptance gates.
Online platform implementations must stay outside the core; further externalization of local playback,
lyrics display, Windows integration or LAN is not a completion requirement.

Do not push, publish, change repository visibility, sign a public release, or install into the user's
profile without explicit authorization. Never include an unsigned MSIX or a private signing key in a release.
