using System.Diagnostics;
using System.Text.Json;

namespace DotMic.Setup;

internal static class ManagerObservation
{
    internal static int Run(int seconds, string output)
    {
        if (seconds is < 2 or > 30) return 2;
        using var stop = new ManualResetEvent(false);
        using var process = Process.GetCurrentProcess();
        TimeSpan before = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        int reconciliations = 0, connected = 0, physical = 0, failures = 0;
        var finish = Task.Run(async () => { await Task.Delay(seconds * 1000); stop.Set(); });
        // Same notification/wait loop as the service, but enumeration only:
        // no profile writes, enrollment, capture, registration or SCM changes.
        ResidentLoop.Run(stop, () => {
            var endpoints = Integration.Endpoints(); connected = endpoints.Length;
            physical = endpoints.Where(e => DotMic.Common.CaptureEligibility.IsTarget(e.State, e.FormFactor, e.PnpId, e.PhysicalInterface, e.JackSubType,
                !string.IsNullOrEmpty(e.StableId) || !string.IsNullOrEmpty(e.ContainerId) && !string.IsNullOrEmpty(e.PhysicalInterface)))
                .Select(e => string.IsNullOrEmpty(e.PnpId) ? e.ContainerId : e.PnpId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            reconciliations++; return false;
        }, _ => failures++);
        finish.GetAwaiter().GetResult(); process.Refresh();
        File.WriteAllBytes(Path.GetFullPath(output), JsonSerializer.SerializeToUtf8Bytes(new {
            Mode = "NotificationObservationOnly", Seconds = clock.Elapsed.TotalSeconds,
            CpuSeconds = (process.TotalProcessorTime - before).TotalSeconds,
            PrivateBytes = process.PrivateMemorySize64, ConnectedCaptureEndpoints = connected,
            PhysicalMicrophoneDevices = physical,
            Reconciliations = reconciliations, Failures = failures,
            RegistryWrites = false, AudioServiceChanges = false, MicrophoneCapture = false, Installed = false
        }, Contract.Json));
        return failures == 0 ? 0 : 1;
    }
}
