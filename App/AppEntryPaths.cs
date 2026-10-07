namespace DotMic;

internal static class AppEntryPaths
{
    internal static string Application(string appDirectory, string processPath)
    {
        string launcher = Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "DOT MIC.exe"));
        return IsNewLayout(appDirectory) && File.Exists(launcher) ? launcher : processPath;
    }
    internal static string Setup(string appDirectory)
    {
        string launcher = Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "セットアップ.exe"));
        return IsNewLayout(appDirectory) && File.Exists(launcher) ? launcher : Path.GetFullPath(Path.Combine(appDirectory, "..", "DotMic.Setup.exe"));
    }
    private static bool IsNewLayout(string appDirectory) => new DirectoryInfo(Path.GetFullPath(appDirectory)).Parent?.Name == "内部ファイル";
}
