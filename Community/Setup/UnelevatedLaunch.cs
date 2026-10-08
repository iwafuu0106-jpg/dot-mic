using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotMic.Setup;

internal static class UnelevatedLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, Reserved2; public nint ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle copy);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logon, string application, string? command, uint flags, nint environment, string directory, ref Startup startup, out ProcessInfo process);
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static void OpenApplication()
    {
        string root = ApplicationInstaller.ConfiguredRoot ?? throw new IOException("アプリが見つかりません。");
        var installed = ApplicationInstaller.ReadInstalled(root) ?? throw new IOException("アプリの配置情報が見つかりません。");
        string executable = Path.Combine(root, "DOT MIC.exe");
        SecureStorage.Validate(executable, false);
        if (installed.Files.SingleOrDefault(f => f.Path == "DOT MIC.exe")?.Hash != Contract.FileHash(executable))
            throw new IOException("アプリの起動ファイルが変更されています。");
        nint shell = GetShellWindow();
        if (shell == 0) throw new IOException("デスクトップからDOT MICを開いてください。");
        GetWindowThreadProcessId(shell, out uint pid);
        Check(ProcessIdToSessionId(pid, out uint shellSession)); Check(ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession));
        if (pid == 0 || shellSession != ownSession) throw new IOException("デスクトップからDOT MICを開いてください。");
        using var explorer = OpenProcess(0x1000, false, pid);
        if (explorer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(OpenProcessToken(explorer, 0x0002 | 0x0008, out var original));
        using (original) {
            Check(GetTokenInformation(original, 20, out uint elevated, 4, out _));
            if (elevated != 0) throw new IOException("通常のデスクトップからDOT MICを開いてください。");
            Check(DuplicateTokenEx(original, 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100, 0, 2, 1, out var user));
            using (user) {
                var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Desktop = @"winsta0\default" };
                Check(CreateProcessWithTokenW(user, 1, executable, null, 0, 0, root, ref startup, out var process));
                CloseHandle(process.Thread); CloseHandle(process.Process);
            }
        }
    }
}
