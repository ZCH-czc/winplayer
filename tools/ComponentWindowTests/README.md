# Isolated native component acceptance

This is a diagnostic executable, **not another player/current entry point**. It source-links the production
`MainWindow.MediaTransportComponents.cs` handler and `MediaTransportServices.cs` composition. It does not
reference or construct the production App, real MainWindow constructor, account services, library, playback
engine, tray, single-instance pipe or Windows media integration.

```powershell
dotnet run --project tools/ComponentWindowTests -c Release -- --check-isolation
dotnet publish tools/ComponentWindowTests -c Release -r win-x64 --self-contained false -o artifacts/component-native-validation-20260908
```

Run the published `ComponentWindowTests.exe` with no arguments (or `--new`) to create a fresh
`acceptance/<random-id>` child under its output directory. All installation and WebView state stays there.
The yellow banner identifies the limited fixture. No existing user profiles are copied. Main WebView requests
outside `http://app.auralis.local` are rejected; other native bridge capabilities are absent. The host's WebView
runtime itself is not a process sandbox or a claim of zero Windows-runtime background traffic.

Navigate **设置 → 平台插件 → 传输组件**. The transport card uses actual native picker/preview/confirmation/
enable/select responses. Platform and playback inventories are explicitly empty placeholders; those modules
are NOT covered by this harness. Ordinary player controls do nothing. Only self-built, reviewed test packages
should be considered; native trust consent remains the human tester's decision. Directory/hash verification
does not authenticate a publisher or make in-process code safe.

The card's restart button closes this shell, drains its preview, and starts a fresh fixture process with the
same generated id (`--resume <32-lowercase-hex-id>`). This validates the new startup composition **only**; it does
not validate Auralis's real single-instance handoff, tray exit, audio continuation or installed-app upgrades.
The same receipt can be inspected without executing a component. Merely selecting a transport component does
not activate it; media-resource preparation and DLL execution are covered separately by transport tests.

`native-events.jsonl` records fixed operation/result labels only, never preview tokens, file paths, signed
media URLs, credentials or payloads. Test data is retained under the owned output for inspection; no broad
recursive cleanup is performed. Do not export `acceptance/` or `isolation-checks/` as source.

## Native checklist (not yet claimed passed)

- Empty installed list; no fictional providers. Window resize and keyboard navigation.
- Open actual Windows picker, cancel, verify card becomes usable again.
- Select self-built `.auralis-transport.zip`, verify native-held digest/descriptor preview and unchecked trust.
- Cancel preview; verify no installed receipt or retained preview payload.
- Human tester grants trust; verify import is OFF and does not change current startup selection.
- Enable/select, restart fixture; distinguish choice from actual activation. Restore bundled choice.
- Real player single-instance/restart/upgrades, physical DPI, playback and account migration require separate acceptance.

2026-09-08: native Computer Use launch approval timed out before a window could be targeted. No native clicks,
trust consent or installation were performed. This is an outstanding acceptance item, not a passing result.
