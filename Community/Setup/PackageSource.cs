using System.Text.Json;

namespace DotMic.Setup;

internal static class PackageSource
{
    internal static string DirectoryPath { get; private set; } = AppContext.BaseDirectory;
    private static bool installable;
    internal static bool Installable => installable || File.Exists(Path.Combine(AppContext.BaseDirectory, "installation-package.json"));
    internal static void Initialize()
    {
        string identity = Path.Combine(AppContext.BaseDirectory, "installation-package.json");
        bool hasIdentity = File.Exists(identity);
        if (hasIdentity) {
            var marker = JsonSerializer.Deserialize<PackageIdentity>(File.ReadAllText(identity), Contract.Json);
            if (marker?.Version != "0.4.2" || marker.BackendContract != Contract.Version) throw new IOException("配布ファイルの版が一致しません。");
        }
        string nearby = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC", "Packages");
        bool nearPayload = File.Exists(Path.Combine(nearby, "payload.json"));
        bool newDistribution = nearPayload && JsonSerializer.Deserialize<Payload>(File.ReadAllText(Path.Combine(nearby, "payload.json")), Contract.Json)?.ApplicationEntries != null;
        string installedMarker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".dot-mic-install.json"));
        string? installedPackage = null;
        if (!nearPayload && File.Exists(installedMarker)) {
            try {
                SecureStorage.Validate(Path.GetDirectoryName(installedMarker)!, true); SecureStorage.Validate(installedMarker, false);
                installedPackage = JsonSerializer.Deserialize<InstalledApplication>(File.ReadAllText(installedMarker), Contract.Json)?.PackageRoot;
                if (string.IsNullOrWhiteSpace(installedPackage)) installedPackage = null;
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) {
                // Do not trust a damaged/unprotected inventory. A separately
                // configured cache still undergoes all protected-path/hash checks.
            }
        }
        string? configuredPackage = PackageSelection.ReadFallback(nearPayload, installedPackage,
            () => RawRegistry.Value(Contract.ConfigPath, "ApplicationPackageDir")?.Display);
        if (!hasIdentity && !newDistribution && !nearby.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(installedMarker) && configuredPackage == null) return; // Legacy portable setup remains compatible.
        installable = true;
        DirectoryPath = nearPayload ? nearby
            : installedPackage ?? configuredPackage ?? throw new IOException("修復用の配布ファイルを確認できません。新しいセットアップをダウンロードしてください。");
        DirectoryPath = Path.GetFullPath(DirectoryPath);
        if (!DirectoryPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) StageDistribution(DirectoryPath, root);
        SecureStorage.Validate(root, true); SecureStorage.Validate(DirectoryPath, true);
        ValidateFiles("DotMic.Integration.dll"); // Unrelated UI assets must not prevent independent receipt recovery.
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Integration).Assembly, (name, assembly, search) =>
            name == "DotMic.Integration.dll" ? System.Runtime.InteropServices.NativeLibrary.Load(Transaction.SafePath(DirectoryPath, name)) : 0);
    }
    private static void StageDistribution(string source, string cacheRoot)
    {
        var manifest = JsonSerializer.Deserialize<Payload>(File.ReadAllText(Path.Combine(source, "payload.json")), Contract.Json) ?? throw new IOException("配布ファイルの確認情報がありません。");
        if (manifest.Version != Contract.Version) throw new IOException("配布ファイルの版が一致しません。");
        var entries = manifest.ApplicationEntries ?? throw new IOException("起動用ファイルの確認情報がありません。");
        if (entries.Count != 2 || entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2
            || entries.Any(e => e.Path is not ("DOT MIC.exe" or "セットアップ.exe"))) throw new IOException("起動用ファイルの範囲が不正です。");
        SecureStorage.Directory(cacheRoot);
        DirectoryPath = Path.Combine(cacheRoot, Guid.NewGuid().ToString("D")); SecureStorage.Directory(DirectoryPath);
        var files = manifest.Files.Concat(entries).ToList();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            string target = Transaction.SafePath(DirectoryPath, file.Path);
            if (!paths.Add(target)) throw new IOException("配布ファイルのパスが重複しています。");
            string origin = Transaction.SafePath(entries.Contains(file) ? Path.GetDirectoryName(source)! : source, file.Path);
            bool verified;
            try { verified = File.Exists(origin) && Contract.FileHash(origin) == file.Hash; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { verified = false; }
            if (!verified) continue; // Missing/unreadable unrelated files still permit helper-only recovery.
            SecureStorage.Directory(Path.GetDirectoryName(target)!);
            using (var input = new FileStream(origin, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            SecureStorage.File(target);
            if (Contract.FileHash(target) != file.Hash) throw new IOException("確認中に配布ファイルが変更されました。");
        }
        string cachedManifest = Path.Combine(DirectoryPath, "payload.json");
        File.WriteAllBytes(cachedManifest, JsonSerializer.SerializeToUtf8Bytes(new Payload(manifest.Version, manifest.ApoHash, files), Contract.Json)); SecureStorage.File(cachedManifest);
    }
    internal static string CleanupExecutable()
    {
        string target = Transaction.SafePath(DirectoryPath, "UI/DotMic.Setup.exe");
        ValidateFiles("UI/DotMic.Setup.exe", "UI/DotMic.Setup.dll", "UI/DotMic.Setup.deps.json", "UI/DotMic.Setup.runtimeconfig.json", "UI/coreclr.dll", "UI/hostfxr.dll", "UI/hostpolicy.dll");
        return target;
    }
    private static void ValidateFiles(params string[] names)
    {
        string manifest = Path.Combine(DirectoryPath, "payload.json"); SecureStorage.Validate(manifest, false);
        var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(manifest), Contract.Json) ?? throw new IOException("配布ファイルの確認情報がありません。");
        if (payload.Version != Contract.Version) throw new IOException("配布ファイルの版が一致しません。");
        foreach (string name in names) {
            var file = payload.Files.SingleOrDefault(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("必要ファイルの確認情報がありません：" + name);
            string path = Transaction.SafePath(DirectoryPath, name); SecureStorage.Validate(path, false);
            if (Contract.FileHash(path) != file.Hash) throw new IOException("必要ファイルが配布時の内容と一致しません：" + name);
        }
    }
    private sealed record PackageIdentity(string Version, string BackendContract);
}
