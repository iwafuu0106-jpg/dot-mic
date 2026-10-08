using DotMic.Setup;

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}
var target = new EndpointIdentity("runtime", "stable", "container", "テスト用マイク", "physical", "capture");
var receipt = new Receipt { Target = target };
receipt.Edits.Add(new() { Path = target.FxPath, Name = Contract.FxFormat + ",6", Before = RawValue.Text("効果", "他の効果"), After = RawValue.Text("効果", Contract.Clsid) });
string install = SetupPresentation.Summary(receipt, SetupOperation.Install, [], false);
Check(install.Contains("既存のマイク効果を置き換え"), "既存効果の置換を表示する");
Check(install.Contains("Windows全体") && install.Contains("著作権保護") && install.Contains("一時的に切れ"), "一括同意の安全説明を省略しない");
Check(!receipt.ReplacementConsent && !receipt.AudioRestartConsent && !receipt.ProtectedAudioConsent && !receipt.DependentServiceConsent, "表示だけでは同意しない");
receipt.ProtectedAudioWasAlreadyOne = true;
string existing = SetupPresentation.Summary(receipt, SetupOperation.Install, [], false);
Check(existing.Contains("変更しません（設定済み）"), "導入前から有効な互換設定を区別する");
receipt.AudioDependents.Add(new("service-id", "テスト用サービス", 4));
string advanced = SetupPresentation.Summary(receipt, SetupOperation.Repair, [new("対象設定", "所有者識別子")], false);
Check(advanced.Contains("テスト用サービス") && advanced.Contains("service-id"), "停止するサービスを明示する");
Check(advanced.Contains("対象設定") && advanced.Contains("所有者識別子") && advanced.Contains("直後"), "権限変更の対象と復元を明示する");
receipt.ProtectedAudioWasAlreadyOne = false;
receipt.Edits.Add(new() { Path = Contract.AudioPath, Name = "DisableProtectedAudioDG", Before = RawValue.Dword("設定", 0), After = RawValue.Dword("設定", 1) });
string restore = SetupPresentation.Summary(receipt, SetupOperation.Remove, [], false);
Check(restore.Contains("復元か維持を選択") && restore.Contains("導入前 0"), "削除時の保護音声設定の選択を表示する");
Check(!restore.Contains("マイクに音量補正"), "復元を導入と表示しない");
foreach (var operation in Enum.GetValues<SetupOperation>()) Check(SetupPresentation.Action(operation).StartsWith("同意して"), "同意操作を明示する");
Check(SetupPresentation.Details(receipt, "復旧データ", null).Contains("導入前の種類") && SetupPresentation.Details(receipt, "復旧データ", null).Contains("元データ"), "正確な値は詳細に残す");
Check(!SetupPresentation.CanPrepare(SetupOperation.Install, false, true, true), "配布ファイル不良なら導入しない");
Check(SetupPresentation.CanPrepare(SetupOperation.Remove, false, false, true) && SetupPresentation.CanPrepare(SetupOperation.Recover, false, false, true), "無関係な配布ファイル不良でも削除・復旧を利用できる");
Check(!SetupPresentation.CanPrepare(SetupOperation.Recover, false, false, false) && !SetupPresentation.CanPrepare(SetupOperation.Remove, false, false, false), "未検証の復旧用コードを実行しない");
Check(SetupPresentation.ServicesMatch(receipt.AudioDependents, [], true), "中断時に停止したサービスを復旧できる");
Check(!SetupPresentation.ServicesMatch(receipt.AudioDependents, [], false), "通常操作でサービス変化を見逃さない");
Check(!SetupPresentation.ServicesMatch(receipt.AudioDependents, [new("new-service", "未同意のサービス", 4)], true), "中断した復旧でも新しいサービスを停止しない");
string parent = SetupPresentation.Summary(receipt, SetupOperation.Repair, [new("実際の親設定", "所有者", "未作成の子設定", true)], false);
Check(parent.Contains("実際の親設定") && parent.Contains("設定の作成権限"), "子設定作成時に実際の権限変更先を表示する");
Check(SetupDefaults.Controls.Single(p => p.Property == 1).Value == 0, "導入時のバイパスは無効");
Check(SetupDefaults.Controls.All(p => p.Value == 0) && SetupDefaults.Controls.Length == 4, "音量補正ゼロ・ゲート無効・ノイズ除去無効を維持");
Console.WriteLine("PASS: Setup表示の確認のみ。レジストリ・音声・サービス操作なし。");
Check(InstallPaths.Default.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), "既定のアプリ保存先はProgram Files配下");
Check(InstallPaths.Normalize(InstallPaths.Default + "\\") == InstallPaths.Default, "保存先の末尾区切りを正規化");
void RejectPath(string path) { try { InstallPaths.Normalize(path); throw new Exception("危険な保存先を拒否しませんでした"); } catch (IOException) { } }
foreach (var path in new[] { "relative", @"\\server\share\DOT MIC", Path.GetPathRoot(InstallPaths.Default)!, Contract.InstallRoot,
    Environment.GetFolderPath(Environment.SpecialFolder.Windows), Contract.RecoveryRoot }) RejectPath(path);
void RejectFile(string path) {
    try { InstallPaths.ValidateDeployment(new() { Root = InstallPaths.Default, Files = [new() { Relative = path }] }); throw new Exception("危険な配置ファイルを拒否しませんでした"); }
    catch (IOException) { }
}
foreach (var path in new[] { "APO/DotMic.ApoGate.dll", "内部ファイル/UI/../foreign.exe", "内部ファイル/UI/x:stream", "../DOT MIC.exe", "C:/foreign.exe" }) RejectFile(path);
InstallPaths.ValidateDeployment(new() { Root = InstallPaths.Default, Files = [new() { Relative = "DOT MIC.exe" }, new() { Relative = "内部ファイル/UI/DotMic.App.exe" }] });
Check(System.Text.Json.JsonSerializer.Deserialize<Receipt>("{\"Schema\":1,\"Version\":\"0.4.0-community\"}")!.Application == null, "旧復旧データではアプリの配置・削除を追加しない");
Console.WriteLine("PASS: 保存先とアプリ配置範囲の確認。ファイル配置・ショートカット作成なし。");
var cleanup = new Receipt { Status = "ApplicationCleanupPending", Application = new() { CleanupPending = true } };
Check(SetupPresentation.CanFinishApplicationCleanup(cleanup), "検証済みのアプリだけの終了待ちは完了可能");
cleanup.AudioRestartPending = true;
Check(!SetupPresentation.CanFinishApplicationCleanup(cleanup), "音声の復旧待ちをアプリ復旧だけで成功にしない");
cleanup.AudioRestartPending = false; cleanup.RegistrationPending = true;
Check(!SetupPresentation.CanFinishApplicationCleanup(cleanup), "登録の復旧待ちは完了不可");
cleanup.RegistrationPending = false; cleanup.PendingSecurityRestore.Add("key");
Check(!SetupPresentation.CanFinishApplicationCleanup(cleanup), "権限の復旧待ちは完了不可");
cleanup.PendingSecurityRestore.Clear(); cleanup.Status = "RecoveryRequired";
Check(!SetupPresentation.CanFinishApplicationCleanup(cleanup), "古いCleanupPendingフラグだけで復旧失敗を隠さない");
var removal = new Receipt { Status = "ApplicationRemovalPending", ApplicationRemovalPending = true, ApplicationRemovalJournal = "journal" };
Check(SetupPresentation.CanFinishApplicationRemoval(removal), "音声復旧後の削除終了待ちは完了可能");
removal.AudioRestartPending = true;
Check(!SetupPresentation.CanFinishApplicationRemoval(removal), "音声復旧前はアプリ削除を開始しない");
var installed = new InstalledApplication(InstallPaths.Default, [], null);
var values = new[] { RawValue.Text("ApplicationDir", InstallPaths.Default), RawValue.Text("ApplicationReceipt", "receipt"), RawValue.Text("ApplicationPackageDir", "package") };
var edits = InstallPaths.MetadataRemovals(installed, values);
Check(edits.Count == 3 && edits.All(e => e.Path == Contract.ConfigPath && e.Before == null && e.Applied && e.After != null), "旧導入からの移行も同意済みの復元トランザクションでアプリ設定だけ削除");
Check(InstallPaths.MetadataRemovals(installed, [RawValue.Text("ApplicationDir", "other")]).Count == 0, "他の保存先の登録は削除しない");
try { InstallPaths.MetadataRemovals(installed, [RawValue.Text("UnrelatedValue", "keep")]); throw new Exception("許可外アプリ設定を拒否しませんでした"); } catch (IOException) { }
var recovery = new Receipt { Target = new("", "", "", "マイク", "", ""), Application = new() { Root = InstallPaths.Default } };
Check(!SetupPresentation.Summary(recovery, SetupOperation.Remove, [], false).Contains("ショートカットも導入前"), "削除でアプリ復元を約束しない");
Check(SetupPresentation.Summary(recovery, SetupOperation.Recover, [], false).Contains("ショートカットも導入前"), "復旧時のアプリ復元を表示");
Check(!SetupPresentation.Summary(recovery, SetupOperation.Recover, [], false, removingApplication: true).Contains("ショートカットも導入前"), "中断した削除の再開ではアプリ復元を約束しない");
Console.WriteLine("PASS: 中断した削除・音声/登録/権限の未復旧・移行用アプリ設定の純粋な状態判定。");
recovery.Status = "RecoveryRequired";
recovery.Diagnostics.Add("fixture ACL restore failure");
recovery.PendingSecurityRestore.Add("fixture-pending-key");
string diagnosticDetails = SetupPresentation.Details(recovery, "fixture-snapshot.json", "fixture recovery error");
Check(diagnosticDetails.Contains("処理状態：RecoveryRequired") && diagnosticDetails.Contains("fixture ACL restore failure") && diagnosticDetails.Contains("fixture recovery error")
    && diagnosticDetails.IndexOf("fixture ACL restore failure") < diagnosticDetails.IndexOf("変更する設定：")
    && diagnosticDetails.IndexOf("fixture recovery error") < diagnosticDetails.IndexOf("変更する設定：")
    && diagnosticDetails.Contains("権限の復旧待ち：fixture-pending-key")
    && diagnosticDetails.IndexOf("権限の復旧待ち：") < diagnosticDetails.IndexOf("変更する設定："), "長いファイル一覧より前に復旧状態・原因を表示");
Console.WriteLine("PASS: 復旧状態・保留権限・原因が詳細一覧の先頭に含まれることを確認。");
var startupChecks = new SetupStartup.Checks(() => { }, () => { }, () => null, _ => false, () => [], () => [target], () => target);
var startup = SetupStartup.Inspect(startupChecks);
Check(startup.PayloadReady && startup.RecoveryReady && startup.Endpoints.Length == 1 && !startup.MustResolveRemoval, "起動時の独立した確認が成功");
foreach (var checks in new[] {
    startupChecks with { ReadApplicationRoot = () => throw new IOException("broken application metadata") },
    startupChecks with { ReadEndpoints = () => throw new IOException("transient endpoint error") },
    startupChecks with { ReadConfiguredIdentity = () => throw new IOException("unreadable saved device") },
    startupChecks with { ReadPendingRemovals = () => throw new IOException("unreadable removal receipt") }
}) {
    var isolated = SetupStartup.Inspect(checks);
    Check(isolated.RecoveryReady && SetupPresentation.CanPrepare(SetupOperation.Recover, isolated.PayloadReady, false, isolated.RecoveryReady), "無関係な起動エラーで復旧を無効にしない");
}
var damagedApplication = SetupStartup.Inspect(startupChecks with { ReadApplicationRoot = () => "fixture-app", ReadShortcut = _ => throw new IOException("broken inventory") });
Check(damagedApplication.ApplicationRoot == "fixture-app" && damagedApplication.ApplicationError != null && damagedApplication.RecoveryReady, "破損した配置情報でも取得済み保存先と独立復旧を維持");
var invalidSelection = SetupStartup.Inspect(startupChecks with { ReadConfiguredIdentity = () => throw new IOException("optional selection failed") });
Check(invalidSelection.Endpoints.Length == 1 && invalidSelection.DeviceError != null, "保存した選択情報のエラーで正常なマイク一覧を捨てない");
var emptyStable = target with { StableId = "" };
var fallbackSelection = SetupStartup.Inspect(startupChecks with { ReadConfiguredIdentity = () => emptyStable, ReadEndpoints = () => [emptyStable, emptyStable with { EndpointId = "other", PhysicalInterface = "different" }] });
Check(fallbackSelection.SelectedEndpoint == emptyStable && fallbackSelection.RecoveryReady, "空のStableIdは機器・物理インターフェイスの一意な照合にフォールバック");
var ambiguousSelection = SetupStartup.Inspect(startupChecks with { ReadConfiguredIdentity = () => emptyStable, ReadEndpoints = () => [emptyStable, emptyStable with { EndpointId = "ambiguous" }] });
Check(ambiguousSelection.SelectedEndpoint == null && ambiguousSelection.RecoveryReady, "曖昧な自動選択は空欄にし、他の操作を無効にしない");
var unknownRemoval = SetupStartup.Inspect(startupChecks with { ReadPendingRemovals = () => throw new IOException("scan failed") });
Check(unknownRemoval.MustResolveRemoval && unknownRemoval.PendingRemovalError != null, "未確認の削除状態で新規導入を許可しない");
var helperOnly = SetupStartup.Inspect(startupChecks with { ValidatePayload = () => throw new IOException("unrelated file damaged"), ReadEndpoints = () => throw new Exception("must not enumerate") });
Check(!helperOnly.PayloadReady && helperOnly.RecoveryReady && helperOnly.Endpoints.Length == 0 && helperOnly.DeviceError == null, "検証済みヘルパーだけで復旧を準備可能");
var invalidHelper = SetupStartup.Inspect(startupChecks with { ValidatePayload = () => throw new IOException("bad payload"), ValidateRecoveryHelper = () => throw new IOException("bad helper") });
Check(!invalidHelper.PayloadReady && !invalidHelper.RecoveryReady, "未検証の復旧コードは実行しない");
Console.WriteLine("PASS: 起動確認の失敗分離。偽の読み取りだけで検証、ファイル・レジストリ・音声操作なし。");
Check(!InstallPaths.RequiresShortcutAccess(false, null), "ショートカットなし・既存所有なしならデスクトップにアクセスしない");
Check(InstallPaths.RequiresShortcutAccess(true, null) && InstallPaths.RequiresShortcutAccess(false, "owned"), "作成時と既存所有ショートカットの変更時は確認を維持");
string parentRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DOT MIC test-parent", "apps", "DOT MIC");
string trustedBase = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
var plannedParents = InstallPaths.PlanParents(parentRoot, path => path == trustedBase, _ => false);
Check(plannedParents.SequenceEqual([Path.Combine(trustedBase, "DOT MIC test-parent"), Path.Combine(trustedBase, "DOT MIC test-parent", "apps")]), "未作成の中間フォルダーを既存の親から順に計画");
InstallPaths.ValidateDeployment(new() { Root = parentRoot, ParentDirectories = plannedParents });
try { InstallPaths.PlanParents(parentRoot, _ => false, _ => true); throw new Exception("親のファイル衝突を拒否しませんでした"); } catch (IOException) { }
try { InstallPaths.ValidateDeployment(new() { Root = parentRoot, ParentDirectories = [Path.Combine(trustedBase, "unrelated")] }); throw new Exception("無関係な親作成を拒否しませんでした"); } catch (IOException) { }
Console.WriteLine("PASS: 未作成の親フォルダー作成計画。模擬の存在判定のみ、実際のフォルダー・権限変更なし。");
long startupClock = 0;
var delayedList = SetupStartup.Inspect(startupChecks with { ReadEndpoints = () => {
    if (startupClock < 600) throw new System.Runtime.InteropServices.COMException("not ready", unchecked((int)0x80070015));
    return [target];
}}, () => startupClock, delay => startupClock += delay);
Check(delayedList.SelectedEndpoint == target && delayedList.RecoveryReady && startupClock == 600, "一時的なマイク一覧の準備遅れを有限待機で回復");
startupClock = 0;
var stalledList = SetupStartup.Inspect(startupChecks with { ReadEndpoints = () => throw new System.Runtime.InteropServices.COMException("not ready", unchecked((int)0x80070015)) }, () => startupClock, delay => startupClock += delay);
Check(stalledList.DeviceError != null && stalledList.RecoveryReady && startupClock == 5000, "一覧取得が準備完了しなくても復旧を維持し、待機を終了");
Check(PackageSelection.ReadFallback(true, null, () => throw new UnauthorizedAccessException("unrelated config denied")) == null,
    "新しい配布ファイルがあれば不要な保存先設定の読み取りをしない");
Check(PackageSelection.ReadFallback(false, "protected-known-cache", () => throw new UnauthorizedAccessException("unrelated config denied")) == null,
    "配置情報の既知のキャッシュがあれば不要な保存先設定の読み取りをしない");
Check(PackageSelection.ReadFallback(false, null, () => "fallback-cache") == "fallback-cache", "他の候補がない場合だけ設定からキャッシュを取得");
Console.WriteLine("PASS: 復旧パッケージの優先候補を不要な設定読み取り失敗から分離。模擬関数のみ。");
