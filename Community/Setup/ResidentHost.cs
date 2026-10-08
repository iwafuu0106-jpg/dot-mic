using System.Runtime.InteropServices;

namespace DotMic.Setup;

// Minimal SCM host; no UI, capture, inference, network listener or polling timer.
internal static class ResidentHost
{
    internal const string ServiceName = "DotMicMicrophones";
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint argc, nint argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlCallback(uint control, uint eventType, nint data, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { public string? Name; public MainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, Hint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint RegisterServiceCtrlHandlerEx(string name, ControlCallback callback, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(nint handle, ref Status status);
    internal static int Run(Action<WaitHandle, Action> run, Action<Exception> failed)
    {
        using var stop = new ManualResetEvent(false);
        nint handle = 0; int result = 0; uint checkpoint = 0, reportedState = 2;
        var statusLock = new object();
        void Report(uint state, uint error = 0) {
            lock (statusLock) {
                reportedState = state;
                var status = new Status { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u,
                    Win32Error = error, Hint = state is 2 or 3 ? 30000u : 0u, Checkpoint = state is 2 or 3 ? ++checkpoint : 0u };
                if (handle != 0) SetServiceStatus(handle, ref status);
            }
        }
        ControlCallback control = (code, _, _, _) => {
            if (code is 1 or 5) { Report(3); stop.Set(); }
            return 0;
        };
        MainCallback main = (_, _) => {
            handle = RegisterServiceCtrlHandlerEx(ServiceName, control, 0);
            if (handle == 0) { result = 1; return; }
            Report(2);
            var progress = new System.Threading.Timer(_ => {
                lock (statusLock) if (reportedState is 2 or 3) Report(reportedState);
            }, null, 5000, 5000);
            try { run(stop, () => Report(4)); }
            catch (Exception error) { result = 1; try { failed(error); } catch { } }
            finally {
                using var callbacksFinished = new ManualResetEvent(false);
                if (progress.Dispose(callbacksFinished)) callbacksFinished.WaitOne();
                Report(1, result == 0 ? 0u : 1064u);
            }
        };
        bool dispatched = StartServiceCtrlDispatcher([new() { Name = ServiceName, Main = main }, new()]);
        GC.KeepAlive(main); GC.KeepAlive(control);
        return dispatched ? result : 1;
    }
}
