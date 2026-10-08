# 0.5.0-rc.1 検証範囲

ソース公開用の候補です。公開済み0.4.2の実機受入結果を、この候補の実機検証結果として流用しません。新しいtag・バイナリReleaseは作成しません。

## 確認済み

- Setup / App のRelease build：警告・エラー0。
- Native gate / control bridge / metadata fixture：build成功。既存のSPSC alignment padding警告、IntegrationのVARENUM変換警告は残ります。
- Native pure fixtures：マイクごとのDSP状態分離、原音形式通過、共通profile、diagnostic集計、Init1/2/3解析、v2 ABI、freshness／frame進行、mixed RAW/DSPの誤判定防止、NC状態とerrorの対応。
- Managed pure/mock fixtures：fleet／端点別復旧／共有compensation、resident retry、条件付きSetup同意、ViewModelの設定保持・再試行・終了時保存、ACL比較、既存presentation／worker。
- 更新終了policy：12秒の通常終了待機→必要時だけ強制終了→5秒の終了確認、正確な実行path、process開始時刻token、確認後に起動したアプリの再同意、終了未確認時の拒否をdelegate fixtureで確認。
- Setup offscreen fixture：109画面、19 fake flow、幅480／424、模擬100／150／200% scale。起動中アプリの終了説明も含みます。実per-monitor DPIではありません。
- Native／managed observation ABIは48 bytes、calls offset 32。共通設定の取得成功と動作診断の成否を分離します。
- 候補APOは新しくbuildし、`ApoCandidate.lock.json`を更新。旧`ApoPayload.lock.json`、推論DLL、モデル、ORT/runtimeの固定hashは保持します。
- 再生成ZIPのpackaged Setupで`--inspect-package`成功。612 payload files、ZIP内の614 manifest hash、全616 entriesと重複pathなしを確認。

ローカル配布候補ZIPのSHA-256：`8F9726316FF15ADE721693F747DA75325C5CD8DB7D7985BF4650E9EB5E887851`。バイナリReleaseを公開したという意味ではありません。

## 未検証・限界

- 実権限でのinstall／旧版upgrade／repair／remove／復旧、serviceのlifecycle、hotplug、ACL復元、unelevated launch。
- 実プロセスの通常終了message配信／強制終了／使用中ファイル置換。旧版はmessage未対応のため待機後の強制終了になる場合があります。
- OEMのCAPX通知、Init1/2の実activation、複数マイク同時処理、audiodg／常駐処理負荷、実高contrast／DPI。
- DSPはmodeを確認できたnon-RAW float32 48 kHz mono/stereoのみ。その他の対応descriptor、RAW、Init1の未知modeは原音通過であり、広い形式でのNCやSRCは未実装です。
- NCはOS RTQueueが必要です。旧initに合わせた代替推論poolは追加していません。
- endpoint propertyの全host同時集計は未実装です。別publisher、古い公開、packet進行未確認は動作成功にしません。OEM通知が届かなければ未確認が残り得ます。
- 既存のDPDFNet2音声品質・Discord受入は再実行していません。worker executableの引数なし起動はusage確認のみで、今回の推論検証には数えません。

動作表示を変えるだけの自己録音、音声サービス再起動、復旧データの削除は行っていません。個人のendpoint識別子や外部snapshotは公開ソースに含めません。

設計・build手順は[MULTIMIC-0.5.0.md](MULTIMIC-0.5.0.md)、native ABIは[Native candidate contract](../Apo/MULTIMIC-CONTRACT.md)を参照してください。
