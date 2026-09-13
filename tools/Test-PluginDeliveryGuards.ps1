#Requires -Version 7.2
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture=Join-Path $repo ('artifacts/delivery-guards-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixture
$script:checks=0
function Check([bool]$Condition,[string]$Label){if(!$Condition){throw "FAIL $Label"};$script:checks++}
function Reject([scriptblock]$Action,[string]$Label){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Label}
function Stages {
    $stages=@{};foreach($name in @('package','static','frozenHost','contract','offline','ui','live','native')){$stages[$name]=@{Status='passed';Reason='CurrentRun'}}
    return $stages
}
$stages=Stages;Check (Get-DeliveryAssessment $stages).ReleaseReady 'All current checks required'
foreach($name in @($stages.Keys)){
    foreach($state in @('failed','not-run','not-applicable')){
        $trial=Stages;$trial[$name]=@{Status=$state;Reason='Explicit gap'}
        $result=Get-DeliveryAssessment $trial
        Check (!$result.ReleaseReady) "$name $state cannot approve release"
        Check ($result.HasFailure -eq ($state -eq 'failed')) 'Failure preserved independently of untested'
    }
}
$trial=Stages;$trial.live.Status='failed'
Check (Get-DeliveryAssessment $trial).LocalReady 'Live failure must not rewrite local evidence'
Check (!(Get-DeliveryAssessment $trial).ReleaseReady) 'Offline pass must not mask live failure'
$trial=Stages;$trial.ui.Status='success';Reject {Get-DeliveryAssessment $trial} 'Unknown status rejected'
$trial=Stages;$trial.ui=@{Status='not-applicable';Reason=''};Reject {Get-DeliveryAssessment $trial} 'No unexplained waiver'
$trial=Stages;$trial.Remove('native');Reject {Get-DeliveryAssessment $trial} 'Missing stage rejected'

$packageDir=Join-Path $fixture 'plugin';$null=New-Item -ItemType Directory -Path $packageDir
$manifest='{"id":"example.test","version":"1.1.0","hostRequirements":{"minimumHostSdkVersion":"2.10.0","requiredFeatures":[]}}'
[IO.File]::WriteAllText((Join-Path $packageDir 'platform.plugin.json'),$manifest)
[IO.File]::WriteAllText((Join-Path $packageDir 'payload.txt'),'fixture only; not executable')
function Zip([string]$Name,[string[]]$Extra=@(),[switch]$OmitPayload,[switch]$AlterPayload){
    $file=Join-Path $fixture "$Name.zip"
    $zip=[IO.Compression.ZipFile]::Open($file,[IO.Compression.ZipArchiveMode]::Create)
    try{
        foreach($entryName in @('platform.plugin.json','payload.txt')){
            if($OmitPayload -and $entryName -eq 'payload.txt'){continue}
            $entry=$zip.CreateEntry($entryName);$writer=[IO.StreamWriter]::new($entry.Open())
            try{$content=Get-Content -LiteralPath (Join-Path $packageDir $entryName) -Raw
                if($AlterPayload -and $entryName -eq 'payload.txt'){$content='different'}
                $writer.Write($content)
            }finally{$writer.Dispose()}
        }
        foreach($entryName in $Extra){$null=$zip.CreateEntry($entryName)}
    }finally{$zip.Dispose()}
    return $file
}
$good=Zip 'valid';$identity=Get-DeliveryPackage $good $packageDir
Check ($identity.Id -eq 'example.test' -and $identity.FileCount -eq 2) 'Exact package matches'
Check ($identity.ArchiveSha256 -eq (Get-FileHash -LiteralPath $good).Hash) 'Archive digest exact'
Check ((Get-DeliveryPackage $good $packageDir).PayloadSha256 -eq $identity.PayloadSha256) 'Stable payload digest'
$bad=Zip 'missing' -OmitPayload;Reject {Get-DeliveryPackage $bad $packageDir} 'Missing archive file'
$bad=Zip 'different' -AlterPayload;Reject {Get-DeliveryPackage $bad $packageDir} 'Wrong archive bytes'
$bad=Zip 'extra' @('unexpected.txt');Reject {Get-DeliveryPackage $bad $packageDir} 'Extra archive file'
$bad=Zip 'duplicate' @('payload.txt');Reject {Get-DeliveryPackage $bad $packageDir} 'Duplicate entry'
$bad=Zip 'case-collision' @('PAYLOAD.TXT');Reject {Get-DeliveryPackage $bad $packageDir} 'Case collision'
$i=0
foreach($name in @('../escape','/absolute','C:/absolute','sub\file','sub/../file','./file','file:stream','file.','file ','CON','dir/NUL.txt','dir//file','dir/')){
    $bad=Zip "unsafe-$i" @($name);$i++;Reject {Get-DeliveryPackage $bad $packageDir} 'Unsafe or unsupported ZIP path'
}
$bad=Zip 'too-many' @(1..256|ForEach-Object{"file-$_"});Reject {Get-DeliveryPackage $bad $packageDir} 'Archive count bound'
[IO.File]::WriteAllText((Join-Path $packageDir 'extra.txt'),'unpack only')
Reject {Get-DeliveryPackage $good $packageDir} 'Extra unpacked file'
$before=Get-DeliveryTree $packageDir
Check (Test-DeliveryTree $before (Get-DeliveryTree $packageDir)) 'Unchanged inventory'
[IO.File]::WriteAllText((Join-Path $packageDir 'payload.txt'),'changed')
Check (!(Test-DeliveryTree $before (Get-DeliveryTree $packageDir))) 'Content changes detected'
$after=Get-DeliveryTree $packageDir;$key=@($after.Keys)[0];$after.Remove($key)
Check (!(Test-DeliveryTree $before $after)) 'Removed file detected'
$after=@{};foreach($key in $before.Keys){$after[$key]=$before[$key]};$after['added']='ABC'
Check (!(Test-DeliveryTree $before $after)) 'Added file detected'
Check ((Get-DeliveryDigest @{a='B';b='C'}) -eq (Get-DeliveryDigest @{b='C';a='B'})) 'Hash independent of enumeration order'
$link=Join-Path $fixture 'linked';$null=New-Item -ItemType Junction -Path $link -Target $packageDir
Reject {Get-DeliveryTree $link} 'Junction input rejected'
$empty=Join-Path $fixture 'empty';$null=New-Item -ItemType Directory -Path $empty
Reject {Get-DeliveryTree $empty} 'Empty inventory rejected'

$result=Invoke-DeliveryProcess 'pwsh' @('-NoProfile','-Command','Write-Output bounded; exit 7') $repo 10
Check ($result.ExitCode -eq 7 -and $result.Output.Contains('bounded') -and !$result.TimedOut) 'Child failure preserved'
$result=Invoke-DeliveryProcess 'pwsh' @('-NoProfile','-Command','Start-Sleep -Seconds 30') $repo 1
Check $result.TimedOut 'Own child process deadline enforced'
$result=Invoke-DeliveryProcess 'pwsh' @('-NoProfile','-Command',"[Console]::Write(('x' * 1200000))") $repo 10
Check ($result.Output.Length -eq 1048576) 'Bounded capture drains oversized output'

$evidence=Join-Path $fixture 'evidence';$null=New-Item -ItemType Directory -Path $evidence
$runId=[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $evidence 'run.json'),(@{RunId=$runId}|ConvertTo-Json))
[IO.File]::WriteAllText((Join-Path $evidence 'check.json'),'{"fixture":true}')
$bound=@{};foreach($key in (Get-DeliveryTree $evidence).Keys){$bound[[IO.Path]::GetFileName($key)]=(Get-FileHash -LiteralPath $key).Hash}
$states=Stages;$states.live.Status='not-run';$states.native.Status='not-run'
$envelope=@{SchemaVersion=1;RunId=$runId;Stages=$states;Assessment=(Get-DeliveryAssessment $states);Evidence=$bound}
$reportPath=Join-Path $evidence 'delivery-report.json'
function Save-Envelope {[IO.File]::WriteAllText($reportPath,($envelope|ConvertTo-Json -Depth 8))}
Save-Envelope
Check (Test-DeliveryEvidence $evidence) 'Bound evidence verifies'
$envelope.RunId=[guid]::NewGuid().ToString('N');Save-Envelope
Reject {Test-DeliveryEvidence $evidence} 'Stale run ID rejected'
$envelope.RunId=$runId;$envelope.Assessment.ReleaseReady=$true;Save-Envelope
Reject {Test-DeliveryEvidence $evidence} 'Falsely green aggregate rejected'
$envelope.Assessment.ReleaseReady=$false;Save-Envelope
[IO.File]::WriteAllText((Join-Path $evidence 'check.json'),'{"fixture":false}')
Reject {Test-DeliveryEvidence $evidence} 'Changed report artifact rejected'
[IO.File]::WriteAllText((Join-Path $evidence 'check.json'),'{"fixture":true}')
[IO.File]::WriteAllText((Join-Path $evidence 'extra.json'),'{}')
Reject {Test-DeliveryEvidence $evidence} 'Added report artifact rejected'

# Failure before activation must still leave an honest report, and never execute a supplied profile.
$gate=Join-Path $PSScriptRoot 'Test-PluginDelivery.ps1'
$cliEvidence=Join-Path $fixture 'cli-failure'
$arguments=@('-NoProfile','-File',$gate,'-PreviousPackage',$good,'-PreviousDirectory',$packageDir,
    '-UpdatedPackage',$good,'-UpdatedDirectory',$packageDir,'-FrozenCore',$packageDir,'-EvidenceDirectory',$cliEvidence)
$result=Invoke-DeliveryProcess pwsh $arguments $repo 15
Check ($result.ExitCode -ne 0) 'Mismatched package gate fails before activation'
$failure=Get-Content -LiteralPath (Join-Path $cliEvidence 'delivery-report.json') -Raw|ConvertFrom-Json
Check ($failure.Stages.package.Status -eq 'failed' -and $failure.Stages.contract.Status -eq 'not-run' -and !$failure.Assessment.ReleaseReady) 'Failure report does not execute or claim readiness'
Check (Test-DeliveryEvidence $cliEvidence) 'Failure evidence remains inspectable'
$result=Invoke-DeliveryProcess pwsh ($arguments+@('-Profile','not-executed.ps1')) $repo 15
Check ($result.ExitCode -ne 0) 'Profile requires explicit trust'
$result=Invoke-DeliveryProcess pwsh $arguments $repo 15
Check ($result.ExitCode -ne 0 -and (Test-DeliveryEvidence $cliEvidence)) 'Existing evidence never overwritten'

[IO.File]::WriteAllText((Join-Path $fixture 'guards-result.json'),(@{Passed=$true;Checks=$script:checks;Synthetic=$true;Installed=$false}|ConvertTo-Json))
Write-Host "PASS $script:checks plugin delivery guards. Fixtures: $fixture"
