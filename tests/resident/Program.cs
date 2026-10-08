using DotMic.Setup;

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
var schedule = new ReconcileSchedule();
Check(schedule.Next(false) == Timeout.Infinite, "idle has no polling timeout");
Check(schedule.Next(true) == 2000 && schedule.Next(true) == 4000, "pending work uses bounded backoff");
for (int i = 0; i < 100; i++) Check(schedule.Next(true) <= 300000, "backoff has a finite maximum");
Check(schedule.Next(true, true) == 2000, "device event permits a fresh attempt");
Check(schedule.Next(false) == Timeout.Infinite && schedule.Next(true) == 2000, "successful recovery clears the failure schedule");
int calls = 0, errors = 0; var waits = new List<int>();
ResidentLoop.Run(() => { calls++; if (calls == 1) throw new IOException("fixture device not ready"); return false; },
    timeout => { waits.Add(timeout); return waits.Count == 1 ? WaitHandle.WaitTimeout : 0; }, _ => errors++);
Check(calls == 2 && errors == 1 && waits.SequenceEqual([2000, Timeout.Infinite]), "one failure recovers and returns to notification-only idle");
calls = 0; waits.Clear();
ResidentLoop.Run(() => { calls++; return false; }, timeout => { waits.Add(timeout); return waits.Count == 1 ? 1 : 0; }, _ => throw new Exception("unexpected error"));
Check(calls == 2 && waits.All(delay => delay == Timeout.Infinite), "new device wakes exactly one idle reconciliation");
var evidence = new ActivationEvidence();
Check(!evidence.Observe("a", 1, 10, 0), "one stale read is not activation proof");
Check(!evidence.Observe("a", 1, 10, 0), "frozen counter is not activation proof");
Check(!evidence.Observe("b", 1, 11, 0), "endpoint counters are independent");
Check(evidence.Observe("a", 1, 11, 0), "same endpoint frame progress confirms running processing");
Check(!evidence.Observe("a", 0, 20, 0) && !evidence.Observe("a", 1, 21, 0), "stopped observation resets baseline");
Check(!evidence.Observe("a", 1, 1, 0) && evidence.Observe("a", 1, 2, 0), "counter reset cannot reuse prior progress");
Check(!evidence.Observe("a", 1, 3, 5), "processing error is never healthy proof");
Check(DotMic.Common.CaptureEligibility.IsTarget(1, 4, "USB\\VID-fixture", "adapter", "", true), "physical USB microphone included");
Check(DotMic.Common.CaptureEligibility.IsTarget(1, 8, "USB\\interface-fixture", "adapter", "", true), "physical interface line input included");
Check(DotMic.Common.CaptureEligibility.IsTarget(1, -1, "", "", "{DFF21BE1-F70F-11D0-B917-00A0C9223196}", true), "unknown form is assessed using microphone jack metadata");
Check(!DotMic.Common.CaptureEligibility.IsTarget(1, 4, "ROOT\\virtual-fixture", "adapter", "", true), "known software adapter is not auto-enrolled");
Check(!DotMic.Common.CaptureEligibility.IsTarget(1, 4, "USB\\fixture", "adapter", "", false), "missing durable identity is never guessed");
int allocation = 0;
try { FixedRegistryData.Read("2", 4, buffer => { allocation = buffer.Length; return new(234, 3, 64 * 1024 * 1024); }); throw new Exception("oversized value accepted"); }
catch (IOException) { Check(allocation == 4, "unprivileged size never becomes allocation size"); }
var small = FixedRegistryData.Read("2", 4, buffer => { BitConverter.GetBytes(6f).CopyTo(buffer, 0); return new(0, 3, 4); });
Check(small?.Type == 3 && BitConverter.ToSingle(small.Data) == 6f, "bounded binary float read preserved");
Check(FixedRegistryData.Read("2", 4, _ => new(2, 0, 0)) == null, "missing property is not invented");
Check(LegacyProfileValue.Decode(new("4", 1, System.Text.Encoding.Unicode.GetBytes("-48\0")), true) == -48,
    "legacy REG_SZ default threshold migrated");
Check(LegacyProfileValue.Decode(new("1", 4, BitConverter.GetBytes(1u)), true) == 1, "legacy DWORD default bypass migrated");
byte[] serialized = new byte[12]; BitConverter.GetBytes(4u).CopyTo(serialized, 0); BitConverter.GetBytes(1u).CopyTo(serialized, 4); BitConverter.GetBytes(6f).CopyTo(serialized, 8);
Check(LegacyProfileValue.Decode(new("2", 3, serialized), false) == 6, "legacy CAPX User float migrated");
try { LegacyProfileValue.Decode(new("2", 1, System.Text.Encoding.Unicode.GetBytes("6\0")), false); throw new Exception("User type loosened"); }
catch (IOException) { }
Console.WriteLine("PASS: notification-driven resident scheduling, bounded failure backoff, stop and automatic recovery. Fake waits only; no service, registry, microphone or desktop operations.");

namespace DotMic.Setup
{
    internal sealed record RawValue(string Name, uint Type, byte[] Data);
    internal sealed record EndpointIdentity(string EndpointId);
    internal static class Integration { internal static void Status(string endpoint) => throw new Exception("meter requests must not run in pure fixtures"); }
    internal sealed class ParameterNotifications : IDisposable { internal ParameterNotifications(AutoResetEvent changed) { _ = changed; } internal void Arm() => throw new Exception("parameter notifications must not run in pure fixtures"); internal void Consumed() { } public void Dispose() { } }
    // The native COM notification adapter is never instantiated by this test.
    internal sealed class DeviceNotifications : IDisposable { internal DeviceNotifications(AutoResetEvent changed) { _ = changed; } public void Dispose() { } }
}
