#include <windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include <servprov.h>
#include <baseaudioprocessingobject.h>
#include <initguid.h>
#include <audioengineextensionapo.h>
#include <TraceLoggingProvider.h>
#include <mutex>
#include <stdexcept>
#include "../ApoGate/gate_ids.h"
#include "settings.h"
#include "nc-worker.h"
using namespace dm::apo;
TRACELOGGING_DEFINE_PROVIDER(ProductionProvider,"DotMic.ProductionMfx",
    (0x75669aaf,0xe7a1,0x4dbd,0x9d,0x1e,0x79,0x0f,0x0d,0x19,0x50,0x0b));
class ProductionModule:public ATL::CAtlDllModuleT<ProductionModule>{};
ProductionModule _AtlModule;
static void releaseNcModule(){_AtlModule.Unlock();}
static const APO_REG_PROPERTIES Registration{GateClsid,APO_FLAG_DEFAULT,L"DOT MIC Capture MFX",L"DOT MIC (MIT)",
    1,0,1,1,1,1,ULONG_MAX,1,{__uuidof(IAudioProcessingObject)}};
class ATL_NO_VTABLE ProductionMfx:
    public ATL::CComObjectRootEx<ATL::CComMultiThreadModel>,public ATL::CComCoClass<ProductionMfx,&GateClsid>,
    public CBaseAudioProcessingObject,public IAudioSystemEffects3,public IAudioProcessingObjectNotifications {
    Shell shell;Snapshot snapshot;
    ComPtr<IMMDevice> endpoint;
    ComPtr<IAudioSystemEffectsPropertyStore> effects;
    ComPtr<IAudioProcessingObjectRTQueueService> queueService;
    ComPtr<NcWorker> worker;
    DWORD queueId=0;HRESULT queueResult=E_NOINTERFACE;
    std::mutex settingsMutex; // Initialize/notifications/Unlock only, NEVER APOProcess.
    Values values{};GUID mode=GUID_NULL;
    wchar_t endpointId[256]{};
    UINT32 maxFrames=0;bool discovery=false;
    std::atomic<uint64_t> processTicks{0},processMaximum{0},invalidPackets{0};
    std::atomic<uint64_t> liveCalls{0},liveFrames{0};
    std::atomic<uint32_t> liveInput{0},liveOutput{0},liveReduction{0},liveGain{0},liveGate{0},running{0};
    static uint32_t bits(float value) noexcept {uint32_t b;std::memcpy(&b,&value,4);return b;}
    static float real(uint32_t b) noexcept {float value;std::memcpy(&value,&b,4);return value;}
    void meters(bool percentiles=false) noexcept { // OS notification/configuration thread only.
        if(!effects||!liveCalls.load(std::memory_order_relaxed))return;ComPtr<IPropertyStore> store;if(FAILED(effects->OpenVolatilePropertyStore(STGM_READWRITE,&store)))return;
        for(DWORD id=Calls;id<=ConsumedGain;++id){PROPVARIANT v{};
            if(id==Calls||id==Frames){v.vt=VT_UI8;v.uhVal.QuadPart=(id==Calls?liveCalls:liveFrames).load(std::memory_order_relaxed);}
            else if(id==GateOpen||id==Running){v.vt=VT_UI4;v.ulVal=(id==GateOpen?liveGate:running).load(std::memory_order_relaxed);}
            else {v.vt=VT_R4;v.fltVal=real((id==InputPeak?liveInput:id==OutputPeak?liveOutput:id==LimiterGain?liveReduction:liveGain).load(std::memory_order_relaxed));}
            if(FAILED(store->SetValue(key(id),v)))return;
        }store->Commit();
        {auto d=worker?worker->diagnostics(percentiles):NcDiagnostics{};if(!worker){d.error=queueResult;d.state=values.nc?NcState::FaultBypassed:NcState::Off;}
            const uint64_t counts[]={uint32_t(d.state),uint32_t(d.error),d.runs,d.stft,d.istft,d.stateUpdates,d.resets,d.wetPublished,d.adopted,d.fallback,d.jobs,d.generation,d.epoch,d.highWater,d.faults,d.discarded,processTicks.load(),processMaximum.load(),invalidPackets.load()};
            for(DWORD i=0;i<std::size(counts);++i){PROPVARIANT v{};v.vt=VT_UI8;v.uhVal.QuadPart=counts[i];store->SetValue(key(NcStatus+i),v);}
            for(DWORD i=0;i<(percentiles?2u:1u);++i){PROPVARIANT v{};v.vt=VT_R8;v.dblVal=i?d.p99Ms:d.meanMs;store->SetValue(key(NcHopMean+i),v);}store->Commit();
        }
    }
    void refresh() noexcept {
        std::lock_guard<std::mutex> lock(settingsMutex);
        Values next;HRESULT hr=readValues(effects.Get(),next);
        if(SUCCEEDED(hr)){values=next;if(worker)worker->request(next.nc&&!next.bypass);snapshot.publish(prepare(next));}
        TraceLoggingWrite(ProductionProvider,"Parameters",TraceLoggingHResult(hr,"ReadSettings"),
            TraceLoggingFloat32(values.gainDb,"GainDb"),TraceLoggingUInt32(values.bypass,"MasterBypass"),
            TraceLoggingUInt32(values.gate,"GateEnabled"),TraceLoggingUInt32(values.nc,"NcEnabled"),
            TraceLoggingFloat32(values.thresholdDb,"GateThresholdDbfs"),TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this),"Instance"));
    }
public:
    ProductionMfx():CBaseAudioProcessingObject(&Registration){}
    DECLARE_NO_REGISTRY()
    DECLARE_AGGREGATABLE(ProductionMfx)
    DECLARE_PROTECT_FINAL_CONSTRUCT()
    BEGIN_COM_MAP(ProductionMfx)
        COM_INTERFACE_ENTRY(IAudioProcessingObject)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectRT)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectConfiguration)
        COM_INTERFACE_ENTRY(IAudioSystemEffects)
        COM_INTERFACE_ENTRY(IAudioSystemEffects2)
        COM_INTERFACE_ENTRY(IAudioSystemEffects3)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectNotifications)
    END_COM_MAP()
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size,BYTE* data) override {
        if(m_bIsInitialized)return APOERR_ALREADY_INITIALIZED;
        if(!data||size!=sizeof(APOInitSystemEffects3))return E_INVALIDARG;
        auto init=reinterpret_cast<APOInitSystemEffects3*>(data);
        if(init->APOInit.cbSize!=size||init->APOInit.clsid!=GateClsid)return E_INVALIDARG;
        mode=init->AudioProcessingMode;discovery=init->InitializeForDiscoveryOnly!=FALSE;
        if(init->pDeviceCollection){UINT count=0;if(SUCCEEDED(init->pDeviceCollection->GetCount(&count))&&count)
            init->pDeviceCollection->Item(count-1,&endpoint);}
        HRESULT settingsHr=E_NOINTERFACE,queueHr=E_NOINTERFACE;
        if(endpoint){LPWSTR id=nullptr;if(SUCCEEDED(endpoint->GetId(&id))){wcsncpy_s(endpointId,id,_TRUNCATE);CoTaskMemFree(id);}
            settingsHr=openEffects(endpoint.Get(),&effects);if(SUCCEEDED(settingsHr))refresh();}
        if(init->pServiceProvider)queueHr=init->pServiceProvider->QueryService(SID_AudioProcessingObjectRTQueue,__uuidof(IAudioProcessingObjectRTQueueService),reinterpret_cast<void**>(queueService.GetAddressOf()));
        queueResult=queueHr;if(SUCCEEDED(queueHr))queueResult=queueService->GetRealTimeWorkQueue(&queueId);
        TraceLoggingWrite(ProductionProvider,"Initialize",TraceLoggingHResult(settingsHr,"SettingsStore"),TraceLoggingHResult(queueHr,"RTQueueService"),
            TraceLoggingWideString(endpointId,"EndpointId"),TraceLoggingGuid(mode,"Mode"),TraceLoggingBoolean(discovery,"DiscoveryOnly"),
            TraceLoggingHResult(queueResult,"RTQueueIdResult"),TraceLoggingUInt32(queueId,"RTQueueId"));
        // An unavailable settings bridge must not prevent ordinary audio. Safe defaults, NC OFF.
        m_bIsInitialized=true;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* latency) override {
        if(!latency)return E_POINTER;*latency=ProcessingDelay::hns;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs,APO_CONNECTION_DESCRIPTOR** in,UINT32 outputs,APO_CONNECTION_DESCRIPTOR** out) override {
        HRESULT hr=CBaseAudioProcessingObject::LockForProcess(inputs,in,outputs,out);if(FAILED(hr))return hr;
        UNCOMPRESSEDAUDIOFORMAT format{};
        if(FAILED(in[0]->pFormat->GetUncompressedAudioFormat(&format))||format.guidFormatType!=KSDATAFORMAT_SUBTYPE_IEEE_FLOAT||
            GetBytesPerSampleContainer()!=4||GetSamplesPerFrame()<1||GetSamplesPerFrame()>2||GetFramesPerSecond()!=48000){
            CBaseAudioProcessingObject::UnlockForProcess();return APOERR_FORMAT_NOT_SUPPORTED;}
        try{shell.lock(GetSamplesPerFrame());}catch(...){CBaseAudioProcessingObject::UnlockForProcess();return E_OUTOFMEMORY;}
        maxFrames=std::min(in[0]->u32MaxFrameCount,out[0]->u32MaxFrameCount);
        if(effects)refresh();
        liveCalls=liveFrames=0;liveInput=liveOutput=0;liveReduction=liveGain=bits(1);liveGate=0;running=1;
        try {
            std::lock_guard<std::mutex> control(settingsMutex);
            if(SUCCEEDED(queueResult)){
                HMODULE self=nullptr;wchar_t path[32768]{};
                if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<PCWSTR>(&Registration),&self))throw std::runtime_error("Module directory");
                auto length=GetModuleFileNameW(self,path,DWORD(std::size(path)));if(!length||length>=std::size(path))throw std::runtime_error("Module path");
                std::wstring directory(path,length);directory.resize(directory.find_last_of(L"\\/"));
                static std::atomic<uint64_t> epoch{0};_AtlModule.Lock();
                try{worker.Attach(new NcWorker(directory,releaseNcModule));}catch(...){_AtlModule.Unlock();throw;}
                auto readyResult=worker->start(queueId,GetSamplesPerFrame(),++epoch);
                if(FAILED(readyResult)){worker->stop();worker.Reset();queueResult=readyResult;}
            }
        }catch(...){if(worker){worker->stop();worker.Reset();}queueResult=E_FAIL;}
        processTicks=processMaximum=invalidPackets=0;
        if(effects)refresh();
        TraceLoggingWrite(ProductionProvider,"ProcessLocked",TraceLoggingUInt32(GetCurrentProcessId(),"HostPid"),
            TraceLoggingUInt32(ProcessingDelay::total,"DelaySamples"),TraceLoggingUInt32(GetSamplesPerFrame(),"Channels"),
            TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this),"Instance"),TraceLoggingWideString(endpointId,"EndpointId"));return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        HRESULT hr=CBaseAudioProcessingObject::UnlockForProcess();if(FAILED(hr))return hr;
        std::lock_guard<std::mutex> lock(settingsMutex);
        if(worker)worker->stop();
        running=0;meters();
        if(worker){const auto d=worker->diagnostics(true);TraceLoggingWrite(ProductionProvider,"NcSummary",
            TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this),"Instance"),TraceLoggingWideString(endpointId,"EndpointId"),
            TraceLoggingUInt64(d.runs,"Runs"),TraceLoggingUInt64(d.stft,"Stft"),TraceLoggingUInt64(d.stateUpdates,"StateUpdates"),
            TraceLoggingUInt64(d.wetPublished,"WetPublished"),TraceLoggingUInt64(d.adopted,"WetAdoptedFrames"),TraceLoggingUInt64(d.fallback,"FallbackFrames"),
            TraceLoggingUInt64(d.faults,"Faults"),TraceLoggingUInt64(d.highWater,"QueueHighWater"),
            TraceLoggingFloat64(d.meanMs,"HopMeanMs"),TraceLoggingFloat64(d.p99Ms,"HopP99Ms"));}
        TraceLoggingWrite(ProductionProvider,"StreamSummary",TraceLoggingUInt64(shell.counters.calls,"Calls"),
            TraceLoggingUInt64(shell.counters.frames,"Frames"),TraceLoggingUInt64(shell.counters.nonfinite,"Nonfinite"),
            TraceLoggingUInt64(shell.counters.parameterMisses,"SnapshotMisses"),TraceLoggingFloat32(shell.counters.inputPeak,"InputPeak"),
            TraceLoggingFloat32(shell.counters.outputPeak,"OutputPeak"),TraceLoggingFloat32(shell.counters.reduction,"LimiterGain"),
            TraceLoggingHexUInt64(reinterpret_cast<UINT_PTR>(this),"Instance"),TraceLoggingWideString(endpointId,"EndpointId"));return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetApoNotificationRegistrationInfo(APO_NOTIFICATION_DESCRIPTOR** notifications,DWORD* count) override {
        if(!notifications||!count)return E_POINTER;*notifications=nullptr;*count=0;if(!endpoint||discovery)return S_OK;
        auto n=static_cast<APO_NOTIFICATION_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(APO_NOTIFICATION_DESCRIPTOR)));if(!n)return E_OUTOFMEMORY;
        *n={};n->type=APO_NOTIFICATION_TYPE_SYSTEM_EFFECTS_PROPERTY_CHANGE;
        n->audioSystemEffectsPropertyChange.device=endpoint.Get();endpoint->AddRef();
        n->audioSystemEffectsPropertyChange.propertyStoreContext=Context;*notifications=n;*count=1;return S_OK;
    }
    void STDMETHODCALLTYPE HandleNotification(APO_NOTIFICATION* notification) override {
        if(notification&&notification->type==APO_NOTIFICATION_TYPE_SYSTEM_EFFECTS_PROPERTY_CHANGE){
            const auto& n=notification->audioSystemEffectsPropertyChange;
            if(n.propertyStoreContext==Context&&n.propertyKey.fmtid==PropertyFormat){
                if(n.propertyKey.pid<=GateHysteresisDb)refresh();
                else if(n.propertyKey.pid==RequestMeters){std::lock_guard<std::mutex> lock(settingsMutex);meters();}
                else if(n.propertyKey.pid==RequestPerformance){std::lock_guard<std::mutex> lock(settingsMutex);meters(true);}
                else if(n.propertyKey.pid==RequestNcFault){std::lock_guard<std::mutex> lock(settingsMutex);if(worker)worker->diagnosticFault();}
            }
        }
    }
    HRESULT STDMETHODCALLTYPE GetEffectsList(GUID** list,UINT* count,HANDLE) override {
        if(!list||!count)return E_POINTER;*count=0;*list=static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
        if(!*list)return E_OUTOFMEMORY;**list=GateEffectId;*count=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** list,UINT* count,HANDLE) override {
        if(!list||!count)return E_POINTER;*count=0;*list=static_cast<AUDIO_SYSTEMEFFECT*>(CoTaskMemAlloc(sizeof(AUDIO_SYSTEMEFFECT)));
        if(!*list)return E_OUTOFMEMORY;**list={GateEffectId,FALSE,AUDIO_SYSTEMEFFECT_STATE_ON};*count=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetAudioSystemEffectState(GUID,AUDIO_SYSTEMEFFECT_STATE) override {return E_NOTIMPL;}
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs,APO_CONNECTION_PROPERTY** in,UINT32 outputs,APO_CONNECTION_PROPERTY** out) override;
    ~ProductionMfx(){if(worker)worker->stop();}
};
#pragma AVRT_CODE_BEGIN
void ProductionMfx::APOProcess(UINT32 inputs,APO_CONNECTION_PROPERTY** in,UINT32 outputs,APO_CONNECTION_PROPERTY** out){
    LARGE_INTEGER started{},finished{};QueryPerformanceCounter(&started);
    if(outputs!=1||!out||!out[0])return;
    auto& dst=*out[0];
    if(inputs!=1||!in||!in[0]||!m_bIsLocked||in[0]->u32ValidFrameCount>maxFrames){++invalidPackets;dst.u32ValidFrameCount=0;dst.u32BufferFlags=BUFFER_INVALID;if(worker)worker->diagnosticFault();return;}
    const auto& src=*in[0];const uint32_t count=src.u32ValidFrameCount;
    if(src.u32BufferFlags==BUFFER_INVALID||(count&&!dst.pBuffer)||(src.u32BufferFlags!=BUFFER_SILENT&&src.u32BufferFlags!=BUFFER_VALID)||
        (src.u32BufferFlags==BUFFER_VALID&&count&&!src.pBuffer)){++invalidPackets;dst.u32ValidFrameCount=0;dst.u32BufferFlags=BUFFER_INVALID;if(worker)worker->diagnosticFault();return;}
    shell.process(reinterpret_cast<const float*>(src.pBuffer),reinterpret_cast<float*>(dst.pBuffer),count,src.u32BufferFlags==BUFFER_SILENT,snapshot,
        [this](uint64_t time,const float* source,const float* dry,float* selected,uint32_t channels,const Parameters& p) noexcept {
            if(worker)worker->select(time,source,dry,selected,p);else std::memcpy(selected,dry,channels*sizeof(float));
        });
    if(worker)worker->endPacket();
    liveCalls.store(shell.counters.calls,std::memory_order_relaxed);liveFrames.store(shell.counters.frames,std::memory_order_relaxed);
    liveInput.store(bits(shell.counters.inputPeak),std::memory_order_relaxed);liveOutput.store(bits(shell.counters.outputPeak),std::memory_order_relaxed);
    liveReduction.store(bits(shell.counters.reduction),std::memory_order_relaxed);liveGain.store(bits(shell.currentGain()),std::memory_order_relaxed);
    liveGate.store(shell.counters.gateOpen?1:0,std::memory_order_relaxed);
    dst.u32ValidFrameCount=count;dst.u32BufferFlags=BUFFER_VALID; // A silent input can have delayed non-silent output.
    QueryPerformanceCounter(&finished);auto ticks=uint64_t(finished.QuadPart-started.QuadPart);processTicks.fetch_add(ticks,std::memory_order_relaxed);
    auto maximum=processMaximum.load(std::memory_order_relaxed);if(ticks>maximum)processMaximum.store(ticks,std::memory_order_relaxed);
}
#pragma AVRT_CODE_END
OBJECT_ENTRY_AUTO(GateClsid,ProductionMfx)
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** result){return _AtlModule.DllGetClassObject(clsid,iid,result);}
extern "C" HRESULT WINAPI DllCanUnloadNow(){return _AtlModule.DllCanUnloadNow();}
BOOL WINAPI DllMain(HINSTANCE,DWORD reason,LPVOID reserved){
    BOOL ok=_AtlModule.DllMain(reason,reserved);if(!ok)return FALSE;
    if(reason==DLL_PROCESS_ATTACH)TraceLoggingRegister(ProductionProvider);
    else if(reason==DLL_PROCESS_DETACH)TraceLoggingUnregister(ProductionProvider);return TRUE;
}
