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
[StructLayout(LayoutKind.Sequential)]
internal struct ApoObservation
{
    internal uint size, version, state, targets, observed, missing;
    internal int result;
    internal uint reason;
    internal ulong calls, frames;
}
internal static class ApoSettings
{
    [DllImport("DotMic.ApoSettings.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int dm_apo_read_ex(ref ApoValues values, ref ApoStatus status, ref ApoObservation observation, StringBuilder name, uint capacity, uint request);
    [DllImport("DotMic.ApoSettings.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dm_apo_set(uint property, float value, uint userCommit);
    internal static (ApoValues values, ApoStatus status, ApoObservation observation, string name) Read(bool request)
    {
        var values = new ApoValues { size = (uint)Marshal.SizeOf<ApoValues>(), version = 1 };
        var status = new ApoStatus { size = (uint)Marshal.SizeOf<ApoStatus>(), version = 1 };
        var observation = new ApoObservation { size = (uint)Marshal.SizeOf<ApoObservation>(), version = 2 };
        var name = new StringBuilder(512);
        Marshal.ThrowExceptionForHR(dm_apo_read_ex(ref values, ref status, ref observation, name, (uint)name.Capacity, request ? 1u : 0u));
        if (observation.version != 2 || observation.size != Marshal.SizeOf<ApoObservation>() || observation.state > 4)
            throw new InvalidDataException("動作情報の形式が一致しません。");
        return (values, status, observation, name.ToString());
    }
    internal static void Set(uint property, float value, bool commit) => Marshal.ThrowExceptionForHR(dm_apo_set(property, value, commit ? 1u : 0u));
}
