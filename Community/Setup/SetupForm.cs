namespace DotMic.Setup;

internal sealed class SetupForm : Form
{
    private readonly ComboBox operation = new() { Name = "OperationList", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox microphones = new() { Name = "MicrophoneList", DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(EndpointIdentity.FriendlyName), Dock = DockStyle.Fill };
    private readonly TextBox information = new() { Name = "ChangeInformation", Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Control, TabStop = false };
    private readonly ComboBox protection = new() { Name = "ProtectedAudioChoice", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Visible = false };
    private readonly LinkLabel details = new() { Name = "DetailsButton", Text = "詳細", AutoSize = true, Enabled = false };
    private readonly Button apply = new() { Name = "InstallButton", Text = "変更内容を確認", AutoSize = true, Enabled = false };
    private readonly bool failVerify;
    private readonly string package = AppContext.BaseDirectory;
    private readonly List<PermissionChange> permissions = [];
    private Transaction? transaction;
    private bool busy, loading, ready, payloadReady, recoveryReady, awaitingConsent, finished;
    private string? payloadError, recoveryError;
    private string? pendingRecoveryPath;
    private bool Restoring => operation.SelectedIndex >= 2;
    private SetupOperation Operation => (SetupOperation)operation.SelectedIndex;

    internal SetupForm(string[] args)
    {
        failVerify = args.Contains("--verify-failure-once");
        Text = "DOT MIC セットアップ 0.4.0";
        Font = new("Yu Gothic UI", 10);
        ClientSize = new(480, 300); MinimumSize = new(440, 320); StartPosition = FormStartPosition.CenterScreen;
        operation.Items.AddRange(["導入", "修復", "削除", "復旧"]);
        operation.SelectedIndex = args.Contains("--repair") ? 1 : 0;
        protection.Items.AddRange(["保護音声の設定を導入前に戻す", "保護音声の設定を現在のまま維持"]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "操作", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(operation, 1, 0);
        layout.Controls.Add(new Label { Text = "マイク", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(microphones, 1, 1);
        information.Margin = new(0, 12, 0, 12);
        layout.Controls.Add(information, 0, 2); layout.SetColumnSpan(information, 2);
        layout.Controls.Add(protection, 0, 3); layout.SetColumnSpan(protection, 2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        actions.ColumnStyles.Add(new(SizeType.Percent, 100)); actions.ColumnStyles.Add(new(SizeType.AutoSize));
        details.Anchor = AnchorStyles.Left; actions.Controls.Add(details, 0, 0); actions.Controls.Add(apply, 1, 0);
        layout.Controls.Add(actions, 0, 4); layout.SetColumnSpan(actions, 2); Controls.Add(layout);
        operation.SelectedIndexChanged += (_, _) => Reset();
        microphones.SelectedIndexChanged += (_, _) => { if (!loading) Reset(); };
        protection.SelectedIndexChanged += (_, _) => UpdateAction();
        apply.Click += async (_, _) => { if (finished) { Close(); return; } await Guard(async () => { if (awaitingConsent) await Execute(); else await Prepare(); }); };
        details.LinkClicked += (_, _) => ShowDetails();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += (_, _) => {
            try {
                loading = true;
                try { Transaction.ValidatePayload(package); payloadReady = true; }
                catch (Exception e) { payloadError = e.Message; }
                if (payloadReady) recoveryReady = true;
                else {
                    try { ValidateRecoveryHelper(); recoveryReady = true; }
                    catch (Exception e) { recoveryError = e.Message; }
                }
                if (payloadReady) {
                    var devices = Integration.Endpoints(); microphones.Items.AddRange(devices.Cast<object>().ToArray());
                    var configured = RawRegistry.Value(Contract.ConfigPath, "StableId")?.Display;
                    if (configured != null) microphones.SelectedItem = devices.SingleOrDefault(e => e.StableId == configured);
                }
                ready = true;
            } catch (Exception e) { information.Text = "準備できませんでした。\r\n" + e.Message; }
            finally { loading = false; if (ready) Reset(); else UpdateAction(); }
        };
    }

    private void Reset()
    {
        if (busy) return;
        transaction = null; awaitingConsent = false; permissions.Clear(); lastError = Restoring ? recoveryError : !payloadReady ? payloadError : null;
        protection.Visible = false; protection.SelectedIndex = -1; details.Enabled = false;
        information.Text = Restoring ? "保存した設定を確認してから復元します。" : "マイクを選び、変更内容を確認してください。";
        if (!payloadReady && !Restoring) information.Text = "導入用ファイルを確認できません。配布ファイルをすべて展開し直してください。削除・復旧は選択できます。";
        if (!recoveryReady && Restoring) information.Text = "復旧用ファイルを確認できません。正規の配布ファイルをすべて展開し直してください。";
        UpdateAction();
    }

    private void UpdateAction()
    {
        operation.Enabled = ready && !busy;
        microphones.Enabled = ready && !busy && !Restoring;
        protection.Enabled = !busy;
        apply.Text = finished ? "閉じる" : busy ? "処理中…" : awaitingConsent ? SetupPresentation.Action(Operation) : "変更内容を確認";
        apply.Enabled = !busy && (finished || (ready && SetupPresentation.CanPrepare(Operation, payloadReady, microphones.SelectedItem is EndpointIdentity, recoveryReady)
            && (!awaitingConsent || !protection.Visible || protection.SelectedIndex >= 0)));
        details.Enabled = !busy && (transaction != null || lastError != null);
    }

    private async Task Prepare()
    {
        transaction = null; awaitingConsent = false; permissions.Clear(); protection.Visible = false; lastError = null;
        if (Restoring) {
            ValidateRecoveryHelper(); // Before the first native call, even if unrelated install files are damaged.
            string? path = Operation == SetupOperation.Remove ? RawRegistry.Value(Contract.ConfigPath, "OriginReceipt")?.Display : pendingRecoveryPath;
            if (Operation == SetupOperation.Remove && path == null) throw new IOException("導入済みの設定がありません。必要な場合は「復旧」を選択してください。");
            if (path == null) {
                using var dialog = new OpenFileDialog { InitialDirectory = Contract.RecoveryRoot, Filter = "復旧データ|snapshot.json", Title = "復旧データを選択" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            transaction = Transaction.Existing(path, false);
            if (!transaction.Receipt.AudioRestartPending) transaction.Receipt.AudioDependents = AudioService.Dependents();
            var target = EndpointIdentity.Resolve(transaction.Receipt.Target, Integration.Endpoints());
            loading = true;
            try {
                var item = microphones.Items.Cast<EndpointIdentity>().SingleOrDefault(e => e.EndpointId == target.EndpointId);
                if (item == null) { microphones.Items.Add(target); item = target; }
                microphones.SelectedItem = item;
            }
            finally { loading = false; }
            var edit = transaction.Receipt.Edits.FirstOrDefault(e => e.Path == Contract.AudioPath && e.Name == "DisableProtectedAudioDG");
            protection.Visible = !transaction.Receipt.ProtectedAudioWasAlreadyOne && edit != null;
            protection.SelectedIndex = -1;
        } else {
            if (microphones.SelectedItem is not EndpointIdentity target) throw new InvalidOperationException("マイクを選択してください。");
            string requestedOperation = Operation == SetupOperation.Repair ? "Repair" : "Install";
            transaction = await Task.Run(() => Transaction.Prepare(target, package, requestedOperation, false));
        }
        var current = EndpointIdentity.Resolve(transaction.Receipt.Target, Integration.Endpoints());
        foreach (var path in transaction.Receipt.Edits.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase)) {
            string mapped = path.StartsWith(transaction.Receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase)
                ? current.FxPath + path[transaction.Receipt.Target.FxPath.Length..] : path;
            var permission = PermissionFor(mapped);
            if (permission != null) permissions.Add(permission);
        }
        information.Text = SetupPresentation.Summary(transaction.Receipt, Operation, permissions, failVerify);
        if (protection.Visible) information.AppendText("\r\n現在の保護音声の設定：" + SetupPresentation.Value(RawRegistry.Value(Contract.AudioPath, "DisableProtectedAudioDG")));
        int height = TextRenderer.MeasureText(information.Text, Font, new Size(ClientSize.Width - 40, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        ClientSize = new(ClientSize.Width, Math.Clamp(height + 150 + (protection.Visible ? 36 : 0), 300, 600));
        awaitingConsent = true;
        // No default Enter action: preparing a plan must not also accept it.
    }

    private void ValidateRecoveryHelper()
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Payload>(File.ReadAllText(Path.Combine(package, "payload.json")), Contract.Json)
            ?? throw new IOException("配布ファイルの確認情報がありません。");
        if (manifest.Version != Contract.Version) throw new IOException("復旧用ファイルの版が一致しません。");
        var helper = manifest.Files.SingleOrDefault(f => f.Path.Equals("DotMic.Integration.dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("復旧用ファイルの確認情報がありません。");
        if (!Contract.FileHash(Transaction.SafePath(package, helper.Path)).Equals(helper.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("復旧用ファイルが配布時の内容と一致しません。");
    }

    private static PermissionChange? PermissionFor(string changedPath)
    {
        if (RawRegistry.CanWrite(changedPath)) return null;
        string ancestor = changedPath;
        KeyImage image;
        while (!(image = RawRegistry.KeyOnly(ancestor)).Exists) {
            int split = ancestor.LastIndexOf('\\'); if (split < 0) throw new IOException("権限の確認に必要な設定を取得できません。");
            ancestor = ancestor[..split];
        }
        var owner = new System.Security.AccessControl.RawSecurityDescriptor(image.Security, 0).Owner?.Value ?? "未設定";
        return new(ancestor, owner, changedPath, ancestor != changedPath);
    }

    private async Task Execute()
    {
        var tx = transaction ?? throw new InvalidOperationException("変更内容を確認してください。");
        if (!SetupPresentation.ServicesMatch(tx.Receipt.AudioDependents, AudioService.Dependents(), Restoring && tx.Receipt.AudioRestartPending))
            throw new IOException("停止するサービスが変わりました。変更内容を確認し直してください。");
        // Never silently broaden the single consent if permissions changed after preview.
        var current = EndpointIdentity.Resolve(tx.Receipt.Target, Integration.Endpoints());
        foreach (var edit in tx.Receipt.Edits) {
            string path = edit.Path.StartsWith(tx.Receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase) ? current.FxPath + edit.Path[tx.Receipt.Target.FxPath.Length..] : edit.Path;
            var actual = PermissionFor(path);
            if (actual != null && !permissions.Any(p => p.Path.Equals(actual.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.ValuePath, actual.ValuePath, StringComparison.OrdinalIgnoreCase) && p.Owner == actual.Owner && p.CreateChild == actual.CreateChild))
                throw new IOException("必要な権限が変わりました。変更内容を確認し直してください。");
        }
        tx.AdvancedPermissionApproved = permissions.Count > 0;
        tx.Receipt.AudioRestartConsent = true;
        tx.Receipt.DependentServiceConsent = true;
        tx.Receipt.ProtectedAudioConsent = true;
        tx.Receipt.ReplacementConsent = true;
        tx.Save();
        if (Restoring) {
            bool keep = protection.Visible && protection.SelectedIndex == 1;
            await tx.Rollback(true, (key, facts) => MessageBox.Show(this,
                "他の操作で設定が変更されています。導入前に戻すと他の設定を失う可能性があります。\r\n" + key + "\r\n" + facts + "\r\nはい：導入前に戻す／いいえ：現在の設定を残す",
                "設定の競合", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes, keep);
            information.Text = Operation == SetupOperation.Remove ? "削除しました。" : "復旧しました。";
        } else {
            await tx.Apply(failVerify);
            information.Text = Operation == SetupOperation.Repair ? "修復しました。" : "導入しました。";
            information.AppendText("\r\n展開先の「DOT MIC.exe」を開いてください。\r\n初期値：バイパス無効・音量補正 0 dB・ゲート無効・ノイズ除去無効");
        }
        awaitingConsent = false;
        ready = false; finished = true; // Reopen to create a fresh plan, never reuse a completed receipt.
    }

    private async Task Guard(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateAction();
        try { await action(); }
        catch (Exception e) {
            awaitingConsent = false;
            information.Text = transaction?.Receipt.Status == "RecoveryRequired"
                ? "復旧が必要です。「復旧」を選択してください。保存した復旧データは削除しないでください。"
                : "処理を完了できませんでした。変更内容を確認し直してください。";
            lastError = e.Message;
            if (transaction?.Receipt.Status == "RecoveryRequired") pendingRecoveryPath = transaction.ReceiptPath;
        }
        finally { busy = false; UpdateAction(); if (awaitingConsent) operation.Focus(); }
    }
    private string? lastError;
    private void ShowDetails()
    {
        if (transaction == null && lastError == null) return;
        using var view = new Form { Text = "変更の詳細", ClientSize = new(680, 460), StartPosition = FormStartPosition.CenterParent, Font = Font };
        view.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Text = transaction == null ? "エラーの詳細：" + lastError : SetupPresentation.Details(transaction.Receipt, transaction.ReceiptPath, lastError) });
        view.ShowDialog(this);
    }
}
