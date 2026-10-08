# DOT MIC 0.4.2

0.4.1のアプリ配置・通常ZIP方式を維持し、セットアップの応答性、導入条件、復旧、設定の再反映を改善します。既存Release・タグ・assetは置き換えません。

## 主な修正

- 重い準備・配置・復旧処理をSTA workerへ移し、競合の確認はUIへ戻します。内部snapshot保存時の重複した全アプリパス検査を削減し、外部復旧データの検証は維持します。
- 一時的なレジストリ権限変更後のACL復元を修正します。比較で無視するのは自動継承の記録用flagだけです。所有者・保護・ACE・監査の変更は見逃しません。既存keyを保持したまま復元し、確認中に権限が変わった場合は停止します。
- 任意のアプリ配置情報やマイク一覧の失敗で、独立した復旧まで無効にしません。原因・復旧状態・権限の復旧待ちを詳細の先頭へ表示します。中断した削除の状態が不明なら新しい導入は許可しません。
- マイク一覧の不足バッファを有限回再取得します。再初期化後の対象マイク・APO進行を有限待機し、一意性、Calls／Framesの増加、Running、固定DLL／hash、Error、音声取得完了の確認は維持します。固定native helperの取得時間上限5000ms内で、準備未完了だけ最大3試行します。
- 音声サービスの遷移中は不要な制御を発行せず、checkpoint／wait hintの進捗を確認します。停滞待機20–60秒、全体上限180秒です。制御・復元の両方が失敗した場合は両方の原因を保持します。
- ショートカットを作成せず、既存の所有ショートカットもない場合はデスクトップ確認を省きます。未作成の連続する保存先の親フォルダーを、管理者管理の既存親の下へ保護付きで作成できます。既存親のACLを変更しません。空の親フォルダーが残る場合があります。
- アプリ・ショートカットの復元を、保護されたstageの完成・hash検証後の置換へ変更します。中断したstageは記録と元データに照合し、変更されたファイルは保持します。既存の同じhashのアプリファイルも配置時にACL確認します。
- 復旧時は空のレジストリkey shellを残します。他のソフトが確認直後に追加した値をkeyごと削除しません。
- サインイン起動の任意設定の失敗が通常メニューを妨げないようにし、欠落launcherでも登録解除できるようにします。有効化時の導入済みlauncher検証は維持します。trayが使えない場合は画面を表示します。
- 設定書き込み失敗時に編集を保持し、commitを500／1000／2000msの3回まで再試行します。明示的な再取得・復帰では読み取りを待って保留編集を再反映します。自動再導入・昇格は追加しません。

## 配布と互換性

- `DOT MIC 0.4.2.zip`は複数ファイルの通常ZIPです。全体を展開して`セットアップ.exe`を起動します。
- 既定のアプリ保存先はProgram Files。保存先選択と同意画面の全ユーザーデスクトップショートカットcheckboxを維持します。UI／Setupのruntimeは`内部ファイル/UI`で共有します。
- x64、管理者によるセットアップ、保護されたローカル配置、対象機器の一意性、ユーザー同意、payload／復旧データの検証は必須のままです。
- backend contract・APO固定配置は`0.4.0-community`を維持します。APO・DSP・モデル・native bridgeは受け入れ済みファイルと同じhashです。旧receiptの新規fieldは空リスト／falseの既定値で互換性を保ちます。
- 復旧データ・バックアップは削除しないでください。復旧が必要な場合は新しい配布を展開して「復旧」を選びます。成功するまで繰り返し導入しないでください。
- Microsoft／WHQL／Authenticode認証版ではありません。Secure Boot／Memory Integrity無効化、TESTSIGNING、証明書root追加、広いACL付与を対応策として追加していません。

## 確認範囲と限界

App／Setup／File.Testsのビルド、純粋な表示・状態・ACL比較テスト、模擬worker・時計・service controller・native reader・capture・ファイルstageテストが成功しました。実際の`AudioViewModel.cs`を仮のbackend／timerと組み合わせ、再接続pollとrefreshの競合、retry制限、編集保持、終了後の書き込み防止を確認しました。

ACL復元については、事前に許可された専用GUIDレジストリkeyで復元失敗と再試行のaccess deniedを再現し、修正後の継承／保護／所有者のみの中断／外部変更拒否／欠落keyを再作成しない動作を確認しました。keyは削除済みです。マイク設定、音声サービス、既存構成、外部利用者のreceiptは変更していません。

**修正版の実導入・使用・サービス再初期化・アンインストール・実デスクトップを含む一連の実OS検証は未実施です。** 特定の利用者のPCでの復旧成功や、全driver／APO構成の対応を保証するものではありません。模擬stage試験は実OSの耐停電試験ではありません。従来のDSP／音声受入は再利用し、再試験していません。

配布ZIPのmanifest／全ファイル、固定payloadとbridge hash、ライセンス、秘密情報・端末識別子・ローカルpathを公開前に検査します。最終ZIPのSHA-256はGitHub Release notesを参照してください。

## 開発者向け

0.4.2のインストーラーZIPを作る場合は、Windows x64、.NET 10 SDK、Visual Studio 2019 C++ Build Tools、CMake、Windows SDK 10.0.19041.0と、展開済みの受け入れ済みbaselineを使用します。旧portableビルド手順は`SOURCE.md`を参照してください。

```powershell
./Community/package-install.ps1 -BaseDirectory <展開済み0.4.0-community> -OutputDirectory artifacts/install-0.4.2-new
dotnet run --project Community/Setup.Tests/DotMic.Setup.Presentation.Tests.csproj -c Release
dotnet run --project Community/Setup.Security.Tests/DotMic.Setup.Security.Tests.csproj -c Release -- --pure
dotnet run --project Community/Setup.Worker.Tests/DotMic.Setup.Worker.Tests.csproj -c Release
dotnet run --project tests/reliability/DotMic.Reliability.Tests.csproj -c Release
dotnet run --project tests/viewmodel-reliability/DotMic.ViewModel.Tests.csproj -c Release
dotnet run --project tests/app-defaults/App.Defaults.Check.csproj -c Release
```

出力先は新規フォルダーのみです。`Setup.Security.Tests`は必ず`--pure`を付けてください。付けないfixtureや`Setup.Files.Tests`の実行は管理者管理の対象を変更するため、上の無変更テストには含みません。
