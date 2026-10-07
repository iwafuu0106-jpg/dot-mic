using System.Text.Json;

namespace DotMic.Setup;

// Explicit reference-PC migration only; general Install never removes driver
// packages or fabricates a prior state from the archived production deployment.
internal static class ReferenceMigration
{
    internal static bool OwnedContextValue(string name) => name.Equals(Contract.FxFormat + ",0", StringComparison.OrdinalIgnoreCase) || name.StartsWith("{91795F52-2DC0-4E20-A732-58816672E635},", StringComparison.OrdinalIgnoreCase);
    internal static Transaction Prepare(string package)
    {
        string path = RawRegistry.Value(Contract.ConfigPath, "LatestReceipt")?.Display ?? throw new IOException("確定済みCommunity統合がありません。");
        var installed = Transaction.Read(path);
        if (installed.Status != "Committed") throw new IOException("未確定状態は先にRecoveryしてください。");
        var target = EndpointIdentity.Resolve(installed.Target, Integration.Endpoints());
        if (RawRegistry.Value(target.FxPath, Contract.Clsid + ",100") != null) throw new IOException("旧PnP namespace参照が残っています。実在するPnP設定を推測して撤去しません。");
        var payload = Transaction.ValidatePayload(package);
        RawRegistry.Privilege("SeSecurityPrivilege");
        var receipt = new Receipt { Operation = "ReferenceMigrationDetach", Target = target, ArchivedOriginReceipt = RawRegistry.Value(Contract.ConfigPath, "OriginReceipt")?.Display,
            BootBefore = (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToString("O"), AudioDependents = AudioService.Dependents() };
        receipt.Keys.AddRange(RawRegistry.Tree(target.FxPath));
        string context = target.FxPath + "\\" + Contract.Context;
        foreach (var root in new[] { Contract.ComPath, Contract.ApoPath, Contract.ApoClassesPath, Contract.ConfigPath }) receipt.Keys.AddRange(RawRegistry.Tree(root));
        var tx = Transaction.Reference(receipt, package, payload);
        void Remove(string key, string name, RawValue? expected = null) {
            var value = RawRegistry.Value(key, name); if (value == null) return;
            if (expected != null && !expected.Same(value)) throw new IOException("移行対象がDOT MIC適用値と異なります。現在値を上書きしません: " + key + " / " + name);
            receipt.Edits.Add(new() { Path = key, Name = name, Before = value, After = null });
        }
        Remove(target.FxPath, Contract.FxFormat + ",6", RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid));
        Remove(target.FxPath, Contract.ModeFormat + ",6", RawValue.Multi(Contract.ModeFormat + ",6", Contract.DefaultMode));
        var comPath = RawRegistry.Value(Contract.ComPath + "\\InprocServer32", "");
        if (!RawValue.Text("", Transaction.SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll")).Same(comPath)) throw new IOException("現在COM配置が固定Community pathではありません。");
        var configNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "StableId", "ContainerId", "PhysicalInterface", "CachedEndpointId", "InstallDir", "ApoPath", "ApoHash", "OriginReceipt", "LatestReceipt", "Version", "RequiredFiles" };
        foreach (var image in receipt.Keys.Where(k => k.Exists)) foreach (var value in image.Values) {
            bool own = (image.Path.Equals(context, StringComparison.OrdinalIgnoreCase) || image.Path.StartsWith(context + "\\", StringComparison.OrdinalIgnoreCase)) && OwnedContextValue(value.Name);
            if (image.Path.Equals(context + "\\Volatile", StringComparison.OrdinalIgnoreCase) && value.Name.StartsWith("{91795F52-2DC0-4E20-A732-58816672E635},", StringComparison.OrdinalIgnoreCase)) {
                var id = value.Name[(value.Name.LastIndexOf(',') + 1)..];
                if (!uint.TryParse(id, out uint property) || property > 9) own = false; // OS/graph telemetry changes on Stop; never install/remove recorded counters.
            }
            own |= image.Path.Equals(Contract.ComPath, StringComparison.OrdinalIgnoreCase) && value.Name == "";
            own |= image.Path.Equals(Contract.ComPath + "\\InprocServer32", StringComparison.OrdinalIgnoreCase) && (value.Name == "" || value.Name.Equals("ThreadingModel", StringComparison.OrdinalIgnoreCase));
            own |= new[] { Contract.ApoPath, Contract.ApoClassesPath }.Contains(image.Path, StringComparer.OrdinalIgnoreCase) && installed.RegistrationExpected.Any(v => v.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase) && v.Same(value));
            own |= image.Path.Equals(Contract.ConfigPath, StringComparison.OrdinalIgnoreCase) && configNames.Contains(value.Name);
            if (own) Remove(image.Path, value.Name, value);
        }
        foreach (var file in payload.Files.Where(f => f.Path.StartsWith("APO/", StringComparison.Ordinal))) {
            string source = Transaction.SafePath(Contract.InstallRoot, file.Path); SecureStorage.Validate(Path.GetDirectoryName(source)!, true); SecureStorage.Validate(source, false);
            if (Contract.FileHash(source) != file.Hash) throw new IOException("移行前の固定payloadが変更されています: " + file.Path);
            string backup = Transaction.SafePath(Path.Combine(tx.DirectoryPath, "files"), file.Path); Transaction.ProtectDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup, false); SecureStorage.File(backup);
            if (Contract.FileHash(backup) != file.Hash) throw new IOException("移行用配置ファイルsnapshotが一致しません。");
            receipt.Files.Add(new() { Relative = file.Path, Hash = file.Hash, BeforeHash = file.Hash, Existed = true });
        }
        tx.Save(); return tx;
    }
    internal static async Task Apply(Transaction tx)
    {
        var receipt = tx.Receipt;
        if (!receipt.AudioRestartConsent || !receipt.ReplacementConsent || (receipt.AudioDependents.Count > 0 && !receipt.DependentServiceConsent)) throw new OperationCanceledException("参照機移行と表示依存サービスへの明示的同意が必要です。");
        AudioService.ValidateDependents(receipt.AudioDependents);
        receipt.Status = "ReferenceDetaching"; receipt.AudioRestartPending = true; tx.Save();
        try {
            try {
                AudioService.Stop(true, receipt.AudioDependents, receipt.DependentServiceConsent);
                Integration.RequireInstalledModulesUnloaded();
                foreach (var edit in receipt.Edits.ToArray()) tx.ReferenceWrite(edit);
                foreach (var file in receipt.Files) {
                    string path = Transaction.SafePath(Contract.InstallRoot, file.Relative);
                    if (Contract.FileHash(path) != file.BeforeHash) throw new IOException("撤去直前に配置ファイルが変更されました。");
                    file.Applied = true; tx.Save(); File.Delete(path);
                    if (File.Exists(path)) throw new IOException("専用配置ファイルの撤去read-backに失敗しました。");
                }
                // Retain key shells/security. RegDeleteKeyEx has no atomic
                // "only if no values" condition; retaining them avoids deleting
                // a concurrent third-party write and needs no ACL reconstruction.
            } finally {
                AudioService.Start(true, receipt.AudioDependents, receipt.DependentServiceConsent); receipt.AudioRestartPending = false; tx.Save();
            }
            await Integration.VerifyCaptureOnly(receipt.Target);
            receipt.Status = "ReferenceDetached"; tx.Save();
        } catch (Exception e) {
            receipt.Diagnostics.Add("Reference detach failed: " + e); try { tx.Save(); } catch { }
            await tx.Rollback(false, (_, _) => false, false); throw new IOException("移行を確定せず、保存したCommunity状態へ戻しました。", e);
        }
    }
}
