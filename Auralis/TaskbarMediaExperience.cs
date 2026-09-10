using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Auralis.Services;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace Auralis;

public sealed record TaskbarMediaOptions(
    int SchemaVersion = 0,
    bool Enabled = false,
    bool AutoShowOnPlayback = false,
    bool FlyoutEnabled = false,
    bool ShowOnTrackChange = false,
    bool ShowControls = true,
    bool ReduceMotion = false,
    string Theme = "light",
    string Accent = "#6B9DCA");

public sealed record TaskbarMediaMetadata(
    string Id,
    string Title,
    string Artist,
    string Album,
    string? ArtworkPath = null);

public enum TaskbarMediaCommand
{
    Previous,
    PlayPause,
    Next
}

/// <summary>
/// Owns Auralis' taskbar-aligned media surface.
/// It consumes the player's existing state directly; it never reads Auralis back through SMTC.
/// </summary>
public sealed class TaskbarMediaExperience : IDisposable
{
    private const int CurrentOptionsSchemaVersion = 3;
    private TaskbarMediaWidgetWindow _widget;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly TaskbarWidgetLease _widgetLease = new();
    private readonly TaskbarWidgetVisibilityPolicy _widgetVisibility = new();
    private TaskbarMediaOptions _options = new(Enabled: false);
    private TaskbarMediaMetadata? _metadata;
    private LyricsResponse? _lyrics;
    private bool _isPlaying;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _lastWidgetVisibility;
    private DateTime _nextRecoveryAttemptUtc = DateTime.MinValue;
    private int _recoveryFailureCount;
    private bool _disposed;

    public event Action<TaskbarMediaCommand>? CommandRequested;
    public event Action? OpenAuralisRequested;

    public TaskbarMediaExperience()
    {
        _widget = CreateWidget();
        _recoveryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => RecoverWidgetAndLeaseIfNeeded(),
            Dispatcher.CurrentDispatcher);
    }

    public void ApplyOptions(TaskbarMediaOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _options = options with
        {
            Enabled = options.SchemaVersion >= CurrentOptionsSchemaVersion && options.Enabled,
            Theme = string.Equals(options.Theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light",
            Accent = NormalizeAccent(options.Accent)
        };
        _widgetVisibility.ApplyOptions(_options.Enabled, _options.AutoShowOnPlayback);
        if (_options.Enabled)
        {
            _recoveryTimer.Start();
        }
        else
        {
            _recoveryTimer.Stop();
        }
        RecoverWidgetIfNeeded();
        _widget.ApplyOptions(_options);
        SynchronizeWidgetVisibility(force: true);
    }

    public void UpdateMetadata(TaskbarMediaMetadata metadata, bool isTrackChange = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _metadata = metadata;
        _widget.UpdateMetadata(metadata, isTrackChange);
    }

    public void UpdateArtwork(string trackId, string? artworkPath)
    {
        if (_disposed || _metadata is null ||
            !string.Equals(_metadata.Id, trackId, StringComparison.Ordinal))
        {
            return;
        }

        _metadata = _metadata with { ArtworkPath = artworkPath };
        _widget.UpdateMetadata(_metadata, isTrackChange: false);
    }

    public void UpdateLyrics(LyricsResponse? lyrics)
    {
        if (_disposed)
        {
            return;
        }

        _lyrics = lyrics;
        _widget.UpdateLyrics(lyrics);
    }

    public void UpdatePlayback(double positionSeconds, double durationSeconds, bool isPlaying)
    {
        if (_disposed)
        {
            return;
        }

        _isPlaying = isPlaying;
        _positionSeconds = Math.Max(0, positionSeconds);
        _durationSeconds = Math.Max(0, durationSeconds);
        _widgetVisibility.UpdatePlayback(isPlaying);
        _widget.UpdatePlayback(positionSeconds, durationSeconds, isPlaying);
        SynchronizeWidgetVisibility();
    }

    public void RefreshAfterHostWindowStateChanged()
    {
        if (_disposed || !_widgetVisibility.ShouldShow)
        {
            return;
        }

        // The taskbar surface is an independent top-level HWND. Reassert that relationship when
        // the main window is minimized/hidden because Windows can otherwise carry an implicit
        // owner relationship across a WPF visibility transition and hide both windows together.
        RecoverWidgetIfNeeded();
        SynchronizeWidgetVisibility(force: true);
        _widget.RefreshPlacement();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recoveryTimer.Stop();
        SafeCloseWidget(_widget);
        _widgetLease.Dispose();
    }

    private TaskbarMediaWidgetWindow CreateWidget()
    {
        var widget = new TaskbarMediaWidgetWindow();
        widget.OpenAuralisRequested += () => OpenAuralisRequested?.Invoke();
        widget.CommandRequested += command => CommandRequested?.Invoke(command);
        return widget;
    }

    private void RecoverWidgetIfNeeded()
    {
        if (_disposed || !ShouldShowWidget || !_widget.RequiresRecreation)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextRecoveryAttemptUtc)
        {
            return;
        }

        // A damaged HWND must not create an unbounded recreation loop on Explorer restarts or
        // display topology changes. Retry slowly and exponentially instead of on every playback tick.
        _nextRecoveryAttemptUtc = now.Add(TaskbarWidgetRecoveryBackoff.GetDelay(_recoveryFailureCount));
        TaskbarMediaWidgetWindow? replacement = null;
        try
        {
            var previous = _widget;
            replacement = CreateWidget();
            replacement.ApplyOptions(_options);
            if (_metadata is not null)
            {
                replacement.UpdateMetadata(_metadata, isTrackChange: false);
            }
            replacement.UpdateLyrics(_lyrics);
            replacement.UpdatePlayback(_positionSeconds, _durationSeconds, _isPlaying);
            replacement.SetEnabled(ShouldShowWidget);
            _widget = replacement;
            replacement = null;
            SafeCloseWidget(previous);
            _recoveryFailureCount = 0;
            _nextRecoveryAttemptUtc = DateTime.MinValue;
        }
        catch (Exception exception)
        {
            if (replacement is not null)
            {
                SafeCloseWidget(replacement);
            }
            _recoveryFailureCount++;
            Debug.WriteLine($"Auralis taskbar widget recovery was deferred: {exception}");
        }
    }

    private void RecoverWidgetAndLeaseIfNeeded()
    {
        if (_disposed)
        {
            return;
        }

        if (_widgetVisibility.ShouldShow && !_widgetLease.IsHeld && _widgetLease.TryAcquire())
        {
            _lastWidgetVisibility = false;
            SynchronizeWidgetVisibility(force: true);
        }
        RecoverWidgetIfNeeded();
    }

    private void SynchronizeWidgetVisibility(bool force = false)
    {
        var wantsWidget = _widgetVisibility.ShouldShow;
        if (!wantsWidget)
        {
            // Hide our HWND before releasing the cross-process lease so a waiting Auralis instance
            // can never overlap this widget during hand-off.
            if (force || _lastWidgetVisibility)
            {
                _widget.SetEnabled(false);
            }
            _lastWidgetVisibility = false;
            _widgetLease.Release();
            return;
        }

        _widgetLease.TryAcquire();
        var shouldShow = _widgetLease.IsHeld;
        if (!force && shouldShow == _lastWidgetVisibility)
        {
            return;
        }

        if (!shouldShow)
        {
            _widget.SetEnabled(false);
            _lastWidgetVisibility = false;
            return;
        }

        RecoverWidgetIfNeeded();
        _widget.SetEnabled(true);
        _lastWidgetVisibility = true;
    }

    private static void SafeCloseWidget(TaskbarMediaWidgetWindow widget)
    {
        try
        {
            widget.CloseFromOwner();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            Debug.WriteLine($"Auralis taskbar widget cleanup was safely degraded: {exception.Message}");
        }
    }

    private bool ShouldShowWidget => _widgetVisibility.ShouldShow && _widgetLease.IsHeld;

    private static string NormalizeAccent(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            value.Length == 7 && value[0] == '#' &&
            value.Skip(1).All(Uri.IsHexDigit))
        {
            return value;
        }

        return "#6B9DCA";
    }
}

internal sealed class TaskbarMediaWidgetWindow : Window
{
    private const double ExpandedLogicalWidth = 294;
    private const double CompactLogicalWidth = 202;
    private readonly Border _surface;
    private readonly Border _separator;
    private readonly Border _coverSurface;
    private readonly Image _cover;
    private TextBlock _title;
    private TextBlock _standbyTitle;
    private readonly Grid _lyricViewport;
    private readonly TextBlock _artist;
    private readonly StackPanel _controls;
    private readonly Button _playPause;
    private readonly DispatcherTimer _positionTimer;
    private readonly ScaleTransform _surfaceScale = new(1, 1);
    private readonly TranslateTransform _surfaceTranslate = new();
    private readonly ScaleTransform _coverScale = new(1, 1);
    private readonly RotateTransform _coverRotate = new();
    private readonly TaskbarWidgetUpdateGate _updateGate = new();
    private TaskbarMediaOptions _options = new(Enabled: false);
    private TaskbarMediaMetadata? _metadata;
    private LyricsResponse? _lyrics;
    private double _positionSeconds;
    private bool _isPlaying;
    private string? _renderedTrackId;
    private string? _renderedArtworkPath;
    private bool? _renderedIsPlaying;
    private string _displayedPrimaryText = "Auralis";
    private int _primaryAnimationRevision;
    private nint _handle;
    private nint _taskbarHandle;
    private bool _sourceInitialized;
    private bool _effectiveEnabled;
    private bool _attachedAndVisible;
    private bool _positionRequestPending;
    private bool _positioning;
    private bool _closed;
    private bool _ownerClosing;
    private bool _surfacePressed;
    private bool? _taskbarDark;

    public event Action? OpenAuralisRequested;
    public event Action<TaskbarMediaCommand>? CommandRequested;

    public TaskbarMediaWidgetWindow()
    {
        Title = "Auralis 任务栏音乐组件";
        Width = ExpandedLogicalWidth;
        Height = 40;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Keep the native surface off-screen until its first safe taskbar placement completes.
        Left = -32000;
        Top = -32000;

        _cover = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        _coverSurface = new Border
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(4, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
            ClipToBounds = true,
            Child = _cover
        };

        _title = CreatePrimaryTextBlock("Auralis", 1);
        _standbyTitle = CreatePrimaryTextBlock(string.Empty, 0);
        _lyricViewport = new Grid
        {
            Height = 18,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _lyricViewport.Children.Add(_title);
        _lyricViewport.Children.Add(_standbyTitle);
        _artist = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 10,
            Text = "等待播放",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = .72,
            VerticalAlignment = VerticalAlignment.Top
        };
        var text = new Grid { MinWidth = 92, MaxWidth = 132, Margin = new Thickness(0, 2, 6, 2) };
        text.RowDefinitions.Add(new RowDefinition());
        text.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_lyricViewport, 0);
        Grid.SetRow(_artist, 1);
        text.Children.Add(_lyricViewport);
        text.Children.Add(_artist);

        var previous = CreateIconButton("\uE892", "上一首");
        previous.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.Previous);
        _playPause = CreateIconButton("\uE768", "播放");
        _playPause.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.PlayPause);
        var next = CreateIconButton("\uE893", "下一首");
        next.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.Next);
        _controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        };
        _controls.Children.Add(previous);
        _controls.Children.Add(_playPause);
        _controls.Children.Add(next);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _separator = new Border
        {
            Width = 1,
            Height = 24,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetColumn(_coverSurface, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_controls, 2);
        Grid.SetColumn(_separator, 3);
        layout.Children.Add(_coverSurface);
        layout.Children.Add(text);
        layout.Children.Add(_controls);
        layout.Children.Add(_separator);

        _surface = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Child = layout
        };
        var surfaceTransform = new TransformGroup();
        surfaceTransform.Children.Add(_surfaceScale);
        surfaceTransform.Children.Add(_surfaceTranslate);
        _surface.RenderTransform = surfaceTransform;
        _surface.RenderTransformOrigin = new System.Windows.Point(.5, .5);
        var coverTransform = new TransformGroup();
        coverTransform.Children.Add(_coverScale);
        coverTransform.Children.Add(_coverRotate);
        _coverSurface.RenderTransform = coverTransform;
        _coverSurface.RenderTransformOrigin = new System.Windows.Point(.5, .5);
        Content = _surface;
        AutomationProperties.SetName(_surface, "Auralis 当前播放");

        _surface.MouseEnter += (_, _) => SetHover(true);
        _surface.MouseLeave += (_, _) =>
        {
            _surfacePressed = false;
            SetHover(false);
            AnimateSurfaceScale(1, 140);
        };
        _surface.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (!IsInsideButton(args.OriginalSource as DependencyObject))
            {
                _surfacePressed = true;
                SetHover(true);
                AnimateSurfaceScale(.982, 80);
            }
        };
        _surface.MouseLeftButtonUp += (_, args) =>
        {
            if (!IsInsideButton(args.OriginalSource as DependencyObject))
            {
                _surfacePressed = false;
                SetHover(true);
                AnimateSurfaceScale(1, 180);
                OpenAuralisRequested?.Invoke();
                args.Handled = true;
            }
        };

        // Display/taskbar messages trigger immediate placement. This timer is only a low-frequency
        // safety net for auto-hide and Explorer restarts, not a layout loop.
        _positionTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => AttachAndPosition(), Dispatcher);
        SourceInitialized += OnSourceInitialized;
        Closing += (_, args) =>
        {
            if (!_ownerClosing)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _effectiveEnabled = false;
            _attachedAndVisible = false;
            _handle = nint.Zero;
            _taskbarHandle = nint.Zero;
            _positionTimer.Stop();
        };
    }

    public bool RequiresRecreation =>
        _closed || (_sourceInitialized && (_handle == nint.Zero || !NativeMethods.IsWindow(_handle)));

    public void ApplyOptions(TaskbarMediaOptions options)
    {
        var reduceMotionChanged = _options.ReduceMotion != options.ReduceMotion;
        _options = options;
        _controls.Visibility = options.ShowControls ? Visibility.Visible : Visibility.Collapsed;
        Width = options.ShowControls ? ExpandedLogicalWidth : CompactLogicalWidth;
        if (reduceMotionChanged && options.ReduceMotion)
        {
            SuspendVisuals();
        }
        ApplyTaskbarPalette(force: true);
        if (IsVisible)
        {
            RequestPosition();
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_closed)
        {
            return;
        }

        if (!_updateGate.TryChangeEnabled(enabled))
        {
            return;
        }

        _effectiveEnabled = enabled;
        if (!enabled)
        {
            _positionTimer.Stop();
            _attachedAndVisible = false;
            _updateGate.ResetBounds();
            SuspendVisuals();
            if (IsVisible)
            {
                Hide();
            }
            return;
        }

        if (!IsVisible)
        {
            Show();
        }
        _positionTimer.Start();
        RequestPosition();
    }

    public void RefreshPlacement()
    {
        if (_closed || _ownerClosing)
        {
            return;
        }

        DetachFromImplicitOwner();
        if (_effectiveEnabled)
        {
            RequestPosition();
        }
    }

    public void UpdateMetadata(TaskbarMediaMetadata metadata, bool isTrackChange)
    {
        var changed = isTrackChange && !string.Equals(_metadata?.Id, metadata.Id, StringComparison.Ordinal);
        _metadata = metadata;
        if (!CanRender)
        {
            return;
        }

        RenderMetadata(changed);
    }

    private void RenderMetadata(bool isTrackChange)
    {
        if (_metadata is not { } metadata)
        {
            return;
        }

        var changed = isTrackChange || !string.Equals(_renderedTrackId, metadata.Id, StringComparison.Ordinal);
        var artworkChanged = !string.Equals(_renderedArtworkPath, metadata.ArtworkPath, StringComparison.OrdinalIgnoreCase);
        var songTitle = string.IsNullOrWhiteSpace(metadata.Title) ? "Auralis" : metadata.Title;
        var artist = string.IsNullOrWhiteSpace(metadata.Artist) ? "未知艺术家" : metadata.Artist;
        _artist.Text = $"{songTitle}  ·  {artist}";
        if (artworkChanged)
        {
            _cover.Source = LoadArtwork(metadata.ArtworkPath);
            _cover.Visibility = _cover.Source is null ? Visibility.Collapsed : Visibility.Visible;
            _renderedArtworkPath = metadata.ArtworkPath;
        }
        _renderedTrackId = metadata.Id;
        UpdateDisplayedPrimaryText(changed);
        AutomationProperties.SetName(_surface, $"{_displayedPrimaryText}，{songTitle}，{artist}");
        if (changed && IsVisible)
        {
            AnimateTrackChange();
        }
        else if (artworkChanged && IsVisible)
        {
            AnimateArtworkReveal();
        }
    }

    public void UpdateLyrics(LyricsResponse? lyrics)
    {
        _lyrics = lyrics;
        if (CanRender)
        {
            UpdateDisplayedPrimaryText(animate: true);
        }
    }

    public void UpdatePlayback(double positionSeconds, double durationSeconds, bool isPlaying)
    {
        _positionSeconds = Math.Max(0, positionSeconds);
        _isPlaying = isPlaying;
        if (!CanRender)
        {
            return;
        }

        RenderPlayback();
    }

    private void RenderPlayback()
    {
        var isPlaying = _isPlaying;
        var nextGlyph = isPlaying ? "\uE769" : "\uE768";
        if (_renderedIsPlaying != isPlaying || !Equals(_playPause.Content, nextGlyph))
        {
            _playPause.Content = nextGlyph;
            if (_renderedIsPlaying is not null && ShouldAnimate)
            {
                AnimateButtonPulse(_playPause);
            }
            _renderedIsPlaying = isPlaying;
            _playPause.ToolTip = isPlaying ? "暂停" : "播放";
            AutomationProperties.SetName(_playPause, isPlaying ? "暂停" : "播放");
        }
        UpdateDisplayedPrimaryText(animate: true);
    }

    private bool CanRender =>
        _effectiveEnabled && _attachedAndVisible && !_closed && IsVisible;

    private bool ShouldAnimate =>
        CanRender && !_options.ReduceMotion && SystemParameters.ClientAreaAnimation;

    private void RenderSnapshot()
    {
        RenderMetadata(isTrackChange: false);
        RenderPlayback();
    }

    private void SuspendVisuals()
    {
        _primaryAnimationRevision++;
        ResetPrimaryAnimation(_title);
        ResetPrimaryAnimation(_standbyTitle);
        _surface.BeginAnimation(OpacityProperty, null);
        _surface.Opacity = 1;
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _surfaceScale.ScaleX = 1;
        _surfaceScale.ScaleY = 1;
        _surfaceTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _surfaceTranslate.X = 0;
        _cover.BeginAnimation(OpacityProperty, null);
        _cover.Opacity = 1;
        _coverSurface.BeginAnimation(OpacityProperty, null);
        _coverSurface.Opacity = 1;
        _coverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _coverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _coverScale.ScaleX = 1;
        _coverScale.ScaleY = 1;
        _coverRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _coverRotate.Angle = 0;
        _artist.BeginAnimation(OpacityProperty, null);
        _artist.Opacity = .72;
        if (_artist.RenderTransform is TranslateTransform artistTransform)
        {
            artistTransform.BeginAnimation(TranslateTransform.XProperty, null);
            artistTransform.X = 0;
        }
        foreach (var button in _controls.Children.OfType<Button>())
        {
            if (button.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }
    }

    public TaskbarMediaAnchor? GetAnchor()
    {
        NativeMethods.Rect rect;
        if (!_attachedAndVisible || _handle == nint.Zero || !IsVisible ||
            !NativeMethods.IsWindow(_handle) || !NativeMethods.IsWindowVisible(_handle) ||
            _taskbarHandle == nint.Zero || !NativeMethods.IsWindow(_taskbarHandle) ||
            !NativeMethods.GetWindowRect(_handle, out rect))
        {
            return null;
        }

        var dpi = _taskbarHandle != nint.Zero ? NativeMethods.GetDpiForWindow(_taskbarHandle) : 96u;
        var work = NativeMethods.TryGetMonitorWorkArea(_taskbarHandle, out var workArea)
            ? workArea
            : rect;
        return new TaskbarMediaAnchor(
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            Math.Max(1, dpi / 96d),
            work.Left,
            work.Top,
            work.Right,
            work.Bottom);
    }

    public void CloseFromOwner()
    {
        if (_closed)
        {
            return;
        }

        _ownerClosing = true;
        _options = _options with { Enabled = false };
        _effectiveEnabled = false;
        _attachedAndVisible = false;
        _positionTimer.Stop();
        SuspendVisuals();
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            _closed = true;
            _handle = nint.Zero;
            _taskbarHandle = nint.Zero;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _sourceInitialized = true;
        _handle = new WindowInteropHelper(this).Handle;
        var extended = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        extended |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new nint(extended));
        DetachFromImplicitOwner();
        HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
        AttachAndPosition();
    }

    private void DetachFromImplicitOwner()
    {
        if (_handle != nint.Zero && NativeMethods.IsWindow(_handle))
        {
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlHwndParent, nint.Zero);
        }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcDestroy)
        {
            SuspendVisuals();
            _attachedAndVisible = false;
            _handle = nint.Zero;
            _taskbarHandle = nint.Zero;
        }
        else if (message is NativeMethods.WmDpiChanged or NativeMethods.WmDisplayChange)
        {
            _updateGate.ResetBounds();
            RequestPosition();
        }
        return nint.Zero;
    }

    private void RequestPosition()
    {
        if (_positionRequestPending || _ownerClosing || _closed)
        {
            return;
        }

        _positionRequestPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _positionRequestPending = false;
            AttachAndPosition();
        }, DispatcherPriority.Loaded);
    }

    private void AttachAndPosition()
    {
        if (_positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            var wasAttachedAndVisible = _attachedAndVisible;
            if (_ownerClosing || _closed || !_options.Enabled || !_effectiveEnabled || _handle == nint.Zero ||
                !NativeMethods.IsWindow(_handle))
            {
                _attachedAndVisible = false;
                return;
            }

            // The widget is now an Auralis-owned, no-activate overlay. Never place it over a real
            // fullscreen application, and never make Explorer own or destroy this HWND.
            if (NativeMethods.IsForegroundFullscreen())
            {
                HideNative(resetTaskbar: false);
                return;
            }

            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == nint.Zero || !NativeMethods.IsWindow(taskbar) ||
                !NativeMethods.IsWindowVisible(taskbar) ||
                !NativeMethods.GetWindowRect(taskbar, out var taskbarRect) ||
                NativeMethods.IsTaskbarAutoHidden() ||
                !NativeMethods.IsPrimaryBottomTaskbar(taskbar, taskbarRect))
            {
                HideNative(resetTaskbar: true);
                return;
            }

            var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
            var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            if (taskbarWidth <= taskbarHeight || taskbarHeight < 28)
            {
                // Fail safe on vertical, auto-hidden or malformed taskbars.
                HideNative(resetTaskbar: true);
                return;
            }

            _taskbarHandle = taskbar;
            ApplyTaskbarPalette(force: false);
            var dpiScale = Math.Max(1, NativeMethods.GetDpiForWindow(taskbar) / 96d);
            var height = Math.Min(taskbarHeight - 8, Math.Max(28, (int)Math.Round(Height * dpiScale)));
            var tray = NativeMethods.FindWindowEx(taskbar, nint.Zero, "TrayNotifyWnd", null);
            if (tray == nint.Zero || !NativeMethods.GetWindowRect(tray, out var trayRect))
            {
                HideNative(resetTaskbar: false);
                return;
            }

            var expandedWidth = Math.Max(120, (int)Math.Round((_options.ShowControls ? ExpandedLogicalWidth : CompactLogicalWidth) * dpiScale));
            var compactWidth = Math.Max(120, (int)Math.Round(CompactLogicalWidth * dpiScale));
            if (!TryGetAvailableTaskbarSpace(taskbarRect, trayRect, out var availableLeft, out var right))
            {
                HideNative(resetTaskbar: false);
                return;
            }

            var availableWidth = right - availableLeft;
            var compact = _options.ShowControls && availableWidth < expandedWidth;
            var width = compact ? compactWidth : expandedWidth;
            if (availableWidth < width || width > taskbarWidth - 16)
            {
                HideNative(resetTaskbar: false);
                return;
            }

            _controls.Visibility = _options.ShowControls && !compact ? Visibility.Visible : Visibility.Collapsed;
            Width = compact || !_options.ShowControls ? CompactLogicalWidth : ExpandedLogicalWidth;

            var top = taskbarRect.Top + Math.Max(0, (taskbarHeight - height) / 2);
            var targetBounds = new TaskbarWidgetBounds(
                right - width,
                top,
                right,
                top + height);
            var nativeVisible = NativeMethods.IsWindowVisible(_handle);
            var shouldApplyBounds = _updateGate.ShouldApplyBounds(targetBounds, nativeVisible);
            if (shouldApplyBounds)
            {
                if (!NativeMethods.SetWindowPos(
                        _handle,
                        nint.Zero,
                        targetBounds.Left,
                        targetBounds.Top,
                        width,
                        height,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpShowWindow))
                {
                    HideNative(resetTaskbar: false);
                    return;
                }

                // Keep the last actually applied native rectangle as the hysteresis baseline.
                // Updating it for a skipped 1–2 px jitter would allow repeated small changes to
                // accumulate forever without ever correcting the real HWND position.
                _updateGate.MarkBoundsApplied(targetBounds);
            }

            _attachedAndVisible = NativeMethods.IsWindowVisible(_handle);
            if (_attachedAndVisible && !wasAttachedAndVisible)
            {
                RenderSnapshot();
                AnimateAppearance();
            }
        }
        finally
        {
            _positioning = false;
        }
    }

    private void HideNative(bool resetTaskbar)
    {
        _attachedAndVisible = false;
        SuspendVisuals();
        if (_handle != nint.Zero && NativeMethods.IsWindow(_handle))
        {
            NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
        }
        if (resetTaskbar)
        {
            _taskbarHandle = nint.Zero;
        }
    }

    private static bool TryGetAvailableTaskbarSpace(
        NativeMethods.Rect taskbarRect,
        NativeMethods.Rect trayRect,
        out int availableLeft,
        out int availableRight)
    {
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        // Reserve the Start/task-button region without walking Explorer's cross-process UIA tree.
        // If the conservative free region is too narrow, placement safely compacts or hides.
        availableLeft = taskbarRect.Left + (int)Math.Ceiling(taskbarWidth * .58);
        availableRight = Math.Min(taskbarRect.Right - 4, trayRect.Left - 4);
        return taskbarWidth > 0 && availableRight > availableLeft;
    }

    private void SetHover(bool hover)
    {
        var dark = _taskbarDark ?? ResolveTaskbarDark();
        var target = !hover
            ? Colors.Transparent
            : dark
                ? (_surfacePressed ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(22, 255, 255, 255))
                : (_surfacePressed ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(17, 0, 0, 0));
        if (_surface.Background is not SolidColorBrush brush || brush.IsFrozen)
        {
            brush = new SolidColorBrush(Colors.Transparent);
            _surface.Background = brush;
        }
        if (ShouldAnimate)
        {
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(brush.Color, target, TimeSpan.FromMilliseconds(_surfacePressed ? 75 : 135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
        else
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
        brush.Color = target;
    }

    private void ApplyTaskbarPalette(bool force)
    {
        var dark = ResolveTaskbarDark();
        if (!force && _taskbarDark == dark)
        {
            return;
        }

        _taskbarDark = dark;
        var foreground = new SolidColorBrush(dark ? Color.FromRgb(242, 242, 242) : Color.FromRgb(32, 32, 32));
        _title.Foreground = foreground;
        _standbyTitle.Foreground = foreground;
        _artist.Foreground = new SolidColorBrush(dark ? Color.FromRgb(190, 190, 190) : Color.FromRgb(92, 92, 92));
        _separator.Background = new SolidColorBrush(dark
            ? Color.FromArgb(34, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0));
        _coverSurface.Background = new SolidColorBrush(dark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0));
        foreach (var button in _controls.Children.OfType<Button>())
        {
            button.Foreground = foreground;
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            button.Style = CreateTaskbarButtonStyle(dark);
        }
        SetHover(_surface.IsMouseOver);
    }

    private bool ResolveTaskbarDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                null);
            if (value is int systemUsesLightTheme)
            {
                return systemUsesLightTheme == 0;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fall through to the application theme when Explorer's preference is unavailable.
        }

        return string.Equals(_options.Theme, "dark", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDisplayedPrimaryText(bool animate)
    {
        animate = animate && ShouldAnimate;
        var fallback = string.IsNullOrWhiteSpace(_metadata?.Title) ? "Auralis" : _metadata.Title;
        var primary = TaskbarLyricProjection.ResolvePrimaryText(_lyrics, _positionSeconds, fallback);
        if (string.Equals(primary, _displayedPrimaryText, StringComparison.Ordinal))
        {
            return;
        }

        _displayedPrimaryText = primary;
        if (_metadata is not null)
        {
            AutomationProperties.SetName(
                _surface,
                $"{primary}，{_metadata.Title}，{_metadata.Artist}");
        }
        var outgoing = _title;
        var incoming = _standbyTitle;
        var revision = ++_primaryAnimationRevision;
        ResetPrimaryAnimation(outgoing);
        ResetPrimaryAnimation(incoming);
        incoming.Text = primary;
        incoming.Opacity = animate ? 0 : 1;
        outgoing.Opacity = animate ? 1 : 0;
        _title = incoming;
        _standbyTitle = outgoing;

        var incomingTransform = (TranslateTransform)incoming.RenderTransform;
        var outgoingTransform = (TranslateTransform)outgoing.RenderTransform;
        incomingTransform.Y = animate ? 6 : 0;
        outgoingTransform.Y = 0;
        if (!animate)
        {
            outgoing.Text = string.Empty;
            StartPrimaryMarquee(incoming, revision);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        outgoing.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(145)) { EasingFunction = ease });
        outgoingTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, -5, TimeSpan.FromMilliseconds(165)) { EasingFunction = ease });
        var incomingFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
        {
            BeginTime = TimeSpan.FromMilliseconds(35),
            EasingFunction = ease
        };
        incomingFade.Completed += (_, _) =>
        {
            if (revision != _primaryAnimationRevision)
            {
                return;
            }
            ResetPrimaryAnimation(outgoing);
            outgoing.Opacity = 0;
            outgoing.Text = string.Empty;
            ResetPrimaryAnimation(incoming);
            incoming.Opacity = 1;
            StartPrimaryMarquee(incoming, revision);
        };
        incoming.BeginAnimation(OpacityProperty, incomingFade);
        incomingTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(245))
            {
                BeginTime = TimeSpan.FromMilliseconds(20),
                EasingFunction = ease
            });
    }

    private void StartPrimaryMarquee(TextBlock text, int revision)
    {
        if (!ShouldAnimate)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (revision != _primaryAnimationRevision || !ReferenceEquals(text, _title) ||
                _lyricViewport.ActualWidth <= 1 || !ShouldAnimate)
            {
                return;
            }

            text.Measure(new System.Windows.Size(double.PositiveInfinity, _lyricViewport.ActualHeight));
            var overflow = text.DesiredSize.Width - _lyricViewport.ActualWidth;
            if (overflow <= 4)
            {
                return;
            }

            var transform = (TranslateTransform)text.RenderTransform;
            var duration = TimeSpan.FromMilliseconds(Math.Clamp((overflow + 12) / 28 * 1000, 2200, 7200));
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(0, -(overflow + 10), duration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(650),
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(1),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }, DispatcherPriority.Loaded);
    }

    private static void ResetPrimaryAnimation(TextBlock text)
    {
        text.BeginAnimation(OpacityProperty, null);
        if (text.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;
        }
    }

    private void AnimateAppearance()
    {
        if (!ShouldAnimate)
        {
            return;
        }

        _surface.BeginAnimation(OpacityProperty, null);
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _surfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _surfaceTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        _surface.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _surfaceScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        _surfaceScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        _surfaceTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    private void AnimateTrackChange()
    {
        if (!ShouldAnimate)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _coverSurface.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        _coverScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(.78, 1, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease });
        _coverScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(.78, 1, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease });
        _coverRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(-7, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });

        var artistTransform = _artist.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _artist.RenderTransform = artistTransform;
        _artist.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, .72, TimeSpan.FromMilliseconds(245))
            {
                BeginTime = TimeSpan.FromMilliseconds(55),
                EasingFunction = ease
            });
        artistTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(290)) { EasingFunction = ease });
    }

    private void AnimateArtworkReveal()
    {
        if (!ShouldAnimate)
        {
            return;
        }

        var ease = new BackEase { Amplitude = .18, EasingMode = EasingMode.EaseOut };
        _cover.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        _coverScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(.86, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        _coverScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(.86, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private void AnimateSurfaceScale(double target, int milliseconds)
    {
        if (!ShouldAnimate)
        {
            _surfaceScale.ScaleX = target;
            _surfaceScale.ScaleY = target;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _surfaceScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_surfaceScale.ScaleX, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        _surfaceScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_surfaceScale.ScaleY, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        _surfaceScale.ScaleX = target;
        _surfaceScale.ScaleY = target;
    }

    private static TextBlock CreatePrimaryTextBlock(string text, double opacity)
    {
        return new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = opacity,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform()
        };
    }

    private Button CreateIconButton(string glyph, string name)
    {
        var button = new Button
        {
            Width = 28,
            Height = 30,
            Margin = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0),
            Content = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            ToolTip = name,
            Cursor = Cursors.Arrow,
            RenderTransformOrigin = new System.Windows.Point(.5, .5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        button.PreviewMouseLeftButtonDown += (_, _) => AnimateButtonScale(button, ShouldAnimate ? .84 : 1, 75);
        button.PreviewMouseLeftButtonUp += (_, _) => AnimateButtonScale(button, 1, 150);
        button.LostMouseCapture += (_, _) => AnimateButtonScale(button, 1, 150);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static void AnimateButtonPulse(Button button)
    {
        if (button.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            button.RenderTransform = scale;
            button.RenderTransformOrigin = new System.Windows.Point(.5, .5);
        }
        var ease = new BackEase { Amplitude = .22, EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private void AnimateButtonScale(Button button, double target, int milliseconds)
    {
        if (button.RenderTransform is not ScaleTransform scale)
        {
            return;
        }
        if (!ShouldAnimate)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = target;
            scale.ScaleY = target;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale.ScaleX, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale.ScaleY, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        scale.ScaleX = target;
        scale.ScaleY = target;
    }

    private static Style CreateTaskbarButtonStyle(bool dark)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new SolidColorBrush(dark
            ? Color.FromArgb(28, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0))));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new SolidColorBrush(dark
            ? Color.FromArgb(42, 255, 255, 255)
            : Color.FromArgb(30, 0, 0, 0))));
        style.Triggers.Add(pressed);
        return style;
    }

    private void RaiseCommand(RoutedEventArgs args, TaskbarMediaCommand command)
    {
        args.Handled = true;
        CommandRequested?.Invoke(command);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        return LogicalTreeHelper.GetParent(child);
    }

    private static ImageSource? LoadArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 192;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static SolidColorBrush BrushFrom(string value, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { return new SolidColorBrush(fallback); }
    }
}

internal sealed class TaskbarMediaFlyoutWindow : Window
{
    private readonly Border _surface;
    private readonly Border _coverSurface;
    private readonly Image _cover;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _elapsed;
    private readonly TextBlock _duration;
    private readonly Button _playPause;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _open;
    private readonly Slider _seek;
    private readonly DispatcherTimer _hideTimer;
    private TaskbarMediaOptions _options = new(Enabled: false);
    private bool _ownerClosing;
    private bool _seeking;
    private bool _pointerEntered;
    private nint _handle;

    public event Action<TaskbarMediaCommand>? CommandRequested;
    public event Action<double>? SeekRequested;
    public event Action? OpenAuralisRequested;

    public TaskbarMediaFlyoutWindow()
    {
        Title = "Auralis 音乐弹窗";
        Width = 390;
        Height = 174;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;

        _cover = new Image { Stretch = Stretch.UniformToFill };
        _coverSurface = new Border
        {
            Width = 104,
            Height = 104,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromRgb(107, 157, 202)),
            ClipToBounds = true,
            Child = _cover
        };

        _title = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Microsoft YaHei UI"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Text = "Auralis",
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _artist = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 12,
            Text = "等待播放",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = .68
        };

        _previous = CreateFlyoutButton("\uE892", "上一首", 34);
        _previous.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.Previous);
        _playPause = CreateFlyoutButton("\uE768", "播放", 42);
        _playPause.FontSize = 16;
        _playPause.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.PlayPause);
        _next = CreateFlyoutButton("\uE893", "下一首", 34);
        _next.Click += (_, args) => RaiseCommand(args, TaskbarMediaCommand.Next);
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        controls.Children.Add(_previous);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);

        _open = CreateFlyoutButton("\uE8A7", "打开 Auralis", 34);
        _open.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        _open.VerticalAlignment = VerticalAlignment.Top;
        _open.Click += (_, args) =>
        {
            args.Handled = true;
            OpenAuralisRequested?.Invoke();
            HideAnimated();
        };

        var information = new Grid { Margin = new Thickness(15, 1, 0, 0) };
        information.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        information.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        information.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_title, 0);
        Grid.SetRow(_artist, 1);
        Grid.SetRow(controls, 2);
        information.Children.Add(_title);
        information.Children.Add(_artist);
        information.Children.Add(controls);
        information.Children.Add(_open);

        var media = new Grid { Margin = new Thickness(14, 14, 14, 7) };
        media.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        media.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_coverSurface, 0);
        Grid.SetColumn(information, 1);
        media.Children.Add(_coverSurface);
        media.Children.Add(information);

        _seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 20,
            Margin = new Thickness(14, 0, 14, 0),
            IsMoveToPointEnabled = true
        };
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            SeekRequested?.Invoke(_seek.Value);
            ScheduleHide();
        };
        _seek.LostMouseCapture += (_, _) => _seeking = false;
        _elapsed = CreateTimeText("0:00", TextAlignment.Left);
        _duration = CreateTimeText("0:00", TextAlignment.Right);
        var times = new Grid { Margin = new Thickness(15, -4, 15, 8) };
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_elapsed, 0);
        Grid.SetColumn(_duration, 1);
        times.Children.Add(_elapsed);
        times.Children.Add(_duration);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(media, 0);
        Grid.SetRow(_seek, 1);
        Grid.SetRow(times, 2);
        root.Children.Add(media);
        root.Children.Add(_seek);
        root.Children.Add(times);

        _surface = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Child = root,
            Effect = new DropShadowEffect
            {
                BlurRadius = 34,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = .28,
                Color = Colors.Black
            }
        };
        Content = _surface;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4500) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HideAnimated();
        };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var extended = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            extended |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new nint(extended));
            var corner = 2;
            NativeMethods.DwmSetWindowAttribute(_handle, 33, ref corner, sizeof(int));
        };
        Closing += (_, args) =>
        {
            if (!_ownerClosing)
            {
                args.Cancel = true;
                HideImmediately();
            }
        };
        MouseEnter += (_, _) =>
        {
            _pointerEntered = true;
            _hideTimer.Stop();
        };
        MouseLeave += (_, _) =>
        {
            if (_pointerEntered)
            {
                _pointerEntered = false;
                ScheduleHide(1100);
            }
        };
        ApplyOptions(_options);
    }

    public void ApplyOptions(TaskbarMediaOptions options)
    {
        _options = options;
        var dark = options.Theme == "dark";
        var foreground = dark ? Color.FromRgb(247, 247, 247) : Color.FromRgb(30, 30, 30);
        var secondary = dark ? Color.FromRgb(205, 205, 205) : Color.FromRgb(92, 92, 92);
        _surface.Background = new SolidColorBrush(dark ? Color.FromArgb(248, 32, 32, 32) : Color.FromArgb(250, 249, 249, 249));
        _surface.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
        _title.Foreground = new SolidColorBrush(foreground);
        _artist.Foreground = new SolidColorBrush(secondary);
        _elapsed.Foreground = new SolidColorBrush(secondary);
        _duration.Foreground = new SolidColorBrush(secondary);
        foreach (var button in new[] { _previous, _playPause, _next, _open })
        {
            button.Foreground = new SolidColorBrush(foreground);
        }
        var accent = BrushFrom(options.Accent, Color.FromRgb(107, 157, 202));
        _coverSurface.Background = accent;
        _playPause.Background = accent;
        var darkMode = dark ? 1 : 0;
        if (_handle != nint.Zero)
        {
            NativeMethods.DwmSetWindowAttribute(_handle, 20, ref darkMode, sizeof(int));
        }
    }

    public void UpdateMetadata(TaskbarMediaMetadata metadata)
    {
        _title.Text = string.IsNullOrWhiteSpace(metadata.Title) ? "Auralis" : metadata.Title;
        _artist.Text = string.IsNullOrWhiteSpace(metadata.Artist) ? "未知艺术家" : metadata.Artist;
        _cover.Source = LoadArtwork(metadata.ArtworkPath);
        _cover.Visibility = _cover.Source is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_surface, $"{_title.Text}，{_artist.Text}");
    }

    public void UpdatePlayback(double positionSeconds, double durationSeconds, bool isPlaying)
    {
        var duration = Math.Max(0, durationSeconds);
        _seek.Maximum = Math.Max(1, duration);
        if (!_seeking)
        {
            _seek.Value = Math.Clamp(positionSeconds, 0, _seek.Maximum);
        }
        _elapsed.Text = FormatTime(positionSeconds);
        _duration.Text = FormatTime(duration);
        _playPause.Content = isPlaying ? "\uE769" : "\uE768";
        _playPause.ToolTip = isPlaying ? "暂停" : "播放";
        AutomationProperties.SetName(_playPause, isPlaying ? "暂停" : "播放");
    }

    public void ToggleNear(TaskbarMediaAnchor? anchor)
    {
        if (IsVisible && Opacity > .1)
        {
            HideAnimated();
        }
        else
        {
            ShowNear(anchor, automatic: false);
        }
    }

    public void ShowNear(TaskbarMediaAnchor? anchor, bool automatic)
    {
        if (!_options.Enabled || !_options.FlyoutEnabled || anchor is null ||
            (automatic && NativeMethods.IsForegroundFullscreen()))
        {
            return;
        }

        var scale = Math.Max(1, anchor.DpiScale);
        var workLeft = anchor.WorkLeft / scale;
        var workTop = anchor.WorkTop / scale;
        var workRight = anchor.WorkRight / scale;
        var workBottom = anchor.WorkBottom / scale;
        var targetLeft = Math.Clamp(anchor.Right / scale - Width, workLeft + 8, workRight - Width - 8);
        var targetTop = anchor.Top / scale - Height - 11;
        if (targetTop < workTop + 8)
        {
            targetTop = anchor.Bottom / scale + 11;
        }
        targetTop = Math.Clamp(targetTop, workTop + 8, workBottom - Height - 8);

        Left = targetLeft;
        Top = targetTop + 10;
        Opacity = 0;
        _pointerEntered = false;
        if (!IsVisible)
        {
            Show();
        }
        if (_handle != nint.Zero)
        {
            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        ScheduleHide(automatic ? 10000 : 12000);
    }

    public void HideImmediately()
    {
        _hideTimer.Stop();
        _seeking = false;
        _pointerEntered = false;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(TopProperty, null);
        Opacity = 0;
        if (IsVisible)
        {
            Hide();
        }
    }

    public void CloseFromOwner()
    {
        _ownerClosing = true;
        _hideTimer.Stop();
        Close();
    }

    private void HideAnimated()
    {
        if (!IsVisible)
        {
            return;
        }
        _hideTimer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => HideImmediately();
        BeginAnimation(OpacityProperty, fade);
        BeginAnimation(TopProperty, new DoubleAnimation(Top, Top + 7, TimeSpan.FromMilliseconds(170)));
    }

    private void ScheduleHide(int milliseconds = 4500)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _hideTimer.Start();
    }

    private void RaiseCommand(RoutedEventArgs args, TaskbarMediaCommand command)
    {
        args.Handled = true;
        CommandRequested?.Invoke(command);
        ScheduleHide();
    }

    private static Button CreateFlyoutButton(string glyph, string name, double size)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0),
            Content = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            ToolTip = name,
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static TextBlock CreateTimeText(string value, TextAlignment alignment) => new()
    {
        Text = value,
        FontFamily = new FontFamily("Segoe UI Variable Text"),
        FontSize = 10.5,
        TextAlignment = alignment
    };

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static ImageSource? LoadArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 384;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static SolidColorBrush BrushFrom(string value, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { return new SolidColorBrush(fallback); }
    }
}

internal sealed record TaskbarMediaAnchor(
    double Left,
    double Top,
    double Right,
    double Bottom,
    double DpiScale,
    double WorkLeft,
    double WorkTop,
    double WorkRight,
    double WorkBottom);

internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int GwlHwndParent = -8;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    private const long WsCaption = 0x00C00000L;
    private const int SwShowMaximized = 3;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmNcDestroy = 0x0082;
    internal const int WmDpiChanged = 0x02E0;
    internal const int SwHide = 0;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal static readonly nint HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal int Length;
        internal int Flags;
        internal int ShowCommand;
        internal Point MinPosition;
        internal Point MaxPosition;
        internal Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        internal int Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal Rect Rectangle;
        internal nint Parameter;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(nint window, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(nint window, int attribute, out int value, int size);

    internal static bool TryGetMonitorWorkArea(nint window, out Rect workArea)
    {
        workArea = default;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = info.Work;
        return workArea.Right > workArea.Left && workArea.Bottom > workArea.Top;
    }

    internal static bool IsTaskbarAutoHidden()
    {
        const uint AbmGetState = 0x00000004;
        const uint AbsAutoHide = 0x00000001;
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        return (SHAppBarMessage(AbmGetState, ref data) & AbsAutoHide) != 0;
    }

    internal static bool IsPrimaryBottomTaskbar(nint taskbar, Rect taskbarRect)
    {
        const uint MonitorInfoPrimary = 0x00000001;
        var monitor = MonitorFromWindow(taskbar, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info) ||
            (info.Flags & MonitorInfoPrimary) == 0)
        {
            return false;
        }

        var tolerance = Math.Max(3, (int)Math.Ceiling(GetDpiForWindow(taskbar) / 48d));
        return Math.Abs(taskbarRect.Left - info.Monitor.Left) <= tolerance &&
               Math.Abs(taskbarRect.Right - info.Monitor.Right) <= tolerance &&
               Math.Abs(taskbarRect.Bottom - info.Monitor.Bottom) <= tolerance &&
               taskbarRect.Top > info.Monitor.Top + tolerance;
    }

    internal static bool IsForegroundFullscreen()
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero || foreground == GetShellWindow() || !IsWindowVisible(foreground))
        {
            return false;
        }

        var classNameBuffer = new StringBuilder(64);
        _ = GetClassName(foreground, classNameBuffer, classNameBuffer.Capacity);
        var className = classNameBuffer.ToString();
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (DwmGetWindowAttributeInt(foreground, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        if (DwmGetWindowAttributeRect(
                foreground,
                DwmwaExtendedFrameBounds,
                out var windowRect,
                Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(foreground, out windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var tolerance = Math.Max(2, (int)Math.Ceiling(GetDpiForWindow(foreground) / 48d));
        var coversMonitor = windowRect.Left <= info.Monitor.Left + tolerance &&
                            windowRect.Top <= info.Monitor.Top + tolerance &&
                            windowRect.Right >= info.Monitor.Right - tolerance &&
                            windowRect.Bottom >= info.Monitor.Bottom - tolerance;
        if (!coversMonitor)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId)
        {
            return true;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        var style = GetWindowLongPtr(foreground, GwlStyle).ToInt64();
        return !GetWindowPlacement(foreground, ref placement) ||
               placement.ShowCommand != SwShowMaximized ||
               (style & WsCaption) != WsCaption;
    }
}
