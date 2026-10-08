using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DotMic.Setup;

internal sealed class ActivationEvidence
{
    private readonly Dictionary<string, ulong> baseline = new(StringComparer.OrdinalIgnoreCase);
    internal bool Observe(string endpoint, int running, ulong frames, ulong error)
    {
        if (running <= 0 || error != 0) { baseline.Remove(endpoint); return false; }
        bool progressed = baseline.TryGetValue(endpoint, out ulong before) && frames > before;
        if (baseline.Count >= 4096 && !baseline.ContainsKey(endpoint)) baseline.Clear();
        baseline[endpoint] = frames;
        return progressed;
    }
    internal void Reset(string endpoint) => baseline.Remove(endpoint);
}

internal static class ActivationObservation
{
    private static readonly ActivationEvidence Evidence = new();
    [DllImport("DotMic.Integration.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int dm_community_observe(string endpoint, StringBuilder? buffer, uint capacity, out uint required);
    internal static bool Progressed(EndpointIdentity endpoint)
    {
        try {
            // Only an activation-pending endpoint is queried here. A bounded
            // volatile meter request wakes non-RT publication on an existing
            // graph; it neither opens capture nor changes processing controls.
            Integration.Status(endpoint.EndpointId);
            string text = DotMic.Common.NativeTextBuffer.Read((StringBuilder? buffer, uint capacity, out uint required) =>
                dm_community_observe(endpoint.EndpointId, buffer, capacity, out required));
            var observed = JsonSerializer.Deserialize<JsonElement>(text);
            return Evidence.Observe(endpoint.EndpointId, observed.GetProperty("Running").GetInt32(),
                observed.GetProperty("Frames").GetUInt64(), observed.GetProperty("Error").GetUInt64());
        } catch (Exception error) when (error is COMException or JsonException or InvalidOperationException or EntryPointNotFoundException
            or IOException or UnauthorizedAccessException or KeyNotFoundException) {
            Evidence.Reset(endpoint.EndpointId); return false;
        }
    }
}
