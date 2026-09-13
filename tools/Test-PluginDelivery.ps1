#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackage,
    [Parameter(Mandatory)][string]$PreviousDirectory,
    [Parameter(Mandatory)][string]$UpdatedPackage,
    [Parameter(Mandatory)][string]$UpdatedDirectory,
    [Parameter(Mandatory)][string]$FrozenCore,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$CoreBaseline,
    [string[]]$CoreSourceDirectories = @(),
    [string]$CoreSourceListFile,
    [switch]$TrustPluginCode,
    [string]$Profile,
    [ValidateRange(30,600)][int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if($CoreSourceListFile){
    if($CoreSourceDirectories.Count){throw 'Choose one core source list.'}
    $CoreSourceDirectories=@(Get-Content -LiteralPath (Get-DeliveryPath $CoreSourceListFile) -Raw|ConvertFrom-Json)
    if($CoreSourceDirectories.Count -gt 32){throw 'Core source list exceeds limit.'}
}
if ($Profile -and !$TrustPluginCode) { throw 'A profile executes trusted test/plugin code; specify -TrustPluginCode after reviewing it.' }
$taskCore = Get-DeliveryPath $FrozenCore
$taskOutput = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'EvidenceDirectory must be new; previous evidence is never overwritten or imported as current.' }
$taskParent = Get-DeliveryPath (Split-Path $taskOutput -Parent)
foreach ($protected in @($taskCore,$PreviousDirectory,$UpdatedDirectory) + $CoreSourceDirectories) {
    $resolved = Get-DeliveryPath $protected
    if ($taskOutput.StartsWith($resolved.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be outside frozen inputs.' }
}
$null = New-Item -ItemType Directory -Path $taskOutput
$stages = @{}
foreach ($name in @('package','static','frozenHost','contract','offline','ui','live','native')) {
    $stages[$name] = @{Status='not-run';Reason='NotSelected'}
}
$stages.live.Reason='RequiresSeparateCurrentCandidatePlatformAcceptance'
$stages.native.Reason='RequiresInstalledAppAndRealPlaybackAcceptance'
$report = @{SchemaVersion=1;RunId=[guid]::NewGuid().ToString('N');StartedUtc=[DateTime]::UtcNow.ToString('O');
    Mode=$(if($TrustPluginCode){'trusted-offline'}else{'inspect'}); Stages=$stages;
    Installed=$false;Uploaded=$false;RealPlatformTested=$false;NativeAppTested=$false;
    Warning='Trusted plugin execution is not an OS sandbox. Hashes are provenance, not publisher signatures.'}
$toolSources=Get-DeliveryTree (Join-Path $taskRoot 'plugin-sdk/ContractCheck') -Source
foreach($file in @($PSCommandPath,(Join-Path $PSScriptRoot 'PluginDelivery.psm1'))){$toolSources[$file]=(Get-FileHash -LiteralPath $file).Hash}
$report.ToolSources=$toolSources
$active='package'; $frozen=@{}; $packageInputs=@(); $toolHashes=@{}; $profileHash=$null
function Save-Json($Value,[string]$Name) {
    [IO.File]::WriteAllText((Join-Path $taskOutput $Name),($Value|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
}
function Read-Frozen {
    $files=Get-DeliveryTree $taskCore
    foreach($source in $CoreSourceDirectories) {
        $sourceFiles=Get-DeliveryTree $source -Source
        foreach($key in $sourceFiles.Keys){$files[$key]=$sourceFiles[$key]}
    }
    return $files
}
function Run-Checked([string]$Executable,[string[]]$Arguments) {
    $result=Invoke-DeliveryProcess $Executable $Arguments $taskRoot $TimeoutSeconds
    $report.LastProcess=@{Stage=$active;ExitCode=$result.ExitCode;TimedOut=$result.TimedOut}
    if($result.TimedOut){throw 'CheckTimedOut'}
    if($result.ExitCode -ne 0){throw 'CheckProcessFailed'}
    return $result.Output
}
Save-Json @{RunId=$report.RunId;StartedUtc=$report.StartedUtc} 'run.json'
try {
    $old=Get-DeliveryPackage $PreviousPackage $PreviousDirectory
    $new=Get-DeliveryPackage $UpdatedPackage $UpdatedDirectory
    $packageInputs=@($old,$new)
    if($old.Id -cne $new.Id -or [version]$new.Version -le [version]$old.Version -or $old.PayloadSha256 -eq $new.PayloadSha256){throw 'NotAnIndependentPluginUpgrade'}
    $report.Previous=$old; $report.Candidate=$new
    $stages.package=@{Status='passed';Reason='ExactArchiveAndUnpackedInventoryMatch'}
    $active='frozenHost'; $frozen=Read-Frozen
    if($CoreBaseline){
        $baseline=Get-Content -LiteralPath (Get-DeliveryPath $CoreBaseline) -Raw|ConvertFrom-Json -AsHashtable
        if(!(Test-DeliveryTree $baseline $frozen)){throw 'FrozenBaselineMismatch'}
    }
    $report.Host=@{FileCount=$frozen.Count;SnapshotSha256=(Get-DeliveryDigest $frozen);
        ExistingBaselineChecked=[bool]$CoreBaseline;SourcesIncluded=($CoreSourceDirectories.Count -gt 0);
        SdkBinaryVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskCore 'Auralis.Platform.Host.dll')).ProductVersion}
    Save-Json $frozen 'frozen-core.json'
    $active='static'
    $tool=Join-Path $taskOutput 'checker'
    $null=Run-Checked dotnet @('build',(Join-Path $taskRoot 'plugin-sdk/ContractCheck/Auralis.PluginContractCheck.csproj'),'-c','Release','--no-restore',"-p:FrozenCore=$taskCore",'-o',$tool,'-v:q')
    foreach($assembly in @('Auralis.Platform.Host.dll','Auralis.Platform.Abstractions.dll')) {
        if((Get-FileHash -LiteralPath (Join-Path $tool $assembly)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskCore $assembly)).Hash){throw 'CheckerSdkMismatch'}
    }
    $toolHashes=Get-DeliveryTree $tool
    Save-Json $toolHashes 'frozen-checker.json'
    $checker=Join-Path $tool 'Auralis.PluginContractCheck.dll'
    foreach($revision in @('Previous','Candidate')) {
        $output=Run-Checked dotnet @($checker,'--inspect',$report[$revision].Directory)
        $check=$output|ConvertFrom-Json -AsHashtable
        if($check.Passed -isnot [bool] -or !$check.Passed -or !$check.RuntimeManifestCompatible -or $check.Executed -or $check.Plugins -ne 1){throw 'StaticCompatibilityFailed'}
        Save-Json $check ("static-$revision.json")
    }
    $stages.static=@{Status='passed';Reason='ActualFrozenSdkManifestCompatibility';Revisions=2;PluginCodeExecuted=$false}
    if($TrustPluginCode) {
        $active='contract'
        foreach($revision in @('Previous','Candidate')) {
            $output=Run-Checked dotnet @($checker,'--verify',$report[$revision].Directory,'--trust-plugin-code','--timeout-seconds','30')
            $check=$output|ConvertFrom-Json -AsHashtable
            if($check.Passed -isnot [bool] -or !$check.Passed -or !$check.Executed -or $check.HttpRequests -ne 0){throw 'RuntimeContractFailed'}
            Save-Json $check ("contract-$revision.json")
        }
        $stages.contract=@{Status='passed';Reason='FrozenSdkDescriptorsInterfacesAndLifecycle';Revisions=2;NetworkViaHost=0}
    }
    if($Profile) {
        $taskProfile=Get-DeliveryPath $Profile
        $profileHash=(Get-FileHash -LiteralPath $taskProfile).Hash
        $report.Profile=@{File=[IO.Path]::GetFileName($taskProfile);Sha256=$profileHash;ExplicitTrustedSelection=$true}
        foreach($level in @('offline','ui')) {
            $active=$level
            # Fresh run directories only: an old stage result cannot accidentally pass this run.
            $null=Run-Checked pwsh @('-NoProfile','-File',$taskProfile,'-Check',$level,'-PreviousDirectory',$old.Directory,
                '-UpdatedDirectory',$new.Directory,'-FrozenCore',$taskCore,'-EvidenceDirectory',$taskOutput)
            $check=Get-Content -LiteralPath (Join-Path $taskOutput "$level-result.json") -Raw|ConvertFrom-Json -AsHashtable
            if($check.Passed -isnot [bool] -or !$check.Passed -or $check.Check -cne $level -or $check.Synthetic -ne $true -or
                $check.Checks -isnot [long] -and $check.Checks -isnot [int] -or $check.Checks -lt 1){throw 'ProfileResultInvalid'}
            $stages[$level]=@{Status='passed';Reason='ExplicitProfileSyntheticChecks';Checks=$check.Checks;Synthetic=$true}
        }
    }
} catch {
    # Never forward plugin output, exceptions or platform response bodies to the aggregate report.
    $reason=$_.Exception.Message
    if($reason -notin @('LinkedInput','DirectoryRequired','EmptyInput','UnsafePackageEntry','PackageLimit','InputLimit',
        'PackageInventoryMismatch','PackageHashMismatch','ManifestLimit','PackageIdentityInvalid','NotAnIndependentPluginUpgrade',
        'FrozenBaselineMismatch','CheckerSdkMismatch','StaticCompatibilityFailed','RuntimeContractFailed','ProfileResultInvalid',
        'CheckTimedOut','CheckProcessFailed')){$reason='CheckFailed'}
    $stages[$active]=@{Status='failed';Reason=$reason}
    Write-Warning "Delivery check failed at $active; no installation or publication was attempted."
} finally {
    try {
        foreach($file in $toolSources.Keys){if((Get-FileHash -LiteralPath $file).Hash -ne $toolSources[$file]){throw 'ToolSourceChanged'}}
        if(!$frozen.Count -or !(Test-DeliveryTree $frozen (Read-Frozen))){throw 'FrozenHostChangedOrNotCaptured'}
        if($toolHashes.Count -and !(Test-DeliveryTree $toolHashes (Get-DeliveryTree $tool))){throw 'CheckerChanged'}
        if($profileHash -and (Get-FileHash -LiteralPath $taskProfile).Hash -ne $profileHash){throw 'ProfileChanged'}
        foreach($input in $packageInputs) {
            $after=Get-DeliveryPackage $input.Archive $input.Directory
            if($after.ArchiveSha256 -ne $input.ArchiveSha256 -or $after.PayloadSha256 -ne $input.PayloadSha256){throw 'PackageChangedDuringCheck'}
        }
        if($stages.frozenHost.Status -ne 'failed'){$stages.frozenHost=@{Status='passed';Reason='BeforeAfterExactInventoryAndHash';Files=$frozen.Count}}
    } catch { $stages.frozenHost=@{Status='failed';Reason='FrozenInputChangedOrNotCaptured'} }
    $report.Assessment=Get-DeliveryAssessment $stages
    $report.CompletedUtc=[DateTime]::UtcNow.ToString('O')
    # Every artifact hash is bound to this run and its package + host identities. No external report import.
    $evidenceFiles=Get-DeliveryTree $taskOutput
    $report.Evidence=@{}
    foreach($key in $evidenceFiles.Keys){$report.Evidence[[IO.Path]::GetRelativePath($taskOutput,$key).Replace('\','/')]=$evidenceFiles[$key]}
    Save-Json $report 'delivery-report.json'
    $lines=@('# Plugin independent delivery check','',"Run: $($report.RunId)",'',"Mode: $($report.Mode)",'',
        '| Check | Status | Scope |','| --- | --- | --- |')
    foreach($name in @('package','static','frozenHost','contract','offline','ui','live','native')){$lines+="| $name | $($stages[$name].Status) | $($stages[$name].Reason) |"}
    $lines+=@('',"Local compatibility ready: $($report.Assessment.LocalReady)","Release ready: $($report.Assessment.ReleaseReady)",'',
        'No installation, account access, live platform test or native player acceptance is performed by this gate.',
        'A zero exit code means the selected checks passed. It does not mean release approval.',
        'Synthetic browser scaling is not Windows PerMonitorV2 / real audio acceptance.')
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'delivery-report.md'),$lines)
}
Write-Host "Delivery report: $taskOutput"
$null=Test-DeliveryEvidence $taskOutput
if($report.Assessment.HasFailure){exit 1}
exit 0
