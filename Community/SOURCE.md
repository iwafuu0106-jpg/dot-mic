# Community source

Windows x64 / .NET SDK10.0.103 / Visual Studio2019 C++ Build Tools / CMake3.20+ / Windows SDK10.0.19041 を使用した受入snapshotです。WinUI NuGetは`App/packages.lock.json`、外部native/modelは`dependencies.lock.json`、Windows11 API headersは`ApoGate/headers.lock.json`に固定されています。

## UI / Setup / Community controlのbuild

source ZIPを全部展開し、通常権限のPowerShellで次を実行します。

```powershell
./Community/source-build.ps1 -OutputDirectory artifacts/my-community-build
```

不足する固定API headersとNuGet依存を取得し、UI、Setup、Community control helperをbuildします。Driverのinstall、証明書登録、registry association変更、音声サービス再起動は行いません。

`reference-payload/`は受入済みproductionの**署名metadataだけを除いたAPO/inference**と同じmodel/runtimeです。独立embedded lockで14ファイルを照合し、DSPはこのbuildでは変更しません。`provenance.json`に元署名payloadのhashと、checksum／certificate tableを除いた同一PE imageの証拠があります。自己署名証明書の秘密鍵やtrust rootは同梱しません。

DSPの全sourceは`Apo/`、shared inference／固定SRC sourceは`Native/`に含まれます。DSP自体を研究用に再buildする場合は`dev.ps1 restore`と`Apo/build.ps1 build`を使用します。新しいDSP DLLは受入済みpayloadと同一hashになると保証しません。受入済みCommunity Releaseを黙って差し替えず、別候補として検証してください。

`Apo/*stage*`／`ApoGate/development.ps1`等は旧ローカル開発用PnP署名経路です。**Communityの配布・導入には使いません。** Community SetupはlegacyのMFX / DEFAULTのみを適用します。参照PC固有の移行diagnosticは`ReferenceMigration.cs`に分離し、一般Installから呼び出しません。

作業中のbin/obj/.git、private key、端末のRecovery snapshot、マイクPCMはsource archiveに含めません。ライセンスを変更せず、依存物のnoticeも付属します。
