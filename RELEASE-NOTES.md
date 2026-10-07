## Download

Windows 11 x64 / DOT MIC 0.4.0-community

- **[通常利用：0.4.0-community.zip](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community/0.4.0-community.zip)**
- [開発者向け：0.4.0-community-source.zip](https://github.com/iwafuu0106-jpg/dot-mic/releases/download/v0.4.0-community/0.4.0-community-source.zip) — 通常利用には不要です。

SHA-256:

Distribution (`0.4.0-community.zip`):
```text
93B940C91DBC43A49661B4CA323110B1C9D037DDF24D8B8697D954CF31B13976
```

Source (`0.4.0-community-source.zip`):
```text
88C1E36A7FE65710C24E5CB7095C8460A6CA80E0E870060D5AD59F5A443B0265
```

## 導入方法

1. `0.4.0-community.zip`をダウンロードし、ZIP全体を展開します。
2. `DotMic.Setup.exe`で使用する物理マイクを選択し、変更内容を確認してInstallします。Install / Repair / Uninstall / Recoveryは管理者権限が必要です。再起動が必要と表示された場合だけWindowsを再起動します。
3. `UI/DotMic.App.exe`を通常権限で起動します。初期値は`Bypass ON / Gain 0 dB / Gate OFF / NC OFF`です。効果を使用するにはメニューで**BypassをOFF**にします。

Discordでは従来の物理マイクを選択してください。「DOT MIC」という新しい録音デバイスは作成されません。

## Community版について

`0.4.0-community`は**Microsoft認証／WHQL版ではありません**。ゼロコスト配布のためlegacy Windows Audio APO integrationを使用します。

未署名APOを利用するため、SetupがProtected AudioDG関連設定を変更する場合があります。一部のDRM／secure audio path対応アプリへ影響する可能性があります。Install前にSetupで説明します。

Windows SmartScreenが確認画面を表示する場合があります。公式配布元はこのGitHub repositoryとReleasesです。Secure Boot OFF、Memory Integrity OFF、TESTSIGNINGを要求しません。

詳細：[Community/SECURITY.md](https://github.com/iwafuu0106-jpg/dot-mic/blob/main/Community/SECURITY.md)。

## 問題がある場合

- 効果が適用されない：`DotMic.Setup.exe --repair`
- 削除：SetupのUninstall
- `Recovery Required`：SetupのRecovery機能。保存したsnapshotは削除しないでください。

操作画面と機能説明は[README](https://github.com/iwafuu0106-jpg/dot-mic#readme)を参照してください。
