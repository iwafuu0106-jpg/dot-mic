# DOT MIC 0.4.0-community

Windowsの物理マイクendpointを維持したまま、Gain、Noise Gate、DPDFNet2 Noise Cancellation、Limiterを適用するWindows向けマイク処理ソフトです。Discord等では原則として元の物理マイクをそのまま使用します。VB-CABLEやcapture-render bridgeは不要です。

## Requirements / Install

- Windows 11 x64。
- 配布ZIPをすべて展開し、`DotMic.Setup.exe`でマイクを選択して変更内容を確認します。同意した場合だけInstallします。
- Install / Repair / Uninstall / Recoveryは管理者権限。通常の`UI/DotMic.App.exe`は一般ユーザー権限です。
- 初期値は **Bypass ON / Gain 0 dB / Gate OFF / NC OFF**。効果を使う場合はBypassをOFFにしてください。

## Community notice

`0.4.0-community`はゼロコスト配布のため、Windows Audio APOのlegacy integrationを使用します。**Microsoft認証／WHQLではありません。**

未署名APOを利用するため、SetupはWindows全体のProtected AudioDG関連互換設定（`DisableProtectedAudioDG=1`）を説明し、必要な場合に変更します。一部のDRMコンテンツやsecure audio path対応アプリへ影響する可能性があります。導入前から1の場合は新たな変更とは扱いません。

Authenticode署名／SmartScreen reputationを保証しません。Windows SmartScreen等の警告が表示される可能性があります。Secure Boot OFF、Memory Integrity OFF、TESTSIGNINGや自己署名Root追加を要求しません。

既存MFX効果の置換や音声サービスの一時停止が必要な場合は、Setupに表示して同意を求めます。APO／model／runtimeは保護された固定Program Filesへ配置します。

## Recovery

- Repair：通常UIの統合状態通知から、または`DotMic.Setup.exe --repair`で開始します。
- Uninstall：SetupのUninstallを使用します。Protected Audio設定をDOT MICが変更した場合は、復元／維持を選べます。
- 失敗時は保存したsnapshotへRollbackします。`Recovery Required`の場合はsnapshotを消さず、SetupのRecoveryを使用してください。
- 詳細は配布の`SECURITY.md`（repositoryでは`Community/SECURITY.md`）を参照してください。

## Source / licenses / acceptance

source ZIPとrepositoryのbuild方法は`Community/SOURCE.md`、第三者license／noticeは`licenses/`にあります。ONNX Runtime、DPDFNet2 model、KissFFT、.NET／WinUI／MSVC runtimeを同梱します。Equalizer APOの実装コードは取り込んでおらず、runtime dependencyでもありません。

既存の参照PC受入結果（Discord、NC wet／OFF停止、再起動、Uninstall／Rollback／Repair）を再利用しています。公開用の概要は`evidence/README.md`にあり、端末固有のraw証拠は公開しません。全デバイスでの動作保証ではありません。

Release識別子は`0.4.0-community`、Git tagは`v0.4.0-community`です。Setup表示の「Community Setup 0.4.0」と`UI/community.json`はこのReleaseを識別します。通常UIに独立したversion表示はありません。受入済みUI assemblyの`3.2.0`は共有UI componentのversionであり、Release番号ではありません。公開準備ではUI／SetupのCodeView内の個人PDBパスだけをファイル名に置き換え、実行コード・動作・DSP／modelは変更していません。

旧`0.3.4-local`の復旧用sourceは保持しています。旧PnP／開発署名／参照PC専用の検証操作はCommunityの一般導入手順ではありません。
