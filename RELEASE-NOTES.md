## DOT MIC 0.4.0-community

初のCommunity Release。

- 元の物理マイクendpointを維持したWindows APO統合
- Gain / Noise Gate / DPDFNet2 Noise Cancellation / Limiter
- Discord実動作確認済み
- 旧VB-CABLE / capture-render bridge不要
- Repair / Rollback / Uninstall対応

### Requirements

- Windows 11 x64
- Install / Repair / Uninstall / Recovery時のみ管理者権限
- 通常利用は一般ユーザー権限

### Important

Community版はMicrosoft認証／WHQLではありません。未署名APOのためProtected AudioDG関連設定を変更し、一部DRM / secure audio path対応アプリへ影響する可能性があります。Setupで変更前に説明します。SmartScreen警告が表示される可能性があります。Secure Boot / Memory Integrityの無効化やTESTSIGNINGを要求しません。

### Initial state

Bypass ON / Gain 0 dB / Gate OFF / NC OFF。効果を使う場合はBypassをOFFにしてください。

### Files

| File | SHA-256 |
|---|---|
| `0.4.0-community.zip` | `93B940C91DBC43A49661B4CA323110B1C9D037DDF24D8B8697D954CF31B13976` |
| `0.4.0-community-source.zip` | `88C1E36A7FE65710C24E5CB7095C8460A6CA80E0E870060D5AD59F5A443B0265` |

既存受入を再利用し、公開準備で機能試験／buildを繰り返していません。公開用に端末固有証拠とPDBを除き、UI／SetupのCodeView内の個人パスだけを除去しました。実行コード・DSP・model・installer動作は変更していません。元Release／0.3.4-local原本は別保管です。
