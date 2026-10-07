#include <windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include <servprov.h>
#include <baseaudioprocessingobject.h>
#include <audioengineextensionapo.h>
#include <TraceLoggingProvider.h>
#include <cstring>
#include "gate_ids.h"
#include "pcm-proof-key.h"

TRACELOGGING_DEFINE_PROVIDER(GateProvider, "DotMic.CaptureApoGate",
    (0x289603df,0xb7ed,0x4e5c,0x9a,0xa4,0xa7,0x0c,0x89,0xc8,0x87,0xfc));
class GateModule : public ATL::CAtlDllModuleT<GateModule> {};
GateModule _AtlModule;
static const APO_REG_PROPERTIES Registration{
    GateClsid, APO_FLAG_DEFAULT, L"DOT MIC no-op Capture MFX gate", L"DOT MIC (MIT)",
    1, 0, 1, 1, 1, 1, ULONG_MAX, 1, {__uuidof(IAudioProcessingObject)}};
#if GATE_PCM_SWITCH
static constexpr float FixedGain = 1.0f; // Absent switch is exact no-op.
#elif GATE_QUARTER_GAIN
static constexpr float FixedGain = 0.25f; // Temporary PCM-path proof only.
#else
static constexpr float FixedGain = 1.0f;
#endif

class ATL_NO_VTABLE NoopMfx :
    public ATL::CComObjectRootEx<ATL::CComMultiThreadModel>,
    public ATL::CComCoClass<NoopMfx, &GateClsid>,
    public CBaseAudioProcessingObject, public IAudioSystemEffects3 {
    GUID mode = GUID_NULL;
    bool modeObserved = false, discoveryOnly = false;
    UINT32 bytesPerFrame = 0, maxFrames = 0;
    float fixedGain = FixedGain; // Written only before RT starts; immutable while locked.
    // Audio engine serializes the RT callbacks and stops them before UnlockForProcess.
    // Only Unlock reads these counters: no shared mapping, logger, atomic polling or worker.
    UINT64 calls = 0, frames = 0;
    wchar_t endpointId[256]{};
public:
    NoopMfx() : CBaseAudioProcessingObject(&Registration) {}
    DECLARE_NO_REGISTRY()
    DECLARE_AGGREGATABLE(NoopMfx)
    DECLARE_PROTECT_FINAL_CONSTRUCT()
    BEGIN_COM_MAP(NoopMfx)
        COM_INTERFACE_ENTRY(IAudioProcessingObject)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectRT)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectConfiguration)
        COM_INTERFACE_ENTRY(IAudioSystemEffects)
        COM_INTERFACE_ENTRY(IAudioSystemEffects2)
        COM_INTERFACE_ENTRY(IAudioSystemEffects3)
    END_COM_MAP()
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size, BYTE* data) override {
        if (m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        if (!data) return E_INVALIDARG;
        if (size < sizeof(APOInitBaseStruct) || reinterpret_cast<const APOInitBaseStruct*>(data)->cbSize != size) return E_INVALIDARG;
        IMMDeviceCollection* devices = nullptr;
        if (size == sizeof(APOInitSystemEffects3)) {
            auto init = reinterpret_cast<const APOInitSystemEffects3*>(data);
            if (init->APOInit.clsid != GateClsid) return E_INVALIDARG;
            mode = init->AudioProcessingMode; discoveryOnly = init->InitializeForDiscoveryOnly != FALSE;
            modeObserved = true; devices = init->pDeviceCollection;
        } else if (size == sizeof(APOInitSystemEffects2)) {
            auto init = reinterpret_cast<const APOInitSystemEffects2*>(data);
            if (init->APOInit.clsid != GateClsid) return E_INVALIDARG;
            mode = init->AudioProcessingMode; discoveryOnly = init->InitializeForDiscoveryOnly != FALSE;
            modeObserved = true; devices = init->pDeviceCollection;
        } else if (size == sizeof(APOInitSystemEffects)) {
            auto init = reinterpret_cast<const APOInitSystemEffects*>(data);
            if (init->APOInit.clsid != GateClsid) return E_INVALIDARG;
            // Legacy initialization carries no requested mode. Do not label it observed DEFAULT.
        } else return E_INVALIDARG;
        if (devices) {
            UINT count = 0; IMMDevice* endpoint = nullptr;
            if (SUCCEEDED(devices->GetCount(&count)) && count && SUCCEEDED(devices->Item(count - 1, &endpoint))) {
                LPWSTR id = nullptr;
                if (SUCCEEDED(endpoint->GetId(&id))) { wcsncpy_s(endpointId, id, _TRUNCATE); CoTaskMemFree(id); }
                endpoint->Release();
            }
        }
        m_bIsInitialized = true;
        TraceLoggingWrite(GateProvider, "Initialize", TraceLoggingUInt32(GetCurrentProcessId(), "HostPid"),
            TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this), "Instance"), TraceLoggingWideString(endpointId, "EndpointId"),
            TraceLoggingGuid(mode, "AudioProcessingMode"), TraceLoggingBoolean(modeObserved, "ModeObserved"),
            TraceLoggingBoolean(discoveryOnly, "DiscoveryOnly"), TraceLoggingUInt32(size, "InitSize"),
            TraceLoggingFloat32(fixedGain, "FixedGain"));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in,
        UINT32 outputs, APO_CONNECTION_DESCRIPTOR** out) override {
        HRESULT hr = CBaseAudioProcessingObject::LockForProcess(inputs, in, outputs, out);
        if (FAILED(hr)) return hr;
#if GATE_QUARTER_GAIN
        if (GetBytesPerSampleContainer() != sizeof(float)) {
            CBaseAudioProcessingObject::UnlockForProcess(); return APOERR_FORMAT_NOT_SUPPORTED;
        }
#endif
#if GATE_PCM_SWITCH
        DWORD quarter = 0, size = sizeof(quarter);
        const LSTATUS status = RegGetValueW(HKEY_LOCAL_MACHINE, PcmProofRegistryKey, PcmProofRegistryValue,
            RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &quarter, &size);
        fixedGain = status == ERROR_SUCCESS && quarter == 1 ? 0.25f : 1.0f;
#endif
        bytesPerFrame = GetSamplesPerFrame() * GetBytesPerSampleContainer();
        maxFrames = (in[0]->u32MaxFrameCount < out[0]->u32MaxFrameCount) ? in[0]->u32MaxFrameCount : out[0]->u32MaxFrameCount;
        calls = frames = 0;
        TraceLoggingWrite(GateProvider, "ProcessLocked", TraceLoggingUInt32(GetCurrentProcessId(), "HostPid"),
            TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this), "Instance"), TraceLoggingWideString(endpointId, "EndpointId"),
            TraceLoggingFloat32(fixedGain, "FixedGain"), TraceLoggingUInt32(bytesPerFrame, "BytesPerFrame"),
            TraceLoggingUInt32(maxFrames, "MaxFrames")); // Configuration thread only, not APOProcess.
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        HRESULT hr = CBaseAudioProcessingObject::UnlockForProcess();
        if (SUCCEEDED(hr)) {
            // Metadata only, outside APOProcess. Never emit samples or per-block logging.
            TraceLoggingWrite(GateProvider, "StreamSummary", TraceLoggingUInt32(GetCurrentProcessId(), "HostPid"),
                TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this), "Instance"), TraceLoggingWideString(endpointId, "EndpointId"),
                TraceLoggingGuid(mode, "AudioProcessingMode"), TraceLoggingBoolean(modeObserved, "ModeObserved"),
                TraceLoggingBoolean(discoveryOnly, "DiscoveryOnly"), TraceLoggingUInt64(calls, "APOProcessCalls"),
                TraceLoggingUInt64(frames, "ValidFrames"), TraceLoggingFloat32(fixedGain, "FixedGain"));
        }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetEffectsList(GUID** effects, UINT* count, HANDLE) override {
        if (!effects || !count) return E_POINTER;
        *effects = static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID))); *count = 0;
        if (!*effects) return E_OUTOFMEMORY;
        **effects = GateEffectId; *count = 1; return S_OK; // Private diagnostic identity, no DSP claim.
    }
    HRESULT STDMETHODCALLTYPE GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** effects, UINT* count, HANDLE) override {
        if (!effects || !count) return E_POINTER;
        *effects = static_cast<AUDIO_SYSTEMEFFECT*>(CoTaskMemAlloc(sizeof(AUDIO_SYSTEMEFFECT))); *count = 0;
        if (!*effects) return E_OUTOFMEMORY;
        **effects = {GateEffectId, FALSE, AUDIO_SYSTEMEFFECT_STATE_ON}; *count = 1; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetAudioSystemEffectState(GUID, AUDIO_SYSTEMEFFECT_STATE) override { return E_NOTIMPL; }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** in,
        UINT32 outputs, APO_CONNECTION_PROPERTY** out) override;
};

#pragma AVRT_CODE_BEGIN
void NoopMfx::APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** in, UINT32 outputs, APO_CONNECTION_PROPERTY** out) {
    if (outputs != 1 || !out || !out[0]) return;
    auto& dst = *out[0];
    if (inputs != 1 || !in || !in[0] || !m_bIsLocked || in[0]->u32ValidFrameCount > maxFrames) {
        dst.u32ValidFrameCount = 0; dst.u32BufferFlags = BUFFER_INVALID; return;
    }
    const auto& src = *in[0]; ++calls;
    if (src.u32BufferFlags == BUFFER_INVALID) { dst.u32ValidFrameCount = 0; dst.u32BufferFlags = BUFFER_INVALID; return; }
    const size_t bytes = size_t(src.u32ValidFrameCount) * bytesPerFrame;
    if (bytes && !dst.pBuffer) { dst.u32ValidFrameCount = 0; dst.u32BufferFlags = BUFFER_INVALID; return; }
    if (src.u32BufferFlags == BUFFER_SILENT) { if (bytes) std::memset(reinterpret_cast<void*>(dst.pBuffer), 0, bytes); }
    else if (src.u32BufferFlags == BUFFER_VALID && (!bytes || src.pBuffer)) {
        if (bytes && src.pBuffer != dst.pBuffer) std::memmove(reinterpret_cast<void*>(dst.pBuffer), reinterpret_cast<const void*>(src.pBuffer), bytes);
#if GATE_QUARTER_GAIN
        if (fixedGain != 1.0f) { // Bypass preserves float bits, including signed zero/NaN payloads.
            auto samples = reinterpret_cast<float*>(dst.pBuffer);
            for (size_t i = 0; i < bytes / sizeof(float); ++i) samples[i] *= fixedGain;
        }
#endif
    } else { dst.u32ValidFrameCount = 0; dst.u32BufferFlags = BUFFER_INVALID; return; }
    frames += src.u32ValidFrameCount; dst.u32ValidFrameCount = src.u32ValidFrameCount; dst.u32BufferFlags = src.u32BufferFlags;
}
#pragma AVRT_CODE_END

OBJECT_ENTRY_AUTO(GateClsid, NoopMfx)
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    return _AtlModule.DllGetClassObject(clsid, iid, result);
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return _AtlModule.DllCanUnloadNow(); }
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved) {
    const BOOL ok = _AtlModule.DllMain(reason, reserved);
    if (!ok) return FALSE;
    if (reason == DLL_PROCESS_ATTACH) TraceLoggingRegister(GateProvider);
    else if (reason == DLL_PROCESS_DETACH) TraceLoggingUnregister(GateProvider);
    return TRUE;
}
