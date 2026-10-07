using System.Security.Principal;

namespace DotMic.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixtures") { ConsentPolicy.Fixtures(Path.GetFullPath(args[1])); return 0; }
        ApplicationConfiguration.Initialize();
        if (!Environment.Is64BitProcess || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { MessageBox.Show("導入・修復・削除・復旧には64ビットの管理者権限が必要です。通常のアプリは管理者権限なしで起動してください。", "DOT MIC セットアップ"); return 1; }
        bool cleanup = args.Length == 3 && args[0] is "--cleanup-application" or "--cleanup-rollback";
        if (cleanup) {
            if (!int.TryParse(args[2], out int parent) || parent == Environment.ProcessId) return 1;
            try { using var process = System.Diagnostics.Process.GetProcessById(parent); if (!process.WaitForExit(60000)) return 1; }
            catch (ArgumentException) { }
        }
        FileStream operation;
        try {
            Transaction.ProtectDirectory(Contract.RecoveryRoot);
            string path = Path.Combine(Contract.RecoveryRoot, "setup-operation.lock");
            if (File.Exists(path)) SecureStorage.RecoveryFile(path);
            operation = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); SecureStorage.File(path);
        } catch (Exception e) { MessageBox.Show("処理を開始できません。別のセットアップが開いている場合は閉じてください。\n" + e.Message, "DOT MIC セットアップ"); return 1; }
        using var operationLifetime = operation; // No service, IPC server or background resident component.
        if (cleanup) {
            Transaction? cleanupTransaction = null;
            try {
                if (args[0] == "--cleanup-application") {
                    cleanupTransaction = Transaction.Existing(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "snapshot.json"), false);
                    if (!SetupPresentation.CanFinishApplicationRemoval(cleanupTransaction.Receipt)
                        || !Path.GetFullPath(args[1]).Equals(cleanupTransaction.Receipt.ApplicationRemovalJournal, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("音声設定の復旧を完了してから、アプリを削除してください。");
                    ApplicationInstaller.RemoveOwned(Path.GetFullPath(args[1]));
                    cleanupTransaction.Receipt.ApplicationRemovalPending = false; cleanupTransaction.Receipt.Status = "RolledBack"; cleanupTransaction.Save();
                }
                else { var tx = cleanupTransaction = Transaction.Existing(args[1], false);
                    if (!SetupPresentation.CanFinishApplicationCleanup(tx.Receipt)) throw new IOException("音声設定・登録・権限の復旧を先に完了してください。");
                    ApplicationInstaller.Rollback(tx, false, (_, _) => false);
                    if (tx.Receipt.Application?.CleanupPending == true) throw new IOException("後片付けを完了できません。");
                    tx.Receipt.Status = "RolledBack"; tx.Save(); }
                return 0;
            } catch (Exception e) {
                if (cleanupTransaction != null) { cleanupTransaction.Receipt.Status = "RecoveryRequired"; try { cleanupTransaction.Save(); } catch { } }
                MessageBox.Show("アプリの後片付けを完了できませんでした。復旧データは保持しています。\n" + e.Message, "DOT MIC セットアップ"); return 1;
            }
        }
        try { PackageSource.Initialize(); }
        catch (Exception e) { MessageBox.Show("配布ファイルを確認・準備できませんでした。\n" + e.Message, "DOT MIC セットアップ"); return 1; }
        if (args.Length == 3 && args[0] == "--reference-detach-archived-pnp" && args[1] == "--explicit-reference-and-dependent-consent") {
            Transaction? tx = null;
            try {
                tx = ReferenceMigration.Prepare(AppContext.BaseDirectory);
                tx.Receipt.ReplacementConsent = true; tx.Receipt.AudioRestartConsent = true; tx.Receipt.DependentServiceConsent = true;
                if (tx.Receipt.AudioDependents.Any(s => s.Name is not ("RtkAudioUniversalService" or "LightingService"))) throw new IOException("同意範囲外の依存サービスが見つかりました。");
                tx.Save(); ReferenceMigration.Apply(tx).GetAwaiter().GetResult();
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Snapshot = tx.ReceiptPath, tx.Receipt.Status, tx.Receipt.Target, tx.Receipt.ArchivedOriginReceipt, Scope = "Consented own Community namespace/files detach for clean no-PnP origin; not general installation", ChangedValues = tx.Receipt.Edits.Count, OwnedFiles = tx.Receipt.Files.Count, ProtectedAudioUnchanged = true, PhysicalCapture = "PASS", NoVendorEffectDeletion = true, PcmRecorded = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new { Snapshot = tx?.ReceiptPath, Status = tx?.Receipt.Status, Error = e.ToString() }, Contract.Json)); return 1; }
        }
        if (args.Length == 2 && args[0] == "--fixture-remove-association") {
            try {
                string path = RawRegistry.Value(Contract.ConfigPath, "LatestReceipt")?.Display ?? throw new IOException("確定済みCommunity receiptがありません。");
                var tx = Transaction.RemoveAssociationFixture(path);
                File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Snapshot = tx.ReceiptPath, tx.Receipt.Status, tx.Receipt.Target, ChangedValueCount = 1, GlobalSettingsChanged = false, AudioServicesRestarted = false, PcmRecorded = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[1], e.ToString()); return 1; }
        }
        if (args.Length == 3 && args[0] == "--audit-restored") {
            try { var tx = Transaction.Existing(args[1], false); tx.AuditRestored().GetAwaiter().GetResult();
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Result = "PASS_EXACT_ORIGINAL_REGISTRY_FILES_AND_PHYSICAL_CAPTURE", Snapshot = tx.ReceiptPath, tx.Receipt.Status, AudioServicesRestarted = false, PcmRecorded = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[2], e.ToString()); return 1; }
        }
        if (args.Length == 3 && args[0] == "--finish-unloaded-rollback") {
            try {
                var tx = Transaction.Existing(args[1], false); tx.FinishUnloadedRollback().GetAwaiter().GetResult();
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Snapshot = tx.ReceiptPath, tx.Receipt.Status, RegistryAtOriginalRawState = true, CommunityModulesNotLoaded = true, OwnedFilesRestored = true, PhysicalCaptureReceivedFrames = true, AudioServicesRestarted = false, ForcedTermination = false, PcmRecorded = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[2], e.ToString()); return 1; }
        }
        if (args.Length == 3 && args[0] == "--diagnose-reference") {
            try {
                var matches = Integration.Endpoints().Where(e => e.StableId == args[1]).ToArray();
                if (matches.Length != 1) throw new IOException("診断対象StableIdが一意ではありません。");
                var tx = Transaction.Prepare(matches[0], AppContext.BaseDirectory, "Diagnostic", false);
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new { Snapshot = tx.ReceiptPath, Plan = tx.Description(), Receipt = tx.Receipt, AudioRegistryWrites = false, AudioServiceRestarted = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[2], e.ToString()); return 1; }
        }
        if (args.Length == 2 && args[0] == "--verify-installed") {
            try {
                string path = RawRegistry.Value(Contract.ConfigPath, "LatestReceipt")?.Display ?? throw new IOException("Community導入receiptがありません。");
                var receipt = Transaction.Read(path); if (receipt.Status != "Committed") throw new IOException("未確定transactionです。Recoveryで状態を確認してください。");
                string apo = RawRegistry.Value(Contract.ConfigPath, "ApoPath")?.Display ?? throw new IOException("APO配置情報がありません。");
                string hash = RawRegistry.Value(Contract.ConfigPath, "ApoHash")?.Display ?? throw new IOException("APO hash情報がありません。");
                var current = EndpointIdentity.Resolve(receipt.Target, Integration.Endpoints());
                Integration.Verify(current, apo, hash).GetAwaiter().GetResult();
                var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
                bool rebooted = DateTime.TryParse(receipt.BootBefore, out var prior) && Math.Abs((boot - prior.ToUniversalTime()).TotalSeconds) > 10;
                File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { Time = DateTime.UtcNow, Receipt = path, Target = current, StoredTarget = receipt.Target, receipt.BootBefore, BootAfter = boot, ManualRebootObserved = rebooted, CommunityModuleAndProcess = "PASS", NoRegistryAssociationWrites = true, AudioServicesRestarted = false, MicrophonePcmRecorded = false }, Contract.Json)); return 0;
            } catch (Exception e) { File.WriteAllText(args[1], e.ToString()); return 1; }
        }
        Application.Run(new SetupForm(args)); return 0;
    }
}
