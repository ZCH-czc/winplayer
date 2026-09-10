using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Auralis.Localization;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Auralis;

/// <summary>
/// Small Fluent-styled replacement for the legacy WinForms ContextMenuStrip used by NotifyIcon.
/// It remains an Auralis-owned top-level tool window and never attaches to Explorer.
/// </summary>
internal sealed class TrayContextMenuWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private readonly Border _surface;
    private readonly TextBlock _showLabel;
    private readonly TextBlock _exitLabel;
    private readonly Button _showButton;
    private readonly Button _exitButton;
    private readonly List<Button> _buttons = [];
    private readonly ScaleTransform _scale = new(.96, .96);
    private readonly TranslateTransform _translate = new(0, 6);
    private bool _closingFromApp;
    private nint _handle;

    public event Action? ShowAuralisRequested;
    public event Action? ExitRequested;

    public TrayContextMenuWindow()
    {
        Title = "Auralis";
        Width = 228;
        Height = 118;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        _showLabel = new TextBlock();
        _exitLabel = new TextBlock();
        var panel = new StackPanel { Margin = new Thickness(6) };
        _showButton = CreateMenuButton("\uE8A7", _showLabel, () => ShowAuralisRequested?.Invoke(), isPrimary: true);
        panel.Children.Add(_showButton);
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128))
        });
        _exitButton = CreateMenuButton("\uE7E8", _exitLabel, () => ExitRequested?.Invoke(), isPrimary: false);
        panel.Children.Add(_exitButton);

        _surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Child = panel,
            RenderTransformOrigin = new Point(.88, .92),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 7,
                Direction = 270,
                Opacity = .24,
                Color = Colors.Black
            }
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_translate);
        _surface.RenderTransform = transforms;
        Content = _surface;

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64() | WsExToolWindow;
            SetWindowLongPtr(_handle, GwlExStyle, new nint(style));
            var corner = 2;
            _ = DwmSetWindowAttribute(_handle, 33, ref corner, sizeof(int));
        };
        Deactivated += (_, _) => HideImmediately();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                HideImmediately();
            }
        };
        Closing += (_, args) =>
        {
            if (!_closingFromApp)
            {
                args.Cancel = true;
                HideImmediately();
            }
        };
        UpdateLanguage("zh-CN");
        ApplyTheme(false);
    }

    public void UpdateLanguage(string language)
    {
        _showLabel.Text = NativeText.Get(language, "tray.show");
        _exitLabel.Text = NativeText.Get(language, "tray.exit");
        AutomationProperties.SetName(_showButton, _showLabel.Text);
        AutomationProperties.SetName(_exitButton, _exitLabel.Text);
    }

    public void ApplyTheme(bool dark)
    {
        var foreground = dark ? Color.FromRgb(249, 249, 249) : Color.FromRgb(31, 31, 31);
        _surface.Background = new SolidColorBrush(dark
            ? Color.FromArgb(250, 38, 38, 38)
            : Color.FromArgb(252, 252, 252, 252));
        _surface.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(38, 255, 255, 255)
            : Color.FromArgb(30, 0, 0, 0));
        _showLabel.Foreground = new SolidColorBrush(foreground);
        _exitLabel.Foreground = new SolidColorBrush(foreground);
        foreach (var button in _buttons)
        {
            button.Foreground = new SolidColorBrush(foreground);
        }
        if (_handle != nint.Zero)
        {
            var darkMode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(_handle, 20, ref darkMode, sizeof(int));
        }
    }

    public void ShowAtCursor()
    {
        Opacity = 0;
        _scale.ScaleX = .96;
        _scale.ScaleY = .96;
        _translate.Y = 6;
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionNearCursor();
        Activate();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            _scale.ScaleX = 1;
            _scale.ScaleY = 1;
            _translate.Y = 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    public void CloseFromApp()
    {
        _closingFromApp = true;
        Close();
    }

    private Button CreateMenuButton(string glyph, TextBlock label, Action action, bool isPrimary)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        label.FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        label.FontSize = 13;
        label.FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal;
        var icon = new TextBlock
        {
            Text = glyph,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
            TextAlignment = TextAlignment.Center
        };
        icon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
        });
        var layout = new Grid { Margin = new Thickness(8, 0, 10, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        layout.Children.Add(icon);
        layout.Children.Add(label);

        var button = new Button
        {
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = WpfHorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Content = layout,
            Cursor = Cursors.Hand,
            Template = CreateButtonTemplate()
        };
        button.Click += (_, _) =>
        {
            HideImmediately();
            action();
        };
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(22, 128, 128, 128));
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.PreviewMouseLeftButtonDown += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128));
        AutomationProperties.SetName(button, label.Text);
        _buttons.Add(button);
        return button;
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void PositionNearCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        var dpiScale = _handle == nint.Zero ? 1d : Math.Max(1d, GetDpiForWindow(_handle) / 96d);
        var work = screen.WorkingArea;
        var left = Math.Clamp(cursor.X / dpiScale - ActualWidth + 12, work.Left / dpiScale + 8, work.Right / dpiScale - ActualWidth - 8);
        var top = Math.Clamp(cursor.Y / dpiScale - ActualHeight - 8, work.Top / dpiScale + 8, work.Bottom / dpiScale - ActualHeight - 8);
        Left = left;
        Top = top;
    }

    private void HideImmediately()
    {
        BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _translate.BeginAnimation(TranslateTransform.YProperty, null);
        if (IsVisible)
        {
            Hide();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
