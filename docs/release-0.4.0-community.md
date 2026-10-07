# 0.4.0-communityのRelease構成

## UX更新1

配布識別子／tagは`0.4.0-community-ux1`／`v0.4.0-community-ux1`です。ZIP直下の`セットアップ.exe`と`DOT MIC.exe`から起動します。サインイン時に起動する対象もこの入口です。

新しい導入ではバイパス無効・音量補正0 dB・ゲート無効・ノイズ除去無効に設定します。通常アプリの初回起動時はサインイン起動を有効にし、以後は保存した有効／無効の選択を維持します。モーション設定と常駐設定は廃止しました。閉じるとトレイへ移動し、終了はメニューから行います。アニメーションの有効／無効はWindows設定に従います。

APO／model／推論DLL／native helperは変更せず、導入・復旧データの形式と固定配置先は`0.4.0-community`を維持します。以下は旧Releaseの構成記録です。

## Version表記

Release識別子は`0.4.0-community`、Git tagは`v0.4.0-community`です。Setup表示の「Community Setup 0.4.0」と`UI/community.json`はこのReleaseを識別します。

通常UIに独立したversion表示はありません。受入済みUI assemblyの`3.2.0`は共有UI componentのversionであり、Release番号ではありません。

## 公開時のmetadata処理

初回公開時は端末固有証拠とPDBを配布から除き、UI／SetupのCodeView内の個人PDBパスだけをファイル名に置き換えました。実行コード・DSP・model・installer動作は変更していません。元Releaseと原証拠は別保管です。

README／Release本文の導入案内更新は公開ページだけの変更です。公開済みZIP、tag、SHA-256は維持しています。

## 受入・復旧用source

参照PCでの既存受入は[概要](../Community/PUBLIC-ACCEPTANCE.md)に記載しています。端末固有のraw証拠は公開せず、全デバイスでの動作保証とはしません。

旧`0.3.4-local`の復旧用sourceは保持しています。旧PnP／開発署名／参照PC専用の検証操作はCommunityの一般導入手順ではありません。詳細は[ソースの構成とbuild方法](../Community/SOURCE.md)を参照してください。

ONNX Runtime、DPDFNet2 model、KissFFT、.NET／WinUI／MSVC runtimeのnoticeは[licenses](../licenses/README.md)にあります。Equalizer APOの実装コードは取り込んでおらず、runtime dependencyでもありません。
