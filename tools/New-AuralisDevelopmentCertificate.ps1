[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^CN=.+$')]
    [string]$Subject = 'CN=Auralis',

    [ValidateRange(1, 10)]
    [int]$ValidYears = 3,

    [switch]$TrustForLocalMachine
)

$ErrorActionPreference = 'Stop'
$friendlyName = 'Auralis Development Package Signing'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publicCertificatePath = Join-Path $artifactsDirectory 'Auralis-Development-Package-Signing.cer'
$codeSigningEku = '1.3.6.1.5.5.7.3.3'

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

$certificate = Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object {
        $_.FriendlyName -eq $friendlyName -and
        $_.Subject -eq $Subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    if (-not $PSCmdlet.ShouldProcess(
        'Cert:\CurrentUser\My',
        "Create the $friendlyName certificate for $Subject")) {
        return
    }

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -FriendlyName $friendlyName `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears($ValidYears) `
        -TextExtension @(
            "2.5.29.37={text}$codeSigningEku",
            '2.5.29.19={text}ca=0')
}

Export-Certificate `
    -Cert $certificate `
    -FilePath $publicCertificatePath `
    -Type CERT `
    -Force | Out-Null

$trusted = Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
if ($TrustForLocalMachine -and -not $trusted) {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Trusting an MSIX test certificate requires an elevated PowerShell process.'
    }

    if ($PSCmdlet.ShouldProcess(
        'Cert:\LocalMachine\TrustedPeople',
        "Trust the public Auralis development certificate $($certificate.Thumbprint)")) {
        Import-Certificate `
            -FilePath $publicCertificatePath `
            -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        $trusted = $true
    }
}

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    NotAfter = $certificate.NotAfter
    PublicCertificate = $publicCertificatePath
    TrustedForLocalMachine = $trusted
}
