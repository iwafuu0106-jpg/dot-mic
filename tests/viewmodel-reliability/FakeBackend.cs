namespace DotMic;

// Test-only substitutes. None accesses files, registry, native audio or real timers.
internal sealed class Settings
{
    internal double Gain { get; set; }
    internal bool Gate { get; set; }
    internal double Threshold { get; set; } = -48;
    internal double Hysteresis { get; set; } = 6;
    internal double Attack { get; set; } = 5;
    internal double Hold { get; set; } = 160;
    internal double Release { get; set; } = 120;
    internal bool Nc { get; set; }
    internal bool MasterBypass { get; set; }
    internal bool StartOnSignIn { get; set; }
    internal void Validate() { }
}
internal static class SettingsStore
{
    internal static Settings Load(out string warning) { warning = ""; return new(); }
    internal static void Save(Settings settings) { }
}
internal static class StartupRegistration { internal static void Set(bool enabled) { } }
internal static class CommunityIntegration
{
    internal enum HealthKind { Healthy, MissingIntegration, RepairRequired, NeedsSelection, Unknown }
    internal sealed record HealthResult(HealthKind Kind, string Message);
    internal static bool Enabled => true;
    internal static TaskCompletionSource HealthCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static HealthResult Health() { HealthCalled.TrySetResult(); return new(HealthKind.Healthy, ""); }
    internal static void Repair(bool repair) { }
}
internal static class ApoSettings
{
    internal sealed record Values(float gain, float threshold, float hysteresis, float attack, float hold, float release, uint bypass, uint gate, uint nc);
    internal sealed record Metrics(float input, float output, float limiter, uint running, uint nc, uint gate, ulong runs, ulong adopted, ulong fallback, ulong faults);
    internal static bool FailRead, FailWrite;
    internal static int BlockRead;
    internal static int Commits;
    internal static float CommittedGain;
    internal static TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static TaskCompletionSource ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static (Values values, Metrics status, string name) Read(bool request)
    {
        if (Interlocked.Exchange(ref BlockRead, 0) == 1) { ReadEntered.TrySetResult(); ReleaseRead.Task.GetAwaiter().GetResult(); }
        if (FailRead) throw new IOException("fake disconnected microphone");
        return (new(CommittedGain, -48, 6, 5, 160, 120, 0, 0, 0), new(0, 0, 1, 1, 0, 0, 1, 0, 0, 0), "fake microphone");
    }
    internal static void Set(uint property, float value, bool commit)
    {
        if (FailWrite) throw new IOException("fake transient write failure");
        if (commit) { Interlocked.Increment(ref Commits); if (property == 2) CommittedGain = value; }
    }
}
