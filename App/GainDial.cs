using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DotMic;

internal sealed class GainSlider : Slider
{
    private bool dragging;
    private readonly RotaryGesture gesture = new();
    internal bool Dragging => dragging;
    internal GainSlider()
    {
        Minimum = -12; Maximum = 36; StepFrequency = .1; SmallChange = .5; LargeChange = 1;
        AutomationProperties.SetName(this, "マイクゲイン (dB)");
        AutomationProperties.SetAutomationId(this, "GainSlider");
        Template = (ControlTemplate)XamlReader.Load("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Slider">
          <Grid Background="Transparent"><VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="FocusStates" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><VisualState x:Name="Unfocused"/><VisualState x:Name="PointerFocused"/><VisualState x:Name="Focused"><Storyboard><DoubleAnimation Storyboard.TargetName="FocusRing" Storyboard.TargetProperty="Opacity" Duration="0" To="1" /></Storyboard></VisualState></VisualStateGroup></VisualStateManager.VisualStateGroups><Border x:Name="FocusRing" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" BorderBrush="{ThemeResource SystemControlHighlightAccentBrush}" BorderThickness="2" Opacity="0" CornerRadius="999" /></Grid>
        </ControlTemplate>
        """);
    }
    internal static bool Shift => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
    private static bool Ctrl => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        if (Ctrl) { Value = 0; e.Handled = true; return; }
        var point = e.GetCurrentPoint(this).Position;
        gesture.Begin(point.X - ActualWidth / 2, point.Y - ActualHeight / 2, Value, Shift);
        dragging = CapturePointer(e.Pointer); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        var point = e.GetCurrentPoint(this).Position;
        Value = gesture.Move(point.X - ActualWidth / 2, point.Y - ActualHeight / 2, Shift);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e) { dragging = false; ReleasePointerCapture(e.Pointer); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    { Value = Math.Clamp(Math.Round(Value + e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120d * (Shift ? .1 : 1), 1), Minimum, Maximum); e.Handled = true; }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Home) { Value = 0; e.Handled = true; }
        else if (e.Key is VirtualKey.Up or VirtualKey.Right or VirtualKey.Down or VirtualKey.Left)
        { Value = Math.Clamp(Math.Round(Value + (e.Key is VirtualKey.Up or VirtualKey.Right ? 1 : -1) * (Shift ? .1 : .5), 1), Minimum, Maximum); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}

internal sealed class GainDial : Grid
{
    internal readonly GainSlider Slider = new();
    internal readonly Canvas Plate = new() { IsHitTestVisible = false };
    private readonly Path digits = new() { IsHitTestVisible = false, Fill = Ui.Primary, Stretch = Stretch.None };
    private readonly Path previousDigits = new() { IsHitTestVisible = false, Fill = Ui.Primary, Stretch = Stretch.None };
    private readonly Button number;
    private readonly TextBox editor = new() { Visibility = Visibility.Collapsed, MinWidth = 80, TextAlignment = TextAlignment.Center, MaxLength = 12 };
    private readonly Ellipse positionDot;
    private readonly bool compact;
    private readonly AudioViewModel model;
    private bool updating;
    private double editingValue;
    private readonly double diameter;
    private readonly MotionHub motion;
    private readonly MotionHub.Region numeric;
    private readonly MotionHub.Region previousNumeric;
    private readonly MotionHub.Region detent;
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ['6'] = ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
        ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
        ['+'] = ["00000", "00100", "00100", "11111", "00100", "00100", "00000"],
        ['.'] = ["00000", "00000", "00000", "00000", "00000", "00110", "00110"]
    };
    internal GainDial(AudioViewModel vm, bool compact, MotionHub motion)
    {
        model = vm; this.motion = motion; this.compact = compact; diameter = compact ? 144 : 176; Width = Height = diameter;
        var face = new Ellipse { Width = diameter - 32, Height = diameter - 32, Fill = Ui.Panel, Stroke = Ui.Dots, StrokeThickness = 1, IsHitTestVisible = false };
        Children.Add(face);
        var clickRing = new Ellipse { Width = diameter - 32, Height = diameter - 32, Stroke = Ui.Primary, StrokeThickness = 1, IsHitTestVisible = false };
        Children.Add(clickRing); detent = motion.Register(clickRing, "detent", 0); detent.IdleOpacity = 0; detent.Visual.Opacity = 0;
        Children.Add(Plate); Plate.Width = Plate.Height = diameter;
        var markerCanvas = new Canvas { Width = diameter, Height = diameter, IsHitTestVisible = false };
        Children.Add(markerCanvas);
        var ticks = new GeometryGroup();
        for (int i = 0; i <= 48; i++)
        {
            double angle = (-135 + i * 270d / 48) * Math.PI / 180;
            double x = diameter / 2 + Math.Sin(angle) * (diameter / 2 - 8), y = diameter / 2 - Math.Cos(angle) * (diameter / 2 - 8);
            if (i == 12)
            {
                var zero = new Ellipse { Width = 3, Height = 3, Fill = Ui.Secondary };
                Canvas.SetLeft(zero, x - 1.5); Canvas.SetTop(zero, y - 1.5); markerCanvas.Children.Add(zero);
            }
            else ticks.Children.Add(new EllipseGeometry { Center = new Windows.Foundation.Point(x, y), RadiusX = i % 4 == 0 ? 1.8 : 1.1, RadiusY = i % 4 == 0 ? 1.8 : 1.1 });
        }
        Plate.Children.Add(new Path { Data = ticks, Fill = Ui.Dots, Stretch = Stretch.None, Width = diameter, Height = diameter });
        // The gain position is one stable dot only; no needle, radial line or ambient motion.
        positionDot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Primary };
        markerCanvas.Children.Add(positionDot);
        Children.Add(Slider);
        number = Ui.Button("", "ゲインを数値で編集。ダブルクリック、またはEnter");
        number.Background = new SolidColorBrush(Colors.Transparent); number.BorderThickness = new Thickness(0, 0, 0, 1); number.BorderBrush = new SolidColorBrush(Colors.Transparent); number.Padding = new Thickness(0);
        number.Height = 40; number.VerticalAlignment = VerticalAlignment.Center; number.HorizontalAlignment = HorizontalAlignment.Center;
        var readout = new Grid(); digits.HorizontalAlignment = previousDigits.HorizontalAlignment = HorizontalAlignment.Center;
        digits.Height = previousDigits.Height = compact ? 20 : 24; readout.Children.Add(previousDigits); readout.Children.Add(digits);
        number.Content = readout; Children.Add(number);
        AutomationProperties.SetAutomationId(number, "GainReadout");
        number.DoubleTapped += (_, e) => { BeginEdit(); e.Handled = true; };
        number.Click += (_, _) => { if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0) model.SetGain(0); };
        number.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { BeginEdit(); e.Handled = true; } }), true);
        // Wheel anywhere on the readout is the same gain operation; clicks never jump the dial.
        number.PointerWheelChanged += (_, e) => { vm.SetGain(vm.Settings.Gain + e.GetCurrentPoint(number).Properties.MouseWheelDelta / 120d * (GainSlider.Shift ? .1 : 1)); motion.Tick(detent); e.Handled = true; };
        var units = Ui.Label("dB", 10); units.HorizontalAlignment = HorizontalAlignment.Center; units.VerticalAlignment = VerticalAlignment.Center; units.Margin = new Thickness(0, 48, 0, 0); units.IsHitTestVisible = false; Children.Add(units);
        editor.VerticalAlignment = VerticalAlignment.Center; editor.HorizontalAlignment = HorizontalAlignment.Center; Children.Add(editor);
        AutomationProperties.SetName(editor, "ゲイン dB、-12から36");
        AutomationProperties.SetAutomationId(editor, "GainEditor");
        editor.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { CancelEdit(); e.Handled = true; } else if (e.Key == VirtualKey.Enter) { CommitEdit(); e.Handled = true; } };
        editor.LostFocus += (_, _) => { if (editor.Visibility == Visibility.Visible) CancelEdit(); };
        Slider.ValueChanged += (_, e) => { if (!updating) { vm.SetGain(e.NewValue); motion.Tick(detent); } };
        Slider.PointerEntered += (_, _) => face.Stroke = Ui.Secondary;
        Slider.PointerExited += (_, _) => { if (!Slider.Dragging) face.Stroke = Ui.Dots; };
        Slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { face.Stroke = Ui.Primary; motion.Tick(detent); }), true);
        Slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => face.Stroke = Ui.Dots), true);
        Slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => face.Stroke = Ui.Dots), true);
        numeric = motion.Register(digits, "number", 0); // Readout never receives ambient animation.
        previousNumeric = motion.Register(previousDigits, "number", 0);
        previousNumeric.IdleOpacity = 0; previousNumeric.Visual.Opacity = 0;
        number.PointerEntered += (_, _) => number.BorderBrush = Ui.Primary;
        number.PointerExited += (_, _) => number.BorderBrush = new SolidColorBrush(Colors.Transparent);
        vm.PropertyChanged += (_, _) => Refresh(); Refresh();
    }
    private void BeginEdit() { editingValue = model.Settings.Gain; editor.Text = editingValue.ToString("0.0", CultureInfo.CurrentCulture); editor.Visibility = Visibility.Visible; number.Visibility = Visibility.Collapsed; editor.Focus(FocusState.Programmatic); editor.SelectAll(); }
    private void CancelEdit() { editor.Visibility = Visibility.Collapsed; number.Visibility = Visibility.Visible; number.Focus(FocusState.Programmatic); }
    private void CommitEdit()
    {
        if (!double.TryParse(editor.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || value is < -12 or > 36)
        { AutomationProperties.SetHelpText(editor, "-12〜36の有限な数値を入力してください。"); model.SetNotice("ゲインには -12〜36 dB の数値を入力してください。"); return; }
        Draw(previousDigits, editingValue); model.SetGain(value); CancelEdit();
        if (motion.Mode != MotionMode.Off) { previousNumeric.Visual.Opacity = 1; numeric.Visual.Opacity = 0; motion.Fade(previousNumeric, 0); motion.Fade(numeric, 1); }
        else { previousNumeric.Visual.Opacity = 0; numeric.Visual.Opacity = 1; }
    }
    private double previous = double.NaN;
    internal void Refresh()
    {
        double value = model.Settings.Gain; if (value == previous) return; previous = value;
        updating = true; Slider.Value = value; updating = false;
        double angle = (-135 + (value + 12) / 48 * 270) * Math.PI / 180;
        Canvas.SetLeft(positionDot, diameter / 2 + Math.Sin(angle) * (diameter / 2 - 8) - positionDot.Width / 2);
        Canvas.SetTop(positionDot, diameter / 2 - Math.Cos(angle) * (diameter / 2 - 8) - positionDot.Height / 2);
        string text = value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        AutomationProperties.SetName(number, $"{text} dB、数値で編集"); AutomationProperties.SetHelpText(Slider, $"現在 {text} dB。円周に沿ってドラッグ、ホイール、矢印キー。Shiftで微調整、Homeで0。");
        previousNumeric.Visual.StopAnimation("Opacity"); previousNumeric.Visual.Opacity = 0;
        numeric.Visual.StopAnimation("Opacity"); numeric.Visual.Opacity = 1;
        Draw(digits, value);
    }
    private void Draw(Path target, double value)
    {
        string text = value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        double cell = compact ? 2.7 : 3.4; target.Width = text.Length * 6 * cell;
        // Identical dot geometry in one XAML shape, rather than a visual/layout node per dot.
        var geometry = new GeometryGroup();
        for (int c = 0; c < text.Length; c++) if (Glyphs.TryGetValue(text[c], out var rows))
            for (int y = 0; y < 7; y++) for (int x = 0; x < 5; x++) if (rows[y][x] == '1')
            { double radius = cell * .325; geometry.Children.Add(new EllipseGeometry { Center = new Windows.Foundation.Point((c * 6 + x) * cell + radius, y * cell + radius), RadiusX = radius, RadiusY = radius }); }
        target.Data = geometry;
    }
}
