# 固定0.25 PCM到達証明 — 2026-10-07

## 現在

**このページは完了した固定PCM gateの証拠。後続のRT shell/CAPX作業と最新deployment状態は [production状態](apo-production.md)を参照。方式成立性を再試験しない。** 下記の「現在」「現runtime」はこのgate終了時点の記録。

**PASS：このPCのDiscord Standardローカルマイクテストで、固定0.25 PCM減衰の到達とno-op元音量復帰を確認。四条件すべて成立。** 一時フラグを撤去済み、現在はデフォルトのbit-exact no-op。production DSP／UI／DPDFNet／性能／一般配布は未検証。

最新run=`artifacts/apo-gate/development/pcm-switch-20261007-04/`。選択component=oem171.inf／0.1.4.0、Extension=oem162.inf／0.1.1.0は未変更。現runtime ID=`<REFERENCE-ENDPOINT-ID>`。原名／StableId／ContainerId／physical USB GLOBALは一致、3段階音声APIはS_OK。Secure Boot ON／HVCI ON、boot/security設定変更なし。

ユーザー自身が再起動し、LastBootUpTime=`2026-10-07T03:29:49.5000000+09:00`。新DLLの実ロード・SHA256一致を確認して以下の一回のA/Bを完了した。エージェントからOS再起動を実行／予約していない。

## 受入結果（最新）

| 条件 | 結果 |
|---|---|
| ① Discordで明確な減衰 | **PASS**：同じStandard／fifine入力／他設定でユーザーが「明確に小さい」と確認 |
| ② 同時に加工APOの正カウンター | **PASS**：実FixedGain0.25、audiodg17668、7569calls／3633120frames。新DLLの実module hash一致、Discord4148のcapture所有／通常終了と相関 |
| ③ no-opで元音量復帰 | **PASS**：同じDLL・endpointでFixedGain1、5655calls／2714400frames。Discord8984所有／終了と相関、ユーザーが元音量復帰を確認 |
| ④ 識別情報・Discord入力設定維持 | **PASS**：両試験前後・A/B間でruntime IDも同じ4e23db9d。原名・StableId・ContainerId・physical USB GLOBAL一致。入力再選択／他設定変更なしを両試験でユーザー確認 |

証拠は最新runの`discord-quarter-postreboot-01/`と`discord-bypass-postreboot-01/`。実ロードDLLは両方とも`dotmic.apogate.inf_amd64_8bf6afbed3759e11\DotMic.ApoGate.dll`、SHA256=`09078c72df88425c2d6d9f256522c0e412386ec6147147be08bb266486f1111f`。旧no-op module/hashは使っていない。双方EventsLost0（540／1196 events）。

- 減衰graph：Instance=`0x2A180173B98`。stream／Initializeのcreation activity=`{14ce30c8-76ab-438b-9ae9-1cf07a8e1758}`、Lock／Summaryのprocessing activity=`{d15fce4f-a12f-4f9f-89d9-9ac323ef3893}`。
- no-op graph：Instance=`0x2A180127D38`。creation activity=`{87a4f81c-8193-4c4c-8165-65f563a3fafc}`、processing activity=`{20530714-44d4-4a74-b829-115a486fc0da}`。
- creation側event127/128と独自CLSID event50／Initializeを同activityで照合。Lock側のactivityはcreationと異なるため、全イベントが同activityと仮定しない。同PID／Instance／endpointと直近のInitialize→Lock順序（2.860ms／3.263ms）で結合し、同processing activityのLock→Summaryを照合。両graphの実modeはDEFAULT、DiscoveryOnly0、ModeObserved1、stream Raw=false。
- Discord ownershipは同じendpointの唯一のactive session／GetProcessId=S_OK／soleProcess=true／Discord.exeを含むsession IDで確認。通常終了後、active capture sessionと全Discord processが消失。Audio eventのPID4772はservice側でありDiscord PIDと扱わない。
- 上記結合条件／四条件／cleanupを確認した`pcm-proof-result.json`、ETL／XML／観測／所有情報のSHA256を保存した`pcm-evidence-hashes.json`を保持。初期の検査式はcreationとprocessingのactivityを誤って同一視して停止したが、実イベントを確認して二段の結合へ修正。原証拠は変更していない。
- カウンターは5秒の声だけではなく、ユーザーの通常終了まで保持されたgraph全体。減衰75.722秒／frame期間75.690秒、no-op56.580秒／56.550秒。callback時間／glitch／CPU／latencyの性能測定ではない。正確な約-12.04dBはコードの係数からの値で、Discord出力のdBを実測したとは報告しない。
- `cleanup-status.json`：ProofKeyAbsent=true、DiscordPids空、selected0.1.4/oem171/Problem0。一時OwnedBy／QuarterGainの自分のleafだけ撤去。prototype DLLは残るがキー欠落はbit-exact bypass。fallback0.1.5は使っていない。再導入／追加OS再起動なし。

## 最初の比較が無効だった理由

- `pcm-proof-20261007-03/`: ユーザーがStandard／同じfifine入力で基準音量を聴き、通常終了した。componentだけを0.1.2/oem164.infへ更新。ユーザーは「入力はそのままだが、音量に変化なし」と回答。
- 同runの`discord-quarter/`: Discord35936の唯一のactive capture session、非RAW/DEFAULT／audiodg18268の正カウンター10267calls/4928160framesを回収、EventsLost0。ただし新DLLに追加したFixedGain fieldが一切ない。これを0.25処理カウンターとは数えない。
- ユーザー通常終了後、元署名DLLとbyte-identicalなno-opをversion0.1.3/oem165.infとして選択。管理者のread-only module一覧で、audiodg18268がロードしているのは0.1.1の旧DriverStoreパス。SHA256=`b7e1a666bd875253c740773bd22a9b1a31568021a2497d5ffbb977fa983477a4`は元no-opと一致。
- **PnPの選択version更新 ≠ 稼働中audiodgのDLL切替**。今回の差なしは実際に旧no-opで比較した結果であり、AGC補償／RAW bypass／加工PCM不達の証拠ではない。加工版の実A/Bはまだ行えていない。
- componentのみの更新でもruntime IDは7d315d75→b7ff2cbd→3ed01c72へ再生成。名前／StableId／ContainerIdは維持。Discord入力選び直しは行わず、ユーザーは比較時に入力そのままを確認。元IDを復元しない。
- PnP更新直後の一時的な未列挙でhelperは停止し、後のread-only `verify` で回復を確認した。初期FAILED receiptは変更せず保持。次回からhelperはこの特定の状態をREENUMERATION_PENDINGとして記録し、再導入ではなくverifyで再開する。

## 一時切替（ユーザーが明示選択）

旧DLLの解放に再起動が必要なため、ユーザーは「一時切替で続行」を選択。固定版の再導入／再起動をA/Bごとに繰り返さない。

- `GATE_PCM_SWITCH=ON`の実験ビルドのみが使用。通常CMakeはOFF、DSP／ORT／production設定／UIは追加しない。
- 管理者所有の一時key=`HKLM\SOFTWARE\DOT MIC\ApoGate\PCMProof-8F611FC3`、`QuarterGain` DWORD。1なら0.25、0／欠落／型不一致／読み取り失敗なら完全no-op。OwnedByはこのrunの絶対path。試験後にkey撤去済み。
- **LockForProcessだけ**で一度読む。gainはlocked graphの全期間固定。APOProcessは入出力copy＋必要時float32全channel×0.25＋counterのみ。registry／モデル／allocation／wait／log／sample-file I/Oはcallbackにない。gain=1は乗算もせずmemmoveなのでbitsを保持。
- 切替前はDiscordを通常終了し、active captureがないことを確認。設定は次のgraphにだけ適用。既存graphをflag変更だけでライブ変更できると報告しない。
- ProcessLocked／StreamSummaryに実FixedGainを記録。Initializeの値はLock前なのでgain受理の証拠として使わない。実ロードpath/hashも照合し、旧DLLのcounterを取り違えない。
- 本フラグはproduction IPC/CAPX評価の代わりではない。試験終了後はquarter-off→no-opの聴感確認→cleanup-switch。削除はOwnedByと既知value／subkey不在を照合して自分のleafだけ。cleanup後も本DLLはデフォルトno-op。
- 万一のexact元DLL復帰package0.1.5も署名・CAT再生成済みだが未導入。flag-offで復帰できれば、このpackageは使わない。

## オフライン検証と導入

- 署名payload同一DLLをflag1でselftest-quarter、flag0でselftest：正確な0.25、全channel、in-place、silent、invalid、zero-frame／bit-exact bypassがPASS。合成COMテストをDiscord証明に転用しない。
- DLL署名→INF /w→CAT生成→CAT署名、DLL/CAT/member integrity verify PASS。既存の開発certthumbprint7C11C359D556261E70B71829B497ADEC3BBD3C20を再利用。新しいmachine trust／秘密鍵exportはない。
- /install exit0、selectedchild0.1.4/oem171.inf/Problem0。OS再起動前には依然旧no-op module/hash。`prereboot-status.json`：Boot01:54:43、QuarterGain0、SecureBoottrue/HVCI1。管理者wrapperは120秒でtimeoutしたが、子の完了receiptと後続read-only検証で導入完了を確認し、再導入していない。
- 元runのreceiptは最新所有状態ではない。今後はpcm-switch-20261007-04のExpectedDriversを使う。storeには旧161、164、165も残存。Extension162、Microsoft proxy／物理device／他社packageを削除しない。DriverStore直接overwrite／audiodg・service強制停止なし。

## 実施した最短手順（再試験の指示ではない）

1. LastBootUpTime更新、同じphysical name/StableId/ContainerId/3APIを確認。再インストールしない。通常の初期状態はflag0。
2. Discordのactive captureを通常終了後、管理者`pcm-proof.ps1 quarter-on -RunDirectory <最新run>`。metadata trace開始→同じStandard/input/settingsで約5秒同じ声。**入力の選び直し／volume／AGC変更はしない**。別入力へ変わったら条件④未達として停止する。
3. 新DriverStoreのhashとProcessLocked FixedGain0.25、実Discord session ownerと同instance positive summary、ETW欠落0を確認。聞こえた明確な減衰をユーザー確認。gain0.25が確定する前に「音量差なし」を経路不成立と判定しない。
4. Discord通常終了→quarter-off→同じpackage／endpoint／入力設定でno-opテスト。ProcessLocked／summary FixedGain1と元音量復帰を確認。その後通常終了してcleanup-switch。
5. 四条件が全部通って初めてPCM到達PASS。音量差がない場合はAGC等の可能性を記録するが未確認原因を断定せず、設定を勝手に変えない。production DSPへ進まない。

次の最小sliceはユーザー指定の軽量production gain/limiterと完全bypass。DPDFNet2/ORTやUI設定を混ぜず、固定ABI／48kHzHR／STFT960/hop480／alignment／NC OFF停止の契約を維持して順番に進める。今回は到達証明までで、App/Native/models/bridge/元ZIPは変更していない。
