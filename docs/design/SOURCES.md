# 先例・公式APIと確認範囲

改訂基準日：2026-10-06。本文の[Sxx]は以下を参照する。今回再確認したAPIと、v2から採用判断を引き継いだ製品先例を区別する。すべての製品を実機で操作・計測したという意味ではない。v3の工程・回数・P制御値は本製品向けの設計判断であり、出典が保証する値ではない。

## S01 — VB-CABLE（v2から経路を継承）
https://vb-audio.com/Cable/
アプリ出力と他アプリのcaptureをつなぐ外部ケーブル。ドライバを無断同梱しない。インストール・配布条件は使用する配布物で確認する。

## S02 — Microsoft: AUDCLNT_STREAMFLAGS（今回再確認）
https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants
EVENTCALLBACK、AUTOCONVERTPCM、SRC_DEFAULT_QUALITY。OS側の既存変換を使い、自作のフォーマット処理を必要以上に増やさない。

## S03 — Microsoft: IAudioClient::GetMixFormat（今回再確認）
https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getmixformat
mix formatと物理機器のPCM形式は同一とは限らず、共有エンジンが変換する。取得formatの型とchannel情報を確認して利用する。

## S04 — CEVA: real_time_demo.py（今回再確認）
https://raw.githubusercontent.com/ceva-ip/DPDFNet/main/real_time_demo.py
48k HRの960/480、Vorbis窓、STFT/ISTFT、metadataのnorm初期化。数学的な前後処理を参照し、callback内推論、AGC、可視化UIは移植しない。

## S05 — CEVA: 48k HR exporter（今回再確認）
https://raw.githubusercontent.com/ceva-ip/DPDFNet/main/onnx_model/export_dpdfnet_48khz_hr_to_onnx.py
spec/stateの入出力とmetadata生成。実使用モデルのrevisionとhashは実装時に固定する。

## S06 — Microsoft: Low Latency Audio（今回再確認）
https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio
WASAPI、buffer periodと低遅延処理の条件。最小period探索や他モードへの展開を無条件に実装しない。

## S07 — Microsoft: IAudioClient::Initialize（今回再確認）
https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize
共有event駆動、event handle、buffer/period、実サイズ・padding。使用しないexclusiveモードの互換処理まで作らない。

## S08 — FabFilter: Pro-G Knobs（今回再確認）
https://www.fabfilter.com/help/pro-g/using/knobs
上下drag、fine、wheel、直接入力、resetという精密操作を採用する。DOT MICの数値レンジと感度は独自値。

## S09 — Microsoft: Composition animations（今回再確認）
https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-animation
UI threadと独立したvisual animation。見た目の動きは保持し、実装を少数の再利用部品へまとめる。

## S10 — Microsoft: Timing and easing（今回再確認）
https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing
83/167/250msの時間群。個々のDOT MIC操作への割当・移動量・ambient周期は本設計の値。

## S11 — Microsoft: Notification Area（v2からAPI選定を継承）
https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
trayの常駐と利用者が管理する表示位置。関連位置取得とExplorer通知も使用版の正式APIに従う。

## S12 — Microsoft: Run and RunOnce（v2から選定を継承）
https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
unpackagedの利用者単位の起動登録。自身の値だけを扱う。

## S13 — ONNX Runtime: Thread management（今回再確認）
https://onnxruntime.ai/docs/performance/tune-performance/threading.html
CPU EPのintra/inter、SEQUENTIAL、spinning。1threadは推論の制約でありUIやWASAPI thread全体を1本にする意味ではない。

## S14 — libsamplerate: Full API（今回再確認）
https://libsndfile.github.io/libsamplerate/api_full.html
ストリーミング変換、比率の連続変更、実入力消費・出力生成。clock制御器の採用値自体はDOT MICの設計。

## 継承する視覚・機能先例（再調査は義務にしない）
Nothingのモノクロと選択的dot、OBSのgain/gate/limiter、EarTrumpetのトレイ起点、Riveの状態／中断という採用点はv2から維持する。以下は必要箇所の参照先であり、開発者へ全件の読み直し・観察台帳作成を要求しない。

https://nothing.community/d/61774-nothing-os-evolution
https://obsproject.com/kb/gain-filter
https://obsproject.com/kb/noise-gate-filter
https://obsproject.com/kb/limiter-filter
https://github.com/File-New-Project/EarTrumpet
https://rive.app/docs/editor/state-machine/state-machine

## 固定依存の取得元（使用版を実装時に固定）
https://huggingface.co/Ceva-IP/DPDFNet
https://github.com/mborgerding/kissfft
https://github.com/libsndfile/libsamplerate
https://onnxruntime.ai/

既存正常版はアップデートのために変更しない。公式配布物・source・licenseを確認するが、通常のpackage lockが管理する項目まで独自の台帳へ二重転記しない。
