using DotMic.Setup;

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
var plan = new object();
const string raw = "fixture-id / HKLM-fixture / owner-SID / snapshot.json / 0x80070005";
var preview = new MultiSetupPreview(3, 1, 2, 0, 0, false, false, [], raw, plan, true);
Check(MultiSetupPresentation.Counts(preview) == "マイク 3台中、2台に適用します。", "initial counts describe only planned work");
var ordinary = MultiSetupPresentation.Summary(preview);
Check(ordinary == MultiSetupPresentation.AutomaticApplicationConsent && ordinary.Contains("管理者権限の常駐処理"), "explicit resident automatic-application consent");
Check(!ordinary.Contains("強制終了") && MultiSetupPresentation.Summary(preview with { CloseRunningApplication = true }).Contains("未保存"), "automatic exit and force fallback disclosed only for a running application");
Check(!ordinary.Contains(raw) && !ordinary.Contains("保護音声") && !ordinary.Contains("置き換え") && !ordinary.Contains("一時的に切れ"), "only actual impacts on primary screen");
Check(MultiSetupPresentation.CanAccept(preview, false), "ordinary single consent");
Check(!MultiSetupPresentation.CanAccept(preview with { CanApply = false }, true), "backend blocked preview");
Check(!MultiSetupPresentation.CanAccept(preview with { Plan = null }, true), "no opaque plan, no writes");
Check(!MultiSetupPresentation.CanAccept(preview with { Block = MultiSetupBlock.RecoveryRequired }, true), "no consent bypass of recovery");
var advanced = preview with { ReplacementCount = 2, PermissionCount = 3, ProtectedAudioChanges = true, AudioInterruption = true, DependentServices = ["音声補助サービス"] };
var summary = MultiSetupPresentation.Summary(advanced);
Check(summary.Contains("Windows全体") && summary.Contains("著作権保護"), "short protected-audio impact remains mandatory");
Check(summary.Contains("マイク2台の既存") && summary.Contains("設定 3 件") && summary.Contains("直後に所有者・アクセス権を元へ"), "actual effect and temporary-permission impact counts");
Check(summary.Contains("音声・通話") && summary.Contains("自動再起動しません") && summary.Contains("音声補助サービス"), "actual interruption and human service name");
Check(!MultiSetupPresentation.CanAccept(advanced, false) && MultiSetupPresentation.CanAccept(advanced, true), "conditional advanced approval before write");
Check(!MultiSetupPresentation.Summary(preview with { DependentServices = ["not-stopped"] }).Contains("not-stopped"), "no stopped-service claim without interruption");
var removed = MultiSetupPresentation.Summary(advanced with { Operation = MultiSetupOperation.Remove });
Check(!removed.Contains(MultiSetupPresentation.AutomaticApplicationConsent) && removed.Contains("導入前に戻します"), "remove never promises installation");
var partial = new MultiSetupResult(2, 1, 0, "fixture per-endpoint error and snapshot", CanOpenApplication: true);
Check(MultiSetupPresentation.Counts(partial, MultiSetupOperation.Install) == "マイク2台に適用しました。" + Environment.NewLine + "1台は再試行できます。", "partial counts preserve successful endpoints");
Check(MultiSetupPresentation.Counts(partial, MultiSetupOperation.Remove).Contains("2台の設定を戻しました"), "remove count is restored, not installed");
var details = MultiSetupPresentation.Details(new(Details: "fixture startup error"), advanced, partial, "fixture HRESULT");
Check(details.Contains(raw) && details.Contains("fixture per-endpoint error") && details.Contains("fixture startup error") && details.Contains("fixture HRESULT"), "all technical data remains in details");
foreach (var state in Enum.GetValues<MultiSetupState>()) Check(!MultiSetupPresentation.Title(state, MultiSetupOperation.Install).Contains(raw), "state heading contains no technical diagnostics");
Check(MultiSetupPresentation.Summary(preview with { DeferredCount = 1 }).Contains("ほかのマイクは使用できます"), "pending does not mark other microphones unusable");
Check(MultiSetupPresentation.Next(MultiSetupBlock.RecoveryRequired).Contains("復旧を確認")
    && !MultiSetupPresentation.Next(MultiSetupBlock.RecoveryRequired).Contains("復旧データを開く"), "fleet recovery never points to legacy receipt picker");
Check(MultiSetupPresentation.Counts(preview with { TargetCount = 0, AlreadyApplied = 0, Pending = 0 }).Contains("接続されると"), "zero-device count is not a fabricated endpoint");
int writes = 0;
MultiSetupRequest? captured = null;
var actions = new MultiSetupActions(() => Task.FromResult(new MultiSetupInspection(Details: "fake inspection")),
    request => { captured = request; return Task.FromResult(preview with { Operation = request.Operation }); },
    (opaque, approved) => { Check(ReferenceEquals(opaque, plan), "opaque plan unchanged"); ++writes; return Task.FromResult(partial); },
    () => Task.CompletedTask);
await actions.Inspect();
var fresh = await actions.Prepare(new(MultiSetupOperation.Repair, [], "fake destination", true, RetryFailedOnly: true));
Check(writes == 0 && captured is { RetryFailedOnly: true, DesktopShortcut: true } && fresh.Operation == MultiSetupOperation.Repair, "fake inspection/prepare never imply apply consent; retry scope retained");
await actions.Apply(fresh.Plan!, false);
Check(writes == 1, "one explicit apply operation");
var globalIssue = new MultiSetupResult(0, 0, 0, raw, OperationIssue: "自動適用の常駐処理を開始できませんでした。");
Check(MultiSetupPresentation.IsPartial(globalIssue) && !MultiSetupPresentation.IsPartial(new(0, 0, 0, raw)), "shared failure alone is partial, zero microphones alone is success");
Check(MultiSetupPresentation.Counts(globalIssue, MultiSetupOperation.Install) == "", "shared failure never invents a microphone failure");
Check(MultiSetupPresentation.ResultSummary(globalIssue).Contains(globalIssue.OperationIssue)
    && !MultiSetupPresentation.ResultSummary(globalIssue).Contains(raw), "short operation issue only on primary screen");
Check(!MultiSetupPresentation.Title(MultiSetupState.Partial, MultiSetupOperation.Install, true).Contains("マイク"), "global failure heading is not endpoint failure");
Check(MultiSetupPresentation.Details(null, null, globalIssue, null).Contains(raw), "global technical failure remains in details");
var recoveryPreview = advanced with { Operation = MultiSetupOperation.Recover };
var recoverySummary = MultiSetupPresentation.Summary(recoveryPreview);
Check(MultiSetupPresentation.Action(MultiSetupOperation.Recover) == "同意して復旧"
    && MultiSetupPresentation.Title(MultiSetupState.Ready, MultiSetupOperation.Recover) == "復旧", "explicit recovery action and heading");
Check(recoverySummary.Contains("中断した変更") && recoverySummary.Contains("保護音声") && recoverySummary.Contains("音声・通話")
    && !recoverySummary.Contains("新しく接続") && !recoverySummary.Contains("置き換えます"), "recovery shows actual restore/interruption impacts, never automatic-install promise");
Check(!MultiSetupPresentation.CanAccept(recoveryPreview, false) && MultiSetupPresentation.CanAccept(recoveryPreview, true), "recovery permissions require separate approval");
Check(MultiSetupPresentation.Counts(recoveryPreview).Contains("2台の復旧を確認")
    && MultiSetupPresentation.Counts(partial, MultiSetupOperation.Recover).Contains("2台の設定を戻しました"), "recovery counts describe recovery work");
fresh = await actions.Prepare(new(MultiSetupOperation.Recover, [], "ignored-by-fleet-inventory", true));
Check(writes == 1 && captured is { Operation: MultiSetupOperation.Recover, RetryFailedOnly: false }
    && fresh.Operation == MultiSetupOperation.Recover, "recovery delegate prepares without applying old plan");
Console.WriteLine("PASS multi-microphone counts, shared failure separation, fresh recovery contract, conditional consent and details-only diagnostics. Pure presentation only; no forms, files, registry, service, installation or audio calls.");
