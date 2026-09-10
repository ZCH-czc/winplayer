#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishedDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath((Join-Path $PublishedDirectory 'Auralis.MediaTransport.Http.dll'))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskPublish = [IO.Path]::GetDirectoryName($taskSource)
$taskArchive = if ($ArchivePath) { [IO.Path]::GetFullPath($ArchivePath) } else { $null }
function Assert-LocalCandidatePath([string]$Path) {
    if ($Path.StartsWith('\\') -or ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path))).DriveType -eq [IO.DriveType]::Network) { throw 'Only local candidate paths are supported.' }
    for ($taskParent = $Path; $taskParent; $taskParent = Split-Path -Parent $taskParent) {
        if ((Test-Path -LiteralPath $taskParent) -and ((Get-Item -LiteralPath $taskParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked candidate paths are not accepted.' }
    }
}
Assert-LocalCandidatePath $taskSource
Assert-LocalCandidatePath $taskOutput
if ($taskOutput.StartsWith($taskPublish + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the published input.' }
if ($taskArchive) {
    Assert-LocalCandidatePath $taskArchive
    if (-not $taskArchive.EndsWith('.auralis-transport.zip', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use the .auralis-transport.zip suffix, not a platform/playback package suffix.' }
    if ((Test-Path -LiteralPath $taskArchive) -or $taskArchive.StartsWith($taskOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $taskArchive.StartsWith($taskPublish + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a new archive outside both input and candidate directory.' }
}
if (Test-Path -LiteralPath $taskOutput) { throw 'Output must be a new directory; existing files are never replaced.' }
if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw 'Published HTTP transport assembly is missing.' }
for ($taskPath = $taskSource; $taskPath; $taskPath = Split-Path -Parent $taskPath) {
    if ((Get-Item -LiteralPath $taskPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked sources are not accepted.' }
}
$taskVersion = [Reflection.AssemblyName]::GetAssemblyName($taskSource).Version
if ($taskVersion.ToString(3) -ne '0.6.0') { throw 'Update this format builder when the HTTP factory descriptor changes.' }
$taskLength = (Get-Item -LiteralPath $taskSource).Length
if ($taskLength -le 0 -or $taskLength -gt 64MB) { throw 'Invalid payload size.' }
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskDll = Join-Path $taskOutput 'Auralis.MediaTransport.Http.dll'
Copy-Item -LiteralPath $taskSource -Destination $taskDll
$taskManifest = [ordered]@{
    schemaVersion = 1; kind = 'mediaTransport'; id = 'auralis.transport.http'; displayName = 'HTTP media transport'
    version = '0.6.0'; contractApiVersion = 1; minimumHostVersion = '0.1.0'; runtimeIdentifier = 'win-x64'
    capabilities = @('authorizedHttp', 'completeBuffering', 'prefetch', 'independentResources', 'sharedBudget', 'sharedRequests')
    entryAssembly = 'Auralis.MediaTransport.Http.dll'; entryType = 'Auralis.MediaTransport.HttpMediaTransportFactory'
    files = @(@{ path = 'Auralis.MediaTransport.Http.dll'; length = (Get-Item -LiteralPath $taskDll).Length;
        sha256 = (Get-FileHash -LiteralPath $taskDll -Algorithm SHA256).Hash })
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'transport.component.json') -Encoding utf8NoBOM
Write-Host "Transport candidate: $taskOutput"
& dotnet run --project (Join-Path $PSScriptRoot '../plugin-sdk/TransportInspect') -c Release -- --check-files $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Transport candidate failed static validation.' }
if ($taskArchive) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive) -Force | Out-Null
    $taskStream = [IO.FileStream]::new($taskArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($taskName in @('transport.component.json', 'Auralis.MediaTransport.Http.dll')) {
                $taskEntry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, (Join-Path $taskOutput $taskName), $taskName, [IO.Compression.CompressionLevel]::Optimal)
                $taskEntry.ExternalAttributes = 0
            }
        } finally { $taskZip.Dispose() }
    } finally { $taskStream.Dispose() }
    if ((Get-Item -LiteralPath $taskArchive).Length -gt 80MB) { throw 'Transport archive exceeds importer limit.' }
    & dotnet run --project (Join-Path $PSScriptRoot '../plugin-sdk/TransportInspect') -c Release -- --check-archive $taskArchive
    if ($LASTEXITCODE -ne 0) { throw 'Transport ZIP failed static validation.' }
    Write-Host "Transport ZIP: $taskArchive"
    Write-Host ('SHA-256: ' + (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash)
}
Write-Host 'Development candidate only. No installation, approval, code activation, user settings or current changes. Import validation and explicit trust remain required.'
