[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskRoot ('artifacts/page-upgrade-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskOutput | Out-Null
& dotnet build (Join-Path $taskRoot 'Auralis.Platform.Host.Tests') -c Release
if($LASTEXITCODE -ne 0){throw 'Host tests build failed'}
$taskHost=Join-Path $taskRoot 'Auralis.Platform.Host.Tests/bin/Release/net8.0'
$taskFrozen=@{}
foreach($taskFile in Get-ChildItem -LiteralPath $taskHost -File){$taskFrozen[$taskFile.FullName]=(Get-FileHash -LiteralPath $taskFile.FullName).Hash}
foreach($taskRevision in @(1,2)){
    $taskBuild=Join-Path $taskOutput "build-$taskRevision"
    & dotnet build (Join-Path $taskRoot 'plugin-sdk/samples/DeclarativePages') -c Release "-p:DemoRevision=$taskRevision" -o $taskBuild
    if($LASTEXITCODE -ne 0){throw 'Sample build failed'}
    $taskPayload=Join-Path $taskOutput "v$taskRevision/sample.pages"
    New-Item -ItemType Directory -Path $taskPayload -Force | Out-Null
    $taskHashes=@{}
    foreach($taskName in @('Auralis.Sample.Pages.dll','platform.plugin.json')){
        Copy-Item -LiteralPath (Join-Path $taskBuild $taskName) -Destination $taskPayload
        $taskHashes[$taskName]=(Get-FileHash -LiteralPath (Join-Path $taskPayload $taskName)).Hash
    }
    $taskApprovals=Join-Path $taskOutput "v$taskRevision/.approvals"
    New-Item -ItemType Directory -Path $taskApprovals | Out-Null
    # Explicit test-only approval for this original sample. Never touches user plugin state.
    $taskReceipt=@{SchemaVersion=2;PluginId='sample.pages';Files=$taskHashes;CredentialAliases=@()}|ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $taskApprovals 'sample.pages.json'),$taskReceipt)
}
& dotnet (Join-Path $taskHost 'Auralis.Platform.Host.Tests.dll') --page-upgrade (Join-Path $taskOutput 'v1') (Join-Path $taskOutput 'v2')
if($LASTEXITCODE -ne 0){throw 'Plugin-only upgrade failed'}
foreach($taskFile in $taskFrozen.Keys){if((Get-FileHash -LiteralPath $taskFile).Hash -ne $taskFrozen[$taskFile]){throw 'Frozen host files changed'}}
$taskV1=(Get-FileHash -LiteralPath (Join-Path $taskOutput 'v1/sample.pages/Auralis.Sample.Pages.dll')).Hash
$taskV2=(Get-FileHash -LiteralPath (Join-Path $taskOutput 'v2/sample.pages/Auralis.Sample.Pages.dll')).Hash
if($taskV1 -eq $taskV2){throw 'Expected distinct plugin revisions'}
$taskSdk=([xml](Get-Content -Raw (Join-Path $taskRoot 'Auralis.Platform.Host/Auralis.Platform.Host.csproj'))).Project.PropertyGroup.Version
$taskReport=@{Passed=$true;HostSdk=$taskSdk;FrozenRuntimeFiles=$taskFrozen.Count;PluginV1Sha256=$taskV1;PluginV2Sha256=$taskV2;
    NewPageRequiresCoreChange=$false;NewSettingsRequireCoreChange=$false;SettingsConsumedByPlugin=$true;TypedEntityNavigation=$true;MediaCardsUseExistingPlayback=$true;RealAccountTested=$false;InstalledAppUpgradeTested=$false}
[IO.File]::WriteAllText((Join-Path $taskOutput 'result.json'),($taskReport|ConvertTo-Json))
Write-Host "PASS: all frozen host test runtime files unchanged. Evidence: $taskOutput"
