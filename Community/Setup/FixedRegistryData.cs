namespace DotMic.Setup;

internal static class FixedRegistryData
{
    internal sealed record Result(int Error, uint Type, uint Length);
    internal static RawValue? Read(string name, int capacity, Func<byte[], Result> query)
    {
        if (capacity is not (4 or 12 or 32)) throw new ArgumentOutOfRangeException(nameof(capacity));
        byte[] bytes = new byte[capacity];
        var result = query(bytes);
        if (result.Error == 2) return null;
        if (result.Error == 234 || result.Length > bytes.Length) throw new IOException("共通設定の値が大きすぎます。");
        if (result.Error != 0) throw new System.ComponentModel.Win32Exception(result.Error);
        Array.Resize(ref bytes, (int)result.Length);
        return new(name, result.Type, bytes);
    }
}
