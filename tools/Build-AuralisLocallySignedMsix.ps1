#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.16.10',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$KeepStaging,

    [Parameter(DontShow)]
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$publisher = 'CN=Auralis'
$identityName = 'Auralis.Player'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$certificateScript = Join-Path $PSScriptRoot 'New-AuralisDevelopmentCertificate.ps1'
$buildScript = Join-Path $PSScriptRoot 'Build-AuralisMsix.ps1'
$resultFile = Join-Path $artifactsDirectory 'Auralis-Local-Signing-Result.json'
$expectedPackage = Join-Path $artifactsDirectory "Auralis-$Version-$Runtime.msix"
$friendlyName = 'Auralis Development Package Signing'
$codeSigningEku = '1.3.6.1.5.5.7.3.3'

function Test-IsAdministrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SignToolPath {
    $windowsKitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $sdkDirectory = Get-ChildItem -LiteralPath $windowsKitsBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'x64\signtool.exe') -PathType Leaf
        } |
        Select-Object -First 1

    if ($null -eq $sdkDirectory) {
        throw 'Windows SDK SignTool.exe was not found.'
    }

    return Join-Path $sdkDirectory.FullName 'x64\signtool.exe'
}

function Assert-AuralisCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($Certificate.Subject -cne $publisher) {
        throw "The development certificate subject '$($Certificate.Subject)' does not exactly match '$publisher'."
    }
    if ($Certificate.FriendlyName -ne $friendlyName) {
        throw 'The selected certificate was not created by the Auralis development certificate helper.'
    }
    if (-not $Certificate.HasPrivateKey) {
        throw 'The Auralis development certificate does not contain a private key.'
    }

    $now = Get-Date
    if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now.AddDays(30)) {
        throw 'The Auralis development certificate is not valid for at least another 30 days.'
    }

    $ekuExtension = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    if ($null -eq $ekuExtension) {
        throw 'The Auralis development certificate is missing its code-signing usage.'
    }

    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
    if ($null -eq ($enhancedKeyUsage.EnhancedKeyUsages |
        Where-Object { $_.Value -eq $codeSigningEku } |
        Select-Object -First 1)) {
        throw 'The Auralis development certificate is not valid for code signing.'
    }

    $basicConstraintsExtension = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    if ($null -ne $basicConstraintsExtension) {
        $basicConstraints = [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraintsExtension
        if ($basicConstraints.CertificateAuthority) {
            throw 'The Auralis development certificate must not be a certificate authority.'
        }
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'Auralis MSIX signing is available only on Windows.'
}
if (-not (Test-Path -LiteralPath $certificateScript -PathType Leaf)) {
    throw "The certificate helper is missing: $certificateScript"
}
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "The MSIX build helper is missing: $buildScript"
}

$operationDescription = @(
    'create or reuse a non-exportable Auralis development certificate',
    'trust its public key in Local Computer\Trusted People',
    'rebuild the fixed Auralis.Player MSIX',
    'sign it and verify the signature'
) -join '; '

if (-not $PSCmdlet.ShouldProcess('Auralis local development package', $operationDescription)) {
    return
}

if (-not (Test-IsAdministrator)) {
    if ($Elevated) {
        throw 'The elevated signing process does not have administrator rights.'
    }

    Write-Host 'Windows will now request administrator approval.' -ForegroundColor Cyan
    Write-Host 'Elevation is required only to trust the public test certificate in Local Computer\Trusted People.'

    $escapedScriptPath = $PSCommandPath.Replace("'", "''")
    $childCommand = "& '$escapedScriptPath' -Version '$Version' -Runtime '$Runtime' -Elevated -Confirm:`$false"
    if ($KeepStaging) {
        $childCommand += ' -KeepStaging'
    }
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand) `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "The elevated Auralis signing process failed with exit code $($process.ExitCode)."
    }

    if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
        throw 'The elevated signing process completed without returning a result file.'
    }

    $result = Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host ''
    Write-Host 'Auralis local signing completed.' -ForegroundColor Green
    Write-Host "Package: $($result.Package)"
    Write-Host "SHA256 : $($result.Sha256)"
    Write-Host "Certificate: $($result.PublicCertificate)"
    Write-Warning 'This self-signed package is for local development and controlled testing only.'
    return $result
}

try {
    New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

    $certificateResult = & $certificateScript `
        -Subject $publisher `
        -TrustForLocalMachine `
        -Confirm:$false
    if ($null -eq $certificateResult) {
        throw 'The Auralis certificate helper did not return a certificate.'
    }

    $normalizedThumbprint = ([string]$certificateResult.Thumbprint).Replace(' ', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'The Auralis certificate helper returned an invalid SHA-1 thumbprint.'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$normalizedThumbprint" -ErrorAction Stop
    Assert-AuralisCertificate -Certificate $certificate

    $trustedCertificatePath = "Cert:\LocalMachine\TrustedPeople\$normalizedThumbprint"
    if (-not (Test-Path -LiteralPath $trustedCertificatePath)) {
        throw 'The public Auralis development certificate was not added to Local Computer\Trusted People.'
    }

    $buildArguments = @{
        Version = $Version
        Runtime = $Runtime
        IdentityName = $identityName
        Publisher = $publisher
        CertificateThumbprint = $normalizedThumbprint
    }
    if ($KeepStaging) {
        $buildArguments.KeepStaging = $true
    }
    $buildOutput = @(& $buildScript @buildArguments)
    $packageResult = $buildOutput |
        Where-Object {
            $null -ne $_ -and
            $null -ne $_.PSObject.Properties['Package'] -and
            $null -ne $_.PSObject.Properties['Signed']
        } |
        Select-Object -Last 1
    if ($null -eq $packageResult -or -not $packageResult.Signed) {
        throw 'The Auralis MSIX build did not report a valid signed package.'
    }

    $resolvedPackage = [System.IO.Path]::GetFullPath([string]$packageResult.Package)
    $resolvedExpectedPackage = [System.IO.Path]::GetFullPath($expectedPackage)
    if ($resolvedPackage -cne $resolvedExpectedPackage) {
        throw "The build returned an unexpected package path: $resolvedPackage"
    }
    if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) {
        throw "The signed Auralis package was not created: $resolvedPackage"
    }
    if ($packageResult.Identity -cne $identityName -or $packageResult.Publisher -cne $publisher) {
        throw 'The signed package identity or publisher does not match Auralis.'
    }

    $signTool = Get-SignToolPath
    $verificationOutput = & $signTool verify /pa /all /v $resolvedPackage 2>&1
    if ($LASTEXITCODE -ne 0) {
        $verificationOutput | Out-Host
        throw 'The final Auralis MSIX signature verification failed.'
    }

    $hash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
    $result = [pscustomobject]@{
        Package = $resolvedPackage
        Sha256 = $hash
        Identity = $identityName
        Publisher = $publisher
        CertificateThumbprint = $normalizedThumbprint
        PublicCertificate = [System.IO.Path]::GetFullPath([string]$certificateResult.PublicCertificate)
        TrustedStore = 'LocalMachine\TrustedPeople'
        ProductionReady = $false
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding UTF8

    Write-Host ''
    Write-Host 'Auralis local signing completed.' -ForegroundColor Green
    Write-Host "Package: $resolvedPackage"
    Write-Host "SHA256 : $hash"
    Write-Host "Certificate: $($result.PublicCertificate)"
    Write-Warning 'This self-signed package is for local development and controlled testing only.'
    Write-Warning 'Do not publish the development certificate as a production signing identity.'

    return $result
}
catch {
    if ($Elevated) {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }
    throw
}
