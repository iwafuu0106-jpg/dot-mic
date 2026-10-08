namespace DotMic;

internal static class StartupOptions
{
    internal sealed record State(bool Enabled, string? Error);
    internal static State Read(Func<string?> read)
    {
        try { return new(!string.IsNullOrEmpty(read()), null); }
        catch (Exception error) { return new(false, error.Message); }
    }
    internal static void Set(bool enabled, Func<string> command, Action<string?> write, Func<string?> read)
    {
        // Removing registration must not resolve a launcher that may be missing.
        string? expected = enabled ? command() : null;
        write(expected);
        if (!string.Equals(read(), expected, StringComparison.Ordinal)) throw new IOException("サインイン起動の登録を確認できません。Windowsのスタートアップ設定も確認してください。");
    }
}

internal static class AppVisibilityPolicy
{
    internal static bool CanHideOnStartup(bool startup, bool ready, bool trayRegistered) => startup && ready && trayRegistered;
}
