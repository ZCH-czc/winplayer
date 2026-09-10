using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using SystemColors = System.Windows.SystemColors;

namespace Auralis;

// Application-owned login chrome only. Never styles or injects into a provider's webpage.
internal static class LoginSurfaceStyle
{
    internal static void ApplyAction(Button button, bool dark)
    {
        button.MinHeight = 40;
        button.Padding = new Thickness(16, 8, 16, 8);
        button.Margin = new Thickness(8, 4, 18, 12);
        button.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        button.FontSize = 13;
        button.Background = Brush(dark, 46, 50, 56, 255, 255, 255);
        button.Foreground = Brush(dark, 255, 255, 255, 32, 32, 32);
        button.BorderBrush = Brush(dark, 72, 78, 85, 214, 217, 221);
        button.BorderThickness = new Thickness(1);
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ActionSurface";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty,
                     Border.BorderThicknessProperty, Border.PaddingProperty })
            border.SetBinding(property, new System.Windows.Data.Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        AddTrigger(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, Brush(dark, 60, 66, 74, 238, 243, 248), "ActionSurface");
        AddTrigger(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, Brush(dark, 48, 58, 68, 226, 235, 244), "ActionSurface");
        AddTrigger(UIElement.IsKeyboardFocusedProperty, true, Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(95, 155, 202)), "ActionSurface");
        AddTrigger(UIElement.IsEnabledProperty, false, UIElement.OpacityProperty, .55);
        button.Template = template;
        if (SystemParameters.HighContrast)
        {
            // Let Windows own high-contrast focus, hover and disabled states.
            button.ClearValue(Control.TemplateProperty);
            button.Background = SystemColors.ControlBrush;
            button.Foreground = SystemColors.ControlTextBrush;
            button.BorderBrush = SystemColors.ControlTextBrush;
        }
        void AddTrigger(DependencyProperty property, object value, DependencyProperty target, object result, string? targetName = null)
        {
            var trigger = new Trigger { Property = property, Value = value };
            trigger.Setters.Add(new Setter(target, result) { TargetName = targetName });
            template.Triggers.Add(trigger);
        }
    }

    private static SolidColorBrush Brush(bool dark, byte dr, byte dg, byte db, byte lr, byte lg, byte lb) =>
        new(dark ? Color.FromRgb(dr, dg, db) : Color.FromRgb(lr, lg, lb));
}
