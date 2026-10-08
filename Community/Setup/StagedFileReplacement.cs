namespace DotMic.Setup;

internal static class StagedFileReplacement
{
    internal static void Publish(Action copyStage, Func<string> stageHash, string expectedHash, Action publish)
    {
        copyStage();
        if (stageHash() != expectedHash) throw new IOException("配置・復元用のコピーが一致しません。");
        // The caller publishes with a same-volume atomic rename, not an overwrite copy.
        publish();
    }
    internal static bool PrefixMatches(Stream stage, Stream source)
    {
        if (stage.Length > source.Length) return false;
        byte[] actual = new byte[65536], expected = new byte[65536];
        while (true) {
            int count = stage.Read(actual, 0, actual.Length);
            if (count == 0) return true;
            try { source.ReadExactly(expected.AsSpan(0, count)); } catch (EndOfStreamException) { return false; }
            if (!actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count))) return false;
        }
    }
}
