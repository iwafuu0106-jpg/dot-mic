namespace DotMic.Common;

// Separate settings availability, transport evidence and DSP applicability.
internal enum InputObservationState : uint { Idle, Processing, Unconfirmed, Unavailable, Passthrough }
internal static class InputObservationText
{
    internal const string LatencyContract = "対応DSPの固定遅延: 83 ms / 原音通過: 0 ms";
    internal static string Connection(InputObservationState state, uint reason) => state switch {
        InputObservationState.Idle => "入力待機",
        InputObservationState.Processing => "接続中",
        InputObservationState.Unconfirmed => "動作未確認",
        InputObservationState.Unavailable => "診断未取得",
        InputObservationState.Passthrough => reason == 1 ? "原音：RAW" : reason == 3 ? "原音：モード未確認" : "原音：形式非対応",
        _ => "診断未取得"
    };
    internal static string Notice(InputObservationState state, uint missing) => state switch {
        InputObservationState.Unconfirmed => "マイクを使うアプリの入力先と、Windowsのオーディオ拡張機能を確認してください。",
        InputObservationState.Unavailable => "動作情報を取得できません。「診断」を確認してください。設定は変更できます。",
        InputObservationState.Passthrough => "この入力では原音を通します。音量・ゲート・ノイズ除去は適用しません。",
        _ => missing > 0 ? "動作情報を取得できないマイクがあります。「診断」を確認してください。" : ""
    };
}
