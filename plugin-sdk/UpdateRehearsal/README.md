# Isolated update / reimport rehearsal

This developer probe references the caller-selected **published** Host and Abstractions DLLs.
It links the unchanged `SavedPlaylistStore` source and always gives it a new synthetic path. It never uses the
production default constructor, user profiles, credentials, accounts, audio, network or a real player window.

Use `tools/New-PluginUpdatePlan.ps1 -Rehearse`, not the probe directly. The wrapper first binds actual old/new
archives and unpacked files to compatible-host and local-delivery evidence. It checks frozen core/source hashes,
the probe's SDK DLL copies and runtime before/after execution. A failed or incomplete plan cannot rehearse.

The probe uses the real frozen `PlatformPluginManager` APIs to prepare, cancel, reject a changed preview,
confirm, explicitly enable, create next-session plans, and reimport the original package. All writes and temporary
staging cleanup stay in a newly created `rehearsal/isolated-manager` directory. Committed revisions remain intact.
No registry file is manually patched to simulate rollback, and no provider assembly is loaded.

When the selected published Host exposes management review and `PrepareRecoveryAsync`, the probe also
checks real update/recovery metadata, writes the safe preview projections, and exercises explicit recovery
through the production batch trust/cancel/confirm path. Reflection preserves compatibility with older frozen
SDKs; reports explicitly state whether these additional APIs were tested. This still does not drive a native
picker or prove the WebView2 callback timing. No platform-specific branch is added to the probe.

It verifies exact original payload restoration and unchanged synthetic local/online/missing-provider saved
references, a synthetic local-library canary and non-sensitive preferences. Candidate failure is an **injected
decision marker**, not an observed plugin/runtime failure. A next-session plan is not a launched native process.

This is not automatic rollback, live DLL replacement, a credentials/data migration test, an installer or an app UI.
The existing app still requires explicit import/trust/enable/restart. Reimporting an older archive requires the same
trust review as any other revision. The wrapper's JSON/Markdown are local evidence, not signed release approval.

See [update plans](../../docs/PLUGIN_UPDATES.md) for input binding, decisions and limitations.
