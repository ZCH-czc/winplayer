[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.16.10',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$IdentityName = 'Auralis.Player',

    [ValidateNotNullOrEmpty()]
    [string]$Publisher = 'CN=Auralis',

    [string]$CertificatePath,

    [string]$CertificateThumbprint,

    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$manifestTemplate = Join-Path $repositoryRoot 'packaging\msix\AppxManifest.xml'
$iconSource = Join-Path $repositoryRoot 'Auralis\Assets\AuralisIcon.png'
$stagingDirectory = Join-Path $artifactsDirectory ('.msix-staging-' + [Guid]::NewGuid().ToString('N'))
$isSigningRequested = -not [string]::IsNullOrWhiteSpace($CertificatePath) -or
    -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
$packageSuffix = if ($isSigningRequested) { '' } else { '-unsigned' }
$packageFile = Join-Path $artifactsDirectory "Auralis-$Version-$Runtime$packageSuffix.msix"
$checksumFile = "$packageFile.sha256"

if ($CertificatePath -and $CertificateThumbprint) {
    throw 'CertificatePath and CertificateThumbprint cannot be used together.'
}

$versionParts = @($Version.Split('.') | ForEach-Object { [int]$_ })
while ($versionParts.Count -lt 4) {
    $versionParts += 0
}
if ($versionParts.Count -ne 4 -or ($versionParts | Where-Object { $_ -lt 0 -or $_ -gt 65535 })) {
    throw 'MSIX versions must contain four numeric parts between 0 and 65535.'
}
$packageVersion = $versionParts -join '.'

$windowsKitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
$sdkToolsDirectory = Get-ChildItem -LiteralPath $windowsKitsBin -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\signtool.exe') -PathType Leaf)
    } |
    Select-Object -First 1

if ($null -eq $sdkToolsDirectory) {
    throw 'Windows SDK MakeAppx.exe and SignTool.exe were not found.'
}

$makeAppx = Join-Path $sdkToolsDirectory.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdkToolsDirectory.FullName 'x64\signtool.exe'
Add-Type -AssemblyName System.Drawing

function Assert-PackageSigningCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [Parameter(Mandatory)]
        [string]$ExpectedPublisher
    )

    if (-not $Certificate.HasPrivateKey) {
        throw 'The selected certificate does not contain a private key.'
    }
    if (-not [string]::Equals(
        $Certificate.Subject,
        $ExpectedPublisher,
        [System.StringComparison]::Ordinal)) {
        throw "The certificate subject '$($Certificate.Subject)' does not exactly match Publisher '$ExpectedPublisher'."
    }

    $now = Get-Date
    if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) {
        throw 'The selected certificate is not currently valid.'
    }

    $ekuExtension = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    if ($null -eq $ekuExtension) {
        throw 'The selected certificate does not declare an Enhanced Key Usage extension.'
    }

    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
    $hasCodeSigningUsage = $enhancedKeyUsage.EnhancedKeyUsages |
        Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' } |
        Select-Object -First 1
    if ($null -eq $hasCodeSigningUsage) {
        throw 'The selected certificate is not valid for code signing.'
    }

    $basicConstraintsExtension = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    if ($null -ne $basicConstraintsExtension) {
        $basicConstraints = [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraintsExtension
        if ($basicConstraints.CertificateAuthority) {
            throw 'A certificate-authority certificate cannot be used as the Auralis package signing leaf certificate.'
        }
    }
}

function New-PackageLogo {
    param(
        [Parameter(Mandatory)] [System.Drawing.Image]$Source,
        [Parameter(Mandatory)] [string]$Destination,
        [Parameter(Mandatory)] [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    try {
        $bitmap.SetResolution(96, 96)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($Source, 0, 0, $Size, $Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Assert-SafeStagingPath {
    param([Parameter(Mandatory)] [string]$Path)

    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsDirectory)
    $resolvedStaging = [System.IO.Path]::GetFullPath($Path)
    if ([System.IO.Path]::GetDirectoryName($resolvedStaging) -ne $resolvedArtifacts -or
        -not [System.IO.Path]::GetFileName($resolvedStaging).StartsWith(
            '.msix-staging-',
            [System.StringComparison]::Ordinal)) {
        throw 'Refusing to clean an unvalidated MSIX staging directory.'
    }
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    dotnet publish (Join-Path $repositoryRoot 'Auralis\Auralis.csproj') `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Auralis publish failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        (Join-Path $stagingDirectory 'Auralis.exe'),
        (Join-Path $stagingDirectory 'wwwroot\app.js'),
        (Join-Path $stagingDirectory 'libvlc\win-x64\libvlc.dll'),
        (Join-Path $stagingDirectory 'Auralis.Platform.Host.dll'),
        (Join-Path $stagingDirectory 'Auralis.Platform.Abstractions.dll'),
        (Join-Path $stagingDirectory 'Auralis.Artwork.Abstractions.dll'),
        (Join-Path $stagingDirectory 'Auralis.Artwork.Host.dll'),
        (Join-Path $stagingDirectory 'Auralis.Artwork.Http.dll'),
        (Join-Path $stagingDirectory 'Auralis.Playback.Abstractions.dll'),
        (Join-Path $stagingDirectory 'Auralis.Playback.Host.dll'),
        (Join-Path $stagingDirectory 'Auralis.Playback.LibVlc.dll'),
        (Join-Path $stagingDirectory 'Auralis.MediaTransport.Abstractions.dll'),
        (Join-Path $stagingDirectory 'Auralis.MediaTransport.Host.dll'),
        (Join-Path $stagingDirectory 'Auralis.MediaTransport.Http.dll'),
        (Join-Path $stagingDirectory 'THIRD-PARTY-NOTICES.txt'),
        (Join-Path $stagingDirectory 'licenses\LibVLC\LGPL-2.1.txt'),
        (Join-Path $stagingDirectory 'licenses\LibVLC\SOURCES.txt'),
        (Join-Path $stagingDirectory 'licenses\LibVLCSharp\libvlcsharp.nuspec'),
        (Join-Path $stagingDirectory 'licenses\VideoLAN.LibVLC.Windows\videolan.libvlc.windows.nuspec'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.Web.WebView2\LICENSE.txt'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.Web.WebView2\NOTICE.txt'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.NETCore.App.Runtime.win-x64\LICENSE.TXT'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.NETCore.App.Runtime.win-x64\THIRD-PARTY-NOTICES.TXT'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.WindowsDesktop.App.Runtime.win-x64\LICENSE'),
        (Join-Path $stagingDirectory 'licenses\Microsoft.AspNetCore.App.Runtime.win-x64\THIRD-PARTY-NOTICES.TXT')
    )
    $missingFiles = @(
        $requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }
    )
    if ($missingFiles.Count -gt 0) {
        throw "The Auralis publish output is incomplete: $($missingFiles -join ', ')"
    }

    # Portable PDBs are not runtime dependencies and embed local source paths.
    if (Test-Path -LiteralPath (Join-Path $stagingDirectory 'plugins\platforms')) {
        throw '公开播放器包不应包含平台插件。私人插件请通过独立安装流程部署。'
    }
    if (Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File |
        Where-Object { $_.Name -eq 'Auralis.Platform.dll' -or $_.Name -like 'Auralis.Plugin.*.dll' }) {
        throw '发现具体平台实现，已阻止打包。'
    }

    # Keep them out of the distributable package even when dotnet publish emits them.
    $debugSymbols = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File -Filter '*.pdb')
    foreach ($debugSymbol in $debugSymbols) {
        $resolvedSymbol = [System.IO.Path]::GetFullPath($debugSymbol.FullName)
        $resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingDirectory) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedSymbol.StartsWith($resolvedStagingRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a debug symbol outside the validated staging directory: $resolvedSymbol"
        }
        Remove-Item -LiteralPath $resolvedSymbol -Force
    }
    if (Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File -Filter '*.pdb' | Select-Object -First 1) {
        throw 'The publish staging directory still contains PDB debug symbols.'
    }

    Copy-Item -LiteralPath $manifestTemplate -Destination (Join-Path $stagingDirectory 'AppxManifest.xml')
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $stagingDirectory 'AppxManifest.xml') -Raw -Encoding UTF8
    $manifest.Package.Identity.Name = $IdentityName
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Identity.Version = $packageVersion
    $manifest.Package.Identity.ProcessorArchitecture = 'x64'
    $manifest.Save((Join-Path $stagingDirectory 'AppxManifest.xml'))

    $assetsDirectory = Join-Path $stagingDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    $sourceImage = [System.Drawing.Image]::FromFile($iconSource)
    try {
        New-PackageLogo -Source $sourceImage -Destination (Join-Path $assetsDirectory 'StoreLogo.png') -Size 50
        New-PackageLogo -Source $sourceImage -Destination (Join-Path $assetsDirectory 'Square44x44Logo.png') -Size 44
        New-PackageLogo -Source $sourceImage -Destination (Join-Path $assetsDirectory 'Square150x150Logo.png') -Size 150
    }
    finally {
        $sourceImage.Dispose()
    }

    if (Test-Path -LiteralPath $packageFile -PathType Leaf) {
        Remove-Item -LiteralPath $packageFile -Force
    }
    $makeAppxOutput = & $makeAppx pack /d $stagingDirectory /p $packageFile /o 2>&1
    $makeAppxExitCode = $LASTEXITCODE
    if ($makeAppxExitCode -ne 0) {
        $makeAppxOutput | Out-Host
        throw "MakeAppx failed with exit code $makeAppxExitCode."
    }
    Write-Host 'MSIX package creation succeeded.'

    $signed = $false
    if ($CertificatePath) {
        $resolvedCertificate = [System.IO.Path]::GetFullPath($CertificatePath)
        if (-not (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf)) {
            throw "The signing certificate does not exist: $resolvedCertificate"
        }

        $password = [Environment]::GetEnvironmentVariable('AURALIS_SIGNING_PASSWORD')
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $resolvedCertificate,
            $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            Assert-PackageSigningCertificate -Certificate $certificate -ExpectedPublisher $Publisher
        }
        finally {
            $certificate.Dispose()
        }

        $signArguments = @('sign', '/fd', 'SHA256', '/f', $resolvedCertificate)
        if (-not [string]::IsNullOrEmpty($password)) {
            $signArguments += @('/p', $password)
        }
        $signArguments += $packageFile
        & $signTool @signArguments
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed with exit code $LASTEXITCODE."
        }
        $signed = $true
    }
    elseif ($CertificateThumbprint) {
        $normalizedThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$normalizedThumbprint" -ErrorAction Stop
        Assert-PackageSigningCertificate -Certificate $certificate -ExpectedPublisher $Publisher
        & $signTool sign /fd SHA256 /sha1 $normalizedThumbprint /s My $packageFile
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed with exit code $LASTEXITCODE."
        }
        $signed = $true
    }
    else {
        Write-Warning 'The MSIX was created unsigned. Supply a trusted certificate before public distribution.'
    }

    if ($signed) {
        $verifyOutput = & $signTool verify /pa /all /v $packageFile 2>&1
        $verifyExitCode = $LASTEXITCODE
        if ($verifyExitCode -ne 0) {
            $verifyOutput | Out-Host
            Remove-Item -LiteralPath $packageFile -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $checksumFile -Force -ErrorAction SilentlyContinue
            throw "SignTool verification failed with exit code $verifyExitCode. The invalid package was removed."
        }
        Write-Host 'MSIX signature verification succeeded.'
    }

    $hash = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
    "$hash  $([System.IO.Path]::GetFileName($packageFile))" |
        Set-Content -LiteralPath $checksumFile -Encoding ascii

    [pscustomobject]@{
        Package = $packageFile
        Bytes = (Get-Item -LiteralPath $packageFile).Length
        Sha256 = $hash
        Signed = $signed
        Identity = $IdentityName
        Publisher = $Publisher
        Version = $packageVersion
    }
}
finally {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        Assert-SafeStagingPath -Path $stagingDirectory
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
