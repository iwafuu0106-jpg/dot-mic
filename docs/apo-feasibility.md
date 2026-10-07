# DOT MIC Capture APO 成立性ゲート — 2026-10-07

## 結論

最新作業の [固定PCM到達証明](apo-pcm-proof.md) は**PASS**。ユーザー再起動後の新DLL実ロードを確認し、同じDiscord Standard／入力／設定で明確な0.25倍減衰→no-op元音量復帰、実gain別の正カウンター、識別情報維持を確認。一時フラグ撤去済み。production DSP／DPDFNet／UI／性能／一般配布はまだ未検証。

**0.1.1再起動後の音声APIは全段S_OK。Discord Standardで同じ物理fifineの非RAW／DEFAULT Capture MFX実行を確認（no-op実行ゲートPASS）。** 同一audiodg／Instance／endpointのsummary=132060 calls／63388800 frames、Discord active session所有PIDと通常終了に相関、ETW欠落0。声の聴感は未確認。DSP移植／旧経路置換／性能／一般配布は未実施。詳細は [実行証拠](apo-discord-evidence.md)。

## 0.1.1再起動後・Discord実測（最新）

- ユーザー再起動後LastBootUpTime=`2026-10-07T01:54:43.5000000+09:00`、Secure Boot=1／HVCI=1。両component Status OK。GetDevice／ActivateIAudioClient／GetMixFormat=`0x00000000`。runtime ID=`<REFERENCE-ENDPOINT-ID>`、原名／StableId／ContainerId／実physical interfaceは導入前と同じ。
- StandardローカルマイクテストとDiscord通常終了をユーザーが実施。DEFAULT／DiscoveryOnly=false、実CreateStream Raw=false、audiodg18268、Instance0x291618126D8の正カウンター。session enumerationでDiscord22216が対象captureの唯一のactive owner、終了後に消失。voice playbackはユーザーが未確認と回答。実通話ではない、Legacyは試していない。
- 証拠は同runの`discord-standard-01/`と`discord-standard-release-02/`、`endpoint-postreboot.json`、`diagnostics-20261007-020033/`。10秒テストだけのcounterではなく保持されたgraphの全稼働値。旧graphの先行summaryは合格判定に使わない。音声保存なし。0.1.1開発package／信頼は残置、追加install／security変更なし。
- traceのlogman重複-pエラーを-pfへ修正。Audio providerをmetadata keywords／Informationに絞り、sequential ETLでcircular overwriteを避けた。`analyze.ps1`はmetadata抽出のみ（自動PASSなし）。probeに読み取り専用`capture-sessions`追加。DSP／App／Nativeは未変更。

## 更新された成立条件・COM修正（2026-10-07 01:21）

- 01:24再導入：APO=oem161.inf／Extension=oem162.inf、version0.1.1.0、新DriverStoreパス。Extension exit3010でcomponentはまだ反映待ち。3つのAPIは再起動前いずれもS_OK、StableId／ContainerId／物理interface／原名は一致。実audio graphで修正成功とはまだ判定しない。セキュリティ設定変更なし。published名が旧runと再利用されているため、削除するなら必ず0.1.1 runのreceiptを使う。

- ユーザーがruntime Endpoint ID不変条件を変更。同じ物理fifine capture／原名を維持し、初回統合時に必要ならDiscordで一度再選択する。過去ID復元／registry直接編集／仮想endpointへの置換はしない。下記の「ID維持FAIL」は当時の要件に対する履歴で、今後の単独停止条件ではない。
- `noop.cpp`: Microsoft SYSVAD型のCComObjectRootEx/CComCoClass/CBaseAudioProcessingObject＋COM_MAP＋DECLARE_AGGREGATABLE＋OBJECT_ENTRY_AUTO＋CAtlDllModuleTへ変更。独自IUnknown/refcount/factoryを撤去。RT copy／counterは変更なし。Microsoft ATL componentを既存VS2019 BuildToolsへ追加（installer最初の2回は残留idle MSBuildによる8006、確認して当該workerを閉じた後exit0）。OS再起動／セキュリティ変更なし。
- 同じ新probeで旧署名DLLはaggregation creation=`0x80040110`、新DLLは通常／aggregated生成、6 interfaceのQI、controlling IUnknown同一性、delegated Release、illegal aggregated IID拒否、unloadがPASS。新署名DLLでもPASS。これは実audio graph成功の代用ではない。
- StableIdは公式SDK NuGet Microsoft.Windows.SDK.CPP 10.0.28000.2705のmmdeviceapi.hから定数を確認。package SHA256=`a74ca8f9af98bd61925d9e2932ad98f746306123167b8f0bba99cd9ea9f03807`。古いbuild SDK用compat定数を `stableid-key.h` に記録。SDKをOSへinstallせず、原ヘッダーを製品packageへ同梱しない。
- 実測StableId=`<REFERENCE-ENDPOINT-ID>`（VT_LPWSTR、HR0）、ContainerId=`<REFERENCE-CONTAINER-ID>`。StableIdは不透明・case-sensitiveで保存。欠落／解決失敗時は同じ実USB interfaceの唯一のcaptureを再列挙する。App/Nativeの永続設定移行は実Discord gate後に行う。
- probeはGetDevice／ActivateIAudioClient／GetMixFormatのHRESULTを別々にJSONへ出力、未到達はNOT_REACHED。現captureの3段階はいずれもS_OK。明示旧IDのGetDevice=0x80070490／残りNOT_REACHED。新runtime IDはIMMDevice::GetIdで取得し、旧IDに依存しない。
- `docs/apo-inf-audit.md`: GLOBAL既存referenceの再利用、2段component/effect登録、microphone限定、改名／追加KS filterなしを確認。0.1.1 signed run=`artifacts/apo-gate/development/local-trust-atl-20261007-02/`。DLL署名→CAT生成→CAT署名、INF /w・/h /vと合成COM試験成功。新証明書thumbprint=`7C11C359D556261E70B71829B497ADEC3BBD3C20`、30日・非exportable。実機再導入前のsnapshotを保存済み。

## 0.1.0再起動後・復旧の実測（撤去済み履歴）

- 保存済みのユーザー確認後、強制終了なしで再起動。LastBootUpTime=`2026-10-07T00:50:51.5000000+09:00`。Secure Boot=1／HVCI=1、TESTSIGNING変更なし。
- 対象USB devnode直下の `SWD\\DRIVERENUM\\{E8D7B791-48A6-4F41-81C5-3936E0643BE7}#DOTMICGATE...`／`#MSAPOFXPROXY...` はStatus OK／ProblemCode=0、driverはoem161.inf／wdmaudioapo.inf。旧InstanceId文字列検索はこれらを見落としていたため修正。
- 元IDはGetDeviceで`0x80070490`。全capture列挙でfifineのIDは `<REFERENCE-ENDPOINT-ID>` に変化。原名は同じ。**導入直後・再起動前の原ID一致は反映後の維持証明ではなかった。** 新IDのGetMixFormatは`0x80040110`（CLASS_E_NOAGGREGATION）。gate Factoryがouterを拒否して同値を返すためCOM aggregation未対応が候補だが、実factory引数のtraceは未採取で確定原因とはしない。署名拒否／RAWとは未判定。
- Discord設定／通話／マイクテストは未実施。移植せず、今回のExtension=oem162.inf→APO=oem161.infだけを管理者uninstall/delete、両方成功。元receiptはinstallation-before-removeファイルで保持。当該thumbprintのRoot／TrustedPublisher不在を確認。MS inbox proxy package／物理device／他社APOは削除していない。
- 撤去後の現在IDは `<REFERENCE-ENDPOINT-ID>`。原名・同一USB topology・48kHz/2ch/FP32の形式取得が回復。0.5秒の通常shared capture smokeで23,520フレーム、Start/GetBuffer/ReleaseBuffer/Stop成功。samplesは読まず保存／再生なし。**元IDは未復元。Discordの以前のdevice選択は再確認が必要。**
- 証拠は同runの `capture-endpoints-postreboot.json`、`endpoint-replacement-postreboot.json`（失敗前の部分stdout、完全JSONではない）、`admin-remove.txt`、`installation.json`、`capture-endpoints-after-remove.json`、`endpoint-recovered-current.json`、`capture-recovery-smoke.json`。停止した自分の診断PowerShellと孤立probeだけを終了。音声サービス／audiodg／ユーザーアプリは終了していない。
- probeに `list-capture`（property読み取りのみ）、明示IDの`inspect`、0.5秒の明示ID`capture-smoke`を追加。既定targetを新IDへ自動変更しない。diagnoseのCI読取は最近1000件に限定してDotMic/audiodgのみ保存、不在を署名成功証明にしない。子probeのstdout/stderr非同期読取と20秒期限を追加。
- 次はCOM aggregationとEndpoint ID再生成原因を分離して修正する必要がある。**現packageを再導入しない。ID維持条件を緩和したと仮定せず、別IDでDiscord成立を報告しない。** Secure Boot OFF／TESTSIGNING ONはこの2問題の修正ではないため未実施。

以下の表も撤去済み0.1.0当時の記録であり、現在の0.1.1判定は冒頭を参照。

| 項目 | 実測／実施結果（0.1.0履歴） |
|---|---|
| ユーザー指定対象 | fifine Ampli1 |
| 元Endpoint ID | `<REFERENCE-ENDPOINT-ID>`（反映後に不在） |
| 撤去後の現在Endpoint ID | `<REFERENCE-ENDPOINT-ID>`。元ID維持FAIL／未復元 |
| 元のFriendly Name | `マイク (fifine Ampli1)`（endpoint property store、VT_LPWSTR） |
| PnP HWIDs | `USB\VID_3142&PID_00C1&REV_0100&MI_00`、`USB\VID_3142&PID_00C1&MI_00` |
| INF target HWID | `USB\VID_3142&PID_00C1&MI_00` のみ |
| 物理devnode | `USB\VID_3142&PID_00C1&MI_00\9&28ED31B4&0&0000` |
| 元driver | `wdma_usb.inf` / `usbaudio` / `USBAudio` / 10.0.26100.9457 |
| System.Devices.AudioDevice.RawProcessingSupported | **true**（IMMDevice property store、HRESULT=0、VT_BOOL=11）。Discordの要求modeとは別の能力値 |
| Mix format | 48 kHz / 2ch / FP32、blockAlign=8（capture開始はしていない） |
| 既存interface | AUDIO／CAPTURE `GLOBAL`。接続先AUDIO interfaceをIDeviceTopologyで照合 |
| 実hardware connector subtype | `KSNODETYPE_MICROPHONE` = `{DFF21BE1-F70F-11D0-B917-00A0C9223196}` |
| Discord | 実行中、1.0.9260。対象選択／通話／modeは未検証 |
| Discord観測AudioProcessingMode | **未観測**。合成probeのCOMMUNICATIONSを転用しない |
| 実Discord APOProcess | **未検証**。component登録成功、形式取得失敗、撤去済み。RAW bypassとは判定しない |
| APO位置 | no-op **Capture MFX候補**。実経路採用はゲート後だけ。SFX/EFXなし |
| package | unsigned handoff保持。自己署名DLL＋2 CATのPnP受理／反映後component登録成功。その後oem162.inf／oem161.infと追加信頼を撤去 |
| DSP再利用範囲 | 現時点0（ユーザー指定ゲート順守）。既存Native/DSPは保持。本実装に進む場合のみ再利用 |
| CPU／実遅延 | **未測定**。合成no-op GetLatency=0／480→480 framesの契約を確認しただけで、音声経路性能ではない |
| 元名／ID維持 | Windows名は反映後／撤去後も同じ。**Endpoint ID変化でFAIL、Discord未検証** |

Compatible IDs（物理USB audio devnodeから取得。INFの対象としては**使用しない**）:

```text
USB\COMPAT_VID_3142&Class_01&SubClass_01&Prot_00
USB\COMPAT_VID_3142&Class_01&SubClass_01
USB\COMPAT_VID_3142&Class_01
USB\Class_01&SubClass_01&Prot_00
USB\Class_01&SubClass_01
USB\Class_01
```

## 初期unsigned packageの検証（履歴）

- Release x64 no-op DLL／read-only probeビルド。製品Native／App／モデルは再ビルドしていない。
- 同一DLLのローカルCOM生成／IAudioSystemEffects3、合成48 kHz 2ch FP32のbit-exact copy、in-place、silent、invalid、zero-frame、latency/frame契約、COM解放。**PASS（ローカル合成の範囲のみ）**。
- WDK 10.0.28000 `InfVerif /w` と `/h /v` で2 INFはVALID。
- `Inf2Cat /os:10_NI_X64,10_GE_X64` はerrors/warningsなし。2 CAT生成。
- `signtool verify /pa` はDLL／CATに **No signature found**。配布可能署名とはしていない。
- `dumpbin`: DLLはx64、CFG/ASLR/NX、APOProcessはRT_CODE、importはole32／ADVAPI32／KERNEL32のみ。DSP／ORT依存なし。
- PowerShell build／traceスクリプトparse PASS。**traceの実セッションは未実行**。

## 2026-10-07 ローカル開発署名試験（以下は再起動前の履歴）

ユーザーが管理者導入・信頼証明書・必要な再起動／Secure Boot変更等を明示許可。まずWindows 11 build 26200、Secure Boot ON、HVCI ON、TESTSIGNING OFFのまま試した。DLLは `/MANIFEST:NO`、30日・RSA3072/SHA256・非exportable開発秘密鍵はCurrentUserにのみ保存。公開CERを管理者がLocalMachine Root／TrustedPublisherへ登録。

DLL署名→InfVerif→CAT生成→2 CAT署名→DLL/CAT Authenticode検証／CAT内INF/DLL整合性検証成功。管理者`pnputil`で2 package受理。公開名はAPO=`oem161.inf`、Extension=`oem162.inf`。SetupAPIにはdriver-policy検証0x800b0109の後、trusted Authenticode publisher経路成功、最終import/install SUCCESSが記録されている。

接続中の元devnodeにExtensionは一致。しかし再構成で `PNP_VetoOutstandingOpen` / `CR_REMOVE_VETOED`、exit3010。APO childはまだ現れず、必要な設定反映を待つ。BitLocker OS volumeは暗号化されていない（statusだけ取得、recovery key未取得）。boot/security設定変更なし、物理device無効化なし、audiodg/audio-service強制停止なし。

`endpoint-utf8-prereboot.json`で元ID／`マイク (fifine Ampli1)`完全一致。初回prepare/install JSONの名前はPowerShell5.1のUTF-8 decode文字化けなので証拠に使わない。development/trace/inspectのdecodeを修正済み。

これは**設定反映／再起動待ち**。PnP受理はAPOロード成功ではない。RAW要求も未観測なので方式不成立としない。再起動後にcomponent／CI／原名を確認し、実Discord Standardを1回。Standardの成功streamがRAWと確認された場合だけ、存在するLegacyを1回確認。両方RAWなら方式BLOCKERで移植を止め、仮想endpoint／改名は別途選択する。EFXへの非線形処理やRAW強制変更はしない。

## 残る互換性制約／次の最小ステップ

- このHWID、x64、Win11 22621+を対象とする。HWIDが同じ他のfifine個体にも一致し得る。任意マイク対応／グローバルAPOは設計・実装していない。
- INF検証は実interface FX attachment／APO loadの証明ではない。今回は元name／同じ物理capture、実audiodgのnon-discovery modeとAPOProcess summary、Discord streamとsession所有／終了の相関を確認した。他環境への保証ではない。
- MFX不実行だけでRAWと断定しない。登録・enhancement・mode support・署名・load失敗等と区別し、metadata ETWの実stream raw flagを相関確認する。format query／discoveryのみでは不十分。
- `ApoGate/README.md` に導入・対象2 packageだけの削除、metadata-only trace、判定条件を用意。通常アプリ起動時のelevation／APO設定UIは追加していない。
- 成立後だけCAPX設定連携、非同期worker／delay-matched dry、DPDFNet再利用、旧VB-CABLE削除、DSP smoke／実Discord／短い性能確認へ進む。既存CPU測定をAPO＋Discord実測とは報告しない。

## 証跡

- 旧経路復帰用ローカルGit基準: `75224880121d6ea652567998b708237aa0d6b55f`（外部公開なし）。
- `artifacts/apo-device-properties.json`（PnP HWID／compatible IDs等）、`artifacts/apo-device-interfaces.json`（実GLOBAL reference）。
- `artifacts/apo-gate/endpoint.json`（IMMDevice/IDeviceTopology）、`environment.json`（署名／環境）。
- `artifacts/apo-gate/verification.log`、`package.sha256`、`package/`。
- `artifacts/apo-audio-events.json`（このOSのmode／RAW関連イベント定義。実Discordイベントではない）。
- `artifacts/apo-gate/development/local-trust-20261007-01/`：署名済み開発payload、公開CER、hash/state、署名／検証log、管理者transcript、published INF receipt、SetupAPI／CI／boot状態、UTF-8の再起動前原名。thumbprint=`43135D91DFAE47B9FA9D19AA2A36175E51AFBCE2`、期限2026-11-06。秘密鍵／PFXは配布しない。
