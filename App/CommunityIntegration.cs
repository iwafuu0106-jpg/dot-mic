using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DotMic;

internal static class CommunityIntegration
{
    internal enum HealthKind { Healthy, MissingIntegration, RepairRequired, NeedsSelection, Unknown }
    internal sealed record HealthResult(HealthKind Kind, string Message, string Details = "");
    internal static bool Enabled => File.Exists(Path.Combine(AppContext.BaseDirectory, "community.json"));
    private const string Config = @"SOFTWARE\DOT MIC\Community", Clsid = "{8F611FC3-1A33-477D-9820-F8B9A11D1030}";
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_endpoints(StringBuilder? buffer, uint capacity, out uint required);
    private sealed record Endpoint(string EndpointId, string StableId, string ContainerId, string FriendlyName, string PhysicalInterface, string FxPath,
        int FormFactor = -1, string PnpId = "", string JackSubType = "", uint State = 1);
    internal static HealthResult Health()
    {
        if (!Enabled) return new(HealthKind.Healthy, "");
        try {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var config = hklm.OpenSubKey(Config, false);
            if (config == null) return new(HealthKind.MissingIntegration, "未導入です。セットアップで導入してください。");
            using var common = hklm.OpenSubKey(Config + @"\CommonSettings\User", false);
            if (common == null) return new(HealthKind.RepairRequired, "共通設定がありません。セットアップで修復してください。");
            var all = JsonSerializer.Deserialize<Endpoint[]>(DotMic.Common.NativeTextBuffer.Read(dm_community_endpoints)) ?? [];
            var pending = new List<Endpoint>();
            foreach (var endpoint in all.Where(e => DotMic.Common.CaptureEligibility.IsTarget(e.State, e.FormFactor, e.PnpId, e.PhysicalInterface, e.JackSubType,
                !string.IsNullOrEmpty(e.StableId) || !string.IsNullOrEmpty(e.ContainerId) && !string.IsNullOrEmpty(e.PhysicalInterface)))) {
                const string capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";
                if (!endpoint.FxPath.StartsWith(capture, StringComparison.OrdinalIgnoreCase)
                    || !System.Text.RegularExpressions.Regex.IsMatch(endpoint.FxPath[capture.Length..], @"^\{[0-9a-fA-F-]{36}\}\\FxProperties$"))
                    throw new InvalidDataException("Endpoint capture scope is invalid.");
                using var fx = hklm.OpenSubKey(endpoint.FxPath, false);
                if (!string.Equals(fx?.GetValue("{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},6") as string, Clsid, StringComparison.OrdinalIgnoreCase)
                    || fx?.GetValue(Clsid + ",100") != null) pending.Add(endpoint);
            }
            using var com = hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + Clsid + @"\InprocServer32", false);
            string expected = config.GetValue("ApoPath") as string ?? "";
            if (expected.Length == 0 || !File.Exists(expected) || !string.Equals(com?.GetValue("") as string, expected, StringComparison.OrdinalIgnoreCase)) return new(HealthKind.RepairRequired, "APO登録または配置ファイルがありません。修復できます。");
            string root = config.GetValue("InstallDir") as string ?? "";
            var inventory = JsonSerializer.Deserialize<RequiredFile[]>(config.GetValue("RequiredFiles") as string ?? "[]") ?? [];
            if (root.Length == 0 || inventory.Length == 0) return new(HealthKind.Unknown, "配置ファイルの確認情報を取得できません。");
            foreach (var file in inventory) {
                string path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(HealthKind.Unknown, "配置ファイルの確認情報が不正です。");
                try { File.GetAttributes(path); } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(HealthKind.RepairRequired, "APOの依存ファイルがありません。修復できます。"); }
            }
            using var apo = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\Plugins\AudioProcessingObjects\" + Clsid, false);
            using var alternate = hklm.OpenSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + Clsid, false);
            if (apo == null && alternate == null) return new(HealthKind.RepairRequired, "AudioEngineのAPO登録がありません。修復できます。");
            if (pending.Count > 0) return new(HealthKind.RepairRequired, $"未適用のマイク {pending.Count}。セットアップで確認してください。",
                string.Join(Environment.NewLine, pending.Select(e => $"未適用：{e.FriendlyName}\nEndpointId：{e.EndpointId}\nStableId：{e.StableId}\nFxPath：{e.FxPath}")));
            // Idle capture / no loaded graph is not a repair condition. No property
            // writes, forced stream, privileged module inspection or periodic health polling.
            return new(HealthKind.Healthy, "");
        } catch (Exception e) { return new(HealthKind.Unknown, "マイク統合の状態を確認できません。「診断」を確認してください。", e.ToString()); }
    }
    private sealed record RequiredFile(string Path, string Hash);
    internal static void Repair(bool repair = true)
    {
        string path = AppEntryPaths.Setup(AppContext.BaseDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException("ZIPをすべて展開し、同梱のセットアップを開いてください。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = Path.GetFileName(path) == "セットアップ.exe" ? "open" : "runas", Arguments = repair ? "--repair" : "" });
    }
}
