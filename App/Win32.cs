using System.Runtime.InteropServices;

namespace DotMic;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public uint Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    internal static void StyleFrame(nint hwnd)
    {
        int dark = 1, rounded = 2;
        var color = Ui.Dots.Color; int border = color.R | color.G << 8 | color.B << 16;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }
    [DllImport("wtsapi32.dll")] internal static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(nint hwnd);

}

internal sealed class TrayIcon : IDisposable
{
    internal const uint Callback = 0x8000 + 32;
    internal static readonly Guid StableGuid = new("09BAF257-0348-4453-A4B4-E2EF786E087D");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint size; public nint hwnd; public uint id, flags, callback; public nint icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public uint state, stateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info;
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string title;
        public uint infoFlags; public Guid guid; public nint balloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NOTIFYICONIDENTIFIER { public uint size; public nint hwnd; public uint id; public Guid guid; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint command, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out Win32.RECT rect);
    private NOTIFYICONDATA data;
    private bool disposed;
    internal bool Registered { get; private set; }
    internal TrayIcon(nint hwnd)
    {
        int size = (int)Math.Ceiling(16 * Math.Max(96, Win32.GetDpiForWindow(hwnd)) / 96d);
        nint icon = Win32.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "DotMic.ico"), 1, size, size, 0x10);
        data = new() { size = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hwnd = hwnd, id = 1, flags = 1 | 2 | 4 | 0x20 | 0x80,
            callback = Callback, icon = icon, tip = "DOT MIC", info = "", title = "", guid = StableGuid };
        Add();
    }
    internal void Add() { if (disposed) return; Registered = Shell_NotifyIcon(0, ref data); data.version = 4; if (Registered) Shell_NotifyIcon(4, ref data); }
    internal void SetState(string text) { data.tip = text.Length > 127 ? text[..127] : text; Shell_NotifyIcon(1, ref data); }
    internal Win32.POINT Anchor()
    {
        var id = new NOTIFYICONIDENTIFIER { size = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hwnd = data.hwnd, id = 1, guid = StableGuid };
        if (Shell_NotifyIconGetRect(ref id, out var rect) == 0) return new() { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        Win32.GetCursorPos(out var p); return p;
    }
    public void Dispose() { if (disposed) return; disposed = true; Shell_NotifyIcon(2, ref data); Win32.DestroyIcon(data.icon); }
}
