# Community source

現在のソースは未公開の`0.5.0-rc.1`候補です。複数マイクの導入・共通設定・管理Service・Setupを変更し、APOとnative helperも再buildします。旧復旧データのschema/backendと固定配置先は`0.4.0-community`を維持します。対応範囲と未完了事項は[MULTIMIC-0.5.0.md](MULTIMIC-0.5.0.md)に記載します。

Windows x64 / .NET SDK10.0.103 / Visual Studio2019 C++ Build Tools / CMake3.20+ / Windows SDK10.0.19041 を使用した受入snapshotです。WinUI NuGetは`App/packages.lock.json`、外部native/modelは`dependencies.lock.json`、Windows11 API headersは`ApoGate/headers.lock.json`に固定されています。

## 候補ZIPのbuild

source ZIPを全部展開し、通常権限のPowerShellで次を実行します。

```powershell
./dev.ps1 restore
./ApoGate/gate.ps1 restore
./Community/source-build.ps1 -OutputDirectory artifacts/my-community-build -DependencyRoot .deps
```

固定依存を取得し、APO、UI、Setup、Community helperと入口をbuildします。純粋な複数インスタンスfixtureも実行します。Driverのinstall、証明書登録、registry変更、service登録や音声サービス再起動は行いません。

`reference-payload/`は旧受入済みproductionの**署名metadataだけを除いたAPO/inference**と同じmodel/runtimeです。旧lockの14ファイルは保持し、候補APOだけを別buildへ置きます。残り13ファイルのhashは変更しません。候補APOのlockをbuildごとに生成してSetupへ埋め込みます。自己署名証明書の秘密鍵やtrust rootは同梱しません。

DSPの全sourceは`Apo/`、shared inference／固定SRC sourceは`Native/`に含まれます。DSP自体を研究用に再buildする場合は`dev.ps1 restore`と`Apo/build.ps1 build`を使用します。新しいDSP DLLは受入済みpayloadと同一hashになると保証しません。受入済みCommunity Releaseを黙って差し替えず、別候補として検証してください。

出力の`package/distribution/`に`セットアップ.exe`と`DOT MIC.exe`、`内部ファイル/`を置きます。入口は常駐しません。セットアップは昇格し、同意後に保護cache内のSetupを管理Serviceとして登録します。通常UIは昇格せず、設定は共通の保存先から読み取ります。

`package-install.ps1`へ既存のaccepted payload、候補native出力、候補APO、候補lockを明示して再利用できます。旧portable用の`package-ux.ps1`はこの候補には使いません。既存Releaseや出力フォルダーを上書きせず、公開操作は別途許可を得て行います。

`Apo/*stage*`／`ApoGate/development.ps1`等は旧ローカル開発用PnP署名経路です。**Communityの配布・導入には使いません。** Community SetupはlegacyのMFX / DEFAULTのみを適用します。参照PC固有の移行diagnosticは`ReferenceMigration.cs`に分離し、一般Installから呼び出しません。

作業中のbin/obj/.git、private key、端末のRecovery snapshot、マイクPCMはsource archiveに含めません。ライセンスを変更せず、依存物のnoticeも付属します。
