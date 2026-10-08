using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DotMic.Setup;

// Byte-exact registry64 access. No RegistryKey.GetValue coercion and no broad ACL grants.
internal static class RawRegistry
{
    private static readonly nint Hklm = new(unchecked((int)0x80000002));
    private const uint View64 = 0x100, Read = 0x20019, Query = 1, Set = 2, Create = 4, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const int Missing = 2, Denied = 5, More = 234, End = 259;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyEx(nint root, string path, uint options, uint rights, out nint key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegCreateKeyEx(nint root, string path, uint reserved, string? cls, uint options, uint rights, nint security, out nint key, out uint disposition);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegQueryValueEx(nint key, string name, nint reserved, out uint type, byte[]? bytes, ref uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegEnumValue(nint key, uint index, StringBuilder name, ref uint characters, nint reserved, out uint type, byte[]? data, ref uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegEnumKeyEx(nint key, uint index, StringBuilder name, ref uint characters, nint reserved, nint cls, nint classSize, nint time);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(nint key, string name, uint reserved, uint type, byte[] bytes, uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegDeleteValue(nint key, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegDeleteKeyEx(nint root, string path, uint view, uint reserved);
    [DllImport("advapi32.dll")] private static extern int RegGetKeySecurity(nint key, uint info, byte[]? bytes, ref uint size);
    [DllImport("advapi32.dll")] private static extern int RegSetKeySecurity(nint key, uint info, byte[] bytes);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint rights, out nint token);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivilege state, uint length, nint previous, nint required);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    internal static void Privilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Token privilege");
        try { if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error()); var p = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref p, 0, 0, 0) || Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), name);
        } finally { CloseHandle(token); }
    }
    private sealed class Key(nint handle) : IDisposable { internal nint Handle = handle; public void Dispose() { if (Handle != 0) { RegCloseKey(Handle); Handle = 0; } } }
    private static void Check(int error, string scope) { if (error != 0) throw new Win32Exception(error, scope + $" (registry64, error {error})"); }
    private static Key? Open(string path, uint rights, bool absent = false)
    {
        int e = RegOpenKeyEx(Hklm, path, 0, rights | View64, out var h);
        if (absent && e == Missing) return null; Check(e, path); return new(h);
    }
    internal static bool CanWrite(string path, bool createChild = false)
    {
        int e = RegOpenKeyEx(Hklm, path, 0, (createChild ? Create : Set) | View64, out var h); if (e == 0) { RegCloseKey(h); return true; }
        if (e == Missing) { var split = path.LastIndexOf('\\'); return split > 0 && CanWrite(path[..split], true); }
        if (e == Denied) return false; Check(e, path); return false;
    }
    internal static RawValue? Value(string path, string name)
    {
        using var key = Open(path, Query, true); if (key == null) return null; uint n = 0;
        int e = RegQueryValueEx(key.Handle, name, 0, out uint type, null, ref n); if (e == Missing) return null; if (e != 0 && e != More) Check(e, path);
        if (n > 64 * 1024 * 1024) throw new IOException("Registry value snapshot exceeds the bounded installer budget.");
        byte[] data = new byte[n]; e = RegQueryValueEx(key.Handle, name, 0, out type, data, ref n); Check(e, path); Array.Resize(ref data, checked((int)n)); return new(name, type, data);
    }
    private static byte[] Security(nint key, uint mask)
    {
        uint n = 0; int e = RegGetKeySecurity(key, mask, null, ref n); if (e != 122) Check(e, "Security snapshot size");
        byte[] data = new byte[n]; Check(RegGetKeySecurity(key, mask, data, ref n), "Security snapshot read"); Array.Resize(ref data, checked((int)n)); return data;
    }
    internal static List<KeyImage> Tree(string path)
    {
        var result = new List<KeyImage>();
        void ReadTree(string current)
        {
            using var key = Open(current, Read | 0x01000000, true); if (key == null) { result.Add(new() { Path = current }); return; }
            var image = new KeyImage { Path = current, Exists = true, SecurityMask = 15, Security = Security(key.Handle, 15) }; result.Add(image);
            for (uint i = 0; ; ++i) { var name = new StringBuilder(16384); uint size = 16384, n = 0; int e = RegEnumValue(key.Handle, i, name, ref size, 0, out _, null, ref n); if (e == End) break; if (e != 0 && e != More) Check(e, current); var value = Value(current, name.ToString()); if (value == null) throw new IOException("Registry changed while snapshotting " + current); image.Values.Add(value); }
            for (uint i = 0; ; ++i) { var name = new StringBuilder(256); uint size = 256; int e = RegEnumKeyEx(key.Handle, i, name, ref size, 0, 0, 0, 0); if (e == End) break; Check(e, current); ReadTree(current + "\\" + name); }
        }
        ReadTree(path); return result;
    }
    internal static KeyImage KeyOnly(string path)
    {
        using var key = Open(path, Read | 0x01000000, true); if (key == null) return new() { Path = path };
        return new() { Path = path, Exists = true, SecurityMask = 15, Security = Security(key.Handle, 15) };
    }
    private static byte[] Binary(RawSecurityDescriptor sd) { var bytes = new byte[sd.BinaryLength]; sd.GetBinaryForm(bytes, 0); return bytes; }
    private static void SetSecurity(string path, uint rights, uint mask, byte[] bytes) { using var key = Open(path, rights)!; Check(RegSetKeySecurity(key.Handle, mask, bytes), "Security write " + path); }
    // Only invoked after consent. Grants non-inheritable SetValue/CreateSubKey on ONE key.
    private static byte[] PermissionDescriptor(KeyImage original, uint required)
    {
        if (!original.Exists || original.Security.Length == 0 || original.SecurityMask != 15) throw new IOException("完全なsecurity snapshotがないため一時permission変更を実行できません。");
        var sd = new RawSecurityDescriptor(original.Security, 0); sd.Owner = WindowsIdentity.GetCurrent().User;
        var acl = sd.DiscretionaryAcl ?? new RawAcl(2, 1);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)(Read | required), WindowsIdentity.GetCurrent().User!, false, null));
        sd.DiscretionaryAcl = acl; return Binary(sd);
    }
    private static void TemporaryPermission(string path, KeyImage original, byte[] expected)
    {
        Privilege("SeTakeOwnershipPrivilege"); Privilege("SeRestorePrivilege");
        var intermediate = new RawSecurityDescriptor(original.Security, 0); intermediate.Owner = new RawSecurityDescriptor(expected, 0).Owner;
        SetSecurity(path, WriteOwner, 1, Binary(intermediate)); SetSecurity(path, WriteDac, 4, expected);
    }
    internal static bool SecurityRecognized(KeyImage original, byte[] expected)
    {
        var current = KeyOnly(original.Path);
        return current.Exists && SecurityRecognized(original, expected, current.Security);
    }
    private static bool SecurityRecognized(KeyImage original, byte[] expected, byte[] actual)
    {
        if (SecurityDescriptorPolicy.SamePermissions(original.Security, actual) || SecurityDescriptorPolicy.SamePermissions(expected, actual)) return true;
        var sd = new RawSecurityDescriptor(original.Security, 0); sd.Owner = new RawSecurityDescriptor(expected, 0).Owner;
        return SecurityDescriptorPolicy.SamePermissions(Binary(sd), actual);
    }
    internal static void RestoreSecurity(KeyImage original, byte[]? expected = null, Func<bool>? restoreConflict = null)
    {
        if (!original.Exists || original.SecurityMask != 15 || original.Security.Length == 0) throw new IOException("完全な権限復元記録がありません。");
        var sd = new RawSecurityDescriptor(original.Security, 0);
        using var existing = Open(original.Path, Read | 0x01000000)!;
        byte[] observed = Security(existing.Handle, 15);
        if (SecurityDescriptorPolicy.SamePermissions(original.Security, observed)) return;
        if (expected != null && !SecurityRecognized(original, expected, observed) && !(restoreConflict?.Invoke() ?? false)) throw new IOException("ACL復旧の確認が必要です: " + original.Path);
        Privilege("SeBackupPrivilege"); Privilege("SeRestorePrivilege");
        // An empty subkey reopens the already-held, existing key. It cannot create
        // missing named keys, switch to a replacement path, or grant parent access.
        // REG_OPTION_BACKUP_RESTORE is required for restoring a protected DACL;
        // merely enabling SeRestorePrivilege does not fix normal RegOpenKeyEx.
        Check(RegCreateKeyEx(existing.Handle, "", 0, null, 4, 0, 0, out var handle, out uint disposition), "Open existing key for ACL restore " + original.Path);
        using var key = new Key(handle);
        if (disposition != 2) throw new IOException("既存の権限復元対象を開けませんでした。");
        // Keep the same key held across the decision and privileged reopen; do
        // not overwrite ACL changes made while the user was deciding.
        if (!SecurityDescriptorPolicy.SamePermissions(observed, Security(key.Handle, 15))) throw new IOException("確認後にregistry securityが変わりました: " + original.Path);
        uint mask = 7 | ((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u);
        Check(RegSetKeySecurity(key.Handle, mask, original.Security), "Security restore " + original.Path);
        if (!SecurityDescriptorPolicy.SamePermissions(original.Security, Security(key.Handle, 15))) throw new IOException("ACL/ownerの復元read-backに失敗: " + original.Path);
    }
    internal static void Write(Edit edit, List<KeyImage> snapshots, bool advanced, List<string> advancedKeys, List<string> pendingSecurity, Dictionary<string, byte[]> pendingExpected, Dictionary<string, byte[]> pendingOriginal, Action journal)
    {
        var restore = new List<KeyImage>(); string path = edit.Path;
        try {
            // Find the nearest existing key. Missing keys inherit ordinary parent policy.
            var parent = path;
            bool leafExists;
            using (var leaf = Open(path, Query, true)) leafExists = leaf != null;
            while (true) { using var key = Open(parent, Query, true); if (key != null) break; int slash = parent.LastIndexOf('\\'); if (slash < 0) throw new IOException("Registry parent missing"); parent = parent[..slash]; }
            var observed = Value(path, edit.Name);
            if (edit.Before == null ? observed != null : !edit.Before.Same(observed)) throw new IOException("変更確認後にregistry値が変わりました: " + path + " / " + edit.Name);
            if (!CanWrite(parent, !leafExists)) {
                if (!advanced) throw new UnauthorizedAccessException("追加変更への同意が必要: " + parent);
                var image = snapshots.FirstOrDefault(k => k.Exists && string.Equals(k.Path, parent, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("Permission復元用snapshotがありません: " + parent);
                if (!SecurityDescriptorPolicy.SamePermissions(image.Security, KeyOnly(parent).Security)) throw new IOException("一時permission変更の直前にsecurityが変わりました: " + parent);
                if (!advancedKeys.Contains(parent, StringComparer.OrdinalIgnoreCase)) { advancedKeys.Add(parent); journal(); }
                byte[] expected = PermissionDescriptor(image, leafExists ? Set : Create);
                pendingExpected[parent] = expected;
                pendingOriginal[parent] = image.Security;
                if (!pendingSecurity.Contains(parent, StringComparer.OrdinalIgnoreCase)) pendingSecurity.Add(parent);
                journal(); restore.Add(image); TemporaryPermission(parent, image, expected);
            }
            Check(RegCreateKeyEx(Hklm, path, 0, null, 0, Set | Query | View64, 0, out var h, out _), path);
            using var written = new Key(h);
            observed = Value(path, edit.Name);
            if (edit.Before == null ? observed != null : !edit.Before.Same(observed)) throw new IOException("書込み直前にregistry値が変わりました: " + path + " / " + edit.Name);
            if (edit.After == null) { int e = RegDeleteValue(h, edit.Name); if (e != Missing) Check(e, path); }
            else Check(RegSetValueEx(h, edit.Name, 0, edit.After.Type, edit.After.Data, (uint)edit.After.Data.Length), path);
            var actual = Value(path, edit.Name); if (edit.After == null ? actual != null : !edit.After.Same(actual)) throw new IOException("Registry read-back differs: " + path + " / " + edit.Name);
        } finally { foreach (var image in restore.AsEnumerable().Reverse()) { RestoreSecurity(image, pendingExpected[image.Path]); pendingSecurity.RemoveAll(p => p.Equals(image.Path, StringComparison.OrdinalIgnoreCase)); pendingExpected.Remove(image.Path); pendingOriginal.Remove(image.Path); journal(); } }
    }
    internal static void DeleteEmpty(string path) { int e = RegDeleteKeyEx(Hklm, path, View64, 0); if (e != Missing) Check(e, "Delete owned empty key " + path); }
}
