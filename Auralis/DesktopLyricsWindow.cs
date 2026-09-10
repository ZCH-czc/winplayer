using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Auralis.Models;
using Auralis.Services;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;
using ShapesPath = System.Windows.Shapes.Path;
using WpfButton = System.Windows.Controls.Button;

namespace Auralis;

public sealed record DesktopLyricsOptions(
    bool Enabled = false,
    double FontSize = 30,
    int FontWeight = 600,
    string ActiveColor = "#73BCFC",
    string InactiveColor = "rgba(32,33,36,0.58)",
    string ShadowColor = "rgba(255,255,255,0.72)",
    string MaskColor = "rgba(255,255,255,0.36)",
    double MaskBrightness = DesktopLyricsMaskStyle.DefaultBrightness,
    string Alignment = "center",
    bool ShowMask = true,
    bool Animate = true,
    bool WordHighlight = true,
    bool ShowTranslation = true,
    bool DoubleLine = true,
    bool AlwaysShowSongInfo = true,
    bool Locked = false,
    double OffsetMilliseconds = 0);

public sealed class DesktopLyricsLockStateChangedEventArgs(bool locked) : EventArgs
{
    public bool Locked { get; } = locked;
}

public sealed class DesktopLyricsWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    private readonly Border _surface;
    private readonly Grid _content;
    private readonly TextBlock _songInfo;
    private readonly TextBlock _currentLine;
    private readonly TextBlock _nextLine;
    private readonly Grid _toolbar;
    private readonly Window _unlockWindow;
    private readonly Border _unlockChrome;
    private readonly ShapesPath _unlockIcon;
    private WpfButton _toolbarLockButton = null!;
    private DesktopLyricsOptions _options = new();
    private TrackInfo? _track;
    private LyricsResponse? _lyrics;
    private int _activeIndex = -2;
    private bool _closingFromApp;
    private readonly string _boundsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Auralis",
        "desktop-lyrics-window.json");

    public event EventHandler<DesktopLyricsLockStateChangedEventArgs>? LockStateChanged;

    public DesktopLyricsWindow()
    {
        Title = "Auralis 桌面歌词";
        Width = 760;
        Height = 146;
        MinWidth = 420;
        MinHeight = 108;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _songInfo = new TextBlock
        {
            Margin = new Thickness(22, 11, 22, 2),
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(178, 32, 33, 36)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _currentLine = CreateLyricTextBlock();
        _nextLine = CreateLyricTextBlock();
        _nextLine.FontSize = 17;
        _nextLine.Opacity = .72;

        _content = new Grid();
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_songInfo, 0);
        Grid.SetRow(_currentLine, 1);
        Grid.SetRow(_nextLine, 2);
        _content.Children.Add(_songInfo);
        _content.Children.Add(_currentLine);
        _content.Children.Add(_nextLine);

        _toolbar = BuildToolbar();
        var root = new Grid();
        root.Children.Add(_content);
        root.Children.Add(_toolbar);

        _surface = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(38, 0, 0, 0)),
            Background = new SolidColorBrush(MediaColor.FromArgb(92, 250, 250, 250)),
            Child = root
        };
        Content = _surface;

        (_unlockWindow, _unlockChrome, _unlockIcon) = BuildUnlockWindow();

        Loaded += (_, _) =>
        {
            RestoreSavedBounds();
            ApplyOptions(_options);
        };
        SourceInitialized += (_, _) => ApplyClickThrough();
        LocationChanged += (_, _) => PositionUnlockWindow();
        SizeChanged += (_, _) => PositionUnlockWindow();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionUnlockWindow);
        Closing += (_, args) =>
        {
            SaveBounds();
            if (!_closingFromApp)
            {
                args.Cancel = true;
                HideUnlockWindow();
                Hide();
            }
        };
        MouseLeftButtonDown += (_, args) =>
        {
            if (!_options.Locked && args.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        MouseEnter += (_, _) => _toolbar.Opacity = _options.Locked ? 0 : 1;
        MouseLeave += (_, _) => _toolbar.Opacity = 0;
        IsVisibleChanged += (_, _) => UpdateLockPresentation();
    }

    public void ApplyOptions(DesktopLyricsOptions options)
    {
        var previousOptions = _options;
        _options = options;
        // Desktop lyrics deliberately stay on a light, highly translucent surface in both
        // application themes. This avoids the opaque grey card that looked detached from Windows.
        var maskAlpha = DesktopLyricsMaskStyle.ResolveAlpha(options.MaskBrightness);
        _surface.Background = options.ShowMask
            ? new SolidColorBrush(MediaColor.FromArgb(maskAlpha, 250, 250, 250))
            : MediaBrushes.Transparent;
        _surface.BorderBrush = options.ShowMask
            ? new SolidColorBrush(MediaColor.FromArgb(38, 0, 0, 0))
            : MediaBrushes.Transparent;
        // Brightness slider input can arrive at pointer-move frequency. When the mask is the only
        // changed presentation value, avoid rebuilding text effects or restarting lyric animation.
        var maskOnlyChange = previousOptions with
        {
            ShowMask = options.ShowMask,
            MaskBrightness = options.MaskBrightness
        } == options;
        if (maskOnlyChange)
        {
            return;
        }

        _currentLine.FontSize = Math.Clamp(options.FontSize, 18, 64);
        _currentLine.FontWeight = FontWeightFrom(options.FontWeight);
        _nextLine.FontSize = Math.Clamp(options.FontSize * .62, 13, 38);
        _nextLine.FontWeight = FontWeightFrom(Math.Min(options.FontWeight, 600));
        var activeColor = ParseColor(options.ActiveColor, MediaColor.FromRgb(115, 188, 252));
        _currentLine.Foreground = new SolidColorBrush(activeColor);
        _unlockChrome.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(42, 0, 0, 0));
        _nextLine.Foreground = new SolidColorBrush(ParseColor(options.InactiveColor, MediaColor.FromArgb(148, 32, 33, 36)));
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 5,
            ShadowDepth = 0,
            Opacity = .44,
            Color = ParseColor(options.ShadowColor, MediaColor.FromArgb(176, 255, 255, 255))
        };
        _currentLine.Effect = shadow;
        _nextLine.Effect = shadow;
        _nextLine.Visibility = options.DoubleLine ? Visibility.Visible : Visibility.Collapsed;
        _songInfo.Visibility = options.AlwaysShowSongInfo ? Visibility.Visible : Visibility.Collapsed;
        var alignment = options.Alignment switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        _songInfo.TextAlignment = alignment;
        _currentLine.TextAlignment = alignment;
        _nextLine.TextAlignment = alignment;
        _toolbar.Opacity = 0;
        if (options.Enabled)
        {
            if (!IsVisible) Show();
            ApplyClickThrough();
            UpdateLockPresentation();
        }
        else
        {
            HideUnlockWindow();
            Hide();
        }
        UpdateDisplayedLines(force: true);
    }

    public void SetTrack(TrackInfo? track)
    {
        _track = track;
        _songInfo.Text = track is null ? "Auralis · 等待播放" : $"{track.Title}  ·  {track.Artist}";
    }

    public void SetLyrics(LyricsResponse? lyrics)
    {
        _lyrics = lyrics;
        _activeIndex = -2;
        UpdateDisplayedLines(force: true);
    }

    public void UpdatePlayback(double seconds, double duration, bool isPlaying)
    {
        if (_lyrics is null || _lyrics.Lines.Count == 0) return;

        seconds -= _options.OffsetMilliseconds / 1000d;
        var lines = _lyrics.Lines;
        var active = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].TimeSeconds is not double time || time > seconds + .06) break;
            active = index;
        }
        if (active != _activeIndex)
        {
            _activeIndex = active;
            UpdateDisplayedLines();
        }

        // Line timing still drives lyric changes, but the former blue progress rail was removed.
    }

    public void CloseFromApp()
    {
        _closingFromApp = true;
        _unlockWindow.Close();
        Close();
    }

    private TextBlock CreateLyricTextBlock() => new()
    {
        Margin = new Thickness(22, 5, 22, 5),
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new MediaFontFamily("Segoe UI Variable Display, Microsoft YaHei UI"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private Grid BuildToolbar()
    {
        var bar = new Grid
        {
            Height = 30,
            Margin = new Thickness(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0
        };
        var label = new TextBlock
        {
            Text = "拖动桌面歌词",
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(176, 32, 33, 36)),
            FontSize = 11
        };

        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(label);
        panel.Children.Add(BuildToolbarLockButton());

        bar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(MediaColor.FromArgb(196, 250, 250, 250)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(34, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = panel
        });
        return bar;
    }

    private Border BuildToolbarLockButton()
    {
        var icon = CreateLockIcon(unlocked: false, 14);
        var chrome = new Border
        {
            Width = 28,
            Height = 26,
            Margin = new Thickness(0, 1, 2, 1),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(MediaColor.FromArgb(16, 255, 255, 255)),
            Child = icon
        };
        _toolbarLockButton = new WpfButton
        {
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = chrome,
            ToolTip = "锁定桌面歌词"
        };
        AutomationProperties.SetName(_toolbarLockButton, "锁定桌面歌词");
        AutomationProperties.SetHelpText(_toolbarLockButton, "锁定后歌词区域允许鼠标穿透，右上角的解锁按钮仍可点击");
        AutomationProperties.SetAutomationId(_toolbarLockButton, "DesktopLyricsLockButton");
        ToolTipService.SetShowDuration(_toolbarLockButton, 60000);
        _toolbarLockButton.Click += (_, _) => SetLockedFromWindow(true);
        _toolbarLockButton.MouseEnter += (_, _) => chrome.Background = new SolidColorBrush(MediaColor.FromArgb(42, 255, 255, 255));
        _toolbarLockButton.MouseLeave += (_, _) => chrome.Background = new SolidColorBrush(MediaColor.FromArgb(16, 255, 255, 255));
        return new Border { Child = _toolbarLockButton };
    }

    private (Window Window, Border Chrome, ShapesPath Icon) BuildUnlockWindow()
    {
        var icon = CreateLockIcon(unlocked: true, 13.5);
        var chrome = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(MediaColor.FromArgb(224, 252, 252, 252)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(42, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = icon
        };
        var button = new WpfButton
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = chrome,
            ToolTip = "解锁桌面歌词"
        };
        AutomationProperties.SetName(button, "解锁桌面歌词");
        AutomationProperties.SetHelpText(button, "允许拖动和调整桌面歌词窗口；解锁后歌词区域将不再穿透鼠标");
        AutomationProperties.SetAutomationId(button, "DesktopLyricsUnlockButton");
        ToolTipService.SetInitialShowDelay(button, 350);
        ToolTipService.SetShowDuration(button, 60000);
        button.Click += (_, _) => SetLockedFromWindow(false);
        button.MouseEnter += (_, _) =>
        {
            chrome.Background = new SolidColorBrush(MediaColor.FromArgb(246, 255, 255, 255));
            chrome.RenderTransform = new ScaleTransform(1.04, 1.04, 13, 13);
        };
        button.MouseLeave += (_, _) =>
        {
            chrome.Background = new SolidColorBrush(MediaColor.FromArgb(224, 252, 252, 252));
            chrome.RenderTransform = Transform.Identity;
        };

        var window = new Window
        {
            Title = "解锁 Auralis 桌面歌词",
            Width = 30,
            Height = 30,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = MediaBrushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = button
        };
        window.SourceInitialized += (_, _) => ApplyUnlockWindowStyles(window);
        window.Closing += (_, args) =>
        {
            if (!_closingFromApp)
            {
                args.Cancel = true;
                window.Hide();
            }
        };
        return (window, chrome, icon);
    }

    private static ShapesPath CreateLockIcon(bool unlocked, double size)
    {
        var geometry = unlocked
            ? "M 6,9 L 6,6 C 6,3.8 7.8,2 10,2 C 12.2,2 14,3.8 14,6 M 4,9 L 16,9 L 16,18 L 4,18 Z M 10,12 L 10,15"
            : "M 6,9 L 6,6 C 6,3.8 7.8,2 10,2 C 12.2,2 14,3.8 14,6 L 14,9 M 4,9 L 16,9 L 16,18 L 4,18 Z M 10,12 L 10,15";
        return new ShapesPath
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse(geometry),
            Stroke = new SolidColorBrush(MediaColor.FromArgb(220, 32, 33, 36)),
            StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = MediaBrushes.Transparent,
            IsHitTestVisible = false
        };
    }

    private void SetLockedFromWindow(bool locked)
    {
        if (_options.Locked == locked)
        {
            return;
        }

        _options = _options with { Locked = locked };
        ApplyClickThrough();
        UpdateLockPresentation();
        LockStateChanged?.Invoke(this, new DesktopLyricsLockStateChangedEventArgs(locked));
    }

    private void UpdateLockPresentation()
    {
        _toolbar.Opacity = 0;
        if (_options.Enabled && _options.Locked && IsVisible && !_closingFromApp)
        {
            ShowUnlockWindow();
        }
        else
        {
            HideUnlockWindow();
        }
    }

    private void ShowUnlockWindow()
    {
        if (_unlockWindow.Owner is null)
        {
            _unlockWindow.Owner = this;
        }
        PositionUnlockWindow();
        if (!_unlockWindow.IsVisible)
        {
            _unlockWindow.Opacity = 0;
            _unlockWindow.Show();
            _unlockWindow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            var scale = new ScaleTransform(.82, .82);
            _unlockIcon.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            _unlockIcon.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.82, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new BackEase { Amplitude = .18, EasingMode = EasingMode.EaseOut }
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.82, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new BackEase { Amplitude = .18, EasingMode = EasingMode.EaseOut }
            });
        }
        PositionUnlockWindow();
    }

    private void HideUnlockWindow()
    {
        if (_unlockWindow.IsVisible)
        {
            _unlockWindow.Hide();
        }
    }

    private void PositionUnlockWindow()
    {
        if (!_unlockWindow.IsVisible && (!_options.Enabled || !_options.Locked || !IsVisible))
        {
            return;
        }

        var visibleWidth = ActualWidth > 0 ? ActualWidth : Width;
        _unlockWindow.Left = Left + Math.Max(6, visibleWidth - _unlockWindow.Width - 6);
        _unlockWindow.Top = Top + 6;
    }

    private static void ApplyUnlockWindowStyles(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new nint(style));
    }

    private void UpdateDisplayedLines(bool force = false)
    {
        string current;
        string next;
        if (_lyrics is null || _lyrics.Lines.Count == 0)
        {
            current = _track is null ? "这是 Auralis 桌面歌词" : "暂无歌词";
            next = _lyrics?.Message ?? "可在全屏播放器中为歌曲选择本地歌词";
        }
        else if (!_lyrics.IsSynced)
        {
            current = _lyrics.Lines.FirstOrDefault()?.Text ?? "暂无歌词";
            next = _options.DoubleLine ? _lyrics.Lines.Skip(1).FirstOrDefault()?.Text ?? string.Empty : string.Empty;
        }
        else
        {
            var currentIndex = Math.Max(0, _activeIndex);
            var currentTime = _lyrics.Lines.ElementAtOrDefault(currentIndex)?.TimeSeconds;
            while (currentIndex > 0 && currentTime.HasValue &&
                   Math.Abs((_lyrics.Lines[currentIndex - 1].TimeSeconds ?? -10) - currentTime.Value) < .01)
            {
                currentIndex--;
            }
            current = _lyrics.Lines.ElementAtOrDefault(currentIndex)?.Text ?? "♪";
            var translationIndex = currentIndex + 1 < _lyrics.Lines.Count && currentTime.HasValue &&
                                   Math.Abs((_lyrics.Lines[currentIndex + 1].TimeSeconds ?? -10) - currentTime.Value) < .01
                ? currentIndex + 1
                : -1;
            next = _options.ShowTranslation && translationIndex >= 0
                ? _lyrics.Lines[translationIndex].Text
                : _lyrics.Lines.Skip(currentIndex + 1).FirstOrDefault(line =>
                    !currentTime.HasValue || !line.TimeSeconds.HasValue || Math.Abs(line.TimeSeconds.Value - currentTime.Value) >= .01)?.Text ?? string.Empty;
        }

        if (!force && _currentLine.Text == current && _nextLine.Text == next) return;
        _currentLine.Text = current;
        _nextLine.Text = next;
        if (_options.Animate)
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _currentLine.BeginAnimation(OpacityProperty, animation);
            _nextLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0, .72, TimeSpan.FromMilliseconds(280)));
        }
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow;
        style = _options.Locked ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new nint(style));
    }

    private void RestoreSavedBounds()
    {
        try
        {
            if (!File.Exists(_boundsPath))
            {
                Left = Math.Max(20, (SystemParameters.WorkArea.Width - Width) / 2);
                Top = Math.Max(20, SystemParameters.WorkArea.Bottom - Height - 90);
                return;
            }
            var saved = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(_boundsPath));
            if (saved is null || !HasFiniteBounds(saved)) return;
            Width = Math.Clamp(saved.Width, MinWidth, SystemParameters.WorkArea.Width);
            Height = Math.Clamp(saved.Height, MinHeight, 320);
            Left = Math.Clamp(saved.Left, SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width);
            Top = Math.Clamp(saved.Top, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Invalid persisted bounds should not prevent the lyric window from opening.
        }
    }

    private void SaveBounds()
    {
        try
        {
            var bounds = new SavedBounds(Left, Top, Width, Height);
            if (!HasFiniteBounds(bounds))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_boundsPath)!);
            File.WriteAllText(_boundsPath, JsonSerializer.Serialize(bounds));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window position persistence is best effort.
        }
    }

    private static bool HasFiniteBounds(SavedBounds bounds) =>
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height);

    private static FontWeight FontWeightFrom(int value) => value switch
    {
        >= 800 => FontWeights.ExtraBold,
        >= 700 => FontWeights.Bold,
        >= 600 => FontWeights.SemiBold,
        >= 500 => FontWeights.Medium,
        _ => FontWeights.Normal
    };

    private static MediaColor ParseColor(string? value, MediaColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            if (value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
            {
                var components = value[(value.IndexOf('(') + 1)..value.LastIndexOf(')')]
                    .Split(',', StringSplitOptions.TrimEntries);
                if (components.Length == 4 &&
                    byte.TryParse(components[0], out var red) &&
                    byte.TryParse(components[1], out var green) &&
                    byte.TryParse(components[2], out var blue) &&
                    double.TryParse(components[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var alpha))
                {
                    return MediaColor.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), red, green, blue);
                }
            }
            return (MediaColor)MediaColorConverter.ConvertFromString(value);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private sealed record SavedBounds(double Left, double Top, double Width, double Height);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newLong);
}
