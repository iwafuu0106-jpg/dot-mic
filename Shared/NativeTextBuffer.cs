using System.Runtime.InteropServices;
using System.Text;

namespace DotMic.Common;

internal static class NativeTextBuffer
{
    internal delegate int Reader(StringBuilder? buffer, uint capacity, out uint required);
    internal const int InsufficientBuffer = unchecked((int)0x8007007A);
    internal static string Read(Reader read)
    {
        int hr = read(null, 0, out uint required);
        if (hr != InsufficientBuffer) Marshal.ThrowExceptionForHR(hr);
        for (int attempt = 0; attempt < 4; attempt++) {
            if (required is 0 or > 1024 * 1024) throw new IOException("Endpoint metadata size is invalid.");
            var buffer = new StringBuilder(checked((int)required));
            hr = read(buffer, required, out uint next);
            if (hr == InsufficientBuffer) { required = next; continue; }
            Marshal.ThrowExceptionForHR(hr);
            if (next == 0 || next > buffer.Capacity) throw new IOException("Endpoint metadata size is invalid.");
            return buffer.ToString();
        }
        throw new IOException("マイク一覧が取得中に変化し続けています。接続を確認して再試行してください。");
    }
}
