#include <windows.h>
#include <initguid.h>
#include <audiopolicy.h>
#include <map>
#include <mutex>
#include "../Community/control-target.h"
#include "control-api.h"
#include "observation.h"
using namespace dm::apo;
namespace {
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
HRESULT integer(IPropertyStore* store,DWORD id,uint64_t& value){
    PROPVARIANT raw{};auto hr=store->GetValue(key(id),&raw);
    if(SUCCEEDED(hr)){if(raw.vt==VT_UI8)value=raw.uhVal.QuadPart;else if(raw.vt==VT_UI4)value=raw.ulVal;else hr=HRESULT_FROM_WIN32(ERROR_NOT_READY);}
    PropVariantClear(&raw);return hr;
}
float peak(IPropertyStore* store,DWORD id,float fallback=0){
    PROPVARIANT raw{};float value=fallback;
    if(SUCCEEDED(store->GetValue(key(id),&raw))&&raw.vt==VT_R4&&std::isfinite(raw.fltVal))value=raw.fltVal;
    PropVariantClear(&raw);return value;
}
int sessionActivity(IMMDevice* device){
    ComPtr<IAudioSessionManager2> manager;ComPtr<IAudioSessionEnumerator> sessions;
    if(FAILED(device->Activate(__uuidof(IAudioSessionManager2),CLSCTX_ALL,nullptr,&manager))||FAILED(manager->GetSessionEnumerator(&sessions)))return -1;
    int count=0;if(FAILED(sessions->GetCount(&count))||count<0||count>4096)return -1;
    bool unknown=false;
    for(int i=0;i<count;++i){ComPtr<IAudioSessionControl> session;AudioSessionState state;
        if(FAILED(sessions->GetSession(i,&session))||FAILED(session->GetState(&state)))unknown=true;
        else if(state==AudioSessionStateActive)return 1;
    }return unknown?-1:0;
}
std::mutex observationMutex;
std::map<std::wstring,ObservationBaseline> baselines;
void error(ControlObservation& observation,HRESULT result){if(FAILED(result)&&SUCCEEDED(observation.result))observation.result=result;}
HRESULT read(ControlValues* values,ControlStatus* status,ControlObservation* observation,wchar_t* name,uint32_t capacity,uint32_t requestMeters){
    if(!values||!status||!observation||values->size!=sizeof(ControlValues)||status->size!=sizeof(ControlStatus)||observation->size!=sizeof(ControlObservation)||!name||!capacity)return E_INVALIDARG;
    Values common;auto hr=readCommonValues(common);if(FAILED(hr))return hr;
    *values={sizeof(ControlValues),1,common.gainDb,common.thresholdDb,common.hysteresisDb,common.attackMs,common.holdMs,common.releaseMs,common.bypass,common.gate,common.nc};
    *status=ControlStatus{};*observation=ControlObservation{};wcsncpy_s(name,capacity,L"すべてのマイク",_TRUNCATE);
    Apartment apartment;
    if(FAILED(apartment.hr)&&apartment.hr!=RPC_E_CHANGED_MODE){observation->state=uint32_t(ObservationState::Unavailable);error(*observation,apartment.hr);return S_OK;}
    std::vector<ComPtr<IMMDevice>> endpoints;hr=communityEndpoints(endpoints);error(*observation,hr);
    observation->targets=uint32_t(endpoints.size());bool idle=true,unavailable=FAILED(hr),pending=false,processing=false,passthrough=false;
    for(auto& endpoint:endpoints){
        LPWSTR rawId=nullptr;std::wstring id;
        if(SUCCEEDED(endpoint->GetId(&rawId))&&rawId&&wcsnlen_s(rawId,4097)<=4096)id=rawId;CoTaskMemFree(rawId);
        ComPtr<IAudioSystemEffectsPropertyStore> effects;ComPtr<IPropertyStore> store;
        hr=openEffects(endpoint.Get(),&effects);
        if(SUCCEEDED(hr)&&requestMeters){
            ComPtr<IPropertyStore> request;hr=effects->OpenVolatilePropertyStore(STGM_READWRITE,&request);
            if(SUCCEEDED(hr)){PROPVARIANT value{};value.vt=VT_UI8;value.uhVal.QuadPart=GetTickCount64();hr=request->SetValue(key(RequestMeters),value);if(SUCCEEDED(hr))hr=request->Commit();}
            error(*observation,hr); // Request failure must not discard readable evidence.
        }
        hr=effects?effects->OpenVolatilePropertyStore(STGM_READ,&store):hr;
        if(SUCCEEDED(hr)&&requestMeters)Sleep(20); // Non-RT reader only; not delivery proof.
        uint64_t sequence=0,sequenceAfter=0,running=0,calls=0,totalFrames=0,reason=0,version=0;ObservationSample sample;ControlStatus snapshot;
        HRESULT diagnostic=hr;
        if(SUCCEEDED(diagnostic)&&!id.empty()){
            diagnostic=integer(store.Get(),ObservationSequence,sequence);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationVersion,version);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationPublisher,sample.publisher);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationEpoch,sample.epoch);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationPublished,sample.published);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),Running,running);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),Calls,calls);
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),Frames,totalFrames);
            uint64_t dsp=0;if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationDsp,dsp);
            uint64_t dspFrames=0;if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationDspFrames,dspFrames);
            sample.frames=dsp==1?dspFrames:totalFrames;
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationReason,reason);
            if(SUCCEEDED(diagnostic)){
                snapshot.input=peak(store.Get(),InputPeak);snapshot.output=peak(store.Get(),OutputPeak);snapshot.limiter=peak(store.Get(),LimiterGain,1);
                uint64_t value=0;if(SUCCEEDED(integer(store.Get(),GateOpen,value)))snapshot.gate=uint32_t(value);
                if(SUCCEEDED(integer(store.Get(),NcStatus,value)))snapshot.nc=uint32_t(value);
                integer(store.Get(),NcRuns,snapshot.runs);integer(store.Get(),NcAdopted,snapshot.adopted);
                integer(store.Get(),NcFallback,snapshot.fallback);integer(store.Get(),NcFaults,snapshot.faults);
            }
            if(SUCCEEDED(diagnostic))diagnostic=integer(store.Get(),ObservationSequence,sequenceAfter);
            sample.readable=SUCCEEDED(diagnostic)&&observationSnapshotValid(sequence,sequenceAfter,version,sample.publisher,running,dsp,reason);
            sample.running=running==1;sample.dsp=dsp==1;
            if(!sample.readable&&SUCCEEDED(diagnostic))diagnostic=HRESULT_FROM_WIN32(ERROR_NOT_READY);
        }else if(SUCCEEDED(diagnostic))diagnostic=E_INVALIDARG;
        ObservationState state;
        {std::lock_guard<std::mutex> guard(observationMutex);
            if(baselines.size()>=4096&&!baselines.count(id))baselines.clear();
            state=baselines[id].observe(sample,GetTickCount64());}
        if(!sample.readable||!sample.running){
            int activity=sessionActivity(endpoint.Get());
            if(activity==0)state=ObservationState::Idle;
            else state=sample.readable?ObservationState::Unconfirmed:ObservationState::Unavailable;
        }
        if(sample.readable){++observation->observed;observation->calls+=calls;observation->frames+=totalFrames;}
        else {++observation->missing;error(*observation,diagnostic);}
        idle&=state==ObservationState::Idle;unavailable|=state==ObservationState::Unavailable;pending|=state==ObservationState::Unconfirmed;
        processing|=state==ObservationState::Processing;passthrough|=state==ObservationState::Passthrough;
        if(state!=ObservationState::Processing&&state!=ObservationState::Passthrough)continue;
        observation->reason=std::max(observation->reason,uint32_t(reason));status->running=state==ObservationState::Processing?1:status->running;
        status->input=std::max(status->input,snapshot.input);status->output=std::max(status->output,snapshot.output);
        status->limiter=std::min(status->limiter,snapshot.limiter);status->gate|=snapshot.gate;
        auto rank=[](uint32_t s){return s==3?5:s==4?4:s==1?3:s==2?2:0;};if(rank(snapshot.nc)>rank(status->nc))status->nc=snapshot.nc;
        status->runs+=snapshot.runs;status->adopted+=snapshot.adopted;status->fallback+=snapshot.fallback;status->faults+=snapshot.faults;
    }
    observation->state=uint32_t(processing?ObservationState::Processing:passthrough?ObservationState::Passthrough:unavailable?ObservationState::Unavailable:pending||!idle?ObservationState::Unconfirmed:ObservationState::Idle);
    return S_OK;
}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_read_ex(ControlValues* values,ControlStatus* status,ControlObservation* observation,wchar_t* name,uint32_t capacity,uint32_t request) noexcept {
    try{return read(values,status,observation,name,capacity,request);}catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_read(ControlValues* values,ControlStatus* status,wchar_t* name,uint32_t capacity,uint32_t request) noexcept {
    ControlObservation observation;return dm_apo_read_ex(values,status,&observation,name,capacity,request);
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_set(uint32_t property,float value,uint32_t userCommit) noexcept {
    try{auto hr=setCommonValue(property,value,userCommit!=0);if(FAILED(hr))return hr;
        Apartment apartment;if(FAILED(apartment.hr)&&apartment.hr!=RPC_E_CHANGED_MODE)return apartment.hr;
        return communityFanout(property,value,userCommit!=0);
    }catch(...){return E_FAIL;}
}
