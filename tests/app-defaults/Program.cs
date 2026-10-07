using System.Text.Json;
using DotMic;

static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
var defaults = new Settings(); defaults.Validate();
Check(!defaults.MasterBypass && defaults.StartOnSignIn, "新規設定はバイパス無効・サインイン起動有効");
Check(defaults.Gain == 0 && !defaults.Gate && !defaults.Nc, "音量・ゲート・ノイズ除去の初期値を維持");
var existing = JsonSerializer.Deserialize<Settings>("{\"Version\":1,\"MasterBypass\":true,\"StartOnSignIn\":false,\"Motion\":\"unused\",\"Resident\":false}")!;
existing.Validate();
Check(existing.MasterBypass && !existing.StartOnSignIn, "保存したバイパスとサインイン設定は維持");
string json = JsonSerializer.Serialize(existing);
Check(!json.Contains("Motion") && !json.Contains("Resident"), "廃止した設定は保存しない");
Check(AppEntryPaths.Application("C:/example/UI", "fallback.exe") == "fallback.exe", "旧配置を維持");
Check(AppEntryPaths.Setup("C:/example/UI").EndsWith("DotMic.Setup.exe"), "旧版の修復先を維持");
Console.WriteLine("PASS app defaults and legacy JSON only; no startup registration or audio writes.");
try { AppEntryPaths.InstalledApplication(null); throw new Exception("未導入のダウンロード先を起動登録に使用してしまいます"); } catch (IOException) { }
var folder = Path.Combine(Path.GetTempPath(), "opencode", "DotMic-entry-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try {
    File.WriteAllText(Path.Combine(folder, "DOT MIC.exe"), "inert fixture; never executed");
    Check(AppEntryPaths.InstalledApplication(folder) == Path.Combine(folder, "DOT MIC.exe"), "インストール用配布では導入先を起動登録に使用");
} finally { File.Delete(Path.Combine(folder, "DOT MIC.exe")); Directory.Delete(folder, false); }
