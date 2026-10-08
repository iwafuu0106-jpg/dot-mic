namespace DotMic.Setup;

internal static class RecoveryDecision
{
    internal static bool Ask(Control owner, string title, string detail) => SetupWorker.Decide(owner, () => {
        using var dialog = new Form { Text = "変更された設定", ClientSize = new(470, 150), StartPosition = FormStartPosition.CenterParent,
            Font = owner.Font, AutoScaleMode = AutoScaleMode.Dpi, MaximizeBox = false, MinimizeBox = false };
        var text = new Label { AutoSize = true, MaximumSize = new(430, 0), Text = "保存後に設定が変更されています。\n元に戻すと、後から行った変更が取り消されます。", Location = new(16, 16) };
        var details = new LinkLabel { Text = "詳細を表示", AutoSize = true, Location = new(16, 78), TabIndex = 0 };
        var restore = new Button { Text = "元に戻す", DialogResult = DialogResult.Yes, Location = new(226, 108), Size = new(108, 30), TabIndex = 1 };
        var cancel = new Button { Text = "変更せず中止", DialogResult = DialogResult.Cancel, Location = new(342, 108), Size = new(112, 30), TabIndex = 2 };
        details.LinkClicked += (_, _) => {
            using var view = new Form { Text = "復旧の詳細", ClientSize = new(620, 400), Font = owner.Font, StartPosition = FormStartPosition.CenterParent };
            view.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = title + Environment.NewLine + detail });
            view.ShowDialog(dialog);
        };
        dialog.Controls.AddRange([text, details, restore, cancel]); dialog.AcceptButton = cancel; dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    });
}
