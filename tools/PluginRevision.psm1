Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1')

function Compare-RevisionSet($Before,$After) {
    $left=@($Before|Sort-Object -Unique -CaseSensitive);$right=@($After|Sort-Object -Unique -CaseSensitive)
    return @{Added=@($right|Where-Object {$_ -cnotin $left});Removed=@($left|Where-Object {$_ -cnotin $right})}
}

function Get-RevisionDeclarations([hashtable]$Manifest) {
    $caps=@();$access=@();$pages=@();$settings=@()
    foreach($alias in @($Manifest['credentialAliases'])) {
        if($null -ne $alias){$access+="credential:$($alias.key)|$($alias.scope)|$($alias.legacyKey)"}
    }
    foreach($provider in $Manifest.providers) {
        foreach($cap in $provider.capabilities){$caps+="$($provider.id):$cap"}
        foreach($domain in @($provider['commentArtworkDomains'])){if($null -ne $domain){$access+="artwork:$($provider.id)|$domain"}}
        foreach($setting in @($provider['settings'])) {
            if($null -eq $setting){continue}
            $settings+="$($provider.id):$($setting.key)"
            foreach($key in @($setting['legacyKeys'])){if($null -ne $key){$access+="setting-alias:$($provider.id)|$($setting.key)|$key"}}
        }
        foreach($page in @($provider['pages'])) {
            if($null -ne $page){$pages+="$($provider.id):$($page.id)|$($page.placement)|$($page.presentation)|v$($page.documentVersion)|creator=$($page['acceptsCreatorContext'] -eq $true)"}
        }
    }
    return @{Capabilities=$caps;AccessDeclarations=$access;PageEntries=$pages;SettingKeys=$settings;
        Providers=@($Manifest.providers|ForEach-Object {$_.id});Features=@($Manifest.hostRequirements.requiredFeatures)}
}

function Get-RevisionDelta([hashtable]$Before,[hashtable]$After) {
    if($Before.id -cne $After.id){throw 'PluginIdentityChanged'}
    $old=Get-RevisionDeclarations $Before;$new=Get-RevisionDeclarations $After
    $delta=@{PluginId=$After.id;PreviousVersion=$Before.version;CandidateVersion=$After.version;
        MinimumSdk=@{Before=$Before.hostRequirements.minimumHostSdkVersion;After=$After.hostRequirements.minimumHostSdkVersion};
        ApiRange=@{Before=@($Before.minimumHostApiVersion,$Before.maximumHostApiVersion);After=@($After.minimumHostApiVersion,$After.maximumHostApiVersion)};
        Schema=@{Before=$Before.schemaVersion;After=$After.schemaVersion}}
    foreach($field in @('Providers','Capabilities','AccessDeclarations','PageEntries','SettingKeys','Features')){$delta[$field]=Compare-RevisionSet $old[$field] $new[$field]}
    $delta.AccessReviewRequired=($delta.AccessDeclarations.Added.Count+$delta.AccessDeclarations.Removed.Count -gt 0)
    $delta.Warning='Declared access is not an OS permission sandbox. Imports still require explicit trust for every revision.'
    return $delta
}

function Get-RevisionBuildInputs([string]$Project,[string]$FrozenCore,[string]$WorkingDirectory) {
    $projectPath=Get-DeliveryPath $Project;$core=Get-DeliveryPath $FrozenCore
    $process=Invoke-DeliveryProcess dotnet @('msbuild',$projectPath,"-p:FrozenCore=$core",'-getProperty:AssemblyName,Version,TargetFramework',
        '-getItem:ProjectReference,Reference,Compile') $WorkingDirectory 60
    if($process.ExitCode -ne 0 -or $process.TimedOut){throw 'ProjectEvaluationFailed'}
    $evaluated=$process.Output|ConvertFrom-Json -AsHashtable
    if(@($evaluated.Items.ProjectReference).Count -ne 0){throw 'FrozenBuildMustNotHaveProjectReferences'}
    $contract=@($evaluated.Items.Reference|Where-Object {$_.Identity -eq 'Auralis.Platform.Abstractions'})
    if($contract.Count -ne 1 -or (Get-DeliveryPath $contract[0].HintPath) -ne (Join-Path $core 'Auralis.Platform.Abstractions.dll')){throw 'FrozenContractReferenceRequired'}
    $files=@{}
    foreach($item in @($evaluated.Items.Compile)){
        $file=Get-DeliveryPath $item.FullPath
        $files[$file]=(Get-FileHash -LiteralPath $file).Hash
    }
    foreach($file in @($projectPath,(Join-Path (Split-Path $projectPath -Parent) 'platform.plugin.json'))){$files[$file]=(Get-FileHash -LiteralPath $file).Hash}
    return @{Properties=$evaluated.Properties;Files=$files;ProjectReferenceCount=0;ContractPath=$contract[0].HintPath}
}

function Test-RevisionMatrixEvidence([string]$Directory) {
    # Verify only the caller-selected evidence tree. Never follow recorded host/package/source paths.
    $root=Get-DeliveryPath $Directory
    $reportPath=Join-Path $root 'matrix.json'
    $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json -AsHashtable
    $run=Get-Content -LiteralPath (Join-Path $root 'run.json') -Raw|ConvertFrom-Json -AsHashtable
    if($report.SchemaVersion -ne 1 -or $report.RunId -cne $run.RunId -or $report.RunId -notmatch '^[a-f0-9]{32}$' -or
        $report.StaticOnly -isnot [bool] -or !$report.StaticOnly -or $report.PluginCodeExecuted -cne $false -or
        $report.ReleaseReady -cne $false -or $report.Complete -isnot [bool]){throw 'InvalidMatrixEnvelope'}
    $files=Get-DeliveryTree $root;$null=$files.Remove($reportPath)
    if($files.Count -ne $report.Evidence.Count){throw 'MatrixEvidenceInventoryChanged'}
    foreach($file in $files.Keys){
        $relative=[IO.Path]::GetRelativePath($root,$file)
        if($report.Evidence[$relative] -cne $files[$file]){throw 'MatrixEvidenceBytesChanged'}
    }
    if($report.Rows.Count -lt 1 -or $report.Rows.Count -gt 8){throw 'InvalidMatrixRows'}
    $incomplete=$report['InputsChanged'] -eq $true;$compatible=0
    foreach($row in $report.Rows){
        if($row.Unchanged -isnot [bool]){throw 'InvalidMatrixRow'}
        foreach($key in @('Previous','Candidate')){
            $status=$row[$key].Status
            if($status -cnotin @('compatible','incompatible','check-failed')){throw 'InvalidMatrixStatus'}
            if($status -eq 'check-failed'){$incomplete=$true}
        }
        if(!$row.Unchanged){$incomplete=$true}
        if($row.Candidate.Status -eq 'compatible' -and $row.Unchanged){$compatible++}
    }
    if($report.Complete -eq $incomplete -or $report.CompatibleHostCount -ne $compatible){throw 'MatrixAssessmentMismatch'}
    return $true
}

Export-ModuleMember -Function Compare-RevisionSet,Get-RevisionDeclarations,Get-RevisionDelta,Get-RevisionBuildInputs,Test-RevisionMatrixEvidence
