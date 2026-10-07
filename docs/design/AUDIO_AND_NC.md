# 音声・DPDFNet実装仕様 — 必要な頑健性だけを残す

> 履歴資料（旧capture→render仕様）。仮想ケーブル・出力ASRC・独立clock servoは撤去済み。現在のWindows capture MFX／APO RTQueue worker契約は [APO実装](../apo-production.md)を参照。以下の旧経路を現行仕様として使用しない。

## 1. 経路と所有者
```text
選択した物理mic / WASAPI共有event capture
  → float32 / 明示channel選択 / 必要な入力SRC / mono48k / hop480
  ├→ dry SPSC ──────────────────────────────────────────────┐
  └→ jobs SPSC → NC worker[STFT→DPDFNet2→ISTFT] → wet SPSC ─┤
                                                           ↓
render側: 同一source位置のdry/wet選択 → Gate → Gain
  → output ASRC → lookahead sample-peak limiter → ch複製/符号化
  → CABLE Input → CABLE Output → Discord等
```

Input Trimは内部0dB。計算も設定stageも不要で、raw入力の途中でAGCしない。出力保護とgate/gainはNC OFFでも動く。dryをNC worker経由にしない。単一プロセスとし、プロセスクラッシュやWindows全体の停止まで連続音声を保証する別監視基盤は作らない。

captureはpacket取得／短いcopy／解放／変換／hop化と2queueへの投入、NC workerはsession・FFT・状態とjobs→wet、renderは選択器・gate/gain/ASRC/limiterを所有する。UIと非RT controlは設定・列挙・起動停止を扱う。専用native control threadは既存なら維持、新規で必ず追加する必要はない。直列の非RT制御タスクで足りる。

capture/renderの定常処理にORT Run、heap確保、mutex待ち、ファイルI/O、UI呼出を入れない。eventを待つこと自体は禁止ではない。初期化・設定保存・UI・model loadの確保やmutexは許容する。NC workerは1本、OFFではevent待ち。ORT内部が完全にallocation-freeだと未測定で主張しない。

## 2. WASAPIと形式変換を重複実装しない
共有・event駆動を基本とし、I/OはMMCSS等の通常の音声処理向け設定を利用する。REALTIME_PRIORITY_CLASS、CPU affinity固定、電源プラン変更、busy spinは入れない。実際のperiodを使い、NCの10ms hopとWASAPI packet長を同一視しない。[S06]

新規実装は `IAudioClient::Initialize` の共有event方式を基準にする。GetMixFormatのsample rate/channel maskを尊重し、まずfloat32のclient formatで契約する。mix formatがそのままfloat32ならそのまま使い、変換が必要な接続ではAUTOCONVERTPCM等の正式flagを使う。SRC_DEFAULT_QUALITYの意味も公式仕様に従う。shared eventのhnsBufferDuration/hnsPeriodicityは0を基準に、SetEventHandle、実GetBufferSize、実paddingを使う。[S02][S03][S07]

Windows側が整数PCM機器との変換を行う接続では、アプリにPCM16/24/32の全containerデコーダを重ねて作らない。ただしfloat32として接続できていないデータをfloatで解釈しない。初期化エラーは形式を明記して返す。既存の正常なnative形式アダプターがある場合は置換不要で、その使用する変換だけを検証する。

完成範囲の基本経路はmono/stereo、44.1/48kHz端点から内部48kHz。OS変換と手動SRCを同じrate変換に二重適用しない。入力側は実client rateが48kHzなら固定SRCを省略する。出力は可能ならmix rateと同じclient rateを使い、最終ASRC後のlimiterより後にアプリ側のSRCを置かない。OS側後処理のピークまでは保証しない。

stereo入力はchannel1既定、詳細でchannel2または明示平均(L+R)/2。無断で大きい側へ切り替えない。stereo出力はmonoを両側へ同振幅で複製。SILENT flagはポインターを読まず0、非有限値は0としてカウンター、DISCONTINUITY・端点失効は復旧へ通知する。GetBuffer/ReleaseBufferの対とCOM所有スレッドを守る。

## 3. 小さいqueueと時刻タグ
v2同様、dry32hop、jobs4hop、wet16hopを初期容量にする。容量を埋めてから再生するのではない。通常保持量は検証したmodel delay＋実行余裕で決める。処理中の拡張はない。

1hopの480floatを素直にcopyする。SPSCは単一producer/write cursorと単一consumer/read cursor、publishはrelease/acquire。満杯時にproducerがconsumer cursorを進めない。実装済みのSPSCを再利用し、新規でも固定長の小さい実装または既存ライブラリで足りる。汎用MPMC、参照カウントzero-copyは作らない。

各blockはsource sample位置とNC generationを識別できるようにする。deviceの再構築を識別するepochは既存方式でよく、すべて停止・queue破棄してから再開する場合は不要な多段世代管理を増やさない。source位置とNC世代だけを曖昧にしてはいけない。時刻計測用timestampは測定時だけ追加してよい。

jobs欠落／満杯は連続RNN入力の破綻なので、そのgenerationを失効して原音へ退避する。wetが遅れて届けば古いsource/generationとして捨てる。wetは単一workerのFIFO順であり、一般の並べ替えmapは不要。renderは一致結果があれば使い、必要なら1blockだけ次位置用scratchへ保持する。readerのdiscardはqueue容量以内に有界化する。

dry欠落は別のaudio xrun。無音補完→共通停止／再初期化／refill／fade-inで復帰する。NC failureだけならdryは同じsource位置で続ける。制御側から動作中queueをclearしたり、過去の会話を後から一括再生したりしない。

## 4. DPDFNetの固定契約
公式 `dpdfnet2_48khz_hr.onnx` / FP32 / CPUExecutionProviderのみ。intra-op=1、inter-op=1、ORT_SEQUENTIAL、intra/inter allow_spinning=0。graph最適化はALLを初期基準とし、問題がある場合だけ別levelを比較する。GPU EPや別モデルは入れない。[S04][S05][S13]

前後処理はmono正規化float→960サンプルのVorbis窓STFT→481binのreal/imag→モデル→逆FFTの正規化→窓＋overlap-add→480サンプル。通常のspecは[1,1,481,2]。実ファイルのI/O名・shape・dtype・metadataを読み、契約と合うことを起動準備で確認する。960を1024へ丸めない。モデルへPCM480点を直接渡さない。[S04][S05]

初期stateはmetadataのstate_size、erb/spec norm size、erb_norm_init/spec_norm_initから作る。norm領域を含む全ゼロ初期化は誤り。state_outを次hopへ継承し、input/outputは別の事前確保領域をping-pongする。RNNoiseの×32768や、モデル内部にある正規化の二重適用は行わない。

KissFFT float32・OpenMP無効を初期採用。window、FFT plan、Ort::Value、shape/name、状態、scratchを再利用する。標準CPU版ORTの既成配布を使い、独自ORTビルドや再学習を始めない。Pythonは公式前後処理から短い参照fixtureを作る開発用途のみ。配布物にPythonは不要。

モデルprofileには取得revision/sha256/size、shapeとmetadata、採用ORT、alignmentとavailabilityを保存する。依存全般は通常のlockを使い、モデル独自情報だけを別記する。model hashは取得・package・初回model loadで確認し、毎hop／トグルで再計算しない。runtime downloadはしない。

## 5. 遅延とON/OFF
10msはhopであり遅延ではない。STFT＋modelのsource alignmentと、対応wetを出せるまでの入力蓄積を短い参照比較で確認する。denoiserはimpulseを消す場合があるので、identity OLAと既知発話を併用する。一つのインパルスだけで50msと決めない。測定用に多数のモデルを比較しない。

共通再生遅延は、検証したwetのavailabilityに実行余裕20msを加えたprofileを起点にする。alignment・window待ちを二重加算しない。実行中のNCトグルで再生cursorを飛ばしたり、遅延を増減したりしない。OFFもこのdelayを通るため、ゼロ遅延の完全システムバイパスではない。

NCの要求値、実状態、worker停止ackは区別する。既存のOff/Starting/FadingIn/On/FadingOut/FaultBypassedがあればそのまま使う。新規でもこの程度の小さい状態機械で足り、UI状態の全直積や汎用トランザクションは不要。

ON要求は非RT workerを起こし、初回はsessionをload、state/OLAを初期化する。準備完了まではjobsを貯めずdryを出す。現在の再生位置に整合するwetがそろってから20msのlinear fadeでOnへ進む。再ONは新generationと初期stateで現在のライブ入力から始める。

正常OFFは整合wet→dryへ20msで移り、以後jobs供給を止め、in-flight Runが戻ってworker停止ackを確認したらOff。確定後はRun/STFT/ISTFT/state更新をしない。sessionメモリーは保持してよい。cold OFFで背景warm-upしない。

fadeは `y=(1-a)*dry+a*wet`。aを音声sample clockで進め、反転時は現在値から最新targetへ動かす。UIのanimation clockは使わない。停止処理が終わる前に「推論停止済み」と表示しない。

## 6. 最低限の異常処理
renderが必要とするsource位置のwetを期限内に得られなければ待たずdryを使う。jobs/wetの喪失、ORTエラー、非有限結果はNC faultとしてラッチし、新規Runを止める。自動モデル切替や自動retryはない。利用者のOFF→ONで再試行する。

fade用のwetが残っていれば通常fadeを使う。既にwetが欠けていれば、存在しない未来wetを待たず、同じsourceのdryへ退避する。必要なら最後の出力値との差を最大1msで減衰させるde-clickを使う。これを完全無歪み・完全無停止の保証とは説明しない。先行的な故障予測や複雑な多段overload schedulerは不要。

NCだけが遅いケースにUIやdeviceを再起動しない。逆にdryまで欠けた故障をNC fallback成功と取り違えない。すべてのfaultは実状態と短い理由を表示し、counterを増やす。起きていない故障向けの長い通知・自動復旧frameworkは作らない。

## 7. 共通DSP
ゲートは手動gain前。5ms相当EMA二乗値とopen/close別閾値を使い、Closed/Opening/Open/Holding/Closingとattack/hold/releaseを持つ。初期値はdefaults。閾値はpower域へ事前換算し、サンプルごとのlog/powは不要。OFFではunityへ滑らかに戻す。

NC切替でdetectorやgate gainを0に戻さず、連続入力として追従する。通常hold160msに対してNC fade20msであるため、NC専用のgate modeやタイマー凍結を初めから必須にしない。実際に語頭・語尾切れが再現したときだけ限定的に補正する。

gainは10^(dB/20)、20msの倍率ランプで現在→最新要求へ。サンプルごとのdB変換を避ける。limiterは最終ASRCの後に配置し、-1dBFS ceiling、3ms lookahead、60ms release。通常域は線形で、tanhだけの常時歪みにはしない。最後の安全clampと非有限値の除去を別に持つ。保証はアプリが書くsampleでありtrue-peakやDiscordのAGC後ではない。

## 8. クロック補正を単純に保つ
captureとrenderの実clockは独立なので、48kHz同士でもbufferだけで放置しない。libsamplerateのstreaming output ASRCを一つ使い、ratio=出力frames/入力frames、実used/generated数で位置を進める。通常動作で定期的なsample削除・複製をしない。[S14]

既存の安定したPI制御があれば維持する。新規では、必要以上の自動調整研究を避けるため、目標水位からの小さな偏差を許す有界P制御を開始案にできる。defaultsのKp=2ppm/入力sample、観測100ms、偏差EMA1秒、補正±1000ppm、slew50ppm/sを使う。水位はdryと未消費scratchを合計した内部48kHz相当量。

式は `e = filtered_water_samples - target_water_samples; u_ppm = clamp(-Kp*e, -limit, limit); ratio = nominal_ratio*(1+slew_limited(u_ppm)*1e-6)`。水位が高ければratioを下げて入力を多く消費する。±200ppmの定常clock差に対し理想化した均衡偏差は約±100sample（約2.1ms）であり、零偏差を強制するための積分制御は必須ではない。これは設計上の計算であり実機測定値ではない。

同じ制御関数を使った短い仮想時間確認で符号・水位の有界性を確かめる。不安定なら既存の単純なPI等へ修正して原因を記録するが、多数ゲインの最適化や長い実時間校正は課さない。delay reserve20msとこの水位偏差を分け、実運用でwet期限とdry水位に余裕があることを見る。

## 9. 境界・復旧・終了
C ABIはopaque handle、size/version付きの少数structで足りる。bool表現・文字列・所有権を固定し、例外を境界外へ出さない。UIにPCM pointerやCOM/STLを返さない。アプリとDLLを一緒に配布するので、多世代ABI互換や外部クライアント用protocolは不要。

設定はcontrolが検証し、単一writerの小さいcommand queueまたは安全なatomic値で所有threadへ渡す。通常UI操作は最新値へ合流する。非atomic payloadを並行更新する偽seqlockは不可。meterは小さい値snapshotでよく、履歴データベースやstreaming telemetryは不要。

device切断・sleep/resumeはstop→所有thread停止確認→queue/clock/state再構築→refill/fade-in。選んだ同じ端点へ戻す。別mic/speakerへ無断fallbackしない。モデルだけ壊れた場合はdryの増幅を使えるが、NCを正常とは表示しない。

Exitではcapture/renderを止め、NC停止を要求し、Run完了後に解放する。UIは非同期で待つ。Run中のsession解放やTerminateThreadは禁止。戻らない異常では新しいworkerやsessionを増殖させず、明示Exitの最終終了でのみプロセス全体を終える。通常停止と異常終了を同じ「安全に完了」の表示にしない。
