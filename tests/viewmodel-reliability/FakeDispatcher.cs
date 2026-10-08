namespace Microsoft.UI.Dispatching;

internal sealed class DispatcherQueue
{
    internal List<DispatcherQueueTimer> Timers { get; } = [];
    internal DispatcherQueueTimer CreateTimer() { var timer = new DispatcherQueueTimer(); Timers.Add(timer); return timer; }
}
internal sealed class DispatcherQueueTimer
{
    internal TimeSpan Interval { get; set; }
    internal bool IsRepeating { get; set; }
    internal bool IsRunning { get; private set; }
    internal int Starts { get; private set; }
    internal event EventHandler<object?>? Tick;
    internal void Start() { IsRunning = true; Starts++; }
    internal void Stop() => IsRunning = false;
    internal void Fire() { if (!IsRepeating) IsRunning = false; Tick?.Invoke(this, null); }
}
