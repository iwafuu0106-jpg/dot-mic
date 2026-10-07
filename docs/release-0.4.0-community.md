# 0.4.0-communityのRelease構成

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
