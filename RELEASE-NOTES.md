## ダウンロード

Windows 11 x64 / DOT MIC 0.4.0-community-ux1

- **[通常利用：0.4.0-community-ux1.zip](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community-ux1/0.4.0-community-ux1.zip)**
- [開発者向け：source ZIP](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community-ux1/0.4.0-community-ux1-source.zip) — 通常利用には不要です。
- [SHA256SUMS.txt](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community-ux1/SHA256SUMS.txt)

## 導入方法

1. ZIP全体を展開します。
2. 直下の`セットアップ.exe`を開き、マイクを選択して変更内容を確認し、「同意して導入」を押します。
3. 完了したら、同じ場所の`DOT MIC.exe`を通常権限で開きます。

`内部ファイル`フォルダーは移動・削除しないでください。旧アプリが起動中の場合は、メニューの「終了」で閉じてから進めてください。再起動は、セットアップで必要と表示された場合だけ行います。

新しい導入の初期値は、バイパス無効・音量補正 0 dB・ゲート無効・ノイズ除去無効です。サインイン時の起動は初回のアプリ起動時に有効になり、メニューで無効にできます。Windows側で起動が禁止されている場合は起動しません。既存の音声設定はアプリ起動時に上書きしません。

Discordでは従来の物理マイクを選択してください。

## 変更点

- 起動する2つのEXEをZIP直下に配置。
- セットアップを日本語化し、変更内容の確認後、1つの同意ボタンで実行。
- バイパスOFF・サインイン起動ONを新規設定の初期値に変更。
- モーション・常駐の設定項目を削除。アニメーションはWindows設定に従います。
- メニュー・状態表示・ゲート設定の日本語を整理。

APO・モデル・推論DLL・ネイティブ統合DLLは受入済みファイルを維持しています。導入・復旧データの形式は旧版と互換です。旧ReleaseとZIPは変更していません。

UI・セットアップ・起動用EXEのビルド、初期値と旧設定の読み込み、起動引数、通常権限でのアプリ起動・日本語メニュー・設定保存・正常終了を確認しています。音声の既存受入結果を再利用し、導入・削除・再起動・NC・性能試験の反復は行っていません。更新版セットアップの実画面と同意操作は未確認です。

## Community版について

**Microsoft認証／WHQL版ではありません。** Windows SmartScreenが確認画面を表示する場合があります。配布元を確認してから進めてください。

未署名APOを利用するため、セットアップが保護音声の互換設定を変更する場合があります。一部の著作権保護コンテンツの再生に影響する可能性があり、導入前に変更内容を表示します。Secure Boot OFF、Memory Integrity OFF、TESTSIGNINGは要求しません。

詳細：[Community/SECURITY.md](https://github.com/iwafuu0106-jpg/dot-mic/blob/main/Community/SECURITY.md)。

## 問題がある場合

- 効果が適用されない：セットアップで「修復」。
- アンインストール：セットアップで「削除」。
- 復旧が必要：セットアップで「復旧」。保存した復旧データは削除しないでください。

操作画面：[README](https://github.com/iwafuu0106-jpg/dot-mic#readme)。
