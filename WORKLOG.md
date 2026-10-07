# 0.4.0-community: accepted reference-PC results

以下は既存受入の概要です。公開準備で音声試験、build、install/uninstall、再起動は繰り返していません。

- 未署名legacy APOの保護されたProgram Filesからの実ロード、Init3 / CAPX / OS RTQueue / APOProcess：PASS。
- 旧DOT MIC PnP / Extension / Component package非依存、手動Windows再起動後の維持：PASS。
- 同じ物理マイクのDiscord、Gain / Gate / Limiter / Main / tray反映：PASS。
- DPDFNet2 NC wet採用、NC OFF後の推論停止：PASS。
- 通常Uninstall、人工Verify失敗Rollback、association-loss Repair：PASS。
- 通常権限UI起動・終了、配布ZIP展開、source ZIPからのbuild：PASS。
- 旧0.3.4-local原本・復旧package保持：PASS。

再起動後のruntime ID変化は同じStableId / ContainerIdへ解決しました。移行後の削除用baselineは実際の旧PnPなしの状態から保存しています。参照PCではProtected AudioDG設定は導入前から1でした。Secure Boot / Memory Integrityを無効にせず、TESTSIGNINGを使用していません。

DSP／modelは受入済みと同じです。owned DLLの変更は開発Authenticode metadata除去だけで、正規化PE image同一性を確認済みです。性能／Limiterの既存証拠は再利用します。新たな全デバイス認証、Microsoft認証／WHQL、長時間soakの主張ではありません。

端末固有のendpoint ID、復旧snapshot、ローカルパス、診断log、録音は公開archiveに含めません。原証拠と元Releaseはローカルに保持しています。
