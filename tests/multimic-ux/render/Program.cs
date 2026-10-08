using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotMic.Setup;

internal static class RenderChecks
{
    private const string PrivateMarker = "FAKE-ONLY-ID-HRESULT-SNAPSHOT";
    private static readonly List<object> captures = [];
    private static readonly List<string> flows = [];
    private static string output = "";
    private static int uiThread;
    private static readonly PumpContext context = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try {
            if (args.Length != 1) throw new ArgumentException("Screenshot directory required.");
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            // Own isolated test startup, not production Program/PackageSource/Application.Run.
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            // Closing the last offscreen form can uninstall WinForms' automatic context.
            // Keep an explicit bounded test context so every continuation remains on this STA thread.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(context);
            System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = true;
            uiThread = Environment.CurrentManagedThreadId;
            foreach (int width in new[] { 480, 424 }) foreach (int percent in new[] { 100, 150, 200 }) Matrix(width, percent);
            InteractionFlows();
            RecoveryAndGlobalIssueFlows();
            File.WriteAllText(Path.Combine(output, "fixture-results.json"), JsonSerializer.Serialize(new {
                Result = "PASS_OFFSCREEN_FAKE_UI_ONLY", Captures = captures, Flows = flows,
                Scaling = "Simulated control/font scale 100/150/200%; not actual per-monitor DPI switching",
                NativeIntegrationLinked = false, RegistryOrServicesUsed = false, ProductionStartupUsed = false,
                MicrophoneCaptureUsed = false, RealShortcutCreated = false, RealApplicationLaunched = false,
                RealHotplugOrLifecycleVerified = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {captures.Count} offscreen screenshots and {flows.Count} fake UI flows. No production startup, native integration, registry, services, shortcuts, capture or real app launch.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Matrix(int width, int percent)
    {
        Ready(width, percent, "ready0", Preview(0, 0, 0));
        Ready(width, percent, "ready3", Preview(3, 0, 3));
        Ready(width, percent, "update-running-app", Preview(3, 0, 3) with { CloseRunningApplication = true });
        var impact = Preview(3, 1, 2) with { ReplacementCount = 2, PermissionCount = 3, ProtectedAudioChanges = true,
            AudioInterruption = true, DependentServices = ["音声補助サービス", "デバイス補助サービス"] };
        using (var fixture = New(width, percent, impact)) {
            Check(!Control<Button>(fixture.Form, "InstallButton").Enabled, "unapproved permission must disable primary");
            Capture(fixture.Form, "conditional-permission", width, percent);
            var approval = Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent");
            Reveal(fixture.Form, approval); approval.Checked = true;
            Check(Control<Button>(fixture.Form, "InstallButton").Enabled, "permission consent enables primary");
            Capture(fixture.Form, "conditional-approved", width, percent);
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3))) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick();
            Pump(() => State(fixture.Form) == MultiSetupState.Apply && fixture.Backend.ApplyCalls == 1);
            Check(!Control<Button>(fixture.Form, "InstallButton").Enabled, "apply primary is disabled");
            fixture.Form.Close(); Check(!fixture.Form.IsDisposed && Field<bool>(fixture.Form, "busy"), "closing is blocked during application");
            Capture(fixture.Form, "applying", width, percent);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker, CanOpenApplication: true));
            Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Capture(fixture.Form, "complete3", width, percent);
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3))) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick();
            Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(2, 1, 0, PrivateMarker, CanOpenApplication: true));
            Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            Capture(fixture.Form, "partial2-1", width, percent);
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3))) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(0, 0, 3, PrivateMarker, CanOpenApplication: true));
            Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            Check(Control<Button>(fixture.Form, "InstallButton").Text == "アプリを開く", "activation waiting offers app open, not an endless reinstallation loop");
            Capture(fixture.Form, "activation-pending3", width, percent);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Form.IsDisposed);
            Check(fixture.Backend.OpenCalls == 1 && fixture.Backend.ApplyCalls == 1, "pending activation opens app without repeating consent or applying again");
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3), MultiSetupBlock.RecoveryRequired)) {
            Check(Control<Button>(fixture.Form, "InstallButton").Enabled && Control<Button>(fixture.Form, "InstallButton").Text == "復旧を確認"
                && fixture.Backend.Requests.Count == 0, "recovery blocks install but offers fresh recovery preparation");
            Capture(fixture.Form, "recovery", width, percent);
            ExpandOptions(fixture.Form); Reveal(fixture.Form, Control<LinkLabel>(fixture.Form, "RecoveryDataButton"));
            Check(Control<LinkLabel>(fixture.Form, "RecoveryDataButton").Enabled, "recovery remains accessible");
            Capture(fixture.Form, "recovery-options", width, percent);
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3), prepareError: true)) {
            Check(State(fixture.Form) == MultiSetupState.Error && Control<Button>(fixture.Form, "InstallButton").Enabled, "prepare error offers fresh retry");
            Check(Field<string?>(fixture.Form, "error")?.Contains(PrivateMarker) == true, "prepare exception retained in details field");
            Capture(fixture.Form, "prepare-error", width, percent);
        }
        using (var fixture = New(width, percent, Preview(3, 0, 3))) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(2, 1, 0, PrivateMarker, CanOpenApplication: true, RecoveryRequired: true));
            Pump(() => State(fixture.Form) == MultiSetupState.Error);
            Check(Control<Button>(fixture.Form, "InstallButton").Enabled && Control<Button>(fixture.Form, "InstallButton").Text == "復旧を確認", "post-apply recovery offers a new plan, not blind retry");
            Capture(fixture.Form, "apply-recovery", width, percent);
        }
        Ready(width, percent, "repair", Preview(3, 2, 1), args: ["--repair"]);
        using (var fixture = New(width, percent, Preview(3, 3, 0), args: ["--remove"])) {
            Check(Control<Button>(fixture.Form, "InstallButton").Text == "同意して削除", "uninstall action");
            Check(!Control<Label>(fixture.Form, "ChangeInformation").Text.Contains("新しく接続"), "uninstall does not promise auto-install");
            Capture(fixture.Form, "uninstall-ready", width, percent);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Check(Control<Button>(fixture.Form, "InstallButton").Text == "閉じる", "uninstall closes, never launches app");
            Capture(fixture.Form, "uninstalled", width, percent);
        }
    }

    private static void InteractionFlows()
    {
        using (var fixture = New(480, 100, Preview(3, 0, 3))) {
            Check(fixture.Backend.InspectCalls == 1 && fixture.Backend.Requests.Count == 1 && fixture.Backend.ApplyCalls == 0, "first open prepares once without applying");
            Check(!Field<bool>(fixture.Form, "busy") && fixture.Form.AcceptButton == null, "no automatic Enter consent");
            Check(!Control<CheckBox>(fixture.Form, "DesktopShortcut").Visible && Control<CheckBox>(fixture.Form, "DesktopShortcut").Checked, "shortcut defaults on inside collapsed options");
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Form.IsDisposed);
            Check(fixture.Backend.OpenCalls == 1, "fake unelevated-open boundary called once");
            flows.Add("install-first-open-and-fake-open: explicit consent, then OpenApplication delegate only");
        }
        using (var fixture = New(480, 100, Preview(0, 0, 0))) {
            fixture.Backend.NextPreview = Preview(3, 0, 3);
            var task = (Task)typeof(SetupForm).GetMethod("PrepareAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Form, [true])!;
            Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(Control<Label>(fixture.Form, "MicrophoneCounts").Text.Contains("マイク 3") && fixture.Backend.ApplyCalls == 0, "fake inventory refresh does not auto-consent");
            flows.Add("simulated-hotplug-inventory-refresh: fresh preview only; no real endpoint event or manager tested");
        }
        using (var fixture = New(480, 100, Preview(2, 2, 0))) {
            Check(Control<Label>(fixture.Form, "MicrophoneCounts").Text.Contains("マイク 2") && fixture.Backend.ApplyCalls == 0, "fake unplug count does not break remaining two");
            flows.Add("simulated-removal-of-one-microphone: two remain in supplied inventory, no lifecycle claim");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3))) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(2, 1, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            fixture.Backend.NextPreview = Preview(3, 2, 1);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Last().RetryFailedOnly && fixture.Backend.ApplyCalls == 1, "retry freshly prepares only failed targets, never applies directly");
            Check(Field<MultiSetupPreview>(fixture.Form, "preview").AlreadyApplied == 2
                && Control<Label>(fixture.Form, "MicrophoneCounts").Text.Contains("1台に適用"), "retry preserves successful counts and presents only remaining work");
            flows.Add("one-failure-retry: applied two preserved, fresh failed-only request, explicit second consent");
        }
        using (var fixture = New(480, 100, Preview(3, 2, 1), args: ["--repair"])) {
            Check(fixture.Backend.Requests.Last().Operation == MultiSetupOperation.Repair, "repair operation dispatched");
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            flows.Add("repair: fresh repair plan and one explicit consent");
        }
        using (var fixture = New(480, 100, Preview(3, 3, 0), MultiSetupBlock.Payload, ["--remove"])) {
            Check(State(fixture.Form) == MultiSetupState.Ready, "unrelated payload issue does not block independently planned removal");
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Form.IsDisposed);
            Check(fixture.Backend.OpenCalls == 0, "uninstall does not open app");
            flows.Add("uninstall: payload-independent fake removal, restoration counts and close");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3), MultiSetupBlock.RecoveryRequired)) {
            ExpandOptions(fixture.Form); Reveal(fixture.Form, Control<LinkLabel>(fixture.Form, "RecoveryDataButton"));
            Check(fixture.Backend.ApplyCalls == 0 && Control<LinkLabel>(fixture.Form, "RecoveryDataButton").Enabled, "advanced recovery access without executing legacy/native recovery");
            flows.Add("recovery-required: install blocked, primary fresh-recovery action enabled; legacy entry separate and not executed");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3))) {
            fixture.Backend.ThrowOnOpen = true;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => !Field<bool>(fixture.Form, "busy"));
            Check(State(fixture.Form) == MultiSetupState.Complete && !fixture.Form.IsDisposed, "app-open failure does not relabel committed install");
            Check(!Control<Label>(fixture.Form, "ChangeInformation").Text.Contains(PrivateMarker), "open exception hidden from primary");
            Capture(fixture.Form, "app-open-error", 480, 100);
            flows.Add("fake-app-open-failure: committed state retained, technical exception details-only");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3), prepareError: true)) {
            fixture.Backend.ThrowOnPrepare = false;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Count == 2 && fixture.Backend.ApplyCalls == 0, "prepare failure retries fresh preview without consent write");
            flows.Add("fake-prepare-failure: short error and fresh retry, exception remains details-only");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3))) {
            ExpandOptions(fixture.Form);
            var destination = Control<TextBox>(fixture.Form, "ApplicationDirectory");
            destination.Text = @"C:\fake-changed-destination";
            Check(!Control<Button>(fixture.Form, "InstallButton").Enabled && State(fixture.Form) == MultiSetupState.Inspect, "editing destination invalidates consent plan immediately");
            typeof(System.Windows.Forms.Control).GetMethod("OnValidated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(destination, [EventArgs.Empty]);
            Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Last().Destination == destination.Text && fixture.Backend.ApplyCalls == 0, "validated destination gets a fresh preview only");
            Control<CheckBox>(fixture.Form, "DesktopShortcut").Checked = false; Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(!fixture.Backend.Requests.Last().DesktopShortcut && fixture.Backend.ApplyCalls == 0, "shortcut preference replans without writing a shortcut");
            flows.Add("options-edit: destination/shortcut changes invalidate and automatically reprepare without apply");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3) with { PermissionCount = 2 })) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick();
            Check(fixture.Backend.ApplyCalls == 0, "disabled primary cannot accept advanced permissions");
            Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent").Checked = true;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            Check(fixture.Backend.LastPermissionApproval, "separate permission checkbox approval delivered to backend");
            fixture.Backend.Release(new(2, 1, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            fixture.Backend.NextPreview = Preview(3, 2, 1) with { PermissionCount = 1 };
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(!Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent").Checked && !Control<Button>(fixture.Form, "InstallButton").Enabled, "fresh retry never reuses permission approval");
            flows.Add("advanced-permission: separate explicit approval reaches Apply, failed-only retry clears old consent");
        }
    }

    private static void RecoveryAndGlobalIssueFlows()
    {
        using (var fixture = New(424, 200, Preview(3, 0, 3) with { Block = MultiSetupBlock.RecoveryRequired })) {
            fixture.Backend.NextPreview = Preview(3, 2, 1) with { PermissionCount = 1 };
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            var consent = Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent"); Reveal(fixture.Form, consent); consent.Checked = true;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(0, 1, 0, PrivateMarker, OperationIssue: "中断した変更を戻しました。修復を確認してください。"));
            Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            Check(Control<Button>(fixture.Form, "InstallButton").Text == "修復を確認", "compensated recovery offers fresh repair, not an endless recovery loop");
            Capture(fixture.Form, "recovered-needs-repair", 424, 200);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Last().Operation == MultiSetupOperation.Repair && fixture.Backend.ApplyCalls == 1,
                "post-compensation repair is prepared without applying");
            Check(!Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent").Checked && !Control<Button>(fixture.Form, "InstallButton").Enabled,
                "repair after compensation obtains fresh permission consent");
            flows.Add("compensated-recovery: actionable fresh repair, no repeated recovery or reused consent");
        }
        // Recovery entry must work for inspection blocks, preview blocks and post-apply results.
        foreach (string origin in new[] { "inspection", "preview", "result" }) {
            using var fixture = New(424, 200, Preview(3, 0, 3) with { PermissionCount = 1,
                Block = origin == "preview" ? MultiSetupBlock.RecoveryRequired : MultiSetupBlock.None },
                origin == "inspection" ? MultiSetupBlock.RecoveryRequired : MultiSetupBlock.None);
            if (origin == "result") {
                Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent").Checked = true;
                Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
                fixture.Backend.Release(new(2, 1, 0, PrivateMarker, RecoveryRequired: true));
                Pump(() => State(fixture.Form) == MultiSetupState.Error);
            }
            var oldPlan = fixture.Backend.PreparedPlans.LastOrDefault();
            int calls = fixture.Backend.ApplyCalls;
            fixture.Backend.NextPreview = Preview(3, 1, 2) with { PermissionCount = 1, ProtectedAudioChanges = true,
                AudioInterruption = true, DependentServices = ["音声補助サービス"] };
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Last() is { Operation: MultiSetupOperation.Recover, RetryFailedOnly: false }, "recovery request delegates inventory scopes to primary");
            var prepared = Field<MultiSetupPreview>(fixture.Form, "preview");
            Check(!ReferenceEquals(oldPlan, prepared.Plan) && fixture.Backend.ApplyCalls == calls, "recovery prepares a fresh opaque plan without applying old work");
            Check(!Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent").Checked && !Control<Button>(fixture.Form, "InstallButton").Enabled, "recovery clears all earlier permission approval");
            Check(Control<ComboBox>(fixture.Form, "OperationList").SelectedIndex == (int)MultiSetupOperation.Recover
                && !Control<TextBox>(fixture.Form, "ApplicationDirectory").Enabled, "recovery mode cannot alter inventory scope via destination");
            Check(Control<Label>(fixture.Form, "ChangeInformation").Text.Contains("保護音声")
                && Control<Label>(fixture.Form, "ChangeInformation").Text.Contains("音声・通話"), "recovery retains conditional impact consent");
            if (origin == "result") Capture(fixture.Form, "recover-ready", 424, 200);
            var consent = Control<CheckBox>(fixture.Form, "AdvancedPermissionConsent"); Reveal(fixture.Form, consent); consent.Checked = true;
            Check(Control<Button>(fixture.Form, "InstallButton").Text == "同意して復旧", "recovery requires explicit primary consent");
            if (origin == "result") Capture(fixture.Form, "recover-approved", 424, 200);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == calls + 1);
            Check(ReferenceEquals(prepared.Plan, fixture.Backend.LastAppliedPlan) && fixture.Backend.LastPermissionApproval, "Apply receives only the new recovery plan and explicit approval");
            fixture.Backend.Release(new(3, 0, 0, PrivateMarker, CanOpenApplication: true)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Check(Control<Label>(fixture.Form, "SetupState").Text == "復旧しました" && Control<Button>(fixture.Form, "InstallButton").Text == "アプリを開く", "usable recovery result offers application open");
            if (origin == "result") Capture(fixture.Form, "recovered", 424, 200);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Form.IsDisposed);
            Check(fixture.Backend.OpenCalls == 1, "recovery open uses only provided fake delegate");
            flows.Add($"fresh-recovery-from-{origin}: new request/plan, consent reset, conditional impacts, explicit apply and fake open");
        }
        using (var fixture = New(480, 100, Preview(3, 0, 3), MultiSetupBlock.RecoveryRequired)) {
            fixture.Backend.ThrowOnPrepare = true;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => !Field<bool>(fixture.Form, "busy"));
            Check(State(fixture.Form) == MultiSetupState.Error && Control<Button>(fixture.Form, "InstallButton").Enabled, "failed recovery preparation is not a dead end");
            fixture.Backend.ThrowOnPrepare = false;
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Count == 2 && fixture.Backend.ApplyCalls == 0, "recovery retry makes another fresh plan only");
            flows.Add("recovery-prepare-error: recovery remains enabled and replans without applying");
        }
        using (var fixture = New(480, 100, Preview(0, 0, 0), MultiSetupBlock.RecoveryRequired)) {
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            fixture.Backend.Release(new(0, 0, 0, PrivateMarker)); Pump(() => State(fixture.Form) == MultiSetupState.Complete);
            Check(Control<Button>(fixture.Form, "InstallButton").Enabled && Control<Button>(fixture.Form, "InstallButton").Text == "閉じる", "recovery without a usable application still has a completion action");
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Form.IsDisposed);
            Check(fixture.Backend.OpenCalls == 0, "unusable recovery result never launches application");
            flows.Add("recovered-without-usable-application: close enabled, no open delegate call");
        }
        foreach (int count in new[] { 0, 3 }) {
            int width = count == 0 ? 424 : 480, percent = count == 0 ? 200 : 100;
            using var fixture = New(width, percent, Preview(count, 0, count));
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => fixture.Backend.ApplyCalls == 1);
            const string issue = "自動適用の常駐処理を開始できませんでした。";
            fixture.Backend.Release(new(count, 0, 0, PrivateMarker, CanOpenApplication: count > 0, OperationIssue: issue));
            Pump(() => State(fixture.Form) == MultiSetupState.Partial);
            Check(Control<Label>(fixture.Form, "MicrophoneCounts").Text == (count == 0 ? "" : $"マイク{count}台に適用しました。"), "operation failure leaves microphone counts factual");
            Check(Control<Label>(fixture.Form, "ChangeInformation").Text.Contains(issue)
                && !Control<Label>(fixture.Form, "SetupState").Text.Contains("マイク"), "short global issue displayed without false microphone-failure heading");
            Capture(fixture.Form, $"global-issue{count}", width, percent);
            fixture.Backend.NextPreview = Preview(count, count, 0);
            Control<Button>(fixture.Form, "InstallButton").PerformClick(); Pump(() => State(fixture.Form) == MultiSetupState.Ready);
            Check(fixture.Backend.Requests.Last().RetryFailedOnly && fixture.Backend.ApplyCalls == 1, "global failure freshly retries unresolved shared work without touching successful endpoints automatically");
            flows.Add($"global-operation-issue-{count}-microphones: failure zero, short issue, details raw, fresh shared-work retry");
        }
    }

    private static MultiSetupPreview Preview(int targets, int applied, int pending) => new(targets, applied, pending, 0, 0, false, false, [], PrivateMarker, new object(), true);
    private static void Ready(int width, int percent, string name, MultiSetupPreview preview, string[]? args = null)
    {
        using var fixture = New(width, percent, preview, args: args);
        Check(Control<Button>(fixture.Form, "InstallButton").Enabled, "ordinary preview permits consent"); Capture(fixture.Form, name, width, percent);
        if (name == "ready3") {
            ExpandOptions(fixture.Form); Reveal(fixture.Form, Control<LinkLabel>(fixture.Form, "RecoveryDataButton"));
            Capture(fixture.Form, "ready3-options", width, percent);
        }
    }
    private static Fixture New(int width, int percent, MultiSetupPreview preview, MultiSetupBlock block = MultiSetupBlock.None, string[]? args = null, bool prepareError = false)
    {
        var backend = new FakeBackend(preview, block);
        backend.ThrowOnPrepare = prepareError;
        var form = new SetupForm(args ?? [], backend.Actions) { StartPosition = FormStartPosition.Manual,
            Location = new(-32000, -32000), ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None };
        float scale = percent / 100f;
        form.Scale(new SizeF(scale, scale)); form.Font = new("Yu Gothic UI", 10 * scale);
        Control<Label>(form, "SetupState").Font = new(form.Font, FontStyle.Bold);
        form.ClientSize = new((int)(width * scale), (int)(400 * scale));
        _ = form.Handle; SetWindowLong(form.Handle, -20, GetWindowLong(form.Handle, -20) | 0x08000000);
        form.Show(); Pump(() => State(form) is MultiSetupState.Ready or MultiSetupState.Error);
        Check(!Walk(form).Any(c => c.Name == "MicrophoneList"), "normal form has no microphone selector");
        Check(!Walk(form).OfType<ComboBox>().Any(c => c.Visible), "no selector on default screen");
        if (block != MultiSetupBlock.RecoveryRequired && preview.Block != MultiSetupBlock.RecoveryRequired)
            Check(Control<ComboBox>(form, "OperationList").Items.Count == 3, "no extra recovery option on ordinary setup");
        Check(backend.AllOnWorkers && backend.InspectCalls == 1, "all heavy delegates execute on worker, once initially");
        return new(form, backend);
    }
    private static void Capture(SetupForm form, string name, int width, int percent)
    {
        form.PerformLayout(); form.Refresh(); Application.DoEvents();
        var primary = Control<Button>(form, "InstallButton"); var details = Control<LinkLabel>(form, "DetailsButton");
        foreach (var control in new Control[] { primary, details }) {
            var rectangle = Bounds(form, control);
            Check(control.Visible && form.ClientRectangle.Contains(rectangle), $"fixed action clipped: {name}/{width}/{percent}/{control.Name}/{rectangle}");
            Check(control.Width >= 20 && control.Height >= 15, "action has usable bounds");
            Check(control.AccessibleName?.Length > 0, "action has accessible name");
        }
        foreach (var nameOfHeader in new[] { "SetupState", "MicrophoneCounts" }) {
            var label = Control<Label>(form, nameOfHeader);
            if (label.Text.Length > 0) Check(form.ClientRectangle.Contains(Bounds(form, label)), "state/counts must stay fixed and visible: " + nameOfHeader);
        }
        Check(!Bounds(form, primary).IntersectsWith(Bounds(form, details)), "fixed actions do not overlap");
        var optionsLink = Control<LinkLabel>(form, "OptionsButton");
        if (optionsLink.Visible) Check(!Bounds(form, Control<Label>(form, "SetupState")).IntersectsWith(Bounds(form, optionsLink)), "state heading and options do not overlap");
        var scroll = Walk(form).OfType<Panel>().First(p => p.AutoScroll);
        Check(!scroll.HorizontalScroll.Visible, $"horizontal overflow: {name}/{width}/{percent}");
        if (Control<ComboBox>(form, "OperationList").Visible)
            Check(Bounds(form, Control<ComboBox>(form, "OperationList")).Bottom <= Bounds(form, Control<Label>(form, "DestinationLabel")).Top,
                $"operation overlaps destination label: {name}/{width}/{percent}; combo={Bounds(form, Control<ComboBox>(form, "OperationList"))}; minimum={Control<ComboBox>(form, "OperationList").MinimumSize}; margin={Control<ComboBox>(form, "OperationList").Margin}; rows={string.Join(',', ((TableLayoutPanel)Control<ComboBox>(form, "OperationList").Parent!).GetRowHeights())}; label={Bounds(form, Control<Label>(form, "DestinationLabel"))}");
        foreach (var control in Walk(form).Where(c => c.Visible && c is Label or CheckBox or LinkLabel or TextBox or ComboBox)) {
            var rectangle = Bounds(form, control);
            Check(rectangle.Left >= 0 && rectangle.Right <= form.ClientSize.Width, $"horizontal control clipping: {name}/{width}/{percent}/{control.Name}/{rectangle}");
        }
        Check(!Walk(form).Any(c => c.Visible && c.Text.Contains(PrivateMarker)), "technical diagnostics leaked into normal form");
        var text = MultiSetupPresentation.Details(Field<MultiSetupInspection?>(form, "inspection"), Field<MultiSetupPreview?>(form, "preview"),
            Field<MultiSetupResult?>(form, "result"), Field<string?>(form, "error"));
        Check(details.Enabled || State(form) == MultiSetupState.Apply, $"technical details available except while busy: {name}/{width}/{percent}, state={State(form)}, busy={Field<bool>(form, "busy")}, parent={details.Parent?.Enabled}, details={text.Length}, primary={primary.Text}/{primary.Enabled}, error={Field<string?>(form, "error")}");
        Check(text.Contains(PrivateMarker), "fixture diagnostics retained only in details");
        var order = TabOrder(form).ToArray();
        Check(order.Contains("InstallButton") || !primary.Enabled, "enabled primary in tab sequence");
        if (Control<CheckBox>(form, "AdvancedPermissionConsent").Visible && primary.Enabled)
            Check(Array.IndexOf(order, "AdvancedPermissionConsent") < Array.IndexOf(order, "InstallButton"), "permission precedes apply in keyboard traversal");
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new(0, 0, form.Width, form.Height));
        int ink = 0;
        for (int y = 35; y < image.Height - 20; y += 2) for (int x = 16; x < image.Width - 16; x += 2) {
            var pixel = image.GetPixel(x, y); if (Math.Abs(pixel.R - SystemColors.Control.R) > 40 || Math.Abs(pixel.G - SystemColors.Control.G) > 40) ++ink;
        }
        Check(ink > 100, "offscreen rendering is not blank");
        string filename = $"{name}-{width}-{percent}.png"; image.Save(Path.Combine(output, filename));
        captures.Add(new { File = filename, State = State(form).ToString(), WidthDip = width, SimulatedScalePercent = percent,
            Primary = primary.Text, PrimaryEnabled = primary.Enabled, Counts = Control<Label>(form, "MicrophoneCounts").Text,
            VerticalScroll = scroll.VerticalScroll.Visible, TabOrder = order });
    }
    private static void ExpandOptions(SetupForm form)
    {
        var link = Control<LinkLabel>(form, "OptionsButton");
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);
        form.PerformLayout(); Application.DoEvents();
    }
    private static void Reveal(SetupForm form, Control control)
    {
        Walk(form).OfType<Panel>().First(p => p.AutoScroll).ScrollControlIntoView(control);
        form.PerformLayout(); Application.DoEvents();
        Check(form.ClientRectangle.Contains(Bounds(form, control)), "scroll can reveal required control: " + control.Name);
    }
    private static Rectangle Bounds(Form form, Control control) => new(form.PointToClient(control.PointToScreen(Point.Empty)), control.Size);
    private static IEnumerable<Control> Walk(Control owner) { foreach (Control child in owner.Controls) { yield return child; foreach (var nested in Walk(child)) yield return nested; } }
    private static IEnumerable<string> TabOrder(SetupForm form)
    {
        Control? current = form;
        for (int i = 0; i < 100; ++i) {
            current = form.GetNextControl(current, true); if (current == null) yield break;
            if (current.TabStop && current.CanSelect && current.Visible && current.Enabled) yield return current.Name;
        }
    }
    private static T Control<T>(SetupForm form, string name) where T : Control => (T)Walk(form).Single(c => c.Name == name);
    private static T Field<T>(SetupForm form, string name) => (T)typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static MultiSetupState State(SetupForm form) => Field<MultiSetupState>(form, "state");
    private static void Pump(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Fake UI did not reach expected state."); context.Drain(); Application.DoEvents(); Thread.Sleep(5); }
        context.Drain(); Application.DoEvents();
    }
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => pending.Enqueue((callback, state));
        internal void Drain() {
            while (pending.TryDequeue(out var item)) { SetSynchronizationContext(this); item.Callback(item.State); }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Fixture(SetupForm Form, FakeBackend Backend) : IDisposable
    {
        public void Dispose() { Backend.Release(new(0, 0, 0, PrivateMarker)); Pump(() => !Field<bool>(Form, "busy")); Check(Backend.AllOnWorkers, "all heavy callbacks remain off UI through completion"); Form.Dispose(); }
    }
    private sealed class FakeBackend
    {
        private readonly MultiSetupBlock block;
        private TaskCompletionSource<MultiSetupResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MultiSetupPreview NextPreview;
        internal readonly ConcurrentQueue<MultiSetupRequest> Requests = new();
        internal readonly ConcurrentQueue<object> PreparedPlans = new();
        internal object? LastAppliedPlan;
        internal int InspectCalls, ApplyCalls, OpenCalls;
        internal bool AllOnWorkers = true, ThrowOnOpen, ThrowOnPrepare, LastPermissionApproval;
        internal MultiSetupActions Actions => new(
            () => { Worker(); Interlocked.Increment(ref InspectCalls); return Task.FromResult(new MultiSetupInspection(Details: PrivateMarker, Block: block)); },
            request => { Worker(); Requests.Enqueue(request); if (ThrowOnPrepare) throw new IOException(PrivateMarker); var plan = new object(); PreparedPlans.Enqueue(plan); return Task.FromResult(NextPreview with { Operation = request.Operation, Plan = plan }); },
            (plan, permissions) => { Worker(); LastAppliedPlan = plan; LastPermissionApproval = permissions; var task = completion.Task; Interlocked.Increment(ref ApplyCalls); return task; },
            () => { Worker(); Interlocked.Increment(ref OpenCalls); if (ThrowOnOpen) throw new IOException(PrivateMarker); return Task.CompletedTask; });
        internal FakeBackend(MultiSetupPreview preview, MultiSetupBlock block) { NextPreview = preview; this.block = block; }
        internal void Release(MultiSetupResult result) {
            var released = completion; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); released.TrySetResult(result);
        }
        private void Worker() { if (Environment.CurrentManagedThreadId == uiThread) AllOnWorkers = false; }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
}
