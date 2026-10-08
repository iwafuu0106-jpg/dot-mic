using System.Diagnostics;
using System.Text.Json;

namespace DotMic.Setup;

internal static class ApplicationInstaller
{
    private const string Marker = ".dot-mic-install.json";
    internal static string? ConfiguredRoot => RawRegistry.Value(Contract.ConfigPath, "ApplicationDir")?.Display;
    internal static void Validate(ApplicationDeployment deployment)
    {
        InstallPaths.ValidateDeployment(deployment);
        foreach (var file in deployment.Files) Transaction.SafePath(deployment.Root, file.Relative);
    }
    private static void CheckParents(string root)
    {
        SecureStorage.NoRedirection(root);
        for (string? parent = Path.GetDirectoryName(root); parent != null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent)) SecureStorage.ValidateParent(parent);
    }
    private static void CreateDirectory(string path, string root)
    {
        CheckParents(root); SecureStorage.NoRedirection(path);
        if (!InstallPaths.Within(path, root)) throw new IOException("アプリ保存先の外へは配置できません。");
        if (!Directory.Exists(path)) {
            string? parent = Path.GetDirectoryName(path);
            if (parent != null && InstallPaths.Within(parent, root)) CreateDirectory(parent, root);
            else if (parent == null || !Directory.Exists(parent)) throw new IOException("保存先の親フォルダーを先に作成してください。");
            SecureStorage.CreateApplicationDirectory(path);
        }
        SecureStorage.Validate(path, true);
    }
    internal static void RequireApplicationClosed(string root)
    {
        foreach (var process in Process.GetProcessesByName("DotMic.App")) {
            using (process) {
                try { if (process.MainModule?.FileName is string executable && InstallPaths.Within(executable, root))
                    throw new IOException("DOT MICをメニューの「終了」で閉じてから、変更内容を確認し直してください。"); }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("起動中のDOT MICを確認できません。アプリを終了してから再試行してください。"); }
            }
        }
    }
    internal static InstalledApplication? ReadInstalled(string root)
    {
        root = InstallPaths.Normalize(root); CheckParents(root);
        if (!Directory.Exists(root)) return null;
        SecureStorage.Validate(root, true);
        string path = Transaction.SafePath(root, Marker);
        if (!File.Exists(path)) {
            if (ContainsData(root)) throw new IOException("他のファイルがある保存先は使用できません。DOT MIC専用の空のフォルダーを指定してください。");
            return null;
        }
        SecureStorage.Validate(path, false);
        var installation = JsonSerializer.Deserialize<InstalledApplication>(File.ReadAllBytes(path), Contract.Json) ?? throw new IOException("アプリの配置情報を読み込めません。");
        Validate(new() { Root = installation.Root, Files = installation.Files.Select(f => new FileEdit { Relative = f.Path, Hash = f.Hash }).ToList() });
        if (!root.Equals(installation.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("配置情報と保存先が一致しません。");
        return installation;
    }
    internal static ApplicationDeployment Prepare(Transaction tx, string package, string destination, bool desktop)
    {
        string root = InstallPaths.Normalize(destination); CheckParents(root); RequireApplicationClosed(root);
        if (ConfiguredRoot is string configured && !configured.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("保存先を変更する場合は、既存の導入を削除してから新しい保存先へ導入してください。");
        var previous = ReadInstalled(root);
        var payload = Transaction.ValidatePayload(package);
        var files = payload.Files.Where(f => f.Path is "LICENSE" or "SECURITY.md" || f.Path.StartsWith("UI/", StringComparison.Ordinal) || f.Path.StartsWith("licenses/", StringComparison.Ordinal))
            .Select(f => new PayloadFile("内部ファイル/" + f.Path, f.Hash)).ToList();
        files.Add(new("DOT MIC.exe", Contract.FileHash(Transaction.SafePath(package, "DOT MIC.exe"))));
        files.Add(new("セットアップ.exe", Contract.FileHash(Transaction.SafePath(package, "セットアップ.exe"))));
        if (previous != null && previous.Files.Any(p => !files.Any(f => f.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("以前の配置とファイル構成が異なります。既存アプリの削除を完了してから導入してください。");
        if (Directory.Exists(root)) {
            var owned = new HashSet<string>(previous?.Files.Select(f => Transaction.SafePath(root, f.Path)) ?? [], StringComparer.OrdinalIgnoreCase) { Transaction.SafePath(root, Marker) };
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (!owned.Contains(path)) throw new IOException("保存先にDOT MIC以外のファイルがあります。別の専用フォルダーを選択してください。");
        }
        var deployment = new ApplicationDeployment { Root = root, DesktopShortcut = desktop,
            ParentDirectories = InstallPaths.PlanParents(root, Directory.Exists, File.Exists) };
        foreach (var file in files) Capture(tx, deployment, file.Path, file.Hash, previous);
        if (InstallPaths.RequiresShortcutAccess(desktop, previous?.ShortcutHash)) {
            string shortcut = DesktopShortcut.PathName;
            SecureStorage.NoRedirection(shortcut);
            if (File.Exists(shortcut)) {
                string hash = Contract.FileHash(shortcut);
                if (previous?.ShortcutHash != hash) {
                    if (desktop) throw new IOException("デスクトップに別の「DOT MIC」ショートカットがあります。削除・移動するか、作成のチェックを外してください。");
                } else {
                    deployment.ShortcutBeforeHash = hash;
                    File.Copy(shortcut, Backup(tx, "desktop-shortcut.lnk"), false); SecureStorage.File(Backup(tx, "desktop-shortcut.lnk"));
                }
            }
        }
        if (desktop) {
            string staged = Path.Combine(tx.DirectoryPath, "desktop-shortcut.new.lnk");
            DesktopShortcut.Create(staged, Transaction.SafePath(root, "DOT MIC.exe")); SecureStorage.File(staged);
            deployment.ShortcutHash = Contract.FileHash(staged);
        }
        var installed = new InstalledApplication(root, files, deployment.ShortcutHash, package);
        string record = Path.Combine(tx.DirectoryPath, "application-marker.new.json");
        File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(installed, Contract.Json)); SecureStorage.File(record);
        Capture(tx, deployment, Marker, Contract.FileHash(record), previous);
        Validate(deployment); return deployment;
    }
    private static string Backup(Transaction tx, string relative)
    {
        string root = Path.Combine(tx.DirectoryPath, "application-before");
        string path = Transaction.SafePath(root, relative); SecureStorage.Directory(Path.GetDirectoryName(path)!); return path;
    }
    private static void Capture(Transaction tx, ApplicationDeployment deployment, string relative, string hash, InstalledApplication? previous)
    {
        string path = Transaction.SafePath(deployment.Root, relative); bool existed = File.Exists(path);
        string? before = existed ? Contract.FileHash(path) : null;
        if (existed) {
            SecureStorage.Validate(path, false);
            if (relative != Marker && !previous!.Files.Any(f => f.Path == relative && f.Hash == before))
                throw new IOException("導入後にアプリファイルが変更されています。保存先を確認してください。");
            string backup = Backup(tx, relative); File.Copy(path, backup, false); SecureStorage.File(backup);
            if (Contract.FileHash(backup) != before) throw new IOException("アプリファイルの復元用コピーを確認できません。");
        }
        deployment.Files.Add(new() { Relative = relative, Hash = hash, Existed = existed, BeforeHash = before });
    }
    internal static void Apply(Transaction tx, string package)
    {
        if (tx.Receipt.Application is not { } app) return;
        Validate(app); RequireApplicationClosed(app.Root); CheckParents(app.Root);
        foreach (string parent in app.ParentDirectories) {
            CheckParents(parent);
            if (Directory.Exists(parent)) SecureStorage.ValidateParent(parent);
            else {
                if (Path.GetDirectoryName(parent) is not string existing || !Directory.Exists(existing)) throw new IOException("確認後に保存先の親フォルダーが変わりました。");
                SecureStorage.CreateApplicationDirectory(parent);
            }
        }
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(app.Root)) { SecureStorage.Validate(app.Root, true); checkedDirectories.Add(app.Root); }
        foreach (var file in app.Files) {
            string destination = Transaction.SafePath(app.Root, file.Relative);
            for (string? parent = Path.GetDirectoryName(destination); parent != null && InstallPaths.Within(parent, app.Root); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && checkedDirectories.Add(parent)) SecureStorage.Validate(parent, true);
            if (File.Exists(destination)) SecureStorage.Validate(destination, false);
            if (File.Exists(destination) != file.Existed || (file.Existed && Contract.FileHash(destination) != file.BeforeHash))
                throw new IOException("確認後に保存先のファイルが変わりました。変更内容を確認し直してください。");
            if (file.BeforeHash == file.Hash) continue;
            string source = file.Relative == Marker ? Path.Combine(tx.DirectoryPath, "application-marker.new.json")
                : Transaction.SafePath(package, file.Relative.StartsWith("内部ファイル/", StringComparison.Ordinal) ? file.Relative["内部ファイル/".Length..] : file.Relative);
            if (Contract.FileHash(source) != file.Hash) throw new IOException("確認後に配置用ファイルが変わりました。");
            CreateDirectory(Path.GetDirectoryName(destination)!, app.Root);
            string staged = destination + ".stage-" + tx.Receipt.TransactionId;
            if (File.Exists(staged)) throw new IOException("配置用ファイルが残っています。復旧を完了してから再試行してください。");
            file.StageStarted = true; tx.Save();
            try { File.Copy(source, staged, false); SecureStorage.File(staged); }
            finally { if (File.Exists(staged)) { file.StageHash = Contract.FileHash(staged); tx.Save(); } }
            if (Contract.FileHash(staged) != file.Hash) throw new IOException("アプリの配置用コピーを確認できません。");
            file.Applied = true; tx.Save(); File.Move(staged, destination, true);
        }
        if (app.DesktopShortcut || app.ShortcutBeforeHash != null) {
            string target = DesktopShortcut.PathName;
            string? now = File.Exists(target) ? Contract.FileHash(target) : null;
            if (now != app.ShortcutBeforeHash) throw new IOException("確認後にデスクトップのショートカットが変わりました。");
            if (app.DesktopShortcut) {
                string source = Path.Combine(tx.DirectoryPath, "desktop-shortcut.new.lnk");
                string stage = target + ".stage-" + tx.Receipt.TransactionId;
                RequireFreshStage(stage); app.ShortcutStageStarted = true; tx.Save();
                StagedFileReplacement.Publish(() => SecureStorage.CopyNewProtectedFile(source, stage), () => Contract.FileHash(stage), app.ShortcutHash!, () => {
                    if (CurrentHash(target) != now) throw new IOException("配置中にショートカットが変更されました。");
                    app.ShortcutApplied = true; tx.Save(); File.Move(stage, target, true);
                });
                app.ShortcutStageStarted = false; tx.Save();
            } else { app.ShortcutApplied = true; tx.Save(); File.Delete(target); }
        }
    }
    internal static void Rollback(Transaction tx, bool interactive, Func<string, string, bool> conflict)
    {
        if (tx.Receipt.Application is not { } app) return;
        Validate(app); app.CleanupPending = false;
        if (app.ShortcutStageStarted) {
            CleanupReplacementStage(DesktopShortcut.PathName + ".stage-" + tx.Receipt.TransactionId,
                Path.Combine(tx.DirectoryPath, "desktop-shortcut.new.lnk"), app.ShortcutHash);
            app.ShortcutStageStarted = false; tx.Save();
        }
        if (app.ShortcutRestoreStageStarted) {
            CleanupReplacementStage(DesktopShortcut.PathName + ".restore-" + tx.Receipt.TransactionId,
                Backup(tx, "desktop-shortcut.lnk"), app.ShortcutBeforeHash);
            app.ShortcutRestoreStageStarted = false; tx.Save();
        }
        foreach (var file in app.Files.Where(f => f.RestoreStageStarted)) {
            CleanupReplacementStage(Transaction.SafePath(app.Root, file.Relative) + ".restore-" + tx.Receipt.TransactionId,
                Backup(tx, file.Relative), file.BeforeHash);
            file.RestoreStageStarted = false; tx.Save();
        }
        foreach (var file in app.Files.Where(f => f.StageStarted || f.StageHash != null)) {
            string stage = Transaction.SafePath(app.Root, file.Relative) + ".stage-" + tx.Receipt.TransactionId;
            if (File.Exists(stage)) {
                SecureStorage.NoRedirection(stage);
                if (file.StageHash != null ? Contract.FileHash(stage) != file.StageHash : !StagedPrefixMatches(tx, file, stage))
                    throw new IOException("配置用ファイルが別の内容へ変更されています。復旧データを保持します。");
                File.Delete(stage);
            }
            file.StageHash = null; file.StageStarted = false; tx.Save();
        }
        if (app.ShortcutApplied) {
            string path = DesktopShortcut.PathName; string? now = File.Exists(path) ? Contract.FileHash(path) : null;
            if (now != app.ShortcutBeforeHash && (now == app.ShortcutHash || (interactive && conflict(path, "導入前のショートカットに戻しますか？")))) {
                if (app.ShortcutBeforeHash == null) File.Delete(path);
                else {
                    string backup = Backup(tx, "desktop-shortcut.lnk"); if (Contract.FileHash(backup) != app.ShortcutBeforeHash) throw new IOException("ショートカットの復元用コピーが一致しません。");
                    string stage = path + ".restore-" + tx.Receipt.TransactionId;
                    RequireFreshStage(stage); app.ShortcutRestoreStageStarted = true; tx.Save();
                    StagedFileReplacement.Publish(() => SecureStorage.CopyNewProtectedFile(backup, stage), () => Contract.FileHash(stage), app.ShortcutBeforeHash, () => {
                        if (CurrentHash(path) != now) throw new IOException("復元中にショートカットが変更されました。");
                        File.Move(stage, path, true);
                    });
                    app.ShortcutRestoreStageStarted = false; tx.Save();
                }
            } else if (now != app.ShortcutBeforeHash && !interactive) throw new IOException("ショートカットが別の状態へ変更されています。");
            app.ShortcutApplied = false; tx.Save();
        }
        foreach (var file in app.Files.Where(f => f.Applied).Reverse()) {
            string path = Transaction.SafePath(app.Root, file.Relative); string? now = File.Exists(path) ? Contract.FileHash(path) : null;
            if (now == file.BeforeHash) { file.Applied = false; tx.Save(); continue; }
            if (now != file.Hash && (!interactive || !conflict(path, "変更されたアプリファイルを導入前に戻しますか？"))) {
                if (!interactive) throw new IOException("アプリファイルが別の状態へ変更されています。");
                file.Applied = false; tx.Save(); continue;
            }
            if (path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) || InstallPaths.Within(path, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))) {
                app.CleanupPending = true; tx.Save(); continue;
            }
            if (file.Existed) {
                string backup = Backup(tx, file.Relative); if (Contract.FileHash(backup) != file.BeforeHash) throw new IOException("アプリの復元用コピーが一致しません。");
                CreateDirectory(Path.GetDirectoryName(path)!, app.Root);
                string stage = path + ".restore-" + tx.Receipt.TransactionId;
                RequireFreshStage(stage); file.RestoreStageStarted = true; tx.Save();
                StagedFileReplacement.Publish(() => SecureStorage.CopyNewProtectedFile(backup, stage), () => Contract.FileHash(stage), file.BeforeHash!, () => {
                    if (CurrentHash(path) != now) throw new IOException("復元中にアプリファイルが変更されました。");
                    File.Move(stage, path, true);
                });
                file.RestoreStageStarted = false; tx.Save();
            } else File.Delete(path);
            file.Applied = false; tx.Save();
        }
        RemoveEmptyOwnedDirectories(app.Root, app.Files.Select(f => f.Relative));
    }
    private static string? CurrentHash(string path) => File.Exists(path) ? Contract.FileHash(path) : null;
    private static void RequireFreshStage(string stage)
    {
        SecureStorage.NoRedirection(stage);
        if (File.Exists(stage) || Directory.Exists(stage)) throw new IOException("配置・復元用ファイルが残っています。復旧を完了してください。");
    }
    private static void CleanupReplacementStage(string stage, string source, string? expectedHash)
    {
        if (!File.Exists(stage)) return;
        SecureStorage.Validate(stage, false); SecureStorage.Validate(source, false);
        if (expectedHash == null || Contract.FileHash(source) != expectedHash) throw new IOException("配置・復元用コピーの元データが一致しません。");
        using (var staged = File.OpenRead(stage)) using (var original = File.OpenRead(source))
            if (!StagedFileReplacement.PrefixMatches(staged, original)) throw new IOException("配置・復元用ファイルが別の内容へ変更されています。復旧データを保持します。");
        File.Delete(stage);
    }
    private static bool ContainsData(string directory)
    {
        SecureStorage.Validate(directory, true);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
            SecureStorage.NoRedirection(entry);
            if (!Directory.Exists(entry) || ContainsData(entry)) return true;
        }
        return false;
    }
    private static void RemoveEmptyOwnedDirectories(string root, IEnumerable<string> files)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files) for (string? directory = Path.GetDirectoryName(Transaction.SafePath(root, file));
            directory != null && InstallPaths.Within(directory, root); directory = Path.GetDirectoryName(directory)) directories.Add(directory);
        foreach (string directory in directories.OrderByDescending(d => d.Length)) {
            if (!Directory.Exists(directory)) continue;
            SecureStorage.Validate(directory, true);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
    internal static string? PendingRemoval(string receiptDirectory)
    {
        string journal = Path.Combine(receiptDirectory, "application-removal.json");
        if (!File.Exists(journal)) return null;
        SecureStorage.RecoveryFile(journal);
        string result = journal + ".result";
        if (File.Exists(result)) { SecureStorage.RecoveryFile(result); if (File.ReadAllText(result) == Contract.FileHash(journal)) return null; }
        return journal;
    }
    internal static List<string> PendingRemovalReceipts()
    {
        var receipts = new List<string>();
        if (!Directory.Exists(Contract.RecoveryRoot)) return receipts;
        foreach (string directory in Directory.EnumerateDirectories(Contract.RecoveryRoot)) {
            if (!Guid.TryParse(Path.GetFileName(directory), out _)) continue;
            string snapshot = Path.Combine(directory, "snapshot.json");
            if (!File.Exists(snapshot)) continue;
            try { var receipt = Transaction.Read(snapshot);
                if (receipt.ApplicationRemovalPending && receipt.ApplicationRemovalJournal != null && PendingRemoval(directory) != null) receipts.Add(snapshot);
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (JsonException) { }
        }
        return receipts;
    }
    internal static void PlanMetadataRemoval(Transaction tx, InstalledApplication installed)
    {
        var current = new[] { "ApplicationDir", "ApplicationReceipt", "ApplicationPackageDir" }
            .Select(name => RawRegistry.Value(Contract.ConfigPath, name)).OfType<RawValue>().ToList();
        tx.Receipt.Edits.AddRange(InstallPaths.MetadataRemovals(installed, current));
        // The inverse edits use the same consented, journaled key-only permissions as audio restoration.
        // Do not save during preview; the consented Execute call persists the entire plan.
    }
    internal static InstalledApplication RemovalApplication(string journal)
    {
        SecureStorage.RecoveryFile(journal);
        var record = JsonSerializer.Deserialize<ApplicationRemoval>(File.ReadAllBytes(journal), Contract.Json) ?? throw new IOException("削除するアプリの記録がありません。");
        Validate(new() { Root = record.Application.Root, Files = record.Application.Files.Select(f => new FileEdit { Relative = f.Path, Hash = f.Hash }).ToList() });
        return record.Application;
    }
    internal static string PrepareRemoval(InstalledApplication installed, string receiptDirectory)
    {
        RequireApplicationClosed(installed.Root);
        string path = Path.Combine(receiptDirectory, "application-removal.json");
        var values = new[] { "ApplicationDir", "ApplicationReceipt", "ApplicationPackageDir" }
            .Select(name => RawRegistry.Value(Contract.ConfigPath, name)).OfType<RawValue>().ToList();
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new ApplicationRemoval(installed, values), Contract.Json)); SecureStorage.File(path); return path;
    }
    private static bool StagedPrefixMatches(Transaction tx, FileEdit file, string stage)
    {
        string marker = Path.Combine(tx.DirectoryPath, "application-marker.new.json"); SecureStorage.Validate(marker, false);
        var planned = JsonSerializer.Deserialize<InstalledApplication>(File.ReadAllBytes(marker), Contract.Json);
        string? package = planned?.PackageRoot;
        string boundary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC", "Packages");
        if (package == null || !package.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        string source = file.Relative == Marker ? marker : Transaction.SafePath(package,
            file.Relative.StartsWith("内部ファイル/", StringComparison.Ordinal) ? file.Relative["内部ファイル/".Length..] : file.Relative);
        SecureStorage.Validate(source, false);
        if (Contract.FileHash(source) != file.Hash) return false;
        long length = new FileInfo(stage).Length;
        using var input = File.OpenRead(source);
        if (length > input.Length) return false;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        while (length > 0) { int read = input.Read(buffer, 0, (int)Math.Min(length, buffer.Length)); if (read == 0) return false; hash.AppendData(buffer, 0, read); length -= read; }
        return Convert.ToHexString(hash.GetHashAndReset()) == Contract.FileHash(stage);
    }
    internal static void RemoveOwned(string journal)
    {
        SecureStorage.RecoveryFile(journal);
        var removal = JsonSerializer.Deserialize<ApplicationRemoval>(File.ReadAllBytes(journal), Contract.Json) ?? throw new IOException("削除するアプリの記録がありません。");
        var installed = removal.Application;
        Validate(new() { Root = installed.Root, Files = installed.Files.Select(f => new FileEdit { Relative = f.Path, Hash = f.Hash }).ToList() });
        RequireApplicationClosed(installed.Root); CheckParents(installed.Root);
        string marker = Transaction.SafePath(installed.Root, Marker);
        if (File.Exists(marker)) {
            SecureStorage.Validate(marker, false);
            var current = JsonSerializer.Deserialize<InstalledApplication>(File.ReadAllBytes(marker), Contract.Json);
            if (current?.Root != installed.Root || !current.Files.SequenceEqual(installed.Files) || current.ShortcutHash != installed.ShortcutHash || current.PackageRoot != installed.PackageRoot)
                throw new IOException("削除待ちの間にアプリの配置情報が変更されました。変更内容を確認し直してください。");
        } else if (installed.Files.Any(f => File.Exists(Transaction.SafePath(installed.Root, f.Path)) && Contract.FileHash(Transaction.SafePath(installed.Root, f.Path)) == f.Hash))
            throw new IOException("削除するアプリの配置情報がありません。ファイルは変更しません。");
        foreach (var file in installed.Files) {
            string target = Transaction.SafePath(installed.Root, file.Path);
            if (File.Exists(target) && Contract.FileHash(target) == file.Hash) { SecureStorage.Validate(target, false); File.Delete(target); }
        }
        if (installed.ShortcutHash != null) {
            string shortcut = DesktopShortcut.PathName;
            if (File.Exists(shortcut) && Contract.FileHash(shortcut) == installed.ShortcutHash) { SecureStorage.NoRedirection(shortcut); File.Delete(shortcut); }
        }
        if (File.Exists(marker)) {
            var current = JsonSerializer.Deserialize<InstalledApplication>(File.ReadAllBytes(marker), Contract.Json);
            if (current?.Root == installed.Root && current.Files.SequenceEqual(installed.Files) && current.ShortcutHash == installed.ShortcutHash) { SecureStorage.Validate(marker, false); File.Delete(marker); }
        }
        RemoveEmptyOwnedDirectories(installed.Root, installed.Files.Select(f => f.Path).Append(Marker));
        SecureStorage.NoRedirection(journal + ".result");
        if (File.Exists(journal + ".result")) SecureStorage.RecoveryFile(journal + ".result");
        File.WriteAllText(journal + ".result", Contract.FileHash(journal)); SecureStorage.File(journal + ".result");
    }
    internal static void LaunchCleanup(string journal, bool rollback)
    {
        string executable = PackageSource.CleanupExecutable();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(rollback ? "--cleanup-rollback" : "--cleanup-application");
        start.ArgumentList.Add(journal); start.ArgumentList.Add(Environment.ProcessId.ToString()); Process.Start(start);
    }
}
