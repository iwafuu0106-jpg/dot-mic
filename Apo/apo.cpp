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
#include <map>
#include "../ApoGate/gate_ids.h"
#include "settings.h"
#include "nc-worker.h"
#include "format.h"
#include "diagnostics.h"
#include "initialization.h"
using namespace dm::apo;
TRACELOGGING_DEFINE_PROVIDER(ProductionProvider,"DotMic.ProductionMfx",
    (0x75669aaf,0xe7a1,0x4dbd,0x9d,0x1e,0x79,0x0f,0x0d,0x19,0x50,0x0b));
class ProductionModule:public ATL::CAtlDllModuleT<ProductionModule>{};
ProductionModule _AtlModule;
static void releaseNcModule(){_AtlModule.Unlock();}
// Diagnostic snapshots only, never DSP/model state. All access is non-RT.
static std::mutex diagnosticMutex;
struct DiagnosticEntry {std::wstring endpoint;MeterRecord record;};
static std::map<const void*,DiagnosticEntry> diagnosticEntries;
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
    wchar_t endpointId[4097]{};uint64_t streamEpoch=0;
    UINT32 maxFrames=0;bool discovery=false,modeObserved=false,dspSupported=false;StreamFormat streamFormat;
    std::atomic<uint64_t> processTicks{0},processMaximum{0},invalidPackets{0};
    std::atomic<uint64_t> liveCalls{0},liveFrames{0};
    std::atomic<uint32_t> liveInput{0},liveOutput{0},liveReduction{0},liveGain{0},liveGate{0},running{0};
    static uint32_t bits(float value) noexcept {uint32_t b;std::memcpy(&b,&value,4);return b;}
    static float real(uint32_t b) noexcept {float value;std::memcpy(&value,&b,4);return value;}
    void meters(bool percentiles=false) noexcept { // OS notification/configuration thread only.
        if(!endpointId[0])return; // Never merge unresolved identities across endpoints.
        if(!effects&&endpoint)openEffects(endpoint.Get(),&effects); // Bounded retry outside audio RT.
        if(!effects)return;
        try {
        auto d=worker?worker->diagnostics(percentiles):NcDiagnostics{};
        if(!worker){bool requested=values.nc&&!values.bypass&&mode!=AUDIO_SIGNALPROCESSINGMODE_RAW;d.error=requested?(dspSupported?queueResult:APOERR_FORMAT_NOT_SUPPORTED):S_OK;d.state=requested?NcState::FaultBypassed:NcState::Off;}
        MeterRecord local;local.active=running.load()!=0;local.calls=liveCalls;local.frames=liveFrames;
        local.input=real(liveInput);local.output=real(liveOutput);local.limiter=real(liveReduction);local.gain=real(liveGain);local.gate=liveGate;
        local.nc={uint32_t(d.state),uint32_t(d.error),d.runs,d.stft,d.istft,d.stateUpdates,d.resets,d.wetPublished,d.adopted,d.fallback,d.jobs,d.generation,d.epoch,d.highWater,d.faults,d.discarded,processTicks.load(),processMaximum.load(),invalidPackets.load()};local.mean=d.meanMs;local.p99=d.p99Ms;
        // Serialize publication through lifecycle changes: unlocking A cannot
        // write Running=0 over active B. No pointer to another worker is read.
        std::lock_guard<std::mutex> guard(diagnosticMutex);
        local.dsp=dspSupported;local.epoch=streamEpoch;local.reason=!modeObserved?3u:mode==AUDIO_SIGNALPROCESSINGMODE_RAW?1u:dspSupported?0u:2u;
        diagnosticEntries[this]={endpointId,local};MeterRecord aggregate;
        for(const auto& entry:diagnosticEntries)if(entry.second.endpoint==endpointId)mergeMeter(aggregate,entry.second.record);
        ComPtr<IPropertyStore> store;if(FAILED(effects->OpenVolatilePropertyStore(STGM_READWRITE,&store)))return;
        static uint64_t sequence=0;const auto publishing=++sequence*2;
        PROPVARIANT stamp{};stamp.vt=VT_UI8;stamp.uhVal.QuadPart=publishing-1;
        if(FAILED(store->SetValue(key(ObservationSequence),stamp))||FAILED(store->Commit()))return;
        for(DWORD id=Calls;id<=ConsumedGain;++id){PROPVARIANT v{};
            if(id==Calls||id==Frames){v.vt=VT_UI8;v.uhVal.QuadPart=id==Calls?aggregate.calls:aggregate.frames;}
            else if(id==GateOpen||id==Running){v.vt=VT_UI4;v.ulVal=id==GateOpen?aggregate.gate:uint32_t(aggregate.active);}
            else {v.vt=VT_R4;v.fltVal=id==InputPeak?aggregate.input:id==OutputPeak?aggregate.output:id==LimiterGain?aggregate.limiter:aggregate.gain;}
            if(FAILED(store->SetValue(key(id),v)))return;
        }if(FAILED(store->Commit()))return;
        {const auto& counts=aggregate.nc;
            for(DWORD i=0;i<std::size(counts);++i){PROPVARIANT v{};v.vt=VT_UI8;v.uhVal.QuadPart=counts[i];if(FAILED(store->SetValue(key(NcStatus+i),v)))return;}
            for(DWORD i=0;i<(percentiles?2u:1u);++i){PROPVARIANT v{};v.vt=VT_R8;v.dblVal=i?aggregate.p99:aggregate.mean;if(FAILED(store->SetValue(key(NcHopMean+i),v)))return;}if(FAILED(store->Commit()))return;
        }
        PROPVARIANT request{};uint64_t acknowledged=0;
        if(SUCCEEDED(store->GetValue(key(RequestMeters),&request))&&request.vt==VT_UI8)acknowledged=request.uhVal.QuadPart;
        PropVariantClear(&request);
        const uint64_t metadata[]={2,GetCurrentProcessId(),aggregate.epoch,GetTickCount64(),aggregate.dsp?1u:0u,aggregate.reason,acknowledged};
        for(DWORD index=0;index<std::size(metadata);++index){PROPVARIANT value{};value.vt=VT_UI8;value.uhVal.QuadPart=metadata[index];if(FAILED(store->SetValue(key(ObservationVersion+index),value)))return;}
        stamp.uhVal.QuadPart=aggregate.dspFrames;if(FAILED(store->SetValue(key(ObservationDspFrames),stamp)))return;
        stamp.uhVal.QuadPart=publishing;if(FAILED(store->SetValue(key(ObservationSequence),stamp)))return;
        store->Commit();
        }catch(...){/* Diagnostics must never prevent ordinary audio. */}
    }
    void refresh() noexcept {
        std::lock_guard<std::mutex> lock(settingsMutex);
        Values next;HRESULT hr=readCommonValues(next);
        if(FAILED(hr)){next=Values{};next.bypass=1;} // Invalid/missing authority is transparent, never legacy-selected.
        values=next;if(worker)worker->request(next.nc&&!next.bypass&&dspSupported);snapshot.publish(prepare(next));
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
        EffectsInitialization init;auto parsed=parseEffectsInitialization(size,data,init);if(FAILED(parsed))return parsed;
        mode=init.mode;modeObserved=init.modeObserved;discovery=init.discovery;
        if(init.devices){UINT count=0;if(SUCCEEDED(init.devices->GetCount(&count))&&count)init.devices->Item(count-1,&endpoint);}
        HRESULT settingsHr=E_NOINTERFACE,queueHr=E_NOINTERFACE;
        if(endpoint){LPWSTR id=nullptr;if(SUCCEEDED(endpoint->GetId(&id))){if(id&&wcsnlen_s(id,std::size(endpointId))<std::size(endpointId))wcscpy_s(endpointId,id);CoTaskMemFree(id);}
            settingsHr=openEffects(endpoint.Get(),&effects);}
        refresh();
        if(init.services)queueHr=init.services->QueryService(SID_AudioProcessingObjectRTQueue,__uuidof(IAudioProcessingObjectRTQueueService),reinterpret_cast<void**>(queueService.GetAddressOf()));
        queueResult=queueHr;if(SUCCEEDED(queueHr))queueResult=queueService->GetRealTimeWorkQueue(&queueId);
        TraceLoggingWrite(ProductionProvider,"Initialize",TraceLoggingHResult(settingsHr,"SettingsStore"),TraceLoggingHResult(queueHr,"RTQueueService"),
            TraceLoggingWideString(endpointId,"EndpointId"),TraceLoggingGuid(mode,"Mode"),TraceLoggingBoolean(discovery,"DiscoveryOnly"),
            TraceLoggingHResult(queueResult,"RTQueueIdResult"),TraceLoggingUInt32(queueId,"RTQueueId"),TraceLoggingBoolean(init.modeObserved,"ModeObserved"),TraceLoggingUInt32(size,"InitSize"));
        // An unavailable settings bridge must not prevent ordinary audio. Safe defaults, NC OFF.
        m_bIsInitialized=true;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* latency) override {
        if(!latency)return E_POINTER;*latency=dspSupported?ProcessingDelay::hns:0;return S_OK;
    }
    HRESULT ValidateDefaultAPOFormat(UNCOMPRESSEDAUDIOFORMAT& format,bool) override {StreamFormat parsed;return describe(format,parsed)?S_OK:APOERR_FORMAT_NOT_SUPPORTED;}
    HRESULT supportedFormat(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) noexcept {
        if(!supported)return E_POINTER;*supported=nullptr;StreamFormat candidate,other;
        if(!describeMedia(requested,candidate)||(opposite&&(!describeMedia(opposite,other)||!(candidate==other))))return APOERR_FORMAT_NOT_SUPPORTED;
        requested->AddRef();*supported=requested;return S_OK; // Never claims this APO performs SRC/PCM conversion.
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* output,IAudioMediaType* requested,IAudioMediaType** supported) override {return supportedFormat(output,requested,supported);}
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* input,IAudioMediaType* requested,IAudioMediaType** supported) override {return supportedFormat(input,requested,supported);}
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs,APO_CONNECTION_DESCRIPTOR** in,UINT32 outputs,APO_CONNECTION_DESCRIPTOR** out) override {
        if(inputs!=1||outputs!=1||!in||!out||!in[0]||!out[0])return E_INVALIDARG;
        StreamFormat inputFormat,outputFormat;
        if(!describeMedia(in[0]->pFormat,inputFormat)||!describeMedia(out[0]->pFormat,outputFormat)||!(inputFormat==outputFormat))return APOERR_FORMAT_NOT_SUPPORTED;
        HRESULT hr=CBaseAudioProcessingObject::LockForProcess(inputs,in,outputs,out);if(FAILED(hr))return hr;
        streamFormat=inputFormat;dspSupported=modeObserved&&streamFormat.dsp48()&&mode!=AUDIO_SIGNALPROCESSINGMODE_RAW;
        try{if(dspSupported)shell.lock(streamFormat.channels);}catch(...){CBaseAudioProcessingObject::UnlockForProcess();return E_OUTOFMEMORY;}
        maxFrames=std::min(in[0]->u32MaxFrameCount,out[0]->u32MaxFrameCount);
        refresh();
        static std::atomic<uint64_t> epoch{0};streamEpoch=++epoch;
        liveCalls=liveFrames=0;liveInput=liveOutput=0;liveReduction=liveGain=bits(1);liveGate=0;running=1;
        try {
            std::lock_guard<std::mutex> control(settingsMutex);
            if(dspSupported&&SUCCEEDED(queueResult)){
                HMODULE self=nullptr;wchar_t path[32768]{};
                if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<PCWSTR>(&Registration),&self))throw std::runtime_error("Module directory");
                auto length=GetModuleFileNameW(self,path,DWORD(std::size(path)));if(!length||length>=std::size(path))throw std::runtime_error("Module path");
                std::wstring directory(path,length);directory.resize(directory.find_last_of(L"\\/"));
                _AtlModule.Lock();
                try{worker.Attach(new NcWorker(directory,releaseNcModule));}catch(...){_AtlModule.Unlock();throw;}
                auto readyResult=worker->start(queueId,GetSamplesPerFrame(),streamEpoch);
                if(FAILED(readyResult)){worker->stop();worker.Reset();queueResult=readyResult;}
            }
        }catch(...){if(worker){worker->stop();worker.Reset();}queueResult=E_FAIL;}
        processTicks=processMaximum=invalidPackets=0;
        refresh();
        {std::lock_guard<std::mutex> control(settingsMutex);meters();}
        TraceLoggingWrite(ProductionProvider,"ProcessLocked",TraceLoggingUInt32(GetCurrentProcessId(),"HostPid"),
            TraceLoggingUInt32(dspSupported?ProcessingDelay::total:0,"DelaySamples"),TraceLoggingUInt32(streamFormat.channels,"Channels"),
            TraceLoggingUInt32(streamFormat.rate,"SampleRate"),TraceLoggingBoolean(dspSupported,"DspSupported"),
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
        worker.Reset();
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
        if(mode==AUDIO_SIGNALPROCESSINGMODE_RAW){if(!list||!count)return E_POINTER;*list=nullptr;*count=0;return S_OK;}
        if(!list||!count)return E_POINTER;*count=0;*list=static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
        if(!*list)return E_OUTOFMEMORY;**list=GateEffectId;*count=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** list,UINT* count,HANDLE) override {
        if(mode==AUDIO_SIGNALPROCESSINGMODE_RAW){if(!list||!count)return E_POINTER;*list=nullptr;*count=0;return S_OK;}
        if(!list||!count)return E_POINTER;*count=0;*list=static_cast<AUDIO_SYSTEMEFFECT*>(CoTaskMemAlloc(sizeof(AUDIO_SYSTEMEFFECT)));
        if(!*list)return E_OUTOFMEMORY;**list={GateEffectId,FALSE,AUDIO_SYSTEMEFFECT_STATE_ON};*count=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetAudioSystemEffectState(GUID,AUDIO_SYSTEMEFFECT_STATE) override {return E_NOTIMPL;}
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs,APO_CONNECTION_PROPERTY** in,UINT32 outputs,APO_CONNECTION_PROPERTY** out) override;
    ~ProductionMfx(){if(worker)worker->stop();running=0;meters();std::lock_guard<std::mutex> guard(diagnosticMutex);diagnosticEntries.erase(this);}
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
    if(!dspSupported){
        if(!transparent(streamFormat,reinterpret_cast<const void*>(src.pBuffer),reinterpret_cast<void*>(dst.pBuffer),count,src.u32BufferFlags==BUFFER_SILENT)){++invalidPackets;dst.u32ValidFrameCount=0;dst.u32BufferFlags=BUFFER_INVALID;return;}
        liveCalls.fetch_add(1,std::memory_order_relaxed);liveFrames.fetch_add(count,std::memory_order_relaxed);
        dst.u32ValidFrameCount=count;dst.u32BufferFlags=src.u32BufferFlags;return;
    }
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
