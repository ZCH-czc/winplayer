#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackage,[Parameter(Mandatory)][string]$PreviousDirectory,
    [Parameter(Mandatory)][string]$UpdatedPackage,[Parameter(Mandatory)][string]$UpdatedDirectory,
    [string[]]$Hosts=@(),[string]$HostListFile,[Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if($HostListFile){if($Hosts.Count){throw 'Choose one host list source.'};$Hosts=@(Get-Content -LiteralPath $HostListFile -Raw|ConvertFrom-Json)}
if($Hosts.Count -lt 1 -or $Hosts.Count -gt 8){throw 'Select between one and eight trusted published hosts.'}
$old=Get-DeliveryPackage $PreviousPackage $PreviousDirectory;$new=Get-DeliveryPackage $UpdatedPackage $UpdatedDirectory
$out=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $out){throw 'New evidence directory required.'}
$null=Get-DeliveryPath (Split-Path $out -Parent)
$roots=@($Hosts|ForEach-Object {Get-DeliveryPath $_})
if(@($roots|Select-Object -Unique).Count -ne $roots.Count){throw 'Duplicate host input.'}
foreach($root in $roots+@($old.Directory,$new.Directory)){
    if($out.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output overlaps an input.'}
}
$null=New-Item -ItemType Directory -Path $out
$runId=[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $out 'run.json'),(@{RunId=$runId;StartedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json))
$delta=Get-RevisionDelta (Get-Content -LiteralPath (Join-Path $old.Directory 'platform.plugin.json') -Raw|ConvertFrom-Json -AsHashtable) `
    (Get-Content -LiteralPath (Join-Path $new.Directory 'platform.plugin.json') -Raw|ConvertFrom-Json -AsHashtable)
$report=@{SchemaVersion=1;RunId=$runId;Previous=$old;Candidate=$new;Delta=$delta;Rows=@();
    StaticOnly=$true;PluginCodeExecuted=$false;Installed=$false;ReleaseReady=$false}
$project=Join-Path $repo 'plugin-sdk/ManifestCheck/ManifestCheck.csproj'
$toolSources=Get-DeliveryTree (Split-Path $project -Parent) -Source
foreach($file in @($PSCommandPath,(Join-Path $PSScriptRoot 'PluginRevision.psm1'),(Join-Path $PSScriptRoot 'PluginDelivery.psm1'))){$toolSources[$file]=(Get-FileHash -LiteralPath $file).Hash}
$report.ToolSources=$toolSources
$feed=Join-Path $out 'empty-feed';$null=New-Item -ItemType Directory -Path $feed
$failed=$false
for($index=0;$index -lt $roots.Count;$index++){
    $root=$roots[$index];$row=@{HostDirectory=$root;Previous=@{Status='check-failed'};Candidate=@{Status='check-failed'};Unchanged=$false}
    $before=@{}
    try{
        $before=Get-DeliveryTree $root;$row.HostSha256=Get-DeliveryDigest $before;$row.HostFiles=$before.Count
        $bin=Join-Path $out "host-$index"
        # This metadata tool has no package dependencies. The empty local feed prevents online restores.
        $restore=Invoke-DeliveryProcess dotnet @('restore',$project,'--source',$feed,"-p:FrozenCore=$root") $repo 60
        if($restore.ExitCode -ne 0 -or $restore.TimedOut){throw 'ToolPreparationFailed'}
        $build=Invoke-DeliveryProcess dotnet @('build',$project,'-c','Release','--no-restore',"-p:FrozenCore=$root",'-o',$bin,'-v:q') $repo 60
        if($build.ExitCode -ne 0 -or $build.TimedOut){throw 'SelectedSdkToolUnsupported'}
        foreach($file in @('Auralis.Platform.Host.dll','Auralis.Platform.Abstractions.dll')){
            if((Get-FileHash -LiteralPath (Join-Path $bin $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash){throw 'SdkBytesMismatch'}
        }
        $checkerBefore=Get-DeliveryTree $bin
        foreach($revision in @('Previous','Candidate')){
            $probe=Invoke-DeliveryProcess dotnet @((Join-Path $bin 'ManifestCheck.dll'),$report[$revision].Directory) $repo 45
            if($probe.TimedOut -or $probe.ExitCode -notin @(0,1)){throw 'MetadataProbeFailed'}
            $value=$probe.Output|ConvertFrom-Json -AsHashtable
            if($value.Status -cnotin @('compatible','incompatible') -or $value.PluginCodeExecuted -ne $false -or
                ($value.Status -eq 'compatible') -ne ($probe.ExitCode -eq 0)){throw 'MetadataReportInvalid'}
            $row[$revision]=$value
        }
        if(!(Test-DeliveryTree $checkerBefore (Get-DeliveryTree $bin))){throw 'CheckerChanged'}
    }catch{$row.CheckError='MetadataCheckNotCompleted';$row.Previous=@{Status='check-failed'};$row.Candidate=@{Status='check-failed'};$failed=$true}
    finally{
        try{$row.Unchanged=$before.Count -gt 0 -and (Test-DeliveryTree $before (Get-DeliveryTree $root))}catch{$row.Unchanged=$false}
        if(!$row.Unchanged){$row.Previous=@{Status='check-failed'};$row.Candidate=@{Status='check-failed'};$failed=$true}
    }
    $report.Rows+=$row
}
try{
    foreach($input in @($old,$new)){$after=Get-DeliveryPackage $input.Archive $input.Directory
        if($after.ArchiveSha256 -ne $input.ArchiveSha256 -or $after.PayloadSha256 -ne $input.PayloadSha256){throw 'InputChanged'}}
    $afterTools=Get-DeliveryTree (Split-Path $project -Parent) -Source
    foreach($file in @($PSCommandPath,(Join-Path $PSScriptRoot 'PluginRevision.psm1'),(Join-Path $PSScriptRoot 'PluginDelivery.psm1'))){$afterTools[$file]=(Get-FileHash -LiteralPath $file).Hash}
    if(!(Test-DeliveryTree $toolSources $afterTools)){throw 'ToolChanged'}
}catch{$failed=$true;$report.InputsChanged=$true}
$report.Complete=!$failed
$report.CompatibleHostCount=@($report.Rows|Where-Object {$_.Candidate.Status -eq 'compatible' -and $_.Unchanged}).Count
$lines=@('# Plugin host compatibility matrix','',"Plugin: $($old.Id) $($old.Version) → $($new.Version)",'',
    '| Host SDK | Previous | Candidate | Host unchanged |','| --- | --- | --- | --- |')
foreach($row in $report.Rows){$sdk=if($row.Candidate.ContainsKey('SdkVersion')){$row.Candidate.SdkVersion}else{'unverified'}
    $lines+="| $sdk | $($row.Previous.Status) | $($row.Candidate.Status) | $($row.Unchanged) |"}
$lines+=@('',"Access declaration review required: $($delta.AccessReviewRequired)",'',
    'Static metadata compatibility only; no plugin activation, accounts, installation or release approval.',
    'A known incompatibility is a completed negative result. A tool failure is not compatibility evidence.')
[IO.File]::WriteAllLines((Join-Path $out 'matrix.md'),$lines)
$report.Evidence=@{};foreach($file in (Get-DeliveryTree $out).Keys){$report.Evidence[[IO.Path]::GetRelativePath($out,$file)]=(Get-FileHash -LiteralPath $file).Hash}
[IO.File]::WriteAllText((Join-Path $out 'matrix.json'),($report|ConvertTo-Json -Depth 24))
Write-Host "Matrix: $out"
if($failed){exit 1};exit 0
