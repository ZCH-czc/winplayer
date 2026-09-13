Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DeliveryPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    for ($cursor = $item; $null -ne $cursor; $cursor = $cursor.Parent) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'LinkedInput' }
        if ($cursor -is [IO.FileInfo]) { $cursor = $cursor.Directory; if ($null -eq $cursor) { break } }
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'LinkedInput' }
    }
    return $item.FullName
}

function Get-DeliveryTree([string]$Directory, [switch]$Source, [int]$MaxFiles=0, [long]$MaxBytes=0) {
    $root = Get-DeliveryPath $Directory
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'DirectoryRequired' }
    $result = @{}; [long]$totalBytes=0
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if ($Source -and $item.FullName -match '[\\/](bin|obj)([\\/]|$)') { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'LinkedInput' }
        if (!$item.PSIsContainer) {
            $totalBytes+=$item.Length
            if (($MaxFiles -gt 0 -and $result.Count -ge $MaxFiles) -or ($MaxBytes -gt 0 -and $totalBytes -gt $MaxBytes)) { throw 'InputLimit' }
            $result[$item.FullName] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    if (!$result.Count) { throw 'EmptyInput' }
    return $result
}

function Get-DeliveryDigest([hashtable]$Files) {
    $lines = @($Files.Keys | Sort-Object -CaseSensitive | ForEach-Object { "$_`t$($Files[$_])" })
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))
}

function Test-DeliveryTree([hashtable]$Before, [hashtable]$After) {
    if ($Before.Count -ne $After.Count) { return $false }
    foreach ($key in $Before.Keys) { if (!$After.ContainsKey($key) -or $Before[$key] -cne $After[$key]) { return $false } }
    return $true
}

function Assert-DeliveryEntry([string]$Name) {
    if (!$Name -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.StartsWith('/')) { throw 'UnsafePackageEntry' }
    foreach ($part in $Name.Split('/')) {
        if (!$part -or $part -in '.', '..' -or $part.TrimEnd(' ', '.') -cne $part -or
            $part -match '[<>:"|?*\x00-\x1f]' -or $part -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') { throw 'UnsafePackageEntry' }
    }
}

# Inspect the archive in place; never extract paths supplied by a ZIP. This is not an installer.
function Get-DeliveryPackage([string]$Archive, [string]$Directory) {
    $zipPath = Get-DeliveryPath $Archive
    $root = Get-DeliveryPath $Directory
    $zipFile = Get-Item -LiteralPath $zipPath
    if ($zipFile.PSIsContainer -or $zipFile.Length -gt 128MB) { throw 'PackageLimit' }
    $tree = Get-DeliveryTree $root -MaxFiles 256 -MaxBytes 128MB
    if ($tree.Count -gt 256) { throw 'PackageLimit' }
    $expected = @{}
    foreach ($file in $tree.Keys) {
        $name = [IO.Path]::GetRelativePath($root, $file).Replace('\','/')
        Assert-DeliveryEntry $name
        $expected[$name] = $tree[$file]
    }
    $seen = @{}; [long]$total = 0
    $stream = [IO.File]::OpenRead($zipPath)
    try {
        $archiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)); $stream.Position = 0
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($zip.Entries.Count -gt 256) { throw 'PackageLimit' }
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName; Assert-DeliveryEntry $name
                if ($seen.ContainsKey($name) -or !$expected.ContainsKey($name)) { throw 'PackageInventoryMismatch' }
                # Reject Unix symlinks and DOS reparse attributes even though we do not extract.
                if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)) { throw 'LinkedInput' }
                $total += $entry.Length
                if ($entry.Length -lt 0 -or $total -gt 128MB) { throw 'PackageLimit' }
                $inputStream = $entry.Open(); $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $buffer = [byte[]]::new(65536); [long]$readTotal = 0
                    while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $readTotal += $read
                        if ($readTotal -gt $entry.Length -or $readTotal -gt 128MB) { throw 'PackageLimit' }
                        $hash.AppendData($buffer, 0, $read)
                    }
                    if ($readTotal -ne $entry.Length -or [Convert]::ToHexString($hash.GetHashAndReset()) -cne $expected[$name]) { throw 'PackageHashMismatch' }
                } finally { $inputStream.Dispose(); $hash.Dispose() }
                $seen[$name] = $expected[$name]
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    if (!(Test-DeliveryTree $seen $expected)) { throw 'PackageInventoryMismatch' }
    $manifestPath = Join-Path $root 'platform.plugin.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw 'ManifestLimit' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.id -notmatch '^[a-z0-9][a-z0-9.-]{0,127}$' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'PackageIdentityInvalid' }
    return @{ Id=$manifest.id; Version=$manifest.version; ArchiveSha256=$archiveHash;
        Files=$expected; PayloadSha256=(Get-DeliveryDigest $expected); FileCount=$expected.Count;
        HostRequirements=$manifest.hostRequirements; Directory=$root; Archive=$zipPath }
}

function Get-DeliveryAssessment([hashtable]$Stages) {
    $local = @('package','static','frozenHost','contract','offline','ui')
    $all = @($local) + @('live','native')
    foreach ($name in $all) {
        if (!$Stages.ContainsKey($name) -or $Stages[$name].Status -notin @('passed','failed','not-run','not-applicable')) { throw 'InvalidStageStatus' }
        if ($Stages[$name].Status -eq 'not-applicable' -and !$Stages[$name].Reason) { throw 'ApplicabilityReasonRequired' }
    }
    # Required stages cannot be waived with not-applicable or a previous run's results.
    $localReady = @($local | Where-Object { $Stages[$_].Status -ne 'passed' }).Count -eq 0
    return @{ LocalReady=$localReady; ReleaseReady=($localReady -and $Stages.live.Status -eq 'passed' -and $Stages.native.Status -eq 'passed');
        HasFailure=(@($all | Where-Object { $Stages[$_].Status -eq 'failed' }).Count -gt 0) }
}

# Only caller-selected repository test code is executed. No command is ever read from a package.
# Drain child output continuously, retain a bounded prefix, and terminate only this child tree on timeout.
function Invoke-DeliveryProcess([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [int]$Seconds = 180) {
    if (!('AuralisDelivery.Child' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
namespace AuralisDelivery {
 public sealed record Result(int ExitCode, string Output, bool TimedOut);
 public static class Child {
  static async Task<string> Drain(StreamReader reader) {
   var text=new StringBuilder(); var buffer=new char[4096]; int n;
   while((n=await reader.ReadAsync(buffer,0,buffer.Length))>0)
    if(text.Length<1048576) text.Append(buffer,0,Math.Min(n,1048576-text.Length));
   return text.ToString();
  }
  public static async Task<Result> Run(string file,string[] args,string cwd,int seconds) {
   var info=new ProcessStartInfo(file){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   foreach(var arg in args)info.ArgumentList.Add(arg);
   using(var child=Process.Start(info)) {
    var stdout=Drain(child.StandardOutput); var stderr=Drain(child.StandardError); bool timeout=false;
    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds)); }
    catch(TimeoutException) { timeout=true; if(!child.HasExited)child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    try { return new Result(child.ExitCode,await stdout.WaitAsync(TimeSpan.FromSeconds(3)),timeout); }
    catch(TimeoutException) { return new Result(-1,"",true); }
   }
  }
 }
}
'@
    }
    return [AuralisDelivery.Child]::Run($File, $Arguments, $WorkingDirectory, $Seconds).GetAwaiter().GetResult()
}

# Checks a completed evidence directory without executing code or following paths from its JSON.
# This detects stale/changed artifacts, not a forged report authored by an attacker.
function Test-DeliveryEvidence([string]$Directory) {
    $root=Get-DeliveryPath $Directory
    $reportFile=Join-Path $root 'delivery-report.json'
    if((Get-Item -LiteralPath $reportFile).Length -gt 4MB){throw 'ReportLimit'}
    $report=Get-Content -LiteralPath $reportFile -Raw|ConvertFrom-Json -AsHashtable
    $run=Get-Content -LiteralPath (Join-Path $root 'run.json') -Raw|ConvertFrom-Json -AsHashtable
    if($report.SchemaVersion -ne 1 -or $report.RunId -notmatch '^[a-f0-9]{32}$' -or $report.RunId -cne $run.RunId){throw 'EvidenceRunMismatch'}
    $actual=@{}
    foreach($key in (Get-DeliveryTree $root).Keys){
        $name=[IO.Path]::GetRelativePath($root,$key).Replace('\','/')
        if($name -in @('delivery-report.json','delivery-report.md')){continue}
        $actual[$name]=(Get-FileHash -LiteralPath $key).Hash
    }
    if(!(Test-DeliveryTree $report.Evidence $actual)){throw 'EvidenceFilesChanged'}
    $assessment=Get-DeliveryAssessment $report.Stages
    foreach($field in @('LocalReady','ReleaseReady','HasFailure')){
        if($report.Assessment[$field] -isnot [bool] -or $report.Assessment[$field] -ne $assessment[$field]){throw 'EvidenceAssessmentMismatch'}
    }
    return $true
}

Export-ModuleMember -Function Get-DeliveryPath,Get-DeliveryTree,Get-DeliveryDigest,Test-DeliveryTree,Get-DeliveryPackage,Get-DeliveryAssessment,Invoke-DeliveryProcess,Test-DeliveryEvidence
