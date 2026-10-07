using System.Text.Json;

namespace DotMic.Setup;

internal sealed class SetupForm : Form
{
    private readonly ComboBox microphones = new() { Name = "MicrophoneList", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox information = new() { Name = "ChangeInformation", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly CheckBox effects = new() { Name = "ReplacementConsent", Text = "表示されたMFX置換・登録変更と復元方法を理解して続行する", AutoSize = true };
    private readonly CheckBox protectedAudio = new() { Name = "ProtectedAudioConsent", Text = "Windows全体のProtected Audio / DRMへの影響を理解した", AutoSize = true };
    private readonly CheckBox interruption = new() { Name = "AudioRestartConsent", Text = "音声再生・通話の一時切断に同意する（PC自動再起動なし）", AutoSize = true };
    private readonly CheckBox permission = new() { Name = "AdvancedConsent", Text = "必要な対象keyだけの一時permission変更と直後の復元を許可する", AutoSize = true };
    private readonly CheckBox dependents = new() { Name = "DependentServiceConsent", Text = "表示された依存サービスの一時停止・元状態への復元に同意する", AutoSize = true, Visible = false };
    private readonly Button preview = new() { Name = "PreviewButton", Text = "変更内容を確認", AutoSize = true };
    private readonly Button install = new() { Name = "InstallButton", Text = "Install", AutoSize = true, Enabled = false };
    private readonly Button uninstall = new() { Name = "UninstallButton", Text = "Uninstall", AutoSize = true };
    private readonly Button recovery = new() { Name = "RecoveryButton", Text = "Recovery", AutoSize = true };
    private readonly Button details = new() { Name = "DetailsButton", Text = "詳細", AutoSize = true };
    private readonly bool repair, failVerify;
    private bool busy;
    private Transaction? transaction;
    private readonly string package = AppContext.BaseDirectory;
    internal SetupForm(string[] args)
    {
        repair = args.Contains("--repair"); failVerify = args.Contains("--verify-failure-once");
        Text = "DOT MIC Community Setup 0.4.0"; Width = 710; Height = 640; MinimumSize = new(640, 540); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 10); install.Text = repair ? "Repair" : "Install";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 9 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "物理マイクへlegacy Windows Audio APO integrationを適用します。\n通常UIは管理者不要。WHQL製品ではありません。変更前snapshotから復元できます。", AutoSize = true }, 0, 0);
        layout.Controls.Add(microphones, 0, 1); layout.Controls.Add(information, 0, 2);
        layout.Controls.Add(effects, 0, 3); layout.Controls.Add(protectedAudio, 0, 4); layout.Controls.Add(interruption, 0, 5); layout.Controls.Add(permission, 0, 6); layout.Controls.Add(dependents, 0, 7);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; actions.Controls.AddRange([preview, install, uninstall, recovery, details]); layout.Controls.Add(actions, 0, 8); Controls.Add(layout);
        if (failVerify) information.Text = "診断: このtransactionのVerifyだけを人工的に1回失敗させ、導入前rollbackを確認します。DSP faultは発生させません。";
        preview.Click += async (_, _) => await Guard(async () => { if (microphones.SelectedItem is not EndpointIdentity endpoint) throw new InvalidOperationException("対象captureマイクを選択してください。名前だけでは識別しません。");
            transaction = null;
            effects.Checked = protectedAudio.Checked = interruption.Checked = permission.Checked = dependents.Checked = false;
            transaction = await Task.Run(() => Transaction.Prepare(endpoint, package, repair ? "Repair" : "Install", permission.Checked));
            dependents.Visible = transaction.Receipt.AudioDependents.Count > 0;
            information.Text = transaction.Description(); if (failVerify) information.AppendText("\r\nこの診断transactionではVerifyを1回だけ失敗させます。"); install.Enabled = true; });
        install.Click += async (_, _) => await Guard(async () => {
            if (transaction == null) throw new InvalidOperationException("先に変更内容を確認してください。");
            bool needsPermission = transaction.Receipt.Edits.Any(e => !RawRegistry.CanWrite(e.Path));
            var decision = ConsentPolicy.Decide(true, effects.Checked, protectedAudio.Checked, interruption.Checked, needsPermission, permission.Checked);
            if (decision != Decision.Continue) { information.AppendText("\r\n必要な変更への同意がないためキャンセルしました。Audio registryは変更していません。"); return; }
            transaction.AdvancedPermissionApproved = permission.Checked;
            transaction.Receipt.DependentServiceConsent = dependents.Checked;
            transaction.Receipt.ReplacementConsent = true; transaction.Receipt.ProtectedAudioConsent = true; transaction.Receipt.AudioRestartConsent = true; transaction.Save();
            await transaction.Apply(failVerify); information.Text = "COMMIT: 固定配置APOのaudiodg実ロードとAPOProcessを確認しました。\r\n初期値はBypass ON / Gain0 / Gate OFF / NC OFFです。\r\n" + transaction.ReceiptPath; install.Enabled = false;
        });
        uninstall.Click += async (_, _) => await Restore(false);
        recovery.Click += async (_, _) => await Restore(true);
        details.Click += (_, _) => { if (transaction == null) { MessageBox.Show(this, "対象マイクを選び「変更内容を確認」を押してください。"); return; } var view = new Form { Text = "変更・snapshot詳細", Width = 900, Height = 620 }; view.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = JsonSerializer.Serialize(transaction.Receipt, Contract.Json) }); view.ShowDialog(this); };
        microphones.SelectedIndexChanged += (_, _) => { transaction = null; install.Enabled = false; };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += (_, _) => {
            try { Transaction.ValidatePayload(package); var devices = Integration.Endpoints(); microphones.Items.AddRange(devices.Cast<object>().ToArray());
                var configured = RawRegistry.Value(Contract.ConfigPath, "StableId")?.Display; if (configured != null) microphones.SelectedItem = devices.SingleOrDefault(e => e.StableId == configured);
                if (!failVerify) information.Text = "対象マイクを選択し、実際の変更内容を確認してください。既存効果や特殊permissionは説明と同意により続行できます。snapshot/対象/read-backが成立しない場合だけtransactionを停止します。\r\nSmartScreen警告が出る場合があります。自己署名Root certificateの追加は行いません。";
            } catch (Exception e) { information.Text = "Transaction準備に必要な情報を取得できません: " + e.Message; preview.Enabled = false; }
        };
    }
    private async Task Guard(Func<Task> action)
    {
        if (busy) return; busy = true; var priorInstall = install.Enabled; microphones.Enabled = false; preview.Enabled = install.Enabled = uninstall.Enabled = recovery.Enabled = false;
        try { await action(); }
        catch (Exception e) { information.Text = e.Message + "\r\n" + transaction?.ReceiptPath; if (transaction?.Receipt.Status == "RecoveryRequired") MessageBox.Show(this, information.Text, "Recovery Required", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; microphones.Enabled = true; preview.Enabled = uninstall.Enabled = recovery.Enabled = true; install.Enabled = transaction?.Receipt.Status == "Prepared" && (priorInstall || transaction != null); }
    }
    private async Task Restore(bool choose)
    {
        await Guard(async () => {
            if (!interruption.Checked) { information.Text = "Audio service再初期化で現在の音声再生・通話が一時的に切断されます。同意欄を確認してください。"; return; }
            string? path = choose ? null : RawRegistry.Value(Contract.ConfigPath, "OriginReceipt")?.Display;
            if (path == null) { using var dialog = new OpenFileDialog { InitialDirectory = Contract.RecoveryRoot, Filter = "Recovery snapshot|snapshot.json", Title = "管理者管理のRecovery snapshotを選択" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.FileName; }
            transaction = Transaction.Existing(path, permission.Checked);
            if (!transaction.Receipt.AudioRestartPending) transaction.Receipt.AudioDependents = AudioService.Dependents();
            dependents.Visible = transaction.Receipt.AudioDependents.Count > 0;
            if (transaction.Receipt.AudioDependents.Count > 0 && !dependents.Checked) { information.Text = "復元に必要な一時停止・元状態への復元の対象:\r\n" + string.Join("\r\n", transaction.Receipt.AudioDependents.Select(s => s.DisplayName + " (" + s.Name + ")")) + "\r\n依存サービスへの同意欄を確認してから再実行してください。"; return; }
            transaction.Receipt.DependentServiceConsent = dependents.Checked;
            var target = EndpointIdentity.Resolve(transaction.Receipt.Target, Integration.Endpoints());
            if (MessageBox.Show(this, target.FriendlyName + "\r\n導入前値を復元します。競合は個別に現在値と導入前値を表示します。音声・通話が一時的に切断されます。", choose ? "Recovery" : "Uninstall", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            bool keep = false;
            var protectionEdit = transaction.Receipt.Edits.FirstOrDefault(e => e.Path == Contract.AudioPath && e.Name == "DisableProtectedAudioDG");
            if (!transaction.Receipt.ProtectedAudioWasAlreadyOne && protectionEdit != null) { var prior = protectionEdit.Before; keep = MessageBox.Show(this, $"DisableProtectedAudioDGの導入前: {prior?.Display ?? "未存在"}\r\n導入前のWindows設定へ戻しますか？\r\n「いいえ」は現在設定を維持します。他アプリへの影響は完全検出できません。", "Protected Audio設定の復元", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.No; }
            transaction.Receipt.AudioRestartConsent = true; transaction.Save();
            await transaction.Rollback(true, (key, facts) => MessageBox.Show(this, "この値はDOT MIC導入後に変更されています。導入前へ復元すると他ソフト設定を失う可能性があります。\r\n" + key + "\r\n" + facts + "\r\n「はい」: 導入前へ復元 / 「いいえ」: 現在値を維持", "復元時の競合", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes, keep);
            information.Text = "復元・物理マイクcapture確認完了。\r\n" + transaction.ReceiptPath + "\r\nsnapshotとportable SetupはRecovery用に保持されます。";
        });
    }
}
