using DotMic.Setup;

internal static class WorkerChecks
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        int uiThread = Environment.CurrentManagedThreadId;
        Exception? failure = null;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        int ticks = 0;
        timer.Tick += (_, _) => {
            if (entered.IsSet && ++ticks >= 3) release.Set();
        };
        form.Shown += async (_, _) => {
            timer.Start();
            try {
                int result = await SetupWorker.Run(() => {
                    Check(Environment.CurrentManagedThreadId != uiThread, "work must not use the UI thread");
                    Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "ShellLink preparation starts on STA");
                    Check(SynchronizationContext.Current == null, "worker must not capture the UI context");
                    entered.Set();
                    Check(release.Wait(5000), "UI message pump stopped during synchronous work");
                    Check(SetupWorker.Decide(form, () => Environment.CurrentManagedThreadId == uiThread), "conflict decision must run on UI");
                    return Task.FromResult(42);
                });
                Check(result == 42 && ticks >= 3 && Environment.CurrentManagedThreadId == uiThread, "result must return to live UI");
                Check(SetupWorker.Decide(form, () => true), "UI-local decision must not deadlock");
                bool syncFailure = false, asyncFailure = false;
                try { await SetupWorker.Run(() => throw new IOException("fixture sync failure")); }
                catch (IOException error) { syncFailure = error.Message == "fixture sync failure"; }
                try { await SetupWorker.Run(async () => { await Task.Yield(); throw new IOException("fixture async failure"); }); }
                catch (IOException error) { asyncFailure = error.Message == "fixture async failure"; }
                Check(syncFailure && asyncFailure && Environment.CurrentManagedThreadId == uiThread, "both failures must reach the UI guard");
            } catch (Exception error) { failure = error; }
            finally { timer.Stop(); form.Close(); }
        };
        Application.Run(form);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine("PASS: blocking setup work leaves UI responsive; STA preparation, UI-only decisions and exception delivery. Synthetic work only; no installation, registry, native audio or services.");
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
