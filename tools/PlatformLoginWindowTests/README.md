# Native login lifecycle regression

This isolated diagnostic source-links the actual `Auralis/MainWindow.PlatformLogin.cs` partial.
It uses a real WPF dispatcher, hidden owner HWND in automated mode, the public platform host and a
self-built synthetic plugin. It does **not** construct the production App, use a WebView, open a
login page, read Windows credentials, register a player, play media or inspect user profiles.
The small App/Window shell and configuration-refresh/JavaScript sinks are test substitutes, not a
claim of end-to-end account or WebView acceptance. HTTP and credential services throw if accessed.

```powershell
dotnet run --project tools/PlatformLoginWindowTests -c Release -- --run
dotnet publish tools/PlatformLoginWindowTests -c Release -r win-x64 --self-contained false -o artifacts/platform-login-native
```

The published executable defaults to `--show`: it runs the same checks and leaves a clearly labelled
results-only window for inspection. No authentication dialog is shown. `--run` exits 0 only when all
checks pass; invalid arguments exit 2. Each launch creates its own random
`Auralis-login-lifecycle-<guid>` directory in the system temporary folder with only the synthetic DLL
and manifest. It does not install into a plugin manager or write approval receipts. Those diagnostic
files are retained, because the DLL may remain mapped until process exit; no broad cleanup is performed.

Covered: background Closed dispatch, Show/Close failures and retry, Activate failure, sign-out despite
Close/cancellation exceptions, optional NativeLogin capability, authenticated automatic close,
stale callbacks after replacement/sign-out, and closing all optional sessions.
These checks do not replace actual provider login, Windows scaling/theme or audio/video acceptance.
