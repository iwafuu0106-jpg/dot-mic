# 0.5.0-rc.1 開発候補

ソース公開用の検証候補です。バイナリReleaseは未公開です。実機でのインストール・自動統合・更新・削除・復旧は未検証で、検証済み安定版とは扱いません。

## 変更

- 通常セットアップのマイク選択を廃止。台数、実際に必要な影響説明、同意ボタンを表示します。
- COM／APO登録、音声処理ファイル、保護音声設定、アプリ配置は共有transactionで管理します。
- マイクの関連付け、元の値、権限の復元記録、エラーは端点ごとに管理します。失敗した端点だけ取り消し、共有資源と他の端点は戻しません。
- 管理者権限のWindows Serviceがデバイス通知を待ち、UIが閉じていても新規接続を確認します。メーカー名・製品名では対象を決めません。
- 使用中または活動状態不明のマイク、未承認の効果置換・追加権限は保留します。接続時にWindows Audio Service全体を再起動しません。
- 設定は共通の保存先が正本です。CAPXは端点への反映経路として使い、UIが任意の1台を正本にしません。反映失敗は他の端点へ広げず、共通設定の通知と保留処理で再試行します。
- 旧schema-1の復旧ファイルとバックアップを保持し、共有部分と端点部分のコピーへ移行します。旧ファイルをそのまま複数マイクの削除に使いません。
- 切断中の端点は記録された既存キーだけを復元します。消えたキーは作り直しません。競合や未解決の関連付けが残る場合、共有APOの削除を止めます。
- 更新・修復の同意後だけ、同じセッションの導入済みアプリへ終了を要求します。配置marker・実行ファイルhash・正確なpath・保持したprocess handleで対象を確認します。通常終了を12秒待ち、旧版／無応答時のみ対象を強制終了し、さらに5秒以内に終了確認できなければ上書きしません。別ユーザー、識別不明、配置変更は中止します。確認画面、常駐管理、削除／復旧でこの自動終了権限を使いません。

## 対応範囲と未完了事項

DSPは通常の48 kHz・float32・mono/stereoで動作します。DPDFNet2の48 kHz／hop480／FFT960、非同期RTQueue、dry/wetの時刻整合、固定遅延、NC OFF停止は維持します。

その他の検証可能な8–192 kHz PCM/float形式とRAWは、原音のバイト列を遅延なしで通します。ゲイン・ゲート・リミッター・NCは適用しません。モデル境界のSRCと、広い形式でのDSP対応は未実装です。Windowsの形式変換を保証したものでもありません。

APO初期化はInit1/2/3のサイズ、cbSize、CLSIDを確認して受け入れます。Init1のmodeは不明のまま原音を通します。Init2のmodeは保持しますが、RTQueue提供がない場合はNCを開始せず原音へ戻します。旧initをDEFAULTと推測したり、代わりの推論thread poolを作ったりしません。

APOインスタンスごとにRNN、STFT、Gate、Limiter、queue、generationを分離しています。WindowsのMFXインスタンスとアプリの入力streamは必ずしも1対1ではありません。現在のNCはchannel 0を処理し、ステレオには同じ結果を複製します。

登録のread-backだけでは実動作成功にしません。反映待ちの端点に限って一時的なメーター公開を要求し、既存streamの処理が進むか確認します。確認のために録音を開始したり、音声サービスを再起動したりしません。すべてのドライバー、RAW、exclusive、音声処理modeへの適用は保証しません。

## 「入力待機」の診断

従来は列挙／CAPX公開／診断値の取得失敗を0へ落とし、実際には未確認でも「入力待機」になりました。共通設定の読み取り成功と動作診断を分離し、診断失敗だけで設定操作を止めません。メーター要求の書き込みが失敗しても読み取りを試します。

v2診断は型付き整数、公開sequence、publisher、stream epoch、公開時刻、frame数、DSP適用可否を扱います。「接続中」は同じpublisher／epochでDSP対象streamのframe数が増え、公開と進行が1秒以内の場合に限ります。原音streamだけの進行をDSP成功には使いません。無音でもframeが進めば動作と判定します。lockedのみ、古い値、counter reset、別hostは「動作未確認」です。通常待機の判断には既存capture sessionの状態を読みます。取得不可／活動不明は待機と決めつけません。

診断propertyはhost間で共有されるため、全hostの完全な同時集計は未実装です。別publisherの上書きはbaselineを破棄します。通知が届かないOEM経路では「動作未確認／診断未取得」が残り得ます。通知スレッドでCAPX接続を再試行しますが、音声RT側でpropertyアクセスや公開を行いません。動作表示を変えるための自己録音、既定マイクへの置換、音声サービス再起動は行いません。

## 管理コンポーネント

サービス名は`DotMicMicrophones`。保護されたpackage cache内のSetupを`--manager-service`で起動し、LocalSystemで端点登録を扱います。任意のコマンド、ネットワーク窓口、録音機能は持ちません。UIのサインイン起動とは別です。

イベント起動型Scheduled Taskは音声デバイス通知を直接受け取れず、ドライバーのイベントログ記録に依存するため採用しません。サービスはMMDevice通知と共通設定の変更通知を待ちます。通常待機は無期限、保留・一時エラーの再試行は2–300秒のbackoffです。

共通設定はregistry64の`SOFTWARE\DOT MIC\Community\CommonSettings`に保存します。`User`と`Volatile`のパラメーターleafだけに一般ユーザーのquery/set-value権限を与えます。値は既知のID 1–9の4-byte floatに限定します。実行先、デバイス範囲、service設定、receiptは一般ユーザーが変更できません。

## ビルド・検証

```powershell
# 既存の固定依存cacheを指定。公開物やWindows音声設定を変更しません。
./Community/build-candidate.ps1 -OutputDirectory artifacts/new-candidate -DependencyRoot C:/path/to/.deps
dotnet run --project tests/multimic-core -c Release
dotnet run --project tests/multimic-ux -c Release
dotnet run --project tests/resident -c Release
```

候補APOのlockはbuildごとに生成し、旧`ApoPayload.lock.json`とは分けます。推論DLL、モデル、ORT/runtimeは旧固定hashを維持します。旧Release、tag、資産は変更しません。配布は従来どおり複数ファイルのZIPで、単一EXEにはしません。

検証はbuild、pure/mock、offscreen画面fixture、短い読み取り専用の通知待機観測に限定します。自動終了の順序／待機期限／正確なpath／再利用されたPIDのtoken拒否、最終設定保存、v2 ABI／freshness／frame進行／RAW等の原音表示／Init1/2/3解析をpure fixtureで検証します。実プロセスの強制終了、OEM CAPX通知、旧initの実activationは未検証です。DPDFNet音声品質や旧Discord受入試験は再実行しません。実機での音声処理負荷、複数マイク同時動作、serviceの実ライフサイクルは未確認です。

native境界の詳細は[Apo/MULTIMIC-CONTRACT.md](../Apo/MULTIMIC-CONTRACT.md)、旧導入・復旧の制約は[INSTALLER-0.4.2.md](INSTALLER-0.4.2.md)を参照してください。
