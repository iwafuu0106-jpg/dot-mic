#include <windows.h>
#include <initguid.h>
#include "endpoint.h"
#include <functiondiscoverykeys_devpkey.h>
#ifdef DOTMIC_COMMUNITY_CONTROL
#include "../Community/control-target.h"
#endif
#include "control-api.h"
using namespace dm::apo;
namespace {
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
HRESULT store(ComPtr<IMMDevice>& endpoint,ComPtr<IAudioSystemEffectsPropertyStore>& effects){
#ifdef DOTMIC_COMMUNITY_CONTROL
    auto hr=communityEndpoint(&endpoint);
#else
    auto hr=targetEndpoint(L"{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}",&endpoint);
#endif
    return FAILED(hr)?hr:openEffects(endpoint.Get(),&effects);
}
double diagnostic(IPropertyStore* s,DWORD id,double fallback=0){PROPVARIANT v{};if(FAILED(s->GetValue(key(id),&v)))return fallback;double value=fallback;
    if(v.vt==VT_R4)value=v.fltVal;else if(v.vt==VT_R8)value=v.dblVal;else if(v.vt==VT_UI4)value=v.ulVal;else if(v.vt==VT_UI8)value=double(v.uhVal.QuadPart);PropVariantClear(&v);return value;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_read(ControlValues* values,ControlStatus* status,wchar_t* name,uint32_t capacity,uint32_t requestMeters) noexcept {
    if(!values||!status||values->size!=sizeof(ControlValues)||status->size!=sizeof(ControlStatus)||!name||!capacity)return E_INVALIDARG;
    try {
        Apartment apartment;if(FAILED(apartment.hr)&&apartment.hr!=RPC_E_CHANGED_MODE)return apartment.hr;
        ComPtr<IMMDevice> endpoint;ComPtr<IAudioSystemEffectsPropertyStore> effects;auto hr=store(endpoint,effects);if(FAILED(hr))return hr;
        Values v;hr=readValues(effects.Get(),v);if(FAILED(hr))return hr;
        *values={sizeof(ControlValues),1,v.gainDb,v.thresholdDb,v.hysteresisDb,v.attackMs,v.holdMs,v.releaseMs,v.bypass,v.gate,v.nc};
        ComPtr<IPropertyStore> properties;if(SUCCEEDED(endpoint->OpenPropertyStore(STGM_READ,&properties))){PROPVARIANT n{};
            if(SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName,&n))&&n.vt==VT_LPWSTR)wcsncpy_s(name,capacity,n.pwszVal,_TRUNCATE);PropVariantClear(&n);}
        ComPtr<IPropertyStore> s;hr=effects->OpenVolatilePropertyStore(STGM_READWRITE,&s);if(FAILED(hr))return hr;
        if(requestMeters){PROPVARIANT r{};r.vt=VT_UI8;r.uhVal.QuadPart=GetTickCount64();hr=s->SetValue(key(RequestMeters),r);if(SUCCEEDED(hr))hr=s->Commit();if(FAILED(hr))return hr;
            Sleep(20); // Ordinary settings Task only; no audio RT, no hidden polling.
        }
        *status=ControlStatus{};status->input=float(diagnostic(s.Get(),InputPeak));status->output=float(diagnostic(s.Get(),OutputPeak));status->limiter=float(diagnostic(s.Get(),LimiterGain,1));
        status->running=uint32_t(diagnostic(s.Get(),Running));status->gate=uint32_t(diagnostic(s.Get(),GateOpen));status->nc=uint32_t(diagnostic(s.Get(),NcStatus));
        status->runs=uint64_t(diagnostic(s.Get(),NcRuns));status->adopted=uint64_t(diagnostic(s.Get(),NcAdopted));status->fallback=uint64_t(diagnostic(s.Get(),NcFallback));status->faults=uint64_t(diagnostic(s.Get(),NcFaults));return S_OK;
    }catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_set(uint32_t property,float value,uint32_t userCommit) noexcept {
    if(property<MasterBypass||property>GateHysteresisDb)return E_INVALIDARG;
    try{Apartment apartment;if(FAILED(apartment.hr)&&apartment.hr!=RPC_E_CHANGED_MODE)return apartment.hr;
        ComPtr<IMMDevice> endpoint;ComPtr<IAudioSystemEffectsPropertyStore> effects;auto hr=store(endpoint,effects);return FAILED(hr)?hr:setValue(effects.Get(),property,value,userCommit!=0);
    }catch(...){return E_FAIL;}
}
