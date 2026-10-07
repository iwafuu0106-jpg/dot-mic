using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DotMic.Setup;

internal static class FileChecks
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string output = Path.GetFullPath(args[0]);
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        string id = Guid.NewGuid().ToString("D");
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DOT MIC", "FileChecks", id);
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC", "Packages", "FileChecks-" + id);
        var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<string>();
        byte[] before = ObservedState();
        string desktop = DesktopShortcut.PathName;
        string? beforeShortcut = File.Exists(desktop) ? Contract.FileHash(desktop) : null;
        try {
            SecureStorage.Directory(parent); SecureStorage.Directory(source);
            ownedDirectories.Add(parent); ownedDirectories.Add(source);
            Write(Path.Combine(source, "DOT MIC.exe"), Encoding.UTF8.GetBytes("Harmless file fixture; never executed."));
            Write(Path.Combine(source, "UI", "probe.txt"), Encoding.UTF8.GetBytes("File deployment probe; no native module or audio data."));
            (Transaction Tx, string Root) Fixture(string name) {
                string root = Path.Combine(parent, name);
                var receipt = new Receipt { Operation = "ApplicationFileCheck", Target = new("fixture", "fixture", "fixture", "File check",
                    "fixture", @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{11111111-1111-1111-1111-111111111111}\FxProperties") };
                var tx = Transaction.Reference(receipt, source, new(Contract.Version, "", []));
                ownedDirectories.Add(tx.DirectoryPath);
                ownedFiles.Add(Path.Combine(tx.DirectoryPath, "snapshot.json")); ownedFiles.Add(Path.Combine(tx.DirectoryPath, "snapshot-base.json"));
                var files = new List<PayloadFile> { new("DOT MIC.exe", Contract.FileHash(Path.Combine(source, "DOT MIC.exe"))),
                    new("内部ファイル/UI/probe.txt", Contract.FileHash(Path.Combine(source, "UI", "probe.txt"))) };
                string marker = Path.Combine(tx.DirectoryPath, "application-marker.new.json");
                Write(marker, JsonSerializer.SerializeToUtf8Bytes(new InstalledApplication(root, files, null, source), Contract.Json));
                receipt.Application = new() { Root = root, Files = files.Select(f => new FileEdit { Relative = f.Path, Hash = f.Hash }).ToList() };
                receipt.Application.Files.Add(new() { Relative = ".dot-mic-install.json", Hash = Contract.FileHash(marker) });
                tx.Save();
                foreach (var file in receipt.Application.Files) {
                    string path = Transaction.SafePath(root, file.Relative); ownedFiles.Add(path); ownedFiles.Add(path + ".stage-" + receipt.TransactionId);
                    for (string? dir = Path.GetDirectoryName(path); dir != null && InstallPaths.Within(dir, root); dir = Path.GetDirectoryName(dir)) ownedDirectories.Add(dir);
                }
                return (tx, root);
            }
            var install = Fixture("install-and-rollback");
            ApplicationInstaller.Apply(install.Tx, source);
            Check(ApplicationInstaller.ReadInstalled(install.Root)?.Root == install.Root, "protected installation marker read");
            foreach (var file in install.Tx.Receipt.Application!.Files) Check(Contract.FileHash(Transaction.SafePath(install.Root, file.Relative)) == file.Hash, "deployed hash");
            ApplicationInstaller.Rollback(install.Tx, false, (_, _) => false);
            Check(!Directory.Exists(install.Root) || !Directory.EnumerateFiles(install.Root, "*", SearchOption.AllDirectories).Any(), "rollback removes exact new owned files");
            Check(ApplicationInstaller.ReadInstalled(install.Root) == null, "same destination can be reused after rollback");
            checks.Add("protected placement and rollback");

            var partial = Fixture("interrupted-stage");
            SecureStorage.Directory(partial.Root);
            var entry = partial.Tx.Receipt.Application!.Files[0]; entry.StageStarted = true; partial.Tx.Save();
            string partialStage = Transaction.SafePath(partial.Root, entry.Relative) + ".stage-" + partial.Tx.Receipt.TransactionId;
            Write(partialStage, Encoding.UTF8.GetBytes("Harmless file"));
            ApplicationInstaller.Rollback(partial.Tx, false, (_, _) => false);
            Check(!File.Exists(partialStage), "interrupted prefix-matching stage removed");
            checks.Add("interrupted partial stage cleanup");

            var failure = Fixture("failed-move-and-retry");
            SecureStorage.Directory(failure.Root);
            string collision = Transaction.SafePath(failure.Root, "DOT MIC.exe"); SecureStorage.Directory(collision); ownedDirectories.Add(collision);
            bool failed = false;
            try { ApplicationInstaller.Apply(failure.Tx, source); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed, "controlled file move failure");
            ApplicationInstaller.Rollback(failure.Tx, false, (_, _) => false);
            Check(!Directory.EnumerateFiles(failure.Root, "*.stage-*", SearchOption.AllDirectories).Any(), "failed move stage removed");
            Directory.Delete(collision, false);
            ApplicationInstaller.Apply(failure.Tx, source); ApplicationInstaller.Rollback(failure.Tx, false, (_, _) => false);
            checks.Add("file move failure cleanup and retry");

            var changed = Fixture("retain-changed-file"); ApplicationInstaller.Apply(changed.Tx, source);
            string changedFile = Transaction.SafePath(changed.Root, "内部ファイル/UI/probe.txt");
            byte[] changedBytes = Encoding.UTF8.GetBytes("Changed fixture data must be retained."); Write(changedFile, changedBytes);
            bool rejected = false;
            try { ApplicationInstaller.Rollback(changed.Tx, false, (_, _) => false); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(changedFile).SequenceEqual(changedBytes), "unconfirmed changed file retained");
            ApplicationInstaller.Rollback(changed.Tx, true, (_, _) => false);
            Check(File.ReadAllBytes(changedFile).SequenceEqual(changedBytes), "explicit retain decision respected");
            checks.Add("changed-file retention");

            var uninstall = Fixture("owned-file-removal"); ApplicationInstaller.Apply(uninstall.Tx, source);
            var inventory = ApplicationInstaller.ReadInstalled(uninstall.Root)!;
            string journal = Path.Combine(uninstall.Tx.DirectoryPath, "application-removal.json");
            Write(journal, JsonSerializer.SerializeToUtf8Bytes(new ApplicationRemoval(inventory, []), Contract.Json));
            ownedFiles.Add(journal + ".result");
            uninstall.Tx.Receipt.ApplicationRemovalJournal = journal; uninstall.Tx.Receipt.ApplicationRemovalPending = true;
            uninstall.Tx.Receipt.Status = "ApplicationRemovalPending"; uninstall.Tx.Save();
            Check(ApplicationInstaller.PendingRemovalReceipts().Contains(uninstall.Tx.ReceiptPath), "pending deletion survives restart discovery");
            ApplicationInstaller.RemoveOwned(journal);
            Check(ApplicationInstaller.PendingRemoval(uninstall.Tx.DirectoryPath) == null, "deletion acknowledgement durable");
            Check(ApplicationInstaller.ReadInstalled(uninstall.Root) == null, "same destination can be reused after deletion");
            Check(!ApplicationInstaller.PendingRemovalReceipts().Contains(uninstall.Tx.ReceiptPath), "acknowledged deletion not rediscovered");
            checks.Add("owned deletion, durable acknowledgement and same-folder reuse");

            var stale = Fixture("retain-new-installation"); ApplicationInstaller.Apply(stale.Tx, source);
            var oldInventory = ApplicationInstaller.ReadInstalled(stale.Root)!;
            string staleJournal = Path.Combine(stale.Tx.DirectoryPath, "application-removal.json");
            Write(staleJournal, JsonSerializer.SerializeToUtf8Bytes(new ApplicationRemoval(oldInventory, []), Contract.Json));
            Write(Transaction.SafePath(stale.Root, ".dot-mic-install.json"), JsonSerializer.SerializeToUtf8Bytes(oldInventory with { PackageRoot = source + "-new" }, Contract.Json));
            bool staleRejected = false;
            try { ApplicationInstaller.RemoveOwned(staleJournal); } catch (IOException) { staleRejected = true; }
            Check(staleRejected && File.Exists(Transaction.SafePath(stale.Root, "DOT MIC.exe")), "stale cleanup never deletes a newer installation");
            checks.Add("stale deletion refuses changed installation inventory");

            string link = Path.Combine(source, "fixture-shortcut.lnk"); DesktopShortcut.Create(link, Path.Combine(parent, "DOT MIC.exe"));
            SecureStorage.File(link); ownedFiles.Add(link); Check(new FileInfo(link).Length > 0, "sandbox-only shortcut created");
            checks.Add("shortcut creation inside sandbox only");
            Check(before.SequenceEqual(ObservedState()), "observed DOT MIC and audio registry unchanged");
            Check((File.Exists(desktop) ? Contract.FileHash(desktop) : null) == beforeShortcut, "real desktop shortcut unchanged");
            File.WriteAllText(output, JsonSerializer.Serialize(new { Result = "PASS", Checks = checks, RealDotMicInstalled = false, AudioRegistryWrites = false,
                AudioServiceOperations = false, RealDesktopModified = false, Scope = "Dedicated GUID file fixtures only", ProgramFilesFixture = parent }, Contract.Json), Encoding.UTF8);
            return 0;
        } catch (Exception error) {
            File.WriteAllText(output, JsonSerializer.Serialize(new { Result = "FAIL", Error = error.ToString(), Checks = checks, ProgramFilesFixture = parent }, Contract.Json), Encoding.UTF8);
            return 1;
        } finally {
            // Only this run's named fixture files; never recursive deletion or parent ACL changes.
            foreach (string file in ownedFiles) if (File.Exists(file)) File.Delete(file);
            foreach (string directory in ownedDirectories.OrderByDescending(d => d.Length))
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
        void Write(string path, byte[] bytes) {
            SecureStorage.Directory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes); SecureStorage.File(path); ownedFiles.Add(path);
            string boundary = path.StartsWith(source, StringComparison.OrdinalIgnoreCase) ? source : path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ? parent : Path.GetDirectoryName(path)!;
            for (string? dir = Path.GetDirectoryName(path); dir != null && InstallPaths.Within(dir, boundary); dir = Path.GetDirectoryName(dir)) ownedDirectories.Add(dir);
        }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
    private static byte[] ObservedState()
    {
        var values = new[] { "ApplicationDir", "ApplicationReceipt", "ApplicationPackageDir", "OriginReceipt", "LatestReceipt", "StableId", "ApoPath", "ApoHash" }
            .Select(name => new { Name = name, Value = RawRegistry.Value(Contract.ConfigPath, name) }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new { Values = values, ProtectedAudio = RawRegistry.Value(Contract.AudioPath, "DisableProtectedAudioDG") }, Contract.Json);
    }
}
