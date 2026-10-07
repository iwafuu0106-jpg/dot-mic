using System.Text;

namespace DotMic.Setup;

internal enum SetupOperation { Install, Repair, Remove, Recover }
internal sealed record PermissionChange(string Path, string Owner, string? ValuePath = null, bool CreateChild = false);

internal static class SetupPresentation
{
    internal static bool CanPrepare(SetupOperation operation, bool payloadReady, bool microphoneSelected, bool recoveryHelperReady) =>
        operation is SetupOperation.Remove or SetupOperation.Recover ? recoveryHelperReady : payloadReady && microphoneSelected;
    internal static bool ServicesMatch(IReadOnlyList<AudioDependent> expected, IReadOnlyList<AudioDependent> current, bool interruptedRecovery) =>
        (interruptedRecovery || current.Count == expected.Count) && current.All(c => expected.Any(e =>
            e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && (interruptedRecovery || e.OriginalState == c.OriginalState)));
    internal static string Action(SetupOperation operation) => operation switch {
        SetupOperation.Install => "同意して導入", SetupOperation.Repair => "同意して修復",
        SetupOperation.Remove => "同意して削除", _ => "同意して復旧"
    };
    internal static string Value(RawValue? value) => value == null ? "未設定" : value.Type is 1 or 2 or 7 or 4 ? value.Display : $"種類 {value.Type}、{value.Data.Length} バイト";
    internal static string Summary(Receipt receipt, SetupOperation operation, IReadOnlyList<PermissionChange> permissions, bool failVerify)
    {
        bool restoring = operation is SetupOperation.Remove or SetupOperation.Recover;
        var text = new StringBuilder();
        text.AppendLine(restoring ? "保存した導入前の設定に戻します。" : "選択したマイクに音量補正・ゲート・ノイズ除去・音量制限を適用します。");
        if (!restoring) {
            var mfx = receipt.Edits.FirstOrDefault(e => e.Path == receipt.Target.FxPath && e.Name == Contract.FxFormat + ",6");
            if (mfx?.Before != null && !mfx.Before.Same(mfx.After)) text.AppendLine("既存のマイク効果を置き換えます。削除時に元へ戻せます。");
            if (receipt.WindowsEffectsDisabled != null) text.AppendLine("音声拡張が無効な場合は動作しません。Windowsのマイク設定で有効にしてください。");
            text.AppendLine(receipt.ProtectedAudioWasAlreadyOne ? "保護音声の互換設定は変更しません（設定済み）。" : "Windows全体の保護音声の互換設定を変更します。");
            text.AppendLine("一部の著作権保護コンテンツや保護音声の再生に影響する可能性があります。Microsoft認証版ではありません。");
        } else {
            var edit = receipt.Edits.FirstOrDefault(e => e.Path == Contract.AudioPath && e.Name == "DisableProtectedAudioDG");
            if (!receipt.ProtectedAudioWasAlreadyOne && edit != null) text.AppendLine("保護音声の設定：導入前 " + Value(edit.Before) + "。下で復元か維持を選択してください。ほかのアプリへの影響は完全には検出できません。");
            if (receipt.PendingSecurityRestore.Count > 0) text.AppendLine("中断した権限変更を元に戻します：" + string.Join("、", receipt.PendingSecurityRestore));
        }
        text.AppendLine("音声・通話が一時的に切れます。パソコンは自動再起動しません。");
        if (receipt.AudioDependents.Count > 0) text.AppendLine("一時停止して元に戻すサービス：" + string.Join("、", receipt.AudioDependents.Select(s => s.DisplayName + "（" + s.Name + "）")));
        if (permissions.Count > 0) {
            text.AppendLine("次の設定だけに書き込み権限を一時追加し、直後に所有者・アクセス権を戻します。他の設定の権限は変更しません。");
            foreach (var permission in permissions.DistinctBy(p => (p.Path, p.Owner, p.CreateChild))) text.AppendLine(permission.Path + "\r\n所有者：" + permission.Owner + "／追加：" + (permission.CreateChild ? "設定の作成権限" : "値の書き込み権限"));
        }
        if (failVerify && !restoring) text.AppendLine("診断用：適用確認を一度失敗させ、導入前へ戻します。");
        return text.ToString().TrimEnd();
    }
    internal static string Details(Receipt receipt, string path, string? error)
    {
        var text = new StringBuilder();
        text.AppendLine("対象マイク：" + receipt.Target.FriendlyName);
        text.AppendLine("固定識別子：" + receipt.Target.StableId);
        text.AppendLine("機器識別子：" + receipt.Target.ContainerId);
        text.AppendLine("復旧データ：" + path);
        text.AppendLine("変更する設定：");
        foreach (var edit in receipt.Edits) {
            text.AppendLine(edit.Path + " / " + edit.Name);
            text.AppendLine("導入前：" + Value(edit.Before) + " → 適用後：" + Value(edit.After));
            if (edit.Before != null) text.AppendLine("導入前の種類：" + edit.Before.Type + "／元データ：" + Convert.ToHexString(edit.Before.Data));
            if (edit.After != null) text.AppendLine("適用後の種類：" + edit.After.Type + "／元データ：" + Convert.ToHexString(edit.After.Data));
        }
        if (error != null) text.AppendLine("エラーの詳細：" + error);
        return text.ToString();
    }
}
