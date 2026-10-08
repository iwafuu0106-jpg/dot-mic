namespace DotMic.Setup;

internal static class SetupWorker
{
    internal static Task Run(Func<Task> action) => Run(async () => { await action().ConfigureAwait(false); return true; });

    internal static Task<T> Run<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { completion.SetResult(action().GetAwaiter().GetResult()); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "DOT MIC Setup operation" };
        // Preparation may create a ShellLink. Never run its synchronous work on the UI thread.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool Decide(Control owner, Func<bool> decision) => owner.InvokeRequired ? (bool)owner.Invoke(decision) : decision();
}
