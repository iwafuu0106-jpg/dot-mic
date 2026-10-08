using System.Text;
using DotMic;
using DotMic.Common;

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
int calls = 0;
string text = NativeTextBuffer.Read((StringBuilder? buffer, uint capacity, out uint required) => {
    calls++; required = calls == 1 ? 3u : 9u;
    if (capacity < required) return NativeTextBuffer.InsufficientBuffer;
    buffer!.Append("[1,2,3]"); return 0;
});
Check(text == "[1,2,3]" && calls == 3, "endpoint list growth is retried");
foreach (uint invalid in new[] { 0u, 1024u * 1024u + 1 }) {
    try { NativeTextBuffer.Read((StringBuilder? buffer, uint capacity, out uint required) => { required = invalid; return NativeTextBuffer.InsufficientBuffer; }); throw new Exception("invalid size accepted"); }
    catch (IOException) { }
}
calls = 0;
try { NativeTextBuffer.Read((StringBuilder? buffer, uint capacity, out uint required) => { calls++; required = capacity + 10; return NativeTextBuffer.InsufficientBuffer; }); throw new Exception("unbounded buffer retry"); }
catch (IOException) { Check(calls == 5, "buffer growth has an attempt cap"); }
const int denied = unchecked((int)0x80070005);
try { NativeTextBuffer.Read((StringBuilder? buffer, uint capacity, out uint required) => { required = 20; return denied; }); throw new Exception("unrelated native error ignored"); }
catch (Exception error) when (error.HResult == denied) { }
var writes = new PendingAudioWrites();
writes.Add(2, 1); var preview = writes.Snapshot(false);
Check(writes.HasPreview && writes.HasChanges, "capturing a batch must not discard failed writes");
Check(writes.RetryCommit() == TimeSpan.FromMilliseconds(500) && writes.RetryCommit() == TimeSpan.FromMilliseconds(1000)
    && writes.RetryCommit() == TimeSpan.FromMilliseconds(2000) && writes.RetryCommit() == null, "write retries are bounded");
writes.Add(2, 2); writes.Complete(preview, false);
Check(writes.Snapshot(false).Single().Value == 2, "old preview completion preserves newer edits");
var commit = writes.Snapshot(true); writes.Add(2, 3); writes.Complete(commit, true);
Check(writes.HasChanges && writes.Snapshot(true).Single().Value == 3, "old commit completion preserves newer edits");
writes.Complete(writes.Snapshot(true), true);
Check(!writes.HasChanges && !writes.HasPreview && writes.RetryCommit() == null, "successful retry drains matching pending edits");
string? registration = null; int resolves = 0;
Check(!StartupOptions.Read(() => registration).Enabled, "uninstalled package can read optional startup state");
var deniedRead = StartupOptions.Read(() => throw new UnauthorizedAccessException("read denied"));
Check(!deniedRead.Enabled && deniedRead.Error == "read denied", "startup read error is isolated");
registration = "missing-launcher --startup";
StartupOptions.Set(false, () => { resolves++; throw new IOException("missing launcher"); }, value => registration = value, () => registration);
Check(registration == null && resolves == 0, "disabling startup does not resolve missing launcher");
StartupOptions.Set(true, () => { resolves++; return "installed --startup"; }, value => registration = value, () => registration);
Check(resolves == 1 && StartupOptions.Read(() => registration).Enabled, "enabling resolves and verifies the exact target");
try { StartupOptions.Set(true, () => throw new IOException("uninstalled"), _ => throw new Exception("must not write"), () => registration); throw new Exception("uninstalled startup target accepted"); }
catch (IOException) { }
foreach (bool startup in new[] { false, true }) foreach (bool ready in new[] { false, true }) foreach (bool tray in new[] { false, true })
    Check(AppVisibilityPolicy.CanHideOnStartup(startup, ready, tray) == (startup && ready && tray), "a missing tray never allows background-only startup");
Console.WriteLine("PASS: bounded endpoint buffers, retained/retried edits, isolated startup options and tray visibility. Delegates only; no registry, service, audio or desktop operations.");
long clock = 0; var commands = new List<DotMic.Setup.ServiceTransition.Command>();
void ServiceCase(uint[] states, bool start, bool paused, params DotMic.Setup.ServiceTransition.Command[] expected) {
    int index = 0; clock = 0; commands.Clear();
    DotMic.Setup.ServiceTransition.Run(() => new(states[Math.Min(index++, states.Length - 1)], 0, 1000), commands.Add, () => clock, delay => clock += delay, "fake-service", start, paused);
    Check(commands.SequenceEqual(expected), "service commands wait for controllable states");
}
ServiceCase([2, 2, 4, 3, 1], false, false, DotMic.Setup.ServiceTransition.Command.Stop);
ServiceCase([3, 3, 1, 2, 4], true, false, DotMic.Setup.ServiceTransition.Command.Start);
ServiceCase([1, 2, 4, 6, 7], true, true, DotMic.Setup.ServiceTransition.Command.Start, DotMic.Setup.ServiceTransition.Command.Pause);
ServiceCase([7, 5, 4], true, false, DotMic.Setup.ServiceTransition.Command.Continue);
ServiceCase([7], true, true);
clock = 0;
DotMic.Setup.ServiceTransition.Run(() => new(clock < 45000 ? 2u : 4u, (uint)(clock / 10000), 15000), _ => throw new Exception("must wait"), () => clock, delay => clock += delay, "slow-but-progressing", true);
Check(clock == 45000, "legitimate progress can exceed the old 20 second cutoff");
clock = 0;
try { DotMic.Setup.ServiceTransition.Run(() => new(2, 0, 1000), _ => { }, () => clock, delay => clock += delay, "stalled", true); throw new Exception("stalled transition accepted"); }
catch (IOException) { Check(clock == 20000, "stalled progress is bounded"); }
clock = 0;
try { DotMic.Setup.ServiceTransition.Run(() => new(2, (uint)(clock / 100), uint.MaxValue), _ => { }, () => clock, delay => clock += delay, "endless-progress", true); throw new Exception("unbounded transition"); }
catch (IOException) { Check(clock == 180000, "overall service deadline is bounded"); }
clock = 0; int raced = 0;
DotMic.Setup.ServiceTransition.Run(() => new(raced == 0 ? 1u : 4u, 0, 1000), _ => { raced++; throw new System.ComponentModel.Win32Exception(1056); }, () => clock, delay => clock += delay, "raced-start", true);
Check(raced == 1, "already-running race is rechecked");
try { DotMic.Setup.ServiceTransition.Run(() => new(1, 0, 1000), _ => throw new System.ComponentModel.Win32Exception(5), () => 0, _ => { }, "denied", true); throw new Exception("service denial ignored"); }
catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5) { }
var primaryError = new IOException("primary"); var finishError = new IOException("finish");
try { FailurePreservation.Run(() => throw primaryError, () => throw finishError); throw new Exception("errors lost"); }
catch (AggregateException error) { Check(error.InnerExceptions.Contains(primaryError) && error.InnerExceptions.Contains(finishError), "operation and recovery failures are both retained"); }
await FailurePreservation.Drain(Task.CompletedTask, () => Task.CompletedTask);
foreach (var pair in new[] { (primaryError, (IOException?)null), ((IOException?)null, finishError), (primaryError, finishError) }) {
    try { await FailurePreservation.Drain(pair.Item2 == null ? Task.CompletedTask : Task.FromException(pair.Item2), () => pair.Item1 == null ? Task.CompletedTask : Task.FromException(pair.Item1)); throw new Exception("verification failure accepted"); }
    catch (AggregateException error) { Check(error.InnerExceptions.Contains(pair.Item1!) && error.InnerExceptions.Contains(pair.Item2!), "verification and capture causes survive"); }
    catch (IOException error) { Check(ReferenceEquals(error, pair.Item1 ?? pair.Item2), "single original failure survives"); }
}
Console.WriteLine("PASS: pending service transitions, progress/stall/overall deadlines and paired failure preservation. Fake clocks/controllers only.");
var endpoint = new DotMic.Setup.EndpointIdentity("runtime", "stable", "container", "fake", "physical", "fx");
clock = 0; int resolvesAfterDelay = 0;
var delayedEndpoint = await DotMic.Setup.AudioReadiness.WaitEndpoint(() => {
    resolvesAfterDelay++; return DotMic.Setup.EndpointIdentity.Resolve(endpoint, clock < 1200 ? [] : [endpoint]);
}, () => clock, delay => { clock += delay; return Task.CompletedTask; });
Check(delayedEndpoint == endpoint && resolvesAfterDelay == 7, "post-restart endpoint readiness is retried within a deadline");
clock = 0;
try { await DotMic.Setup.AudioReadiness.WaitEndpoint(() => DotMic.Setup.EndpointIdentity.Resolve(endpoint, []), () => clock, delay => { clock += delay; return Task.CompletedTask; }); throw new Exception("missing endpoint accepted"); }
catch (IOException) { Check(clock == 10000, "missing endpoint readiness is bounded"); }
clock = 0;
try { await DotMic.Setup.AudioReadiness.WaitEndpoint(() => DotMic.Setup.EndpointIdentity.Resolve(endpoint, [endpoint, endpoint with { EndpointId = "different" }]), () => clock, _ => throw new Exception("must not retry ambiguity")); throw new Exception("ambiguous endpoint selected"); }
catch (InvalidOperationException) { Check(clock == 0, "ambiguous identity fails without guessing"); }
var activity = new TaskCompletionSource(); clock = 0;
var advancedProgress = await DotMic.Setup.AudioReadiness.WaitProgress(() => {
    if (clock < 1000) throw new System.Runtime.InteropServices.COMException("not published", unchecked((int)0x80070015));
    return new(clock < 2000 ? 1u : 2u, clock < 2000 ? 480u : 960u, 1, 0);
}, activity.Task, true, () => clock, delay => { clock += delay; return Task.CompletedTask; });
Check(clock == 2000 && advancedProgress.Frames == 960, "delayed publication and temporarily stale counters can recover");
clock = 0;
try { await DotMic.Setup.AudioReadiness.WaitProgress(() => new(1, 480, 1, 0), activity.Task, true, () => clock, delay => { clock += delay; return Task.CompletedTask; }); throw new Exception("stale counters accepted"); }
catch (IOException) { Check(clock >= 4500 && clock <= 4700, "progress polling fits the supported capture window"); }
clock = 0;
try { await DotMic.Setup.AudioReadiness.WaitProgress(() => new((ulong)clock + 1, (ulong)clock + 480, 1, 5), activity.Task, true, () => clock, delay => { clock += delay; return Task.CompletedTask; }); throw new Exception("queue error accepted"); }
catch (IOException) { }
try { await DotMic.Setup.AudioReadiness.WaitProgress(() => new(1, 480, 1, 0), Task.FromException(primaryError), true, () => 0, _ => Task.CompletedTask); throw new Exception("failed capture accepted"); }
catch (IOException error) { Check(ReferenceEquals(error, primaryError), "capture failure remains a failure"); }
Console.WriteLine("PASS: delayed endpoint/metrics readiness, missing/ambiguous identity, stalled counters, queue errors and capture failure. Fake endpoints, counters and clocks only.");
byte[] originalBytes = [1, 2, 3, 4], nextBytes = [9, 8, 7, 6], stagedBytes = [];
string ByteHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
try { DotMic.Setup.StagedFileReplacement.Publish(() => { stagedBytes = [9, 8]; throw new IOException("interrupted copy"); }, () => ByteHash(stagedBytes), ByteHash(nextBytes), () => originalBytes = stagedBytes); throw new Exception("interrupted copy published"); }
catch (IOException) { Check(originalBytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "partial restore never overwrites the destination"); }
using (var partialStage = new MemoryStream(stagedBytes)) using (var sourceBytes = new MemoryStream(nextBytes))
    Check(DotMic.Setup.StagedFileReplacement.PrefixMatches(partialStage, sourceBytes), "interrupted own prefix can be discarded and retried");
using (var changedStage = new MemoryStream([9, 99])) using (var sourceBytes = new MemoryStream(nextBytes))
    Check(!DotMic.Setup.StagedFileReplacement.PrefixMatches(changedStage, sourceBytes), "externally modified stage is not accepted");
using (var oversizedStage = new MemoryStream([9, 8, 7, 6, 5])) using (var sourceBytes = new MemoryStream(nextBytes))
    Check(!DotMic.Setup.StagedFileReplacement.PrefixMatches(oversizedStage, sourceBytes), "overlong stage is not accepted");
try { DotMic.Setup.StagedFileReplacement.Publish(() => stagedBytes = [9, 99], () => ByteHash(stagedBytes), ByteHash(nextBytes), () => throw new Exception("must not publish")); throw new Exception("wrong hash accepted"); }
catch (IOException) { }
DotMic.Setup.StagedFileReplacement.Publish(() => stagedBytes = nextBytes.ToArray(), () => ByteHash(stagedBytes), ByteHash(nextBytes), () => originalBytes = stagedBytes);
Check(originalBytes.SequenceEqual(nextBytes), "full verified stage can publish after retry");
Console.WriteLine("PASS: interrupted/changed/hash-mismatched replacement stages and successful retry. Memory-only file model; no real installation files.");
int captureAttempts = 0;
await DotMic.Setup.AudioReadiness.VerifyAttempts(duration => {
    Check(duration is >= 500 and <= 5000, "production request respects the pinned native capture contract");
    captureAttempts++;
    return captureAttempts < 3 ? Task.FromException(new DotMic.Setup.AudioReadiness.ProcessNotReadyException()) : Task.CompletedTask;
});
Check(captureAttempts == 3, "delayed processing gets bounded supported-window retries");
captureAttempts = 0;
try { await DotMic.Setup.AudioReadiness.VerifyAttempts(_ => { captureAttempts++; return Task.FromException(new DotMic.Setup.AudioReadiness.ProcessNotReadyException()); }); throw new Exception("unbounded verification retry"); }
catch (DotMic.Setup.AudioReadiness.ProcessNotReadyException) { Check(captureAttempts == 3, "persistent verification failure remains failure"); }
captureAttempts = 0;
try { await DotMic.Setup.AudioReadiness.VerifyAttempts(_ => { captureAttempts++; return Task.FromException(primaryError); }); throw new Exception("permanent verification error accepted"); }
catch (IOException error) { Check(captureAttempts == 1 && ReferenceEquals(error, primaryError), "wrong modules and permanent capture failures are not retried away"); }
Check(DotMic.Setup.ServiceTransition.ControlAccess(DotMic.Setup.ServiceTransition.Command.Continue) == 0x44
    && DotMic.Setup.ServiceTransition.ControlAccess(DotMic.Setup.ServiceTransition.Command.Pause) == 0x44
    && (DotMic.Setup.ServiceTransition.ControlAccess(DotMic.Setup.ServiceTransition.Command.Start) & 0x40) == 0, "pause/continue rights are acquired only for those commands");
Console.WriteLine("PASS: native capture argument boundary, finite readiness attempts and lazy pause/continue access plan.");
