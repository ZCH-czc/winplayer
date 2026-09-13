Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1')
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1')

function Get-UpdateIdentity([hashtable]$Package) {
    return @{Id=$Package.Id;Version=$Package.Version;ArchiveSha256=$Package.ArchiveSha256;PayloadSha256=$Package.PayloadSha256}
}
function Assert-UpdateIdentity([hashtable]$Expected,[hashtable]$Actual) {
    foreach($key in @('Id','Version','ArchiveSha256','PayloadSha256')) {
        if(!$Expected.ContainsKey($key) -or $Expected[$key] -cne $Actual[$key]){throw 'EvidencePackageMismatch'}
    }
}
function Get-PluginUpdateDecision([hashtable]$Row,[hashtable]$Delta,[bool]$MatrixComplete,[hashtable]$Delivery) {
    $result=@{Kind='verification-required';Reasons=@();RequiresTrust=$true;RequiresRestart=$true;
        AccessReviewRequired=[bool]$Delta.AccessReviewRequired;ReleaseReady=$false;AutomaticInstallAllowed=$false}
    if($Delta.Providers.Removed.Count){$result.Kind='rejected';$result.Reasons=@('ProviderIdentityRemoved');return $result}
    if(!$MatrixComplete -or !$Row.Unchanged -or $Row.Candidate.Status -eq 'check-failed' -or $Row.Previous.Status -eq 'check-failed'){
        $result.Reasons=@('CompatibilityCheckIncomplete');return $result
    }
    if($Row.Candidate.Status -eq 'incompatible'){
        $reasons=@($Row.Candidate.Reasons)
        $hostOnly=@('HostSdkIncompatible','HostFeatureUnsupported','HostApiIncompatible','ManifestSchemaUnsupported')
        if($reasons.Count -and @($reasons|Where-Object {$_ -cnotin $hostOnly}).Count -eq 0){
            $result.Kind='host-upgrade-required';$result.Reasons=$reasons
        }else{$result.Kind='rejected';$result.Reasons=@('CandidateManifestIncompatible')}
        return $result
    }
    if($Row.Candidate.Status -ne 'compatible' -or $Row.Previous.Status -ne 'compatible'){
        $result.Kind='rejected';$result.Reasons=@('PreviousRevisionNotUsableForRollback');return $result
    }
    if($null -eq $Delivery){$result.Reasons=@('DeliveryEvidenceRequired');return $result}
    if($Delivery.HasFailure){$result.Reasons=@('DeliveryCheckFailed');return $result}
    if(!$Delivery.LocalReady){$result.Reasons=@('DeliveryChecksIncomplete');return $result}
    $result.Kind='plugin-only';$result.Reasons=@('ExactCandidateLocallyVerified','RealPlatformAndNativeAcceptanceSeparate')
    return $result
}
function Read-PluginUpdateContext([string]$PreviousPackage,[string]$PreviousDirectory,[string]$UpdatedPackage,
    [string]$UpdatedDirectory,[string]$FrozenCore,[string]$MatrixDirectory,[string]$DeliveryDirectory) {
    $previous=Get-DeliveryPackage $PreviousPackage $PreviousDirectory;$candidate=Get-DeliveryPackage $UpdatedPackage $UpdatedDirectory
    if($previous.Id -cne $candidate.Id -or [version]$candidate.Version -le [version]$previous.Version -or
        $previous.PayloadSha256 -ceq $candidate.PayloadSha256){throw 'InvalidUpgradeIdentity'}
    $core=Get-DeliveryPath $FrozenCore;$coreFiles=Get-DeliveryTree $core
    $matrixRoot=Get-DeliveryPath $MatrixDirectory
    if((Get-Item -LiteralPath (Join-Path $matrixRoot 'matrix.json')).Length -gt 4MB){throw 'MatrixReportLimit'}
    $null=Test-RevisionMatrixEvidence $matrixRoot
    $matrix=Get-Content -LiteralPath (Join-Path $matrixRoot 'matrix.json') -Raw|ConvertFrom-Json -AsHashtable
    Assert-UpdateIdentity $matrix.Previous $previous;Assert-UpdateIdentity $matrix.Candidate $candidate
    $rows=@($matrix.Rows|Where-Object {$_.HostDirectory -eq $core})
    if($rows.Count -ne 1){throw 'SelectedHostEvidenceMissing'}
    $row=$rows[0]
    if($row.HostSha256 -cne (Get-DeliveryDigest $coreFiles) -or $row.HostFiles -ne $coreFiles.Count){throw 'SelectedHostChanged'}
    $delta=Get-RevisionDelta (Get-Content -LiteralPath (Join-Path $previous.Directory 'platform.plugin.json') -Raw|ConvertFrom-Json -AsHashtable) `
        (Get-Content -LiteralPath (Join-Path $candidate.Directory 'platform.plugin.json') -Raw|ConvertFrom-Json -AsHashtable)
    # Recompute declarations from the verified actual manifests, never trust the matrix's text summary.
    $assessment=$null;$deliveryReference=$null;$evidenceFiles=Get-DeliveryTree $matrixRoot;$evidenceRoots=@($matrixRoot)
    if($DeliveryDirectory){
        $deliveryRoot=Get-DeliveryPath $DeliveryDirectory;$null=Test-DeliveryEvidence $deliveryRoot
        $delivery=Get-Content -LiteralPath (Join-Path $deliveryRoot 'delivery-report.json') -Raw|ConvertFrom-Json -AsHashtable
        Assert-UpdateIdentity $delivery.Previous $previous;Assert-UpdateIdentity $delivery.Candidate $candidate
        $recorded=Get-Content -LiteralPath (Join-Path $deliveryRoot 'frozen-core.json') -Raw|ConvertFrom-Json -AsHashtable
        $hostSubset=@{};$prefix=$core.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        foreach($key in $recorded.Keys){if($key.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){$hostSubset[$key]=$recorded[$key]}}
        # Recorded external paths are compared as strings only, never opened.
        if(!(Test-DeliveryTree $coreFiles $hostSubset)){throw 'DeliveryHostMismatch'}
        $assessment=$delivery.Assessment
        $deliveryReference=@{RunId=$delivery.RunId;Assessment=$assessment;Stages=$delivery.Stages;
            ReportSha256=(Get-FileHash -LiteralPath (Join-Path $deliveryRoot 'delivery-report.json')).Hash}
        $more=Get-DeliveryTree $deliveryRoot;foreach($key in $more.Keys){$evidenceFiles[$key]=$more[$key]}
        $evidenceRoots+=$deliveryRoot
    }
    return @{Previous=$previous;Candidate=$candidate;Core=$core;CoreFiles=$coreFiles;Row=$row;Delta=$delta;
        Decision=(Get-PluginUpdateDecision $row $delta $matrix.Complete $assessment);EvidenceFiles=$evidenceFiles;EvidenceRoots=$evidenceRoots;MatrixComplete=$matrix.Complete;
        Matrix=@{RunId=$matrix.RunId;ReportSha256=(Get-FileHash -LiteralPath (Join-Path $matrixRoot 'matrix.json')).Hash};Delivery=$deliveryReference}
}
function Assert-PluginUpdateInputs([hashtable]$Context) {
    Assert-UpdateIdentity $Context.Previous (Get-DeliveryPackage $Context.Previous.Archive $Context.Previous.Directory)
    Assert-UpdateIdentity $Context.Candidate (Get-DeliveryPackage $Context.Candidate.Archive $Context.Candidate.Directory)
    if(!(Test-DeliveryTree $Context.CoreFiles (Get-DeliveryTree $Context.Core))){throw 'SelectedHostChanged'}
    $after=@{};foreach($root in $Context.EvidenceRoots){$tree=Get-DeliveryTree $root;foreach($key in $tree.Keys){$after[$key]=$tree[$key]}}
    if(!(Test-DeliveryTree $Context.EvidenceFiles $after)){throw 'EvidenceChangedDuringPlan'}
}
function Test-PluginUpdateEvidence([string]$Directory) {
    $root=Get-DeliveryPath $Directory;$file=Join-Path $root 'update-plan.json'
    if((Get-Item -LiteralPath $file).Length -gt 4MB){throw 'PlanReportLimit'}
    $plan=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json -AsHashtable
    $run=Get-Content -LiteralPath (Join-Path $root 'run.json') -Raw|ConvertFrom-Json -AsHashtable
    if($plan.SchemaVersion -ne 1 -or $plan.RunId -notmatch '^[a-f0-9]{32}$' -or $plan.RunId -cne $run.RunId){throw 'InvalidPlanEnvelope'}
    foreach($key in @('Installed','Uploaded','ReleaseReady','AutomaticInstallAllowed')){
        if($plan[$key] -isnot [bool] -or $plan[$key]){throw 'InvalidPlanAuthority'}
    }
    foreach($key in @('RequiresTrust','RequiresRestart')){
        if($plan[$key] -isnot [bool] -or !$plan[$key]){throw 'InvalidPlanAuthority'}
    }
    $actual=@{};foreach($key in (Get-DeliveryTree $root).Keys){if($key -eq $file){continue};$actual[[IO.Path]::GetRelativePath($root,$key)]=(Get-FileHash -LiteralPath $key).Hash}
    if(!(Test-DeliveryTree $plan.Evidence $actual)){throw 'PlanEvidenceChanged'}
    if($plan.Status -cnotin @('plugin-only','host-upgrade-required','verification-required','rejected')){throw 'InvalidPlanStatus'}
    if($plan.ContainsKey('DecisionInputs') -and !$plan['Error']){
        $inputs=$plan.DecisionInputs;$decision=Get-PluginUpdateDecision $inputs.Row $inputs.Delta $inputs.MatrixComplete $inputs.Delivery
        if($decision.Kind -cne $plan.Status -or $plan.AccessReviewRequired -isnot [bool] -or
            $plan.AccessReviewRequired -ne $decision.AccessReviewRequired){throw 'PlanDecisionMismatch'}
    }elseif($plan.Status -ne 'rejected'){throw 'RejectedPlanRequired'}
    if($plan.Rehearsal.Status -cnotin @('passed','failed','not-run')){throw 'InvalidRehearsalStatus'}
    if($plan.Rehearsal.Status -eq 'passed'){
        if($plan.AccessReviewRequired -and $plan.Rehearsal['DeclarationsAcknowledged'] -ne $true){throw 'DeclarationReviewMissing'}
        if($plan.Status -ne 'plugin-only' -or $plan.Rehearsal.Checks -isnot [long] -and $plan.Rehearsal.Checks -isnot [int] -or
            $plan.Rehearsal.Checks -lt 1){throw 'InvalidRehearsalPass'}
        $result=Get-Content -LiteralPath (Join-Path $root 'rehearsal/result.json') -Raw|ConvertFrom-Json -AsHashtable
        if($result.Passed -isnot [bool] -or !$result.Passed -or $result.Checks -ne $plan.Rehearsal.Checks -or
            $result.ProviderAssembliesLoaded -ne 0 -or $result.PersonalStateAccessed -ne $false){throw 'InvalidRehearsalEvidence'}
        Assert-UpdateIdentity $plan.Previous $result.Previous;Assert-UpdateIdentity $plan.Candidate $result.Candidate
    }
    return $true
}
Export-ModuleMember -Function Get-UpdateIdentity,Assert-UpdateIdentity,Get-PluginUpdateDecision,Read-PluginUpdateContext,Assert-PluginUpdateInputs,Test-PluginUpdateEvidence
