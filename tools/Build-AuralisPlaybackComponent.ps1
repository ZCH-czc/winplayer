#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskSource = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$taskDestination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskDestination) { throw 'Choose a new component output directory; never overwrite an installed revision.' }
if ($taskDestination.StartsWith($taskSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Component output must not be inside the input publish directory.'
}
$taskArchive = if ($ArchivePath) { [IO.Path]::GetFullPath($ArchivePath) } else { $null }
if ($taskArchive) {
    if (-not $taskArchive.EndsWith('.auralis-playback.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use the .auralis-playback.zip suffix to distinguish playback components from platform plugins.'
    }
    if ((Test-Path -LiteralPath $taskArchive) -or $taskArchive.StartsWith($taskDestination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $taskArchive.StartsWith($taskSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose a new archive outside both the candidate and input publish directory.'
    }
}
# Explicit allowlist: no player executable, Host/contract copies, Web UI, providers or user data.
$taskNames = @('Auralis.Playback.LibVlc.dll', 'LibVLCSharp.dll', 'THIRD-PARTY-NOTICES.txt',
    'licenses/LibVLC/LGPL-2.1.txt', 'licenses/LibVLC/SOURCES.txt',
    'licenses/LibVLCSharp/libvlcsharp.nuspec', 'licenses/VideoLAN.LibVLC.Windows/videolan.libvlc.windows.nuspec')
foreach ($taskName in $taskNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskName) -PathType Leaf)) { throw "Required build input missing: $taskName" }
}
$taskNative = Join-Path $taskSource 'libvlc/win-x64'
foreach ($taskName in @('libvlc.dll', 'libvlccore.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskNative $taskName) -PathType Leaf)) { throw "Required native runtime missing: $taskName" }
}
foreach ($taskEntry in Get-ChildItem -LiteralPath $taskNative -Recurse -Force) {
    if ($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payloads cannot be packaged.' }
    if (-not $taskEntry.PSIsContainer -and $taskEntry.Extension -ne '.lib') {
        $taskNames += [IO.Path]::GetRelativePath($taskSource, $taskEntry.FullName)
    }
}
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRepository 'Auralis.Playback.LibVlc/Auralis.Playback.LibVlc.csproj') -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
$taskAssemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskSource 'Auralis.Playback.LibVlc.dll')).ProductVersion.Split('+')[0]
if ($taskAssemblyVersion -ne $taskVersion) { throw 'Build input component version does not match the current source.' }
New-Item -ItemType Directory -Path $taskDestination | Out-Null
$taskFiles = @()
foreach ($taskName in $taskNames | Sort-Object -Unique) {
    $taskInput = Join-Path $taskSource $taskName
    if ((Get-Item -LiteralPath $taskInput).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payloads cannot be packaged.' }
    $taskOutput = Join-Path $taskDestination $taskName
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutput) -Force | Out-Null
    Copy-Item -LiteralPath $taskInput -Destination $taskOutput
    $taskFiles += [ordered]@{ path = $taskName.Replace('\', '/'); length = (Get-Item -LiteralPath $taskOutput).Length; sha256 = (Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash }
}
$taskManifest = [ordered]@{
    schemaVersion = 1; kind = 'playback'; id = 'auralis.playback.libvlc'; displayName = 'LibVLC'
    version = $taskVersion; contractApiVersion = 1; minimumHostVersion = '0.1.0'; runtimeIdentifier = 'win-x64'
    capabilities = @('audio', 'videoFrames', 'separateAudio', 'seek', 'rate', 'outputDevices', 'mute')
    entryAssembly = 'Auralis.Playback.LibVlc.dll'; entryType = 'Auralis.Services.LibVlcPlaybackFactory'; files = $taskFiles
}
$taskJson = $taskManifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $taskDestination 'playback.component.json'), $taskJson, [Text.UTF8Encoding]::new($false))
& dotnet run --project (Join-Path $taskRepository 'plugin-sdk/PlaybackInspect') -c Release -- --check-files $taskDestination
if ($LASTEXITCODE -ne 0) { throw 'Component payload validation failed; output is not a valid candidate.' }
Write-Host 'Created an unpacked playback component candidate. Not installed, trusted, signed or runtime-loaded.'
if ($taskArchive) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive) -Force | Out-Null
    $taskArchiveStream = [IO.FileStream]::new($taskArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $taskZip = [IO.Compression.ZipArchive]::new($taskArchiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($taskRelative in @('playback.component.json') + @($taskFiles | ForEach-Object { $_.path })) {
                $taskZipEntry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,
                    (Join-Path $taskDestination $taskRelative), $taskRelative, [IO.Compression.CompressionLevel]::Optimal)
                $taskZipEntry.ExternalAttributes = 0
            }
        } finally { $taskZip.Dispose() }
    } finally { $taskArchiveStream.Dispose() }
    if ((Get-Item -LiteralPath $taskArchive).Length -gt 512MB) { throw 'Archive exceeds the playback importer limit.' }
    Write-Host "Created playback archive: $taskArchive"
    Write-Host ('SHA-256: ' + (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash)
    Write-Host 'Archive transport only; import validation and explicit trust are still required. Not a platform plugin package.'
}
