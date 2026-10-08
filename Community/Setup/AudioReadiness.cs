namespace DotMic.Setup;

internal static class AudioReadiness
{
    internal sealed record Progress(ulong Calls, ulong Frames, int Running, ulong Error);
    // The pinned native helper accepts only 500..5000 ms. Extend readiness by
    // bounded attempts, never by requesting an unsupported capture duration.
    internal const int CaptureMilliseconds = 5000;
    internal sealed class ProcessNotReadyException : IOException
    {
        internal ProcessNotReadyException() : base("APOProcessの進行を確認できません。registry書込みだけでは成功にしません。") { }
    }
    internal static async Task VerifyAttempts(Func<int, Task> verify)
    {
        for (int attempt = 0; ; attempt++) {
            try { await verify(CaptureMilliseconds).ConfigureAwait(false); return; }
            catch (ProcessNotReadyException) when (attempt < 2) { }
        }
    }
    private const int NotReady = unchecked((int)0x80070015);
    internal static async Task<EndpointIdentity> WaitEndpoint(Func<EndpointIdentity> resolve, Func<long> clock, Func<int, Task> delay)
    {
        long began = clock();
        while (true) {
            try { return resolve(); }
            catch (Exception error) when (error is EndpointUnavailableException || error.HResult == NotReady) {
                if (clock() - began >= 10000) throw new IOException("対象マイクの準備が完了しません。接続を確認してください。", error);
                await delay(200).ConfigureAwait(false);
            }
        }
    }
    internal static async Task<Progress> WaitProgress(Func<Progress> status, Task capture, bool requireQueue,
        Func<long> clock, Func<int, Task> delay)
    {
        long began = clock(); Progress? before = null;
        while (true) {
            if (capture.IsFaulted || capture.IsCanceled) await capture.ConfigureAwait(false);
            try {
                var after = status();
                if (after.Running == 1) {
                    if (before != null && after.Calls > before.Calls && after.Frames > before.Frames) {
                        if (requireQueue && after.Error != 0) throw new IOException("production RTQueueが利用できません。NC代替実装には切り替えません。");
                        return after;
                    }
                    before = after;
                } else before = null;
            } catch (Exception error) when (error.HResult == NotReady) { before = null; }
            if (capture.IsCompleted || clock() - began >= 4500)
                throw new ProcessNotReadyException();
            await delay(200).ConfigureAwait(false);
        }
    }
}
