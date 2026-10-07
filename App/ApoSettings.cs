using System.Runtime.InteropServices;
using System.Text;
namespace DotMic;
[StructLayout(LayoutKind.Sequential)]
internal struct ApoValues
{
    internal uint size, version;
    internal float gain, threshold, hysteresis, attack, hold, release;
    internal uint bypass, gate, nc;
}
[StructLayout(LayoutKind.Sequential)]
internal struct ApoStatus
{
    internal uint size, version;
    internal float input, output, limiter;
    internal uint running, nc, gate;
    internal ulong runs, adopted, fallback, faults;
}
internal static class ApoSettings
{
    internal const string StableId = "{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}";
    [DllImport("DotMic.ApoSettings.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int dm_apo_read(ref ApoValues values, ref ApoStatus status, StringBuilder name, uint capacity, uint request);
    [DllImport("DotMic.ApoSettings.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dm_apo_set(uint property, float value, uint userCommit);
    internal static (ApoValues values, ApoStatus status, string name) Read(bool request)
    {
        var values = new ApoValues { size = (uint)Marshal.SizeOf<ApoValues>(), version = 1 };
        var status = new ApoStatus { size = (uint)Marshal.SizeOf<ApoStatus>(), version = 1 };
        var name = new StringBuilder(512);
        Marshal.ThrowExceptionForHR(dm_apo_read(ref values, ref status, name, (uint)name.Capacity, request ? 1u : 0u));
        return (values, status, name.ToString());
    }
    internal static void Set(uint property, float value, bool commit) => Marshal.ThrowExceptionForHR(dm_apo_set(property, value, commit ? 1u : 0u));
}
