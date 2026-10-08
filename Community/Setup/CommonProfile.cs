using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace DotMic.Setup;

// Parameter data only. Users may change nine bounded numeric controls here;
// no executable path, device scope, journal or service configuration is writable.
internal static class CommonProfile
{
    internal const string PathName = Contract.ConfigPath + @"\CommonSettings";
    internal static readonly float[] Defaults = [0, 0, 0, -48, 5, 160, 120, 0, 6];
    private static string JournalPath => System.IO.Path.Combine(Contract.RecoveryRoot, "Manager", "profile-initialization.json");
    private sealed record Initialization(float[] Values, string State);
    private sealed record Envelope(string Sha256, byte[] Data);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyEx(nint root, string path, uint options, uint access, out nint key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegQueryValueEx(nint key, string name, nint reserved, out uint type, byte[] buffer, ref uint length);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
    private static RawValue? SmallValue(string path, string name, int capacity)
    {
        int error = RegOpenKeyEx(new(unchecked((int)0x80000002)), path, 0, 0x101, out nint key);
        if (error == 2) return null;
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        try {
            return FixedRegistryData.Read(name, capacity, bytes => {
                uint length = (uint)bytes.Length;
                int result = RegQueryValueEx(key, name, 0, out uint type, bytes, ref length);
                return new(result, type, length);
            });
        } finally { RegCloseKey(key); }
    }
    private static Initialization? LoadInitialization()
    {
        if (!File.Exists(JournalPath)) return null;
        SecureStorage.RecoveryFile(JournalPath);
        if (new FileInfo(JournalPath).Length > 8192) throw new IOException("共通設定の保存記録が不正です。");
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(JournalPath), Contract.Json) ?? throw new IOException("共通設定の保存記録を読めません。");
        if (Contract.Hash(envelope.Data) != envelope.Sha256) throw new IOException("共通設定の保存記録が一致しません。");
        var record = JsonSerializer.Deserialize<Initialization>(envelope.Data, Contract.Json);
        if (record?.Values.Length != 9 || record.State is not ("Preparing" or "Committed") || record.Values.Where((value, index) => !Valid(index + 1, value)).Any())
            throw new IOException("共通設定の保存記録の内容が不正です。");
        return record;
    }
    private static void SaveInitialization(Initialization record)
    {
        SecureStorage.Directory(System.IO.Path.GetDirectoryName(JournalPath)!);
        if (File.Exists(JournalPath)) SecureStorage.RecoveryFile(JournalPath);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, Contract.Json);
        string temp = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new Envelope(Contract.Hash(data), data), Contract.Json)); stream.Flush(true);
        }
        SecureStorage.File(temp); File.Move(temp, JournalPath, true);
        if (LoadInitialization()?.State != record.State) throw new IOException("共通設定の保存記録を確認できません。");
    }
    private static bool Valid(int id, float value) => id switch {
        1 or 3 or 8 => value is 0 or 1,
        2 => float.IsFinite(value) && value is >= -12 and <= 36,
        4 => float.IsFinite(value) && value is >= -80 and <= -10,
        5 => float.IsFinite(value) && value is >= 1 and <= 30,
        6 => float.IsFinite(value) && value is >= 50 and <= 500,
        7 => float.IsFinite(value) && value is >= 30 and <= 500,
        9 => float.IsFinite(value) && value is >= 2 and <= 12,
        _ => false
    };
    internal static float[] Read()
    {
        var values = new float[9];
        for (int id = 1; id <= values.Length; id++) {
            var value = SmallValue(PathName + @"\User", id.ToString(), 4);
            if (value?.Type != 3 || value.Data.Length != 4 || !Valid(id, values[id - 1] = BitConverter.ToSingle(value.Data)))
                throw new IOException("共通設定を読み込めません。セットアップで修復してください。");
        }
        return values;
    }
    internal static void Initialize(Receipt? legacyOrigin = null)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var initialization = LoadInitialization();
        using var existing = machine.OpenSubKey(PathName + @"\User");
        if (existing != null) {
            ValidateParameterKey(existing, true);
            if (initialization?.State == "Preparing") {
                for (int id = 1; id <= 9; id++) {
                    var actual = SmallValue(PathName + @"\User", id.ToString(), 4);
                    if (actual != null && !actual.Same(new RawValue(id.ToString(), 3, BitConverter.GetBytes(initialization.Values[id - 1]))))
                        throw new IOException("保存途中の共通設定が変更されています。保存記録を保持しています。");
                }
                using var writable = machine.OpenSubKey(PathName + @"\User", true) ?? throw new IOException("共通設定を保存できません。");
                ProtectParameterKey(writable);
                for (int id = 1; id <= 9; id++) if (SmallValue(PathName + @"\User", id.ToString(), 4) == null)
                    writable.SetValue(id.ToString(), BitConverter.GetBytes(initialization.Values[id - 1]), RegistryValueKind.Binary);
                SaveInitialization(initialization with { State = "Committed" });
            }
            Read();
            using var existingPreview = machine.OpenSubKey(PathName + @"\Volatile");
            if (existingPreview != null) ValidateParameterKey(existingPreview, true);
            else {
                using var recreated = machine.CreateSubKey(PathName + @"\Volatile", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
                ProtectParameterKey(recreated);
            }
            return;
        }
        if (initialization?.State == "Committed") throw new IOException("保存済みの共通設定が見つかりません。設定を初期化していません。");
        var values = initialization?.Values.ToArray() ?? Defaults.ToArray();
        // A migration may use the explicitly recorded old installation's sole
        // authority. Enumeration order/default device/name is never authority.
        if (legacyOrigin != null && initialization == null) {
            string user = legacyOrigin.Target.FxPath + "\\" + Contract.Context + @"\User";
            for (int id = 1; id <= values.Length; id++) {
                var old = SmallValue(user, "{91795F52-2DC0-4E20-A732-58816672E635}," + id, 12);
                bool defaults = old == null;
                old ??= SmallValue(legacyOrigin.Target.FxPath + "\\" + Contract.Context + @"\Default",
                    "{91795F52-2DC0-4E20-A732-58816672E635}," + id, 32);
                if (old == null) continue;
                if (!Valid(id, values[id - 1] = LegacyProfileValue.Decode(old, defaults)))
                    throw new IOException("以前の設定を移行できません。共通設定は変更していません。");
            }
        }
        SaveInitialization(new(values, "Preparing"));
        using var root = machine.CreateSubKey(PathName, true);
        using var userKey = root.CreateSubKey("User", true);
        if (userKey.ValueCount != 0 || userKey.SubKeyCount != 0) throw new IOException("共通設定の保存先が変更されています。変更していません。");
        // Protect existing administrator-owned parents; only new parameter keys
        // receive the deliberately narrow parameter-write grant.
        ProtectParameterKey(userKey);
        using var priorPreview = root.OpenSubKey("Volatile");
        if (priorPreview != null) ValidateParameterKey(priorPreview, true);
        else { using var preview = root.CreateSubKey("Volatile", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile); ProtectParameterKey(preview); }
        for (int id = 1; id <= values.Length; id++) userKey.SetValue(id.ToString(), BitConverter.GetBytes(values[id - 1]), RegistryValueKind.Binary);
        if (!Read().SequenceEqual(values)) throw new IOException("共通設定の保存を確認できません。");
        SaveInitialization(new(values, "Committed"));
    }
    private static void ProtectParameterKey(RegistryKey key)
    {
        var security = new RegistrySecurity(); security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new(new SecurityIdentifier(sid, null), RegistryRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.QueryValues | RegistryRights.SetValue | RegistryRights.ReadPermissions, AccessControlType.Allow));
        key.SetAccessControl(security); ValidateParameterKey(key, true);
    }
    private static void ValidateParameterKey(RegistryKey key, bool writableParameters)
    {
        var security = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        string? owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        if (owner is not ("S-1-5-18" or "S-1-5-32-544")) throw new IOException("共通設定の管理権限を確認できません。");
        const RegistryRights mutations = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership;
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
            string sid = rule.IdentityReference.Value;
            if (rule.AccessControlType == AccessControlType.Allow && sid is not ("S-1-5-18" or "S-1-5-32-544")
                && ((rule.RegistryRights & mutations) != 0 && !(writableParameters && sid == "S-1-5-32-545" && (rule.RegistryRights & mutations) == RegistryRights.SetValue)))
                throw new IOException("共通設定の変更範囲が不正です。");
        }
    }
}
