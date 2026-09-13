#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackage,[Parameter(Mandatory)][string]$PreviousDirectory,
    [Parameter(Mandatory)][string]$UpdatedPackage,[Parameter(Mandatory)][string]$UpdatedDirectory,
    [Parameter(Mandatory)][string]$FrozenCore,[Parameter(Mandatory)][string]$MatrixDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,[string]$DeliveryDirectory,[string]$CoreBaseline,
    [switch]$Rehearse,[switch]$AcknowledgeDeclarationChanges
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginUpdatePlan.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'New output directory required.'}
$null=Get-DeliveryPath (Split-Path $out -Parent)
$core=Get-DeliveryPath $FrozenCore
$sourceRoots=@('Auralis','Auralis.Platform.Host','Auralis.Platform.Abstractions','Auralis.Artwork.Http','Auralis.Playback.Abstractions','Auralis.Playback.Host'|ForEach-Object {Join-Path $repo $_})
$protected=@($core,$PreviousDirectory,$UpdatedDirectory,$MatrixDirectory)+$sourceRoots
if($DeliveryDirectory){$protected+=$DeliveryDirectory}
foreach($root in $protected){
    $resolved=Get-DeliveryPath $root
    if($out.StartsWith($resolved.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output overlaps input.'}
}
$null=New-Item -ItemType Directory -Path $out
$plan=@{SchemaVersion=1;RunId=[guid]::NewGuid().ToString('N');Status='rejected';Installed=$false;Uploaded=$false;ReleaseReady=$false;
    Rehearsal=@{Status='not-run';Reason='NotRequested'};RequiresTrust=$true;RequiresRestart=$true;AutomaticInstallAllowed=$false;
    Warning='Developer plan only. Declaration review and synthetic rehearsal are not installation approval, account migration or runtime acceptance.'}
[IO.File]::WriteAllText((Join-Path $out 'run.json'),(@{RunId=$plan.RunId;StartedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json))
$project=Join-Path $repo 'plugin-sdk/UpdateRehearsal/UpdateRehearsal.csproj'
function Tool-Snapshot {
    $files=Get-DeliveryTree (Split-Path $project -Parent) -Source
    foreach($file in @($PSCommandPath,(Join-Path $PSScriptRoot 'PluginUpdatePlan.psm1'),(Join-Path $PSScriptRoot 'PluginRevision.psm1'),(Join-Path $PSScriptRoot 'PluginDelivery.psm1'))){$files[$file]=(Get-FileHash -LiteralPath $file).Hash}
    return $files
}
function Core-Snapshot {
    $files=Get-DeliveryTree $core
    foreach($root in $sourceRoots){$tree=Get-DeliveryTree $root -Source;foreach($key in $tree.Keys){$files[$key]=$tree[$key]}}
    return $files
}
function Run([string]$File,[string[]]$Arguments){
    $result=Invoke-DeliveryProcess $File $Arguments $repo 180
    if($result.ExitCode -ne 0 -or $result.TimedOut){throw 'RehearsalProcessFailed'}
}
$context=$null;$frozen=@{};$toolFiles=@{};$errorOccurred=$false
try{
    $frozen=Core-Snapshot;$toolFiles=Tool-Snapshot;$plan.ToolSources=$toolFiles
    if($CoreBaseline -and !(Test-DeliveryTree $frozen (Get-Content -LiteralPath (Get-DeliveryPath $CoreBaseline) -Raw|ConvertFrom-Json -AsHashtable))){throw 'CoreBaselineMismatch'}
    $context=Read-PluginUpdateContext $PreviousPackage $PreviousDirectory $UpdatedPackage $UpdatedDirectory $core $MatrixDirectory $DeliveryDirectory
    $plan.Previous=Get-UpdateIdentity $context.Previous;$plan.Candidate=Get-UpdateIdentity $context.Candidate
    $plan.Matrix=$context.Matrix;$plan.Delivery=$context.Delivery;$plan.Delta=$context.Delta
    $assessment=if($context.Delivery){$context.Delivery.Assessment}else{$null}
    $plan.DecisionInputs=@{Row=$context.Row;Delta=$context.Delta;MatrixComplete=$context.MatrixComplete;Delivery=$assessment}
    $plan.Status=$context.Decision.Kind;$plan.Reasons=$context.Decision.Reasons;$plan.AccessReviewRequired=$context.Decision.AccessReviewRequired
    $plan.Host=@{Directory=$core;Sdk=$context.Row.Candidate['SdkVersion'];Files=$context.CoreFiles.Count;Sha256=(Get-DeliveryDigest $context.CoreFiles)}
    $plan.Steps=switch($plan.Status){
        'plugin-only' {@('Recheck exact original and candidate archives before any real import.',
            'Review publisher trust and declarations; approval is revision-specific.',
            'Import candidate as a separate immutable revision, OFF by default.',
            'Enable explicitly; exit normally and start a new player session.',
            'If rejected, reimport the exact previous archive, review trust, enable and restart.',
            'Preserve saved identities; never copy credentials or rewrite saved playlists to restore a version.')}
        'host-upgrade-required' {@('Keep the current revision; do not import this candidate into the selected host.',
            'Identify a compatible base SDK, then rerun the matrix and delivery checks against its exact published files.',
            'Generate a new package-bound plan before considering any installation.')}
        'verification-required' {@('Keep the current revision and stop before import.',
            'Complete the missing or failed checks for these exact archives and this selected host.',
            'Generate a new plan; do not substitute a different revision or another host report.')}
        default {@('Keep the current revision; reject this proposed replacement.',
            'Resolve invalid identity, removed provider or manifest compatibility before replanning.')}
    }
    if($Rehearse){
        if($plan.Status -ne 'plugin-only'){$plan.Rehearsal.Reason='PlanDoesNotPermitRehearsal'}
        elseif($plan.AccessReviewRequired -and !$AcknowledgeDeclarationChanges){$plan.Rehearsal.Reason='DeclarationAcknowledgementRequired'}
        else{
            $plan.Rehearsal=@{Status='failed';Reason='RehearsalNotCompleted';DeclarationsAcknowledged=[bool]$AcknowledgeDeclarationChanges}
            $input=@{Previous=@{};Candidate=@{};ProviderIds=@();EntryAssemblies=@()}
            foreach($key in @('Previous','Candidate')){
                $input[$key]=Get-UpdateIdentity $context[$key];$input[$key].Archive=$context[$key].Archive
                $manifest=Get-Content -LiteralPath (Join-Path $context[$key].Directory 'platform.plugin.json') -Raw|ConvertFrom-Json
                $input.EntryAssemblies+=[IO.Path]::GetFileNameWithoutExtension($manifest.entryAssembly)
                if($key -eq 'Previous'){$input.ProviderIds=@($manifest.providers|ForEach-Object {$_.id})}
            }
            $inputFile=Join-Path $out 'rehearsal-input.json';[IO.File]::WriteAllText($inputFile,($input|ConvertTo-Json -Depth 10))
            $feed=Join-Path $out 'empty-feed';$null=New-Item -ItemType Directory -Path $feed
            Run dotnet @('restore',$project,'--source',$feed,"-p:FrozenCore=$core")
            $runtime=Join-Path $out 'probe'
            Run dotnet @('build',$project,'-c','Release','--no-restore',"-p:FrozenCore=$core",'-o',$runtime,'-v:q')
            foreach($file in @('Auralis.Platform.Host.dll','Auralis.Platform.Abstractions.dll')){
                if((Get-FileHash -LiteralPath (Join-Path $runtime $file)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $core $file)).Hash){throw 'RehearsalSdkMismatch'}
            }
            $runtimeFiles=Get-DeliveryTree $runtime
            Run dotnet @((Join-Path $runtime 'UpdateRehearsal.dll'),$inputFile,(Join-Path $out 'rehearsal'))
            if(!(Test-DeliveryTree $runtimeFiles (Get-DeliveryTree $runtime))){throw 'RehearsalRuntimeChanged'}
            $result=Get-Content -LiteralPath (Join-Path $out 'rehearsal/result.json') -Raw|ConvertFrom-Json -AsHashtable
            if($result.Passed -isnot [bool] -or !$result.Passed -or $result.Checks -lt 1 -or $result.ProviderAssembliesLoaded -ne 0){throw 'RehearsalResultInvalid'}
            Assert-UpdateIdentity $plan.Previous $result.Previous;Assert-UpdateIdentity $plan.Candidate $result.Candidate
            $plan.Rehearsal=@{Status='passed';Checks=$result.Checks;Reason='FrozenManagerWithSyntheticState';ProviderCodeExecuted=$false;
                RuntimeFiles=$runtimeFiles;DeclarationsAcknowledged=[bool]$AcknowledgeDeclarationChanges}
        }
    }
}catch{
    $errorOccurred=$true;$reason=$_.Exception.Message
    if($reason -notin @('InvalidUpgradeIdentity','EvidencePackageMismatch','SelectedHostEvidenceMissing','SelectedHostChanged','DeliveryHostMismatch',
        'CoreBaselineMismatch','RehearsalProcessFailed','RehearsalSdkMismatch','RehearsalRuntimeChanged','RehearsalResultInvalid')){$reason='PlanInputOrEvidenceInvalid'}
    $plan.Status='rejected';$plan.Error=$reason
}finally{
    try{
        if($context){Assert-PluginUpdateInputs $context}
        if(!$frozen.Count -or !(Test-DeliveryTree $frozen (Core-Snapshot)) -or !(Test-DeliveryTree $toolFiles (Tool-Snapshot))){throw 'ChangedInputs'}
        $plan.Core=@{Unchanged=$true;Files=$frozen.Count;SnapshotSha256=(Get-DeliveryDigest $frozen);ExistingBaselineChecked=[bool]$CoreBaseline}
    }catch{
        $plan.Status='rejected';$plan.Error='InputsChangedOrUnverified';$plan.Rehearsal=@{Status='failed';Reason='InputsChangedOrUnverified'};$errorOccurred=$true
    }
    if($plan.Status -eq 'rejected'){$plan.Steps=@('Keep the current revision; do not install this candidate.',
        'Resolve the reported identity, compatibility or evidence error and generate a fresh plan.')}
    $lines=@('# Plugin candidate update plan','',"Decision: $($plan.Status)",'',
        "Rehearsal: $($plan.Rehearsal.Status)",'',
        'No personal installation, accounts, network, media playback, hot-swap or release authorization.',
        'Rollback here means explicit old-package reimport in an isolated profile, not automatic rollback of a running DLL.')
    if($plan.ContainsKey('Previous')){$lines+=@('',"Plugin: $($plan.Previous.Id)","Revisions: $($plan.Previous.Version) -> $($plan.Candidate.Version)",
        "Previous SHA-256: $($plan.Previous.ArchiveSha256)","Candidate SHA-256: $($plan.Candidate.ArchiveSha256)",
        "Access declaration review required: $($plan.AccessReviewRequired)",'')+$plan.Steps}
    if($plan.ContainsKey('Host')){$lines+=@('',"Selected SDK: $($plan.Host.Sdk)","Minimum candidate SDK: $($plan.Delta.MinimumSdk.After)")}
    if($plan.ContainsKey('Reasons')){$lines+=@('',"Reasons: $($plan.Reasons -join ', ')")}
    if($plan.ContainsKey('Error')){$lines+=@('',"Error: $($plan.Error)")}
    [IO.File]::WriteAllLines((Join-Path $out 'update-plan.md'),$lines)
    $plan.Evidence=@{};foreach($key in (Get-DeliveryTree $out).Keys){$plan.Evidence[[IO.Path]::GetRelativePath($out,$key)]=(Get-FileHash -LiteralPath $key).Hash}
    [IO.File]::WriteAllText((Join-Path $out 'update-plan.json'),($plan|ConvertTo-Json -Depth 30))
}
Write-Host "Update plan: $($plan.Status); rehearsal: $($plan.Rehearsal.Status); $out"
if($errorOccurred -or $plan.Status -eq 'rejected'){exit 1}
if($plan.Status -ne 'plugin-only' -or $Rehearse -and $plan.Rehearsal.Status -ne 'passed'){exit 2}
exit 0
