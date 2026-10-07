namespace DotMic;

internal static class AppEntryPaths
{
    internal static string Application(string appDirectory, string processPath)
    {
        bool installable = File.Exists(Path.Combine(appDirectory, "installation-package.json"));
        if (installable || IsNewLayout(appDirectory)) {
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var config = machine.OpenSubKey(@"SOFTWARE\DOT MIC\Community");
            string? installedRoot = config?.GetValue("ApplicationDir") as string;
            if (installable || installedRoot != null) return InstalledApplication(installedRoot);
        }
        string launcher = Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "DOT MIC.exe"));
        return IsNewLayout(appDirectory) && File.Exists(launcher) ? launcher : processPath;
    }
    internal static string InstalledApplication(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\"))
            throw new IOException("セットアップでアプリを導入してから、サインイン時の起動を有効にしてください。");
        string launcher = Path.Combine(Path.GetFullPath(root), "DOT MIC.exe");
        if (!File.Exists(launcher)) throw new IOException("導入先のアプリが見つかりません。セットアップで修復してください。");
        return launcher;
    }
    internal static string Setup(string appDirectory)
    {
        string launcher = Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "セットアップ.exe"));
        return IsNewLayout(appDirectory) && File.Exists(launcher) ? launcher : Path.GetFullPath(Path.Combine(appDirectory, "..", "DotMic.Setup.exe"));
    }
    private static bool IsNewLayout(string appDirectory) => new DirectoryInfo(Path.GetFullPath(appDirectory)).Parent?.Name == "内部ファイル";
}
