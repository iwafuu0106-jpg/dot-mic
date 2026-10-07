# Capture APO 成立性ゲート（no-op・Standard実行確認済み）

**固定PCM gate完了時点：四条件PASS。** 0.1.4/oem171.infの実ロードで、同じDiscord Standard/input/settingsの0.25倍減衰→no-op元音量復帰とgain別正カウンターを確認。一時フラグ撤去、no-opへ復帰した。[PCM証拠と制限](../docs/apo-pcm-proof.md)を参照。下記0.1.1 ownership receiptで現在の全packageを削除しない。

**その後のproduction作業は [`Apo/` / production状態](../docs/apo-production.md)へ移行。** RT shell/CAPXのcomponent0.2.4を選択済み、実ロードはユーザー再起動待ち。方式成立性は再試験しない。現在の所有receiptはproduction最新runを使用し、この古いhelperで全packageを削除しない。

**2026-10-07: ATL版0.1.1で実音声API正常化、Discord Standardの非RAW／DEFAULT MFX実行ゲートPASS。** audiodg同一Instanceの132060 calls／63388800 framesとDiscord capture session所有／通常終了を相関。声の聴感、DSP、実通話、性能、一般配布は未確認。Secure Boot ON、HVCI ON、TESTSIGNING OFFを維持。既存アプリ／DSP／配布ZIPは置き換えていない。

最新結果は `docs/apo-feasibility.md` の更新条件／COM修正を参照。旧0.1.0 DLLは単体aggregationで同じ0x80040110を再現、使用しない。runtime ID再生成だけでは停止せず、StableId／ContainerId／同じ物理interfaceと原名で確認する。過去のID復元をしない。

## 現在の導入run（0.1.1・反映済み）

`artifacts/apo-gate/development/local-trust-atl-20261007-02/`、証明書thumbprint=`7C11C359D556261E70B71829B497ADEC3BBD3C20`、APO=oem161.inf／Extension=oem162.inf（新DriverStoreパス、0.1.1.0）。旧runとpublished名が再利用されているので、削除時は必ず現在のrun receiptとoriginal INFのパスを照合する。2つのLocalMachine信頼を登録済み。再起動前のAPI成功だけでは未反映だったため、再起動後の実測で判定した。

ユーザー自身が01:54に再起動、その後診断とStandardローカルマイクテストを実施。3段階APIはすべてS_OK、runtime IDは再生成、StableId／ContainerId／物理interface／原名は一致。[実行証拠と制限](../docs/apo-discord-evidence.md)に記録。0.1.1 packageと当該開発信頼は導入状態のまま。Discordはユーザーの通常終了済み、metadata traceも停止済み。一般配布用packageではない。

## このPCの最初の開発試験（0.1.0・撤去済み履歴）

- ユーザーが管理者導入、証明書信頼、必要な再起動／セキュリティ変更を許可。変更を最小にするため、boot/security設定は変更せず先にローカル信頼方式を実施。
- DLLに `/MANIFEST:NO` を明示。合成selftest PASS。30日・RSA3072・SHA256・非exportable秘密鍵の開発Code Signing証明書をCurrentUser\\Myに生成。公開CERだけをexport。
- DLL署名→InfVerif→CAT再生成→CAT署名。管理者が同じ証明書をLocalMachine\\RootとTrustedPublisherへ登録。DLL/CAT AuthenticodeとCAT内INF/DLL整合性verify PASS。
- 実施run: `artifacts/apo-gate/development/local-trust-20261007-01/`。証明書thumbprint `43135D91DFAE47B9FA9D19AA2A36175E51AFBCE2`。公開INF: APO=`oem161.inf`、Extension=`oem162.inf`。署名済み開発payloadはこのrun内のみ。既存unsigned ZIP／packageはそのまま。
- SetupAPIは最初のdriver-policy検証で`0x800b0109`を出すが、その後trusted Authenticode publisher経路で成功。最終import/install成功を確認。これはMicrosoft署名／PETrustロード成功の証拠ではない。
- 接続中の元USB devnodeへのExtension一致を確認。再構成時に `PNP_VetoOutstandingOpen` / `CR_REMOVE_VETOED` のため再起動要求3010。Microsoft inbox driverは保持、物理device無効化／audio-service強制終了はしていない。
- `endpoint-utf8-prereboot.json`: 元Endpoint ID／正確な日本語Friendly Nameが一致。初回prepare/installのJSONはPowerShell5.1によるUTF-8 decode文字化けを含むため、名前証明には使用しない。probeのUTF-8 decodeを修正済み。
- `development.ps1` は `prepare/install/diagnose/remove` の明示操作。boot変更／再起動／自動昇格はしない。削除は保存したpublished INFと現在のoriginal INFの照合後、Extension→APOだけをuninstall。今回追加した2 trust entryだけを削除。

0.1.0では原ID不在・音声API失敗を確認し停止・撤去した。これ以降ユーザーがruntime ID不変条件を変更したため、現在は同じ物理capture／原名を追跡する。仮想endpointや改名は別途判断で、このゲートでは実施しない。

## 構成

- `noop.cpp`: 独自CLSIDのCapture MFX。Windows SDKの `CBaseAudioProcessingObject` を使い、SYSVAD/SwapAPOのcomponentized登録／初期化を参考に独立実装。チャンネルをswapしない、音を変更しない、追加バッファ遅延なし。
- `APOProcess`: 入力をそのままコピー（silent／invalid／in-placeを処理）。待機、heap確保、モデル、DSP、ログI/Oなし。カウンターだけ更新。
- `Initialize` のmode／discoveryフラグ、`UnlockForProcess` の呼出数をTraceLoggingで記録。**音声samplesは記録しない。** フレームごとのログはない。ストリーム終了後のsummaryが必要。
- `inf/DotMic.Fifine.Extension.inf`: 実測HWID `USB\VID_3142&PID_00C1&MI_00` のみ。既存 `GLOBAL` インターフェイス、実測 `KSNODETYPE_MICROPHONE` に限定。全USB／全エンドポイントへの登録なし。
- `inf/DotMic.ApoGate.inf`: `Class=AudioProcessingObject`、private SWC component、component内COM/APO登録、DIRID 13。SFX／EFXなし。
- Windowsの既存署名済みMicrosoft APO Proxyをcomponentとして参照。SYSVAD仮想ドライバーを導入／同梱しない。
- DEFAULT／COMMUNICATIONSは今回のno-op観測用候補だけ。RAWは登録しない。SPEECH／MEDIAは未観測なので追加しない。実modeが別であれば「未対応／未判定」とし、RAWだと推測しない。
- endpoint／物理devnode／interfaceのFriendlyNameを書き換えるINF行なし。APO登録用FriendlyNameは録音デバイス名ではない。

## 再現

Windows x64、VS2019 Build Tools C++＋Microsoft ATL（Microsoft.VisualStudio.Component.VC.ATL）、SDK 10.0.19041、CMake 3.20+。INF検証／CAT生成にはWDK tools 10.0.28000を使用。SDKに欠けるWin11公開ヘッダー4つを `headers.lock.json` のMicrosoft commit／SHA256で復元する（バイナリではない）。既存 `dev.ps1`／製品ビルドは変更しない。

```powershell
./ApoGate/gate.ps1 restore
./ApoGate/gate.ps1 build
./ApoGate/gate.ps1 inspect
./ApoGate/gate.ps1 selftest
./ApoGate/gate.ps1 package
```

`selftest` は **同じDLLをprobeプロセスへ直接ロードする合成試験**。COM登録も物理captureもDiscord接続も行わず、合成COMMUNICATIONSでの無変更コピー／flags／0遅延の契約だけを確認する。実Discord成立性の代用にしない。

生成物: `artifacts/apo-gate/package/` のDLL、2 INF、2 CAT。**CAT生成成功／InfVerif成功は署名成功／導入成功ではない。現在すべて未署名。** `package` はdriver store／registry／証明書／boot設定を変更しない。

## 一般配布／別環境への導入と削除

このPCのローカル開発packageは上記runで試験中。別環境／一般配布では署名条件を別途満たす。未署名の `package/` をそのまま導入しない。自己署名のPnP受理だけでaudiodgのPETrust条件を満たすとは仮定しない。

- 一般配布にはWindowsのdriver signing／保護されたaudio processの署名要件を満たすdriver package／APO DLLが必要。DLL・INF変更後はCAT再生成／署名を行う。
- 開発用には適切なtest signing対応の検証環境を使用できる。証明書信頼、test signing、Secure Boot、メモリ整合性、BitLocker／再起動の影響を含め、ユーザーの明示確認と準備を先に行う。このスクリプトはそれらを変更しない。
- 署名／承認後だけ、明示的に開いた管理者端末で以下を実行する。`/reboot` や強制再起動は使わない。再接続等が必要なら先に確認する。

```powershell
pnputil /add-driver "artifacts/apo-gate/package/DotMic.ApoGate.inf" /install
pnputil /add-driver "artifacts/apo-gate/package/DotMic.Fifine.Extension.inf" /install
pnputil /enum-drivers
```

導入時に返された **この2 packageだけのpublished `oemNN.inf` 名**を保存。削除はextension→APOの順に `pnputil /delete-driver oemNN.inf /uninstall`。名前を確認せず削除しない、`/force`／`/reboot` は使わない。Microsoft inbox proxy／`wdma_usb.inf`／物理endpointを削除／無効化しない。導入／削除後のruntime ID・StableId・ContainerId・物理interface・元FriendlyName・通常マイク動作を確認する。別環境／配布は未検証。

## 一回だけのDiscord縦切り

署名条件／導入が成立した後にのみ実施。

1. `gate.ps1 inspect` で現在のruntime ID・StableId・ContainerId・物理interface・FriendlyNameを保存。Discordで現在の `マイク (fifine Ampli1)` を選択し、表示名も保存。Windows既定デバイスを変更しない。
2. 管理者端末で `./ApoGate/trace.ps1 start`。ユーザーが短いローカルマイクテストを実施。選択endpoint／試験開始終了時刻を記録。録音／第三者通話を勝手に行わない。試験中にsynthetic selftestやcapture-smokeを実行せず、他のcapture clientを混ぜない。
3. マイクテストを停止してから `capture-sessions` で所有PID／state／session IDを読む（captureは開始しない）。Discordがcaptureを保持してsummaryがなければユーザーに通常終了を依頼し、消失を確認。audiodg／Discord強制終了はしない。その後 `./ApoGate/trace.ps1 stop` と `./ApoGate/analyze.ps1 -TraceDirectory <dir>` でmetadataを抽出。前後identityとETW lost events／circular overwriteなしを確認。
4. `DotMic.CaptureApoGate` の実audiodg PID、同一Instance／Endpoint ID、`DiscoveryOnly=false`、観測mode、`StreamSummary.APOProcessCalls>0`／`ValidFrames>0` を対応付ける。合成probe、discoveryのみ、他のmode/clientの実行はPASSに数えない。Discord選択／通話と対応しないカウンターだけではPASSにしない。
5. 正しく登録／導入されてもMFXが実行されない場合、**それだけでRAWと断定しない**。このPCの `Microsoft-Windows-Audio` manifestにはイベント50（APO mode）、127（CreateStreamのendpoint/category/Raw）、128（終了）がある。イベントのPID／ActivityID／関連client情報とDiscord PIDを照合し、**実際に成功したDiscord stream**のRAW要求を別途確認する。123/125のformat queryだけでは実通話RAWの証明にならない。相関できなければ未判定。

**観測した非RAWの実経路だけ**がDSP移植へ進む条件。RAW要求によってMFX/SFXが通らないと確認できたら方式上BLOCKER。「物理名維持＋常にDPDFNet処理済みDiscord音声」は標準Capture APOでは成立しないと報告して止める。EFXへのDPDFNet／Gate強制配置、INFによるRAW→Communications強制、仮想マイクへの無断回帰は禁止。

## ゲート後にだけ進める差分

既存 `Native/` のDPDFNet ABI・state・STFT・source alignment・Gate/Gain/Limiterを再利用。専用workerと事前確保SPSC、source-timeを合わせた固定遅延wet/dry、NC OFF acknowledgement後の推論停止。APOProcessでORTを呼ばない。CAPX property storeを第一候補として既存未パッケージWinUI配布との要件を評価し、衝突時のみ小規模snapshot IPCを比較する。RTQueueへの移動はshort-running実測後だけ。

その後にだけ製品のVB-CABLE bridge／UI／説明を削除し、DSP smoke、実Discord、短いaudiodg／worker性能・fallback確認を行う。現時点では**DSP再利用・設定連携・性能実測は未着手**。合成no-opの0追加遅延をDPDFNet／Discord遅延実測として転用しない。

## ライセンスと配布

独立したgateソースはroot `LICENSE`（MIT）。参照SYSVADをそのままコピー／同梱していない。Windows SDKのAPO/media-type static libraryとstatic VC CRTをリンクし、DLL importはOSのole32／ADVAPI32／KERNEL32だけ。SDK/Build Toolsのredistribution条件はそれぞれの導入ライセンスに従い、SDKヘッダー／lib／ツールをgate driver packageへ同梱しない。

no-op packageにモデル／ORT／KissFFT／libsamplerate／.NET／WinUIは含まれない。本実装が許可された場合、既存 `licenses/README.md` とDPDFNet（Apache-2.0）／ORT（MIT＋third-party notices）／KissFFT／libsamplerate、VC runtime等の条件・noticeを維持し、driver packageのモデル／依存DLL配置とload権限、DLL search、署名／PETrustを個別検証する。INF/CAT作成だけでそれらの配布／load条件が検証済みとはしない。

参考（Microsoft）:
- [SYSVAD/SwapAPO](https://github.com/microsoft/Windows-driver-samples/tree/2dc3fd3a0cc84a2933f2194e7ec0871584979071/audio/sysvad)
- [APO architecture / RAW contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture)
- [Windows 11 CAPX APIs](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects)
