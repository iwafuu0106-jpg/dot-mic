using System.Security.Principal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DotMic;

public partial class DotMicApplication : Application
{
    private Window? main, flyout;
    private Surface? mainSurface, flyoutSurface;
    private AudioViewModel? model;
    private TrayIcon? tray;
    private Mutex? instance;
    private Win32.SubclassProc? mainProc, flyProc;
    private nint mainHwnd, flyHwnd;
    private uint taskbarCreated;
    private readonly uint activateMessage = Win32.RegisterWindowMessage("DotMic.Activate.3.2.09BAF257");
    private readonly uint updateExitMessage = Win32.RegisterWindowMessage(DotMic.Common.UpdateExitProtocol.MessageName);
    private readonly ulong updateExitToken = CreationTime();
    private static ulong CreationTime() { using var process = System.Diagnostics.Process.GetCurrentProcess(); return unchecked((ulong)process.StartTime.ToFileTimeUtc()); }
    private bool exiting, locked;
    private bool mainHiding, flyHiding;
    private int mainHideGeneration, flyHideGeneration;
    private long nextTooltip;
    internal bool DialogOpen { get; set; }
    public DotMicApplication()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogFailure(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        UnhandledException += (_, e) =>
        {
            LogFailure(e.Exception);
            // Do not mark an unhandled failure as handled or a successful startup.
        };
        try { InitializeComponent(); } catch (Exception e) { LogFailure(e); throw; }
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { await LaunchAsync(args); }
        catch (Exception e) { LogFailure(e); Environment.Exit(1); }
    }
    private static void LogFailure(Exception error)
    {
        try { Directory.CreateDirectory(SettingsStore.DirectoryPath); File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "last-error.log"), error.ToString()); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private async Task LaunchAsync(LaunchActivatedEventArgs args)
    {
        bool startup = Environment.GetCommandLineArgs().Contains("--startup", StringComparer.OrdinalIgnoreCase);
        bool smoke = Environment.GetCommandLineArgs().Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase);
        instance = new Mutex(true, $"Local\\DotMic.3.2.{WindowsIdentity.GetCurrent().User?.Value}", out bool first);
        if (!first)
        {
            if (!startup) { for (int i = 0; i < 3; i++) { Win32.PostMessage((nint)0xffff, activateMessage, 0, 0); await Task.Delay(100); } }
            instance.Dispose(); Exit(); return;
        }
        Ui.InstallResources();
        main = new Window { Title = "DOT MIC" }; mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
        model = new(main.DispatcherQueue, smoke);
        mainSurface = new(this, model, false); main.Content = mainSurface;
        main.ExtendsContentIntoTitleBar = true; main.SetTitleBar(mainSurface.DragRegion);
        main.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        ApplyTitleBar();
        main.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "DotMic.ico"));
        flyout = new Window { Title = "DOT MIC · 小型" }; flyHwnd = WinRT.Interop.WindowNative.GetWindowHandle(flyout);
        flyoutSurface = new(this, model, true); flyout.Content = flyoutSurface;
        model.VisibilityChanged += () => { mainSurface.SetActive(IsVisible(mainSurface)); flyoutSurface.SetActive(IsVisible(flyoutSurface)); };
        main.AppWindow.ResizeClient(new SizeInt32(Scale(mainHwnd, 360), Scale(mainHwnd, 416)));
        if (main.AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = true; presenter.IsMaximizable = false; }
        if (flyout.AppWindow.Presenter is OverlappedPresenter fp)
        { fp.IsResizable = false; fp.IsMaximizable = false; fp.IsMinimizable = false; fp.SetBorderAndTitleBar(true, false); fp.IsAlwaysOnTop = true; }
        Win32.StyleFrame(mainHwnd); Win32.StyleFrame(flyHwnd);
        main.AppWindow.Closing += (_, e) => { if (!exiting) { e.Cancel = true; CloseMain(); } };
        flyout.AppWindow.Closing += (_, e) => { if (!exiting) { e.Cancel = true; HideFlyout(); } };
        main.Activated += (_, _) => UpdateVisibility(); flyout.Activated += (_, _) => UpdateVisibility();
        mainProc = MainMessage; flyProc = FlyMessage;
        Win32.SetWindowSubclass(mainHwnd, mainProc, 1, 0); Win32.SetWindowSubclass(flyHwnd, flyProc, 1, 0);
        taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated"); Win32.WTSRegisterSessionNotification(mainHwnd, 0);
        tray = new(mainHwnd);
        if (!tray.Registered) model.SetNotice("トレイアイコンを登録できませんでした。Explorerの復帰を待つか、メニューから終了してください。");
        model.PropertyChanged += (_, _) =>
        {
            if (Environment.TickCount64 >= nextTooltip)
            {
                nextTooltip = Environment.TickCount64 + 1000;
                tray?.SetState($"DOT MIC · {(model.Status.running != 0 ? "音声処理中" : "停止")} · NC {model.NcText}{(model.Notice.Length > 0 ? " · 要確認" : "")}");
            }
        };
        if (!AppVisibilityPolicy.CanHideOnStartup(startup, model.Ready, tray.Registered)) ShowMain(); else { main.AppWindow.Hide(); flyout.AppWindow.Hide(); UpdateVisibility(); }
        await model.InitializeAsync();
        if (exiting) return; // Exit may have disposed the tray while initialization was awaiting.
        if (AppVisibilityPolicy.CanHideOnStartup(startup, model.Ready, tray.Registered)) { main.AppWindow.Hide(); flyout.AppWindow.Hide(); UpdateVisibility(); }
        else if (!tray.Registered) { ShowMain(); model.SetNotice("トレイアイコンを登録できませんでした。メニューから終了できます。"); }
        if (smoke) { await RunSmokeAsync(); return; }
        if (!model.Ready && !exiting)
        {
            ShowMain();
            // Activate schedules XAML loading; native initialization may finish synchronously.
            // ContentDialog requires the real loaded XamlRoot, not a timer-based guess.
            if (mainSurface.XamlRoot == null)
            {
                var loaded = new TaskCompletionSource();
                RoutedEventHandler? handler = null;
                handler = (_, _) => { mainSurface.Loaded -= handler; loaded.TrySetResult(); };
                mainSurface.Loaded += handler;
                await loaded.Task;
            }
            if (!exiting) await mainSurface.ConnectionAsync();
        }
    }
    private void ApplyTitleBar()
    {
        if (main == null) return;
        var bar = main.AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = Ui.Background.Color;
        bar.ForegroundColor = bar.ButtonForegroundColor = Ui.Primary.Color;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveForegroundColor = Ui.Secondary.Color;
        bar.ButtonHoverBackgroundColor = Ui.Raised.Color; bar.ButtonHoverForegroundColor = Ui.Accent.Color;
        bar.ButtonPressedBackgroundColor = Ui.AccentSoft.Color; bar.ButtonPressedForegroundColor = Ui.Primary.Color;
        if (mainHwnd != 0) Win32.StyleFrame(mainHwnd);
        if (flyHwnd != 0) Win32.StyleFrame(flyHwnd);
    }
    private async Task RunSmokeAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ui-smoke.log");
        try
        {
            await Task.Delay(500);
            if (System.Runtime.InteropServices.Marshal.SizeOf<ApoValues>() != 44 || System.Runtime.InteropServices.Marshal.SizeOf<ApoStatus>() != 64)
                throw new InvalidOperationException("CAPX ABI size mismatch.");
            mainSurface!.Dial.Slider.Value = 1.2;
            if (model!.Settings.Gain != 1.2) throw new InvalidOperationException("Gain slider is not connected.");
            model.SetGain(0); ShowFlyout(); await Task.Delay(350);
            if (!IsVisible(flyoutSurface!)) throw new InvalidOperationException("Tray flyout is not visible.");
            HideFlyout(); await Task.Delay(250);
            if (IsVisible(flyoutSurface!)) throw new InvalidOperationException("Flyout did not hide.");
            var target = await Task.Run(() => ApoSettings.Read(false));
            File.WriteAllText(path, $"PASS WinUI3 main/flyout, shared gain, CAPX target read: {target.name}. UI smoke only; not live audio acceptance.\nNotice: {model.Notice}\n");
            Environment.ExitCode = 0;
        }
        catch (Exception e) { File.WriteAllText(path, $"FAIL {e}\n"); Environment.ExitCode = 1; }
        await ExitAsync();
    }
    private static int Scale(nint hwnd, double dip) => (int)Math.Ceiling(dip * Math.Max(96, Win32.GetDpiForWindow(hwnd)) / 96d);
    internal bool IsVisible(Surface surface)
    {
        nint hwnd = ReferenceEquals(surface, mainSurface) ? mainHwnd : flyHwnd;
        return !locked && !exiting && model?.Suspended != true && hwnd != 0 && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd);
    }
    private void UpdateVisibility()
    {
        if (model == null || mainSurface == null || flyoutSurface == null) return;
        bool a = IsVisible(mainSurface), b = IsVisible(flyoutSurface);
        mainSurface.SetActive(a); flyoutSurface.SetActive(b); model.SetVisibility(a || b);
    }
    internal void ShowMain()
    {
        if (exiting || main == null) return;
        bool visible = mainSurface != null && IsVisible(mainSurface), retarget = mainHiding;
        mainHiding = false; mainHideGeneration++; Win32.ShowWindow(mainHwnd, 9); main.Activate(); Win32.SetForegroundWindow(mainHwnd);
        if (!visible || retarget) mainSurface?.ShowAnimation(retarget); UpdateVisibility();
    }
    internal void CloseMain()
    {
        if (tray?.Registered == true) _ = HideMainAsync();
        else model?.SetNotice("トレイアイコンがありません。メニューの「終了」を使用してください。");
    }
    private async Task HideMainAsync()
    {
        int generation = ++mainHideGeneration;
        mainHiding = true;
        if (mainSurface != null) await mainSurface.HideAnimationAsync();
        if (generation != mainHideGeneration || exiting) return;
        mainHiding = false; main?.AppWindow.Hide(); UpdateVisibility();
    }
    internal void ShowFlyout()
    {
        if (exiting || flyout == null || tray == null) return;
        bool visible = Win32.IsWindowVisible(flyHwnd), retarget = flyHiding;
        if (visible && !flyHiding) { HideFlyout(); return; }
        flyHiding = false;
        flyHideGeneration++;
        var point = tray.Anchor(); var monitor = Win32.MonitorFromPoint(point, 2);
        var info = new Win32.MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info); Win32.GetDpiForMonitor(monitor, 0, out uint dpi, out _); if (dpi == 0) dpi = 96;
        var ui = new Windows.UI.ViewManagement.UISettings(); double textScale = Math.Max(1, ui.TextScaleFactor);
        int width = Math.Min(info.Work.Right - info.Work.Left, (int)Math.Ceiling(320 * dpi / 96d * Math.Min(textScale, 1.5)));
        int height = Math.Min(info.Work.Bottom - info.Work.Top, (int)Math.Ceiling(416 * dpi / 96d * Math.Min(textScale, 1.5)));
        int gap = (int)Math.Ceiling(8 * dpi / 96d);
        int x = Math.Clamp(point.X - width / 2, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = point.Y > (info.Work.Top + info.Work.Bottom) / 2 ? point.Y - height - gap : point.Y + gap;
        y = Math.Clamp(y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        Win32.SetWindowPos(flyHwnd, (nint)(-1), x, y, width, height, 0x10);
        flyout.Activate(); Win32.SetForegroundWindow(flyHwnd); flyoutSurface?.ShowAnimation(retarget && visible); UpdateVisibility();
    }
    internal void HideFlyout() => _ = HideFlyoutAsync();
    private async Task HideFlyoutAsync()
    {
        int generation = ++flyHideGeneration;
        flyHiding = true;
        if (flyoutSurface != null) await flyoutSurface.HideAnimationAsync();
        if (generation != flyHideGeneration || exiting) return;
        flyHiding = false; flyout?.AppWindow.Hide(); UpdateVisibility();
    }
    internal void DismissFlyoutIfInactive()
    {
        if (flyoutSurface == null || !IsVisible(flyoutSurface) || DialogOpen || flyoutSurface.PopupOpen || flyoutSurface.Dial.Slider.Dragging || Win32.GetCapture() != 0) return;
        if (Win32.GetAncestor(Win32.GetForegroundWindow(), 3) != flyHwnd) HideFlyout();
    }
    internal async Task ExitAsync()
    {
        if (exiting) return; exiting = true; mainHideGeneration++; flyHideGeneration++;
        mainSurface?.SetActive(false); flyoutSurface?.SetActive(false); tray?.Dispose(); tray = null;
        if (model != null)
        {
            var shutdown = model.ExitAsync();
            if (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(10))) != shutdown)
            {
                const string reason = "設定の保存が終了要求に応答しません。明示Exitによりアプリを終了します。";
                model.SetNotice(reason);
                try { Directory.CreateDirectory(SettingsStore.DirectoryPath); File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "last-error.log"), reason); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Environment.Exit(2); return;
            }
            await shutdown;
        }
        Win32.WTSUnRegisterSessionNotification(mainHwnd);
        if (mainProc != null) Win32.RemoveWindowSubclass(mainHwnd, mainProc, 1);
        if (flyProc != null) Win32.RemoveWindowSubclass(flyHwnd, flyProc, 1);
        flyout?.Close(); main?.Close(); instance?.ReleaseMutex(); instance?.Dispose(); Exit();
    }
    private nint MainMessage(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == taskbarCreated) { tray?.Add(); if (tray?.Registered != true) main!.DispatcherQueue.TryEnqueue(() => { ShowMain(); model?.SetNotice("トレイアイコンを復元できませんでした。メニューから終了できます。"); }); return 0; }
        if (msg == activateMessage) { main!.DispatcherQueue.TryEnqueue(ShowMain); return 0; }
        if (updateExitMessage != 0 && msg == updateExitMessage) { if (DotMic.Common.UpdateExitProtocol.Accepts(updateExitToken, (ulong)wp)) main!.DispatcherQueue.TryEnqueue(async () => { try { await ExitAsync(); } catch (Exception error) { LogFailure(error); } }); return 0; }
        if (msg == TrayIcon.Callback)
        {
            uint notification = (uint)((long)lp & 0xffff);
            if (notification is 0x400 or 0x401) main!.DispatcherQueue.TryEnqueue(ShowFlyout); // NIN_SELECT / NIN_KEYSELECT, version 4.
            else if (notification == 0x7b) main!.DispatcherQueue.TryEnqueue(TrayMenu);
            return 0;
        }
        if (msg == 5) main!.DispatcherQueue.TryEnqueue(UpdateVisibility);
        if (msg == 0x1a) main!.DispatcherQueue.TryEnqueue(() => { Ui.RefreshPalette(); ApplyTitleBar(); UpdateVisibility(); }); // OS high-contrast/animation settings.
        if (msg == 0x24)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(lp);
            info.MinTrackSize = new Win32.POINT { X = Scale(hwnd, 320), Y = Scale(hwnd, 432) };
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lp, false); return 0;
        }
        if (msg == 0x219 && wp is 7 or 0x8000 or 0x8004) main!.DispatcherQueue.TryEnqueue(() => { if (model?.Status.running == 0) _ = model.RefreshAsync(); });
        if (msg == 0x218)
        {
            if (wp == 4) main!.DispatcherQueue.TryEnqueue(() => { if (model != null) _ = model.SuspendAsync(); });
            if (wp is 7 or 18) main!.DispatcherQueue.TryEnqueue(() => { model?.Resume(); UpdateVisibility(); });
        }
        if (msg == 0x2b1)
        {
            if (wp == 7) { locked = true; main!.DispatcherQueue.TryEnqueue(UpdateVisibility); }
            if (wp == 8) { locked = false; main!.DispatcherQueue.TryEnqueue(UpdateVisibility); }
        }
        return Win32.DefSubclassProc(hwnd, msg, wp, lp);
    }
    private nint FlyMessage(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == 6 && (wp & 0xffff) == 0)
        {
            flyout!.DispatcherQueue.TryEnqueue(() =>
            {
                if (!DialogOpen && flyoutSurface?.PopupOpen != true && flyoutSurface?.Dial.Slider.Dragging != true && Win32.GetCapture() == 0) HideFlyout();
            });
        }
        if (msg == 5) flyout!.DispatcherQueue.TryEnqueue(UpdateVisibility);
        return Win32.DefSubclassProc(hwnd, msg, wp, lp);
    }
    private void TrayMenu()
    {
        if (flyoutSurface == null || flyout == null) return;
        if (!IsVisible(flyoutSurface) || flyHiding) ShowFlyout();
        flyout.Activate(); Win32.SetForegroundWindow(flyHwnd);
        flyoutSurface.OpenSettings();
    }
}
