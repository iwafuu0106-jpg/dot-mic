namespace DotMic.Common;

internal static class UpdateExitProtocol
{
    internal const string MessageName = "DotMic.ExitForUpdate.0.5.09BAF257";
    internal static bool Accepts(ulong ownCreationTime, ulong requestedCreationTime) => ownCreationTime != 0 && ownCreationTime == requestedCreationTime;
}

internal static class ApplicationExitPolicy
{
    internal static void RequireConsent(bool running, bool approved)
    {
        if (running && !approved) throw new IOException("確認後にDOT MICが起動しました。終了の説明を確認し直してください。");
    }
    internal static bool IsOwnedImage(string expected, string actual) => Path.GetFullPath(expected).Equals(Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase);
    // Longer than the application's ten-second final-save deadline. Never
    // terminate a process whose verified native handle was not retained.
    internal static bool Run(Action request, Func<int, bool> wait, Action terminate)
    {
        request();
        if (wait(12000)) return false;
        terminate();
        if (!wait(5000)) throw new IOException("DOT MICの終了を確認できません。更新していません。");
        return true;
    }
}
