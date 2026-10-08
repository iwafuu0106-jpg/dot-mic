using System.Runtime.ExceptionServices;

namespace DotMic.Common;

internal static class FailurePreservation
{
    internal static void Run(Action operation, Action finish)
    {
        Exception? primary = null;
        try { operation(); } catch (Exception error) { primary = error; }
        try { finish(); }
        catch (Exception error) { if (primary != null && !ReferenceEquals(primary, error)) throw new AggregateException("処理と後処理の両方に失敗しました。", primary, error); throw; }
        if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    internal static async Task Drain(Task activity, Func<Task> inspect)
    {
        Exception? primary = null;
        try { await inspect().ConfigureAwait(false); } catch (Exception error) { primary = error; }
        try { await activity.ConfigureAwait(false); }
        catch (Exception error) { if (primary != null && !ReferenceEquals(primary, error)) throw new AggregateException("検証と音声取得の両方に失敗しました。", primary, error); throw; }
        if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
