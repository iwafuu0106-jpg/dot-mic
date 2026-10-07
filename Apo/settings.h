#pragma once
#include <windows.h>
#include <mmdeviceapi.h>
#include <propsys.h>
#include <propvarutil.h>
#include <wrl/client.h>
#include "processing.h"
namespace dm::apo {
using Microsoft::WRL::ComPtr;
inline constexpr GUID Context{0x7dd39e65,0x9848,0x4cde,{0xaf,0x2a,0x91,0x0e,0x9d,0x16,0x03,0x76}};
inline constexpr GUID PropertyFormat{0x91795f52,0x2dc0,0x4e20,{0xa7,0x32,0x58,0x81,0x66,0x72,0xe6,0x35}};
enum Property : DWORD {MasterBypass=1,GainDb,GateEnabled,GateThresholdDbfs,GateAttackMs,GateHoldMs,GateReleaseMs,NcEnabled,GateHysteresisDb};
enum Diagnostic : DWORD {RequestMeters=90,Calls=100,Frames,InputPeak,OutputPeak,LimiterGain,GateOpen,Running,ConsumedGain};
enum ProofDiagnostic : DWORD {ProofStatus=130,ProofResult,ProofRuns,ProofMaxError};
enum NcDiagnostic : DWORD {RequestPerformance=91,RequestNcFault=92,NcStatus=150,NcError,NcRuns,NcStft,NcIstft,NcStateUpdates,NcResets,NcWetPublished,NcAdopted,NcFallback,NcJobs,NcGeneration,NcAudioEpoch,NcHighWater,NcFaults,NcDiscarded,ApoProcessTicks,ApoProcessMaximum,ApoInvalidPackets,NcHopMean=175,NcHopP99};
inline PROPERTYKEY key(DWORD id){return {PropertyFormat,id};}
inline HRESULT openEffects(IMMDevice* endpoint,IAudioSystemEffectsPropertyStore** store){
    PROPVARIANT v{};HRESULT hr=InitPropVariantFromCLSID(Context,&v);
    if(SUCCEEDED(hr))hr=endpoint->Activate(__uuidof(IAudioSystemEffectsPropertyStore),CLSCTX_ALL,&v,reinterpret_cast<void**>(store));
    PropVariantClear(&v);return hr;
}
inline bool number(const PROPVARIANT& v,float& out){
    if(v.vt==VT_R4)out=v.fltVal;else if(v.vt==VT_R8)out=float(v.dblVal);
    else if(v.vt==VT_UI4)out=float(v.ulVal);else if(v.vt==VT_BOOL)out=v.boolVal?1.f:0.f;
    else if(v.vt==VT_LPWSTR&&v.pwszVal&&wcslen(v.pwszVal)<32){wchar_t* end=nullptr;out=wcstof(v.pwszVal,&end);if(!end||*end||end==v.pwszVal)return false;}
    else return false;return std::isfinite(out);
}
inline bool assign(Values& v,DWORD id,float value) noexcept {
    switch(id){
    case MasterBypass:case GateEnabled:case NcEnabled:
        if(value!=0&&value!=1)return false;
        if(id==MasterBypass)v.bypass=uint32_t(value);else if(id==GateEnabled)v.gate=uint32_t(value);else v.nc=uint32_t(value);break;
    case GainDb:v.gainDb=value;break;
    case GateThresholdDbfs:v.thresholdDb=value;break;
    case GateAttackMs:v.attackMs=value;break;
    case GateHoldMs:v.holdMs=value;break;
    case GateReleaseMs:v.releaseMs=value;break;
    case GateHysteresisDb:v.hysteresisDb=value;break;
    default:return false;
    }return true;
}
inline HRESULT readValues(IAudioSystemEffectsPropertyStore* effects,Values& result){
    if(!effects)return E_POINTER;
    ComPtr<IPropertyStore> defaults,user,volatileStore;
    auto absent=[](HRESULT hr){return hr==HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)||hr==HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)||hr==HRESULT_FROM_WIN32(ERROR_NOT_FOUND);};
    HRESULT hr=effects->OpenDefaultPropertyStore(STGM_READ,&defaults);if(FAILED(hr)&&!absent(hr))return hr;
    hr=effects->OpenUserPropertyStore(STGM_READ,&user);if(FAILED(hr)&&!absent(hr))return hr;
    hr=effects->OpenVolatilePropertyStore(STGM_READ,&volatileStore);if(FAILED(hr)&&!absent(hr))return hr;
    Values values{};
    for(DWORD id=MasterBypass;id<=GateHysteresisDb;++id){
        float value=0;bool found=false;
        for(auto store:{volatileStore.Get(),user.Get(),defaults.Get()}){
            if(!store)continue; // A new context need not have User/Volatile keys yet.
            PROPVARIANT v{};hr=store->GetValue(key(id),&v);
            if(SUCCEEDED(hr)&&v.vt!=VT_EMPTY){found=number(v,value);PropVariantClear(&v);if(!found)return E_INVALIDARG;break;}
            PropVariantClear(&v);if(FAILED(hr)&&!absent(hr))return hr;
        }
        if(!found)continue;
        if(!assign(values,id,value))return E_INVALIDARG;
    }
    if(!validate(values))return E_INVALIDARG;result=values;return S_OK;
}
inline HRESULT setValue(IAudioSystemEffectsPropertyStore* effects,DWORD id,float value,bool persist){
    if(id<MasterBypass||id>GateHysteresisDb||!std::isfinite(value))return E_INVALIDARG;
    // Validate only the requested property against the common domain contract.
    // A malformed older property must not prevent correcting it (or another key).
    Values v;HRESULT hr=S_OK;
    if(!assign(v,id,value)||!validate(v))return E_INVALIDARG;
    ComPtr<IPropertyStore> store;
    hr=persist?effects->OpenUserPropertyStore(STGM_READWRITE,&store):effects->OpenVolatilePropertyStore(STGM_READWRITE,&store);
    if(FAILED(hr))return hr;
    PROPVARIANT p{};p.vt=VT_R4;p.fltVal=value;
    hr=store->SetValue(key(id),p);if(SUCCEEDED(hr))hr=store->Commit();if(FAILED(hr)||!persist)return hr;
    // Remove only this property's drag override, not another control's volatile state.
    hr=effects->OpenVolatilePropertyStore(STGM_READWRITE,&store);if(FAILED(hr))return hr;
    PROPVARIANT empty{};hr=store->SetValue(key(id),empty);if(SUCCEEDED(hr))hr=store->Commit();return hr;
}
}
