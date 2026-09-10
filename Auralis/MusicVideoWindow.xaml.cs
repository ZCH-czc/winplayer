using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Auralis.Platform.Abstractions;
using Auralis.Services;
using System.IO;
using System.Windows.Media.Imaging;

namespace Auralis;

public partial class MusicVideoWindow : Window
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSystemMenu = 0x00080000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const uint DwmColorNone = 0xfffffffe;

    private readonly PlatformVideoLease _lease;
    private readonly DispatcherTimer _progressTimer;
    private readonly IPlaybackSession _player;
    private readonly DispatcherTimer _frameTimer;
    private byte[]? _lastFrame;
    private bool _ended;
    private AudioPlaybackProfile _playbackProfile;
    private bool _isSeeking;
    private bool _synchronizingPlaybackProfile;
    private bool _isFullscreen;
    private bool _closing;
    private Rect _restoreBounds;
    private WindowState _restoreState;

    internal MusicVideoWindow(
        PlatformVideoLease lease,
        string title,
        string artist,
        bool darkTheme,
        AudioPlaybackProfile playbackProfile,
        IPlaybackSession? session = null)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _playbackProfile = NormalizePlaybackProfile(playbackProfile);
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "MV" : title;
        ArtistText.Text = string.IsNullOrWhiteSpace(artist) ? "在线音乐" : artist;
        FormatText.Text = string.IsNullOrWhiteSpace(lease.FormatLabel)
            ? "MV"
            : $"在线音乐 · {DisplayFormat(lease.FormatLabel)}";

        _player = session ?? PlaybackServices.Create(new DispatcherSynchronizationContext(Dispatcher));
        _player.ApplyAudioOutputSettings(_playbackProfile.OutputSettings);
        ApplyVolumeAndRate();
        SynchronizeVolumeControl();
        VideoHost.Visibility = Visibility.Collapsed;
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { UpdateProgress(); UpdateTransport(); }, Dispatcher);
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
            (_, _) => UpdateFrame(), Dispatcher);
        _frameTimer.Stop();
        _progressTimer.Stop();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;

        ApplyTheme(darkTheme);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Closing += OnClosing;
    }

    internal event EventHandler<MusicVideoVolumeChangedEventArgs>? VolumeChanged;

    internal void ApplyPlaybackProfile(AudioPlaybackProfile playbackProfile)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ApplyPlaybackProfile(playbackProfile));
            return;
        }

        if (_closing)
        {
            return;
        }

        _playbackProfile = NormalizePlaybackProfile(playbackProfile);
        _player.ApplyAudioOutputSettings(_playbackProfile.OutputSettings);
        ApplyVolumeAndRate();
        SynchronizeVolumeControl();
    }

    internal void ApplyPlaybackVolume(double volume)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ApplyPlaybackVolume(volume));
            return;
        }

        if (_closing)
        {
            return;
        }

        _playbackProfile = _playbackProfile with { Volume = Math.Clamp(volume, 0, 1) };
        _player.Volume = _playbackProfile.Volume;
        SynchronizeVolumeControl();
    }

    internal void ApplyPlaybackRate(double speedRatio)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ApplyPlaybackRate(speedRatio));
            return;
        }

        if (_closing)
        {
            return;
        }

        _playbackProfile = _playbackProfile with { SpeedRatio = Math.Clamp(speedRatio, 0.5, 2) };
        _player.SpeedRatio = _playbackProfile.SpeedRatio;
    }

    internal void ApplyTheme(bool darkTheme)
    {
        var color = darkTheme
            ? System.Windows.Media.Color.FromRgb(11, 11, 11)
            : System.Windows.Media.Color.FromRgb(20, 20, 20);
        Root.Background = new SolidColorBrush(color);
        Background = new SolidColorBrush(color);
        if (new WindowInteropHelper(this).Handle is { } hwnd && hwnd != nint.Zero)
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        SetWindowLongPtr(hwnd, GwlStyle, new nint(style.ToInt64() |
            WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu));
        _ = SetWindowPos(
            hwnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        var corners = 2;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corners, sizeof(int));
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var border = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(uint));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (SystemParameters.ClientAreaAnimation) Root.BeginAnimation(OpacityProperty, fade);
        else Root.Opacity = 1;

        if (_lease.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(2))
        {
            ShowStatus("MV 播放授权已经过期，请关闭窗口后重试。", false);
            return;
        }

        if (_lease.RequestHeaders.Count != 0)
        {
            ShowStatus("这个 MV 需要当前播放器不支持的额外授权头。", false);
            return;
        }

        try
        {
            ShowStatus("正在缓冲 MV…", true);
            _player.Open(_lease.Url, video: true);
        }
        catch (Exception)
        {
            ShowStatus("无法启动 MV 播放器。", false);
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e) => Dispatch(() =>
    {
        _ended = false;
        _frameTimer.Start();
        _progressTimer.Start();
        UpdateProgress();
        UpdateTransport();
    });
    private void Player_MediaEnded(object? sender, EventArgs e) => Dispatch(() =>
    {
        _ended = true;
        _progressTimer.Stop();
        _frameTimer.Stop();
        ProgressSlider.Value = ProgressSlider.Maximum;
        UpdateTransport();
    });
    private void Player_MediaFailed(object? sender, AudioPlaybackFailedEventArgs e) => Dispatch(() =>
        ShowStatus("这个 MV 暂时无法播放，请检查网络、版权或地区限制。", false));

    private void UpdateTransport()
    {
        PlayPauseButton.Content = _player.WantsPlayback ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = _player.WantsPlayback ? "暂停" : _ended ? "重新播放" : "播放";
    }
    private void UpdateFrame()
    {
        if (_closing || WindowState == WindowState.Minimized) return;
        var frame = _player.ReadVideoFrame();
        if (frame is null || ReferenceEquals(frame, _lastFrame)) return;
        try
        {
            using var stream = new MemoryStream(frame, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            VideoHost.Source = image;
            _lastFrame = frame;
            VideoHost.Visibility = Visibility.Visible;
            StatusOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            // Keep the last complete frame; malformed images never replace it.
            _lastFrame = frame;
        }
    }

    private void UpdateProgress()
    {
        var length = Math.Max(0, (long)_player.Duration.TotalMilliseconds);
        var time = Math.Max(0, (long)_player.Position.TotalMilliseconds);
        if (!_isSeeking)
        {
            ProgressSlider.Maximum = Math.Max(1, length);
            ProgressSlider.Value = Math.Min(time, ProgressSlider.Maximum);
        }

        PositionText.Text = FormatTime(time);
        DurationText.Text = FormatTime(length);
    }

    private void ShowStatus(string message, bool loading)
    {
        VideoHost.Visibility = Visibility.Collapsed;
        if (!loading) { _frameTimer.Stop(); _progressTimer.Stop(); }
        StatusText.Text = message;
        LoadingProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
        UpdateTransport();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _player.IsMuted = !_player.IsMuted;
        MuteButton.Content = _player.IsMuted ? "\uE74F" : "\uE767";
        MuteButton.ToolTip = _player.IsMuted ? "取消静音" : "静音";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
        {
            var volume = Math.Clamp(e.NewValue / 100d, 0, 1);
            _player.Volume = volume;
            if (_player.Volume > 0 && _player.IsMuted)
            {
                _player.IsMuted = false;
                MuteButton.Content = "\uE767";
            }

            if (!_synchronizingPlaybackProfile)
            {
                _playbackProfile = _playbackProfile with { Volume = volume };
                VolumeChanged?.Invoke(this, new MusicVideoVolumeChangedEventArgs(volume));
            }
        }
    }

    private void ApplyVolumeAndRate()
    {
        _player.Volume = _playbackProfile.Volume;
        _player.SpeedRatio = _playbackProfile.SpeedRatio;
    }

    private void SynchronizeVolumeControl()
    {
        _synchronizingPlaybackProfile = true;
        try
        {
            VolumeSlider.Value = _playbackProfile.Volume * 100;
            MuteButton.Content = _player.IsMuted || _playbackProfile.Volume <= 0 ? "\uE74F" : "\uE767";
            MuteButton.ToolTip = _player.IsMuted || _playbackProfile.Volume <= 0 ? "取消静音" : "静音";
        }
        finally
        {
            _synchronizingPlaybackProfile = false;
        }
    }

    private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _player.Position = TimeSpan.FromMilliseconds(Math.Max(0, ProgressSlider.Value));
        _isSeeking = false;
        UpdateProgress();
    }

    private void TopChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _restoreBounds = RestoreBounds;
            _restoreState = WindowState;
            TopChrome.Visibility = Visibility.Collapsed;
            TopRow.Height = new GridLength(0);
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Topmost = true;
            _isFullscreen = true;
        }
        else
        {
            TopChrome.Visibility = Visibility.Visible;
            TopRow.Height = new GridLength(52);
            Topmost = false;
            WindowState = WindowState.Normal;
            if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
            }
            WindowState = _restoreState;
            _isFullscreen = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            PlayPause_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _progressTimer.Stop();
        _frameTimer.Stop();
        _player.MediaOpened -= Player_MediaOpened;
        _player.MediaEnded -= Player_MediaEnded;
        _player.MediaFailed -= Player_MediaFailed;
        _player.Dispose();
        VideoHost.Source = null;
        _lastFrame = null;
    }

    private void Dispatch(Action action)
    {
        if (_closing) return;
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => { if (!_closing) action(); });
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds / 1000);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    private static string DisplayFormat(string formatLabel) =>
        formatLabel.All(char.IsDigit) ? "MP4" : formatLabel;

    private static AudioPlaybackProfile NormalizePlaybackProfile(AudioPlaybackProfile profile) =>
        new(
            profile.OutputSettings ?? AudioOutputSettings.Default,
            Math.Clamp(profile.Volume, 0, 1),
            Math.Clamp(profile.SpeedRatio, 0.5, 2));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    private static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new nint(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : new nint(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}

internal sealed class MusicVideoVolumeChangedEventArgs(double volume) : EventArgs
{
    internal double Volume { get; } = Math.Clamp(volume, 0, 1);
}
