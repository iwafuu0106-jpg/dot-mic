using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotMic.Setup;

internal static class ResidentRegistration
{
    private static string RecordPath => Path.Combine(Contract.RecoveryRoot, "Manager", "service.json");
    private sealed class Record
    {
        public string Command { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Status { get; set; } = "Creating";
        public string? PreviousCommand { get; set; }
        public string? PreviousHash { get; set; }
    }
    private sealed record Envelope(string Sha256, byte[] Data);
    private sealed class Handle(nint value) : IDisposable
    {
        internal nint Value = value;
        public void Dispose() { if (Value != 0) { CloseServiceHandle(Value); Value = 0; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration { public uint Type, StartType, ErrorControl; public nint Binary, Group; public uint Tag; public nint Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Type, Current, Accepted, Error, Specific, Checkpoint, Hint; }
    [StructLayout(LayoutKind.Sequential)] private struct RecoveryActions { public uint Reset; public nint Reboot, Command; public uint Count; public nint Actions; }
    [StructLayout(LayoutKind.Sequential)] private struct RecoveryAction { public uint Type, Delay; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error, string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig2(nint service, uint level, nint information);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(nint service, nint buffer, uint capacity, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out State state);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out State state);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool StartService(nint service, uint count, nint args);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    private static void Check(bool succeeded) { if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static Handle Scm() { nint value = OpenSCManager(null, null, 3); if (value == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return new(value); }
    private static Handle? Open(Handle manager)
    {
        nint value = OpenService(manager.Value, ResidentHost.ServiceName, 0x10037);
        if (value == 0) { int error = Marshal.GetLastWin32Error(); if (error == 1060) return null; throw new Win32Exception(error); }
        return new(value);
    }
    private static Record? ReadRecord()
    {
        if (!File.Exists(RecordPath)) return null;
        SecureStorage.RecoveryFile(RecordPath);
        if (new FileInfo(RecordPath).Length > 32768) throw new IOException("自動設定の記録が不正です。");
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(RecordPath), Contract.Json) ?? throw new IOException("自動設定の記録を読み込めません。");
        if (Contract.Hash(envelope.Data) != envelope.Sha256) throw new IOException("自動設定の記録が一致しません。");
        return JsonSerializer.Deserialize<Record>(envelope.Data, Contract.Json) ?? throw new IOException("自動設定の記録が不正です。");
    }
    private static void Save(Record record)
    {
        SecureStorage.Directory(Path.GetDirectoryName(RecordPath)!);
        if (File.Exists(RecordPath)) SecureStorage.RecoveryFile(RecordPath);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, Contract.Json);
        string temp = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(JsonSerializer.SerializeToUtf8Bytes(new Envelope(Contract.Hash(data), data), Contract.Json)); stream.Flush(true); }
        SecureStorage.File(temp); File.Move(temp, RecordPath, true);
        if (ReadRecord()?.Command != record.Command) throw new IOException("自動設定の記録を保存できません。");
    }
    private static void ValidateOwned(Handle service, Record record)
    {
        QueryServiceConfig(service.Value, 0, 0, out uint size);
        if (size == 0 || size > 65536) throw new IOException("自動設定の登録を確認できません。");
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try {
            Check(QueryServiceConfig(service.Value, buffer, size, out _)); var config = Marshal.PtrToStructure<Configuration>(buffer);
            if (config.Type != 0x10 || Marshal.PtrToStringUni(config.Binary) != record.Command || Marshal.PtrToStringUni(config.Account) is not ("LocalSystem" or "NT AUTHORITY\\SYSTEM"))
                throw new IOException("自動設定の登録が変更されています。詳細を確認してください。");
        } finally { Marshal.FreeHGlobal(buffer); }
        if (!record.Command.StartsWith('"') || !record.Command.EndsWith("\" --manager-service", StringComparison.Ordinal)) throw new IOException("自動設定の実行先が不正です。");
        string executable = record.Command[1..^"\" --manager-service".Length];
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC", "Packages");
        if (!InstallPaths.Within(executable, packages) || Path.GetFileName(executable) != "DotMic.Setup.exe") throw new IOException("自動設定の実行先が管理対象外です。");
        SecureStorage.Validate(executable, false); SecureStorage.Validate(Path.GetDirectoryName(executable)!, true);
        if (Contract.FileHash(executable) != record.Hash) throw new IOException("自動設定の実行ファイルが変更されています。");
    }
    internal static void Install()
    {
        string executable = PackageSource.CleanupExecutable(); Transaction.ValidatePayload(PackageSource.DirectoryPath);
        var next = new Record { Command = "\"" + executable + "\" --manager-service", Hash = Contract.FileHash(executable) };
        using var manager = Scm(); using var current = Open(manager);
        if (current == null) {
            var previous = ReadRecord();
            if (previous != null && previous.Status is not ("Removed" or "Creating")) throw new IOException("自動設定の登録が見つかりません。復旧を完了してください。");
            Save(next);
            nint value = CreateService(manager.Value, ResidentHost.ServiceName, "DOT MIC マイク自動設定", 0x10037, 0x10, 2, 1, next.Command, null, 0, null, null, null);
            if (value == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var created = new Handle(value); ValidateOwned(created, next); ConfigureRecovery(created); Start(created);
        } else {
            var previous = ReadRecord() ?? throw new IOException("同じ名前の管理サービスがあります。変更していません。");
            previous = RecognizePending(current, previous);
            if (previous.Command != next.Command) {
                Stop(current);
                // Persist the next command before changing SCM. A recovery can
                // inspect this journal; an unexpected command is never adopted.
                string backup = RecordPath + ".before-" + Guid.NewGuid().ToString("N");
                SecureStorage.CopyNewProtectedFile(RecordPath, backup);
                next.Status = "Updating"; next.PreviousCommand = previous.Command; next.PreviousHash = previous.Hash;
                Save(next); Check(ChangeServiceConfig(current.Value, 0xffffffff, 2, 0xffffffff, next.Command, null, 0, null, null, null, null));
            }
            ValidateOwned(current, next); ConfigureRecovery(current); Start(current);
        }
        next.Status = "Installed"; next.PreviousCommand = next.PreviousHash = null; Save(next);
    }
    private static void ConfigureRecovery(Handle service)
    {
        var actions = new[] { new RecoveryAction { Type = 1, Delay = 60000 }, new RecoveryAction { Type = 1, Delay = 120000 },
            new RecoveryAction { Type = 1, Delay = 300000 }, new RecoveryAction { Type = 0, Delay = 0 } };
        int stride = Marshal.SizeOf<RecoveryAction>();
        nint actionBuffer = Marshal.AllocHGlobal(stride * actions.Length), info = Marshal.AllocHGlobal(Marshal.SizeOf<RecoveryActions>());
        try {
            for (int i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], actionBuffer + i * stride, false);
            Marshal.StructureToPtr(new RecoveryActions { Reset = 86400, Count = (uint)actions.Length, Actions = actionBuffer }, info, false);
            Check(ChangeServiceConfig2(service.Value, 2, info));
            Marshal.WriteInt32(info, 1); Check(ChangeServiceConfig2(service.Value, 4, info));
        } finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(actionBuffer); }
    }
    private static void Start(Handle service)
    {
        bool attempted = false;
        ServiceTransition.Run(() => {
            Check(QueryServiceStatus(service.Value, out var status));
            if (attempted && status.Current == 1 && status.Error != 0) throw new Win32Exception(checked((int)status.Error), "自動適用の開始に失敗しました。");
            return new(status.Current, status.Checkpoint, status.Hint, status.Error);
        }, command => {
            if (command != ServiceTransition.Command.Start) throw new IOException("自動適用の状態を確認できません。");
            attempted = true;
            Check(StartService(service.Value, 0, 0));
        }, () => Environment.TickCount64, Thread.Sleep, ResidentHost.ServiceName, true);
    }
    private static Record RecognizePending(Handle service, Record record)
    {
        try { ValidateOwned(service, record); return record; }
        catch (IOException) when (record.Status == "Updating" && record.PreviousCommand != null && record.PreviousHash != null) {
            var prior = new Record { Command = record.PreviousCommand, Hash = record.PreviousHash, Status = "Installed" };
            ValidateOwned(service, prior); return prior;
        }
    }
    private static void Stop(Handle service)
    {
        ServiceTransition.Run(() => { Check(QueryServiceStatus(service.Value, out var status)); return new(status.Current, status.Checkpoint, status.Hint, status.Error); },
            command => Check(ControlService(service.Value, 1, out _)), () => Environment.TickCount64, Thread.Sleep, ResidentHost.ServiceName, false);
    }
    internal static void Remove()
    {
        using var manager = Scm(); using var current = Open(manager); var record = ReadRecord();
        if (current == null) { if (record != null) { record.Status = "Removed"; Save(record); } return; }
        if (record == null) throw new IOException("自動設定の所有情報がありません。登録を変更していません。");
        record = RecognizePending(current, record); record.Status = "Removing"; Save(record); Stop(current); Check(DeleteService(current.Value)); record.Status = "Removed"; Save(record);
    }
}
