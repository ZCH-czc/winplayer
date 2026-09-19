# Open Auralis in an IDE

[简体中文](IDE_SETUP.zh-CN.md)

Open **`Auralis.Development.sln`** at the repository root for the complete public development workspace.
It is a classic Visual Studio solution, also readable by Rider and compatible C# IDE tooling on Windows.
The existing **`Auralis.sln`** remains the smaller, unchanged production/CI solution.

## Prerequisites and first run

- Windows 10/11, .NET 8 SDK, Windows SDK targeting support and WebView2 Runtime.
- In Visual Studio 2022, install the **.NET desktop development** workload with .NET 8 support.
- Open the solution, restore NuGet packages, and set **Auralis** as the single startup project.
- Select Debug or Release, then build. The solution's `Any CPU` configuration does not change the
  supported deployment target: the packaged player and bundled native decoder are Windows x64.
- Run only the Auralis project to start the player. The desktop app is not cross-platform merely because
  an IDE can read the solution. Exit an existing tray instance before starting a debug session.

```powershell
dotnet restore Auralis.Development.sln
dotnet build Auralis.Development.sln -c Release --no-restore
dotnet run --project Auralis/Auralis.csproj -c Debug
```

## Solution layout

The 32 projects are grouped into Player, Playback, Media transport, Artwork, Platform contracts and host,
Tests, SDK and samples, and Diagnostics. Documentation is directly accessible through Solution Explorer.
The WebView HTML/CSS/JavaScript files remain under `Auralis/wwwroot` in the player project; Node development
dependencies are optional unless running the browser tests. No extra front-end server is needed to run Auralis.

Building a solution does **not** execute its test/diagnostic programs, install components, approve plugins,
or change the player's dependency graph. Diagnostics that require explicit arguments, prepared files, a
window, or user interaction must be invoked separately according to their README; do not select them as
multiple startup projects. Test programs use their documented `dotnet run` commands, not an assumed test adapter.

`plugin-sdk/ManifestCheck` and `plugin-sdk/UpdateRehearsal` deliberately stay outside the default solution:
they need `-p:FrozenCore=<explicit published player directory>`. They must not silently substitute the current
development SDK for the exact published SDK being tested. See the revision/update guides for invocation.
Unintegrated experimental runtime projects and the deprecated installer are not release build inputs.

## Verification

```powershell
dotnet build Auralis.sln -c Release --no-restore
dotnet run --project Auralis.Tests -c Release
dotnet run --project Auralis.Platform.Host.Tests -c Release
```

Keep `.vs`, `.idea`, `bin`, `obj`, personal run settings, profiles and credentials out of Git.
This change adds IDE organization only; it does not add application capabilities or change the MSIX version.
