#Requires -Version 7.2
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginUpdatePlan.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture=Join-Path $repo ('artifacts/update-plan-guards-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixture
$script:checks=0
function Check([bool]$Value,[string]$Label){if(!$Value){throw "FAIL $Label"};$script:checks++}
function Reject([scriptblock]$Action,[string]$Label){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed $Label}
function Clone($Value){return $Value|ConvertTo-Json -Depth 25|ConvertFrom-Json -AsHashtable}
function Write-Json([string]$File,$Value){[IO.File]::WriteAllText($File,($Value|ConvertTo-Json -Depth 25))}
$row=@{Previous=@{Status='compatible';Reasons=@()};Candidate=@{Status='compatible';Reasons=@()};Unchanged=$true}
$delta=@{Providers=@{Removed=@()};AccessReviewRequired=$false}
$ready=@{LocalReady=$true;HasFailure=$false;ReleaseReady=$false}
Check ((Get-PluginUpdateDecision $row $delta $true $ready).Kind -eq 'plugin-only') 'Exact local evidence permits plugin-only plan'
$result=Get-PluginUpdateDecision $row $delta $true $ready
Check ($result.RequiresTrust -and $result.RequiresRestart -and !$result.ReleaseReady -and !$result.AutomaticInstallAllowed) 'A plan is never automatic installation authority'
Check ((Get-PluginUpdateDecision $row $delta $false $ready).Kind -eq 'verification-required') 'Incomplete matrix'
Check ((Get-PluginUpdateDecision $row $delta $true $null).Kind -eq 'verification-required') 'Missing delivery evidence'
Check ((Get-PluginUpdateDecision $row $delta $true @{LocalReady=$false;HasFailure=$false}).Kind -eq 'verification-required') 'Static checks do not imply complete delivery'
Check ((Get-PluginUpdateDecision $row $delta $true @{LocalReady=$true;HasFailure=$true}).Kind -eq 'verification-required') 'Existing failure cannot be erased by local success'
$changed=Clone $row;$changed.Unchanged=$false
Check ((Get-PluginUpdateDecision $changed $delta $true $ready).Kind -eq 'verification-required') 'Changed host'
$changed=Clone $row;$changed.Candidate.Status='check-failed'
Check ((Get-PluginUpdateDecision $changed $delta $true $ready).Kind -eq 'verification-required') 'Probe failure'
foreach($reason in @('HostSdkIncompatible','HostFeatureUnsupported','HostApiIncompatible','ManifestSchemaUnsupported')){
    $changed=Clone $row;$changed.Candidate=@{Status='incompatible';Reasons=@($reason)}
    Check ((Get-PluginUpdateDecision $changed $delta $true $null).Kind -eq 'host-upgrade-required') "$reason requires a host decision"
}
$changed.Candidate.Reasons=@('HostSdkIncompatible','InvalidManifest')
Check ((Get-PluginUpdateDecision $changed $delta $true $ready).Kind -eq 'rejected') 'Malformed candidate cannot merely ask for a newer host'
$changed=Clone $row;$changed.Previous.Status='incompatible'
Check ((Get-PluginUpdateDecision $changed $delta $true $ready).Kind -eq 'rejected') 'Unusable rollback base'
$changed=Clone $delta;$changed.Providers.Removed=@('example')
Check ((Get-PluginUpdateDecision $row $changed $true $ready).Kind -eq 'rejected') 'Provider removal needs an identity migration design'
$changed=Clone $delta;$changed.AccessReviewRequired=$true
Check (Get-PluginUpdateDecision $row $changed $true $ready).AccessReviewRequired 'Access review is preserved'

# Original inert packages and locally generated evidence exercise binding, not real SDK acceptance.
$manifest=@{schemaVersion=5;id='example.update';version='1.0.0';entryAssembly='Fixture.dll';minimumHostApiVersion=1;maximumHostApiVersion=1;
    hostRequirements=@{minimumHostSdkVersion='2.10.0';requiredFeatures=@()};providers=@(@{id='example';capabilities=@('TrackSearch')})}
$packages=@()
foreach($version in @('1.0.0','1.1.0')){
    $dir=Join-Path $fixture $version;$null=New-Item -ItemType Directory -Path $dir
    $manifest.version=$version;Write-Json (Join-Path $dir 'platform.plugin.json') $manifest
    [IO.File]::WriteAllText((Join-Path $dir 'Fixture.dll'),'Not executable; original inert fixture '+$version)
    $archive=Join-Path $fixture ($version+'.zip');[IO.Compression.ZipFile]::CreateFromDirectory($dir,$archive)
    $packages+=Get-DeliveryPackage $archive $dir
}
$sdk=Join-Path $fixture 'sdk';$null=New-Item -ItemType Directory -Path $sdk
$hostFile=Join-Path $sdk 'host.txt';[IO.File]::WriteAllText($hostFile,'Original inert SDK inventory')
$hostFiles=Get-DeliveryTree $sdk
$matrixDir=Join-Path $fixture 'matrix';$deliveryDir=Join-Path $fixture 'delivery';$null=New-Item -ItemType Directory -Path $matrixDir,$deliveryDir
$row.HostDirectory=$sdk;$row.HostFiles=$hostFiles.Count;$row.HostSha256=Get-DeliveryDigest $hostFiles
$matrix=@{SchemaVersion=1;RunId=[guid]::NewGuid().ToString('N');Rows=@($row);Complete=$true;CompatibleHostCount=1;
    StaticOnly=$true;PluginCodeExecuted=$false;ReleaseReady=$false;Previous=(Get-UpdateIdentity $packages[0]);Candidate=(Get-UpdateIdentity $packages[1])}
Write-Json (Join-Path $matrixDir 'run.json') @{RunId=$matrix.RunId}
[IO.File]::WriteAllText((Join-Path $matrixDir 'matrix.md'),'Original synthetic matrix')
$matrix.Evidence=@{};foreach($file in (Get-DeliveryTree $matrixDir).Keys){$matrix.Evidence[[IO.Path]::GetRelativePath($matrixDir,$file)]=(Get-FileHash -LiteralPath $file).Hash}
$matrixFile=Join-Path $matrixDir 'matrix.json';Write-Json $matrixFile $matrix;$goodMatrix=Clone $matrix
$stages=@{};foreach($key in @('package','static','frozenHost','contract','offline','ui','live','native')){$stages[$key]=@{Status='passed';Reason='SyntheticFixture'}}
$stages.live.Status='not-run';$stages.native.Status='not-run'
$delivery=@{SchemaVersion=1;RunId=[guid]::NewGuid().ToString('N');Stages=$stages;Assessment=(Get-DeliveryAssessment $stages);
    Previous=(Get-UpdateIdentity $packages[0]);Candidate=(Get-UpdateIdentity $packages[1])}
Write-Json (Join-Path $deliveryDir 'run.json') @{RunId=$delivery.RunId}
$frozenFile=Join-Path $deliveryDir 'frozen-core.json';$recorded=Clone $hostFiles;$recorded['Z:\must-not-be-read']='non-host source evidence'
Write-Json $frozenFile $recorded
function Bind-Delivery {
    $delivery.Evidence=@{};foreach($file in (Get-DeliveryTree $deliveryDir).Keys){if([IO.Path]::GetFileName($file) -eq 'delivery-report.json'){continue};$delivery.Evidence[[IO.Path]::GetRelativePath($deliveryDir,$file).Replace('\','/')]=(Get-FileHash -LiteralPath $file).Hash}
    Write-Json (Join-Path $deliveryDir 'delivery-report.json') $delivery
}
Bind-Delivery;$goodDelivery=Clone $delivery
function Context([switch]$NoDelivery){Read-PluginUpdateContext $packages[0].Archive $packages[0].Directory $packages[1].Archive $packages[1].Directory $sdk $matrixDir $(if($NoDelivery){''}else{$deliveryDir})}
$bound=Context
Check ($bound.Decision.Kind -eq 'plugin-only') 'Archive, metadata and selected host bind together without reading recorded external source paths'
Check ((Context -NoDelivery).Decision.Kind -eq 'verification-required') 'Absent business evidence remains explicit'
Assert-PluginUpdateInputs $bound;Check $true 'Original evidence remains valid'
$matrix.Candidate.ArchiveSha256='0'*64;Write-Json $matrixFile $matrix
Reject {Context} 'Wrong candidate evidence';$matrix=Clone $goodMatrix;Write-Json $matrixFile $matrix
$matrix.Previous.Version='9.0.0';Write-Json $matrixFile $matrix
Reject {Context} 'Wrong old revision';$matrix=Clone $goodMatrix;Write-Json $matrixFile $matrix
$matrix.Rows[0].HostDirectory='Z:\not-selected';Write-Json $matrixFile $matrix
Reject {Context} 'Different host cannot supply compatibility';$matrix=Clone $goodMatrix;Write-Json $matrixFile $matrix
$matrix.Delta=@{AccessReviewRequired=$true};Write-Json $matrixFile $matrix
Check (!(Context).Delta.AccessReviewRequired) 'Diff recomputed from exact manifests, not report summary'
$matrix=Clone $goodMatrix;Write-Json $matrixFile $matrix
[IO.File]::WriteAllText($hostFile,'changed host bytes');Reject {Context} 'Changed current host'
[IO.File]::WriteAllText($hostFile,'Original inert SDK inventory')
$delivery.Candidate.PayloadSha256='0'*64;Bind-Delivery
Reject {Context} 'Mismatched delivery candidate';$delivery=Clone $goodDelivery;Bind-Delivery
Write-Json $frozenFile @{'Z:\another-host\host.txt'='not selected'};Bind-Delivery
Reject {Context} 'A compatible package tested on another host is not this host evidence'
Write-Json $frozenFile $recorded;$delivery=Clone $goodDelivery;Bind-Delivery
$bound=Context
[IO.File]::WriteAllText((Join-Path $matrixDir 'matrix.md'),'changed report');Reject {Assert-PluginUpdateInputs $bound} 'Evidence changes during planning'
[IO.File]::WriteAllText((Join-Path $matrixDir 'matrix.md'),'Original synthetic matrix')
[IO.File]::WriteAllText((Join-Path $matrixDir 'extra.txt'),'extra');Reject {Assert-PluginUpdateInputs $bound} 'Evidence additions during planning'

$planDir=Join-Path $fixture 'plan';$null=New-Item -ItemType Directory -Path $planDir
$id=[guid]::NewGuid().ToString('N');Write-Json (Join-Path $planDir 'run.json') @{RunId=$id}
[IO.File]::WriteAllText((Join-Path $planDir 'update-plan.md'),'Original plan')
$plan=@{SchemaVersion=1;RunId=$id;Status='plugin-only';Installed=$false;Uploaded=$false;ReleaseReady=$false;AutomaticInstallAllowed=$false;
    RequiresTrust=$true;RequiresRestart=$true;AccessReviewRequired=$false;Rehearsal=@{Status='not-run'};
    DecisionInputs=@{Row=$row;Delta=$delta;MatrixComplete=$true;Delivery=$ready};Evidence=@{}}
foreach($file in (Get-DeliveryTree $planDir).Keys){$plan.Evidence[[IO.Path]::GetRelativePath($planDir,$file)]=(Get-FileHash -LiteralPath $file).Hash}
$planFile=Join-Path $planDir 'update-plan.json';$goodPlan=Clone $plan
function Save-Plan {Write-Json $planFile $plan}
Save-Plan;Check (Test-PluginUpdateEvidence $planDir) 'Inert plan verifies without external files'
foreach($flag in @('Installed','Uploaded','ReleaseReady','AutomaticInstallAllowed','RequiresTrust','RequiresRestart')){
    $plan=Clone $goodPlan;$plan[$flag]=!$plan[$flag];Save-Plan
    Reject {Test-PluginUpdateEvidence $planDir} "$flag cannot silently grant authority"
}
$plan=Clone $goodPlan;$plan.RunId=[guid]::NewGuid().ToString('N');Save-Plan;Reject {Test-PluginUpdateEvidence $planDir} 'Mixed plan run'
$plan=Clone $goodPlan;$plan.Status='host-upgrade-required';Save-Plan;Reject {Test-PluginUpdateEvidence $planDir} 'Forged decision summary'
$plan=Clone $goodPlan;$plan.AccessReviewRequired=$true;Save-Plan;Reject {Test-PluginUpdateEvidence $planDir} 'Access summary must match derived decision'
$plan=Clone $goodPlan;$plan.Rehearsal=@{Status='passed';Checks=55};Save-Plan;Reject {Test-PluginUpdateEvidence $planDir} 'Missing rehearsal evidence cannot pass'
$plan=Clone $goodPlan;$plan.Rehearsal.Status='success';Save-Plan;Reject {Test-PluginUpdateEvidence $planDir} 'Unknown rehearsal status'
$plan=Clone $goodPlan;Save-Plan
[IO.File]::WriteAllText((Join-Path $planDir 'update-plan.md'),'changed');Reject {Test-PluginUpdateEvidence $planDir} 'Changed plan evidence'
Write-Host "PASS $script:checks update-plan guards; original fixtures retained at $fixture"
