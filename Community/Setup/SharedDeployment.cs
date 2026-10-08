using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DotMic.Setup;

internal static class SharedDeployment
{
    internal static void Restore(Action validate, Action journal, Action stop, Action unload, Action registry, Action files, Action application, Action start, bool alreadyStopped = false)
    {
        bool stopAttempted = false;
        DotMic.Common.FailurePreservation.Run(() => {
            if (!alreadyStopped) { validate(); journal(); stopAttempted = true; stop(); }
            unload(); registry(); files(); application();
        }, () => { if (stopAttempted) start(); });
    }
    internal static bool NeedsStoppedReplacement(Receipt receipt, Receipt? predecessor)
    {
        if (receipt.Scope != "Shared" || predecessor == null) return false;
        var changed = receipt.Files.Where(f => f.Existed && f.BeforeHash != f.Hash).ToArray();
        if (changed.Any(file => !predecessor.Files.Any(prior => prior.Relative.Equals(file.Relative, StringComparison.OrdinalIgnoreCase) && prior.Hash == file.BeforeHash)))
            throw new IOException("Shared replacement baseline is not the owned predecessor payload; recover conflicts before deployment.");
        return changed.Length > 0;
    }
    // A rejected stop still runs the finally callback; the caller's journaled
    // stop-attempt flag ensures that rejection never starts unrelated services.
    internal static void Run(Action stop, Action copy, Action register, Action compensate, Action start) =>
        DotMic.Common.FailurePreservation.Run(() => {
            stop();
            try { copy(); register(); }
            catch (Exception failure) {
                DotMic.Common.FailurePreservation.Run(() => throw failure, compensate);
            }
        }, start);

    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, Hint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint rights);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    internal static uint RootState()
    {
        nint manager = OpenSCManager(null, null, 1); if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            nint service = OpenService(manager, "Audiosrv", 4); if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error()); return status.State; }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
}
