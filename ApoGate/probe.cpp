#include <windows.h>
#include <initguid.h>
#include <servprov.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <devicetopology.h>
#include <audioengineextensionapo.h>
#include <audiomediatype.h>
#include <propkey.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <ks.h>
#include <ksmedia.h>
#include <iostream>
#include <iomanip>
#include <string>
#include <vector>
#include <cstring>
#include <stdexcept>
#include "gate_ids.h"
#include "stableid-key.h"
using Microsoft::WRL::ComPtr;
static void check(HRESULT hr, const char* what) {
    if (FAILED(hr)) { std::cerr << what << ": HRESULT 0x" << std::hex << unsigned(hr) << '\n'; throw std::runtime_error(what); }
}
static std::string utf8(const wchar_t* value) {
    if (!value) return {};
    const int length = int(wcslen(value));
    int count = WideCharToMultiByte(CP_UTF8, 0, value, length, nullptr, 0, nullptr, nullptr);
    std::string result(size_t(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, length, result.data(), count, nullptr, nullptr); return result;
}
static std::string json(const wchar_t* value) {
    std::string out = "\"";
    for (unsigned char c : utf8(value)) {
        if (c == '"' || c == '\\') { out += '\\'; out += char(c); }
        else if (c < 32) { const char* h = "0123456789abcdef"; out += "\\u00"; out += h[c >> 4]; out += h[c & 15]; }
        else out += char(c);
    }
    return out + '"';
}
static void property(IPropertyStore* store, REFPROPERTYKEY key, const char* name) {
    PROPVARIANT value{}; HRESULT hr = store->GetValue(key, &value);
    std::cout << "  \"" << name << "\": {\"hresult\": " << unsigned(hr) << ", \"vt\": " << value.vt << ", \"value\": ";
    if (FAILED(hr) || value.vt == VT_EMPTY) std::cout << "null";
    else if (value.vt == VT_LPWSTR) std::cout << json(value.pwszVal);
    else if (value.vt == VT_BOOL) std::cout << (value.boolVal != VARIANT_FALSE ? "true" : "false");
    else if (value.vt == VT_UI4) std::cout << value.ulVal;
    else if (value.vt == VT_CLSID && value.puuid) { wchar_t text[40]{}; StringFromGUID2(*value.puuid, text, 40); std::cout << json(text); }
    else std::cout << "null";
    std::cout << "}"; PropVariantClear(&value);
}
static std::wstring connectedInterface(IMMDevice* device) {
    ComPtr<IDeviceTopology> topology;
    if (FAILED(device->Activate(__uuidof(IDeviceTopology), CLSCTX_ALL, nullptr, &topology))) return {};
    ComPtr<IConnector> connector;
    if (FAILED(topology->GetConnector(0, &connector))) return {};
    LPWSTR connected = nullptr;
    if (FAILED(connector->GetDeviceIdConnectedTo(&connected))) return {};
    std::wstring result = connected ? connected : L""; CoTaskMemFree(connected); return result;
}
static std::wstring resolveTarget(const wchar_t* stableId = nullptr) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "enumerator");
    if (stableId) {
        ComPtr<IMMDevice> cached;
        if (SUCCEEDED(enumerator->GetDevice(stableId, &cached)) &&
            _wcsicmp(connectedInterface(cached.Get()).c_str(), TargetKsInterface) == 0) {
            DWORD state = 0;
            if (SUCCEEDED(cached->GetState(&state)) && state == DEVICE_STATE_ACTIVE) {
                LPWSTR id = nullptr; check(cached->GetId(&id), "resolved runtime endpoint ID");
                std::wstring result = id; CoTaskMemFree(id); return result;
            }
        }
        // Missing/changed stable ID: re-enumerate the exact physical interface below.
    }
    ComPtr<IMMDeviceCollection> devices;
    check(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &devices), "active capture endpoints");
    UINT count = 0; check(devices->GetCount(&count), "capture count");
    std::wstring result;
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device; check(devices->Item(i, &device), "capture item");
        if (_wcsicmp(connectedInterface(device.Get()).c_str(), TargetKsInterface)) continue;
        if (!result.empty()) throw std::runtime_error("Ambiguous physical microphone; do not select by name");
        LPWSTR id = nullptr; check(device->GetId(&id), "runtime endpoint ID"); result = id; CoTaskMemFree(id);
    }
    if (result.empty()) throw std::runtime_error("Target physical microphone not present");
    return result;
}
static void listCapture() {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "enumerator");
    ComPtr<IMMDeviceCollection> devices;
    check(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATEMASK_ALL, &devices), "capture endpoints");
    UINT count = 0; check(devices->GetCount(&count), "capture count");
    std::cout << "[\n";
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device; check(devices->Item(i, &device), "capture item");
        LPWSTR id = nullptr; check(device->GetId(&id), "capture ID");
        DWORD state = 0; check(device->GetState(&state), "capture state");
        ComPtr<IPropertyStore> store; check(device->OpenPropertyStore(STGM_READ, &store), "capture properties");
        std::cout << "{\n  \"endpointId\": " << json(id) << ",\n  \"state\": " << state << ",\n";
        CoTaskMemFree(id);
        property(store.Get(), PKEY_Device_FriendlyName, "friendlyName"); std::cout << ",\n";
        property(store.Get(), PKEY_AudioEndpoint_Association, "association"); std::cout << ",\n";
        property(store.Get(), PKEY_Device_ContainerId, "containerId"); std::cout << ",\n";
        property(store.Get(), GateStableIdKey, "stableId"); std::cout << ",\n";
        property(store.Get(), PKEY_Devices_AudioDevice_RawProcessingSupported, "rawProcessingSupported"); std::cout << ",\n";
        std::cout << "  \"connectedDeviceId\": " << json(connectedInterface(device.Get()).c_str());
        std::cout << "\n}" << (i + 1 < count ? ",\n" : "\n");
    }
    std::cout << "]\n"; // Property reads only; no audio-client activation or capture.
}
static std::string hrText(HRESULT hr) {
    char text[11]{}; sprintf_s(text, "0x%08X", unsigned(hr)); return text;
}
static int inspect(const wchar_t* target) {
    ComPtr<IMMDeviceEnumerator> enumerator; check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "enumerator");
    ComPtr<IMMDevice> device; const HRESULT deviceHr = enumerator->GetDevice(target, &device);
    if (FAILED(deviceHr)) {
        std::cout << "{\"requestedId\": " << json(target) << ", \"GetDevice\": \"" << hrText(deviceHr)
            << "\", \"ActivateIAudioClient\": \"NOT_REACHED\", \"GetMixFormat\": \"NOT_REACHED\"}\n";
        return 1;
    }
    LPWSTR runtime = nullptr; check(device->GetId(&runtime), "GetId");
    const std::wstring runtimeId = runtime; CoTaskMemFree(runtime);
    ComPtr<IMMEndpoint> endpoint; check(device.As(&endpoint), "endpoint flow"); EDataFlow flow{}; check(endpoint->GetDataFlow(&flow), "GetDataFlow");
    if (flow != eCapture) throw std::runtime_error("Target is not capture");
    DWORD state = 0; check(device->GetState(&state), "state");
    ComPtr<IPropertyStore> store; check(device->OpenPropertyStore(STGM_READ, &store), "read endpoint property store");
    std::cout << "{\n  \"endpointId\": " << json(runtimeId.c_str()) << ",\n  \"GetDevice\": \"" << hrText(deviceHr)
        << "\",\n  \"dataFlow\": \"capture\",\n  \"state\": " << state << ",\n";
    property(store.Get(), PKEY_Device_FriendlyName, "friendlyName"); std::cout << ",\n";
    property(store.Get(), PKEY_Devices_AudioDevice_RawProcessingSupported, "rawProcessingSupported"); std::cout << ",\n";
    property(store.Get(), PKEY_AudioEndpoint_Association, "association"); std::cout << ",\n";
    property(store.Get(), PKEY_AudioEndpoint_GUID, "endpointGuid"); std::cout << ",\n";
    property(store.Get(), PKEY_Device_ContainerId, "containerId"); std::cout << ",\n";
    property(store.Get(), GateStableIdKey, "stableId"); std::cout << ",\n";
    property(store.Get(), PKEY_AudioEndpoint_Disable_SysFx, "systemEffectsDisabled"); std::cout << ",\n";
    ComPtr<IAudioClient> client;
    const HRESULT activateHr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client);
    std::cout << "  \"ActivateIAudioClient\": \"" << hrText(activateHr) << "\",\n";
    WAVEFORMATEX* format = nullptr;
    const HRESULT formatHr = SUCCEEDED(activateHr) ? client->GetMixFormat(&format) : E_UNEXPECTED;
    std::cout << "  \"GetMixFormat\": \"" << (SUCCEEDED(activateHr) ? hrText(formatHr) : "NOT_REACHED") << "\",\n";
    std::cout << "  \"mixFormat\": ";
    if (SUCCEEDED(formatHr) && format) std::cout << "{\"sampleRate\": " << format->nSamplesPerSec << ", \"channels\": " << format->nChannels
        << ", \"bitsPerSample\": " << format->wBitsPerSample << ", \"blockAlign\": " << format->nBlockAlign << "}";
    else std::cout << "null";
    std::cout << ",\n";
    CoTaskMemFree(format); // No Initialize/Start/GetBuffer: inspect never captures microphone audio.
    ComPtr<IDeviceTopology> topology; check(device->Activate(__uuidof(IDeviceTopology), CLSCTX_ALL, nullptr, &topology), "topology");
    ComPtr<IConnector> connector; check(topology->GetConnector(0, &connector), "connector");
    LPWSTR connected = nullptr; check(connector->GetDeviceIdConnectedTo(&connected), "connected KS interface");
    std::cout << "  \"connectedDeviceId\": " << json(connected) << ",\n"; CoTaskMemFree(connected);
    ComPtr<IConnector> hardware; check(connector->GetConnectedTo(&hardware), "hardware connector");
    ComPtr<IPart> part; check(hardware.As(&part), "hardware part"); GUID subtype{};
    check(part->GetSubType(&subtype), "hardware category"); wchar_t subtypeText[40]{}; StringFromGUID2(subtype, subtypeText, 40);
    std::cout << "  \"hardwareConnectorSubtype\": " << json(subtypeText) << "\n}\n";
    return FAILED(activateHr) || FAILED(formatHr) ? 1 : 0;
}
static void captureSessions() {
    const std::wstring target = resolveTarget();
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "session enumerator");
    ComPtr<IMMDevice> device; check(enumerator->GetDevice(target.c_str(), &device), "session endpoint");
    ComPtr<IAudioSessionManager2> manager;
    check(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, &manager), "capture session manager");
    ComPtr<IAudioSessionEnumerator> sessions; check(manager->GetSessionEnumerator(&sessions), "capture sessions");
    int count = 0; check(sessions->GetCount(&count), "session count");
    std::cout << "{\"endpointId\": " << json(target.c_str()) << ", \"sessions\": [\n";
    for (int i = 0; i < count; ++i) {
        ComPtr<IAudioSessionControl> control; check(sessions->GetSession(i, &control), "session control");
        ComPtr<IAudioSessionControl2> control2; check(control.As(&control2), "session control2");
        DWORD pid = 0; const HRESULT pidHr = control2->GetProcessId(&pid);
        check(pidHr, "session owner PID"); // AUDCLNT_S_NO_SINGLE_PROCESS is success, but not sole ownership.
        AudioSessionState state{}; check(control->GetState(&state), "session state");
        LPWSTR instance = nullptr; check(control2->GetSessionInstanceIdentifier(&instance), "session instance ID");
        std::cout << "{\"pid\": " << pid << ", \"pidHresult\": \"" << hrText(pidHr)
            << "\", \"soleProcess\": " << (pidHr == S_OK ? "true" : "false")
            << ", \"state\": " << int(state) << ", \"instanceId\": " << json(instance) << "}";
        CoTaskMemFree(instance); std::cout << (i + 1 < count ? ",\n" : "\n");
    }
    std::cout << "], \"sampleRecording\": false, \"opensCaptureStream\": false}\n";
    // Enumeration only: never Initialize/Start, meter, GetBuffer, or change session state.
}
static void captureSmoke(const wchar_t* target) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "enumerator");
    ComPtr<IMMDevice> device; check(enumerator->GetDevice(target, &device), "smoke endpoint");
    ComPtr<IMMEndpoint> endpoint; check(device.As(&endpoint), "smoke flow");
    EDataFlow flow{}; check(endpoint->GetDataFlow(&flow), "smoke flow");
    if (flow != eCapture) throw std::runtime_error("Smoke target must be capture");
    ComPtr<IAudioClient> client; check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client), "smoke client");
    WAVEFORMATEX* format = nullptr; check(client->GetMixFormat(&format), "smoke format");
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 1000000, 0, format, nullptr);
    CoTaskMemFree(format); check(hr, "smoke Initialize");
    ComPtr<IAudioCaptureClient> capture; check(client->GetService(IID_PPV_ARGS(&capture)), "smoke service");
    UINT64 frames = 0; check(client->Start(), "smoke Start");
    try {
        const ULONGLONG end = GetTickCount64() + 500;
        while (GetTickCount64() < end) {
            Sleep(20); // Diagnostic thread only, never part of APOProcess.
            UINT32 packet = 0; check(capture->GetNextPacketSize(&packet), "smoke packet");
            while (packet) {
                BYTE* data = nullptr; UINT32 count = 0; DWORD flags = 0;
                check(capture->GetBuffer(&data, &count, &flags, nullptr, nullptr), "smoke buffer");
                frames += count; // Never read, copy, play or persist any microphone sample.
                check(capture->ReleaseBuffer(count), "smoke release");
                check(capture->GetNextPacketSize(&packet), "smoke next packet");
            }
        }
    } catch (...) { client->Stop(); throw; }
    check(client->Stop(), "smoke Stop");
    if (!frames) throw std::runtime_error("No smoke capture frames");
    std::cout << "{\"endpointId\": " << json(target) << ", \"captureFrames\": " << frames
        << ", \"sampleRecording\": false, \"discordTest\": false, \"apoGatePass\": false}\n";
}
// Minimal controlling unknown for testing the real COM aggregation contract.
// The outer owns the non-delegating inner IUnknown, not the returned APO interfaces.
class TestOuter final : public IUnknown {
public:
    ULONG references = 1;
    ComPtr<IUnknown> inner;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == __uuidof(IUnknown)) { *result = static_cast<IUnknown*>(this); AddRef(); return S_OK; }
        return inner ? inner->QueryInterface(iid, result) : E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { return --references; } // Stack-owned by this test.
};
static void aggregationTest(IClassFactory* factory) {
    TestOuter outer;
    check(factory->CreateInstance(&outer, __uuidof(IUnknown), reinterpret_cast<void**>(outer.inner.GetAddressOf())), "aggregated creation");
    const IID interfaces[] = {__uuidof(IAudioProcessingObject), __uuidof(IAudioProcessingObjectRT),
        __uuidof(IAudioProcessingObjectConfiguration), __uuidof(IAudioSystemEffects),
        __uuidof(IAudioSystemEffects2), __uuidof(IAudioSystemEffects3)};
    for (const auto& iid : interfaces) {
        ComPtr<IUnknown> effect;
        check(outer.inner->QueryInterface(iid, reinterpret_cast<void**>(effect.GetAddressOf())), "aggregated interface");
        ComPtr<IUnknown> identity; check(effect->QueryInterface(IID_PPV_ARGS(&identity)), "controlling unknown");
        if (identity.Get() != static_cast<IUnknown*>(&outer)) throw std::runtime_error("Aggregation lost controlling identity");
    }
    if (outer.references != 1) throw std::runtime_error("Unbalanced delegated references");
    void* illegal = nullptr;
    const HRESULT hr = factory->CreateInstance(&outer, __uuidof(IAudioProcessingObject), &illegal);
    if (hr != CLASS_E_NOAGGREGATION || illegal) throw std::runtime_error("Aggregated creation must request only IUnknown");
    outer.inner.Reset();
    if (outer.references != 1) throw std::runtime_error("Outer reference ownership");
}
static void selftest(const wchar_t* path, bool quarterGain = false) {
    HMODULE module = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw std::runtime_error("LoadLibraryEx gate DLL (absolute path required)");
    auto getFactory = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
    auto canUnload = reinterpret_cast<HRESULT(WINAPI*)()>(GetProcAddress(module, "DllCanUnloadNow"));
    if (!getFactory || !canUnload) throw std::runtime_error("COM exports");
    {
        ComPtr<IClassFactory> factory; check(getFactory(GateClsid, IID_PPV_ARGS(&factory)), "factory");
        aggregationTest(factory.Get());
        ComPtr<IAudioProcessingObject> apo; check(factory->CreateInstance(nullptr, IID_PPV_ARGS(&apo)), "APO creation");
        ComPtr<IAudioProcessingObjectConfiguration> config; check(apo.As(&config), "configuration");
        ComPtr<IAudioProcessingObjectRT> rt; check(apo.As(&rt), "RT");
        ComPtr<IAudioSystemEffects3> effects; check(apo.As(&effects), "CAPX effects interface");
        APOInitSystemEffects3 init{}; init.APOInit.clsid = GateClsid; init.APOInit.cbSize = sizeof(init);
        init.AudioProcessingMode = AUDIO_SIGNALPROCESSINGMODE_COMMUNICATIONS;
        check(apo->Initialize(sizeof(init), reinterpret_cast<BYTE*>(&init)), "synthetic COMMUNICATIONS init (not Discord)");
        GUID* list = nullptr; UINT count = 0; check(effects->GetEffectsList(&list, &count, nullptr), "effects list");
        const bool listed = count == 1 && list && list[0] == GateEffectId; CoTaskMemFree(list);
        if (!listed) throw std::runtime_error("private diagnostic effect");
        WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT, 2, 48000, 384000, 8, 32, 0};
        ComPtr<IAudioMediaType> media; check(CreateAudioMediaType(&wave, sizeof(wave), &media), "test format");
        alignas(16) float input[960], output[960];
        for (UINT32 i = 0; i < 960; ++i) input[i] = float(int(i) - 480) / 1024;
        APO_CONNECTION_DESCRIPTOR in{}, out{};
        in.Type = out.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
        in.u32Signature = out.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
        in.pBuffer = reinterpret_cast<UINT_PTR>(input); out.pBuffer = reinterpret_cast<UINT_PTR>(output);
        in.u32MaxFrameCount = out.u32MaxFrameCount = 480; in.pFormat = out.pFormat = media.Get();
        APO_CONNECTION_DESCRIPTOR* ins[] = {&in}; APO_CONNECTION_DESCRIPTOR* outs[] = {&out};
        check(config->LockForProcess(1, ins, 1, outs), "LockForProcess");
        HNSTIME latency = -1; check(apo->GetLatency(&latency), "no-op latency");
        if (latency != 0 || rt->CalcInputFrames(480) != 480 || rt->CalcOutputFrames(480) != 480) throw std::runtime_error("no-op frame timing");
        APO_CONNECTION_PROPERTY src{}, dst{};
        src.u32Signature = dst.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;
        src.pBuffer = in.pBuffer; dst.pBuffer = out.pBuffer; src.u32ValidFrameCount = 480; src.u32BufferFlags = BUFFER_VALID;
        APO_CONNECTION_PROPERTY* sources[] = {&src}; APO_CONNECTION_PROPERTY* destinations[] = {&dst};
        rt->APOProcess(1, sources, 1, destinations);
        const float expectedGain = quarterGain ? 0.25f : 1.0f;
        if (dst.u32ValidFrameCount != 480 || dst.u32BufferFlags != BUFFER_VALID) throw std::runtime_error("output frame/flag");
        for (UINT32 i = 0; i < 960; ++i) if (output[i] != input[i] * expectedGain) throw std::runtime_error("exact fixed-gain PCM (all channels)");
        if (!quarterGain && std::memcmp(input, output, sizeof(input))) throw std::runtime_error("bit-exact copy");
        dst.pBuffer = src.pBuffer; rt->APOProcess(1, sources, 1, destinations);
        if (dst.u32BufferFlags != BUFFER_VALID) throw std::runtime_error("in-place copy");
        for (UINT32 i = 0; i < 960; ++i) if (input[i] != float(int(i) - 480) / 1024 * expectedGain) throw std::runtime_error("in-place fixed gain");
        dst.pBuffer = out.pBuffer; src.u32BufferFlags = BUFFER_SILENT;
        std::memset(output, 1, sizeof(output)); rt->APOProcess(1, sources, 1, destinations);
        for (float v : output) if (v != 0) throw std::runtime_error("silent output");
        if (dst.u32BufferFlags != BUFFER_SILENT) throw std::runtime_error("silent flag");
        src.u32BufferFlags = BUFFER_INVALID; rt->APOProcess(1, sources, 1, destinations);
        if (dst.u32ValidFrameCount != 0 || dst.u32BufferFlags != BUFFER_INVALID) throw std::runtime_error("invalid flag");
        src.u32BufferFlags = BUFFER_VALID; src.u32ValidFrameCount = 0;
        rt->APOProcess(1, sources, 1, destinations);
        if (dst.u32ValidFrameCount != 0 || dst.u32BufferFlags != BUFFER_VALID) throw std::runtime_error("zero frames");
        check(config->UnlockForProcess(), "UnlockForProcess");
    }
    if (canUnload() != S_OK) throw std::runtime_error("live COM object at unload");
    FreeLibrary(module);
    std::cout << "PASS normal/aggregated COM creation, interface QI, controlling IUnknown, delegated Release, unload.\n"
        << (quarterGain ? "PASS fixed0.25 FP32 / all-channel / in-place / silent / invalid / zero-frame checks.\n" :
            "PASS local synthetic no-op FP32 copy/in-place/silent/invalid/zero-frame checks.\n")
        << "NOT TESTED by this command: installation, audio-engine load, physical microphone, Discord PCM delivery.\n";
}
int wmain(int argc, wchar_t** argv) {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED); if (FAILED(hr)) return 1;
    int result = 0;
    try {
        if (argc == 2 && wcscmp(argv[1], L"inspect") == 0) result = inspect(resolveTarget().c_str());
        else if (argc == 3 && wcscmp(argv[1], L"inspect") == 0) result = inspect(argv[2]);
        else if (argc == 3 && wcscmp(argv[1], L"inspect-current") == 0) result = inspect(resolveTarget(argv[2]).c_str());
        else if (argc == 2 && wcscmp(argv[1], L"list-capture") == 0) listCapture();
        else if (argc == 2 && wcscmp(argv[1], L"capture-sessions") == 0) captureSessions();
        else if (argc == 3 && wcscmp(argv[1], L"capture-smoke") == 0) captureSmoke(argv[2]);
        else if (argc == 3 && wcscmp(argv[1], L"selftest") == 0) selftest(argv[2]);
        else if (argc == 3 && wcscmp(argv[1], L"selftest-quarter") == 0) selftest(argv[2], true);
        else { std::cerr << "Usage: DotMic.ApoProbe inspect [endpoint ID] | inspect-current <cached stable ID> | list-capture | capture-sessions | capture-smoke <endpoint ID> | selftest|selftest-quarter <absolute gate DLL path>\n"; result = 2; }
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; result = 1; }
    CoUninitialize(); return result;
}
