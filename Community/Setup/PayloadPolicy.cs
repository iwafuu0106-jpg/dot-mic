using System.Text.Json;

namespace DotMic.Setup;

internal static class PayloadPolicy
{
    internal static bool IsCandidate(Payload payload)
    {
        using var stream = typeof(PayloadPolicy).Assembly.GetManifestResourceStream("DotMic.Setup.ApoCandidate.lock.json");
        if (stream == null) return false;
        var files = JsonSerializer.Deserialize<PayloadFile[]>(stream, Contract.Json) ?? throw new IOException("候補版の確認情報が不正です。");
        return files.Single(f => f.Path == "APO/DotMic.ApoGate.dll").Hash == payload.ApoHash;
    }
    // Keep the published dependency lock intact. Candidate hashes are a separate
    // build-specific resource; receipt schema/backend placement are unchanged.
    internal static PayloadFile[] ResolveLock(string apoHash)
    {
        foreach (string name in new[] { "DotMic.Setup.ApoPayload.lock.json", "DotMic.Setup.ApoCandidate.lock.json" }) {
            using var stream = typeof(PayloadPolicy).Assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            var files = JsonSerializer.Deserialize<PayloadFile[]>(stream, Contract.Json) ?? throw new IOException("APOの確認情報が不正です。");
            if (files.Single(f => f.Path == "APO/DotMic.ApoGate.dll").Hash == apoHash) return files;
        }
        throw new IOException("配布ファイルのAPOが固定された内容と一致しません。");
    }
}
