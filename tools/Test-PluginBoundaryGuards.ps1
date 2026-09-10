#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$boundarySource = Split-Path -Parent $PSScriptRoot
$boundaryFixture = Join-Path $boundarySource ('artifacts/boundary-guard-tests-' + [Guid]::NewGuid().ToString('N'))
$boundaryProjects = @('Auralis', 'Auralis.Platform.Host', 'Auralis.Platform.Abstractions',
    'Auralis.Playback.Abstractions', 'Auralis.Playback.Host', 'Auralis.Playback.LibVlc',
    'Auralis.MediaTransport.Abstractions', 'Auralis.MediaTransport.Host', 'Auralis.MediaTransport.Http',
    'Auralis.Artwork.Abstractions', 'Auralis.Artwork.Host', 'Auralis.Artwork.Http')
# Synthetic source/build inputs only. Do not load DLLs, access profiles, approve plugins, or use HTTP.
foreach ($boundaryTree in @($boundaryProjects + @('Auralis.Tests','plugin-sdk'))) {
    foreach ($boundaryFile in Get-ChildItem -LiteralPath (Join-Path $boundarySource $boundaryTree) -Recurse -File |
        Where-Object { [IO.Path]::GetRelativePath($boundarySource, $_.FullName) -notmatch '(?:^|[\\/])(?:bin|obj|artifacts)[\\/]' -and $_.Extension -in @('.cs','.js','.css','.html','.csproj','.props','.targets') }) {
        $boundaryRelative = [IO.Path]::GetRelativePath($boundarySource, $boundaryFile.FullName)
        $boundaryDestination = Join-Path $boundaryFixture $boundaryRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $boundaryDestination) -Force | Out-Null
        Copy-Item -LiteralPath $boundaryFile.FullName -Destination $boundaryDestination
    }
}
New-Item -ItemType Directory -Path (Join-Path $boundaryFixture 'tools') -Force | Out-Null
$boundaryScript = Join-Path $boundaryFixture 'tools/Test-PluginBoundary.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-PluginBoundary.ps1') -Destination $boundaryScript
$boundaryCount = 0
function Assert-BoundaryMutation([string]$Relative, [string]$Contents, [bool]$Reject) {
    $destination = [IO.Path]::GetFullPath((Join-Path $boundaryFixture $Relative))
    if (-not $destination.StartsWith($boundaryFixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture target escaped its isolated root.' }
    $exists = Test-Path -LiteralPath $destination
    $original = [byte[]]::new(0)
    if ($exists) { $original = [IO.File]::ReadAllBytes($destination) }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    try {
        [IO.File]::WriteAllText($destination, $Contents)
        $failure = $null
        try { & $boundaryScript 6>$null } catch { $failure = $_.Exception.Message }
        if ($Reject -and ($null -eq $failure -or $failure -notmatch 'public runtime')) { throw "Expected public-runtime rejection for $Relative; actual: $failure" }
        if (-not $Reject -and $failure) { throw "Valid fixture rejected: $failure" }
        $script:boundaryCount++
        Write-Host "PASS guard $($script:boundaryCount): $Relative / reject=$Reject"
    }
    finally {
        if ($exists) { [IO.File]::WriteAllBytes($destination, $original) }
        else {
            # Retain the GUID fixture for inspection; clear only this test's generated file.
            $empty = if ([IO.Path]::GetExtension($destination) -in @('.csproj','.props','.targets')) { '<Project />' } else { '' }
            [IO.File]::WriteAllText($destination, $empty)
        }
    }
}
& $boundaryScript 6>$null
foreach ($boundaryProject in $boundaryProjects) {
    Assert-BoundaryMutation "$boundaryProject/Nested/BoundaryProbe.cs" 'internal static class BoundaryProbe { const string Endpoint = "https://api.qq.com/"; }' $true
}
Assert-BoundaryMutation 'Auralis.Artwork.Http/Nested/BoundaryProbe.cs' 'internal static class BoundaryProbe { const string Key = "SAPISID"; }' $true
Assert-BoundaryMutation 'Auralis.MediaTransport.Http/Nested/BoundaryProbe.cs' 'internal static class BoundaryProbe { const string Endpoint = "https://r1.googlevideo.com/"; }' $true
foreach ($boundaryInput in @(
    '<ItemGroup><Reference Include="Auralis.Platform"><HintPath>../private-platforms/provider.dll</HintPath></Reference></ItemGroup>',
    '<ItemGroup><ProjectReference Include="../Auralis.Platform/Auralis.Platform.csproj" /></ItemGroup>',
    '<ItemGroup><Compile Include="../private-platforms/Provider.cs" /></ItemGroup>',
    '<Import Project="../private-platforms/Provider.targets" />',
    '<ItemGroup><Content Include="../packages/Auralis.Plugin.Example.dll" /></ItemGroup>'
)) {
    Assert-BoundaryMutation 'Auralis.Artwork.Http/BoundaryProbe.targets' ("<Project>$boundaryInput</Project>") $true
}
Assert-BoundaryMutation 'Auralis.Artwork.Http/Nested/BoundaryProbe.cs' 'internal static class BoundaryProbe { const string Endpoint = "https://images.example.com/"; }' $false
Assert-BoundaryMutation 'Auralis.Artwork.Http/BoundaryProbe.targets' '<Project><!-- Migration documentation: private-platforms is not an active dependency. --></Project>' $false
& $boundaryScript 6>$null
Write-Host "PASS $boundaryCount isolated boundary mutations. Retained fixture: $boundaryFixture"
