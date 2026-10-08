namespace DotMic.Setup;

internal sealed class SetupForm : Form
{
    private readonly MultiSetupActions actions;
    private readonly string[] arguments;
    private readonly Label title = new() { Name = "SetupState", AutoSize = true };
    private readonly Label counts = new() { Name = "MicrophoneCounts", AutoSize = true };
    private readonly Label information = new() { Name = "ChangeInformation", AutoSize = true };
    private readonly CheckBox permissions = new() { Name = "AdvancedPermissionConsent", Text = "必要な一時権限の追加と復元に同意する", AutoSize = true, Visible = false };
    private readonly LinkLabel optionsLink = new() { Name = "OptionsButton", Text = "オプション", AutoSize = true };
    private readonly TableLayoutPanel options = new() { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Visible = false };
    private readonly ComboBox operation = new() { Name = "OperationList", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox destination = new() { Name = "ApplicationDirectory", Text = InstallPaths.Default, Dock = DockStyle.Fill };
    private readonly Button browse = new() { Name = "BrowseDirectory", Text = "変更", AutoSize = true, CausesValidation = false };
    private readonly CheckBox desktopShortcut = new() { Name = "DesktopShortcut", Text = "デスクトップにショートカットを作成（全ユーザー）", Checked = true, AutoSize = true };
    private readonly LinkLabel recovery = new() { Name = "RecoveryDataButton", Text = "復旧データを開く", AutoSize = true };
    private readonly LinkLabel details = new() { Name = "DetailsButton", Text = "詳細", AutoSize = true, Enabled = false };
    private readonly Button apply = new() { Name = "InstallButton", Text = "確認中…", AutoSize = true, MinimumSize = new(120, 32), Enabled = false };
    private MultiSetupInspection? inspection;
    private MultiSetupPreview? preview;
    private MultiSetupResult? result;
    private MultiSetupState state = MultiSetupState.Inspect;
    private string? error;
    private string? userError;
    private bool busy, loading, shown, retryFailed;
    private TableLayoutPanel? headerLayout, bodyLayout, buttonLayout;
    private MultiSetupOperation Operation => (MultiSetupOperation)operation.SelectedIndex;
    private bool RecoveryRequired => result?.RecoveryRequired == true || preview?.Block == MultiSetupBlock.RecoveryRequired
        || (preview == null && inspection?.Block == MultiSetupBlock.RecoveryRequired);
    private bool CanOpen => Operation != MultiSetupOperation.Remove && result?.CanOpenApplication == true && !RecoveryRequired
        && (state == MultiSetupState.Complete || state == MultiSetupState.Partial && result.FailedCount == 0 && result.OperationIssue.Length == 0);

    internal SetupForm(string[] args, MultiSetupActions? actions = null)
    {
        this.actions = actions ?? FleetSetup.Actions((title, detail) => RecoveryDecision.Ask(this, title, detail)); arguments = args.ToArray();
        Text = "DOT MIC セットアップ"; Font = new("Yu Gothic UI", 10);
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(480, 220); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true; StartPosition = FormStartPosition.CenterScreen;
        title.Font = new(Font, FontStyle.Bold);
        operation.Items.AddRange(["導入", "修復", "削除"]);
        operation.SelectedIndex = args.Contains("--remove") ? 2 : args.Contains("--repair") ? 1 : 0;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 3 };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new(0) };
        headerLayout = header;
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        title.Margin = counts.Margin = new(0, 0, 0, 8); optionsLink.Margin = new(8, 0, 0, 8);
        optionsLink.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(title, 0, 0); header.Controls.Add(optionsLink, 1, 0);
        header.Controls.Add(counts, 0, 1); header.SetColumnSpan(counts, 2); shell.Controls.Add(header, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, TabStop = false };
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        bodyLayout = body;
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var control in new Control[] { information, permissions, options }) {
            control.Margin = new(0, 0, 0, 8); body.Controls.Add(control);
        }
        options.Controls.Add(new Label { Name = "OperationLabel", Text = "操作", AutoSize = true }); options.Controls.Add(operation);
        options.Controls.Add(new Label { Name = "DestinationLabel", Text = "保存先", AutoSize = true });
        var path = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        path.ColumnStyles.Add(new(SizeType.Percent, 100)); path.ColumnStyles.Add(new(SizeType.AutoSize));
        path.Controls.Add(destination, 0, 0); path.Controls.Add(browse, 1, 0);
        options.Controls.Add(path); options.Controls.Add(desktopShortcut); options.Controls.Add(recovery);
        foreach (Control control in options.Controls) {
            control.Margin = new(0, 0, 0, 8); options.RowStyles.Add(new(SizeType.AutoSize));
        }
        // ComboBox ignores MinimumSize.Height; its native height can exceed cached preferred size after font changes.
        void ReserveOperationHeight() {
            var row = options.RowStyles[1];
            row.SizeType = SizeType.Absolute;
            float height = Math.Max(operation.PreferredHeight, operation.Height) + operation.Margin.Vertical;
            if (row.Height != height) row.Height = height;
        }
        operation.FontChanged += (_, _) => ReserveOperationHeight();
        operation.HandleCreated += (_, _) => ReserveOperationHeight();
        operation.SizeChanged += (_, _) => ReserveOperationHeight();
        ReserveOperationHeight();
        scroll.Controls.Add(body); shell.Controls.Add(scroll, 0, 1);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new(0) };
        buttonLayout = buttons;
        buttons.ColumnStyles.Add(new(SizeType.Percent, 100)); buttons.ColumnStyles.Add(new(SizeType.AutoSize));
        details.Anchor = AnchorStyles.Left; buttons.Controls.Add(details, 0, 0); buttons.Controls.Add(apply, 1, 0);
        shell.Controls.Add(buttons, 0, 2); Controls.Add(shell);
        scroll.ClientSizeChanged += (_, _) => {
            int width = Math.Max(100, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
            title.MaximumSize = new(Math.Max(100, header.ClientSize.Width - optionsLink.PreferredSize.Width - optionsLink.Margin.Horizontal), 0);
            foreach (var label in new[] { counts, information }) label.MaximumSize = new(width, 0);
            permissions.MaximumSize = desktopShortcut.MaximumSize = new(width, 0);
        };
        foreach (var control in new Control[] { title, counts, information, permissions, optionsLink, operation, destination, browse, desktopShortcut, recovery, details, apply })
            control.AccessibleName = control.Text;
        destination.AccessibleName = "アプリ保存先"; operation.AccessibleName = "操作";
        recovery.AccessibleName = "旧形式の復旧データを開く";
        optionsLink.TabIndex = 0; permissions.TabIndex = 1; operation.TabIndex = 0;
        destination.TabIndex = 0; browse.TabIndex = 1; desktopShortcut.TabIndex = 3; recovery.TabIndex = 4;
        details.TabIndex = 0; apply.TabIndex = 1;
        optionsLink.CausesValidation = recovery.CausesValidation = details.CausesValidation = false;
        // Preparing never supplies a default Enter/Accept action or checks permission consent.
        optionsLink.LinkClicked += (_, _) => { options.Visible = !options.Visible; FitContent(); };
        details.LinkClicked += (_, _) => ShowDetails();
        permissions.CheckedChanged += (_, _) => Render();
        destination.TextChanged += (_, _) => { if (shown && !loading && !busy) InvalidatePreview(); };
        destination.Validated += async (_, _) => { if (shown && preview == null && !loading && !busy) await PrepareAsync(); };
        desktopShortcut.CheckedChanged += async (_, _) => { if (shown && !loading && !busy) { retryFailed = false; await PrepareAsync(); } };
        operation.SelectedIndexChanged += async (_, _) => { if (shown && !loading && !busy) { retryFailed = false; await PrepareAsync(); } };
        browse.Click += async (_, _) => {
            using var picker = new FolderBrowserDialog { Description = "DOT MIC専用の保存先を選択", UseDescriptionForTitle = true, InitialDirectory = destination.Text };
            if (picker.ShowDialog(this) == DialogResult.OK) { destination.Text = picker.SelectedPath; retryFailed = false; await PrepareAsync(); }
        };
        recovery.LinkClicked += async (_, _) => {
            if (busy) return;
            using (var form = new LegacyRecoveryForm(arguments)) form.ShowDialog(this);
            retryFailed = false; await PrepareAsync(true);
        };
        apply.Click += async (_, _) => {
            if (busy) return;
            if (CanOpen) await OpenAsync();
            else if (Operation == MultiSetupOperation.Recover && state == MultiSetupState.Partial && !RecoveryRequired) {
                loading = true; operation.SelectedIndex = (int)MultiSetupOperation.Repair; loading = false;
                await PrepareAsync(true);
            }
            else if (state == MultiSetupState.Complete) {
                if (Operation == MultiSetupOperation.Remove || result?.CanOpenApplication != true) Close(); else await OpenAsync();
            } else if (RecoveryRequired) await PrepareRecoveryAsync();
            else if (state == MultiSetupState.Ready && MultiSetupPresentation.CanAccept(preview, permissions.Checked)) await ApplyAsync();
            else await PrepareAsync(true);
        };
        FormClosing += (_, e) => e.Cancel = busy;
        Shown += async (_, _) => { shown = true; await PrepareAsync(true); };
        Render();
    }

    private void InvalidatePreview()
    {
        preview = null; result = null; retryFailed = false; state = MultiSetupState.Inspect; permissions.Checked = false;
        information.Text = "保存先の入力を完了すると、変更内容を再取得します。"; Render(false);
    }
    private async Task PrepareRecoveryAsync()
    {
        // Selecting Recover never applies a previously prepared install/repair/remove plan.
        loading = true;
        if (operation.Items.Count == 3) operation.Items.Add("復旧");
        operation.SelectedIndex = (int)MultiSetupOperation.Recover;
        loading = false; retryFailed = false;
        await PrepareAsync(true);
    }
    private async Task PrepareAsync(bool inspect = false)
    {
        if (busy) return;
        busy = true; loading = true; state = MultiSetupState.Inspect;
        preview = null; result = null; error = userError = null; permissions.Checked = false; Render();
        try {
            if (inspect || inspection == null) {
                inspection = await SetupWorker.Run(actions.Inspect);
                if (inspection.Destination != null) destination.Text = inspection.Destination;
                if (inspection.DesktopShortcut is bool shortcut) desktopShortcut.Checked = shortcut;
            }
            // Recovery scopes/requirements are independently validated by the backend, including when inspection is blocked.
            // Damaged unrelated install files must not prevent independently validated removal.
            if (Operation != MultiSetupOperation.Recover && inspection.Block != MultiSetupBlock.None
                && !(inspection.Block == MultiSetupBlock.Payload && Operation == MultiSetupOperation.Remove)) {
                state = MultiSetupState.Error; return;
            }
            var request = new MultiSetupRequest(Operation, arguments.ToArray(), destination.Text, desktopShortcut.Checked, RetryFailedOnly: retryFailed);
            preview = await SetupWorker.Run(() => actions.Prepare(request));
            if (preview.Operation != request.Operation) throw new InvalidOperationException("Preview operation does not match requested operation.");
            state = MultiSetupState.Ready;
        } catch (Exception failure) { error = failure.ToString(); userError = failure is MultiSetupException problem ? problem.UserMessage : "確認できませんでした。再試行するか、「詳細」を確認してください。"; state = MultiSetupState.Error; }
        finally { loading = false; busy = false; Render(); }
    }
    private async Task ApplyAsync()
    {
        var approved = preview;
        bool advanced = permissions.Checked;
        if (busy || !MultiSetupPresentation.CanAccept(approved, advanced)) return;
        busy = true; state = MultiSetupState.Apply; error = userError = null; Render();
        try {
            result = await SetupWorker.Run(() => actions.Apply(approved!.Plan!, advanced));
            retryFailed = MultiSetupPresentation.IsPartial(result);
            state = result.RecoveryRequired ? MultiSetupState.Error : retryFailed ? MultiSetupState.Partial : MultiSetupState.Complete;
            // Completed or failed plans are never reused for another Apply.
        } catch (Exception failure) { error = failure.ToString(); userError = failure is MultiSetupException problem ? problem.UserMessage : "処理を完了できませんでした。再試行するか、「詳細」を確認してください。"; retryFailed = true; state = MultiSetupState.Error; }
        finally { busy = false; permissions.Checked = false; Render(); }
    }
    private async Task OpenAsync()
    {
        busy = true; Render();
        bool opened = false;
        try { await SetupWorker.Run(actions.OpenApplication); opened = true; }
        catch (Exception failure) { error = failure.ToString(); information.Text = "アプリを開けませんでした。再試行するか、「詳細」を確認してください。"; }
        finally { busy = false; Render(false); }
        if (opened) Close();
    }
    private void Render(bool updateInformation = true)
    {
        title.Text = state == MultiSetupState.Partial && CanOpen || state == MultiSetupState.Complete && result?.AppliedCount == 0 && Operation is MultiSetupOperation.Install or MultiSetupOperation.Repair
            ? "設定を保存しました" : MultiSetupPresentation.Title(state, Operation, result?.OperationIssue.Length > 0); title.AccessibleName = title.Text;
        counts.Text = result != null ? MultiSetupPresentation.Counts(result, Operation) : preview != null ? MultiSetupPresentation.Counts(preview) : "";
        if (state == MultiSetupState.Complete && CanOpen && counts.Text.Length == 0) counts.Text = "マイクの接続待ち";
        counts.AccessibleName = counts.Text;
        if (updateInformation) information.Text = state switch {
            MultiSetupState.Ready => preview == null ? "" : MultiSetupPresentation.Summary(preview),
            MultiSetupState.Apply => "この画面を閉じずにお待ちください。",
            MultiSetupState.Complete => "",
            MultiSetupState.Partial when CanOpen => "マイクの使用開始後に反映を確認します。",
            MultiSetupState.Partial => result == null ? "" : MultiSetupPresentation.ResultSummary(result, Operation),
            MultiSetupState.Error => !RecoveryRequired && userError != null ? userError : MultiSetupPresentation.Next(RecoveryRequired ? MultiSetupBlock.RecoveryRequired : inspection?.Block ?? MultiSetupBlock.Unavailable),
            _ => ""
        };
        permissions.Visible = state == MultiSetupState.Ready && preview?.PermissionCount > 0;
        if (RecoveryRequired && operation.Items.Count == 3) operation.Items.Add("復旧");
        else if (!RecoveryRequired && Operation != MultiSetupOperation.Recover && operation.Items.Count > 3) operation.Items.RemoveAt(3);
        optionsLink.Visible = state != MultiSetupState.Complete;
        permissions.Enabled = !busy; optionsLink.Enabled = options.Enabled = !busy && state != MultiSetupState.Complete;
        destination.Enabled = browse.Enabled = desktopShortcut.Enabled = Operation is not (MultiSetupOperation.Remove or MultiSetupOperation.Recover);
        details.Enabled = !busy && MultiSetupPresentation.Details(inspection, preview, result, error).Length > 0;
        apply.Text = busy ? "処理中…" : CanOpen ? "アプリを開く" : Operation == MultiSetupOperation.Recover && state == MultiSetupState.Partial && !RecoveryRequired ? "修復を確認" : state == MultiSetupState.Complete ? "閉じる"
            : RecoveryRequired ? "復旧を確認"
            : state == MultiSetupState.Ready && preview?.CanApply == true ? MultiSetupPresentation.Action(Operation) : "再試行";
        apply.AccessibleName = apply.Text;
        apply.Enabled = !busy && (state == MultiSetupState.Complete || RecoveryRequired ? true
            : state == MultiSetupState.Ready && preview?.CanApply == true ? MultiSetupPresentation.CanAccept(preview, permissions.Checked)
            : state != MultiSetupState.Inspect);
        FitContent();
    }
    private void FitContent()
    {
        if (headerLayout == null || bodyLayout == null || buttonLayout == null) return;
        PerformLayout();
        float scale = Math.Max(DeviceDpi / 96f, Font.Size / 10f);
        int height = headerLayout.PreferredSize.Height + bodyLayout.PreferredSize.Height + buttonLayout.PreferredSize.Height + (int)Math.Ceiling(40 * scale);
        height = Math.Clamp(height, (int)Math.Ceiling(180 * scale), (int)Math.Ceiling(360 * scale));
        if (ClientSize.Height != height) ClientSize = new(ClientSize.Width, height);
    }
    private void ShowDetails()
    {
        using var view = new Form { Text = "変更の詳細", ClientSize = new(680, 460), StartPosition = FormStartPosition.CenterParent, Font = Font, AutoScaleMode = AutoScaleMode.Dpi };
        view.Controls.Add(new TextBox { Name = "SetupDetails", AccessibleName = "変更とエラーの詳細", Dock = DockStyle.Fill, Multiline = true,
            ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = MultiSetupPresentation.Details(inspection, preview, result, error) });
        view.ShowDialog(this);
    }
}
