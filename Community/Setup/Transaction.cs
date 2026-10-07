using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DotMic.Setup;

internal sealed class Transaction
{
    internal Receipt Receipt { get; }
    internal string DirectoryPath { get; }
    private readonly string package;
    private readonly Payload payload;
    internal bool AdvancedPermissionApproved { get; set; }
    internal string ReceiptPath => Path.Combine(DirectoryPath, "snapshot.json");
    private Transaction(Receipt receipt, string directory, string package, Payload payload, bool advanced)
    { Receipt = receipt; DirectoryPath = directory; this.package = package; this.payload = payload; AdvancedPermissionApproved = advanced; }
    internal static void ProtectDirectory(string path)
    {
        SecureStorage.Directory(path);
    }
    private static void Atomic(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        SecureStorage.File(temp);
        File.Move(temp, path, true);
    }
    internal void Save()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Receipt, Contract.Json);
        string baseline = Path.Combine(DirectoryPath, "snapshot-base.json");
        if (!File.Exists(baseline)) Atomic(baseline, JsonSerializer.SerializeToUtf8Bytes(new Envelope(Contract.Hash(bytes), bytes), Contract.Json));
        Atomic(ReceiptPath, JsonSerializer.SerializeToUtf8Bytes(new Envelope(Contract.Hash(bytes), bytes), Contract.Json));
        var verify = Read(ReceiptPath); if (verify.TransactionId != Receipt.TransactionId) throw new IOException("Snapshotのatomic保存・整合性確認に失敗しました。");
    }
    private sealed record Envelope(string Sha256, byte[] Data);
    internal static Receipt Read(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Path.GetFullPath(Contract.RecoveryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "snapshot.json") throw new IOException("管理者管理のRecovery snapshotを選択してください。");
        SecureStorage.RecoveryFile(path);
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path), Contract.Json) ?? throw new IOException("Snapshotがありません。");
        if (Contract.Hash(envelope.Data) != envelope.Sha256) throw new IOException("Snapshot checksumが一致しません。registryは変更しません。");
        var receipt = JsonSerializer.Deserialize<Receipt>(envelope.Data, Contract.Json) ?? throw new IOException("Snapshot形式が不正です。");
        if (receipt.Schema != 1 || receipt.Version != Contract.Version || receipt.Target == null) throw new IOException("Snapshotのschema/version/対象情報が不正です。");
        string capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";
        if (!receipt.Target.FxPath.StartsWith(capture, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(receipt.Target.FxPath[capture.Length..], @"^\{[0-9a-fA-F-]{36}\}\\FxProperties$")) throw new IOException("Snapshot capture scopeが不正です。");
        bool Scope(string p) => new[] { receipt.Target.FxPath, Contract.ComPath, Contract.ApoPath, Contract.ApoClassesPath, Contract.ConfigPath }.Any(root => p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
        foreach (var edit in receipt.Edits) if (!Scope(edit.Path) && !(edit.Path.Equals(Contract.AudioPath, StringComparison.OrdinalIgnoreCase) && edit.Name == "DisableProtectedAudioDG")) throw new IOException("Snapshotが許可外registry値を指定しています。");
        foreach (var pending in receipt.PendingSecurityRestore) if (!receipt.PendingSecurityOriginal.ContainsKey(pending) || !receipt.PendingSecurityExpected.ContainsKey(pending) || !receipt.AdvancedKeys.Contains(pending, StringComparer.OrdinalIgnoreCase)) throw new IOException("ACL復元記録が不正です。");
        foreach (var file in receipt.Files) { SafePath(Contract.InstallRoot, file.Relative); if (!file.Relative.StartsWith("APO/", StringComparison.Ordinal)) throw new IOException("Snapshot file scopeが不正です。"); }
        if (receipt.Application != null) ApplicationInstaller.Validate(receipt.Application);
        if (receipt.ApplicationRemovalJournal != null && !receipt.ApplicationRemovalJournal.Equals(Path.Combine(Path.GetDirectoryName(path)!, "application-removal.json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("アプリ削除の復旧記録が保存先の範囲外です。");
        return receipt;
    }
    internal static Payload ValidatePayload(string package)
    {
        var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(Path.Combine(package, "payload.json")), Contract.Json) ?? throw new IOException("Community payload manifestがありません。");
        if (payload.Version != Contract.Version || payload.Files.Count == 0) throw new IOException("Community payload versionが不正です。");
        using var lockFile = typeof(Transaction).Assembly.GetManifestResourceStream("DotMic.Setup.ApoPayload.lock.json") ?? throw new IOException("Production dependency lockがありません。");
        var locked = JsonSerializer.Deserialize<PayloadFile[]>(lockFile, Contract.Json) ?? throw new IOException("Production dependency lockが不正です。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in payload.Files) {
            SafePath(package, file.Path); if (!paths.Add(file.Path)) throw new IOException("Duplicate payload path");
            if (Contract.FileHash(SafePath(package, file.Path)) != file.Hash) throw new IOException("必要ファイルのhashが一致しません: " + file.Path);
        }
        foreach (var file in locked) if (!payload.Files.Any(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && f.Hash == file.Hash)) throw new IOException("Production dependency/modelの固定hashが一致しません: " + file.Path);
        if (payload.Files.Count(f => f.Path.StartsWith("APO/", StringComparison.OrdinalIgnoreCase)) != locked.Length) throw new IOException("固定APO依存以外の追加DLLは配置しません。");
        if (!paths.Contains("APO/DotMic.ApoGate.dll") || !paths.Contains("APO/dpdfnet2_48khz_hr.onnx") || !paths.Contains("UI/DotMic.ApoSettings.dll") || !paths.Contains("DotMic.Integration.dll")) throw new IOException("APO/model/CAPX/setup helperが不足しています。");
        if (payload.ApoHash != locked.Single(f => f.Path == "APO/DotMic.ApoGate.dll").Hash || Contract.FileHash(SafePath(package, "APO/DotMic.ApoGate.dll")) != payload.ApoHash || Contract.FileHash(SafePath(package, "APO/dpdfnet2_48khz_hr.onnx")) != "7F0575A5CEC0BA4FFD8F8BD657E06D007E4CCDD955D76FAAB922B9D3291DC14B") throw new IOException("署名metadataのみを除いたproduction APO / 固定modelが一致しません。");
        return payload;
    }
    internal static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(s => s is ".." or "." or "")) throw new IOException("Payload relative path is invalid.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Payload escapes its root.");
        var current = Path.GetDirectoryName(path); while (current != null && current.Length >= Path.GetFullPath(root).Length) { if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Payload path contains a reparse point."); current = Path.GetDirectoryName(current); }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Payload file is a reparse point."); return path;
    }
    internal static Transaction Prepare(EndpointIdentity target, string package, string operation, bool advanced)
    {
        var payload = ValidatePayload(package); RawRegistry.Privilege("SeSecurityPrivilege");
        target = EndpointIdentity.Resolve(target, Integration.Endpoints());
        var receipt = new Receipt { Target = target, Operation = operation, BootBefore = (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToString("O") };
        receipt.WindowsEffectsDisabled = RawRegistry.Value(target.FxPath, "{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},5");
        receipt.AudioDependents = AudioService.Dependents();
        string directory = Path.Combine(Contract.RecoveryRoot, receipt.TransactionId.ToString("D"));
        ProtectDirectory(Contract.RecoveryRoot); ProtectDirectory(directory);
        var transaction = new Transaction(receipt, directory, package, payload, advanced);
        // Elevated COM validation executes only an independently pinned, protected copy.
        string validation = Path.Combine(directory, "validation"); ProtectDirectory(validation);
        foreach (var file in payload.Files.Where(f => f.Path.StartsWith("APO/", StringComparison.Ordinal))) {
            string destination = SafePath(validation, file.Path); ProtectDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(SafePath(package, file.Path), destination, false); SecureStorage.File(destination);
            if (Contract.FileHash(destination) != file.Hash) throw new IOException("保護された検証payloadのhashが不一致です。");
        }
        receipt.RegistrationExpected = Integration.RegistrationPlan(SafePath(validation, "APO/DotMic.ApoGate.dll"));
        foreach (var root in new[] { target.FxPath, Contract.ComPath, Contract.ApoPath, Contract.ApoClassesPath, Contract.ConfigPath }) receipt.Keys.AddRange(RawRegistry.Tree(root));
        receipt.Keys.Add(RawRegistry.KeyOnly(Contract.AudioPath));
        void Parents(string path)
        {
            var parent = path;
            while (true) {
                var image = receipt.Keys.FirstOrDefault(k => k.Path.Equals(parent, StringComparison.OrdinalIgnoreCase));
                if (image == null) { image = RawRegistry.KeyOnly(parent); receipt.Keys.Add(image); }
                if (image.Exists) break;
                int slash = parent.LastIndexOf('\\'); if (slash < 0) throw new IOException("Registry parent snapshot unavailable"); parent = parent[..slash];
            }
        }
        foreach (var root in new[] { Contract.ApoPath, Contract.ApoClassesPath }) Parents(root);
        void Add(string path, RawValue? after, string? delete = null)
        {
            string name = after?.Name ?? delete ?? throw new ArgumentNullException(nameof(after));
            receipt.Edits.Add(new() { Path = path, Name = name, Before = RawRegistry.Value(path, name), After = after });
            Parents(path);
        }
        Add(Contract.ComPath, RawValue.Text("", "DOT MIC Capture MFX"));
        Add(Contract.ComPath + @"\InprocServer32", RawValue.Text("", SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll")));
        Add(Contract.ComPath + @"\InprocServer32", RawValue.Text("ThreadingModel", "Both"));
        Add(Contract.AudioPath, RawValue.Dword("DisableProtectedAudioDG", 1));
        receipt.ProtectedAudioWasAlreadyOne = receipt.Edits.Last().Before?.Same(RawValue.Dword("DisableProtectedAudioDG", 1)) == true;
        Add(target.FxPath, RawValue.Text(Contract.FxFormat + ",0", Contract.Microphone));
        Add(target.FxPath, RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid));
        Add(target.FxPath, RawValue.Multi(Contract.ModeFormat + ",6", Contract.DefaultMode));
        // Observed production namespace binding selects the PnP DLL. Remove ONLY our
        // CLSID's binding so the identical global class resolves to the fixed directory.
        Add(target.FxPath, null, Contract.Clsid + ",100");
        string context = target.FxPath + "\\" + Contract.Context;
        Add(context, RawValue.Text(Contract.FxFormat + ",0", Contract.Microphone));
        string[] defaults = ["1", "0", "0", "-48", "5", "160", "120", "0", "6"];
        for (int id = 1; id <= defaults.Length; id++) {
            string name = "{91795F52-2DC0-4E20-A732-58816672E635}," + id;
            Add(context + @"\Default", id is 1 or 3 or 8 ? RawValue.Dword(name, uint.Parse(defaults[id - 1])) : RawValue.Text(name, defaults[id - 1]));
        }
        // Key-only stores must exist, without replacing arbitrary User values. These
        // four safe initial controls are committed through the existing CAPX API later.
        foreach (var store in new[] { "User", "Volatile" }) { string path = context + "\\" + store; if (!receipt.Keys.Any(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase))) receipt.Keys.Add(RawRegistry.KeyOnly(path)); }
        var priorOrigin = RawRegistry.Value(Contract.ConfigPath, "OriginReceipt")?.Display;
        if (!string.IsNullOrEmpty(priorOrigin)) {
            var origin = Read(priorOrigin);
            var resolvedOrigin = EndpointIdentity.Resolve(origin.Target, Integration.Endpoints());
            if (resolvedOrigin.EndpointId != target.EndpointId) throw new IOException("既存Communityの対象と選択マイクが異なります。既存統合を削除してから対象を変更してください。");
            if (origin.Status is "RolledBack" or "Prepared") throw new IOException("既存origin receiptが導入済み状態ではありません。Recoveryで状態を確認してください。");
        }
        foreach (var pair in new[] { ("StableId", target.StableId), ("ContainerId", target.ContainerId), ("PhysicalInterface", target.PhysicalInterface), ("CachedEndpointId", target.EndpointId), ("InstallDir", Contract.InstallRoot), ("ApoPath", SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll")), ("ApoHash", payload.ApoHash), ("OriginReceipt", !string.IsNullOrEmpty(priorOrigin) ? priorOrigin : transaction.ReceiptPath), ("LatestReceipt", transaction.ReceiptPath), ("Version", Contract.Version) }) Add(Contract.ConfigPath, RawValue.Text(pair.Item1, pair.Item2));
        Add(Contract.ConfigPath, RawValue.Text("RequiredFiles", JsonSerializer.Serialize(payload.Files.Where(f => f.Path.StartsWith("APO/", StringComparison.Ordinal)).ToArray(), Contract.Json)));
        // The audiodg payload keeps its accepted fixed location. Application placement is a separate bounded journal.
        foreach (var file in payload.Files.Where(f => f.Path.StartsWith("APO/", StringComparison.Ordinal))) {
            string destination = SafePath(Contract.InstallRoot, file.Path); bool existed = File.Exists(destination);
            for (string? parent = Path.GetDirectoryName(destination); parent != null && parent.Length >= Path.GetFullPath(Contract.InstallRoot).Length; parent = Path.GetDirectoryName(parent)) if (Directory.Exists(parent)) SecureStorage.Validate(parent, true);
            if (existed) SecureStorage.Validate(destination, false);
            var image = new FileEdit { Relative = file.Path, Hash = file.Hash, Existed = existed, BeforeHash = existed ? Contract.FileHash(destination) : null }; receipt.Files.Add(image);
            if (existed && image.BeforeHash != image.Hash) { string backup = SafePath(Path.Combine(directory, "files"), file.Path); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(destination, backup, false); if (Contract.FileHash(backup) != image.BeforeHash) throw new IOException("配置ファイルのrollback snapshotに失敗しました。"); }
        }
        transaction.Save();
        // Detect concurrent configuration edits before the first Windows Audio write.
        foreach (var edit in receipt.Edits) { var now = RawRegistry.Value(edit.Path, edit.Name); if (edit.Before == null ? now != null : !edit.Before.Same(now)) throw new IOException("Snapshot後に変更対象値が変わりました。新しいtransactionで再確認してください。"); }
        EndpointIdentity.Resolve(target, Integration.Endpoints()); return transaction;
    }
    internal string Description()
    {
        var text = new StringBuilder(); text.AppendLine(Receipt.Target.FriendlyName); text.AppendLine("方式: legacy COM / AudioEngine登録、同じMFX / DEFAULT。Microsoft certified / WHQLではありません。");
        var mfx = Receipt.Edits.First(e => e.Path == Receipt.Target.FxPath && e.Name == Contract.FxFormat + ",6");
        if (mfx.Before != null) text.AppendLine("既存MFX: " + mfx.Before.Display + "。同じ位置の効果が停止・置換される可能性があります。現在値をraw型/data付きで保存し、削除時に復元できます。");
        var binding = Receipt.Edits.First(e => e.Name == Contract.Clsid + ",100"); if (binding.Before != null) text.AppendLine("現在のDOT MIC PnP参照をlegacy参照に切り替えます。PnP package自体は削除しません。");
        if (Receipt.WindowsEffectsDisabled != null) text.AppendLine("Windows Audio Effectsの無効化値: " + Receipt.WindowsEffectsDisabled.Display + "。無効化されている場合はDOT MIC MFXが実行されません。Windowsのマイク設定でAudio Enhancementsを有効にしてから再確認できます。未知のeffect値は自動変更しません。");
        text.AppendLine(Receipt.ProtectedAudioWasAlreadyOne ? "DisableProtectedAudioDGは導入前から1です。DOT MICが新たに設定した値として扱いません。" : "DisableProtectedAudioDGを1へ変更します。Windows全体のProtected Audio / secure audio path、一部DRMコンテンツやサービスに影響する可能性があります。");
        text.AppendLine("現在の音声再生・通話はAudio service再初期化で一時的に切断されます。PCを自動再起動しません。");
        if (Receipt.AudioDependents.Count > 0) { text.AppendLine("追加の一時停止・元稼働状態への復元が必要な依存サービス:"); foreach (var service in Receipt.AudioDependents) text.AppendLine("  " + service.DisplayName + " (" + service.Name + ")。このサービスの機能も一時中断します。"); }
        foreach (var key in Receipt.Edits.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !RawRegistry.CanWrite(p))) {
            var image = Receipt.Keys.Where(k => k.Exists && (key.Equals(k.Path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(k.Path + "\\", StringComparison.OrdinalIgnoreCase))).OrderByDescending(k => k.Path.Length).FirstOrDefault();
            string owner = image == null ? "取得不能" : new RawSecurityDescriptor(image.Security, 0).Owner?.Value ?? "未設定";
            text.AppendLine("追加変更が必要: " + key + " / 現owner: " + owner + "。必要なSetValueまたはCreateSubKeyだけを対象keyへ一時追加し、書込み直後に元owner/ACLへ復元します。親tree全体へFull Controlは付与しません。");
        }
        text.AppendLine("Snapshot: " + ReceiptPath); return text.ToString();
    }
    internal void ConfigureApplication(string packagePath, string destination, bool desktopShortcut)
    {
        Receipt.Application = ApplicationInstaller.Prepare(this, packagePath, destination, desktopShortcut);
        foreach (var pair in new[] { ("ApplicationDir", Receipt.Application.Root), ("ApplicationReceipt", ReceiptPath), ("ApplicationPackageDir", packagePath) })
            Receipt.Edits.Add(new() { Path = Contract.ConfigPath, Name = pair.Item1, Before = RawRegistry.Value(Contract.ConfigPath, pair.Item1), After = RawValue.Text(pair.Item1, pair.Item2) });
        Save();
    }
    private void Write(Edit edit)
    {
        var now = RawRegistry.Value(edit.Path, edit.Name);
        if (edit.Before == null ? now != null : !edit.Before.Same(now)) throw new IOException("書込み直前にregistry値が変わりました: " + edit.Path + " / " + edit.Name);
        edit.Applied = true; Save(); RawRegistry.Write(edit, Receipt.Keys, AdvancedPermissionApproved, Receipt.AdvancedKeys, Receipt.PendingSecurityRestore, Receipt.PendingSecurityExpected, Receipt.PendingSecurityOriginal, Save);
    }
    private List<Edit> ApiDifferences()
    {
        var differences = new List<Edit>();
        foreach (var root in new[] { Contract.ApoPath, Contract.ApoClassesPath }) {
            var after = RawRegistry.Tree(root); var before = Receipt.Keys.Where(k => k.Path.Equals(root, StringComparison.OrdinalIgnoreCase) || k.Path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var path in after.Select(k => k.Path).Concat(before.Select(k => k.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                var a = after.FirstOrDefault(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); var b = before.FirstOrDefault(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                foreach (var name in (a?.Values.Select(v => v.Name) ?? []).Concat(b?.Values.Select(v => v.Name) ?? []).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    var prior = b?.Find(name); var now = a?.Find(name);
                    if (prior == null ? now != null : !prior.Same(now)) differences.Add(new() { Path = path, Name = name, Before = prior, After = now, Applied = true });
                }
            }
        }
        return differences;
    }
    private void CaptureApiEdits(bool explicitRecovery = false)
    {
        var differences = ApiDifferences();
        foreach (var edit in differences) {
            bool expected = new[] { Contract.ApoPath, Contract.ApoClassesPath }.Contains(edit.Path, StringComparer.OrdinalIgnoreCase) && Receipt.RegistrationExpected.Any(v => v.Name.Equals(edit.Name, StringComparison.OrdinalIgnoreCase) && v.Same(edit.After));
            if (!expected && !explicitRecovery) throw new IOException("RegisterAPOの変更が保存したmetadata contractと異なります。Recoveryで差分を確認してください: " + edit.Path + " / " + edit.Name);
        }
        Receipt.Edits.AddRange(differences);
        foreach (var root in new[] { Contract.ApoPath, Contract.ApoClassesPath }) foreach (var after in RawRegistry.Tree(root).Where(k => k.Exists)) if (!Receipt.Keys.Any(k => k.Path.Equals(after.Path, StringComparison.OrdinalIgnoreCase))) Receipt.Keys.Add(new() { Path = after.Path });
        Receipt.RegistrationPending = false; Save();
    }
    private void CheckRegistrationBaseline()
    {
        if (ApiDifferences().Count != 0) throw new IOException("確認後にAPO registrationの事前値が変わりました。新しいsnapshotを作成してください。");
        foreach (var root in new[] { Contract.ApoPath, Contract.ApoClassesPath }) foreach (var after in RawRegistry.Tree(root)) {
            var before = Receipt.Keys.FirstOrDefault(k => k.Path.Equals(after.Path, StringComparison.OrdinalIgnoreCase));
            if (before == null || before.Exists != after.Exists) throw new IOException("確認後にAPO registrationのkey構造が変わりました。");
        }
    }
    private void Revalidate()
    {
        var current = EndpointIdentity.Resolve(Receipt.Target, Integration.Endpoints());
        if (current.FxPath != Receipt.Target.FxPath) throw new IOException("確認後にendpoint locationが変わりました。マイクを再確認し、新しいsnapshotを作成してください。");
        foreach (var edit in Receipt.Edits) { var now = RawRegistry.Value(edit.Path, edit.Name); if (edit.Before == null ? now != null : !edit.Before.Same(now)) throw new IOException("確認後に変更対象値が変わりました。変更内容を再確認してください: " + edit.Path + " / " + edit.Name); }
        foreach (var file in Receipt.Files) { string path = SafePath(Contract.InstallRoot, file.Relative); if (file.Existed != File.Exists(path) || (file.Existed && Contract.FileHash(path) != file.BeforeHash)) throw new IOException("確認後に配置先ファイルが変わりました。snapshotを作成し直してください。"); }
        foreach (var image in Receipt.Keys.Where(k => k.Exists)) { var now = RawRegistry.KeyOnly(image.Path); if (!now.Exists || !now.Security.AsSpan().SequenceEqual(image.Security)) throw new IOException("確認後にregistry securityが変わりました。snapshotを作成し直してください: " + image.Path); }
    }
    internal async Task Apply(bool failVerification = false)
    {
        if (!Receipt.AudioRestartConsent || !Receipt.ProtectedAudioConsent || !Receipt.ReplacementConsent) throw new OperationCanceledException("必要な変更への同意がありません。");
        if (Receipt.AudioDependents.Count > 0 && !Receipt.DependentServiceConsent) throw new OperationCanceledException("追加の依存サービスの一時停止・復元への同意がありません。");
        AudioService.ValidateDependents(Receipt.AudioDependents);
        Revalidate();
        CheckRegistrationBaseline();
        Receipt.Status = "Applying"; Save();
        try {
            ProtectDirectory(Contract.InstallRoot);
            ProtectDirectory(Path.Combine(Contract.InstallRoot, "APO"));
            ApplicationInstaller.Apply(this, package);
            foreach (var file in Receipt.Files.Where(f => f.BeforeHash != f.Hash)) {
                string source = SafePath(package, file.Relative), destination = SafePath(Contract.InstallRoot, file.Relative), staged = destination + "." + Receipt.TransactionId + ".stage";
                ProtectDirectory(Path.GetDirectoryName(destination)!);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                if (Contract.FileHash(staged) != file.Hash) throw new IOException("配置先staged payload hashが一致しません。");
                SecureStorage.File(staged);
                if (file.Existed != File.Exists(destination) || (file.Existed && Contract.FileHash(destination) != file.BeforeHash)) throw new IOException("配置直前に元ファイルが変わりました。");
                file.Applied = true; Save(); File.Move(staged, destination, true); SecureStorage.Validate(destination, false);
            }
            foreach (var file in Receipt.Files) if (Contract.FileHash(SafePath(Contract.InstallRoot, file.Relative)) != file.Hash) throw new IOException("配置先payload read-back failed");
            foreach (var edit in Receipt.Edits.ToArray()) Write(edit);
            CheckRegistrationBaseline(); Receipt.Status = "RegisteringAPO"; Receipt.RegistrationPending = true; Save();
            try { Integration.Registration(SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll"), true); } finally { CaptureApiEdits(); }
            foreach (var store in new[] { "User", "Volatile" }) {
                // Creating an empty context store is distinct from replacing its values.
                string path = Receipt.Target.FxPath + "\\" + Contract.Context + "\\" + store;
                if (RawRegistry.KeyOnly(path).Exists) continue;
                var marker = new Edit { Path = path, Name = "__DotMicSetupTransient", Before = RawRegistry.Value(path, "__DotMicSetupTransient"), After = RawValue.Dword("__DotMicSetupTransient", 1) };
                if (marker.Before != null) throw new IOException("Unexpected setup marker value"); Receipt.Edits.Add(marker); Write(marker);
                var remove = new Edit { Path = path, Name = marker.Name, Before = marker.After, After = null }; Receipt.Edits.Add(remove); Write(remove);
            }
            foreach (var pair in SetupDefaults.Controls) {
                string path = Receipt.Target.FxPath + "\\" + Contract.Context + "\\User", name = "{91795F52-2DC0-4E20-A732-58816672E635}," + pair.Item1;
                byte[] data = new byte[12]; BitConverter.GetBytes(4u).CopyTo(data, 0); BitConverter.GetBytes(1u).CopyTo(data, 4); BitConverter.GetBytes(pair.Item2).CopyTo(data, 8);
                var edit = new Edit { Path = path, Name = name, Before = RawRegistry.Value(path, name), After = new(name, 3, data), Applied = true }; Receipt.Edits.Add(edit);
                var drag = new Edit { Path = Receipt.Target.FxPath + "\\" + Contract.Context + "\\Volatile", Name = name, Before = RawRegistry.Value(Receipt.Target.FxPath + "\\" + Contract.Context + "\\Volatile", name), After = null, Applied = true }; Receipt.Edits.Add(drag); Save();
                Integration.Set(Receipt.Target.EndpointId, pair.Item1, pair.Item2); if (!edit.After.Same(RawRegistry.Value(path, name)) || RawRegistry.Value(drag.Path, name) != null) throw new IOException("CAPX safe User/Volatile commit read-back failed"); Save();
            }
            Receipt.Status = "AudioRestart"; Receipt.AudioRestartPending = true; Save(); AudioService.Restart(Receipt.AudioRestartConsent, Receipt.AudioDependents, Receipt.DependentServiceConsent); Receipt.AudioRestartPending = false; Save();
            var current = EndpointIdentity.Resolve(Receipt.Target, Integration.Endpoints()); Receipt.Diagnostics.Add("After restart: " + JsonSerializer.Serialize(current));
            Receipt.Status = "Verifying"; Save();
            if (failVerification) throw new IOException("診断用の一度だけの人工Verify失敗。");
            await Integration.Verify(current, SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll"), payload.ApoHash);
            Receipt.Status = "Committed"; Save();
        } catch (Exception failure) {
            Receipt.Diagnostics.Add("Apply failed: " + failure); try { Save(); } catch { /* Logging failure must not suppress compensation. */ }
            try { await Rollback(false, (_, _) => true, false); } catch (Exception rollback) { Receipt.Status = "RecoveryRequired"; Receipt.Diagnostics.Add("Rollback incomplete: " + rollback); try { Save(); } catch { } throw new IOException("Recovery Required。snapshotを保持しました: " + ReceiptPath, rollback); }
            throw new IOException("適用を確定せず導入前へrollbackしました。" + failure.Message, failure);
        }
    }
    internal static Transaction Existing(string path, bool advanced)
    {
        var receipt = Read(path); RawRegistry.Privilege("SeSecurityPrivilege");
        return new(receipt, Path.GetDirectoryName(Path.GetFullPath(path))!, "", new(Contract.Version, "", []), advanced);
    }
    internal static Transaction Reference(Receipt receipt, string package, Payload payload)
    {
        string directory = Path.Combine(Contract.RecoveryRoot, receipt.TransactionId.ToString("D")); ProtectDirectory(directory);
        return new(receipt, directory, package, payload, false);
    }
    internal void ReferenceWrite(Edit edit) => Write(edit);
    internal static Transaction RemoveAssociationFixture(string committedPath)
    {
        var installed = Read(committedPath); if (installed.Status != "Committed") throw new IOException("確定済み統合のRepair fixtureだけを実行できます。");
        var target = EndpointIdentity.Resolve(installed.Target, Integration.Endpoints());
        var userPath = target.FxPath + "\\" + Contract.Context + "\\User";
        foreach (var pair in new[] { (1u, 1f), (2u, 0f), (3u, 0f), (8u, 0f) }) {
            byte[] bytes = new byte[12]; BitConverter.GetBytes(4u).CopyTo(bytes, 0); BitConverter.GetBytes(1u).CopyTo(bytes, 4); BitConverter.GetBytes(pair.Item2).CopyTo(bytes, 8);
            string name = "{91795F52-2DC0-4E20-A732-58816672E635}," + pair.Item1;
            if (!new RawValue(name, 3, bytes).Same(RawRegistry.Value(userPath, name))) throw new IOException("安全なBypass1/Gain0/Gate0/NC0でのみRepair fixtureを実行できます。");
        }
        string mfx = Contract.FxFormat + ",6"; var before = RawRegistry.Value(target.FxPath, mfx);
        if (before == null || !before.Same(RawValue.Text(mfx, Contract.Clsid))) throw new IOException("Fixture前のDOT MIC MFX associationが一致しません。");
        RawRegistry.Privilege("SeSecurityPrivilege");
        var receipt = new Receipt { Operation = "AssociationLossFixture", Target = target, Keys = RawRegistry.Tree(target.FxPath) };
        receipt.Edits.Add(new() { Path = target.FxPath, Name = mfx, Before = before, After = null });
        string directory = Path.Combine(Contract.RecoveryRoot, receipt.TransactionId.ToString("D")); ProtectDirectory(directory);
        var tx = new Transaction(receipt, directory, "", new(Contract.Version, "", []), false); tx.Save();
        tx.Write(receipt.Edits[0]); receipt.Status = "FixtureApplied"; tx.Save(); return tx;
    }
    internal async Task Rollback(bool interactive, Func<string, string, bool> restoreConflict, bool keepProtectedAudio, bool restoreApplication = true)
    {
        try { await RollbackCore(interactive, restoreConflict, keepProtectedAudio, restoreApplication: restoreApplication); }
        catch (Exception e) { Receipt.Status = "RecoveryRequired"; Receipt.Diagnostics.Add("Recovery incomplete: " + e); try { Save(); } catch { } throw new IOException("Recovery Required。snapshotと回復情報を保持しました: " + ReceiptPath, e); }
    }
    internal async Task FinishUnloadedRollback()
    {
        if (Receipt.Status != "RecoveryRequired" || Receipt.RegistrationPending || Receipt.PendingSecurityRestore.Count != 0) throw new IOException("中断registry/ACLの復旧が必要なため、配置ファイルだけの後片付けはできません。");
        var current = EndpointIdentity.Resolve(Receipt.Target, Integration.Endpoints());
        if (current.FxPath != Receipt.Target.FxPath) throw new IOException("物理endpointのlocationが変わっています。通常Recoveryで確認してください。");
        foreach (var group in Receipt.Edits.Where(e => e.Applied).GroupBy(e => e.Path + "\0" + e.Name, StringComparer.OrdinalIgnoreCase)) {
            var original = group.First(); var now = RawRegistry.Value(original.Path, original.Name);
            if (original.Before == null ? now != null : !original.Before.Same(now)) throw new IOException("registryはまだ導入前状態へ復元されていません: " + original.Path + " / " + original.Name);
        }
        Integration.RequireInstalledModulesUnloaded();
        Receipt.Diagnostics.Add("Registry byte-exact before-state and absence of installed audiodg modules confirmed; completing files/capture recovery without restarting services."); Save();
        try { await RollbackCore(false, (_, _) => false, false, false); }
        catch (Exception e) { Receipt.Status = "RecoveryRequired"; Receipt.Diagnostics.Add(e.ToString()); try { Save(); } catch { } throw; }
    }
    internal async Task AuditRestored()
    {
        if (Receipt.Status != "RolledBack" || Receipt.AudioRestartPending || Receipt.PendingSecurityRestore.Count != 0 || Receipt.RegistrationPending) throw new IOException("復元完了状態ではありません。");
        var current = EndpointIdentity.Resolve(Receipt.Target, Integration.Endpoints());
        foreach (var group in Receipt.Edits.Where(e => e.Applied).GroupBy(e => e.Path + "\0" + e.Name, StringComparer.OrdinalIgnoreCase)) {
            var before = group.First(); string path = before.Path.StartsWith(Receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase) ? current.FxPath + before.Path[Receipt.Target.FxPath.Length..] : before.Path;
            var now = RawRegistry.Value(path, before.Name);
            if (before.Before == null ? now != null : !before.Before.Same(now)) throw new IOException("復元済みraw値との一致を確認できません: " + path + " / " + before.Name);
        }
        foreach (var file in Receipt.Files.Where(f => f.Applied)) { string path = SafePath(Contract.InstallRoot, file.Relative); if (file.Existed ? !File.Exists(path) || Contract.FileHash(path) != file.BeforeHash : File.Exists(path)) throw new IOException("復元済み配置ファイルとの一致を確認できません: " + file.Relative); }
        await Integration.VerifyCaptureOnly(current);
    }
    private async Task RollbackCore(bool interactive, Func<string, string, bool> restoreConflict, bool keepProtectedAudio, bool restartAudio = true, bool restoreApplication = true)
    {
        var current = EndpointIdentity.Resolve(Receipt.Target, Integration.Endpoints());
        string Map(string p) => p.StartsWith(Receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase) ? current.FxPath + p[Receipt.Target.FxPath.Length..] : p;
        foreach (var path in Receipt.PendingSecurityRestore.ToArray()) {
            if (!Receipt.PendingSecurityOriginal.TryGetValue(path, out var original)) throw new IOException("保留ACL復元のsnapshotがありません: " + path);
            // ACL ownership belongs to the exact key modified before interruption,
            // not to a newly materialized runtime endpoint's inherited security.
            var mapped = new KeyImage { Path = path, Exists = true, Security = original, SecurityMask = 15 };
            bool originalScope = Receipt.Keys.Any(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) || path.Equals(current.FxPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(current.FxPath + "\\", StringComparison.OrdinalIgnoreCase);
            if (!originalScope) throw new IOException("ACL復旧対象が記録された変更対象と一致しません。");
            if (!Receipt.PendingSecurityExpected.TryGetValue(path, out var expected)) throw new IOException("保留ACLの予定descriptorがありません: " + path);
            if (!RawRegistry.SecurityRecognized(mapped, expected) && (!interactive || !restoreConflict(mapped.Path, "一時ACL変更後のsecurityが予定状態と異なります。保存した導入前owner/ACLへ強制復元しますか？"))) throw new IOException("ACL復旧の確認が必要です: " + path);
            RawRegistry.RestoreSecurity(mapped); Receipt.PendingSecurityRestore.Remove(path); Receipt.PendingSecurityExpected.Remove(path); Receipt.PendingSecurityOriginal.Remove(path); Save();
        }
        if (Receipt.RegistrationPending) {
            // A process interruption cannot establish ownership of the API's partial
            // delta. Show each actual difference before an explicit recovery restores it.
            if (!interactive) throw new IOException("RegisterAPOの中断記録があります。Recoveryで現在値とsnapshotを確認してください。");
            foreach (var edit in ApiDifferences()) if (!restoreConflict(edit.Path + " / " + edit.Name, "RegisterAPO中断後の現在値: " + (edit.After?.Display ?? "未存在") + "\n導入前: " + (edit.Before?.Display ?? "未存在"))) throw new IOException("登録の復旧をキャンセルしました。snapshotを保持します。");
            CaptureApiEdits(true);
        }
        var keys = new List<KeyImage>();
        foreach (var path in Receipt.Edits.Select(e => Map(e.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) { var p = path; while (true) { var image = RawRegistry.KeyOnly(p); keys.Add(image); if (image.Exists) break; int slash = p.LastIndexOf('\\'); if (slash < 0) throw new IOException("Rollback parent not found"); p = p[..slash]; } }
        foreach (var edit in Receipt.Edits.Where(e => e.Applied)) { var image = keys.First(k => k.Path.Equals(Map(edit.Path), StringComparison.OrdinalIgnoreCase)); var value = RawRegistry.Value(image.Path, edit.Name); if (value != null && image.Find(edit.Name) == null) image.Values.Add(value); }
        string recoveryFiles = Path.Combine(DirectoryPath, "rollback-files-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Receipt.Files.Where(f => f.Applied)) { string path = SafePath(Contract.InstallRoot, file.Relative); if (!File.Exists(path)) continue; string backup = SafePath(recoveryFiles, file.Relative); ProtectDirectory(Path.GetDirectoryName(backup)!); File.Copy(path, backup, false); SecureStorage.File(backup); }
        // A recovery operation records its current baseline before restoring anything.
        Atomic(Path.Combine(DirectoryPath, "rollback-current-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(keys, Contract.Json));
        Receipt.Status = "RollingBack"; Save();
        foreach (var edit in Receipt.Edits.Where(e => e.Applied).Reverse()) {
            if (edit.Path == Contract.AudioPath && edit.Name == "DisableProtectedAudioDG" && (Receipt.ProtectedAudioWasAlreadyOne || keepProtectedAudio)) continue;
            string path = Map(edit.Path); var now = RawRegistry.Value(path, edit.Name);
            if (edit.Before == null ? now == null : edit.Before.Same(now)) continue;
            bool matches = edit.After == null ? now == null : edit.After.Same(now);
            if (!matches && (!interactive || !restoreConflict(path + " / " + edit.Name, $"現在: {now?.Display ?? "未存在"}\nDOT MIC適用時: {edit.After?.Display ?? "未存在"}\n導入前: {edit.Before?.Display ?? "未存在"}"))) { if (!interactive) throw new IOException("Rollback対象値が別の状態へ変更されています: " + path); Receipt.Diagnostics.Add("User retained conflicting value: " + path + " / " + edit.Name); continue; }
            RawRegistry.Write(new() { Path = path, Name = edit.Name, Before = now, After = edit.Before }, keys, AdvancedPermissionApproved, Receipt.AdvancedKeys, Receipt.PendingSecurityRestore, Receipt.PendingSecurityExpected, Receipt.PendingSecurityOriginal, Save);
        }
        foreach (var key in Receipt.Keys.Where(k => !k.Exists && Receipt.Operation != "ReferenceMigrationDetach").OrderByDescending(k => k.Path.Length)) {
            string path = Map(key.Path); var image = RawRegistry.Tree(path).First();
            if (image.Exists && image.Values.Count == 0 && RawRegistry.Tree(path).Count == 1) RawRegistry.DeleteEmpty(path);
        }
        // Stop/start, not start-before-file-restore: no graph may reload an owned DLL
        // while inverse file changes are being applied. Start runs even on file failure.
        try { if (restartAudio) { Receipt.AudioRestartPending = true; Save(); AudioService.Stop(Receipt.AudioRestartConsent, Receipt.AudioDependents, Receipt.DependentServiceConsent); } else Integration.RequireInstalledModulesUnloaded(); foreach (var file in Receipt.Files.Where(f => f.Applied).Reverse()) {
            string target = SafePath(Contract.InstallRoot, file.Relative); if (!File.Exists(target)) {
                if (!file.Existed) continue;
                if (Receipt.Operation != "ReferenceMigrationDetach" && (!interactive || !restoreConflict(target, "導入後にファイルが削除されています。保存した導入前ファイルを復元しますか？"))) { if (!interactive) throw new IOException("Rollback file missing: " + file.Relative); Receipt.Diagnostics.Add("User retained missing file: " + file.Relative); continue; }
                string backup = SafePath(Path.Combine(DirectoryPath, "files"), file.Relative);
                if (!File.Exists(backup) || Contract.FileHash(backup) != file.BeforeHash) throw new IOException("Rollback file backup missing/mismatch: " + file.Relative);
                ProtectDirectory(Path.GetDirectoryName(target)!); string staged = target + ".restore-" + Guid.NewGuid().ToString("N");
                File.Copy(backup, staged, false); SecureStorage.File(staged); File.Move(staged, target, false);
                if (Contract.FileHash(target) != file.BeforeHash) throw new IOException("復元ファイルread-back失敗"); continue;
            }
            SecureStorage.Validate(Path.GetDirectoryName(target)!, true); SecureStorage.Validate(target, false);
            string hash = Contract.FileHash(target); if (hash == file.BeforeHash) continue;
            if (hash != file.Hash && (!interactive || !restoreConflict(target, "DOT MIC導入後にファイルが変わっています。導入前ファイルへ戻しますか？"))) { if (!interactive) throw new IOException("Rollback file conflict: " + file.Relative); continue; }
            if (file.Existed) { string backup = SafePath(Path.Combine(DirectoryPath, "files"), file.Relative); if (Contract.FileHash(backup) != file.BeforeHash) throw new IOException("Rollback file backup checksum mismatch"); string staged = target + ".restore-" + Guid.NewGuid().ToString("N"); File.Copy(backup, staged, false); SecureStorage.File(staged); File.Move(staged, target, true); if (Contract.FileHash(target) != file.BeforeHash) throw new IOException("復元ファイルread-back失敗"); }
            else File.Delete(target); // Exact owned file only, never recursive directory deletion.
        } } finally { if (restartAudio) { AudioService.Start(Receipt.AudioRestartConsent, Receipt.AudioDependents, Receipt.DependentServiceConsent); Receipt.AudioRestartPending = false; Save(); } }
        // Restored baseline may contain no APO at all; verify physical capture, not DOT MIC presence.
        await Integration.VerifyCaptureOnly(current);
        if (restoreApplication) ApplicationInstaller.Rollback(this, interactive, restoreConflict);
        else if (Receipt.Application != null) Receipt.Application.CleanupPending = false;
        if (Receipt.Application?.CleanupPending == true) {
            Receipt.Status = "ApplicationCleanupPending"; Save(); return;
        }
        Receipt.Status = Receipt.ApplicationRemovalPending ? "ApplicationRemovalPending" : "RolledBack"; Save();
    }
}
