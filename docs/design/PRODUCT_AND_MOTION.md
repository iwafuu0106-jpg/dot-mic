# 製品・UI・モーション・Windows統合

> 以下の見た目・寸法・ドラッグ方式・モーション値は初期試作の記録。現行UIはルート `DESIGN.md` のMinimal Navy。音声機能・設定値・状態の意味は引き続き本書を参照する。

## 1. 完成範囲
Windows 11 x64向けのローカルマイク増幅ツール。ユーザーが導入したVB-CABLEのrender側へ出力し、Discord等にはそのcapture側を入力として選んでもらう。通常面はゲインノブ、入力デバイス、ゲート、NC ON/OFF、右上三本線menuだけ。Input Trim、NC強度、モデル切替、EQ、録音、クラウド処理、独自仮想ドライバは追加しない。[S01]

手動起動時はメイン、トレイ操作では小型面を開く。二つの見た目は共通ControlとViewModelで作り、audio engineは一つ。両方見せる場合も別マイクstreamを作らない。小型面を開くたびにORT sessionを作らない。

## 2. レイアウトと見た目
数値の正本はdefaults.json。基準はmain 320×360 DIP、flyout 280×304 DIP。高DPIはDIPで換算し、文字拡大では窓の伸長・詳細面scrollを許容する。何が押せるか分からない高密度のカードや診断数値を常設しない。

|領域|main x,y,w,h|flyout x,y,w,h|
|---|---|---|
|header|16,12,288,28|12,8,256,24|
|device|16,48,288,36|12,40,256,32|
|knob＋meter|80,92,160,160|84,80,112,132|
|gate|16,264,288,36|12,220,256,32|
|NC|16,308,288,36|12,260,256,32|

header右端が三本線。mainのcloseはその左に置く。header内のvisual高とhit rect高を混同せず、全操作に非重複の32DIP以上の領域を確保する。通常行はmainで36DIP。ノブの外周径はmain136、flyout96DIPを基準とする。

黒背景、白主要情報、グレー副情報、異常時だけ赤。ドットはノブ数字・外周・小アイコンへ集中し、本文と日本語のデバイス名はシステム書体を使う。数値は自作5×7固定幅ドットでもよいが、UIAには普通の文字の数値を提供する。ロゴやフォントファイルの抽出・同梱はしない。

文字・数値の基準位置・ヒット領域は固定する。ノブ指針は設定gainだけ、実音量はIN/OUTの小さい二列meterで示す。偽の音量・偽の接続点滅は禁止。gradient、glass、blur、装飾shadow、背景particle、全画面の点滅は加えない。OS非クライアント描画を無理に再実装しない。

## 3. ノブと設定
ノブは-12〜+36dB、0.1dB刻み、初期0。上下drag、wheel、Shift微調整、上下key、HomeまたはCtrl-clickで0に戻す、数字double-clickで直接入力、Enter確定/Esc取消を実装する。感度はdefaultsの値を使う。円を描く操作を強制せず、外周clickで突然大きなgainへ飛ばさない。[S08]

dragはpointer captureで継続し、失った場合も安全に終了する。上限下限でクランプ、負の0は0。無効文字、NaN、Infinity、範囲外は拒否する。範囲が非対称なので0dBは12時方向ではない。270°の目盛り内の正しい位置を明示する。

音声gainは20msで平滑化するが、表示値は入力直後に更新する。UIアニメーションの終了を音声反映の条件にしない。LIMITは実limiterの減衰にだけ反応し、音を自動で大きくするAGCは作らない。

入力選択はEndpoint IDで保存。初回は既定候補を提示して利用者が選ぶ。「Windows既定に追従」は明示的な選択肢としてのみ有効。同名機器は完全名と短い識別で分ける。切断時に別マイクへ無断変更しない。ステレオ入力はchannel1が既定、接続詳細でchannel2または(L+R)/2を選べる。

ゲートはON/OFFと閾値のみ通常表示。閾値から詳細を開き、開く閾値、閉じる差分、attack/hold/releaseを調整する。defaultsの範囲を守り、初期OFF。ゲート機能のONと現在の開閉は別の状態である。

NCはON/OFFのみ、初期OFF。準備中と「一時停止・原音」は同じ行で表示する。利用者の要求値と実際の適用状態を分ける。故障退避を正常ONの演出で隠さない。OFF確定後はNC演算を止めるが、原音の固定遅延と後段処理は残る。

## 4. リッチモーションを少ない実装で作る
WinUIのControlTemplate／既存の状態管理とCompositionを使う。ボタンをすべて独自Controlへ作り直す必要はない。共通のpress/hover/focus、popup、ambientを数個の再利用部品にし、ノブ・hamburger・NCの固有動作を組み合わせる。機能を減らすのではなく実装を共有する。[S09]

基準時間はpress83ms、通常の反応167ms、panel/window250ms、同一登場内のstagger12ms。これはMicrosoftの時間群を採用した独自割当であり、他製品の実測値ではない。[S10] すべての操作領域に微細なidleと操作反応を持たせる。ただしlabelや実状態は静止する。

|領域|常時／hover|押下・変更|
|---|---|---|
|header|ロゴ脇の装飾3点だけ8秒周期。接続点は実状態|非操作ロゴは押せるように反応させない|
|menu|線端の装飾点10秒周期、hoverで1DIP整列|press0.97倍、三本線→Xを167ms。再クリックで途中から逆転|
|close|非意味の一点を12秒周期、hoverで外周|pressで1DIP沈む。実機能は常駐設定どおり|
|device|非意味ドット9秒周期、hoverで短い境界反応|chevron回転、起点から4DIPのpopup展開|
|knob|装飾dotに6秒周期の弱い位相波|指針はpointerに即応、台座0.985倍、操作位置に局所dot反応|
|gain数字|読み取り中は固定、hoverで編集可能の短線|確定時に字形crossfade。drag中に架空の中間数字を演出しない|
|IN/OUT|実測値のみ、音がなければ無音表示|操作対象ではない。30Hz以下の取得を表示中だけ補間|
|gate|装飾点10秒周期、閾値hoverとtoggleを区別|実開閉に合わせた隙間変化。ON設定と開閉状態を分ける|
|NC|装飾点11秒周期、hoverで操作領域を明示|実Onで整列、Offで元へ。Starting/Faultは別表示|
|menu項目／詳細入力|既存領域の控えめな位相、focusは固定位置|各入力にpressと状態変化。sliderの値は即時|

ambientは最大0.5DIP、opacity変化±0.04、周期6〜12秒で固定位相差。多数の独立timerを作らず、Compositionの少数Visualや共通surfaceで描く。単なる全ボタン同一scaleだけで済ませない。spring/bounce/overshoot、意味のない待ちspinnerは使わない。

mainはroot contentのopacity、scale0.985→1、Y+6→0。flyoutはトレイ側を起点に同等の短い出入り。HWNDの矩形は最初に決め、毎frame動かさない。閉じる方は167msを基準とする。popupも起点が分かる短距離revealとし、毎回新しいanimation定義を積まない。

再操作時は現在の見た目から最新targetへ遷移し、古いアニメーションをqueueしない。共通のtarget値とcancel/retargetで実現し、全control×全状態の独自エンジンは不要。drag、disabled、focus、errorが衝突したら入力安全性と実状態を優先する。

Fullは既定で上記すべて。Reducedはambient/移動/拡縮なし、必要なopacityのみ100ms以下。Offは即時表示。OSでanimation無効ならReduced相当を優先する。どのモードでも実メーターは意味情報として更新する。高コントラスト、keyboard、UIA対応は標準controlを基に維持する。

全窓非表示／最小化／lockで描画とmeter取得を止める。復帰は最新stateから始める。トレイfault確認の小さいsnapshot pollは最大1Hzを許容し、既にイベント通知があればそのまま使う。NC推論にGPUは使わないが、UIのGPU合成は利用してよい。[S09]

## 5. メニュー・セットアップ
三本線menuは幅248DIP程度、必要なら内部scroll。項目は、小型からmainを開く、常駐、サインイン時起動、音声接続先、Full/Reduced/Off、接続案内、診断、終了。常駐初期ON、自動起動初期OFF。診断グラフを通常面に増やさない。

初回は同じ窓内で物理micとvirtual renderを選ぶ。CABLE Inputがアプリ出力、CABLE OutputがDiscord入力だと方向付きで説明する。[S01] 名前だけで自動確定せず、利用者に実端点を確認できるようにする。既知の自己loopは拒否するが、任意の外部配線全体を検出する仕組みは作らない。

VB-CABLE未導入は案内を出し、導入済みの別端点やスピーカーへ黙って繋がない。正常なstream開始を「Discord接続済み」と断定しない。初期確認はgain0・gateOFF・NCOFF。Discord側との二重NCは比較して判断し、スピーカー使用時のエコー対策まで一律に外す案内はしない。

## 6. トレイ・起動・保存・復旧
Shell_NotifyIcon、安定GUID、version4、左click/keyboardでflyout、右clickでmain/Exitを実装する。表示先がタスクバーか隠れ領域かはWindows／利用者が管理する。非公開レジストリで位置を強制しない。[S11]

Shell_NotifyIconGetRectを起点に、work areaとDPIで窓を画面内へ置く。取得失敗はclick位置等からclampする。外click/Escで閉じるが、子popupとpointer capture中のdragは除外する。TaskbarCreatedを受けるtop-level HWNDでExplorer再開に再登録する。message-only HWNDだけでbroadcastを受ける前提にしない。

常駐ONのmain closeはhide、OFFのcloseはExit。flyoutから常駐OFFにする場合はmainを残す。手動二重起動は既存mainを表示し、新engineを作らない。サインイン起動は常駐ONなら非表示、設定不足ならセットアップ。明示Exitは音声とtrayを停止する。

unpackagedの自動起動はHKCU Runのアプリ専用値に、quoteした実パスと--startupを登録する。登録結果をreadbackし、自分の値だけ解除する。OS側の許可まで正式APIで取得できなければ断定せず、Windowsのスタートアップ設定で確認できる説明にする。[S12]

設定はLocalAppDataのversion付きJSONを一度検証し、temporary→replaceで保存する。汎用migration frameworkは不要。破損時は安全な初期値と説明。model欠損・端点切断・アクセス拒否・sleep/resumeは共通stop/rebuild/refillで扱い、利用者が選んだ同じ端点だけ復帰する。再試行は通知＋小さな上限付きbackoffとし、busy loopしない。

音声保存・送信・広告・テレメトリーはない。ログは短いエラーと状態、数値のみ。起動登録解除とアプリ専用設定の削除手順を同梱する。VB-CABLEや共有runtimeを削除しない。ZIP移動時の自動起動パス、署名なし配布の制約を明記する。
