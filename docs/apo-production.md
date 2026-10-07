# Production APO

方式成立性は `apo-pcm-proof.md` のPASSで終了。再検証しない。

実装順：A RT shell → B CAPX Gain実機反映 → C audiodg依存load proof → D非同期NC → E既存UI → F実測 → G旧bridge撤去。

## 現在：実Discord受入・60秒性能PASS、旧bridge撤去済み・ローカルRelease

最新run／操作ownership=`artifacts/apo-production/runs/worker-ui-20261007-03/`。実受入=`discord-live-nc-01/acceptance.json`、性能=`performance.json`／`performance-interpretation.json`、撤去=`g-result.json`。D2単体試験／D3既存Main-tray証跡は再利用。A/B/Cの成立性は再証明していない。

- Component **0.3.4.0 / oem186.inf** 選択、Problem0。Extension **0.2.2.0 / oem175.inf** を保持（CAPX schema同一、component-only更新）。最新package内のExtension0.3.4は未導入。
- 署名済みDLL SHA256=`9F82245EA0E5061AD30317251F6EF796B82D5C10D876BB3B66FF1AF4C282B20B`、DriverStore=`dotmic.apogate.inf_amd64_f3ea9468002a2bf2`。**実audiodg17952で0.3.4 hash一致**。新payloadの処理を確認してから実NC試験を実施。撤去・ReleaseでAPOの再署名／version変更／再導入はしていない。
- ユーザー自身の最新再起動は`2026-10-07T14:27:04.5000000+09:00`。初回verifyはcapture前のNOT_YET_LOADED、元Discord入力を使用開始してからNEW_MODULE_HASH_MATCH。OS再起動／予約／service終了／audiodg強制終了はエージェントから行っていない。
- User storeの安全値：MasterBypass1、GainDb0、GateEnabled0、NcEnabled0。Gate閾値-48、hysteresis6、attack5/hold160/release120ms。再起動後の新shellもまず遅延一致のBypassになる。
- Friendly Name／StableId／ContainerId／physical GLOBALは保持。runtime endpointは`<REFERENCE-ENDPOINT-ID>`。レジストリで戻していない。最新0.3.4でも元のfifine入力・Discord設定維持をユーザーが確認。
- **D2実worker／D3既存UI／D4実Discord／D5性能／D6条件付き撤去PASS**。このPC向けRelease x64の実装・実利用確認を完了。一般配布certificationは未実施で別条件。自己署名cert期限2026-11-06、既存trust再利用、購入／Store公開／MSIX化なし。
- 最終更新のUACを一度キャンセルしたため自動再試行せず停止。ユーザーが再承認・管理者PS委任を明示し、同じ未試行runで0.3.4導入を完了。管理者PSは導入／実DLL検証／メタデータ測定のみに限定。一般アプリの昇格、汎用管理者常駐service、security/boot設定変更はしない。
- ローカルRelease：`artifacts/apo-production/releases/0.3.4-local.zip`、手順書=`docs/apo-release.md`。元RC ZIPも保持。最新source／hash／phaseは最新runに保存。削除操作のownership正本はこのrunで、ZIP内のreceiptコピーから操作しない。

## 実装と確認

`Apo/processing.h`：Master Bypass、最新targetへの20ms amplitude ramp、既存Gateのpower-domain detector／hysteresis／attack/hold/release、-1dBFS/3ms/60ms channel-linked sample-peak limiter、bounded atomic parameter snapshot、meter/counters。powは非RTの`prepare`のみ。Lock時にdry／lookaheadを確保。無変更のraw用ringを常時維持し、Bypass中はGain/Gate/Limiter演算を停止。解除時はbuffer内のpeakも同じlinked windowへ復元する。

`Apo/apo.cpp`：既存private CLSIDのATL MFX、48kHz FP32 mono/stereo、GetLatency、CAPX `IAudioProcessingObjectNotifications`、非RT property read/validate/linear化/publish。APOProcessにはheap/I/O/lock/wait/COM/ORT/loggingがない。SILENT入力にも遅延中のPCMが残るため出力flagを入力からコピーしない。

`Apo/settings.h`：context=`{7DD39E65-9848-4CDE-AF2A-910E9D160376}`、property fmtid=`{91795F52-2DC0-4E20-A732-58816672E635}`。pid1..9はMasterBypass/GainDb/GateEnabled/GateThresholdDbfs/GateAttackMs/GateHoldMs/GateReleaseMs/NcEnabled/GateHysteresisDb。値の優先順はVolatile→User→Default。dragはVolatile、確定はUserへcommit後、同じpropertyのVolatileだけVT_EMPTYで解除。NcEnabledは新workerのrequested stateに接続し、effective stateを別診断値にする。

INFはDOT MIC contextのDefault値とUser／VolatileのKEYONLYを定義。CAPX Open*はstoreを新規作成しないため、最初のDefault-only候補0.2.0はUser/Volatile openでFILE_NOT_FOUND／ACCESS_DENIEDだった。この実装ミスを0.2.2で修正。管理者／非管理者のAPI-specific診断を保存し、schema修正後は**non-admin token（ElevatedAdministrator=false）**で全store Read/ReadWrite、Volatile gain-6、User gain0確定とoverride解除が通った。権限回避やendpointレジストリ編集はしていない。

確認済み：

1. Bypassの任意packet長でのbit-exact PCM（signed zero/NaN payloadを含む）と3984sample遅延。
2. Gain0/+6、20ms rampの隣接sample勾配。
3. linked limiter ceiling／channel ratio。レビューで発見したBypass解除直後のhot lookahead→silenceも回帰PASS。
4. Gate定常open/close、上記処理中のheap allocation0。
5. production DLLのGetLatency一致と、SILENT入力時にdelayed PCMを失わないこと。
6. 通常権限のCAPX書込／読戻し／override解除、malformed GainDbを修復して元User値を戻す回帰。
7. INFVerif、Inf2Cat、DLL/CAT署名とCAT member検証。新metadata ETW工具のsynthetic CLI hostでの抽出／停止確認（11calls/5280frames、**audiodg測定ではない**）。
8. **0.2.4実audiodg17492／Discord22028**：同じ開いたgraphで通常権限CLIのGain0→−6を消費（amplitude0.501187、positive counters）、ユーザーが即時減衰を確認。Gain−6を残したBypass ONで元音量へ戻ることを確認。Gate−10dBFSで普通の声の抑制→Gate OFF／閾値−48／Gain0／Bypass ONの復帰も確認。31410calls／15076800frames、Nonfinite0／SnapshotMisses0、ETW loss0、通常終了。これは性能計測ではない。
9. **0.2.6 C：実audiodg7104／Discord17520**で依存DLLロード、固定SHA256モデル、metadata／56436 state／FFT960／hop480、CPU ORT1.23.2・1/1・SEQUENTIAL・spinning OFFのSession作成とfixture推論がPASS。Discordが作成した2つの短いgraphそれぞれ1回（合計2回）、Instance／endpoint／PIDでProof→Lock→Summary照合。合計2797calls／1342560frames、loss0。User安全値はgraph再開後も反映、Bypassの声をユーザー確認、通常終了、trace停止。

三並列レビューの指摘を修正：Bypass解除linked保護、単一validation契約／不正設定修復、OEM名再利用時の削除拒否、実際に試みたINF以外の追加を所有として採用しないdelta検証、rollback target別のhash/version検証。削除を実行せず、純粋guard fixtureで意図したcomponent許可／未試行Extension拒否／再利用OEM名不一致を確認。実rollback/uninstall試験はしていない。

実audiodg worker／UI→同じDiscord graph／実機OFF停止／Limiter packet sample-peak／通常通話／APO性能は今回確認済み。hardware xrun counterそのもの、true-peak、Gate全時間境界の実機matrix、別PC／全format／長期互換性は追加検証していない。これらを今回のPASSに含めない。過去のno-op/Fixed0.25方式検証、RAW/mode matrixは再実行しない。

### C実装の限定範囲（終了済みの履歴）

`Apo/inference.cpp`の別DLLは既存`Native/model.h`を無変更で再利用。`inference-loader.h`はpackage-relative＋System32限定のDLL検索で、モデルhash検証→Session→既存FFT→fixtureを準備スレッドで実行。Cのbuild flag `APO_ORT_LOAD_PROOF`はLockごと1回だけ実行し、Sessionを破棄してsidecarをunloadしてからAPOProcessへ進む。マイクPCMを推論しない。fixtureは既存合成fixture先頭の**無音startup 1 hop**とreferenceで、maxError0は追加の品質検証ではない。過去の全stream数値照合を保持する。

当時のロード失敗は段階／HRESULTを記録してdry shellを継続した。C成功でprotected-audiodg依存阻害の追加調査は不要。**現在はLockのfixture hook／build flag／package内fixtureを撤去済み**。新workerのlive jobsへ置換し、NC OFF中にfixtureを実行しない。Cの成功を製品NC-OFF停止契約の証拠と混同しない。

依存はcomponent INFのDIRID13へ配置。ORT／VC14.39 DLLの元Microsoft署名を保持し、自作DLL/CATだけ既存certで署名。Native／models／元ZIPは保持。Appは今回CAPX backendへ接続したが既存UI／tray／motionを維持。全component payloadのCAT membershipと導入先hashを照合。C候補sourceは旧`ort-load-20261007-01/source/`へ保存済み。

### D1／D2：transportと実worker

`Apo/async-path.h`にcapacity4のpreallocated job/wet SPSC、480frame assembler、source position／generation照合、hop先頭でのwet採用、遅着・旧世代wet破棄を実装。dryは既存Shellのbit-exact ringを共有する設計で、selector3840＋既存limiter144の契約は変更しない。job overflow／discontinuity／有効NC切替で世代を更新し、OFF中は新jobを出さず同時刻dryを返す。RTのqueue走査は最大capacity＋pending1に制限。

`DotMic.ApoAsyncCheck.exe`の決定的fake workerで、1/137/71/480/960のpacket分割、source整合、whole-hop fallback、遅着wet非再生、世代不一致、partial-hop enable、job/wet overflow、OFFのsigned-zero/NaN dry bitsと新job0、RT heap0を確認。結果=`ort-load-20261007-01/d1-transport/result.json`。cache-line paddingのC4324 warningは意図したalignmentによるもの。

現在は`Apo/nc-worker.h`経由でAPO selectorへ接続済み。GetRealTimeWorkQueueのIDはAPOInitSystemEffects3のserviceから取得し、失敗時はNCだけUnavailable/Fault、Gain/Gate/Limiter/dryを保持する。独自MMCSS thread poolはない。RTはpreallocated job publish＋precreated event signalのみ。RTQueue callbackはmutexでSession/stateのsingle ownerを保証し、1 hop／callback→wet publish→残jobだけ再queue、空ならevent waiting item。10ms polling／空のRun／常時busy loopはない。

Session／RNN／STFT／OLA／FFT／tensor／scratchはworker所有で初回ON時に作成、OFFでもSession保持。新generationごとmetadata initial stateからresetし、現live inputからだけwarm-up。出力tagはinput位置−alignment2400とaudio epoch／NC generation。20ms（960sample）crossfadeのON後にeffective On。OFF要求は新jobを止め、公開sequenceを凍結し、要求前に既に公開された同時刻wetだけでdryへfade。要求後／in-flightのwetは採用しない。idle＋fade完了でeffective Offとし、その後Run/STFT/ISTFT/state更新/job数を増やさない。

overflow／Run失敗／nonfinite／source不連続はgenerationを無効化、FaultBypassed／aligned dryへ。自動retryなし、明示OFF→ONで再試行。遅れた会話のcatch-upはしない。Shellの固定3984samplesとbit-exact Master Bypassを保持。NCのmono入力は旧製品defaultのchannel0、engineのmono/stereo transportへ同じmono wetを配布する。

callbackの寿命をAPO DLLのATL module lockで保持し、待機解除→callback自然終了→worker所有Model破棄→async-result cycle解除の順に停止。APOProcessは待たず、待機はgraph停止後の非RTだけ。0.3.0→0.3.2はOFF境界／notification競合、0.3.4はmodule寿命保護のcomponent-only修正であり、cache対策の再導入ではない。

最小試験（standalone real CPU model＋Windows RTWorkQ、**audiodg実測ではない**）：
- T1：79 Runs、74 wet publish、34080 wet採用frames、generation／epoch一致。
- T2：effective Off時79/79/79/79/79（Run/STFT/ISTFT/state/job）が、音声を1.2秒流しても不変。
- T4：一度のON→OFF→ONでgeneration3→6、state reset2、wet再採用59040frames。
- T3：workerのみ100ms遅延1回、RTcallback最大0.0721ms、heap0、303callback継続、同時刻dry、stale wet非再生。
- OFF境界修正後はT2だけ関連回帰：81/81/81/81/81が1.2秒不変。T3/T4や成立性の再証明はしていない。

### D3：既存UI

`Apo/control.cpp`／`App/ApoSettings.cs`が通常権限CAPXの薄いABI。既存AudioViewModelをshared state sourceへ接続し、capture/render engineは起動しない。Gainの20ms latest値Volatile追従、操作後400msのUser確定、個別override解除。Gate詳細／NC ON-OFF／menu Master Bypassも同じstate。UI側5Hz meterは可視時だけ、hidden時はproperty polling0。p99 sortは通常meterでは行わず性能要求時だけ。NC表示はOFF／準備中／ON／原音。モデル／strength／threadの操作を追加していない。

実Main/tray UIAでGain−6→tray0、Gate／NC／Bypass write・User commit・共有state・安全復帰・通常Exit PASS。このD3証跡は実音声とは区別し、今回のD4で実音声反映を追加確認した。startupのCAPX読込待ち／余計な接続dialogが出ないこと／通常ExitもPASS。旧ケーブルsetupはD3では到達不能のまま保持し、実受入後のGでソースごと撤去した。

## 操作

通常権限：

```powershell
./Apo/build.ps1 build
./Apo/build.ps1 check
$control = "$PWD/artifacts/apo-production/build/Release/DotMic.ApoControl.exe"
& $control get
& $control set GainDb -6 volatile
& $control set GainDb 0             # Userへ確定し、このGainのVolatileだけ解除
& $control set MasterBypass 1
& $control meters                 # 非RTの要求→CAPX診断値。音声を録音しない
& $control nc                     # requested/effectiveとRun／wet／OFF停止counter
& $control performance            # non-RTのhop mean／p99／callback tick等
```

`meters`は最後のpacketのpeak／gainと累積calls/frames。複数instanceの全体集計ではない。pid90通常meter、91性能、92明示diagnostic NC fault。pid150..168はNC状態／counts／callback ticks、175..176はhop mean/p99。Cの130..133は履歴で新処理から更新しない。property writeは非RTだけ。nc State=0Off／1Starting／2On／3FaultBypassed／4Stopping。

管理者で、全captureを通常終了してから：

```powershell
$run = "$PWD/artifacts/apo-production/runs/worker-ui-20261007-03"
./Apo/deploy.ps1 verify -RunDirectory $run
# 問題時にだけ元の署名no-opを選択（実ロード反映には自分で安全な時刻に再起動）
./Apo/deploy.ps1 rollback -RunDirectory $run
# このproduction ownership chainの追加だけ除去。元のgate/trustは保持
./Apo/deploy.ps1 remove -RunDirectory $run
```

最新rollback0.3.5は元署名no-op DLLとbyte-identical。旧fallbackも元runに保持するが、最新ownershipで操作する。追加trustを作成／削除しない。rollback/removeは必要時のみで、未実行。部分失敗／所有conflict時はreceiptを保持して停止し、未照合packageを削除・採用しない。旧contextのUser設定は保持されうるが元no-opは読まない。

## D4／D5：0.3.4実音声と通常通話

- 実audiodg17952／hash一致、Discord capture owner25236、元入力・設定維持ユーザー確認。metadata ETWは67provider events、loss0、停止済み。RTQueue service／GetRealTimeWorkQueueともS_OK、queue ID327681、ProcessLocked instance`0x2363B151B98`、stereo transport、Delay3984。capture graphは通話利用中のため強制終了せず、閉鎖summaryがなくてもlive counters／hash／session／user観察を用いた。PCM記録なし。
- 既存Main UIでGain−6を操作しactive graph consumed0.501187。Gate−10 ONは非zero入力を伴うcloseを4snapshot、OFF復帰を3snapshot確認。Master Bypass反映も同じgraph。旧FixedGain成立性試験は実施していない。
- 最初の実NC On：Run384／wet publish379／wet adoption180480frames、Fault／InvalidPacket0。effective Off時Run／STFT／ISTFT／state／job4098の全値が継続audio1.3秒後も不変、capture79200frames進行。再ONでgeneration3→6／state reset2／wet再採用。
- Limiter：初回は入力不足で減衰なし（最大0.816424）として**未作動**を保存。返し音停止をユーザーが確認し、普段の声を依頼して関連項目だけ再試行。18snapshot中13で減衰、input最大0.0716553／output最大0.891251／minLimiterGain0.228686。Gainは必ず0へ復帰。診断packet sample-peakでありtrue-peakやPCM録音検証ではない。
- NC diagnostic fault1回：UI「原音」、captureと出力継続、追加Run／wet採用0。自動retryなし、明示OFF→ONでmetadata reset／generation12／Onへ回復。standalone100ms stallは繰り返していない。
- 通常通話開始と、声が相手に届く／重大な音切れ・欠落なし／元入力・設定維持をユーザーが確認。60.016秒1回だけ測定しRun+6021／wet2890080frames／dry fallback0／wet adoption100%／queue high-water1／FaultDelta0／InvalidPacketDelta0。累積Fault1は事前の意図的診断faultであり自然障害ではない。
- graph-cumulative hop mean1.34622ms／p991.9437ms。audiodg0.190833 core、可視UI0.016662 core。private audiodg38.508MiB（区間−286720bytes）／UI154.824MiB（+851968bytes）。QPC frequency10MHz、callback elapsed累計0.1919496s／graph最大0.196ms。QPCをpure CPUと混同しない。hardware xrunはAPI非公開で、InvalidPacket proxy／ETW loss／ユーザー観察を別々に記録した。
- 性能に問題がなく、optimization変更を作らない。モデル／GPU／thread数／quantization／hop変更なし。一時設定はBypass1／Gain0／Gate0／NC0へ戻した。Discord／audio serviceは強制停止せず、owned UIとtraceだけ通常終了した。

## D6／GとRelease

全実受入と通常通話PASS後、`pre-g-source/`＋hashを保存して実行。`Native/engine.cpp`のendpoint Stream／capture→render／出力ASRC、`Native/core.h`のClock servo、Native endpoint API、Appの旧P/Invoke／endpoint選択／ケーブルsetup／default-followを削除。製品はCAPX UI＋APOのみ。固定比SRCを含むoffline fixtureはoptional targetとして保持しcompile PASS、NC fault testsは再実行しない。`Native/model.h`のfrontend／backend／state／FFTとモデル本体は無変更。

root build／freeze／packageはAPO経路へ更新し、古いNative DLLのwildcardコピーと元ZIPの上書きを廃止。新UIのRelease buildとMain/tray shared state／CAPX read／ABI44・64／通常Exitは関連smoke PASS。測定済み署名済みAPO payloadをbyte-identicalで再利用し、再導入・再署名は不要。元ZIP／歴代evidence／撤去前source／original no-op rollbackを保持。

固定遅延契約は `Apo/processing.h:ProcessingDelay`。48kHz、alignment2400 + assembler480 + scheduling reserve960 + linked limiter144 = 3984 samples / 83ms。NC/Bypassによらず同一。今回の60秒区間ではreserve内でwet採用100%だったが、別PC／負荷全条件を保証する測定ではない。

ローカルReleaseの完成と一般配布署名は区別する。一般配布certification／Store／MSIXは未実施。実機受入前RCは過去snapshotとして保持し、現在の完成判定は最新`phase-result.json`と`release-final.json`を参照する。
