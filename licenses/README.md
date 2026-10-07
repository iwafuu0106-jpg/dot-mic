# Third-party notices

DOT MIC source: MIT (`../LICENSE`). Each bundled component remains under its own license.

| Component | Pinned version / source | License |
|---|---|---|
| DPDFNet2 48 kHz HR model and reference code | CEVA, model revision dd6818d00f50c836fed43a6243ebe49116de5964 | Apache-2.0, DPDFNet.txt |
| ONNX Runtime CPU | Microsoft 1.23.2 | MIT, ONNXRuntime.txt and ONNXRuntime-ThirdPartyNotices.txt |
| KissFFT float32 | 131.1.0 | BSD-3-Clause, KissFFT.txt / KissFFT-BSD-3-Clause.txt |
| libsamplerate (optional offline fixed-SRC fixtures only; not a product dependency) | 0.2.2 | BSD-2-Clause, libsamplerate.txt |
| .NET runtime | Community UI:10.0.3; Setup:NETCore/WindowsDesktop10.0.3; exact runtime packs in UI/DotMic.App.deps.json and DotMic.Setup.deps.json | MIT, DOTNET.txt and DOTNET-ThirdPartyNotices.txt |
| Windows App SDK / WinUI | pinned by App/packages.lock.json | Microsoft distribution license, WindowsAppSDK.txt |
| MSVC CRT | Microsoft official x64 desktop runtime14.39.33321.0 (package14.0.33321.0), unmodified DLLs | Microsoft runtime terms, VisualCpp-Runtime-license.txt / .docx and VisualCpp-BuildTools-license.html |

Community binaries contain no Extension/component INF/CAT installer. The two owned APO/inference DLLs are signature-metadata-only derivatives of the accepted production image; model, ORT and Microsoft runtime DLLs are unchanged. Vendor microphone drivers, virtual audio drivers, fonts, Python and certificate private keys are not redistributed. The model hash and metadata are in APO/profile.json. No microphone PCM is included.
sha256.json identifies the exact delivered payload. NuGet packages.lock.json does not lock the SDK-selected .NET runtime pack; the source snapshot records SDK10.0.103 and delivered packs. Old development PnP instructions in source are reference/recovery paths only, not Community installation or Microsoft certification.
