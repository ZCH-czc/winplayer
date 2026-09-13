#Requires -Version 7.2
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PluginDelivery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PluginRevision.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture=Join-Path $repo ('artifacts/revision-guards-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixture
$script:checks=0
function Check([bool]$Condition,[string]$Label){if(!$Condition){throw "FAIL $Label"};$script:checks++}
function Reject([scriptblock]$Action,[string]$Label){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Label}
function Clone($Value){return $Value|ConvertTo-Json -Depth 20|ConvertFrom-Json -AsHashtable}
$old=@{schemaVersion=5;id='example.revision';version='1.0.0';minimumHostApiVersion=1;maximumHostApiVersion=1;
    hostRequirements=@{minimumHostSdkVersion='2.9.0';requiredFeatures=@('pages.v1','artwork.v1')};
    credentialAliases=@(@{key='session';scope='example.scope';legacyKey='previous.session'});
    providers=@(@{id='example';capabilities=@('Pages','TrackSearch');commentArtworkDomains=@('example.com');
        pages=@(@{id='creator';placement='creator';presentation='page';documentVersion=3;acceptsCreatorContext=$true});
        settings=@(@{key='quality';legacyKeys=@('previous.quality')})})}
$new=Clone $old;$new.version='1.1.0';$new.hostRequirements.minimumHostSdkVersion='2.10.0'
$new.providers[0].pages[0].documentVersion=6
$delta=Get-RevisionDelta $old $new
Check ($delta.MinimumSdk.Before -eq '2.9.0' -and $delta.MinimumSdk.After -eq '2.10.0') 'SDK difference'
Check ($delta.PageEntries.Added.Count -eq 1 -and $delta.PageEntries.Removed.Count -eq 1) 'Page contract change'
Check (!$delta.AccessReviewRequired) 'A page revision is not an access declaration'
$new=Clone $old;$new.providers[0].capabilities=@('TrackSearch','Pages','Pages')
$new.hostRequirements.requiredFeatures=@('artwork.v1','pages.v1')
$delta=Get-RevisionDelta $old $new
foreach($field in @('Providers','Capabilities','AccessDeclarations','PageEntries','SettingKeys','Features')){
    Check ($delta[$field].Added.Count+$delta[$field].Removed.Count -eq 0) "Stable unordered $field"
}
$new=Clone $old;$new.providers[0].capabilities+='StreamResolution'
$delta=Get-RevisionDelta $old $new
Check ($delta.Capabilities.Added -ccontains 'example:StreamResolution') 'Added capability'
Check (!$delta.AccessReviewRequired) 'Capability changes are separately reported'
$new=Clone $old;$new.credentialAliases[0].scope='different.scope'
$delta=Get-RevisionDelta $old $new
Check ($delta.AccessReviewRequired -and $delta.AccessDeclarations.Added.Count -eq 1 -and $delta.AccessDeclarations.Removed.Count -eq 1) 'Credential scope replacement'
$new=Clone $old;$new.credentialAliases=@()
Check (Get-RevisionDelta $old $new).AccessReviewRequired 'Removed credential declaration requires review'
$new=Clone $old;$new.providers[0].commentArtworkDomains+='cdn.example.com'
Check ((Get-RevisionDelta $old $new).AccessDeclarations.Added -ccontains 'artwork:example|cdn.example.com') 'Artwork domain addition'
$new=Clone $old;$new.providers[0].settings[0].legacyKeys=@('different.quality')
$delta=Get-RevisionDelta $old $new
Check ($delta.AccessReviewRequired -and $delta.AccessDeclarations.Added.Count -eq 1 -and $delta.AccessDeclarations.Removed.Count -eq 1) 'Legacy setting alias replacement'
$new=Clone $old;$new.providers[0].Remove('commentArtworkDomains');$new.providers[0].Remove('settings');$new.providers[0].Remove('pages');$new.Remove('credentialAliases')
$empty=Get-RevisionDeclarations $new
Check ($empty.AccessDeclarations.Count -eq 0 -and $empty.PageEntries.Count -eq 0) 'Optional declarations can be absent'
$new=Clone $old;$new.id='another.plugin';Reject {Get-RevisionDelta $old $new} 'Identity cannot change in an upgrade'

# Evaluate only repository-owned synthetic MSBuild projects, never untrusted package commands.
$sdk=Join-Path $fixture 'sdk';$src=Join-Path $fixture 'source'
$null=New-Item -ItemType Directory -Path $sdk,$src
[IO.File]::WriteAllText((Join-Path $sdk 'Auralis.Platform.Abstractions.dll'),'not executable; evaluation only')
[IO.File]::WriteAllText((Join-Path $src 'Fixture.cs'),'// original inert fixture')
[IO.File]::WriteAllText((Join-Path $src 'platform.plugin.json'),($old|ConvertTo-Json -Depth 12))
$project=Join-Path $src 'Fixture.csproj'
$xml=@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>1.0.0</Version><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
<ItemGroup><Compile Include="Fixture.cs"/><Reference Include="Auralis.Platform.Abstractions"><HintPath>$(FrozenCore)/Auralis.Platform.Abstractions.dll</HintPath></Reference></ItemGroup></Project>
'@
[IO.File]::WriteAllText($project,$xml)
$inputs=Get-RevisionBuildInputs $project $sdk $repo
Check ($inputs.ProjectReferenceCount -eq 0 -and $inputs.Files.Count -eq 3) 'Exact source/manifest/project inventory without project references'
Check (Test-DeliveryTree $inputs.Files (Get-RevisionBuildInputs $project $sdk $repo).Files) 'Stable selected build inputs'
[IO.File]::WriteAllText((Join-Path $src 'Fixture.cs'),'// changed original fixture')
Check (!(Test-DeliveryTree $inputs.Files (Get-RevisionBuildInputs $project $sdk $repo).Files)) 'Source mutation detected'
[IO.File]::WriteAllText($project,($xml.Replace('</ItemGroup>','<ProjectReference Include="Other.csproj"/></ItemGroup>')))
Reject {Get-RevisionBuildInputs $project $sdk $repo} 'Any project reference prevents frozen build'
[IO.File]::WriteAllText($project,($xml.Replace('Include="Auralis.Platform.Abstractions"','Include="Unrelated.Contract"')))
Reject {Get-RevisionBuildInputs $project $sdk $repo} 'Missing frozen SDK reference rejected'
[IO.File]::WriteAllText($project,($xml.Replace('$(FrozenCore)/Auralis.Platform.Abstractions.dll','different.dll')))
Reject {Get-RevisionBuildInputs $project $sdk $repo} 'Wrong contract path rejected'

$evidence=Join-Path $fixture 'matrix';$null=New-Item -ItemType Directory -Path $evidence
$id=[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $evidence 'run.json'),(@{RunId=$id}|ConvertTo-Json))
[IO.File]::WriteAllText((Join-Path $evidence 'matrix.md'),'Original synthetic matrix')
$rows=@(@{Previous=@{Status='compatible'};Candidate=@{Status='compatible'};Unchanged=$true},
    @{Previous=@{Status='compatible'};Candidate=@{Status='incompatible'};Unchanged=$true},
    @{Previous=@{Status='incompatible'};Candidate=@{Status='incompatible'};Unchanged=$true})
$envelope=@{SchemaVersion=1;RunId=$id;Rows=$rows;StaticOnly=$true;PluginCodeExecuted=$false;ReleaseReady=$false;Complete=$true;CompatibleHostCount=1;
    Evidence=@{};Candidate=@{Directory='Z:\must-not-be-read';Archive='Z:\must-not-be-read.zip'}}
foreach($file in (Get-DeliveryTree $evidence).Keys){$envelope.Evidence[[IO.Path]::GetRelativePath($evidence,$file)]=(Get-FileHash -LiteralPath $file).Hash}
$good=Clone $envelope
function Save {[IO.File]::WriteAllText((Join-Path $evidence 'matrix.json'),($envelope|ConvertTo-Json -Depth 12))}
Save;Check (Test-RevisionMatrixEvidence $evidence) 'Known incompatibility is complete; no external recorded path is followed'
$envelope.RunId=[guid]::NewGuid().ToString('N');Save;Reject {Test-RevisionMatrixEvidence $evidence} 'Mixed-run evidence rejected'
$envelope=Clone $good;$envelope.Rows[0].Candidate.Status='check-failed';Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Tool failure cannot claim complete'
$envelope.Complete=$false;$envelope.CompatibleHostCount=0;Save
Check (Test-RevisionMatrixEvidence $evidence) 'Honest incomplete matrix remains inspectable'
$envelope=Clone $good;$envelope.Rows[0].Unchanged=$false;Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Changed host cannot claim complete'
$envelope=Clone $good;$envelope.CompatibleHostCount=3;Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Incorrect compatible count rejected'
$envelope=Clone $good;$envelope.Rows[0].Candidate.Status='success';Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Unknown status rejected'
$envelope=Clone $good;$envelope.InputsChanged=$true;Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Input mutation cannot claim complete'
$envelope=Clone $good;$envelope.ReleaseReady=$true;Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Static matrix never approves release'
$envelope=Clone $good;$envelope.PluginCodeExecuted=$true;Save
Reject {Test-RevisionMatrixEvidence $evidence} 'Static-only execution declaration enforced'
$envelope=Clone $good;Save
Check (Test-RevisionMatrixEvidence $evidence) 'Untouched evidence verifies again'
[IO.File]::WriteAllText((Join-Path $evidence 'matrix.md'),'different')
Reject {Test-RevisionMatrixEvidence $evidence} 'Evidence bytes changed'
[IO.File]::WriteAllText((Join-Path $evidence 'unexpected.txt'),'extra')
Reject {Test-RevisionMatrixEvidence $evidence} 'Additional evidence file rejected'
Write-Host "PASS $script:checks revision guard assertions; original fixtures retained at $fixture"
