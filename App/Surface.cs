using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DotMic;

internal sealed class Surface : UserControl
{
    internal readonly MotionHub Motion;
    internal GainDial Dial { get; }
    internal bool PopupOpen { get; private set; }
    private readonly AudioViewModel model;
    private readonly DotMicApplication owner;
    private readonly bool compact;
    private readonly Grid root = new();
    internal FrameworkElement DragRegion { get; }
    private readonly Button device, threshold, menu;
    private Flyout? menuFlyout;
    private readonly Visual deviceChevron;
    private readonly ScalarKeyFrameAnimation deviceRotation;
    private readonly ToggleSwitch gate = new() { FontSize = 12, MinWidth = 40, OnContent = "", OffContent = "" };
    private readonly ToggleSwitch nc = new() { FontSize = 12, MinWidth = 40, OnContent = "", OffContent = "" };
    private readonly TextBlock connection = Ui.Label("未接続", 12), gateState = Ui.Label("無効", 10), ncState = Ui.Label("無効", 10);
    private readonly TextBlock deviceName = Ui.Label("マイク選択", 12), inputValue = Ui.Label("−∞", 10), outputValue = Ui.Label("−∞", 10);
    private readonly Rectangle inLevel = new() { Fill = Ui.Accent, Height = 4 }, outLevel = new() { Fill = Ui.Accent, Height = 4 };
    private readonly TextBlock limit = Ui.Label("", 10);
    private readonly List<(Visual visual, ScalarKeyFrameAnimation rotate, Vector3KeyFrameAnimation offset)> menuLines = [];
    private bool refresh;
    private bool active;
    private string oldNotice = "";
    private MotionMode lastMotion;
    internal Surface(DotMicApplication app, AudioViewModel vm, bool small)
    {
        owner = app; model = vm; compact = small; Motion = new(vm); lastMotion = Motion.Mode;
        RequestedTheme = ElementTheme.Dark; Background = Ui.Background; root.Background = Ui.Background;
        AutomationProperties.SetAutomationId(this, small ? "FlyoutSurface" : "MainSurface");
        foreach (string key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed", "SystemControlHighlightAccentBrush" }) Resources[key] = Ui.Accent;
        foreach (string key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" }) Resources[key] = Ui.Background;
        var shell = new Grid { Background = Ui.Background };
        shell.RowDefinitions.Add(new() { Height = new GridLength(40) }); shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.Margin = new Thickness(16, 8, 16, 16);
        root.RowSpacing = 8;
        for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = root, Background = Ui.Background, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); shell.Children.Add(scroll); Content = shell;
        var header = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = new GridLength(40) }); header.ColumnDefinitions.Add(new() { Width = new GridLength(small ? 8 : 138) });
        var name = Ui.Label("DOT MIC", 12); name.Foreground = Ui.Primary; name.CharacterSpacing = 160;
        var drag = new Grid { Background = new SolidColorBrush(Colors.Transparent) }; drag.Children.Add(name); header.Children.Add(drag); DragRegion = drag;
        var icon = new Canvas { Width = 18, Height = 18, IsHitTestVisible = false };
        for (int i = 0; i < 3; i++)
        {
            var line = new Rectangle { Width = 16, Height = 1, Fill = Ui.Primary }; Canvas.SetTop(line, 3 + i * 6); icon.Children.Add(line);
            var visual = MotionHub.VisualFor(line); visual.CenterPoint = new Vector3(8, .5f, 0); visual.RotationAxis = new Vector3(0, 0, 1);
            menuLines.Add((visual, visual.Compositor.CreateScalarKeyFrameAnimation(), visual.Compositor.CreateVector3KeyFrameAnimation()));
        }
        menu = Ui.Button(icon, "メニュー"); menu.Background = new SolidColorBrush(Colors.Transparent); menu.BorderThickness = new Thickness(0); menu.Margin = new Thickness(4); Grid.SetColumn(menu, 1); header.Children.Add(menu); Motion.Attach(menu, Motion.Register(icon, "menu", 0));
        AutomationProperties.SetAutomationId(menu, "MenuButton");
        menu.Click += (_, _) => { if (menuFlyout is null) BuildMenu(); if (PopupOpen) menuFlyout!.Hide(); else menuFlyout!.ShowAt(menu); };
        shell.Children.Add(header);
        var deviceContent = new Grid(); deviceContent.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); deviceContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        deviceName.Foreground = Ui.Primary; deviceName.TextWrapping = TextWrapping.NoWrap; deviceName.TextTrimming = TextTrimming.CharacterEllipsis;
        var deviceText = new StackPanel { Spacing = 4 }; deviceText.Children.Add(deviceName); deviceText.Children.Add(connection); deviceContent.Children.Add(deviceText);
        connection.FontSize = 10;
        var chevron = Ui.Icon("\uE70D", 12); chevron.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(chevron, 1); deviceContent.Children.Add(chevron);
        deviceChevron = ElementCompositionPreview.GetElementVisual(chevron); deviceChevron.RotationAxis = new Vector3(0, 0, 1); deviceRotation = deviceChevron.Compositor.CreateScalarKeyFrameAnimation();
        chevron.SizeChanged += (_, _) => deviceChevron.CenterPoint = new Vector3((float)chevron.ActualWidth / 2, (float)chevron.ActualHeight / 2, 0);
        device = Ui.Button(deviceContent, "マイク情報"); device.Background = Ui.Panel; device.Padding = new Thickness(8);
        device.Click += async (_, _) => { RotateDevice(true); try { await ConnectionAsync(); } finally { RotateDevice(false); } };
        AutomationProperties.SetAutomationId(device, "DeviceButton");
        Motion.Attach(device, Motion.Register(chevron, "device", 0));
        AddRow(device, 0);
        var center = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        Dial = new(vm, small, Motion); center.Children.Add(Dial);
        var meters = new Grid { Width = small ? 168 : 208, ColumnSpacing = 16 };
        meters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); meters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        meters.Children.Add(Meter("入力", inLevel, inputValue)); var outputMeter = Meter("出力", outLevel, outputValue); Grid.SetColumn(outputMeter, 1); meters.Children.Add(outputMeter);
        center.Children.Add(meters); limit.Foreground = Ui.Warning; limit.HorizontalAlignment = HorizontalAlignment.Center; limit.Visibility = Visibility.Collapsed; center.Children.Add(limit); AddRow(center, 1);
        Dial.Slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => DispatcherQueue.TryEnqueue(owner.DismissFlyoutIfInactive)), true);
        var gateRow = Row("ノイズゲート", gate, out var gateLabel); gateLabel.Children.Add(gateState);
        threshold = Ui.Button("−48 dB", "ゲート閾値と詳細"); threshold.Click += async (_, _) => await GateAsync();
        threshold.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Label("−48 dB") } };
        threshold.Margin = new Thickness(8, 0, 16, 0); Grid.SetColumn(threshold, 1); gateRow.Children.Add(threshold); AddRow(gateRow, 2);
        var ncRow = Row("ノイズ除去", nc, out var ncLabel); ncLabel.Children.Add(ncState); AddRow(ncRow, 3);
        AutomationProperties.SetName(gate, "ゲート有効"); AutomationProperties.SetName(nc, "ノイズキャンセルの要求");
        AutomationProperties.SetAutomationId(gate, "GateToggle"); AutomationProperties.SetAutomationId(nc, "NcToggle"); AutomationProperties.SetAutomationId(threshold, "GateDetails");
        gate.Toggled += (_, _) => { if (!refresh) model.Update(s => s.Gate = gate.IsOn); };
        nc.Toggled += (_, _) => { if (!refresh) model.Update(s => s.Nc = nc.IsOn); };
        model.PropertyChanged += (_, _) => Refresh(); Refresh();
        Loaded += (_, _) => { Motion.SetActive(owner.IsVisible(this)); Motion.Reveal(root, true); Motion.Stagger(root); };
        KeyDown += (_, e) => { if (compact && e.Key == VirtualKey.Escape && !PopupOpen && !owner.DialogOpen && !Dial.Slider.Dragging) { owner.HideFlyout(); e.Handled = true; } };
    }
    private static Grid Row(string name, ToggleSwitch toggle, out StackPanel labels)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.MinHeight = 40;
        labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; var label = Ui.Label(name); label.Foreground = Ui.Primary; labels.Children.Add(label); row.Children.Add(labels);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle); toggle.VerticalAlignment = VerticalAlignment.Center; return row;
    }
    private static Grid Meter(string text, Rectangle fill, TextBlock value)
    {
        var row = new Grid { Height = 16, ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Ui.Eyebrow(text));
        var track = new Grid { Background = Ui.Dots, Height = 4, VerticalAlignment = VerticalAlignment.Center }; fill.HorizontalAlignment = HorizontalAlignment.Left; track.Children.Add(fill); Grid.SetColumn(track, 1); row.Children.Add(track);
        track.SizeChanged += (_, _) => fill.Width = 0; return row;
    }
    private void AddRow(FrameworkElement element, int row) { Grid.SetRow(element, row); root.Children.Add(element); }
    private void Refresh()
    {
        refresh = true; gate.IsOn = model.Settings.Gate; nc.IsOn = model.Settings.Nc; refresh = false;
        deviceName.Text = model.InputName;
        connection.Text = !model.Ready ? "未接続" : model.Settings.MasterBypass ? "バイパス" : model.Status.running != 0 ? "接続中" : "入力待機";
        connection.Foreground = model.Notice.Length > 0 ? Ui.Warning : Ui.Secondary;
        var content = (StackPanel)threshold.Content; ((TextBlock)content.Children[0]).Text = $"{model.Settings.Threshold:0} dB";
        AutomationProperties.SetName(threshold, $"ゲート開閾値 {model.Settings.Threshold:0} dB。詳細を開く");
        gateState.Text = !model.Settings.Gate ? "無効" : model.Status.running == 0 ? "停止" : model.Status.gateOpen != 0 ? "通過" : "遮断";
        ncState.Text = model.Status.running == 0 ? model.Settings.Nc ? "有効・入力待ち" : "無効" : model.NcText;
        ncState.Foreground = model.Status.ncState == 5 ? Ui.Warning : Ui.Secondary;
        gateState.Visibility = model.Settings.Gate ? Visibility.Visible : Visibility.Collapsed;
        ncState.Visibility = model.Settings.Nc || model.Status.ncState != 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Motion.Mode != lastMotion) { lastMotion = Motion.Mode; Motion.SetActive(owner.IsVisible(this)); }
        if (owner.IsVisible(this))
        {
            UpdateMeter(inLevel, inputValue, model.Status.running != 0 ? model.Status.inputPeak : 0);
            UpdateMeter(outLevel, outputValue, model.Status.running != 0 ? model.Status.outputPeak : 0);
            AutomationProperties.SetName(inLevel, $"入力ピーク {Db(model.Status.inputPeak):0.0} dBFS");
            AutomationProperties.SetName(outLevel, $"出力ピーク {Db(model.Status.outputPeak):0.0} dBFS");
            limit.Text = "音量制限中";
            limit.Visibility = model.Limit && model.Status.running != 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (oldNotice != model.Notice) { oldNotice = model.Notice; AutomationProperties.SetHelpText(device, model.Notice); }
    }
    private static double Db(float peak) => peak > 0 && float.IsFinite(peak) ? Math.Max(-80, 20 * Math.Log10(peak)) : -80;
    private static void UpdateMeter(Rectangle fill, TextBlock label, float peak)
    {
        fill.Width = Math.Clamp((Db(peak) + 80) / 80, 0, 1) * ((fill.Parent as FrameworkElement)?.ActualWidth ?? 0);
        label.Text = peak > .0001 ? $"{Db(peak):0.0}" : "−∞";
    }
    internal void SetActive(bool value)
    {
        bool resuming = value && !active; active = value;
        Motion.SetActive(value);
        if (value)
        {
            if (resuming)
            {
                var visual = MotionHub.VisualFor(root);
                visual.StopAnimation("Opacity"); visual.StopAnimation("Translation"); visual.StopAnimation("Scale");
                visual.Opacity = 1; MotionHub.Translate(visual, Vector3.Zero); visual.Scale = Vector3.One;
            }
            Refresh();
        }
        else
        {
            var visual = MotionHub.VisualFor(root);
            visual.StopAnimation("Opacity"); visual.StopAnimation("Translation"); visual.StopAnimation("Scale");
            foreach (var child in root.Children.OfType<FrameworkElement>()) { var v = ElementCompositionPreview.GetElementVisual(child); v.StopAnimation("Opacity"); v.Opacity = 1; }
            foreach (var line in menuLines) { line.visual.StopAnimation("RotationAngleInDegrees"); line.visual.StopAnimation("Translation"); }
            deviceChevron.StopAnimation("RotationAngleInDegrees");
        }
    }
    private void RotateDevice(bool open)
    {
        float target = open ? 180 : 0;
        if (Motion.Mode != MotionMode.Full) { deviceChevron.StopAnimation("RotationAngleInDegrees"); deviceChevron.RotationAngleInDegrees = target; return; }
        deviceRotation.Duration = TimeSpan.FromMilliseconds(167); deviceRotation.InsertExpressionKeyFrame(0, "this.StartingValue"); deviceRotation.InsertKeyFrame(1, target); deviceChevron.StartAnimation("RotationAngleInDegrees", deviceRotation);
    }
    internal async Task HideAnimationAsync()
    {
        Motion.Reveal(root, false); await Task.Delay(Motion.Mode == MotionMode.Off ? 0 : Motion.Mode == MotionMode.Reduced ? 100 : 167);
    }
    internal void ShowAnimation(bool retarget = false) { SetActive(true); Motion.Reveal(root, true, retarget: retarget); Motion.Stagger(root); }
    internal void OpenSettings()
    {
        if (!IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { Loaded -= loaded; OpenSettings(); };
            Loaded += loaded; return;
        }
        if (menuFlyout == null) BuildMenu();
        if (!PopupOpen) menuFlyout!.ShowAt(menu);
    }
    private void BuildMenu()
    {
        var panel = new StackPanel { Width = 248, Spacing = 8 };
        void Add(string text, Func<Task> action) { var button = Ui.Button(text, text); panel.Children.Add(button); Motion.Attach(button, Motion.Register(button, "item", 0)); button.Click += async (_, _) => { menuFlyout!.Hide(); await action(); }; }
        if (compact) Add("メインを開く", () => { owner.ShowMain(); return Task.CompletedTask; });
        bool menuRefresh = false;
        var startup = new ToggleSwitch { Header = "サインイン時に起動", OnContent = "有効", OffContent = "無効" }; panel.Children.Add(startup);
        var startupNote = Ui.Label("", 10); startupNote.Visibility = Visibility.Collapsed; panel.Children.Add(startupNote);
        void ReadStartup() {
            var state = StartupRegistration.Read();
            startup.IsOn = state.Enabled;
            startupNote.Text = state.Error ?? ""; startupNote.Foreground = Ui.Warning;
            startupNote.Visibility = state.Error == null ? Visibility.Collapsed : Visibility.Visible;
        }
        ReadStartup();
        startup.Toggled += (_, _) => { if (menuRefresh) return; try { StartupRegistration.Set(startup.IsOn); model.Update(s => s.StartOnSignIn = startup.IsOn); model.SetStartupNotice(""); startupNote.Visibility = Visibility.Collapsed; } catch (Exception e) { menuRefresh = true; try { ReadStartup(); } finally { menuRefresh = false; } startupNote.Foreground = Ui.Warning; startupNote.Text = e.Message; startupNote.Visibility = Visibility.Visible; model.SetStartupNotice("サインイン時の起動を設定できません：" + e.Message); } };
        var bypass = new ToggleSwitch { Header = "バイパス（処理を停止）", OnContent = "有効", OffContent = "無効", IsOn = model.Settings.MasterBypass }; panel.Children.Add(bypass);
        AutomationProperties.SetAutomationId(bypass, "MasterBypassToggle");
        bypass.Toggled += (_, _) => { if (!menuRefresh) model.Update(s => s.MasterBypass = bypass.IsOn); };
        Add("マイク", ConnectionAsync);
        Add("セットアップ", model.RepairAsync);
        Add("診断", DiagnosticsAsync);
        var menuBody = new Grid { RowSpacing = 8 };
        menuBody.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); menuBody.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var menuScroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        menuBody.Children.Add(menuScroll);
        var exit = Ui.Button("終了", "終了"); Grid.SetRow(exit, 1); menuBody.Children.Add(exit);
        Motion.Attach(exit, Motion.Register(exit, "item", 0)); exit.Click += async (_, _) => { menuFlyout!.Hide(); await owner.ExitAsync(); };
        var flyout = new Flyout { Content = menuBody, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { Setters = { new Setter(Control.BackgroundProperty, Ui.Panel), new Setter(Control.BorderBrushProperty, Ui.Dots), new Setter(Control.CornerRadiusProperty, new CornerRadius(4)), new Setter(FrameworkElement.WidthProperty, 284d), new Setter(FlyoutPresenter.IsDefaultShadowEnabledProperty, false) } };
        flyout.Opening += (_, _) => menuScroll.MaxHeight = Math.Max(120, Math.Min(560, XamlRoot.Size.Height - 96) - 52);
         flyout.Opened += (_, _) => { menuRefresh = true; try { ReadStartup(); bypass.IsOn = model.Settings.MasterBypass; } finally { menuRefresh = false; } PopupOpen = true; SetMenuShape(true); Motion.Reveal(panel, true, true); SetActive(owner.IsVisible(this)); };
        flyout.Closed += (_, _) => { PopupOpen = false; SetMenuShape(false); DispatcherQueue.TryEnqueue(() => { SetActive(owner.IsVisible(this)); owner.DismissFlyoutIfInactive(); }); };
        menuFlyout = flyout; // Manual toggling only; do not also enable Button's automatic Flyout opening.
    }
    private void SetMenuShape(bool open)
    {
        for (int i = 0; i < 3; i++)
        {
            var (v, animation, offset) = menuLines[i]; float target = open ? i == 0 ? 45 : i == 2 ? -45 : 0 : 0;
            Vector3 translation = open ? new Vector3(0, i == 0 ? 6 : i == 2 ? -6 : 0, 0) : Vector3.Zero;
            v.Opacity = open && i == 1 ? 0 : 1;
            if (Motion.Mode != MotionMode.Full) { v.StopAnimation("RotationAngleInDegrees"); v.StopAnimation("Translation"); v.RotationAngleInDegrees = target; MotionHub.Translate(v, translation); continue; }
            animation.Duration = TimeSpan.FromMilliseconds(167); animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, target); v.StartAnimation("RotationAngleInDegrees", animation);
            offset.Duration = animation.Duration; offset.InsertExpressionKeyFrame(0, "this.StartingValue"); offset.InsertKeyFrame(1, translation); v.StartAnimation("Translation", offset);
        }
    }
    private ContentDialog Dialog(string title, UIElement content, string primary = "")
    {
        double width = Math.Max(180, XamlRoot.Size.Width - 24);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(100, XamlRoot.Size.Height - 160), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = primary, CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark, Background = Ui.Panel, Foreground = Ui.Primary, BorderBrush = Ui.Dots, CornerRadius = new CornerRadius(4), MinWidth = 0, MaxWidth = width };
        dialog.Resources["ContentDialogMinWidth"] = 0d; dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogBackground"] = Ui.Panel; dialog.Resources["ContentDialogTopOverlay"] = Ui.Panel;
        dialog.Resources["ContentDialogBorderBrush"] = Ui.Dots;
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            dialog.Resources["AccentButtonBackground" + state] = Ui.Accent;
            dialog.Resources["AccentButtonForeground" + state] = Ui.Background;
        }
        return dialog;
    }
    private async Task ShowAsync(ContentDialog dialog, FrameworkElement body)
    {
        if (owner.DialogOpen) return;
        owner.DialogOpen = true;
        try { dialog.Opened += (_, _) => { Motion.Reveal(body, true, true); if (body is Panel panel) Motion.Stagger(panel); }; await dialog.ShowAsync(); }
        finally { owner.DialogOpen = false; Motion.ReleaseTemporary(); owner.DismissFlyoutIfInactive(); }
    }
    internal async Task ConnectionAsync()
    {
        if (owner.DialogOpen) return;
        await model.RefreshAsync();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Ui.Label(model.InputName, 13));
        if (model.Notice.Length > 0) { var error = Ui.Label(model.Notice, 12); error.Foreground = Ui.Warning; body.Children.Add(error); }
        var dialog = Dialog("マイク", body, model.IntegrationNeedsRepair ? "修復" : model.IntegrationNeedsSetup ? "導入" : "");
        if (model.IntegrationNeedsRepair || model.IntegrationNeedsSetup) dialog.PrimaryButtonClick += (_, _) => _ = model.RepairAsync();
        await ShowAsync(dialog, body);
    }
    private async Task GateAsync()
    {
        if (owner.DialogOpen) return;
        var body = new StackPanel { Spacing = 8 };
        var closeThreshold = Ui.Label("");
        void Add(string text, double value, double min, double max, Action<Settings, double> set)
        {
            var box = new NumberBox { Header = text, Value = value, Minimum = min, Maximum = max, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten };
            AutomationProperties.SetName(box, text); body.Children.Add(box);
            box.ValueChanged += (_, e) => { if (!double.IsFinite(e.NewValue) || e.NewValue < min || e.NewValue > max) { box.Value = e.OldValue; return; } model.Update(s => set(s, e.NewValue)); closeThreshold.Text = $"閉じる閾値: {model.Settings.Threshold - model.Settings.Hysteresis:0.#} dBFS"; };
        }
        Add("音を通すレベル（dBFS）", model.Settings.Threshold, -80, -10, (s, v) => s.Threshold = v);
        Add("停止レベルとの差（dB）", model.Settings.Hysteresis, 2, 12, (s, v) => s.Hysteresis = v);
        Add("開く時間（ms）", model.Settings.Attack, 1, 30, (s, v) => s.Attack = v);
        Add("維持する時間（ms）", model.Settings.Hold, 50, 500, (s, v) => s.Hold = v);
        Add("閉じる時間（ms）", model.Settings.Release, 30, 500, (s, v) => s.Release = v);
        closeThreshold.Text = $"閉じる閾値: {model.Settings.Threshold - model.Settings.Hysteresis:0.#} dBFS"; body.Children.Add(closeThreshold);
        await ShowAsync(Dialog("ゲート詳細", body), body);
    }
    private async Task DiagnosticsAsync()
    {
        var body = new StackPanel { Spacing = 10 }; var text = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Primary }; body.Children.Add(text);
        string Snapshot() { var s = model.Status; return $"DOT MIC\n{model.Notice}\n入力: {model.InputName}\nバイパス: {(model.Settings.MasterBypass ? "有効" : "無効")}\nノイズ除去: {model.NcText}\n推論回数: {s.runs} / 採用区間: {s.wetBlocks} / 原音区間: {s.fallbackBlocks}\n異常回数: {s.faults}\n固定遅延: 83 ms"; }
        var copy = Ui.Button("数値と状態をコピー", "診断をクリップボードにコピー"); body.Children.Add(copy); copy.Click += (_, _) => { var data = new DataPackage(); data.SetText(Snapshot()); Clipboard.SetContent(data); };
        System.ComponentModel.PropertyChangedEventHandler handler = (_, _) => text.Text = Snapshot(); model.PropertyChanged += handler; text.Text = Snapshot();
        try { await ShowAsync(Dialog("診断（実測状態）", body), body); } finally { model.PropertyChanged -= handler; }
    }
}
