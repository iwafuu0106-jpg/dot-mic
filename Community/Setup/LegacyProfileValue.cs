using System.Globalization;
using System.Text;

namespace DotMic.Setup;

internal static class LegacyProfileValue
{
    internal static float Decode(RawValue value, bool defaults)
    {
        if (value.Type == 3 && value.Data.Length == 12 && BitConverter.ToUInt32(value.Data, 0) == 4 && BitConverter.ToUInt32(value.Data, 4) == 1)
            return BitConverter.ToSingle(value.Data, 8);
        if (defaults && value.Type == 4 && value.Data.Length == 4) return BitConverter.ToUInt32(value.Data);
        if (defaults && value.Type == 1 && value.Data.Length is >= 2 and <= 32 && value.Data.Length % 2 == 0) {
            string text = Encoding.Unicode.GetString(value.Data);
            if (text[^1] == '\0' && !text[..^1].Contains('\0')
                && float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float result)) return result;
        }
        throw new IOException("以前の設定を移行できません。共通設定は変更していません。");
    }
}
