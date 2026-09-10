using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Auralis.Localization;
using Auralis.Models;
using Auralis.Platform.Abstractions;
using Auralis.Services;
using Auralis.Artwork;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Auralis;

public partial class MainWindow : Window
{
    private const int GwlStyle = -16;
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSystemMenu = 0x00080000L;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const uint DwmColorNone = 0xfffffffe;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };

    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LibraryStore _libraryStore = new();
    private readonly MusicFolderStore _musicFolderStore = new();
    private readonly LanSharingSettingsStore _lanSharingSettingsStore = new();
    private readonly LyricsService _lyricsService = new(onlineLookup: (request, token) =>
        ((App)System.Windows.Application.Current).PlatformBackend.LookupLyricsAsync(request, token));
    private readonly Dictionary<string, StoredTrack> _library = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _musicFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _musicFolderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pendingMusicFolderChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingMusicFolderRefreshPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingMusicFolderRescans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _musicFolderChangeLock = new();
    private readonly SemaphoreSlim _musicFolderScanGate = new(1, 1);
    private readonly object _platformConfigurationSyncLock = new();
    private readonly TaskCompletionSource<bool> _localStartupReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowSettingsStore _windowSettingsStore = new();
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private readonly IPlaybackSession _mediaPlayer = PlaybackServices.Create(
        new DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
    private readonly OnlinePlaybackSourceService _onlinePlaybackSource = new();
    private readonly IArtworkSource _onlineArtworkProxy = ArtworkServices.CreateDefault();
    private readonly SystemMediaSessionService _systemMediaSession = new();
    private readonly DesktopLyricsWindow _desktopLyricsWindow = new();
    private readonly TaskbarMediaExperience _taskbarMediaExperience = new();
    private readonly TrayContextMenuWindow _trayMenu;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _musicFolderRefreshTimer;
    private readonly DispatcherTimer _trayMinimizeTimer;
    private string? _currentTrackId;
    private bool _mediaKeysEnabled = true;
    private CancellationTokenSource? _lyricsRequestCancellation;
    private CancellationTokenSource? _platformSearchCancellation;
    private CancellationTokenSource? _platformPlaybackCancellation;
    private CancellationTokenSource? _platformVideoCancellation;
    private CancellationTokenSource? _systemMediaArtworkCancellation;
    private long _audioDiagnosticsRequestVersion;
    private Task? _platformConfigurationSyncTask;
    private OnlinePlatformCoordinator? _onlinePlatforms;
    private LyricsResponse? _currentLyrics;
    private TrackInfo? _currentTrackInfo;
    private string _lyricsPreference = "localFirst";
    private string _lyricsSourceSelection = "auto";
    private readonly string _coverCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Auralis",
        "Covers");
    private readonly string _artistImageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Auralis",
        "ArtistImages");
    private readonly string _backgroundImageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Auralis",
        "Backgrounds");
    private nint _windowHandle;
    private bool _isFullscreen;
    private bool _userTopmost;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;
    private bool _closeToTrayEnabled;
    private string _uiLanguagePreference = UiLanguagePreference.System;
    private LanSharingSettings _lanSharingSettings = new();
    private bool _trayEnabled;
    private bool _exitRequested;
    private bool _currentPlaybackIsOnline;
    private bool _nativeDarkTheme;
    private bool _windowClosed;
    private bool _musicFolderTimerRestartQueued;
    private DateTimeOffset? _trayMinimizeDeadline;
    private readonly HashSet<MusicVideoWindow> _musicVideoWindows = [];

    private OnlinePlatformCoordinator OnlinePlatforms =>
        _onlinePlatforms ??= new OnlinePlatformCoordinator(((App)System.Windows.Application.Current).PlatformBackend);

    private LanMusicSharingService LanMusicSharing =>
        ((App)System.Windows.Application.Current).LanMusicSharing;

    private IAppLogger AppLogger => ((App)System.Windows.Application.Current).Logger;

    private UiLanguageState CurrentLanguageState => UiLanguagePreference.Resolve(_uiLanguagePreference);

    public MainWindow()
    {
        InitializeComponent();
        LoadNativeWindowSettings();
        _trayMenu = CreateTrayMenu();
        ApplyNativeLocalizedText();
        _trayIcon = CreateTrayIcon();
        ApplyNativeLocalizedText();
        _trayIcon.Visible = _trayEnabled;
        _playbackTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => NotifyPlaybackState(), Dispatcher);
        _musicFolderRefreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, OnMusicFolderRefreshTimerTick, Dispatcher);
        _trayMinimizeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTrayMinimizeTimerTick, Dispatcher);
        _systemMediaSession.CommandRequested += OnSystemMediaCommandRequested;
        _systemMediaSession.PositionChangeRequested += OnSystemMediaPositionChangeRequested;
        _desktopLyricsWindow.LockStateChanged += OnDesktopLyricsLockStateChanged;
        _taskbarMediaExperience.CommandRequested += OnTaskbarMediaCommandRequested;
        _taskbarMediaExperience.OpenAuralisRequested += RestoreFromTray;
        LanMusicSharing.StateChanged += OnLanMusicSharingStateChanged;
        _mediaPlayer.MediaOpened += (_, _) =>
        {
            // Playing is not proof of a decoded video frame. The embedded readiness watcher
            // acknowledges the first complete picture. Start-paused handoffs may show a
            // waiting-picture placeholder until the user resumes decoding.
            if (_embeddedVideoActive && !_mediaPlayer.WantsPlayback)
            {
                _embeddedVideoOpened = true;
                CancelAndDispose(ref _embeddedOpenTimeout);
                _ = SendEmbeddedVideoStateAsync(_currentTrackId, _embeddedVideoRequestId, true, null);
            }
            var playing = _mediaPlayer.IsPlaying;
            if (playing)
            {
                _playbackTimer.Start();
            }
            else
            {
                _playbackTimer.Stop();
            }

            NotifyPlaybackState(playing);
        };
        _mediaPlayer.MediaEnded += (_, _) =>
        {
            _playbackTimer.Stop();
            NotifyPlaybackState(false);
            _ = ExecuteScriptAsync($"window.Auralis?.nativeEnded({JsonSerializer.Serialize(_currentTrackId)})");
        };
        _mediaPlayer.MediaFailed += (_, args) =>
        {
            if (_embeddedVideoActive)
            {
                StopEmbeddedVideo(true);
                _ = SendEmbeddedVideoStateAsync(_currentTrackId, _embeddedVideoRequestId, false, "视频播放失败，已返回音频。");
                return;
            }
            _playbackTimer.Stop();
            NotifyPlaybackState(false);
            var safeMessage = _currentPlaybackIsOnline
                ? "无法播放此在线音频，请检查网络、服务权限或地区限制。"
                : $"无法播放此音频：{args.Message}";
            var message = JsonSerializer.Serialize(safeMessage);
            _ = ExecuteScriptAsync($"window.Auralis?.showToast({message})");
            if (_currentPlaybackIsOnline && _currentTrackId is { } handle)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    handle,
                    success = false,
                    error = new { code = "mediaFailed", message = safeMessage }
                }, WebJsonOptions);
                _ = ExecuteScriptAsync($"window.Auralis?.setPlatformPlaybackResult({payload})");
            }
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += (_, _) =>
        {
            UpdateWebViewResizeFrame();
            NotifyWindowState();
            _taskbarMediaExperience.RefreshAfterHostWindowStateChanged();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(NotifyDpiScale);
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnWindowClosing;
        Closed += async (_, _) =>
        {
            _windowClosed = true;
            _ = ClosePlaybackComponentsAsync();
            _ = CloseMediaTransportComponentsAsync();
            ClosePlatformLogins();
            _localStartupReady.TrySetResult(true);
            _musicFolderRefreshTimer.Stop();
            _trayMinimizeTimer.Stop();
            DisposeMusicFolderWatchers();
            _lyricsRequestCancellation?.Cancel();
            _lyricsRequestCancellation?.Dispose();
            CancelAndDispose(ref _platformSearchCancellation);
            CancelAndDispose(ref _platformPlaybackCancellation);
            CancelAndDispose(ref _platformVideoCancellation);
            CancelAndDispose(ref _systemMediaArtworkCancellation);
            foreach (var videoWindow in _musicVideoWindows.ToArray())
            {
                videoWindow.Close();
            }
            _musicVideoWindows.Clear();
            _playbackTimer.Stop();
            _taskbarMediaExperience.Dispose();
            if (((App)System.Windows.Application.Current).LanMusicSharing is { } lanMusicSharing)
            {
                lanMusicSharing.StateChanged -= OnLanMusicSharingStateChanged;
            }
            _desktopLyricsWindow.CloseFromApp();
            _trayMenu.CloseFromApp();
            if (PlayerWebView.CoreWebView2 is not null)
            {
                PlayerWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                PlayerWebView.CoreWebView2.WebResourceRequested -= OnLocalImageRequested;
                PlayerWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
            PlayerWebView.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _mediaPlayer.Dispose();
            CancelPlatformExtras();
            CancelAndDispose(ref _embeddedVideoCancellation);
            CancelAndDispose(ref _embeddedOpenTimeout);
            _systemMediaSession.Dispose();
            try
            {
                ++_prefetchVersion;
                await DrainPrefetchAsync(null);
                await _onlinePlaybackSource.DisposeAsync();
                await _embeddedVideoSource.DisposeAsync();
                await _embeddedAudioSource.DisposeAsync();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Online playback cleanup failed: {exception.Message}");
            }
            finally
            {
                try { await _onlineArtworkProxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { Debug.WriteLine("Artwork component cleanup did not complete successfully within the shutdown budget."); }
                _lyricsService.Dispose();
                // Hidden auxiliary windows must not leave a windowless host owning the
                // single-instance lock after the real shell has completed cleanup.
                System.Windows.Application.Current.Shutdown();
            }
        };

        LogStartupMilestone("window.constructed");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _systemMediaSession.Initialize(_windowHandle, Dispatcher);
        var style = GetWindowStyle(_windowHandle);
        style = new nint(style.ToInt64() | WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu);
        SetWindowStyle(_windowHandle, style);
        SetWindowPos(
            _windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        var cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(_windowHandle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(_windowHandle, DwmBorderColor, ref borderColor, sizeof(uint));
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowMessageHook);
        ConstrainInitialSizeToCurrentMonitor();
        LogStartupMilestone("window.source-initialized");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Log(
                AppLogLevel.Critical,
                "startup",
                "webview.initialization-failed",
                exception: exception);
            var language = CurrentLanguageState.ResolvedLanguage;
            System.Windows.MessageBox.Show(
                $"{NativeText.Get(language, "startup.webview.failure")}\n\n{exception.Message}",
                NativeText.Get(language, "startup.failure.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auralis",
            "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await PlayerWebView.EnsureCoreWebView2Async(environment);
        LogStartupMilestone("webview.controller-ready");

        PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 244, 244, 244);
        PlayerWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        PlayerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        PlayerWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        PlayerWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        PlayerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        await PlayerWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
            (() => {
              const updateVisibility = () => {
                if (document.documentElement) {
                  document.documentElement.dataset.documentHidden = document.hidden ? 'true' : 'false';
                }
              };
              document.addEventListener('visibilitychange', updateVisibility, { passive: true });
              if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', updateVisibility, { once: true });
              } else {
                updateVisibility();
              }
            })();
            """);

        PlayerWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        PlayerWebView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("http://app.auralis.local/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        PlayerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.auralis.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        Directory.CreateDirectory(_coverCacheDirectory);

        Directory.CreateDirectory(_artistImageDirectory);

        Directory.CreateDirectory(_backgroundImageDirectory);

        foreach (var host in new[]
                 {
                     "covers.auralis.local",
                     "artists.auralis.local",
                     "backgrounds.auralis.local",
                     "platform-art.auralis.local",
                     "video.auralis.local"
                 })
        {
            PlayerWebView.CoreWebView2.AddWebResourceRequestedFilter(
                $"https://{host}/*",
                CoreWebView2WebResourceContext.All);
        }
        PlayerWebView.CoreWebView2.WebResourceRequested += OnLocalImageRequested;

        PlayerWebView.NavigationCompleted += OnNavigationCompleted;
        PlayerWebView.Source = new Uri("http://app.auralis.local/index.html");
    }

    private void OnLocalImageRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (PlayerWebView.CoreWebView2 is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Host == "video.auralis.local") { ServeVideoFrame(e, uri); return; }
        if (string.Equals(uri.Host, "platform-art.auralis.local", StringComparison.OrdinalIgnoreCase))
        {
            var handle = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var deferral = e.GetDeferral();
            _ = CompletePlatformArtworkRequestAsync(e, handle, deferral);
            return;
        }

        var root = uri.Host.ToLowerInvariant() switch
        {
            "covers.auralis.local" => _coverCacheDirectory,
            "artists.auralis.local" => _artistImageDirectory,
            "backgrounds.auralis.local" => _backgroundImageDirectory,
            _ => null
        };
        if (root is null) return;

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        var path = Path.Combine(root, fileName);
        if (!File.Exists(path))
        {
            e.Response = PlayerWebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        e.Response = PlayerWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            $"Content-Type: {contentType}\r\nCache-Control: public, max-age=31536000, immutable");
    }

    private async Task CompletePlatformArtworkRequestAsync(
        CoreWebView2WebResourceRequestedEventArgs e,
        string handle,
        CoreWebView2Deferral deferral)
    {
        try
        {
            if (handle.Length is < 1 or > 128 || handle.Contains('/') ||
                !OnlinePlatforms.TryGetArtworkUri(handle, out var source, out var destinationAllowed) || source is null)
            {
                SetPlatformArtworkErrorResponse(e, 404, "Not Found");
                return;
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var avatar = handle.StartsWith("avatar-", StringComparison.Ordinal);
            var artwork = await _onlineArtworkProxy.FetchAsync(new ArtworkRequest(source,
                avatar ? 2 * 1024 * 1024 : 16 * 1024 * 1024, destinationAllowed), cancellation.Token);
            if (destinationAllowed?.Invoke(source) == false)
            {
                SetPlatformArtworkErrorResponse(e, 404, "Not Found");
                return;
            }
            var stream = artwork.OpenRead();
            e.Response = PlayerWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream,
                200,
                "OK",
                $"Content-Type: {artwork.ContentType}\r\nCache-Control: public, max-age=3600");
        }
        catch (Exception exception) when (exception is ArtworkException or
                                           HttpRequestException or
                                           OperationCanceledException or
                                           IOException or
                                           InvalidOperationException or
                                           COMException)
        {
            SetPlatformArtworkErrorResponse(e, 502, "Bad Gateway");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetPlatformArtworkErrorResponse(
        CoreWebView2WebResourceRequestedEventArgs e,
        int statusCode,
        string reason)
    {
        if (PlayerWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        e.Response = core.Environment.CreateWebResourceResponse(null, statusCode, reason, "Cache-Control: no-store");
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        PlayerWebView.NavigationCompleted -= OnNavigationCompleted;
        LogStartupMilestone("webview.navigation-completed");
        NotifyWindowState();
        NotifyDpiScale();
        await SendUiLanguageStateAsync();
        HideNativeSplash();

        var storedTracksTask = _libraryStore.LoadAsync();
        var storedMusicFoldersTask = _musicFolderStore.LoadAsync();
        var lanSharingSettingsTask = _lanSharingSettingsStore.LoadAsync();
        var storedTracks = await storedTracksTask;
        var validStoredTracks = await Task.Run(
            () => storedTracks.Where(track => File.Exists(track.Path)).ToArray());
        _library.Clear();

        foreach (var track in validStoredTracks)
        {
            _library[track.Path] = track;
        }

        if (_library.Count != storedTracks.Count)
        {
            await PersistLibraryAsync();
        }

        await SendLibraryAsync("receiveLibrary", _library.Values, validatePaths: false);
        LogStartupMilestone("library.first-snapshot-sent", _library.Count);
        _lanSharingSettings = await lanSharingSettingsTask;
        await LanMusicSharing.ApplySettingsAsync(_lanSharingSettings);
        await SendLanMusicSharingStateAsync();
        var storedMusicFolders = await storedMusicFoldersTask;
        _musicFolders.Clear();
        foreach (var folder in storedMusicFolders)
        {
            _musicFolders.Add(folder);
        }
        RebuildMusicFolderWatchers();
        await SendMusicFoldersAsync();
        LogStartupMilestone("library.folder-watchers-ready", _musicFolders.Count);
        _ = ReconcileMusicFoldersAsync(_musicFolders, userInitiated: false);
        await HandlePendingShellAudioFileAsync();
        await ExecuteScriptAsync($"window.Auralis?.setRuntimeBuild?.({JsonSerializer.Serialize(RuntimeBuildIdentity.Label)})");
        _localStartupReady.TrySetResult(true);
        _ = SendPlatformConfigurationAfterStartupAsync();
    }

    private void HideNativeSplash()
    {
        PlayerWebView.Visibility = Visibility.Visible;
        if (NativeSplash.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            NativeSplash.BeginAnimation(OpacityProperty, null);
            NativeSplash.Opacity = 0;
            NativeSplash.Visibility = Visibility.Collapsed;
            LogStartupMilestone("shell.interactive");
            return;
        }

        var fade = new DoubleAnimation
        {
            From = NativeSplash.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            NativeSplash.Visibility = Visibility.Collapsed;
            LogStartupMilestone("shell.interactive");
        };
        NativeSplash.BeginAnimation(OpacityProperty, fade);
    }

    private void LogStartupMilestone(string eventId, int? count = null)
    {
        IReadOnlyDictionary<string, object?>? fields = count is null
            ? new Dictionary<string, object?>
            {
                [AppLogFieldNames.DurationMilliseconds] = _startupStopwatch.Elapsed.TotalMilliseconds
            }
            : new Dictionary<string, object?>
            {
                [AppLogFieldNames.DurationMilliseconds] = _startupStopwatch.Elapsed.TotalMilliseconds,
                [AppLogFieldNames.Count] = count.Value
            };
        AppLogger.Log(AppLogLevel.Information, "startup", eventId, fields);
    }

    private async Task SendUiLanguageStateAsync()
    {
        var payload = JsonSerializer.Serialize(CurrentLanguageState, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setUiLanguageState?.({payload})");
    }

    private void ApplyNativeLocalizedText()
    {
        var language = CurrentLanguageState.ResolvedLanguage;
        NativeSplashSubtitle.Text = NativeText.Get(language, "splash.subtitle");
        _trayMenu?.UpdateLanguage(language);

        if (IsLoaded && _trayIcon is not null)
        {
            _trayIcon.Text = NativeText.Get(language, "tray.tooltip");
        }
    }

    private async Task OpenApplicationLogsAsync()
    {
        var language = CurrentLanguageState.ResolvedLanguage;
        var logDirectory = AppLogger.LogDirectory;
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            var unavailable = JsonSerializer.Serialize(NativeText.Get(language, "logs.unavailable"));
            await ExecuteScriptAsync($"window.Auralis?.showToast({unavailable})");
            return;
        }

        try
        {
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = logDirectory,
                UseShellExecute = true
            });
            AppLogger.Log(AppLogLevel.Information, "diagnostics", "logs.folder-opened");
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           Win32Exception)
        {
            AppLogger.Log(AppLogLevel.Warning, "diagnostics", "logs.folder-open-failed", exception: exception);
            var failed = JsonSerializer.Serialize(NativeText.Get(language, "logs.open.failed"));
            await ExecuteScriptAsync($"window.Auralis?.showToast({failed})");
        }
    }

    private async Task SendPlatformConfigurationAfterStartupAsync()
    {
        await _localStartupReady.Task;
        if (_windowClosed)
        {
            return;
        }

        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        if (!_windowClosed)
        {
            await SendPlatformConfigurationAsync();
        }
    }

    private void OnDesktopLyricsLockStateChanged(object? sender, DesktopLyricsLockStateChangedEventArgs e)
    {
        _ = SynchronizeDesktopLyricsLockStateAsync(e.Locked);
    }

    private async Task SynchronizeDesktopLyricsLockStateAsync(bool locked)
    {
        var jsValue = locked ? "true" : "false";
        try
        {
            await ExecuteScriptAsync($$"""
                (() => {
                  const locked = {{jsValue}};
                  localStorage.setItem('auralis:desktop-lyrics-locked', String(locked));
                  document.documentElement.dataset.desktopLyricsLocked = String(locked);
                  window.Auralis?.nativeDesktopLyricsLockChanged?.(locked);
                })();
                """);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            Debug.WriteLine($"Desktop lyrics lock-state synchronization failed: {exception.Message}");
        }
    }

    private void OnLanMusicSharingStateChanged(object? sender, LanMusicSharingState state)
    {
        if (_windowClosed)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(() => _ = SendLanMusicSharingStateAsync()),
            DispatcherPriority.Background);
    }

    private async Task SendLanMusicSharingStateAsync()
    {
        if (_windowClosed || PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var state = LanMusicSharing.CurrentState;
        var payload = JsonSerializer.Serialize(new
        {
            enabled = state.Enabled,
            running = state.IsRunning,
            status = state.IsRunning ? "running" : state.Enabled ? "error" : "stopped",
            port = state.Port,
            baseUrls = state.BaseUrls,
            pairingUrls = state.PairingUrls,
            pairingExpiresAt = state.PairingExpiresAt,
            trackCount = state.TrackCount,
            activeSessionCount = state.ActiveSessionCount,
            errorCode = state.ErrorCode,
            errorMessage = state.ErrorMessage
        }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setLanMusicSharingState({payload})");
    }

    private async Task ApplyLanMusicSharingOptionsAsync(JsonElement root)
    {
        var enabled = root.TryGetProperty("enabled", out var enabledProperty)
            ? enabledProperty.ValueKind == JsonValueKind.True
            : _lanSharingSettings.Enabled;
        var port = root.TryGetProperty("port", out var portProperty) && portProperty.TryGetInt32(out var requestedPort)
            ? requestedPort
            : _lanSharingSettings.Port;
        _lanSharingSettings = LanSharingSettingsStore.Normalize(new LanSharingSettings(
            LanSharingSettingsStore.CurrentSchemaVersion,
            enabled,
            port));

        try
        {
            await _lanSharingSettingsStore.SaveAsync(_lanSharingSettings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Log(AppLogLevel.Warning, "lan.player", "settings.save-failed", exception: exception);
            await ExecuteScriptAsync("window.Auralis?.showToast('局域网设置暂时无法保存；本次会话仍会应用')");
        }

        await LanMusicSharing.ApplySettingsAsync(_lanSharingSettings);
        await SendLanMusicSharingStateAsync();
    }

    private bool _copyLanUrlInProgress;

    private async Task CopyLanMusicSharingUrlAsync()
    {
        if (_copyLanUrlInProgress) return;
        var url = LanMusicSharing.CurrentState.PairingUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('请先启用局域网播放器')");
            return;
        }

        _copyLanUrlInProgress = true;
        try
        {
            var copied = await ClipboardWriteRetry.TryAsync(async () =>
                await Dispatcher.InvokeAsync(() =>
                {
                    // Re-read after every retry: a browser may have consumed the previous link.
                    var currentUrl = LanMusicSharing.CurrentState.PairingUrls.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(currentUrl)) throw new InvalidOperationException();
                    System.Windows.Clipboard.SetDataObject(currentUrl, copy: true);
                }));
            if (copied)
                await ExecuteScriptAsync("window.Auralis?.showToast('访问链接已复制；请只发送给同一可信网络中的设备')");
            else
            {
                AppLogger.Log(AppLogLevel.Warning, "lan.player", "clipboard.busy");
                await SendLanMusicSharingStateAsync();
                await ExecuteScriptAsync("window.Auralis?.showLanPairingLink(); window.Auralis?.showToast('系统剪贴板暂不可用，已展开完整配对链接，可手动选择复制')");
            }
        }
        catch (InvalidOperationException)
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('配对链接已失效，请确认局域网播放器仍在运行')");
        }
        finally { _copyLanUrlInProgress = false; }
    }

    private async Task OpenLanMusicSharingUrlAsync()
    {
        var state = LanMusicSharing.CurrentState;
        var url = state.PairingUrls
            .Zip(state.BaseUrls, (pairing, baseUrl) => new { pairing, baseUrl })
            .FirstOrDefault(item => item.baseUrl.Contains("127.0.0.1", StringComparison.Ordinal))
            ?.pairing ?? state.PairingUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('请先启用局域网播放器')");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            AppLogger.Log(AppLogLevel.Warning, "lan.player", "browser.open-failed", exception: exception);
            await ExecuteScriptAsync("window.Auralis?.showToast('无法打开默认浏览器；可复制访问链接后手动打开')");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("action", out var actionProperty))
            {
                return;
            }

            switch (actionProperty.GetString())
            {
                case "windowDrag":
                    BeginWindowDrag();
                    break;
                case "windowToggleMaximize":
                    ToggleMaximize();
                    break;
                case "windowToggleFullscreen":
                    ToggleFullscreen();
                    break;
                case "windowToggleTopmost":
                    _userTopmost = !_userTopmost;
                    Topmost = _userTopmost || _isFullscreen;
                    break;
                case "windowMinimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "windowClose":
                    Close();
                    break;
                case "setUiLanguage":
                    if (root.TryGetProperty("preference", out var languageProperty) &&
                        UiLanguagePreference.IsSupported(languageProperty.GetString()))
                    {
                        _uiLanguagePreference = languageProperty.GetString()!;
                        SaveNativeWindowSettings();
                        ApplyNativeLocalizedText();
                        await SendUiLanguageStateAsync();
                    }
                    break;
                case "openApplicationLogs":
                    await OpenApplicationLogsAsync();
                    break;
                case "setCloseToTray":
                    if (root.TryGetProperty("enabled", out var closeToTrayProperty))
                    {
                        _closeToTrayEnabled = closeToTrayProperty.ValueKind == JsonValueKind.True;
                        _trayEnabled = _closeToTrayEnabled;
                        _trayIcon.Visible = _trayEnabled || _closeToTrayEnabled;
                        SaveNativeWindowSettings();
                    }
                    break;
                case "setTrayEnabled":
                    if (root.TryGetProperty("enabled", out var trayProperty))
                    {
                        _trayEnabled = trayProperty.ValueKind == JsonValueKind.True;
                        _closeToTrayEnabled = _trayEnabled;
                        _trayIcon.Visible = _trayEnabled || _closeToTrayEnabled;
                        SaveNativeWindowSettings();
                    }
                    break;
                case "setTrayMinimizeTimer":
                    var timerMinutes = root.TryGetProperty("minutes", out var timerMinutesProperty) &&
                                       timerMinutesProperty.TryGetInt32(out var requestedMinutes)
                        ? Math.Clamp(requestedMinutes, 0, 24 * 60)
                        : 0;
                    SetTrayMinimizeTimer(timerMinutes);
                    break;
                case "requestAudioDevices":
                    await SendAudioDevicesAsync(includeEndpointDiagnostics: true);
                    break;
                case "setAudioOutputSettings":
                    var audioSettings = root.TryGetProperty("settings", out var audioSettingsProperty)
                        ? audioSettingsProperty.Deserialize<AudioOutputSettings>(WebJsonOptions)
                        : null;
                    if (audioSettings is not null)
                    {
                        try
                        {
                            _mediaPlayer.ApplyAudioOutputSettings(audioSettings);
                            SynchronizeMusicVideoPlaybackProfiles();
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException)
                        {
                            AppLogger.Log(
                                AppLogLevel.Warning,
                                "audio.output",
                                "settings.apply-failed",
                                exception: exception);
                            var fallback = JsonSerializer.Serialize(NativeText.Get(
                                CurrentLanguageState.ResolvedLanguage,
                                "audio.output.fallback"));
                            await ExecuteScriptAsync($"window.Auralis?.showToast({fallback})");
                        }
                        var includeEndpointDiagnostics = root.TryGetProperty("includeDiagnostics", out var diagnosticsProperty) &&
                                                         diagnosticsProperty.ValueKind == JsonValueKind.True;
                        await SendAudioDevicesAsync(includeEndpointDiagnostics);
                    }
                    break;
                case "setStartupEnabled":
                    if (root.TryGetProperty("enabled", out var startupProperty))
                    {
                        await SetStartupEnabledAsync(startupProperty.ValueKind == JsonValueKind.True);
                    }
                    break;
                case "setDesktopLyricsOptions":
                    var desktopOptions = root.TryGetProperty("options", out var optionsProperty)
                        ? optionsProperty.Deserialize<DesktopLyricsOptions>(WebJsonOptions)
                        : null;
                    if (desktopOptions is not null)
                    {
                        _desktopLyricsWindow.ApplyOptions(desktopOptions);
                        _desktopLyricsWindow.SetTrack(_currentTrackInfo);
                        _desktopLyricsWindow.SetLyrics(_currentLyrics);
                        await ExecuteScriptAsync($"document.documentElement.dataset.desktopLyricsVisible='{(_desktopLyricsWindow.IsVisible ? "true" : "false")}'");
                    }
                    break;
                case "setTaskbarMediaOptions":
                    var taskbarOptions = root.Deserialize<TaskbarMediaOptions>(WebJsonOptions);
                    if (taskbarOptions is not null)
                    {
                        _taskbarMediaExperience.ApplyOptions(taskbarOptions);
                    }
                    break;
                case "requestLanMusicSharingState":
                    await SendLanMusicSharingStateAsync();
                    break;
                case "setLanMusicSharingOptions":
                    await ApplyLanMusicSharingOptionsAsync(root);
                    break;
                case "regenerateLanMusicSharingAccess":
                    LanMusicSharing.RegeneratePairingLink();
                    await SendLanMusicSharingStateAsync();
                    break;
                case "revokeLanMusicSharingSessions":
                    LanMusicSharing.RevokeAllSessions();
                    await SendLanMusicSharingStateAsync();
                    break;
                case "copyLanMusicSharingUrl":
                    await CopyLanMusicSharingUrlAsync();
                    break;
                case "openLanMusicSharingUrl":
                    await OpenLanMusicSharingUrlAsync();
                    break;
                case "pickFiles":
                    await PickFilesAsync();
                    break;
                case "pickFolder":
                    await PickFolderAsync();
                    break;
                case "requestMusicFolders":
                    await SendMusicFoldersAsync();
                    break;
                case "removeMusicFolder":
                    if (root.TryGetProperty("id", out var removeFolderIdProperty))
                    {
                        await RemoveMusicFolderAsync(removeFolderIdProperty.GetString());
                    }
                    break;
                case "rescanMusicFolder":
                    await RescanMusicFolderAsync(
                        root.TryGetProperty("id", out var rescanFolderIdProperty)
                            ? rescanFolderIdProperty.GetString()
                            : null);
                    break;
                case "pickArtistImage":
                    if (root.TryGetProperty("artist", out var artistProperty))
                    {
                        await PickArtistImageAsync(artistProperty.GetString());
                    }
                    break;
                case "pickWindowBackground":
                    await PickWindowBackgroundAsync();
                    break;
                case "requestLyrics":
                    if (root.TryGetProperty("id", out var lyricsIdProperty))
                    {
                        var allowOnline = root.TryGetProperty("allowOnline", out var allowOnlineProperty) &&
                            allowOnlineProperty.ValueKind == JsonValueKind.True;
                        _lyricsPreference = root.TryGetProperty("preference", out var preferenceProperty)
                            ? preferenceProperty.GetString() ?? "localFirst"
                            : "localFirst";
                        _lyricsSourceSelection = root.TryGetProperty("sourceSelection", out var sourceSelectionProperty)
                            ? sourceSelectionProperty.GetString() ?? "auto"
                            : "auto";
                        await RequestLyricsAsync(
                            lyricsIdProperty.GetString(),
                            allowOnline,
                            _lyricsPreference,
                            _lyricsSourceSelection);
                    }
                    break;
                case "pickLyricsFile":
                    if (root.TryGetProperty("id", out var pickLyricsIdProperty))
                    {
                        await PickLyricsFileAsync(pickLyricsIdProperty.GetString());
                    }
                    break;
                case "resetLyricsOverride":
                    if (root.TryGetProperty("id", out var resetLyricsIdProperty))
                    {
                        await ResetLyricsOverrideAsync(resetLyricsIdProperty.GetString());
                    }
                    break;
                case "clearLyricsCache":
                    if (root.TryGetProperty("id", out var clearLyricsIdProperty))
                    {
                        await ClearLyricsCacheAsync(clearLyricsIdProperty.GetString());
                    }
                    break;
                case "requestLyricsCacheIndex":
                    await SendLyricsCacheIndexAsync();
                    break;
                case "removeTrack":
                    if (root.TryGetProperty("id", out var idProperty))
                    {
                        await RemoveTrackAsync(idProperty.GetString());
                    }
                    break;
                case "playTrack":
                    if (root.TryGetProperty("id", out var playIdProperty))
                    {
                        await PlayNativeTrackAsync(playIdProperty.GetString(), true, 0);
                    }
                    break;
                case "loadTrack":
                    if (root.TryGetProperty("id", out var loadIdProperty))
                    {
                        var autoplay = root.TryGetProperty("autoplay", out var autoplayProperty) && autoplayProperty.ValueKind == JsonValueKind.True;
                        var position = root.TryGetProperty("seconds", out var positionProperty) && positionProperty.TryGetDouble(out var restoredSeconds)
                            ? restoredSeconds
                            : 0;
                        await PlayNativeTrackAsync(loadIdProperty.GetString(), autoplay, position);
                    }
                    break;
                case "platformSearchTracks":
                    if (root.TryGetProperty("requestId", out var searchRequestIdProperty) &&
                        searchRequestIdProperty.TryGetInt32(out var searchRequestId) &&
                        root.TryGetProperty("query", out var searchQueryProperty))
                    {
                        var providerId = JsonText(root, "providerId") ?? string.Empty;
                        var pageSize = root.TryGetProperty("pageSize", out var searchPageSizeProperty) &&
                                       searchPageSizeProperty.TryGetInt32(out var requestedPageSize)
                            ? requestedPageSize
                            : 30;
                        var pageHandle = root.TryGetProperty("pageHandle", out var searchPageHandleProperty) &&
                                         searchPageHandleProperty.ValueKind == JsonValueKind.String
                            ? searchPageHandleProperty.GetString()
                            : null;
                        await SearchPlatformTracksAsync(
                            searchRequestId,
                            searchQueryProperty.GetString(),
                            providerId,
                            pageSize,
                            pageHandle);
                    }
                    break;
                case "cancelPlatformSearch":
                    CancelAndDispose(ref _platformSearchCancellation);
                    break;
                case "playPlatformResult":
                    if (root.TryGetProperty("handle", out var platformHandleProperty))
                    {
                        await PlayPlatformTrackAsync(platformHandleProperty.GetString());
                    }
                    break;
                case "requestPlatformExtras":
                    await SendPlatformExtrasAsync(root);
                    break;
                case "setEmbeddedVideo":
                    await SetEmbeddedVideoAsync(root);
                    break;
                case "setEmbeddedVideoBounds":
                    SetEmbeddedVideoBounds(root);
                    break;
                case "cancelPlatformExtras":
                    CancelPlatformExtras(JsonText(root, "kind"));
                    break;
                case "requestSavedPlaylists":
                case "createSavedPlaylist":
                case "addToSavedPlaylist":
                case "removeFromSavedPlaylist":
                    await HandleSavedPlaylistsAsync(root);
                    break;
                case "hydrateSavedTrack":
                    await HydrateSavedTrackAsync(root);
                    break;
                case "openPlatformMusicVideo":
                    if (root.TryGetProperty("handle", out var videoHandleProperty))
                    {
                        await OpenPlatformMusicVideoAsync(videoHandleProperty.GetString());
                    }
                    break;
                case "getPlatformConfiguration":
                    await SendPlatformConfigurationAfterStartupAsync();
                    break;
                case "manageOnlineAccount":
                case "requestOnlineCollection":
                    await HandleOnlineCollectionAsync(actionProperty.GetString()!, root);
                    break;
                case "restartForPlugins":
                    await RestartForPluginsAsync();
                    break;
                case "managePlaybackComponents":
                    await HandlePlaybackComponentsAsync(root, e);
                    break;
                case "manageMediaTransportComponents":
                    await HandleMediaTransportComponentsAsync(root, e);
                    break;
                case "requestPluginInventory":
                    if (root.TryGetProperty("requestId", out var pluginRequestProperty) &&
                        pluginRequestProperty.TryGetInt64(out var pluginRequestId) && pluginRequestId > 0 && pluginRequestId <= 9007199254740991L)
                        await SendPluginInventoryAsync(pluginRequestId);
                    break;
                case "pickPluginPackage":
                case "dropPluginPackages":
                case "confirmPluginImport":
                case "cancelPluginImport":
                case "setPluginEnabled":
                    await HandlePluginManagementAsync(actionProperty.GetString()!, root, e);
                    break;
                case "openPluginFolder":
                    await OpenPluginFolderAsync();
                    break;
                case "saveOnlineProviderSetting":
                    await SaveOnlineProviderSettingAsync(root);
                    break;
                case "openDefaultAppsSettings":
                    await OpenDefaultAppsSettingsAsync();
                    break;
                case "resumePlayback":
                    _mediaPlayer.Play();
                    _playbackTimer.Start();
                    NotifyPlaybackState(true);
                    break;
                case "restartPlayback":
                    if (_currentTrackId is not null && JsonText(root, "id") == _currentTrackId)
                    {
                        _mediaPlayer.Restart();
                        _playbackTimer.Start();
                    }
                    break;
                case "prefetchPlatformTrack":
                    await PrefetchNextAsync(JsonText(root, "currentId"), JsonText(root, "handle"));
                    break;
                case "pausePlayback":
                    _mediaPlayer.Pause();
                    _playbackTimer.Stop();
                    NotifyPlaybackState(false);
                    break;
                case "seekPlayback":
                    if (root.TryGetProperty("seconds", out var secondsProperty) && secondsProperty.TryGetDouble(out var seconds))
                    {
                        _mediaPlayer.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
                        NotifyPlaybackState();
                    }
                    break;
                case "setVolume":
                    if (root.TryGetProperty("value", out var volumeProperty) && volumeProperty.TryGetDouble(out var volume))
                    {
                        _mediaPlayer.Volume = Math.Clamp(volume, 0, 1);
                        SynchronizeMusicVideoVolumes();
                    }
                    break;
                case "setPlaybackRate":
                    if (root.TryGetProperty("value", out var rateProperty) && rateProperty.TryGetDouble(out var rate))
                    {
                        _mediaPlayer.SpeedRatio = Math.Clamp(rate, 0.5, 2);
                        SynchronizeMusicVideoPlaybackRates();
                        NotifyPlaybackState();
                    }
                    break;
                case "setMediaKeys":
                    _mediaKeysEnabled = !root.TryGetProperty("enabled", out var enabledProperty) || enabledProperty.ValueKind == JsonValueKind.True;
                    break;
                case "setWindowMaterial":
                    _micaRequested = JsonText(root, "material") == "mica";
                    ApplyNativeTheme(_nativeDarkTheme ? "dark" : "light");
                    break;
                case "themeChanged":
                    if (root.TryGetProperty("theme", out var themeProperty))
                    {
                        ApplyNativeTheme(themeProperty.GetString());
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the UI surface.
        }
        catch (InvalidOperationException)
        {
            // Window operations can race with a closing window.
        }
    }

    private async Task PickFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加本地音乐",
            Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddPathsAsync(dialog.FileNames);
        }
    }

    private async Task PickFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择一个或多个音乐文件夹",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selectedFolders = MusicFolderStore.Normalize(dialog.FolderNames)
            .Where(Directory.Exists)
            .ToArray();
        if (selectedFolders.Length == 0)
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('没有选择可用的音乐文件夹')");
            return;
        }

        foreach (var folder in selectedFolders)
        {
            _musicFolders.Add(folder);
        }

        await _musicFolderStore.SaveAsync(_musicFolders);
        RebuildMusicFolderWatchers();
        await SendMusicFoldersAsync();
        await ReconcileMusicFoldersAsync(selectedFolders, userInitiated: true);
    }

    private async Task RemoveMusicFolderAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var folder = _musicFolders.FirstOrDefault(path => CreateId(path) == id);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _musicFolders.Remove(folder);
        await _musicFolderStore.SaveAsync(_musicFolders);
        RebuildMusicFolderWatchers();
        await SendMusicFoldersAsync();
        await ExecuteScriptAsync("window.Auralis?.showToast('已停止监听；已经导入的歌曲仍保留在本地曲库中')");
    }

    private async Task RescanMusicFolderAsync(string? id)
    {
        var folders = string.IsNullOrWhiteSpace(id)
            ? _musicFolders.ToArray()
            : _musicFolders.Where(path => CreateId(path) == id).ToArray();

        if (folders.Length == 0)
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('还没有设置要监听的音乐文件夹')");
            return;
        }

        await ReconcileMusicFoldersAsync(folders, userInitiated: true);
    }

    private void RebuildMusicFolderWatchers()
    {
        DisposeMusicFolderWatchers();
        if (_windowClosed)
        {
            return;
        }

        foreach (var root in MusicFolderStore.CollapseWatchRoots(_musicFolders.Where(Directory.Exists)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    InternalBufferSize = 32 * 1024
                };
                watcher.Created += (_, args) => OnMusicFolderCreated(root, args.FullPath);
                watcher.Changed += (_, args) => OnMusicFolderChanged(args.FullPath);
                watcher.Deleted += (_, args) => OnMusicFolderDeleted(root, args.FullPath);
                watcher.Renamed += (_, args) => OnMusicFolderRenamed(root, args.OldFullPath, args.FullPath);
                watcher.Error += (_, _) => QueueMusicFolderRescan(root);
                watcher.EnableRaisingEvents = true;
                _musicFolderWatchers[root] = watcher;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Debug.WriteLine($"Music folder watcher failed for {root}: {exception.Message}");
            }
        }
    }

    private void DisposeMusicFolderWatchers()
    {
        foreach (var watcher in _musicFolderWatchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _musicFolderWatchers.Clear();
    }

    private void OnMusicFolderCreated(string root, string path)
    {
        if (IsSupportedAudioPath(path))
        {
            QueueMusicFolderPathChange(path, exists: true, refreshMetadata: false);
        }
        else if (Directory.Exists(path))
        {
            QueueMusicFolderRescan(root);
        }
    }

    private void OnMusicFolderChanged(string path)
    {
        if (IsSupportedAudioPath(path))
        {
            QueueMusicFolderPathChange(path, exists: true, refreshMetadata: true);
        }
    }

    private void OnMusicFolderDeleted(string root, string path)
    {
        if (IsSupportedAudioPath(path))
        {
            QueueMusicFolderPathChange(path, exists: false, refreshMetadata: false);
        }
        else
        {
            // FileSystemWatcher does not identify whether a deleted item was a directory.
            // A debounced folder reconciliation safely handles deleted subdirectories.
            QueueMusicFolderRescan(root);
        }
    }

    private void OnMusicFolderRenamed(string root, string oldPath, string newPath)
    {
        var oldIsAudio = IsSupportedAudioPath(oldPath);
        var newIsAudio = IsSupportedAudioPath(newPath);
        if (oldIsAudio)
        {
            QueueMusicFolderPathChange(oldPath, exists: false, refreshMetadata: false);
        }
        if (newIsAudio)
        {
            QueueMusicFolderPathChange(newPath, exists: true, refreshMetadata: false);
        }
        if (!oldIsAudio && !newIsAudio && Directory.Exists(newPath))
        {
            QueueMusicFolderRescan(root);
        }
    }

    private static bool IsSupportedAudioPath(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    private void QueueMusicFolderPathChange(string path, bool exists, bool refreshMetadata)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_musicFolderChangeLock)
        {
            _pendingMusicFolderChanges[fullPath] = exists;
            if (refreshMetadata)
            {
                _pendingMusicFolderRefreshPaths.Add(fullPath);
            }
        }
        RestartMusicFolderRefreshTimer();
    }

    private void QueueMusicFolderRescan(string root)
    {
        lock (_musicFolderChangeLock)
        {
            _pendingMusicFolderRescans.Add(root);
        }
        RestartMusicFolderRefreshTimer();
    }

    private void RestartMusicFolderRefreshTimer()
    {
        if (_windowClosed)
        {
            return;
        }

        lock (_musicFolderChangeLock)
        {
            if (_musicFolderTimerRestartQueued)
            {
                return;
            }
            _musicFolderTimerRestartQueued = true;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            lock (_musicFolderChangeLock)
            {
                _musicFolderTimerRestartQueued = false;
            }
            if (_windowClosed)
            {
                return;
            }
            _musicFolderRefreshTimer.Stop();
            _musicFolderRefreshTimer.Start();
        }, DispatcherPriority.Background);
    }

    private async void OnMusicFolderRefreshTimerTick(object? sender, EventArgs e)
    {
        _musicFolderRefreshTimer.Stop();
        try
        {
            await FlushMusicFolderChangesAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Music folder refresh failed: {exception.Message}");
        }
    }

    private async Task FlushMusicFolderChangesAsync()
    {
        Dictionary<string, bool> changes;
        HashSet<string> refreshPaths;
        HashSet<string> rescanRoots;
        lock (_musicFolderChangeLock)
        {
            changes = new Dictionary<string, bool>(_pendingMusicFolderChanges, StringComparer.OrdinalIgnoreCase);
            refreshPaths = new HashSet<string>(_pendingMusicFolderRefreshPaths, StringComparer.OrdinalIgnoreCase);
            rescanRoots = new HashSet<string>(_pendingMusicFolderRescans, StringComparer.OrdinalIgnoreCase);
            _pendingMusicFolderChanges.Clear();
            _pendingMusicFolderRefreshPaths.Clear();
            _pendingMusicFolderRescans.Clear();
        }

        rescanRoots.RemoveWhere(root => !_musicFolders.Contains(root));
        if (rescanRoots.Count > 0)
        {
            await ReconcileMusicFoldersAsync(rescanRoots, userInitiated: false);
        }

        var libraryChanged = false;
        var updatedTracks = new Dictionary<string, StoredTrack>(StringComparer.OrdinalIgnoreCase);
        var removedTrackIds = new List<string>();
        foreach (var (path, exists) in changes)
        {
            if (!_musicFolders.Any(root => MusicFolderStore.IsPathWithinRoot(path, root)))
            {
                continue;
            }
            if (rescanRoots.Any(root => MusicFolderStore.IsPathWithinRoot(path, root)))
            {
                continue;
            }

            if (exists && File.Exists(path) && IsSupportedAudioPath(path))
            {
                if (!_library.ContainsKey(path))
                {
                    var stored = new StoredTrack(path, DateTime.Now);
                    _library[path] = stored;
                    updatedTracks[path] = stored;
                    libraryChanged = true;
                }
                else if (refreshPaths.Contains(path))
                {
                    updatedTracks[path] = _library[path];
                }
            }
            else if (!exists && _library.Remove(path))
            {
                removedTrackIds.Add(CreateId(path));
                libraryChanged = true;
            }
        }

        if (libraryChanged)
        {
            await PersistLibraryAsync();
        }
        if (updatedTracks.Count > 0)
        {
            await SendLibraryAsync("updateLibraryTracks", updatedTracks.Values);
        }
        if (removedTrackIds.Count > 0)
        {
            await SendRemovedLibraryTracksAsync(removedTrackIds);
        }
        if (libraryChanged || updatedTracks.Count > 0)
        {
            await SendMusicFoldersAsync();
        }
    }

    private async Task ReconcileMusicFoldersAsync(IEnumerable<string> requestedFolders, bool userInitiated)
    {
        var folders = MusicFolderStore.Normalize(requestedFolders).ToArray();
        if (folders.Length == 0 || _windowClosed)
        {
            return;
        }

        await _musicFolderScanGate.WaitAsync();
        try
        {
            if (userInitiated)
            {
                await ExecuteScriptAsync("window.Auralis?.setScanning(true)");
            }

            var availableFolders = folders.Where(Directory.Exists).ToArray();
            var unavailableCount = folders.Length - availableFolders.Length;
            if (availableFolders.Length == 0)
            {
                await SendMusicFoldersAsync();
                if (userInitiated)
                {
                    await ExecuteScriptAsync("window.Auralis?.showToast('所选音乐文件夹当前不可访问，请检查磁盘或网络位置')");
                }
                return;
            }

            var discovered = await Task.Run(() =>
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in availableFolders)
                {
                    foreach (var path in EnumerateAudioFiles(folder).Take(50_000))
                    {
                        paths.Add(Path.GetFullPath(path));
                    }
                }
                return paths;
            });

            if (_windowClosed)
            {
                return;
            }

            // The user may stop listening to a folder while its background scan is running.
            // Re-check the current registry before applying the result so a stale scan cannot
            // mutate library rows after the folder has been removed.
            availableFolders = availableFolders.Where(folder => _musicFolders.Contains(folder)).ToArray();
            if (availableFolders.Length == 0)
            {
                await SendMusicFoldersAsync();
                return;
            }
            discovered.RemoveWhere(path => !availableFolders.Any(folder => MusicFolderStore.IsPathWithinRoot(path, folder)));

            var changed = false;
            var removedTrackIds = new List<string>();
            foreach (var path in _library.Keys
                         .Where(path => availableFolders.Any(folder => MusicFolderStore.IsPathWithinRoot(path, folder)))
                         .Where(path => !discovered.Contains(path))
                         .ToArray())
            {
                _library.Remove(path);
                removedTrackIds.Add(CreateId(path));
                changed = true;
            }

            var addedCount = 0;
            var addedTracks = new List<StoredTrack>();
            foreach (var path in discovered)
            {
                if (_library.ContainsKey(path))
                {
                    continue;
                }
                var stored = new StoredTrack(path, DateTime.Now);
                _library[path] = stored;
                addedTracks.Add(stored);
                addedCount++;
                changed = true;
            }

            if (changed)
            {
                await PersistLibraryAsync();
                if (addedTracks.Count > 0)
                {
                    await SendLibraryAsync("updateLibraryTracks", addedTracks);
                }
                if (removedTrackIds.Count > 0)
                {
                    await SendRemovedLibraryTracksAsync(removedTrackIds);
                }
            }
            await SendMusicFoldersAsync();

            if (userInitiated)
            {
                var message = unavailableCount > 0
                    ? $"扫描完成，新增 {addedCount} 首；{unavailableCount} 个文件夹暂不可访问"
                    : addedCount > 0
                        ? $"已从多个文件夹新增 {addedCount} 首本地音乐"
                        : "所有音乐文件夹都已是最新状态";
                await ExecuteScriptAsync($"window.Auralis?.showToast({JsonSerializer.Serialize(message)})");
            }
        }
        finally
        {
            if (userInitiated && !_windowClosed)
            {
                await ExecuteScriptAsync("window.Auralis?.setScanning(false)");
            }
            _musicFolderScanGate.Release();
        }
    }

    private async Task SendMusicFoldersAsync()
    {
        if (_windowClosed || PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var folders = _musicFolders
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new
            {
                id = CreateId(path),
                path,
                name = new DirectoryInfo(path).Name is { Length: > 0 } name ? name : path,
                available = Directory.Exists(path),
                trackCount = _library.Keys.Count(trackPath => MusicFolderStore.IsPathWithinRoot(trackPath, path))
            })
            .ToArray();
        var payload = JsonSerializer.Serialize(folders, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setMusicFolders({payload})");
    }

    private async Task SendRemovedLibraryTracksAsync(IEnumerable<string> trackIds)
    {
        var ids = trackIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        LanMusicSharing.RemoveTrackIds(ids);
        var payload = JsonSerializer.Serialize(ids);
        await ExecuteScriptAsync($"window.Auralis?.removeLibraryTracks({payload})");
    }

    private async Task PickArtistImageAsync(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"为“{artist}”选择本地头像",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Directory.CreateDirectory(_artistImageDirectory);
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var fileName = $"{CreateId(artist)}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}";
        var destination = Path.Combine(_artistImageDirectory, fileName);
        File.Copy(dialog.FileName, destination, overwrite: true);

        var artistPayload = JsonSerializer.Serialize(artist);
        var urlPayload = JsonSerializer.Serialize($"https://artists.auralis.local/{fileName}");
        await ExecuteScriptAsync($"window.Auralis?.setArtistImage({artistPayload}, {urlPayload})");
    }

    private async Task PickWindowBackgroundAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Auralis 窗口背景",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        Directory.CreateDirectory(_backgroundImageDirectory);
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var fileName = $"window-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}";
        File.Copy(dialog.FileName, Path.Combine(_backgroundImageDirectory, fileName), overwrite: true);
        var url = JsonSerializer.Serialize($"https://backgrounds.auralis.local/{fileName}");
        await ExecuteScriptAsync($"window.Auralis?.setWindowBackground({url})");
    }

    private async Task PickLyricsFileAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_library.Any(pair => CreateId(pair.Key) == id)) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "为当前歌曲选择本地歌词",
            Filter = "歌词文件|*.lrc;*.txt|所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        await _lyricsService.SetOverrideAsync(id, dialog.FileName);
        _lyricsSourceSelection = "local";
        await RequestLyricsAsync(id, false, _lyricsPreference, _lyricsSourceSelection);
        await SendLyricsCacheIndexAsync();
        await ExecuteScriptAsync("window.Auralis?.showToast('已为这首歌切换到指定的本地歌词')");
    }

    private async Task ResetLyricsOverrideAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await _lyricsService.RemoveOverrideAsync(id);
        await RequestLyricsAsync(id, false, _lyricsPreference, _lyricsSourceSelection);
        await SendLyricsCacheIndexAsync();
        await ExecuteScriptAsync("window.Auralis?.showToast('已恢复自动匹配歌词')");
    }

    private async Task ClearLyricsCacheAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await _lyricsService.ClearCacheAsync(id);
        if (id == _currentTrackId)
        {
            await RequestLyricsAsync(id, false, _lyricsPreference, _lyricsSourceSelection);
        }
        await SendLyricsCacheIndexAsync();
        await ExecuteScriptAsync("window.Auralis?.showToast('已清除这首歌的歌词缓存')");
    }

    private async Task SendLyricsCacheIndexAsync()
    {
        var rows = new List<object>();
        foreach (var pair in _library)
        {
            var id = CreateId(pair.Key);
            var info = await _lyricsService.GetCacheInfoAsync(id);
            if (!info.HasCache && !info.HasOverride) continue;
            var track = CreateTrackInfo(pair.Value);
            rows.Add(new
            {
                id,
                track.Title,
                track.Artist,
                info.HasCache,
                info.HasOverride,
                info.CacheSource,
                info.UpdatedAt
            });
        }
        var payload = JsonSerializer.Serialize(rows, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setLyricsCacheIndex({payload})");
    }

    private async Task RequestLyricsAsync(
        string? id,
        bool allowOnline,
        string preference = "localFirst",
        string sourceSelection = "auto")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var match = _library.FirstOrDefault(pair => CreateId(pair.Key) == id);
        if (string.IsNullOrWhiteSpace(match.Key) || !File.Exists(match.Key))
        {
            return;
        }

        _lyricsRequestCancellation?.Cancel();
        _lyricsRequestCancellation?.Dispose();
        _lyricsRequestCancellation = new CancellationTokenSource();
        var cancellationToken = _lyricsRequestCancellation.Token;

        try
        {
            var track = CreateTrackInfo(match.Value);
            var lyrics = await _lyricsService.GetLyricsAsync(
                match.Value,
                track,
                allowOnline,
                preference,
                sourceSelection,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // WebView messages are dispatched through an async event handler, so a lyrics lookup
            // can finish after the user has already switched tracks. Never let that stale result
            // replace the native desktop-lyrics state for the new song.
            if (!string.Equals(_currentTrackId, id, StringComparison.Ordinal) || _currentPlaybackIsOnline)
            {
                return;
            }
            _currentLyrics = lyrics;
            _desktopLyricsWindow.SetTrack(track);
            _desktopLyricsWindow.SetLyrics(lyrics);
            _taskbarMediaExperience.UpdateLyrics(lyrics);
            var duration = _mediaPlayer.Duration.TotalSeconds;
            _desktopLyricsWindow.UpdatePlayback(
                _mediaPlayer.Position.TotalSeconds,
                duration,
                _playbackTimer.IsEnabled);
            var payload = JsonSerializer.Serialize(lyrics, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setLyrics({payload})");
        }
        catch (OperationCanceledException)
        {
            // A newer track requested lyrics before this lookup completed.
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<StoredTrack>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (_library.ContainsKey(fullPath))
            {
                continue;
            }

            var stored = new StoredTrack(fullPath, DateTime.Now);
            _library[fullPath] = stored;
            added.Add(stored);
        }

        if (added.Count == 0)
        {
            await ExecuteScriptAsync("window.Auralis.showToast('没有发现新的音频文件')");
            return;
        }

        await PersistLibraryAsync();
        await SendLibraryAsync("addTracks", added);
        await SendMusicFoldersAsync();
    }

    private async Task RemoveTrackAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var match = _library.FirstOrDefault(pair => CreateId(pair.Key) == id);
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            return;
        }

        _library.Remove(match.Key);
        LanMusicSharing.RemoveTrackIds([id]);
        await PersistLibraryAsync();
        await SendMusicFoldersAsync();
    }

    private async Task PlayNativeTrackAsync(string? id, bool autoplay, double positionSeconds)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var match = _library.FirstOrDefault(pair => CreateId(pair.Key) == id);
        if (string.IsNullOrWhiteSpace(match.Key) || !File.Exists(match.Key))
        {
            return;
        }

        CancelAndDispose(ref _platformPlaybackCancellation);
        StopEmbeddedVideo(false);
        _mediaPlayer.Close();
        // Commit the new local-track identity before the first await. A requestLyrics WebView
        // message may be handled concurrently; clearing lyrics after awaiting ReleaseAsync used
        // to erase an already-resolved local LRC from the desktop lyric window.
        _currentTrackId = id;
        _currentPlaybackIsOnline = false;
        _currentTrackInfo = CreateTrackInfo(match.Value);
        _currentLyrics = null;
        _taskbarMediaExperience.UpdateLyrics(null);
        PublishSystemMediaMetadata(_currentTrackInfo);
        _desktopLyricsWindow.SetTrack(_currentTrackInfo);
        _desktopLyricsWindow.SetLyrics(null);
        ++_prefetchVersion;
        await DrainPrefetchAsync(null);
        await _onlinePlaybackSource.ReleaseAsync();
        if (_currentTrackId != id || _currentPlaybackIsOnline) return;
        var knownDuration = _currentTrackInfo?.DurationSeconds ?? 0;
        var startPosition = knownDuration > 0 ? Math.Min(positionSeconds, knownDuration) : positionSeconds;
        _currentAudioSource = new Uri(match.Key, UriKind.Absolute);
        _mediaPlayer.Open(_currentAudioSource, autoplay, startPosition);
    }

    private async Task SearchPlatformTracksAsync(
        int requestId,
        string? query,
        string providerId,
        int pageSize,
        string? pageHandle)
    {
        var cancellation = ReplaceCancellation(ref _platformSearchCancellation);
        try
        {
            var result = await OnlinePlatforms.SearchAsync(
                providerId,
                query ?? string.Empty,
                pageSize,
                pageHandle,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var payload = JsonSerializer.Serialize(new
            {
                requestId,
                query = query ?? string.Empty,
                providerId,
                pageHandle,
                items = result.IsSuccess ? result.Value.Items : Array.Empty<OnlineTrackView>(),
                nextPageHandle = result.IsSuccess ? result.Value.NextPageHandle : null,
                totalCount = result.IsSuccess ? result.Value.TotalCount : null,
                error = result.IsSuccess ? null : ToPlatformError(result.Error)
            }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPlatformSearchResult({payload})");
        }
        catch (OperationCanceledException)
        {
            // A newer query superseded this response.
        }
        finally
        {
            ClearCancellation(ref _platformSearchCancellation, cancellation);
        }
    }

    private async Task PlayPlatformTrackAsync(string? handle)
    {
        await RestoreSavedHandleAsync(handle);
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 128 ||
            !OnlinePlatforms.TryGetTrack(handle, out var track) || track is null)
        {
            await SendPlatformPlaybackResultAsync(
                handle,
                false,
                null,
                new PlatformError(PlatformErrorCode.NotFound, "在线歌曲结果已过期，请重新搜索。"));
            return;
        }

        var cancellation = ReplaceCancellation(ref _platformPlaybackCancellation);
        Auralis.MediaTransport.IMediaTransportResource? preparedSource = null;
        var sourceActivated = false;
        try
        {
            ++_prefetchVersion;
            var prefetched = await DrainPrefetchAsync(handle, cancellation.Token);
            var leaseResult = prefetched is not null
                ? PlatformResult<PlatformStreamLease>.Success(prefetched.Lease)
                : await OnlinePlatforms.AcquireStreamAsync(handle, cancellation.Token);
            if (!leaseResult.IsSuccess)
            {
                await SendPlatformPlaybackResultAsync(handle, false, null, leaseResult.Error);
                return;
            }

            preparedSource = prefetched?.Source ?? await _onlinePlaybackSource.PrepareAsync(
                leaseResult.Value,
                $"{track.ProviderId}:{track.Id}:{leaseResult.Value.Quality.Id}",
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            StopEmbeddedVideo(false);
            _mediaPlayer.Close();
            // Retire the old identity before yielding: a late video-return message must
            // never restore the previous song during activation of the next source.
            _currentTrackId = handle;
            _currentAudioSource = null;
            await _onlinePlaybackSource.ActivateAsync(preparedSource);
            sourceActivated = true;
            cancellation.Token.ThrowIfCancellationRequested();
            _currentTrackId = handle;
            _currentPlaybackIsOnline = true;
            _currentTrackInfo = new TrackInfo(
                track.Handle,
                track.Title,
                track.Artist,
                track.Album,
                $"{track.SourceName}在线歌曲",
                ".online",
                track.CoverUrl,
                0,
                track.DurationSeconds,
                DateTime.Now);
            _currentLyrics = null;
            _taskbarMediaExperience.UpdateLyrics(null);
            PublishSystemMediaMetadata(track);
            _desktopLyricsWindow.SetTrack(_currentTrackInfo);
            _desktopLyricsWindow.SetLyrics(null);
            _currentAudioSource = preparedSource.Source;
            await SendPlatformPlaybackResultAsync(handle, true, track, null, leaseResult.Value);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_currentTrackId != handle) return;
            // The full-screen projection must commit before the new audio/video can sound.
            _mediaPlayer.Open(preparedSource.Source);
            await LoadPlatformLyricsAsync(handle, track, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A local track or a newer online result replaced this pending playback request.
        }
        catch (OnlinePlaybackException exception)
        {
            await SendPlatformPlaybackResultAsync(
                handle,
                false,
                null,
                new PlatformError(PlatformErrorCode.ContentUnavailable, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await SendPlatformPlaybackResultAsync(
                handle,
                false,
                null,
                new PlatformError(PlatformErrorCode.Unknown, "无法启动在线歌曲播放。"));
        }
        finally
        {
            if (preparedSource is not null && !sourceActivated)
            {
                await _onlinePlaybackSource.DiscardPreparedAsync(preparedSource);
            }

            ClearCancellation(ref _platformPlaybackCancellation, cancellation);
        }
    }

    private async Task LoadPlatformLyricsAsync(
        string handle,
        OnlineTrackView track,
        CancellationToken cancellationToken)
    {
        var result = await OnlinePlatforms.GetLyricsAsync(handle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_currentPlaybackIsOnline || !string.Equals(_currentTrackId, handle, StringComparison.Ordinal))
        {
            return;
        }

        LyricsResponse response;
        if (result.IsSuccess)
        {
            response = new LyricsResponse(
                handle,
                result.Value.SourceName,
                result.Value.IsSynchronized,
                false,
                result.Value.Lines.Select(static line => new LyricLine(line.Start?.TotalSeconds, line.Text)).ToArray(),
                result.Value.Lines.Count == 0 ? $"{result.Value.SourceName}没有返回这首歌的歌词" : "",
                "online",
                new LyricsSourceAvailability(false, null, false, true, true, result.Value.SourceName));
        }
        else
        {
            var sourceName = string.IsNullOrWhiteSpace(track.SourceName)
                ? "在线来源"
                : track.SourceName;
            response = new LyricsResponse(
                handle,
                sourceName,
                false,
                false,
                [],
                result.Error?.Message ?? $"{sourceName}没有返回这首歌的歌词",
                "online",
                new LyricsSourceAvailability(false, null, false, false, true, sourceName));
        }

        _currentLyrics = response;
        _desktopLyricsWindow.SetTrack(_currentTrackInfo);
        _desktopLyricsWindow.SetLyrics(response);
        _taskbarMediaExperience.UpdateLyrics(response);
        var payload = JsonSerializer.Serialize(response, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setLyrics({payload})");
    }

    private async Task OpenPlatformMusicVideoAsync(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 128 ||
            !OnlinePlatforms.TryGetTrack(handle, out var track) || track is null || !track.HasMusicVideo)
        {
            await SendPlatformVideoResultAsync(
                handle,
                false,
                new PlatformError(PlatformErrorCode.NotFound, "这个搜索结果没有可播放的 MV。"));
            return;
        }

        var cancellation = ReplaceCancellation(ref _platformVideoCancellation);
        try
        {
            var result = await OnlinePlatforms.AcquireVideoAsync(handle, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
            {
                await SendPlatformVideoResultAsync(handle, false, result.Error);
                return;
            }

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                _playbackTimer.Stop();
                NotifyPlaybackState(false);
            }

            var window = new MusicVideoWindow(
                result.Value,
                track.Title,
                track.Artist,
                _nativeDarkTheme,
                _mediaPlayer.CapturePlaybackProfile())
            {
                Owner = this
            };
            _musicVideoWindows.Add(window);
            window.VolumeChanged += MusicVideoWindow_VolumeChanged;
            window.Closed += (_, _) =>
            {
                window.VolumeChanged -= MusicVideoWindow_VolumeChanged;
                _musicVideoWindows.Remove(window);
            };
            window.Show();
            window.Activate();
            await SendPlatformVideoResultAsync(handle, true, null);
        }
        catch (OperationCanceledException)
        {
            // A newer MV request superseded this one.
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            await SendPlatformVideoResultAsync(
                handle,
                false,
                new PlatformError(PlatformErrorCode.Unknown, "无法创建 MV 播放窗口。"));
        }
        finally
        {
            ClearCancellation(ref _platformVideoCancellation, cancellation);
        }
    }

    private async Task SendPlatformVideoResultAsync(
        string? handle,
        bool success,
        PlatformError? error)
    {
        var payload = JsonSerializer.Serialize(new
        {
            handle,
            success,
            error = ToPlatformError(error)
        }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setPlatformVideoResult({payload})");
    }

    private void SynchronizeMusicVideoPlaybackProfiles(MusicVideoWindow? source = null)
    {
        var profile = _mediaPlayer.CapturePlaybackProfile();
        foreach (var window in _musicVideoWindows.ToArray())
        {
            if (!ReferenceEquals(window, source))
            {
                window.ApplyPlaybackProfile(profile);
            }
        }
    }

    private void SynchronizeMusicVideoVolumes(MusicVideoWindow? source = null)
    {
        foreach (var window in _musicVideoWindows.ToArray())
        {
            if (!ReferenceEquals(window, source))
            {
                window.ApplyPlaybackVolume(_mediaPlayer.Volume);
            }
        }
    }

    private void SynchronizeMusicVideoPlaybackRates()
    {
        foreach (var window in _musicVideoWindows.ToArray())
        {
            window.ApplyPlaybackRate(_mediaPlayer.SpeedRatio);
        }
    }

    private void MusicVideoWindow_VolumeChanged(object? sender, MusicVideoVolumeChangedEventArgs e)
    {
        _mediaPlayer.Volume = e.Volume;
        SynchronizeMusicVideoVolumes(sender as MusicVideoWindow);
        _ = SendMusicVideoVolumeToWebAsync(e.Volume);
    }

    private async Task SendMusicVideoVolumeToWebAsync(double volume)
    {
        var payload = JsonSerializer.Serialize(volume, WebJsonOptions);
        try
        {
            await ExecuteScriptAsync($"window.Auralis?.setPlaybackVolume({payload})");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            AppLogger.Log(
                AppLogLevel.Debug,
                "mv.audio",
                "volume.web-sync-skipped",
                exception: exception);
        }
    }

    private async Task SendPlatformPlaybackResultAsync(
        string? handle,
        bool success,
        OnlineTrackView? item,
        PlatformError? error,
        PlatformStreamLease? streamLease = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            handle,
            success,
            item,
            quality = streamLease is null
                ? null
                : new
                {
                    id = streamLease.Quality.Id,
                    displayName = streamLease.Quality.DisplayName,
                    bitrateKbps = streamLease.Quality.BitrateKbps,
                    codec = streamLease.Quality.Codec,
                    isLossless = streamLease.Quality.IsLossless,
                    requestedQualityId = streamLease.RequestedQualityId,
                    requestedQualityDisplayName = streamLease.RequestedQualityDisplayName,
                    usedFallback = streamLease.UsedQualityFallback
                },
            error = ToPlatformError(error)
        }, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setPlatformPlaybackResult({payload})");
    }

    private Task SendPlatformConfigurationAsync()
    {
        lock (_platformConfigurationSyncLock)
        {
            if (_platformConfigurationSyncTask is { IsCompleted: false })
            {
                return _platformConfigurationSyncTask;
            }

            _platformConfigurationSyncTask = SendPlatformConfigurationCoreAsync();
            return _platformConfigurationSyncTask;
        }
    }

    private async Task SendPlatformConfigurationCoreAsync()
    {
        var backend = ((App)System.Windows.Application.Current).PlatformBackend;
        try
        {
            var snapshot = await backend.DiscoverAsync();
            var available = new List<(string PluginId, object View)>();
            foreach (var registration in snapshot.Providers)
            {
                if (backend.IsPluginDisabled(registration.PluginId)) continue;
                var provider = registration.Provider;
                var settings = await backend.ReadSettingsAsync(registration);
                var ready = settings.All(s => !s.Required || !string.IsNullOrEmpty(s.Value));
                var collectionState = await OnlinePlatforms.ReadProviderCollectionsAsync(provider, ready, CancellationToken.None);
                object? authentication = collectionState.Authentication is { } auth
                    ? new { status = auth.Status.ToString().ToLowerInvariant(), accountDisplayName = auth.AccountDisplayName, accountId = auth.AccountId } : null;
                var playlists = collectionState.Playlists;
                var error = ToPlatformError(collectionState.Error);
                // A disable may finish while an account or collection request is in flight.
                if (backend.IsPluginDisabled(registration.PluginId)) continue;
                available.Add((registration.PluginId, new { id = provider.Id, name = provider.DisplayName,
                    capabilities = provider.Capabilities.Select(c => c.ToString()).ToArray(),
                    authentication, playlists, error, settings, configured = ready }));
            }
            var providers = available.Where(p => !backend.IsPluginDisabled(p.PluginId)).Select(p => p.View).ToArray();
            var payload = JsonSerializer.Serialize(new { providers }, WebJsonOptions);
            await ExecuteScriptAsync($"window.Auralis?.setPlatformConfiguration({payload})");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ExecuteScriptAsync("window.Auralis?.setPlatformConfiguration({providers:[],error:{message:'无法读取插件平台，请在插件设置中检查。'}})");
        }
    }

    private static object? ToPlatformError(PlatformError? error) => error is null
        ? null
        : new
        {
            code = error.Code.ToString(),
            error.Message,
            error.IsTransient,
            retryAfterSeconds = error.RetryAfter?.TotalSeconds
        };

    private static CancellationTokenSource ReplaceCancellation(ref CancellationTokenSource? target)
    {
        CancelAndDispose(ref target);
        target = new CancellationTokenSource();
        return target;
    }

    private static void ClearCancellation(
        ref CancellationTokenSource? target,
        CancellationTokenSource completed)
    {
        if (!ReferenceEquals(target, completed))
        {
            return;
        }

        target = null;
        completed.Dispose();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? target)
    {
        var cancellation = target;
        target = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_mediaKeysEnabled || PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var script = e.Key switch
        {
            Key.MediaPlayPause => "window.Auralis?.mediaCommand('playPause')",
            Key.MediaNextTrack => "window.Auralis?.mediaCommand('next')",
            Key.MediaPreviousTrack => "window.Auralis?.mediaCommand('previous')",
            _ => null
        };
        if (script is null)
        {
            return;
        }

        e.Handled = true;
        await ExecuteScriptAsync(script);
    }

    private void NotifyPlaybackState(bool? isPlaying = null)
    {
        var duration = _mediaPlayer.Duration.TotalSeconds;
        var playing = isPlaying ?? _playbackTimer.IsEnabled;
        _systemMediaSession.UpdatePlayback(
            playing,
            _mediaPlayer.Position,
            TimeSpan.FromSeconds(Math.Max(0, duration)),
            _mediaPlayer.SpeedRatio);
        _desktopLyricsWindow.UpdatePlayback(
            _mediaPlayer.Position.TotalSeconds,
            duration,
            playing);
        _taskbarMediaExperience.UpdatePlayback(
            _mediaPlayer.Position.TotalSeconds,
            duration,
            playing);

        if (PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = _currentTrackId,
            isPlaying = playing,
            currentTime = _mediaPlayer.Position.TotalSeconds,
            audioInformation = (_mediaPlayer as IPlaybackAudioInformation)?.AudioInformation,
            duration
        }, WebJsonOptions);
        _ = ExecuteScriptAsync($"window.Auralis?.setPlaybackState({payload})");
    }

    private async Task HandlePendingShellAudioFileAsync()
    {
        var path = App.TakePendingShellAudioFile();
        await HandleShellAudioFileAsync(path);
    }

    private async Task HandleShellAudioFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!_library.ContainsKey(fullPath))
        {
            var stored = new StoredTrack(fullPath, DateTime.Now);
            _library[fullPath] = stored;
            await PersistLibraryAsync();
            await SendLibraryAsync("addTracks", [stored]);
        }

        var id = JsonSerializer.Serialize(CreateId(fullPath));
        await ExecuteScriptAsync($"window.Auralis?.playLocalTrack({id})");
    }

    private async Task OpenDefaultAppsSettingsAsync()
    {
        try
        {
            DefaultMusicAppRegistrationService.SetRegistrationEnabledForCurrentUser(enabled: true);
            DefaultMusicAppRegistrationService.OpenWindowsDefaultAppsSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or Win32Exception)
        {
            await ExecuteScriptAsync("window.Auralis?.showToast('无法打开 Windows 默认应用设置')");
        }
    }

    private void OnSystemMediaCommandRequested(SystemMediaCommand command)
    {
        switch (command)
        {
            case SystemMediaCommand.Play:
                if (_currentTrackId is null)
                {
                    _ = ExecuteScriptAsync("window.Auralis?.mediaCommand('playPause')");
                    return;
                }

                _mediaPlayer.Play();
                _playbackTimer.Start();
                NotifyPlaybackState(true);
                break;
            case SystemMediaCommand.Pause:
                _mediaPlayer.Pause();
                _playbackTimer.Stop();
                NotifyPlaybackState(false);
                break;
            case SystemMediaCommand.Previous:
                _ = ExecuteScriptAsync("window.Auralis?.mediaCommand('previous')");
                break;
            case SystemMediaCommand.Next:
                _ = ExecuteScriptAsync("window.Auralis?.mediaCommand('next')");
                break;
            case SystemMediaCommand.None:
            default:
                break;
        }
    }

    private void OnSystemMediaPositionChangeRequested(TimeSpan requestedPosition)
    {
        var duration = _mediaPlayer.Duration;
        _mediaPlayer.Position = duration > TimeSpan.Zero && requestedPosition > duration
            ? duration
            : requestedPosition;
        NotifyPlaybackState();
    }

    private void OnTaskbarMediaCommandRequested(TaskbarMediaCommand command)
    {
        switch (command)
        {
            case TaskbarMediaCommand.Previous:
                _ = ExecuteScriptAsync("window.Auralis?.mediaCommand('previous')");
                break;
            case TaskbarMediaCommand.Next:
                _ = ExecuteScriptAsync("window.Auralis?.mediaCommand('next')");
                break;
            case TaskbarMediaCommand.PlayPause:
                OnSystemMediaCommandRequested(
                    _playbackTimer.IsEnabled ? SystemMediaCommand.Pause : SystemMediaCommand.Play);
                break;
        }
    }

    private void PublishSystemMediaMetadata(TrackInfo track)
    {
        var artworkPath = ResolveLocalCoverPath(track.CoverUrl);
        _taskbarMediaExperience.UpdateMetadata(new TaskbarMediaMetadata(
            track.Id,
            track.Title,
            track.Artist,
            track.Album,
            artworkPath));
        var cancellation = ReplaceCancellation(ref _systemMediaArtworkCancellation);
        var revision = _systemMediaSession.UpdateMetadata(new SystemMediaMetadata(
            track.Id,
            track.Title,
            track.Artist,
            track.Album));
        if (artworkPath is null)
        {
            ClearCancellation(ref _systemMediaArtworkCancellation, cancellation);
            return;
        }

        _ = CompleteSystemMediaArtworkAsync(revision, artworkPath, cancellation);
    }

    private void PublishSystemMediaMetadata(OnlineTrackView track)
    {
        _taskbarMediaExperience.UpdateMetadata(new TaskbarMediaMetadata(
            track.Handle,
            track.Title,
            track.Artist,
            track.Album));
        var cancellation = ReplaceCancellation(ref _systemMediaArtworkCancellation);
        var revision = _systemMediaSession.UpdateMetadata(new SystemMediaMetadata(
            track.Handle,
            track.Title,
            track.Artist,
            track.Album));
        _ = FetchAndPublishOnlineSystemMediaArtworkAsync(track.Handle, revision, cancellation);
    }

    private async Task CompleteSystemMediaArtworkAsync(
        long revision,
        string artworkPath,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _systemMediaSession.UpdateArtworkAsync(revision, artworkPath, cancellation.Token);
        }
        finally
        {
            ClearCancellation(ref _systemMediaArtworkCancellation, cancellation);
        }
    }

    private async Task FetchAndPublishOnlineSystemMediaArtworkAsync(
        string handle,
        long revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (!OnlinePlatforms.TryGetArtworkUri(handle, out var artworkUri, out var destinationAllowed) || artworkUri is null)
            {
                return;
            }

            var artwork = await _onlineArtworkProxy.FetchAsync(new ArtworkRequest(artworkUri,
                handle.StartsWith("avatar-", StringComparison.Ordinal) ? 2 * 1024 * 1024 : 16 * 1024 * 1024,
                destinationAllowed), cancellation.Token);
            if (destinationAllowed?.Invoke(artworkUri) == false) return;
            var extension = artwork.ContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                _ => string.Empty
            };
            if (extension.Length == 0)
            {
                return;
            }

            var cachePath = Path.Combine(_coverCacheDirectory, $"online-{CreateId(handle)}{extension}");
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length != artwork.Length)
            {
                await using var source = artwork.OpenRead();
                await using var destination = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read, 32768, useAsync: true);
                await source.CopyToAsync(destination, cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (!string.Equals(_currentTrackId, handle, StringComparison.Ordinal))
            {
                return;
            }

            _taskbarMediaExperience.UpdateArtwork(handle, cachePath);
            await _systemMediaSession.UpdateArtworkAsync(revision, cachePath, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer track replaced this cover request.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArtworkException)
        {
            Debug.WriteLine($"Windows media artwork could not be cached: {exception.Message}");
        }
        finally
        {
            ClearCancellation(ref _systemMediaArtworkCancellation, cancellation);
        }
    }

    private string? ResolveLocalCoverPath(string? coverUrl)
    {
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "covers.auralis.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var cacheRoot = Path.GetFullPath(_coverCacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(cacheRoot, fileName));
        return candidate.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static async Task SetStartupEnabledAsync(bool enabled)
    {
        if (PackageIdentityService.IsPackaged)
        {
            var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("AuralisStartup");
            if (enabled)
            {
                if (startupTask.State is Windows.ApplicationModel.StartupTaskState.Disabled)
                {
                    _ = await startupTask.RequestEnableAsync();
                }
            }
            else if (startupTask.State is Windows.ApplicationModel.StartupTaskState.Enabled)
            {
                startupTask.Disable();
            }
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            key.SetValue("Auralis", $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue("Auralis", throwOnMissingValue: false);
        }
    }

    private async Task SendLibraryAsync(
        string methodName,
        IEnumerable<StoredTrack> storedTracks,
        bool validatePaths = true)
    {
        var snapshot = storedTracks.ToArray();
        var result = await Task.Run(() =>
        {
            IEnumerable<StoredTrack> candidates = validatePaths
                ? snapshot.Where(track => File.Exists(track.Path))
                : snapshot;
            var projected = candidates.Select(stored =>
            {
                var track = CreateTrackInfo(stored);
                return new
                {
                    Track = track,
                    Shared = new LanSharedTrack(
                        track.Id,
                        stored.Path,
                        track.Title,
                        track.Artist,
                        track.Album,
                        track.Extension,
                        track.Size,
                        track.DurationSeconds,
                        track.DateAdded,
                        ResolveLocalCoverPath(track.CoverUrl))
                };
            }).ToArray();
            return new
            {
                Payload = JsonSerializer.Serialize(projected.Select(item => item.Track), WebJsonOptions),
                Shared = projected.Select(item => item.Shared).ToArray()
            };
        });
        if (string.Equals(methodName, "receiveLibrary", StringComparison.Ordinal))
        {
            LanMusicSharing.ReplaceLibrary(result.Shared);
        }
        else
        {
            LanMusicSharing.UpsertLibrary(result.Shared);
        }
        await ExecuteScriptAsync($"window.Auralis.{methodName}({result.Payload})");
    }

    private TrackInfo CreateTrackInfo(StoredTrack stored)
    {
        var info = new FileInfo(stored.Path);
        var id = CreateId(stored.Path);
        var fallback = ParseDisplayName(Path.GetFileNameWithoutExtension(info.Name));
        var metadata = AudioMetadataReader.Read(stored.Path);
        var title = metadata.Title ?? fallback.Title;
        var artist = metadata.Artist ?? fallback.Artist;
        var album = metadata.Album ?? info.Directory?.Name ?? "本地音乐";
        var coverUrl = CacheCover(CreateId(stored.Path), metadata);

        return new TrackInfo(
            id,
            title,
            artist,
            album,
            info.Name,
            info.Extension.TrimStart('.').ToUpperInvariant(),
            coverUrl,
            info.Length,
            metadata.DurationSeconds,
            stored.DateAdded);
    }

    private string? CacheCover(string id, AudioMetadata metadata)
    {
        if (metadata.Picture is null || string.IsNullOrWhiteSpace(metadata.PictureExtension))
        {
            return null;
        }

        var fileName = id + metadata.PictureExtension;
        var cachePath = Path.Combine(_coverCacheDirectory, fileName);
        try
        {
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length != metadata.Picture.Length)
            {
                File.WriteAllBytes(cachePath, metadata.Picture);
            }

            return $"https://covers.auralis.local/{fileName}";
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task PersistLibraryAsync()
    {
        await _libraryStore.SaveAsync(_library.Values.OrderBy(track => track.DateAdded));
    }

    private static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.TryPop(out var directory))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    directories.Push(child);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Skip folders that cannot be enumerated.
            }
        }
    }

    private static (string Title, string Artist) ParseDisplayName(string fileName)
    {
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var parts = fileName.Split(separator, 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts.All(part => !string.IsNullOrWhiteSpace(part)))
            {
                return (parts[1], parts[0]);
            }
        }

        var title = System.Text.RegularExpressions.Regex.Replace(fileName, @"^\s*\d+[.、_\-\s]+", "");
        return (string.IsNullOrWhiteSpace(title) ? fileName : title, "未知艺术家");
    }

    private static string CreateId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void ToggleMaximize()
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var language = CurrentLanguageState.ResolvedLanguage;
        var icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        var trayIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = NativeText.Get(language, "tray.tooltip"),
            Visible = false
        };
        trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Right)
            {
                _trayMenu.ShowAtCursor();
            }
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        return trayIcon;
    }

    private TrayContextMenuWindow CreateTrayMenu()
    {
        var menu = new TrayContextMenuWindow();
        menu.ShowAuralisRequested += RestoreFromTray;
        menu.ExitRequested += () =>
        {
            _exitRequested = true;
            _trayIcon.Visible = false;
            Close();
        };
        menu.ApplyTheme(_nativeDarkTheme);
        return menu;
    }

    private void LoadNativeWindowSettings()
    {
        var settings = _windowSettingsStore.Load();
        _closeToTrayEnabled = settings.CloseToTray;
        _trayEnabled = settings.CloseToTray;
        _uiLanguagePreference = UiLanguagePreference.Normalize(settings.UiLanguage);
    }

    private void SaveNativeWindowSettings()
    {
        try
        {
            _windowSettingsStore.Save(new NativeWindowSettings(
                WindowSettingsStore.CurrentSchemaVersion,
                _closeToTrayEnabled,
                _uiLanguagePreference));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Log(
                AppLogLevel.Warning,
                "window.settings",
                "save.failed",
                exception: exception);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _closeToTrayEnabled)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }

        Hide();
        _trayIcon.Visible = true;
        Dispatcher.BeginInvoke(
            _taskbarMediaExperience.RefreshAfterHostWindowStateChanged,
            DispatcherPriority.ContextIdle);
    }

    private void SetTrayMinimizeTimer(int minutes)
    {
        _trayMinimizeTimer.Stop();
        _trayMinimizeDeadline = minutes > 0
            ? DateTimeOffset.UtcNow.AddMinutes(minutes)
            : null;
        if (_trayMinimizeDeadline is not null)
        {
            _trayMinimizeTimer.Start();
        }

        NotifyTrayMinimizeTimer();
    }

    private void OnTrayMinimizeTimerTick(object? sender, EventArgs e)
    {
        if (_trayMinimizeDeadline is not { } deadline)
        {
            _trayMinimizeTimer.Stop();
            return;
        }

        if (deadline > DateTimeOffset.UtcNow)
        {
            NotifyTrayMinimizeTimer();
            return;
        }

        _trayMinimizeTimer.Stop();
        _trayMinimizeDeadline = null;
        HideToTray();
        NotifyTrayMinimizeTimer();
    }

    private void NotifyTrayMinimizeTimer()
    {
        var remainingSeconds = _trayMinimizeDeadline is { } deadline
            ? Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))
            : 0;
        _ = ExecuteScriptAsync(
            $"window.Auralis?.setTrayMinimizeTimerState({{active:{(_trayMinimizeDeadline is null ? "false" : "true")},remainingSeconds:{remainingSeconds}}})");
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        });
    }

    private async Task SendAudioDevicesAsync(bool includeEndpointDiagnostics = false)
    {
        var snapshot = _mediaPlayer.GetAudioDeviceSnapshot();
        if (includeEndpointDiagnostics)
        {
            var requestVersion = Interlocked.Increment(ref _audioDiagnosticsRequestVersion);
            var request = _mediaPlayer.GetAudioEndpointProbeRequest(snapshot);
            var endpointLatency = request is null
                ? AudioEndpointLatencyInfo.Unavailable("unsupportedBackend")
                : await WindowsAudioEndpointLatencyProbe.ProbeAsync(request.EndpointId, request.FallbackName);
            if (requestVersion != Volatile.Read(ref _audioDiagnosticsRequestVersion))
            {
                return;
            }
            snapshot = snapshot with { EndpointLatency = endpointLatency };
        }

        var payload = JsonSerializer.Serialize(snapshot, WebJsonOptions);
        await ExecuteScriptAsync($"window.Auralis?.setAudioDevices({payload})");
    }

    private void NotifyDpiScale()
    {
        if (_windowHandle == nint.Zero || PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var dpi = GetDpiForWindow(_windowHandle);
        var scale = Math.Max(1, dpi) / 96d;
        _ = ExecuteScriptAsync($"window.Auralis?.setDpiScale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private void ConstrainInitialSizeToCurrentMonitor()
    {
        var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var scale = Math.Max(1, GetDpiForWindow(_windowHandle)) / 96d;
        var workArea = monitorInfo.WorkArea;
        var availableWidth = Math.Max(MinWidth, (workArea.Right - workArea.Left) / scale);
        var availableHeight = Math.Max(MinHeight, (workArea.Bottom - workArea.Top) / scale);
        Width = Math.Min(Width, availableWidth * .94);
        Height = Math.Min(Height, availableHeight * .94);
    }

    private void BeginWindowDrag()
    {
        if (_windowHandle == nint.Zero || _isFullscreen)
        {
            return;
        }

        // WebView2 owns the pointer, so WPF's DragMove cannot reliably observe
        // the pressed mouse button. Hand control to Windows' native caption move
        // loop instead; this also preserves drag-to-snap and maximized drag-down.
        _ = ReleaseCapture();
        _ = SendMessage(_windowHandle, WmNcLeftButtonDown, new nint(HtCaption), nint.Zero);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _restoreBounds = RestoreBounds;
            _restoreWindowState = WindowState;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            {
                var monitorArea = monitorInfo.MonitorArea;
                _ = SetWindowPos(
                    _windowHandle,
                    nint.Zero,
                    monitorArea.Left,
                    monitorArea.Top,
                    monitorArea.Right - monitorArea.Left,
                    monitorArea.Bottom - monitorArea.Top,
                    SwpNoZOrder | SwpNoActivate);
            }
            else
            {
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
            }
            _isFullscreen = true;
        }
        else
        {
            Topmost = _userTopmost;
            ResizeMode = ResizeMode.CanResize;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _restoreWindowState;
            _isFullscreen = false;
        }

        UpdateWebViewResizeFrame();
        _ = ExecuteScriptAsync($"window.Auralis?.setFullscreenState({(_isFullscreen ? "true" : "false")})");
    }

    private void UpdateWebViewResizeFrame()
    {
        PlayerWebView.Margin = _isFullscreen || WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(8);
    }

    private void NotifyWindowState()
    {
        if (PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized ? "true" : "false";
        _ = ExecuteScriptAsync($"window.Auralis?.setWindowState({maximized})");
    }

    private void ApplyNativeTheme(string? theme)
    {
        var dark = theme == "dark";
        _nativeDarkTheme = dark;
        var color = dark ? System.Windows.Media.Color.FromRgb(32, 32, 32) : System.Windows.Media.Color.FromRgb(243, 243, 243);
        Root.Background = new SolidColorBrush(color);
        PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        if (_windowHandle != nint.Zero)
        {
            var darkMode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(_windowHandle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));
        }
        foreach (var videoWindow in _musicVideoWindows.ToArray())
        {
            videoWindow.ApplyTheme(dark);
        }
        _trayMenu.ApplyTheme(dark);
        ApplyMicaMaterial();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmNcHitTest && !_isFullscreen && lParam != nint.Zero)
        {
            var packedPosition = lParam.ToInt64();
            var pointerX = unchecked((short)(packedPosition & 0xffff));
            var pointerY = unchecked((short)((packedPosition >> 16) & 0xffff));
            var windowRect = new NativeRect();
            if (GetWindowRect(hwnd, ref windowRect))
            {
                var scale = GetDpiForWindow(hwnd) / 96d;
                if (WindowState == WindowState.Normal)
                {
                    var horizontalResizeBorder = Math.Max(
                        (int)Math.Ceiling(8 * scale),
                        GetSystemMetricsForDpi(SmCxSizeFrame, GetDpiForWindow(hwnd)) +
                        GetSystemMetricsForDpi(SmCxPaddedBorder, GetDpiForWindow(hwnd)));
                    var verticalResizeBorder = Math.Max(
                        (int)Math.Ceiling(8 * scale),
                        GetSystemMetricsForDpi(SmCySizeFrame, GetDpiForWindow(hwnd)) +
                        GetSystemMetricsForDpi(SmCxPaddedBorder, GetDpiForWindow(hwnd)));
                    var onLeft = pointerX >= windowRect.Left && pointerX < windowRect.Left + horizontalResizeBorder;
                    var onRight = pointerX < windowRect.Right && pointerX >= windowRect.Right - horizontalResizeBorder;
                    var onTop = pointerY >= windowRect.Top && pointerY < windowRect.Top + verticalResizeBorder;
                    var onBottom = pointerY < windowRect.Bottom && pointerY >= windowRect.Bottom - verticalResizeBorder;
                    var resizeHit = onTop
                        ? onLeft ? HtTopLeft : onRight ? HtTopRight : HtTop
                        : onBottom
                            ? onLeft ? HtBottomLeft : onRight ? HtBottomRight : HtBottom
                            : onLeft ? HtLeft : onRight ? HtRight : 0;
                    if (resizeHit != 0)
                    {
                        handled = true;
                        return new nint(resizeHit);
                    }
                }

                // The HTML title bar already forwards its exact drag region through
                // BeginWindowDrag(). Keeping caption geometry here would duplicate CSS layout
                // constants and could turn buttons or page content into an accidental drag area.
            }
        }

        if (message != WmGetMinMaxInfo || lParam == nint.Zero)
        {
            return nint.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var workArea = monitorInfo.WorkArea;
            var monitorArea = monitorInfo.MonitorArea;
            minMaxInfo.MaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
            minMaxInfo.MaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
            minMaxInfo.MaxSize.X = Math.Abs(workArea.Right - workArea.Left);
            minMaxInfo.MaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
            Marshal.StructureToPtr(minMaxInfo, lParam, true);
            handled = true;
        }

        return nint.Zero;
    }

    private static nint GetWindowStyle(nint handle)
    {
        return nint.Size == 8 ? GetWindowLongPtr64(handle, GwlStyle) : new nint(GetWindowLong32(handle, GwlStyle));
    }

    private static void SetWindowStyle(nint handle, nint style)
    {
        if (nint.Size == 8)
        {
            _ = SetWindowLongPtr64(handle, GwlStyle, style);
        }
        else
        {
            _ = SetWindowLong32(handle, GwlStyle, style.ToInt32());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(nint hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, ref NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int valueSize);

    private async Task ExecuteScriptAsync(string script)
    {
        if (PlayerWebView.CoreWebView2 is not null)
        {
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}
