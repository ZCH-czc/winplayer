#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$FrozenCore,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$PreviousPackage,[Parameter(Mandatory)][string]$PreviousDirectory,
    [string[]]$CompatibilityHosts=@(),[string]$CoreBaseline,
    [switch]$TrustPluginCode,[string]$Profile
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if($Profile -and !$TrustPluginCode){throw 'Profile requires explicit trust.'}
$projectPath=Get-DeliveryPath $Project;$core=Get-DeliveryPath $FrozenCore
$old=Get-DeliveryPackage $PreviousPackage $PreviousDirectory
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Output directory must be new.'}
$null=Get-DeliveryPath (Split-Path $out -Parent)
$sources=@('Auralis','Auralis.Platform.Host','Auralis.Platform.Abstractions','Auralis.Artwork.Http','Auralis.Playback.Abstractions','Auralis.Playback.Host'|ForEach-Object {Join-Path $repo $_})
foreach($protected in @($core,$old.Directory,(Split-Path $projectPath -Parent))+$sources){
    if($out.StartsWith($protected.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output overlaps protected input.'}
}
$coreFiles=Get-DeliveryTree $core
foreach($source in $sources){$files=Get-DeliveryTree $source -Source;foreach($key in $files.Keys){$coreFiles[$key]=$files[$key]}}
if($CoreBaseline -and !(Test-DeliveryTree $coreFiles (Get-Content -LiteralPath $CoreBaseline -Raw|ConvertFrom-Json -AsHashtable))){throw 'Existing core baseline mismatch.'}
$inputs=Get-RevisionBuildInputs $projectPath $core $repo
$manifestPath=Join-Path (Split-Path $projectPath -Parent) 'platform.plugin.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -AsHashtable
$name=$inputs.Properties.AssemblyName
if($name -notmatch '^[A-Za-z][A-Za-z0-9.]{0,120}$' -or $manifest.entryAssembly -cne "$name.dll" -or
    $manifest.version -cne $inputs.Properties.Version -or $manifest.id -cne $old.Id -or [version]$manifest.version -le [version]$old.Version){throw 'Source identity/version mismatch or not an upgrade.'}
$null=New-Item -ItemType Directory -Path $out
$report=@{SchemaVersion=1;RunId=[guid]::NewGuid().ToString('N');Project=$projectPath;BuildInputs=$inputs;
    SelectedPlugin=$manifest.id;Version=$manifest.version;ProjectReferencesBuilt=0;PlayerBuilt=$false;OtherPluginsBuilt=$false;
    Installed=$false;Uploaded=$false;ReleaseReady=$false;Passed=$false}
$failed=$false
try{
    $bin=Join-Path $out 'build'
    $build=Invoke-DeliveryProcess dotnet @('build',$projectPath,'-c','Release','--no-restore',"-p:FrozenCore=$core",
        '-p:BuildProjectReferences=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$bin,'-v:q') $repo 180
    if($build.ExitCode -ne 0 -or $build.TimedOut){throw 'PluginBuildFailed'}
    if((Get-FileHash -LiteralPath $manifestPath).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bin 'platform.plugin.json')).Hash){throw 'BuiltManifestMismatch'}
    if((Get-FileHash -LiteralPath (Join-Path $bin 'Auralis.Platform.Abstractions.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $core 'Auralis.Platform.Abstractions.dll')).Hash){throw 'BuiltContractMismatch'}
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin "$name.dll")).FileVersion
    if([version]$version -ne [version]($manifest.version+'.0')){throw 'AssemblyVersionMismatch'}
    $payload=Join-Path $out "platforms/$($manifest.id)";$null=New-Item -ItemType Directory -Path $payload
    # Only a flat managed plugin payload; no SDK, profiles, PDBs, receipts or arbitrary output globs.
    foreach($file in @('platform.plugin.json',"$name.dll","$name.deps.json")){Copy-Item -LiteralPath (Join-Path $bin $file) -Destination $payload}
    $package=Join-Path $out "$($manifest.id)-$($manifest.version).auralis-plugin"
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$package)
    $candidate=Get-DeliveryPackage $package $payload
    [IO.File]::WriteAllText("$package.sha256","$($candidate.ArchiveSha256)  $([IO.Path]::GetFileName($package))`n")
    $report.Candidate=$candidate
    $report.Delta=Get-RevisionDelta (Get-Content -LiteralPath (Join-Path $old.Directory 'platform.plugin.json') -Raw|ConvertFrom-Json -AsHashtable) $manifest
    $hosts=@($core)+@($CompatibilityHosts|ForEach-Object {Get-DeliveryPath $_})|Select-Object -Unique
    $hostFile=Join-Path $out 'hosts.json';[IO.File]::WriteAllText($hostFile,(ConvertTo-Json -InputObject @($hosts)))
    $matrixDir=Join-Path $out 'compatibility'
    $matrix=Invoke-DeliveryProcess pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-PluginCompatibilityMatrix.ps1'),
        '-PreviousPackage',$old.Archive,'-PreviousDirectory',$old.Directory,'-UpdatedPackage',$package,'-UpdatedDirectory',$payload,
        '-HostListFile',$hostFile,'-EvidenceDirectory',$matrixDir) $repo 300
    if($matrix.ExitCode -ne 0 -or $matrix.TimedOut){throw 'CompatibilityMatrixIncomplete'}
    $null=Test-RevisionMatrixEvidence $matrixDir
    $matrixReport=Get-Content -LiteralPath (Join-Path $matrixDir 'matrix.json') -Raw|ConvertFrom-Json -AsHashtable
    if(!$matrixReport.Complete -or $matrixReport.Rows[0].Candidate.Status -ne 'compatible'){throw 'SelectedHostIncompatible'}
    $sourceFile=Join-Path $out 'core-sources.json';[IO.File]::WriteAllText($sourceFile,(ConvertTo-Json -InputObject $sources))
    $arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-PluginDelivery.ps1'),'-PreviousPackage',$old.Archive,
        '-PreviousDirectory',$old.Directory,'-UpdatedPackage',$package,'-UpdatedDirectory',$payload,'-FrozenCore',$core,
        '-CoreSourceListFile',$sourceFile,'-EvidenceDirectory',(Join-Path $out 'delivery'))
    if($CoreBaseline){$arguments+=@('-CoreBaseline',(Get-DeliveryPath $CoreBaseline))}
    if($TrustPluginCode){$arguments+='-TrustPluginCode'}
    if($Profile){$arguments+=@('-Profile',(Get-DeliveryPath $Profile))}
    $gate=Invoke-DeliveryProcess pwsh $arguments $repo 600
    if($gate.ExitCode -ne 0 -or $gate.TimedOut){throw 'DeliveryCheckFailed'}
    $null=Test-DeliveryEvidence (Join-Path $out 'delivery')
    $report.LocalReady=(Get-Content -LiteralPath (Join-Path $out 'delivery/delivery-report.json') -Raw|ConvertFrom-Json).Assessment.LocalReady
    $report.Passed=$true
}catch{
    $failed=$true;$reason=$_.Exception.Message
    if($reason -notin @('PluginBuildFailed','BuiltManifestMismatch','BuiltContractMismatch','AssemblyVersionMismatch',
        'CompatibilityMatrixIncomplete','SelectedHostIncompatible','DeliveryCheckFailed')){$reason='BuildPipelineFailed'}
    $report.Error=$reason;Write-Warning $reason
}finally{
    try{
        $after=Get-DeliveryTree $core
        foreach($source in $sources){$files=Get-DeliveryTree $source -Source;foreach($key in $files.Keys){$after[$key]=$files[$key]}}
        $newInputs=Get-RevisionBuildInputs $projectPath $core $repo
        if(!(Test-DeliveryTree $coreFiles $after) -or !(Test-DeliveryTree $inputs.Files $newInputs.Files)){throw 'InputsChanged'}
        $report.CoreUnchanged=$true;$report.CoreFiles=$coreFiles.Count
    }catch{$report.CoreUnchanged=$false;$report.Passed=$false;$report.LocalReady=$false;$failed=$true}
    [IO.File]::WriteAllText((Join-Path $out 'build-report.json'),($report|ConvertTo-Json -Depth 30))
}
Write-Host "Single plugin build: $out (not installed or published)"
if($failed){exit 1};exit 0
