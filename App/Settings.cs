using System.Text.Json;
using Microsoft.Win32;

namespace DotMic;

internal enum MotionMode { Full, Reduced, Off }
internal sealed class Settings
{
    public int Version { get; set; } = 1;
    public double Gain { get; set; }
    public bool Gate { get; set; }
    public double Threshold { get; set; } = -48;
    public double Hysteresis { get; set; } = 6;
    public double Attack { get; set; } = 5;
    public double Hold { get; set; } = 160;
    public double Release { get; set; } = 120;
    public bool Nc { get; set; }
    public bool MasterBypass { get; set; }
    public bool StartOnSignIn { get; set; } = true;
    public void Validate()
    {
        // Version1 legacy JSON routing fields are ignored by System.Text.Json.
        if (Version != 1)
            throw new InvalidDataException("設定形式が不正です。");
        Check(Gain, -12, 36); Check(Threshold, -80, -10); Check(Hysteresis, 2, 12);
        Check(Attack, 1, 30); Check(Hold, 50, 500); Check(Release, 30, 500);
        if (Math.Abs(Gain - Math.Round(Gain, 1)) > 1e-8) throw new InvalidDataException("ゲインは0.1 dB刻みです。");
    }
    private static void Check(double v, double min, double max) { if (!double.IsFinite(v) || v < min || v > max) throw new InvalidDataException("設定値が範囲外です。"); }
}

internal static class SettingsStore
{
    internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotMic");
    internal static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    internal static Settings Load(out string warning)
    {
        warning = "";
        if (!File.Exists(FilePath)) return new();
        try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException(); s.Validate(); return s; }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { warning = "保存した設定を読み込めません。初期設定で起動します。"; return new(); }
    }
    internal static void Save(Settings settings)
    {
        settings.Validate(); Directory.CreateDirectory(DirectoryPath);
        string temp = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "DotMic";
    internal static string Command => $"\"{AppEntryPaths.Application(AppContext.BaseDirectory, Environment.ProcessPath!)}\" --startup";
    private static string? ReadValue() { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Value) as string; }
    internal static StartupOptions.State Read() => StartupOptions.Read(ReadValue);
    internal static void Set(bool enabled)
    {
        StartupOptions.Set(enabled, () => Command, value => {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value != null) key.SetValue(Value, value, RegistryValueKind.String); else key.DeleteValue(Value, false);
        }, ReadValue);
    }
}
