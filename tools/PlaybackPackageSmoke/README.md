# Explicit dynamic playback smoke

```powershell
dotnet run --project tools/PlaybackPackageSmoke -c Release -- --run <component-directory> --trust-plugin-code
dotnet run --project tools/PlaybackPackageSmoke -c Release -- --run-archive <component.auralis-playback.zip> --trust-plugin-code --install-root <NEW-isolated-directory>
```

This Windows-only diagnostic references Playback.Host and the contracts, **not** LibVLCSharp or the
bundled implementation. Its output must contain neither the decoder implementation nor native LibVLC.
It activates the explicitly trusted package through the actual package loader/registry, generates silent
WAV/AVI, exercises pause/seek/repeat and three video/audio cycles, and checks this process's libvlc modules
were loaded from the package directory. It also rejects any visible native output window owned by itself.

By default it does not install or persist approval. The optional `--install-root <NEW isolated directory>`
explicitly exercises preview, exact approval, disabled import, enable/selection, reopening the receipt store,
then actual playback from the installed immutable revision. This path must not already exist; it is retained
for inspection after the test because CLR/native mappings may live until process exit. Never use a user
installation/current directory. The flag is developer approval for this synthetic test, not end-user UI.
`--run-archive` requires the new installation directory. It uses the real archive preview/import methods,
disposes the preview, reopens persisted selection and verifies actual native module paths from that
installed revision. The input ZIP is read-only and the installation is retained for inspection.
It does not access user accounts/media, change the application default component or touch another player.
This is trusted in-process code, not a sandbox. Managed stage waits are bounded; a hung
constructor or native crash cannot be contained by this executable. Use a bounded parent process in CI.
No real speaker output, acoustic synchronization, UI/DPI matrix or concurrent engine-version acceptance
is implied. Native module unloading is not guaranteed before process exit; upgrades require restart.
