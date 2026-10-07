using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DotMic.Setup;

internal static class Integration
{
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_endpoints(StringBuilder? buffer, uint capacity, out uint required);
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_registration(string path, uint apply);
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_registration_plan(string path, StringBuilder buffer, uint capacity);
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_capture(string endpoint, uint milliseconds);
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_status(string endpoint, StringBuilder buffer, uint capacity);
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_set(string endpoint, uint property, float value, uint persist);
    internal static void HResult(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    internal static EndpointIdentity[] Endpoints()
    {
        int hr = dm_community_endpoints(null, 0, out uint required); if (hr != unchecked((int)0x8007007A)) HResult(hr);
        if (required is 0 or > 1024 * 1024) throw new IOException("Endpoint metadata size is invalid.");
        var buffer = new StringBuilder(checked((int)required)); HResult(dm_community_endpoints(buffer, required, out _));
        return JsonSerializer.Deserialize<EndpointIdentity[]>(buffer.ToString(), Contract.Json) ?? [];
    }
    internal static void Registration(string path, bool apply) => HResult(dm_community_registration(path, apply ? 1u : 0u));
    internal static List<RawValue> RegistrationPlan(string path)
    {
        var text = new StringBuilder(8192); HResult(dm_community_registration_plan(path, text, 8192));
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text.ToString()) ?? throw new IOException("GetRegistrationProperties metadataがありません。");
        return values.Select(v => v.Value.ValueKind == JsonValueKind.String ? RawValue.Text(v.Key, v.Value.GetString()!) : RawValue.Dword(v.Key, v.Value.GetUInt32())).ToList();
    }
    internal static JsonElement Status(string endpoint) { var text = new StringBuilder(4096); HResult(dm_community_status(endpoint, text, 4096)); return JsonSerializer.Deserialize<JsonElement>(text.ToString()); }
    internal static void Set(string endpoint, uint property, float value) => HResult(dm_community_set(endpoint, property, value, 1));
    internal static Task VerifyCaptureOnly(EndpointIdentity target) => Task.Run(() => HResult(dm_community_capture(EndpointIdentity.Resolve(target, Endpoints()).EndpointId, 900)));
    internal static void RequireInstalledModulesUnloaded()
    {
        RawRegistry.Privilege("SeDebugPrivilege");
        foreach (var process in Process.GetProcessesByName("audiodg")) using (process) {
            foreach (ProcessModule module in process.Modules) if (Path.GetFullPath(module.FileName).StartsWith(Path.GetFullPath(Contract.InstallRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("配置ファイルがaudiodgにロードされています。サービス再初期化なしの後片付けは実行しません: " + module.FileName);
        }
    }
    internal static async Task Verify(EndpointIdentity target, string? expectedPath, string? expectedHash)
    {
        if (expectedPath != null) RawRegistry.Privilege("SeDebugPrivilege"); // Setup-only module inspection, never ordinary UI.
        var resolved = EndpointIdentity.Resolve(target, Endpoints());
        var capture = Task.Run(() => HResult(dm_community_capture(resolved.EndpointId, 3500)));
        try {
            await Task.Delay(550); var before = Status(resolved.EndpointId); await Task.Delay(700); var after = Status(resolved.EndpointId);
            if (after.GetProperty("Calls").GetUInt64() <= before.GetProperty("Calls").GetUInt64() || after.GetProperty("Frames").GetUInt64() <= before.GetProperty("Frames").GetUInt64() || after.GetProperty("Running").GetInt32() != 1)
                throw new IOException("APOProcessの進行を確認できません。registry書込みだけでは成功にしません。");
            if (expectedPath != null) {
                bool loaded = false;
                foreach (var process in Process.GetProcessesByName("audiodg")) { using (process) { try { foreach (ProcessModule module in process.Modules) if (string.Equals(module.FileName, expectedPath, StringComparison.OrdinalIgnoreCase) && Contract.FileHash(module.FileName) == expectedHash) loaded = true; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } } }
                if (!loaded) throw new IOException("Communityの固定配置DLLがaudiodgへ実ロードされていません。旧PnP DLLの実行はCommunity成功に含めません。");
                if (after.GetProperty("Error").GetUInt64() != 0) throw new IOException("production RTQueueが利用できません。NC代替実装には切り替えません。");
            }
        } finally { await capture; }
    }
}
