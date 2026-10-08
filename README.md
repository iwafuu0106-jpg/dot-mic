# DOT MIC

Windows 11向けのマイク音量調整アプリです。Discordなどで使用している入力設定を変更せず、ゲイン調整やノイズキャンセリングを適用できます。

**[公開版0.4.2をダウンロード](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.2/DOT.MIC.0.4.2.zip)**（旧・単一マイク版）

Windows 11 x64 / Community Release

![DOT MICのメイン画面。Gain 0 dB、ノイズゲートとノイズ除去はOFF。](docs/assets/dot-mic-main.png)



## 開発候補：0.5.0-rc.1

このブランチはマルチマイク対応候補のソースです。候補のバイナリReleaseは未公開です。マイクの選択は不要です。接続したマイクへの自動適用は、管理者権限のWindows Serviceが行います。

一部のマイクで失敗しても、ほかのマイクの設定は戻しません。既存効果の置換や追加権限が必要な新規マイクは、承認まで保留します。

処理対象は通常の48 kHz・float32・1〜2チャンネル音声です。その他の形式とRAWは原音を通し、ゲイン・ゲート・ノイズ除去は適用しません。実機での導入・自動適用・復旧は未検証です。

## 導入方法（候補版）


### 1. ZIPをダウンロードして展開

`DOT MIC 0.5.0-rc.1.zip`全体を展開してください。候補版はまだGitHubで公開していません。

展開後、セットアップ.exeを開いてください。

```text
セットアップ.exe  ← 最初に開く
DOT MIC.exe       ← 導入後に開く
内部ファイル/      ← 移動・削除しない
```


### 2. セットアップ.exeを開く

台数と変更内容を確認し、「同意して導入」を押します。保存先とショートカットは「オプション」で変更できます。再起動が必要と表示された場合だけWindowsを再起動してください。

更新・修復では、起動中の導入済みDOT MICを自動で終了してから上書きします。通常は設定を保存して終了しますが、旧版や応答しないアプリは待機後に強制終了するため、未保存の変更が失われる場合があります。別ユーザーのアプリは自動終了しません。


### 3. DOT MICを起動

完了画面で「アプリを開く」を押します。次回からは`DOT MIC.exe`を開きます。

初期設定は、音量補正 0 dB・ノイズゲート無効・ノイズ除去無効です。ノイズ除去やノイズゲートは必要に応じて有効にしてください。

Discordでは従来の物理マイクを選択してください。「DOT MIC」という新しい録音デバイスは作成されません。


### SmartScreen

Community版はAuthenticode／Microsoft認証版ではないため、Windows SmartScreenが確認画面を表示する場合があります。公式配布元はこのGitHub repositoryと[Releases](https://github.com/iwafuu0106-jpg/dot-mic/releases/tag/v0.4.2)です。


## 使い方

ダイヤルで入力音量を調整し、ノイズゲート・ノイズ除去を必要に応じて有効にします。効果を一時停止する場合はメニューでバイパスを有効にします。Discord側に新しい入力を選ぶ必要はありません。


## 主な機能

- Gain：マイク入力を増幅・減衰します。
- Noise Gate：設定したレベルより小さい入力を抑えます。
- Noise Cancellation：DPDFNet2をローカルCPUで実行してノイズを抑制します。音声はクラウドへ送信しません。
- Limiter：過大な出力レベルを抑えます。


## 問題がある場合

- DOT MICが効かない：`セットアップ.exe`で「修復」を選択します。
- 「入力待機」：マイクを使うアプリが開いていない場合は正常です。Discord等を開き、物理マイクの入力先を確認してください。
- 「動作未確認」「診断未取得」：設定の読み取りと動作確認は別です。アプリの入力先、Windowsのオーディオ拡張機能、「診断」を確認してください。録音中でもRAW・対象外mode・拡張機能無効ではDOT MICが動作しないことがあります。
- 「原音：RAW」「原音：形式非対応」「原音：モード未確認」：音声をそのまま通しています。ゲイン・ゲート・ノイズ除去は適用されません。
- アンインストール：セットアップで「削除」を選択します。
- 「復旧が必要です」と表示：セットアップで「修復」を選択します。保存した復旧データは削除しないでください。


## 開発者向け・技術情報

- [source ZIP（開発者向け）](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.2/DOT.MIC.0.4.2-source.zip) — 通常利用には不要です。
- [ソースのbuild方法](Community/SOURCE.md)
- [0.5候補の設計・検証範囲](Community/MULTIMIC-0.5.0.md)
- [0.5候補の確認済み項目と未検証事項](Community/VERIFICATION-0.5.0-rc.1.md)
- [第三者ライセンス・notice](licenses/README.md)
- [受入結果の概要](Community/PUBLIC-ACCEPTANCE.md)
- [Release構成・version・旧版復旧の補足](docs/release-0.4.0-community.md)
- [SHA-256](https://github.com/iwafuu0106-jpg/dot-mic/releases/tag/v0.4.2)
