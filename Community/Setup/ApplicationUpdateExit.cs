using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotMic.Setup;

internal static class ApplicationUpdateExit
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nuint wp, nint lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    private static string Image(Process process)
    {
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(path.ToString());
    }
    private static string Expected(string root)
    {
        var installed = ApplicationInstaller.ReadInstalled(root) ?? throw new IOException("アプリの配置情報を確認できません。");
        const string relative = "内部ファイル/UI/DotMic.App.exe";
        var entry = installed.Files.SingleOrDefault(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("アプリの起動ファイルが管理対象外です。");
        string path = Transaction.SafePath(installed.Root, relative); SecureStorage.Validate(path, false);
        if (Contract.FileHash(path) != entry.Hash) throw new IOException("アプリの起動ファイルが変更されています。終了・更新していません。");
        return path;
    }
    private static List<Process> Find(string expected)
    {
        var owned = new List<Process>();
        using var self = Process.GetCurrentProcess();
        try {
            foreach (var process in Process.GetProcessesByName("DotMic.App")) {
                bool retained = false;
                try {
                    if (process.HasExited) continue;
                    if (!DotMic.Common.ApplicationExitPolicy.IsOwnedImage(expected, Image(process))) continue;
                    if (process.SessionId != self.SessionId)
                        throw new IOException("別のユーザーのDOT MICが起動中です。そのユーザーが終了してから再試行してください。");
                    owned.Add(process); retained = true;
                } catch (Win32Exception) when (process.HasExited) { }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally { if (!retained) process.Dispose(); }
            }
            return owned;
        } catch { foreach (var process in owned) process.Dispose(); throw; }
    }
    internal static bool Running(string root)
    {
        if (ApplicationInstaller.ReadInstalled(root) == null) return false;
        var targets = Find(Expected(root));
        try { return targets.Count > 0; } finally { foreach (var process in targets) process.Dispose(); }
    }
    internal static bool CloseForUpdate(string root, bool approved)
    {
        if (ApplicationInstaller.ReadInstalled(root) == null) return false;
        string expected = Expected(root); var targets = Find(expected); bool forced = false;
        uint message = RegisterWindowMessage(DotMic.Common.UpdateExitProtocol.MessageName);
        if (message == 0) { foreach (var process in targets) process.Dispose(); throw new IOException("アプリの終了を要求できません。更新していません。"); }
        try {
            DotMic.Common.ApplicationExitPolicy.RequireConsent(targets.Count > 0, approved);
            foreach (var process in targets) {
                try {
                    if (process.HasExited) continue;
                    ulong creationTime = unchecked((ulong)process.StartTime.ToFileTimeUtc());
                    forced |= DotMic.Common.ApplicationExitPolicy.Run(() => {
                        WindowCallback callback = (window, _) => {
                            if (!process.HasExited) {
                                GetWindowThreadProcessId(window, out uint pid);
                                if (pid == (uint)process.Id && !process.HasExited) PostMessage(window, message, (nuint)creationTime, 0);
                            }
                            return true;
                        };
                        EnumWindows(callback, 0); GC.KeepAlive(callback);
                    }, milliseconds => process.WaitForExit(milliseconds), () => {
                        if (process.HasExited) return;
                        if (!DotMic.Common.ApplicationExitPolicy.IsOwnedImage(expected, Image(process))) throw new IOException("アプリの識別情報が変わりました。更新していません。");
                        if (!TerminateProcess(process.SafeHandle, 2) && !process.HasExited) throw new Win32Exception(Marshal.GetLastWin32Error());
                    });
                } catch (InvalidOperationException) when (process.WaitForExit(0)) { }
                catch (Win32Exception) when (process.WaitForExit(0)) { }
            }
            // A newly launched instance is not a reason to repeat termination
            // indefinitely. The existing deployment guard refuses any overwrite.
            ApplicationInstaller.RequireApplicationClosed(root);
            return forced;
        } finally { foreach (var process in targets) process.Dispose(); }
    }
}
