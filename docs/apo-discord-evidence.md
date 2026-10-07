# Discord Standard no-op MFX実行証拠 — 2026-10-07

## 判定

**PASS: このPC／fifine Ampli1／Discord 1.0.9260のStandardで、物理captureに関連付けたno-op MFXの非RAW実行を確認。** これは無変更copy APOの実行成立性だけ。声の聴感、実通話、DPDFNet/Gate/Gain、設定連携、性能、一般配布署名はPASSに含めない。ユーザーはマイクテスト実行・停止を確認したが、自分の声が返ったかは未確認と回答。Legacyは非RAW経路が確認できたため試さない。

## 実機状態

run: `artifacts/apo-gate/development/local-trust-atl-20261007-02/`。

- LastBootUpTime=`2026-10-07T01:54:43.5000000+09:00`。ユーザー自身が再起動。Secure Boot=1／HVCI=1、TESTSIGNING変更なし。
- 0.1.1 APO／Microsoft inbox ProxyのchildがStatus OK。GetDevice、ActivateIAudioClient、GetMixFormatはいずれも`0x00000000`。`0x80040110`は解消。
- runtime ID=`<REFERENCE-ENDPOINT-ID>`（導入前から再生成）。原名=`マイク (fifine Ampli1)`、同じ物理USB AUDIO GLOBAL、StableId=`<REFERENCE-ENDPOINT-ID>`、ContainerId=`<REFERENCE-CONTAINER-ID>`は導入前と一致。48kHz/2ch/FP32。
- audiodg PID=18268。同runのpost-reboot診断、`endpoint-postreboot.json`、trace前後snapshotで確認。

## 相関の鎖（2つのtraceを使用）

1. `discord-standard-01/user-observation.json`: Standard／現fifine選択／ローカルマイクテスト実行・停止をユーザー確認。試験用capture-smokeはこのtraceで実行していない。
2. `discord-standard-01/relevant-events.json`: 02:13:35のAudio event127で対象captureの`Raw=false`／category0。Activity=`{aa6d5a9d-ea46-42f0-a5b6-864d89eb5d40}`。同activityで独自CLSIDのevent50、gate Initialize、event128を確認。
3. Initialize: HostPid=18268、Instance=`0x291618126D8`、対象runtime ID、mode=`{c18e2f7e-933d-4965-b7d1-1eef228d2af3}`（**DEFAULT**）、ModeObserved=1、DiscoveryOnly=0。同activityの別Instance／DiscoveryOnly=1は実行証拠に数えない。
4. event127／128のExecution PID=4892はaudio service側であり、Discord PIDではない。これを直接Discordと扱わない。`IAudioSessionManager2`の読み取り専用列挙を別途追加。`sessions-after-late.json`と次traceの`sessions-before.json`で対象captureの唯一の**active** sessionはDiscord PID22216、GetProcessId=S_OK、soleProcess=true、session instance IDにもDiscord.exe／PID／対象endpointが含まれる。PID0のcross-process sessionはinactive（state0）なので所有証明に使わない。
5. `discord-standard-release-02`: ユーザーがトレイからDiscordを通常終了。開始時のDiscord PIDsに22216があり、終了時は全Discord PID不在。audiodgは前後とも18268。`sessions-after.json`でDiscord session消失、active capture sessionなし。
6. この終了時のStreamSummary（02:35:36）: HostPid=18268、Instance=`0x291618126D8`、同じendpoint／DEFAULT／ModeObserved=1／DiscoveryOnly=0、**APOProcessCalls=132060、ValidFrames=63388800**。直前Initializeから約1320.623秒、処理frame/48000=1320.600秒で期間も対応。この正のカウンターと所有sessionの終了で実行を相関確認。
7. 両traceとも`Total Events Lost=0`（297 events／158 events）。sequential ETLで先頭のcircular上書きなし。ETW時刻の表示はtracerpt出力そのまま（`+08:59`表記）で、時刻の相対差とactivityを使う。PID／Instance／endpointを時間範囲を無視して結合しない。

上記の結合条件／期間を検査した`discord-execution-correlation.json`と、原ETL2本／署名payload DLLのSHA256を保存した`discord-evidence-hashes.json`も同runに保持。元ETL／旧runは変更していない。

## 数えなかった証拠・制限

- 先行`control-capture-02/03`は短いprobe通常capture。実DEFAULT Initializeを記録したが、Discordが既にcaptureを保持していたため終了summaryは出なかった。これをDiscord単体試験とはしない。
- `discord-standard-01`の初期summary=108779 calls／52213920 framesは、試験前から稼働した旧graphの終了値。最終判定には使わない。初期化時にcounterがresetされるため、最終summaryからこの値を引いて差分とすることもしない。
- 123／125のRaw=falseはformat queryだけなので判定に使わない。RAW capability=trueは依然trueであり、Discordの実stream Raw=falseと矛盾しない。
- 本相関はaudio-service ETW＋endpoint session ownership＋Discord終了時のgraph summaryによるもの。client要求関数そのものの引数traceではない。マイクテスト停止だけではcaptureが終わらず、ユーザーの通常終了まで記録を継続したため、カウンターは10秒試験だけの値ではない。長いETW待機を性能試験／録音と報告しない。
- エージェントは音声sampleを保存／再生していない。ローカルマイクテストはユーザーが実行。第三者通話／強制終了／endpoint disable／Windows既定変更はしていない。
- 開発自己署名のこのPCでの実audiodgロードを確認したが、PETrust保護状態の一般保証やMicrosoft production certificationを意味しない。0.1.1開発packageと当該信頼は残っている。秘密鍵は非exportable、一般配布しない。

## 次の最小ステップ

声の聞こえ方を一度確認し、その後CAPX配布条件の検討と既存DSPの切り分けへ。今回はApp/Native／モデル／UI／既存bridge／元ZIPを変更しない。DPDFNetをEFXへ移さず、固定ABI／ORT設定／STFT／alignment／NC OFF停止契約を維持する。
