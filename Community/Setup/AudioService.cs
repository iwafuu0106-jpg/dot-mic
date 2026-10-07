using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DotMic.Setup;

internal static class AudioService
{
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceEntry { public nint Name, DisplayName; public Status Status; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint rights);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool StartService(nint service, uint count, nint args);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumDependentServices(nint service, uint state, nint buffer, uint size, out uint needed, out uint count);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    internal static List<AudioDependent> Dependents()
    {
        var result = new List<AudioDependent>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manager = OpenSCManager(null, null, 1); if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            void Walk(string name) {
                var service = OpenService(manager, name, 0x0c); if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), name);
                try {
                    if (EnumDependentServices(service, 1, 0, 0, out uint size, out _)) return;
                    if (Marshal.GetLastWin32Error() != 234 || size > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error(), "Audio dependency inventory");
                    nint buffer = Marshal.AllocHGlobal(checked((int)size));
                    try {
                        if (!EnumDependentServices(service, 1, buffer, size, out _, out uint count)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        for (int i = 0; i < count; i++) {
                            var entry = Marshal.PtrToStructure<ServiceEntry>(buffer + i * Marshal.SizeOf<ServiceEntry>());
                            string child = Marshal.PtrToStringUni(entry.Name) ?? throw new IOException("Audio dependency name missing");
                            if (!seen.Add(child)) continue;
                            if (entry.Status.State is not (4 or 7)) throw new IOException("依存サービスが状態遷移中です。完了後に変更内容を再確認してください: " + child);
                            Walk(child); result.Add(new(child, Marshal.PtrToStringUni(entry.DisplayName) ?? child, entry.Status.State));
                        }
                    } finally { Marshal.FreeHGlobal(buffer); }
                } finally { CloseServiceHandle(service); }
            }
            Walk("Audiosrv"); return result;
        } finally { CloseServiceHandle(manager); }
    }
    internal static void ValidateDependents(IReadOnlyList<AudioDependent> expected)
    {
        var current = Dependents();
        if (current.Count != expected.Count || current.Any(c => !expected.Any(e => e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && e.OriginalState == c.OriginalState))) throw new IOException("確認後に音声サービスの依存元・稼働状態が変わりました。変更内容を再確認してください。");
    }
    internal static void Restart(bool consent, IReadOnlyList<AudioDependent> dependents, bool dependentConsent)
    { try { Stop(consent, dependents, dependentConsent); } finally { Start(consent, dependents, dependentConsent); } }
    internal static void Stop(bool consent, IReadOnlyList<AudioDependent> dependents, bool dependentConsent)
    {
        if (dependents.Count > 0 && !dependentConsent) throw new OperationCanceledException("表示された依存サービスの一時停止への同意がありません。");
        foreach (var service in dependents) Change(consent, service.Name, false);
        Change(consent, "Audiosrv", false);
    }
    internal static void Start(bool consent, IReadOnlyList<AudioDependent> dependents, bool dependentConsent)
    {
        Change(consent, "Audiosrv", true);
        if (!dependentConsent) return;
        var errors = new List<Exception>();
        foreach (var service in dependents.Reverse()) try { Change(consent, service.Name, true, service.OriginalState == 7); } catch (Exception e) { errors.Add(e); }
        if (errors.Count > 0) throw new AggregateException("依存サービスの元状態への復元が未完了です。", errors);
    }
    private static void Change(bool consent, string name, bool start, bool paused = false)
    {
        if (!consent) throw new OperationCanceledException("音声再生・通話の一時切断への同意がありません。");
        var manager = OpenSCManager(null, null, 1); if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { var service = OpenService(manager, name, paused ? 0x74u : 0x34u); if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), name);
            try {
                void Wait(uint wanted) { var deadline = Environment.TickCount64 + 20000; while (Environment.TickCount64 < deadline) { if (!QueryServiceStatus(service, out var s)) throw new Win32Exception(Marshal.GetLastWin32Error()); if (s.State == wanted) return; Thread.Sleep(100); } throw new IOException("Audio service再初期化が完了しません。PC再起動が必要な場合は利用者が手動で行ってください。"); }
                if (!QueryServiceStatus(service, out var current)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (start && paused && current.State == 7) return;
                if (start && current.State == 7) { if (!ControlService(service, 3, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), name); Wait(4); current.State = 4; }
                if (!start && current.State != 1) { if (current.State != 3 && !ControlService(service, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error()); Wait(1); }
                if (start && current.State != 4) { if (current.State != 2 && !StartService(service, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); Wait(4); }
                if (start && paused) { if (!ControlService(service, 2, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), name); Wait(7); }
            } finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
}
