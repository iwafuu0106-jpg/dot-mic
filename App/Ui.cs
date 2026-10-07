using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace DotMic;

// Minimal navy tokens. See DESIGN.md. Shared brushes update open surfaces in high contrast.
internal static class Ui
{
    private static readonly AccessibilitySettings Accessibility = new();
    internal static bool HighContrast => Accessibility.HighContrast;
    internal static SolidColorBrush Primary { get; } = new();
    internal static SolidColorBrush Secondary { get; } = new();
    internal static SolidColorBrush Dots { get; } = new();
    internal static SolidColorBrush Background { get; } = new();
    internal static SolidColorBrush Panel { get; } = new();
    internal static SolidColorBrush Raised { get; } = new();
    internal static SolidColorBrush Accent { get; } = new();
    internal static SolidColorBrush AccentSoft { get; } = new();
    internal static SolidColorBrush Warning { get; } = new();
    internal static FontFamily Mono { get; } = new("Consolas");
    static Ui() => RefreshPalette();
    internal static void RefreshPalette()
    {
        void Set(SolidColorBrush target, uint hex, string system)
        {
            target.Color = HighContrast && Application.Current.Resources.TryGetValue(system, out var brush) && brush is SolidColorBrush value
                ? value.Color : Windows.UI.Color.FromArgb(255, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        }
        const string foreground = "SystemControlForegroundBaseHighBrush", background = "SystemControlBackgroundAltHighBrush";
        Set(Primary, 0xE8ECF4, foreground); Set(Secondary, 0x929BAE, foreground);
        Set(Dots, 0x293247, foreground); Set(Background, 0x0D1321, background);
        Set(Panel, 0x111A2B, background); Set(Raised, 0x192338, background);
        Set(Accent, 0xE8ECF4, foreground); Set(AccentSoft, 0x293247, background);
        Set(Warning, 0xE97982, foreground);
    }
    internal static TextBlock Label(string text, double size = 12) => new()
    { Text = text, FontSize = size, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    internal static void InstallResources()
    {
        void Assign(SolidColorBrush brush, params string[] keys) { foreach (string key in keys) Application.Current.Resources[key] = brush; }
        Assign(Panel, "ComboBoxBackground", "TextControlBackground", "TextControlBackgroundFocused", "NumberBoxSpinButtonBackground", "FlyoutBackground", "ContentDialogBackground", "ContentDialogTopOverlay");
        Assign(Raised, "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "TextControlBackgroundPointerOver", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed");
        Assign(Dots, "ComboBoxBorderBrush", "TextControlBorderBrush", "ButtonBorderBrushPointerOver", "ContentDialogBorderBrush", "FlyoutBorderBrush");
        Assign(Primary, "ComboBoxForeground", "ComboBoxForegroundPointerOver", "TextControlForeground", "TextControlForegroundFocused", "ButtonForegroundPointerOver", "ButtonForegroundPressed");
        Assign(Accent, "TextControlBorderBrushFocused", "SystemControlHighlightAccentBrush", "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed");
        Assign(Background, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
    }
    internal static TextBlock Eyebrow(string text)
    { var label = Label(text, 10); label.CharacterSpacing = 160; label.FontFamily = Mono; return label; }
    internal static Button Button(object content, string name)
    {
        var b = new Button { Content = content, FontSize = 12, Foreground = Primary, Background = Raised, BorderBrush = Dots,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), MinWidth = 32, MinHeight = 32,
            Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(b, name); return b;
    }
    internal static Ellipse Dot(double size = 3) => new() { Width = size, Height = size, Fill = Dots, VerticalAlignment = VerticalAlignment.Center };
    internal static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size, Foreground = Primary, IsHitTestVisible = false };
    internal static Grid Mark(double size = 28)
    {
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Stroke = Accent, StrokeThickness = 1.5 });
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (double height in new[] { 5d, 13d, 9d }) bars.Children.Add(new Rectangle { Width = 2, Height = height, RadiusX = 1, RadiusY = 1, Fill = Accent, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(bars); return grid;
    }
}
