param([string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot

# Structural guardrails, not a security sandbox or a substitute for code review.
# Main-app implementation/resources must not select a known provider, rewrite its labels or style
# its identity. Cover every bundled first-party runtime project, not only the app/platform Host:
# moving provider knowledge into a bundled playback/transport/artwork assembly is not decoupling.
$taskRuntimeProjects = @('Auralis', 'Auralis.Platform.Host', 'Auralis.Platform.Abstractions',
    'Auralis.Playback.Abstractions', 'Auralis.Playback.Host', 'Auralis.Playback.LibVlc',
    'Auralis.MediaTransport.Abstractions', 'Auralis.MediaTransport.Host', 'Auralis.MediaTransport.Http',
    'Auralis.Artwork.Abstractions', 'Auralis.Artwork.Host', 'Auralis.Artwork.Http')
$taskPlatformKnowledge = '(?i)\b(?:bilibili|youtube|netease|subsonic|lrclib|hdslb|qpic|qlogo|qqmusic|sessdata|bili_jct|sapisid)\b|(?:qq\.com|music\.163\.com|googlevideo\.com|ytimg\.com)|酷狗|酷我|咪咕|网易|哔哩|["'']local-lyrics["'']'
$taskRuntimeRoots = @($taskRuntimeProjects | ForEach-Object { Join-Path $taskRoot $_ })
foreach ($taskSource in Get-ChildItem -LiteralPath @($taskRuntimeRoots + (Join-Path $taskRoot 'Auralis.Tests')) -File -Recurse |
    Where-Object { $_.Extension -in @('.cs','.js','.html','.css') -and $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' }) {
    if ((Get-Content -LiteralPath $taskSource.FullName -Raw) -match $taskPlatformKnowledge) {
        throw "Platform-specific implementation/resource in public runtime: $([IO.Path]::GetRelativePath($taskRoot, $taskSource.FullName))"
    }
}
foreach ($taskBuildFile in Get-ChildItem -LiteralPath $taskRuntimeRoots -Recurse -File |
    Where-Object { $_.Extension -in @('.csproj','.props','.targets') -and $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' }) {
    [xml]$taskBuildXml = Get-Content -LiteralPath $taskBuildFile.FullName -Raw
    # Include Reference/HintPath and imported targets as well as ProjectReference/Compile. XML
    # comments are documentation; actual values/attributes must not reference private inputs.
    foreach ($taskValue in $taskBuildXml.SelectNodes('//@* | //text()')) {
        if ($taskValue.Value -match '(?i)private-platforms|Auralis\.Platform(?:[\\/]|\.dll|\.csproj)|Auralis\.Plugin\.') {
            throw "Private build input in public runtime: $([IO.Path]::GetRelativePath($taskRoot, $taskBuildFile.FullName))"
        }
    }
}
foreach ($taskCoreTest in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Auralis.Tests') -Recurse -File |
    Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' }) {
    if ((Get-Content -LiteralPath $taskCoreTest.FullName -Raw) -match 'PRIVATE_PLATFORM_TESTS|private-platforms') {
        throw "Private implementation dependency returned to core tests: $($taskCoreTest.Name)"
    }
}
foreach ($taskProject in @('Auralis', 'Auralis.Platform.Host')) {
    [xml]$taskXml = Get-Content -LiteralPath (Join-Path $taskRoot "$taskProject\$taskProject.csproj") -Raw
    $taskAllowed = if ($taskProject -eq 'Auralis') { @('Auralis.Platform.Host', 'Auralis.Platform.Abstractions', 'Auralis.Playback.LibVlc', 'Auralis.Playback.Abstractions', 'Auralis.Playback.Host', 'Auralis.MediaTransport.Http', 'Auralis.MediaTransport.Host', 'Auralis.Artwork.Http', 'Auralis.Artwork.Abstractions', 'Auralis.Artwork.Host') } else { @('Auralis.Platform.Abstractions') }
    foreach ($taskReference in $taskXml.SelectNodes('//ProjectReference')) {
        $taskName = [IO.Path]::GetFileNameWithoutExtension($taskReference.Include)
        if ($taskName -notin $taskAllowed) { throw "Unexpected core project reference: $taskName" }
    }
    foreach ($taskCompile in $taskXml.SelectNodes('//Compile')) {
        if ($taskCompile.Include -match 'private-platforms|Auralis\.Platform[\\/]') { throw 'Concrete provider source linked into core.' }
    }
    if ($taskProject -eq 'Auralis' -and @($taskXml.SelectNodes('//PackageReference') |
        Where-Object { $_.Include -match 'LibVLC' }).Count -gt 0) { throw 'Direct decoder package returned to application.' }
}
foreach ($taskAppFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Auralis') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' }) {
    if ((Get-Content -LiteralPath $taskAppFile.FullName -Raw) -match 'LibVLCSharp|LibVlcAudioOutputConfiguration|BundledAudioPlayer') {
        throw "Decoder implementation dependency returned to $($taskAppFile.Name)."
    }
}
foreach ($taskTransportConsumer in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Auralis') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' -and $_.Name -ne 'MediaTransportServices.cs' }) {
    if ((Get-Content -LiteralPath $taskTransportConsumer.FullName -Raw) -match '\b(?:HttpMediaTransportSession|HttpMediaTransportFactory|MediaTransferBudget)\b') {
        throw "Concrete transport escaped composition point: $($taskTransportConsumer.Name)."
    }
}
$taskCoordinators = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Auralis\Services') -Filter 'OnlinePlatformCoordinator*.cs'
foreach ($taskArtworkConsumer in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Auralis') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' -and $_.Name -ne 'ArtworkServices.cs' }) {
    if ((Get-Content -LiteralPath $taskArtworkConsumer.FullName -Raw) -match '\b(?:HttpArtworkSource|HttpArtworkSourceFactory|OnlineArtworkProxyService)\b') {
        throw "Concrete artwork escaped composition point: $($taskArtworkConsumer.Name)."
    }
}
foreach ($taskArtwork in @('Auralis.Artwork.Abstractions', 'Auralis.Artwork.Http', 'Auralis.Artwork.Host')) {
    [xml]$taskArtworkXml = Get-Content -LiteralPath (Join-Path $taskRoot "$taskArtwork\$taskArtwork.csproj") -Raw
    foreach ($taskReference in $taskArtworkXml.SelectNodes('//ProjectReference')) {
        if ($taskArtwork -eq 'Auralis.Artwork.Abstractions' -or [IO.Path]::GetFileNameWithoutExtension($taskReference.Include) -ne 'Auralis.Artwork.Abstractions') { throw 'Artwork has a reverse app/platform dependency.' }
    }
    if ($taskArtworkXml.SelectNodes('//Compile[@Include]').Count) { throw 'Artwork links external implementation.' }
    $taskArtworkText = (Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskArtwork) -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' } |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    if ($taskArtworkText -match 'using (?:Auralis.Platform|Auralis.Services|System.Windows|LibVLCSharp)|DllImport|IntPtr') { throw 'Artwork depends on platform/decoder/UI implementation.' }
}
foreach ($taskTransport in @('Auralis.MediaTransport.Abstractions','Auralis.MediaTransport.Http','Auralis.MediaTransport.Host')) {
    [xml]$taskTransportXml = Get-Content -LiteralPath (Join-Path $taskRoot "$taskTransport\$taskTransport.csproj") -Raw
    foreach ($taskReference in $taskTransportXml.SelectNodes('//ProjectReference')) {
        if ($taskTransport -eq 'Auralis.MediaTransport.Abstractions' -or [IO.Path]::GetFileNameWithoutExtension($taskReference.Include) -ne 'Auralis.MediaTransport.Abstractions') { throw 'Transport has a reverse app/platform dependency.' }
    }
    if ($taskTransportXml.SelectNodes('//Compile[@Include]').Count) { throw 'Transport links external implementation.' }
    $taskTransportText = (Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskTransport) -Filter '*.cs' | ForEach-Object {
        $taskSource = Get-Content -LiteralPath $_.FullName -Raw
        if ($taskTransport -eq 'Auralis.MediaTransport.Host' -and $_.Name -eq 'MediaTransportPackageLoader.cs') {
            # ALC requires this exact native-library resolver return type. It is a private loader
            # implementation detail, never a window handle/public transport contract or P/Invoke.
            $taskSource = $taskSource.Replace('protected override IntPtr LoadUnmanagedDll(string name)', 'protected override ResolverResult LoadUnmanagedDll(string name)').Replace('return IntPtr.Zero;', 'return ResolverResult.Zero;')
        }
        $taskSource
    }) -join "`n"
    if ($taskTransportText -match 'using (?:Auralis.Platform|Auralis.Services|System.Windows|LibVLCSharp)|DllImport|IntPtr') { throw 'Transport depends on platform/decoder/UI implementation.' }
}
# Playback contracts stay platform/decoder/UI-free; bundled implementation must not reference the app.
foreach ($taskPlayback in @('Auralis.Playback.Abstractions', 'Auralis.Playback.LibVlc', 'Auralis.Playback.Host')) {
    [xml]$taskPlaybackXml = Get-Content -LiteralPath (Join-Path $taskRoot "$taskPlayback\$taskPlayback.csproj") -Raw
    foreach ($taskReference in $taskPlaybackXml.SelectNodes('//ProjectReference')) {
        if ($taskPlayback -eq 'Auralis.Playback.Abstractions' -or
            [IO.Path]::GetFileNameWithoutExtension($taskReference.Include) -ne 'Auralis.Playback.Abstractions') {
            throw 'Playback component has a reverse dependency on the app/platform.'
        }
    }
    if ($taskPlaybackXml.SelectNodes('//Compile[@Include]').Count -ne 0) { throw 'Playback component links external implementation.' }
    if (@($taskPlaybackXml.SelectNodes('//InternalsVisibleTo') | Where-Object { $_.Include -eq 'Auralis' }).Count -gt 0) {
        throw 'Private playback internals must not be exposed to the app.'
    }
}
$taskContractText = (Get-ChildItem (Join-Path $taskRoot 'Auralis.Playback.Abstractions') -Filter '*.cs' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
if ($taskContractText -match 'using (?:LibVLCSharp|System.Windows|Auralis.Platform)|DllImport|IntPtr') {
    throw 'Native/platform types entered playback contracts.'
}
$taskEmbedded = Get-Content -LiteralPath (Join-Path $taskRoot 'Auralis\MainWindow.EmbeddedVideo.cs') -Raw
if ($taskEmbedded -match 'SetVideoWindow|UseCompositedVideo|\.Hwnd') { throw 'UI output mode ownership returned to embedded playback.' }
foreach ($taskConsumer in @('Auralis\MainWindow.xaml.cs', 'Auralis\MusicVideoWindow.xaml.cs', 'Auralis\App.DefaultMusicRegistration.cs')) {
    if ((Get-Content -LiteralPath (Join-Path $taskRoot $taskConsumer) -Raw) -match 'PlaybackEngine|LibVlcPlaybackFactory|auralis\.playback\.libvlc') {
        throw 'Window/startup bypasses playback component composition.'
    }
}
[xml]$taskChecker = Get-Content -LiteralPath (Join-Path $taskRoot 'plugin-sdk\ContractCheck\Auralis.PluginContractCheck.csproj') -Raw
foreach ($taskReference in $taskChecker.SelectNodes('//ProjectReference')) {
    if ([IO.Path]::GetFileNameWithoutExtension($taskReference.Include) -notin @('Auralis.Platform.Host', 'Auralis.Platform.Abstractions')) {
        throw 'Contract checker must depend only on public SDK projects.'
    }
}
if ($taskChecker.SelectNodes('//Compile[@Include]').Count -ne 0) { throw 'Contract checker must not link player/private implementation source.' }
[xml]$taskPlaybackInspector = Get-Content -LiteralPath (Join-Path $taskRoot 'plugin-sdk\PlaybackInspect\Auralis.PlaybackInspect.csproj') -Raw
foreach ($taskReference in $taskPlaybackInspector.SelectNodes('//ProjectReference')) {
    if ([IO.Path]::GetFileNameWithoutExtension($taskReference.Include) -ne 'Auralis.Playback.Host') {
        throw 'Playback inspector must depend only on its public Host.'
    }
}
foreach ($taskInertFile in @('PlaybackComponentManifest.cs', 'PlaybackComponentCatalog.cs')) {
    $taskInertSource = Get-Content -LiteralPath (Join-Path $taskRoot "Auralis.Playback.Host\$taskInertFile") -Raw
    if ($taskInertSource -match 'System.Reflection|AssemblyLoadContext|Assembly\.Load|Activator\.|HttpClient|Process\.Start') {
        throw 'Playback metadata discovery must not load/execute code or make HTTP requests.'
    }
}
foreach ($taskFile in $taskCoordinators) {
    $taskSource = Get-Content -LiteralPath $taskFile.FullName -Raw
    if ($taskSource -match 'ProviderNames|IsKnownProvider|"(?:tx|wy|bili|ytm|kg|kw|mg|bd|subsonic)"') {
        throw "Platform-specific routing returned to $($taskFile.Name)."
    }
}
$taskSettings = Get-Content -LiteralPath (Join-Path $taskRoot 'Auralis\Services\PlatformSettingsStore.cs') -Raw
if ($taskSettings -match 'qq\.quality|gateway\.baseUrl|QqQuality|ValidateGateway') { throw 'Platform-specific settings policy returned to core.' }
foreach ($taskBridge in @('Auralis\MainWindow.xaml.cs', 'Auralis\wwwroot\app.js')) {
    $taskSource = Get-Content -LiteralPath (Join-Path $taskRoot $taskBridge) -Raw
    if ($taskSource -match '(?i)(?:open|signOut|request|send|set|render|normalize|synchronize)(?:Qq|YouTubeMusic|NeteaseMusic|Bilibili)|(?:qq|youtubeMusic|neteaseMusic|bilibili)(?:Authentication|Playlists?|Navigation|Section)') {
        throw "Platform-specific account/collection bridge returned to $taskBridge."
    }
    if ($taskSource -match '[''"](?:tx|wy|bili|ytm)[''"]') { throw "Hard-coded platform ID returned to $taskBridge." }
}
$taskSavedStore = Get-Content -LiteralPath (Join-Path $taskRoot 'Auralis\Services\SavedPlaylistStore.cs') -Raw
if ($taskSavedStore -match 'ProviderId\s+(?:is\s+["'']|switch)' -or
    $taskSavedStore -match 'PlatformBackendService|PlatformPluginCatalog|DiscoverAsync|HttpClient') {
    throw 'Saved references must use generic identity syntax, not platform switches, installed inventory or network access.'
}
if ($PublishedDirectory) {
    $taskPublished = (Resolve-Path -LiteralPath $PublishedDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $taskPublished 'Auralis.exe'))) { throw 'Not a player publish output.' }
    $taskAllowedDlls = @('Auralis.dll', 'Auralis.Platform.Host.dll', 'Auralis.Platform.Abstractions.dll', 'Auralis.Playback.Abstractions.dll', 'Auralis.Playback.LibVlc.dll', 'Auralis.Playback.Host.dll', 'Auralis.MediaTransport.Abstractions.dll', 'Auralis.MediaTransport.Http.dll', 'Auralis.MediaTransport.Host.dll', 'Auralis.Artwork.Abstractions.dll', 'Auralis.Artwork.Http.dll', 'Auralis.Artwork.Host.dll')
    foreach ($taskRequired in $taskAllowedDlls) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskPublished $taskRequired) -PathType Leaf)) {
            throw "Missing bundled core component: $taskRequired"
        }
    }
    foreach ($taskDll in Get-ChildItem -LiteralPath $taskPublished -Filter 'Auralis*.dll' -Recurse) {
        if ($taskDll.Name -notin $taskAllowedDlls) { throw "Private implementation in core-only output: $($taskDll.Name)" }
    }
    if (Test-Path -LiteralPath (Join-Path $taskPublished 'plugins\platforms')) { throw 'Core-only output includes platform plugins.' }
}
Write-Host 'PASS plugin boundaries: core references, generic coordinator/settings, optional core-only publish payload.'
Write-Host 'All bundled first-party runtime platform/build-input guards passed; targeted checks do not replace full source/resource/license and native acceptance review.'
