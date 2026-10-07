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
