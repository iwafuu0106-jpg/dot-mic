using System.ComponentModel;
using Microsoft.UI.Dispatching;
namespace DotMic;

// Main and tray share this one state. No capture/render engine or endpoint selection.
internal sealed class AudioViewModel : INotifyPropertyChanged
{
    internal Settings Settings { get; }
    internal AudioStatus Status { get; private set; }
    private string notice = "", integrationActionNotice = "", startupNotice = "", writeNotice = "";
    private CommunityIntegration.HealthResult integrationHealth = new(CommunityIntegration.HealthKind.Healthy, "");
    internal string Notice { get => integrationActionNotice.Length > 0 ? integrationActionNotice : integrationHealth.Message.Length > 0 ? integrationHealth.Message : startupNotice.Length > 0 ? startupNotice : writeNotice.Length > 0 ? writeNotice : notice; private set => notice = value; }
    internal bool IntegrationNeedsRepair => CommunityIntegration.Enabled && integrationHealth.Kind == CommunityIntegration.HealthKind.RepairRequired;
    internal bool IntegrationNeedsSetup => CommunityIntegration.Enabled && integrationHealth.Kind is CommunityIntegration.HealthKind.MissingIntegration or CommunityIntegration.HealthKind.NeedsSelection;
    internal string InputName { get; private set; } = "マイク";
    internal string NcText => Status.ncState switch { 0 => "無効", 1 => "準備中", 3 => "有効", 4 => "原音に戻しています", _ => "原音" };
    internal bool Ready { get; private set; }
    internal bool Exiting { get; private set; }
    internal bool Visible { get; private set; }
    internal bool Suspended { get; private set; }
    internal bool Limit => Environment.TickCount64 < limiterUntil;
    internal event Action? VisibilityChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly bool smoke;
    private readonly DispatcherQueueTimer poll, apply, save;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly PendingAudioWrites edits = new();
    private bool writing, polling;
    private long limiterUntil;
    internal AudioViewModel(DispatcherQueue queue, bool uiSmoke = false)
    {
        smoke = uiSmoke; string warning = ""; Settings = smoke ? new() : SettingsStore.Load(out warning); Notice = warning;
        poll = queue.CreateTimer(); poll.Interval = TimeSpan.FromMilliseconds(200); poll.Tick += async (_, _) => await PollAsync();
        apply = queue.CreateTimer(); apply.IsRepeating = false; apply.Interval = TimeSpan.FromMilliseconds(20); apply.Tick += async (_, _) => await FlushAsync(false);
        save = queue.CreateTimer(); save.IsRepeating = false; save.Interval = TimeSpan.FromMilliseconds(400); save.Tick += async (_, _) => { await FlushAsync(true); SaveNow(); };
    }
    internal async Task InitializeAsync()
    {
        if (smoke) return;
        try { StartupRegistration.Set(Settings.StartOnSignIn); SetStartupNotice(""); SaveNow(); }
        catch (Exception e) { SetStartupNotice("サインイン時の起動を設定できません：" + e.Message); }
        integrationHealth = await Task.Run(CommunityIntegration.Health); Changed();
        // ShowMain may already have started the first read. Await its completion
        // before deciding that settings are unavailable; do not open a spurious dialog.
        await serial.WaitAsync(); serial.Release(); if (!Ready) await PollAsync(true);
    }
    internal async Task RefreshAsync() { integrationActionNotice = ""; integrationHealth = await Task.Run(CommunityIntegration.Health); if (Exiting || Suspended) return; await PollAsync(true); if (Ready && edits.HasChanges && !Exiting && !Suspended) await FlushAsync(true); Changed(); } // No automatic elevation or reinstall.
    internal Task RepairAsync() { try { CommunityIntegration.Repair(IntegrationNeedsRepair); } catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { integrationActionNotice = "セットアップをキャンセルしました。"; } catch (Exception e) { integrationActionNotice = e.Message; } Changed(); return Task.CompletedTask; }
    internal void Update(Action<Settings> update)
    {
        var before = AudioValues(); update(Settings); Settings.Validate(); var after = AudioValues();
        foreach (var (id, value) in after) if (before[id] != value) edits.Add(id, value);
        if (!smoke) { if (!apply.IsRunning) apply.Start(); save.Stop(); save.Interval = TimeSpan.FromMilliseconds(400); save.Start(); } Changed();
    }
    private Dictionary<uint, float> AudioValues() => new() { [1] = Settings.MasterBypass ? 1 : 0, [2] = (float)Settings.Gain, [3] = Settings.Gate ? 1 : 0,
        [4] = (float)Settings.Threshold, [5] = (float)Settings.Attack, [6] = (float)Settings.Hold, [7] = (float)Settings.Release, [8] = Settings.Nc ? 1 : 0, [9] = (float)Settings.Hysteresis };
    private async Task FlushAsync(bool commit)
    {
        if (smoke || writing) { if (writing && commit) save.Start(); return; }
        writing = true; bool succeeded = false;
        try {
            var writes = edits.Snapshot(commit);
            await serial.WaitAsync();
            try { await Task.Run(() => { foreach (var (id, value) in writes) ApoSettings.Set(id, value, commit); }); }
            finally { serial.Release(); }
            edits.Complete(writes, commit); succeeded = true;
            if (!edits.HasChanges) { writeNotice = ""; Changed(); }
        } catch (Exception e) {
            writeNotice = $"設定を反映できません: {e.Message}"; Changed();
            if (commit && !Exiting && !Suspended && edits.RetryCommit() is TimeSpan delay) { save.Interval = delay; save.Start(); }
        }
        finally { writing = false; if (succeeded && edits.HasPreview && !Exiting) apply.Start(); }
    }
    private async Task PollAsync(bool force = false)
    {
        if (smoke || Exiting || Suspended || (!Visible && !force)) return;
        if (force) {
            await serial.WaitAsync();
            if (Exiting || Suspended) { serial.Release(); return; }
        } else if (polling || !serial.Wait(0)) return;
        polling = true;
        try {
            var data = await Task.Run(() => ApoSettings.Read(Visible)); Ready = true;
            InputName = data.name;
            // CAPX is authoritative. Never overwrite in-progress local edits with
            // their older readback. Startup reads existing safe state, never writes it.
            var v = data.values;
            if (!edits.HasChanges && !edits.HasPreview) { Settings.Gain = Math.Round(v.gain, 1); Settings.Gate = v.gate != 0; Settings.Nc = v.nc != 0; Settings.MasterBypass = v.bypass != 0;
                Settings.Threshold = v.threshold; Settings.Hysteresis = v.hysteresis; Settings.Attack = v.attack; Settings.Hold = v.hold; Settings.Release = v.release; }
            var s = data.status;
            Status = new() { running = (int)s.running, ncState = s.nc switch { 0 => 0, 1 => 1, 2 => 3, 3 => 5, _ => 4 }, gateOpen = (int)s.gate,
                inputPeak = s.input, outputPeak = s.output, limiterReductionDb = s.limiter > 0 ? -20 * MathF.Log10(s.limiter) : 0,
                runs = s.runs, wetBlocks = s.adopted / 480, fallbackBlocks = s.fallback / 480, faults = s.faults };
            if (Status.limiterReductionDb >= 1) limiterUntil = Environment.TickCount64 + 300;
            Notice = Status.ncState == 5 ? "ノイズ除去を無効にしてから有効にすると、再試行できます。" : ""; Changed();
        } catch (Exception e) { Ready = false; Status = default; SetNotice($"マイク設定を取得できません: {e.Message}"); }
        finally { polling = false; serial.Release(); }
    }
    internal void SetGain(double gain) => Update(s => s.Gain = Math.Round(Math.Clamp(gain, -12, 36), 1, MidpointRounding.AwayFromZero) + 0d);
    internal void SetNotice(string text) { Notice = text; Changed(); }
    internal void SetStartupNotice(string text) { startupNotice = text; Changed(); }
    internal void SetVisibility(bool visible)
    {
        Visible = visible && !Suspended; poll.Stop();
        if (Visible && !Exiting) { poll.Start(); _ = PollAsync(); } // Hidden UI has no meter/property polling.
        VisibilityChanged?.Invoke();
    }
    internal Task SuspendAsync() { Suspended = true; SetVisibility(false); return Task.CompletedTask; }
    internal void Resume() { Suspended = false; _ = RefreshAsync(); }
    internal async Task ExitAsync() { Exiting = true; poll.Stop(); apply.Stop(); save.Stop(); while (writing) await Task.Delay(20); await FlushAsync(true); SaveNow(); }
    internal void SaveNow() { if (smoke) return; try { SettingsStore.Save(Settings); } catch (Exception e) { SetNotice($"設定の保存に失敗: {e.Message}"); } }
    internal void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
