using System.Security.AccessControl;
using System.Security.Principal;

namespace DotMic.Setup;

internal static class SecureStorage
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static bool Trusted(SecurityIdentifier? sid) => sid?.Value is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal static void NoRedirection(string path)
    {
        path = Path.GetFullPath(path);
        for (string? p = path; p != null; p = Path.GetDirectoryName(p)) if ((System.IO.File.Exists(p) || System.IO.Directory.Exists(p)) && (System.IO.File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("復元先を保証できないreparse point: " + p);
    }
    internal static void Validate(string path, bool directory)
    {
        NoRedirection(path);
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access) : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) throw new IOException("管理者管理の所有権を確認できません: " + path);
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) if (rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference as SecurityIdentifier) && (rule.FileSystemRights & mutation) != 0) throw new IOException("一般ユーザーが変更可能な配置・snapshotは使用できません: " + path);
    }
    internal static void Directory(string path)
    {
        path = Path.GetFullPath(path); NoRedirection(path);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string boundary = path.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? programData : path.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? programFiles : throw new IOException("管理対象のProgramData/ProgramFiles外です。");
        var parts = Path.GetRelativePath(boundary, path).Split(Path.DirectorySeparatorChar);
        string current = boundary;
        foreach (var part in parts) {
            current = Path.Combine(current, part);
            if (System.IO.Directory.Exists(current)) { Validate(current, true); continue; }
            var security = new DirectorySecurity(); security.SetOwner(Administrators); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Administrators }) security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(current).Create(security); Validate(current, true);
        }
    }
    internal static void File(string path)
    {
        NoRedirection(path);
        var security = new FileSecurity(); security.SetOwner(Administrators); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Administrators }) security.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security); Validate(path, false);
    }
    internal static void RecoveryFile(string path)
    {
        path = Path.GetFullPath(path); string root = Path.GetFullPath(Contract.RecoveryRoot);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery snapshot pathが不正です。");
        Validate(path, false);
        for (string? p = Path.GetDirectoryName(path); p != null && p.Length >= Path.GetDirectoryName(root)!.Length; p = Path.GetDirectoryName(p)) Validate(p, true);
    }
}
