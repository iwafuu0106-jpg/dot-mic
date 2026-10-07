# DOT MIC

Windows 11向けのマイク処理アプリです。Discordなどで使用している物理マイクを変更せず、Gain・Noise Gate・Noise Cancellation・Limiterを適用できます。

**[ダウンロード：0.4.0-community-ux1](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community-ux1/0.4.0-community-ux1.zip)**

Windows 11 x64 / Community Release

![DOT MICのメイン画面。Gain 0 dB、ノイズゲートとノイズ除去はOFF。](docs/assets/dot-mic-main.png)

## 導入方法

### 1. ZIPをダウンロードして展開

通常利用は上の`0.4.0-community-ux1.zip`を使用します。一部ファイルだけを開かず、ZIP全体を展開してください。

展開先で開くファイルは次の2つです。

```text
セットアップ.exe  ← 最初に開く
DOT MIC.exe       ← 導入後に開く
内部ファイル/      ← 移動・削除しない
```

### 2. セットアップ.exeを開く

展開先の`セットアップ.exe`で使用する物理マイクを選択し、変更内容を確認して「同意して導入」を押します。導入・修復・削除・復旧には管理者権限が必要です。再起動が必要と表示された場合だけWindowsを再起動してください。

旧アプリが起動中の場合は、メニューの「終了」で閉じてから進めてください。

### 3. DOT MICを起動

同じ場所の`DOT MIC.exe`を通常権限で起動します。

新しい導入の初期設定は、バイパス無効・音量補正 0 dB・ゲート無効・ノイズ除去無効です。ノイズ除去やゲートは必要に応じて有効にしてください。

サインイン時の起動は、初回のアプリ起動時に有効になります。メニューで無効にできます。Windowsのスタートアップ設定で禁止されている場合は起動しません。

Discordでは従来の物理マイクを選択してください。「DOT MIC」という新しい録音デバイスは作成されません。

### SmartScreen

Community版はAuthenticode／Microsoft認証版ではないため、Windows SmartScreenが確認画面を表示する場合があります。公式配布元はこのGitHub repositoryと[Releases](https://github.com/iwafuu0106-jpg/dot-mic/releases/tag/v0.4.0-community-ux1)です。

## Community版について

> `0.4.0-community`は**Microsoft認証／WHQL版ではありません**。ゼロコスト配布のためlegacy Windows Audio APO integrationを使用します。
>
> 未署名APOを利用するため、SetupがProtected AudioDG関連設定を変更する場合があります。一部のDRM／secure audio path対応アプリへ影響する可能性があります。既存効果の置換や音声の一時切断も、Install前にSetupで説明します。

Secure Boot OFF、Memory Integrity OFF、TESTSIGNINGを要求しません。詳細：[Community/SECURITY.md](Community/SECURITY.md)。

## 使い方

ダイヤルで入力音量を調整し、ノイズゲート・ノイズ除去を必要に応じて有効にします。効果を一時停止する場合はメニューでバイパスを有効にします。Discord側に新しい入力を選ぶ必要はありません。

## 主な機能

- Gain：マイク入力を増幅・減衰します。
- Noise Gate：設定したレベルより小さい入力を抑えます。
- Noise Cancellation：DPDFNet2をローカルCPUで実行してノイズを抑制します。音声はクラウドへ送信しません。
- Limiter：過大な出力レベルを抑えます。

## 問題がある場合

- DOT MICが効かない：`セットアップ.exe`で「修復」を選択します。
- アンインストール：セットアップで「削除」を選択します。
- 「復旧が必要です」と表示：セットアップで「復旧」を選択します。保存した復旧データは削除しないでください。

## 開発者向け・技術情報

- [source ZIP（開発者向け）](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community-ux1/0.4.0-community-ux1-source.zip) — 通常利用には不要です。
- [ソースのbuild方法](Community/SOURCE.md)
- [第三者ライセンス・notice](licenses/README.md)
- [受入結果の概要](Community/PUBLIC-ACCEPTANCE.md)
- [Release構成・version・旧版復旧の補足](docs/release-0.4.0-community.md)
- [SHA-256](https://github.com/iwafuu0106-jpg/dot-mic/releases/tag/v0.4.0-community-ux1)
