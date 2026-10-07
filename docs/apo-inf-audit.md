# no-op gate 0.1.1 INF監査 — 2026-10-07

- 対象は実測 `USB\VID_3142&PID_00C1&MI_00` のみ。USB class／全captureへmatchしない。
- Extensionは2つのsoftware component（独自APOとMicrosoft inbox APO Proxy）を既存USB audio devnodeのchildとして追加。新しいaudio function driver、KS filter、仮想capture device、AddServiceによる独自kernel serviceはない。
- `AddInterface` のAUDIO／CAPTURE両方のreference stringは、実機で既存を確認した `GLOBAL`。新しいreference stringを発行しない。再導入前のread-only確認は `artifacts/apo-gate/com-repair/registered-interfaces-before.json`。`GetDeviceIdConnectedTo`も同じAUDIO GLOBALへ接続。既存interfaceへのFX property登録を残す。AddInterfaceという命令名だけで新endpoint生成と判定しない。
- FX associationは実hardware connector `KSNODETYPE_MICROPHONE` に限定。MFXのみ、DEFAULT／COMMUNICATIONSのみ。RAW／SFX／EFXは登録しない。
- APO INFはAudioProcessingObject class、private SWC IDとCLSID、HKR component-scoped registration、DIRID13。regsvr32／HKCR直接登録はない。
- Endpoint／physical devnode／interface Friendly Name、EP store、PKEY_AudioEndpoint_StableId、RAW capability、processing-mode overrideを設定する行はない。software componentのDescription／APOのFriendlyNameは録音device名ではない。
- 0.1.1差分はATL COM骨格とversion更新。interface/reference/component/FXの範囲は拡大していない。INF /w と /h /v、CAT再生成を別runで実施。
- インストールによるruntime Endpoint ID再生成は記録するが、ユーザーの新方針により単独ではFAILにしない。StableId（不透明・case-sensitive）を優先し、取得不能／解決不能ではContainerId＋同じphysical KS interfaceで唯一のcaptureを照合。StableIdに永続不変の保証はない。Friendly Nameだけでは選ばない。
- 実機acceptanceは同じ物理capture／同じ名前、GetDevice・ActivateIAudioClient・GetMixFormatの各成功、実audiodg/APO/Discord相関。INF静的検証だけではこれらをPASSにしない。

根拠:
- [Microsoft SYSVAD componentized extension](https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/TabletAudioSample/ComponentizedAudioSampleExtension.inx)
- [Componentized APO](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects#componentized-apo-installation)
- [Endpoint ID lifetime](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings)
- [StableId / Windows11 24H2](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-stableid)
