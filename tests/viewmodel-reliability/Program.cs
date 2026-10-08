using System.Reflection;
using DotMic;
using Microsoft.UI.Dispatching;

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
static Task Flush(AudioViewModel model, bool commit) => (Task)typeof(AudioViewModel).GetMethod("FlushAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [commit])!;
var queue = new DispatcherQueue(); var model = new AudioViewModel(queue);
model.SetGain(1);
ApoSettings.FailWrite = true;
for (int i = 0; i < 4; i++) await Flush(model, true);
Check(queue.Timers[2].Starts == 4, "actual coordinator schedules only three retries after the initial save");
ApoSettings.FailRead = true;
await model.RefreshAsync();
Check(!model.Ready && model.Settings.Gain == 1, "disconnection retains unapplied edits");
ApoSettings.FailRead = false;
ApoSettings.BlockRead = 1;
model.SetVisibility(true); // Starts a recovery poll before the explicit refresh.
await ApoSettings.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
CommunityIntegration.HealthCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
var refresh = model.RefreshAsync();
await CommunityIntegration.HealthCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(await Task.WhenAny(refresh, Task.Delay(80)) != refresh, "explicit refresh waits for the in-flight recovery read");
ApoSettings.FailWrite = false; ApoSettings.ReleaseRead.TrySetResult();
await refresh.WaitAsync(TimeSpan.FromSeconds(5));
Check(model.Ready && ApoSettings.Commits == 1 && ApoSettings.CommittedGain == 1, "retained edits commit after overlapping recovery poll succeeds");
var edits = (PendingAudioWrites)typeof(AudioViewModel).GetField("edits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
Check(!edits.HasChanges && !edits.HasPreview, "matching pending state drains after actual coordinator recovery");
model.SetGain(2); ApoSettings.FailWrite = true;
await Flush(model, true);
await model.RefreshAsync();
Check(model.Notice.Contains("設定を反映できません") && edits.HasChanges, "successful reads do not hide pending write failure");
ApoSettings.FailWrite = false;
await model.RefreshAsync();
Check(!edits.HasChanges && !model.Notice.Contains("設定を反映できません"), "successful explicit retry clears its write notice");
await model.ExitAsync();
var writesAtExit = ApoSettings.Commits;
await model.RefreshAsync();
Check(ApoSettings.Commits == writesAtExit && model.Exiting, "late refresh does not write after exit");
Console.WriteLine("PASS: actual linked AudioViewModel retry scheduling, overlapping poll/refresh recovery, retained edits/notices and shutdown guards. Fake dispatchers/backends only; no WinUI session, files, registry, native audio or services.");
