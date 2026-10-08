using System.Text;

namespace DotMic.Setup;

internal enum MultiSetupState { Inspect, Ready, Apply, Complete, Partial, Error }
internal static class MultiSetupPresentation
{
    internal const string AutomaticApplicationConsent = "管理者権限の常駐処理で、新しく接続したマイクにも自動適用します。";
    internal static string Title(MultiSetupState state, MultiSetupOperation operation, bool operationIssue = false) => state switch {
        MultiSetupState.Inspect => "マイクを確認中…",
        MultiSetupState.Apply => "処理中…",
        MultiSetupState.Complete => operation == MultiSetupOperation.Remove ? "削除しました" : operation == MultiSetupOperation.Recover ? "復旧しました" : "適用しました",
        MultiSetupState.Partial => operationIssue ? "処理を完了できませんでした" : "一部のマイクを処理できませんでした",
        MultiSetupState.Error => "処理を完了できませんでした",
        _ => operation switch { MultiSetupOperation.Repair => "修復", MultiSetupOperation.Remove => "削除", MultiSetupOperation.Recover => "復旧", _ => "導入" }
    };
    internal static string Counts(MultiSetupPreview preview) => preview.Operation switch {
        MultiSetupOperation.Remove => $"マイク {preview.TargetCount}台の設定を元に戻します。",
        MultiSetupOperation.Recover => preview.Pending == 0 ? "中断した音声設定を確認します。" : $"マイク {preview.Pending}台の復旧を確認します。",
        MultiSetupOperation.Repair => preview.Pending == 0 ? "音声設定を確認します。" : $"マイク {preview.TargetCount}台中、{preview.Pending}台を修復します。",
        _ => preview.TargetCount == 0 ? "マイクが接続されると適用します。" : preview.AlreadyApplied == 0 ? $"接続中のマイク {preview.TargetCount}台に適用します。"
            : preview.Pending == 0 ? $"マイク {preview.TargetCount}台に設定済みです。" : $"マイク {preview.TargetCount}台中、{preview.Pending}台に適用します。"
    };
    internal static string Counts(MultiSetupResult result, MultiSetupOperation operation)
    {
        var lines = new List<string>();
        if (result.AppliedCount > 0) lines.Add(operation is MultiSetupOperation.Remove or MultiSetupOperation.Recover
            ? $"マイク{result.AppliedCount}台の設定を戻しました。" : $"マイク{result.AppliedCount}台に適用しました。");
        if (result.FailedCount > 0) lines.Add($"{result.FailedCount}台は再試行できます。");
        if (result.PendingCount > 0) lines.Add(operation is MultiSetupOperation.Remove or MultiSetupOperation.Recover ? $"{result.PendingCount}台は復旧待ちです。" : $"{result.PendingCount}台は反映待ちです。");
        return string.Join(Environment.NewLine, lines);
    }
    internal static bool CanAccept(MultiSetupPreview? preview, bool advancedApproved) =>
        preview is { CanApply: true, Plan: not null, Block: MultiSetupBlock.None }
        && (preview.PermissionCount == 0 || advancedApproved);
    internal static string Action(MultiSetupOperation operation) => operation switch {
        MultiSetupOperation.Repair => "同意して修復", MultiSetupOperation.Remove => "同意して削除", MultiSetupOperation.Recover => "同意して復旧", _ => "同意して導入"
    };
    internal static string Summary(MultiSetupPreview preview)
    {
        var lines = new List<string>();
        if (preview.CloseRunningApplication) lines.Add("起動中のDOT MICを終了して更新します。応答しない場合は強制終了し、未保存の変更が失われる場合があります。");
        if (preview.Operation == MultiSetupOperation.Remove) lines.Add("保存した導入前のマイク設定に戻し、常駐処理を削除します。");
        else if (preview.Operation == MultiSetupOperation.Recover) lines.Add("保存した復旧情報で、中断した変更を元に戻します。");
        else lines.Add(AutomaticApplicationConsent);
        if (preview.ProtectedAudioChanges) lines.Add(preview.Operation is MultiSetupOperation.Remove or MultiSetupOperation.Recover
            ? "Windows全体の保護音声設定を導入前に戻します。他のアプリへの影響は完全には検出できません。"
             : "Windows全体の保護音声設定を変更します。一部の著作権保護コンテンツの再生に影響する可能性があります。");
        if (preview.ReplacementCount > 0 && preview.Operation is not (MultiSetupOperation.Remove or MultiSetupOperation.Recover)) lines.Add($"マイク{preview.ReplacementCount}台の既存の音声効果を置き換えます。元の設定は保存します。");
        if (preview.PermissionCount > 0) lines.Add($"設定 {preview.PermissionCount} 件に必要な権限を一時追加し、書き込み直後に所有者・アクセス権を元へ戻します。");
        if (preview.AudioInterruption) {
            lines.Add("音声・通話が一時的に切れます。パソコンは自動再起動しません。");
            if (preview.DependentServices.Count > 0) lines.Add("一時停止して元に戻すサービス：" + string.Join("、", preview.DependentServices));
        }
        if (preview.DeferredCount > 0) lines.Add($"確認待ち {preview.DeferredCount}。ほかのマイクは使用できます。");
        if (preview.Block != MultiSetupBlock.None) lines.Add(Next(preview.Block));
        return string.Join(Environment.NewLine, lines);
    }
    internal static string Next(MultiSetupBlock block) => block switch {
        MultiSetupBlock.Payload => "配布ファイルをすべて展開し直して、再試行してください。",
        MultiSetupBlock.RecoveryRequired => "復旧が必要です。「復旧を確認」で中断した変更を確認してください。",
        _ => "接続を確認して再試行してください。"
    };
    internal static bool IsPartial(MultiSetupResult result) => result.FailedCount > 0 || result.PendingCount > 0 || result.OperationIssue.Length > 0;
    internal static string ResultSummary(MultiSetupResult result, MultiSetupOperation operation = MultiSetupOperation.Install)
    {
        if (result.OperationIssue.Length > 0) return result.OperationIssue;
        if (operation is MultiSetupOperation.Remove or MultiSetupOperation.Recover) return "失敗・保留分だけ再試行します。";
        return result.AppliedCount > 0 ? "ほかのマイクは使用できます。失敗・保留分だけ再試行します。" : "失敗・保留分だけ再試行します。";
    }
    internal static string Details(MultiSetupInspection? inspection, MultiSetupPreview? preview, MultiSetupResult? result, string? error)
    {
        var text = new StringBuilder();
        if (result != null) { if (result.OperationIssue.Length > 0) text.AppendLine(result.OperationIssue); text.AppendLine(result.Details); }
        if (error != null) text.AppendLine("エラーの詳細：" + error);
        if (preview != null) text.AppendLine(preview.Details);
        if (inspection != null) text.AppendLine(inspection.Details);
        return text.ToString().TrimEnd();
    }
}
