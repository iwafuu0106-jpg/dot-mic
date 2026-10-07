# DOT MIC APO — ローカルRelease 0.3.4

このPCでGain／Gate／Limiter／Bypass／実DPDFNet wet／NC OFF停止／既存UIと通常Discord通話を確認済みです。60秒の通常通話区間はwet採用100%、dry fallback0、追加Fault／InvalidPacket0。旧bridgeと出力clock補正を受入後に撤去しました。**一般配布certification／Store／MSIX承認済みではありません。**

## 起動

`artifacts/apo-production/releases/0.3.4-local.zip`をすべて展開し、`0.3.4-local/UI/DotMic.App.exe`を通常権限で起動します。

- Discord入力は元の「マイク (fifine Ampli1)」のまま。
- DOT MICでGain／Gate／NCを調整。モデル／strength／thread選択はありません。
- 検証後はMaster Bypass ON／Gain0／Gate OFF／NC OFFへ戻しています。効果を使う際はmenuでMaster BypassをOFFにします。
- Master Bypassは全効果をバイパス。NC OFFはNCだけ止めます。
- NC「原音」は同じ時刻のdryへfault退避した状態。自動retryせず、必要時OFF→ONで再試行します。
- 全経路3984samples／83ms。Bypassでも遅延は変わりません。
- UIを終了してもWindows APOと確定設定は残ります。UI終了とNC OFFを混同しません。

## 内容と導入状態

- `UI/`：既存Main／tray／motion。通常権限。
- `APO/`：署名済みcomponent0.3.4、依存DLL、固定モデル。
- `Rollback/`：元署名no-opとbyte-identicalな0.3.5 fallback。未導入。
- `docs/`、`licenses/`、`manifest.json`、`sha256.json`、`evidence/`：手順・ライセンス・hash・実受入結果。

**このPCは導入済みなので、再インストールや入力の選び直しは不要です。** Component=`oem186.inf`／0.3.4／Problem0、Extension=`oem175.inf`／0.2.2を保持。audiodg17952で次の新hashが一致しています。

`9F82245EA0E5061AD30317251F6EF796B82D5C10D876BB3B66FF1AF4C282B20B`

UIアセンブリ版は既存3.2.0、APO package版は0.3.4です。Microsoft依存DLLの署名はそのまま保持。開発証明書は2026-11-06まで有効で、既存local trustを使用しています。証明書秘密鍵／製造元audio driver／第三者仮想driverは同梱しません。

新規package導入は通常UIと別の管理者作業です。別PCへこの開発trustを自動作成するinstallerは提供していません。元workspaceの`Apo/build.ps1 stage`でfresh candidateと署名・INF・CATを確認し、`Apo/deploy.ps1 prepare-rollback`で正しいbaseline／最新ownershipを指定した後にだけ導入します。既存installed runへのinstall再実行はguardが拒否します。これは一般配布手順ではありません。

## 設定と安全な回復

通常権限のUI操作、または元workspaceのcontrolで安全値へ戻せます。

```powershell
$control = "$PWD/artifacts/apo-production/build/Release/DotMic.ApoControl.exe"
& $control set NcEnabled 0
& $control set GateEnabled 0
& $control set GainDb 0
& $control set MasterBypass 1
```

packageの回復／削除は必要時だけ行います。Discordなどのcapture clientを**通常終了**して、管理者PowerShellを開いてください。ownership正本は元workspaceの最新runです。ZIP内のevidenceコピーを操作receiptとして使いません。

```powershell
$run = "$PWD/artifacts/apo-production/runs/worker-ui-20261007-03"
# 状態確認だけ：
./Apo/deploy.ps1 verify -RunDirectory $run
# 問題時に必要な方だけ：
./Apo/deploy.ps1 rollback -RunDirectory $run
# または、この開発で追加したowned packageを除去：
./Apo/deploy.ps1 remove -RunDirectory $run
```

`rollback`は元no-opへの回復、`remove`はreceiptのowned追加分だけを削除してbaseline no-opを保持します。完全な工場状態への復元や製造元driverの削除ではありません。無関係のOEM driver／共有runtime／trustを削除せず、`/force`やendpoint IDの書換えを使いません。ownership不一致や部分失敗ではreceiptを保持して停止します。

OS再起動が必要でもユーザー自身が都合のよい時刻に行います。再起動の実行・予約、audiodg／Discordの強制終了、audio service再起動はありません。

UIだけを削除する場合はサインイン起動OFF→menu「終了」→展開フォルダーを削除。必要なら`%LOCALAPPDATA%\DotMic`のアプリ専用設定も削除します。UI削除だけではAPOは外れません。

## 検証範囲と保存物

実受入・性能は`docs/apo-production.md`と最新runの`discord-live-nc-01/acceptance.json`／`performance.json`／`performance-interpretation.json`が正本です。callback時間はQPC経過時間、hop mean／p99はgraph累積、CPUはlogical core相当です。hardware xrun counterは非公開で、APO InvalidPacket proxy・ETW loss0・ユーザーの音切れ観察を区別しています。true-peak、別PC／全format／長時間matrixは今回のPASS範囲ではありません。

性能に問題がなかったため追加最適化は行っていません。旧bridge／出力ASRC／独立clock servo／ケーブルsetupを削除し、モデル前後処理と無関係の固定比SRCは保持。元ZIP／旧RC／撤去前`pre-g-source/`＋hash／元baseline commit／original no-op fallbackは回復可能なまま保存しています。マイク／通話PCMは記録していません。
