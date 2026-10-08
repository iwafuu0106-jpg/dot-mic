namespace DotMic.Setup;

internal sealed class ReconcileSchedule
{
    private int failures;
    internal int Next(bool pending, bool deviceEvent = false)
    {
        if (deviceEvent) failures = 0;
        if (!pending) { failures = 0; return Timeout.Infinite; }
        return Math.Min(300000, 2000 << Math.Min(failures++, 7));
    }
}

internal static class ResidentLoop
{
    // No work at idle. Timed reconciliation exists only for a deferred job or
    // a transient failure; backoff prevents an unhealthy driver becoming a spin.
    internal static void Run(Func<bool> reconcile, Func<int, int> wait, Action<Exception> failed)
    {
        var schedule = new ReconcileSchedule();
        bool deviceEvent = true;
        while (true) {
            bool pending;
            try { pending = reconcile(); }
            catch (Exception error) { failed(error); pending = true; }
            int wake = wait(schedule.Next(pending, deviceEvent));
            if (wake == 0) return;
            deviceEvent = wake == 1;
        }
    }
    internal static void Run(WaitHandle stop, Func<bool> reconcile, Action<Exception> failed, Action? ready = null, bool watchParameters = false)
    {
        using var changed = new AutoResetEvent(false);
        using var parametersChanged = new AutoResetEvent(false);
        using var notifications = new DeviceNotifications(changed);
        using var parameters = watchParameters ? new ParameterNotifications(parametersChanged) : null;
        parameters?.Arm();
        ready?.Invoke();
        Run(() => { parameters?.Arm(); return reconcile(); }, timeout => {
            int wake = WaitHandle.WaitAny(watchParameters ? [stop, changed, parametersChanged] : [stop, changed], timeout);
            if (wake == 2) { parameters!.Consumed(); wake = 1; }
            if (wake == 1 && stop.WaitOne(200)) return 0; // Coalesce a plug/unplug burst outside the callback.
            return wake;
        }, failed);
    }
}
