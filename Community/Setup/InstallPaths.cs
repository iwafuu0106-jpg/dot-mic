namespace DotMic.Setup;

internal static class InstallPaths
{
    internal static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DOT MIC", "Application");
    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\") || value.Contains('"'))
            throw new IOException("保存先はローカルドライブの絶対パスで指定してください。");
        string path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw new IOException("保存先はネットワークではなく、ローカルドライブを指定してください。");
        if (path.Length <= Path.GetPathRoot(path)!.Length || new DirectoryInfo(path).Name is "." or "..")
            throw new IOException("ドライブ直下そのものは保存先にできません。専用フォルダーを指定してください。");
        foreach (string reserved in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Contract.RecoveryRoot,
            Contract.InstallRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC") })
            if (Within(path, reserved) || Within(reserved, path)) throw new IOException("Windows・音声処理・復旧用のフォルダーとは別の保存先を指定してください。");
        return path;
    }
    internal static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool AllowedFile(string relative) => relative is "DOT MIC.exe" or "セットアップ.exe" or ".dot-mic-install.json" or "内部ファイル/LICENSE" or "内部ファイル/SECURITY.md"
        || relative.StartsWith("内部ファイル/UI/", StringComparison.Ordinal) || relative.StartsWith("内部ファイル/licenses/", StringComparison.Ordinal);
    internal static void ValidateDeployment(ApplicationDeployment deployment)
    {
        if (Normalize(deployment.Root) != deployment.Root) throw new IOException("アプリ保存先の記録が不正です。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in deployment.Files) {
            if (!AllowedFile(file.Relative) || Path.IsPathRooted(file.Relative) || file.Relative.Contains(':')
                || file.Relative.Split('/', '\\').Any(p => p is "" or "." or "..") || !paths.Add(file.Relative))
                throw new IOException("アプリファイルの記録が許可範囲外です。");
        }
    }
    internal static List<Edit> MetadataRemovals(InstalledApplication installation, IReadOnlyList<RawValue> current)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in current) if (value.Name is not ("ApplicationDir" or "ApplicationReceipt" or "ApplicationPackageDir") || !names.Add(value.Name))
            throw new IOException("削除するアプリ設定の範囲が不正です。");
        var root = current.SingleOrDefault(v => v.Name == "ApplicationDir");
        if (root == null || !root.Same(RawValue.Text("ApplicationDir", installation.Root))) return [];
        return current.Select(v => new Edit { Path = Contract.ConfigPath, Name = v.Name, Before = null, After = v, Applied = true }).ToList();
    }
}
