#pragma once
#include <windows.h>
#include <mmdeviceapi.h>
#include <propsys.h>
#include <propvarutil.h>
#include <wrl/client.h>
#include <string>
#include "processing.h"
namespace dm::apo {
using Microsoft::WRL::ComPtr;
inline constexpr GUID Context{0x7dd39e65,0x9848,0x4cde,{0xaf,0x2a,0x91,0x0e,0x9d,0x16,0x03,0x76}};
inline constexpr GUID PropertyFormat{0x91795f52,0x2dc0,0x4e20,{0xa7,0x32,0x58,0x81,0x66,0x72,0xe6,0x35}};
enum Property : DWORD {MasterBypass=1,GainDb,GateEnabled,GateThresholdDbfs,GateAttackMs,GateHoldMs,GateReleaseMs,NcEnabled,GateHysteresisDb};
enum Diagnostic : DWORD {RequestMeters=90,Calls=100,Frames,InputPeak,OutputPeak,LimiterGain,GateOpen,Running,ConsumedGain};
enum ProofDiagnostic : DWORD {ProofStatus=130,ProofResult,ProofRuns,ProofMaxError};
enum NcDiagnostic : DWORD {RequestPerformance=91,RequestNcFault=92,NcStatus=150,NcError,NcRuns,NcStft,NcIstft,NcStateUpdates,NcResets,NcWetPublished,NcAdopted,NcFallback,NcJobs,NcGeneration,NcAudioEpoch,NcHighWater,NcFaults,NcDiscarded,ApoProcessTicks,ApoProcessMaximum,ApoInvalidPackets,NcHopMean=175,NcHopP99};
enum ObservationDiagnostic : DWORD {ObservationVersion=180,ObservationPublisher,ObservationEpoch,ObservationPublished,ObservationDsp,ObservationReason,ObservationAck,ObservationSequence,ObservationDspFrames};
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
inline float propertyValue(const Values& v,DWORD id) noexcept {
    switch(id){case MasterBypass:return float(v.bypass);case GainDb:return v.gainDb;case GateEnabled:return float(v.gate);
    case GateThresholdDbfs:return v.thresholdDb;case GateAttackMs:return v.attackMs;case GateHoldMs:return v.holdMs;
    case GateReleaseMs:return v.releaseMs;case NcEnabled:return float(v.nc);case GateHysteresisDb:return v.hysteresisDb;default:return 0;}
}
inline bool decodeCommonValue(DWORD id,DWORD type,const void* data,DWORD bytes,float& value) noexcept {
    if(id<MasterBypass||id>GateHysteresisDb||type!=REG_BINARY||!data||bytes!=sizeof(float))return false;
    float decoded=0;std::memcpy(&decoded,data,sizeof(decoded));Values check;
    if(!std::isfinite(decoded)||!assign(check,id,decoded)||!validate(check))return false;
    value=decoded;return true;
}
inline constexpr wchar_t CommonSettingsPath[]=L"SOFTWARE\\DOT MIC\\Community\\CommonSettings";
struct CommonKey {
    HKEY handle=nullptr;
    ~CommonKey(){if(handle)RegCloseKey(handle);}
    CommonKey()=default;CommonKey(const CommonKey&)=delete;CommonKey& operator=(const CommonKey&)=delete;
    HRESULT open(const wchar_t* layer,REGSAM access){
        if(!layer||(wcscmp(layer,L"User")&&wcscmp(layer,L"Volatile")))return E_INVALIDARG;
        wchar_t path[128]{};if(swprintf_s(path,L"%s\\%s",CommonSettingsPath,layer)<0)return E_INVALIDARG;
        auto error=RegOpenKeyExW(HKEY_LOCAL_MACHINE,path,0,access|KEY_WOW64_64KEY,&handle);
        if(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        return HRESULT_FROM_WIN32(error);
    }
};
inline HRESULT readCommonProperty(HKEY store,DWORD id,float& value,bool optional=false){
    wchar_t name[12]{};swprintf_s(name,L"%lu",id);DWORD type=0,bytes=sizeof(float);float raw=0;
    auto error=RegQueryValueExW(store,name,nullptr,&type,reinterpret_cast<BYTE*>(&raw),&bytes);
    if(optional&&error==ERROR_FILE_NOT_FOUND)return S_FALSE;
    if(error==ERROR_FILE_NOT_FOUND)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    if(error==ERROR_MORE_DATA)return E_INVALIDARG;
    if(error!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(error);
    return decodeCommonValue(id,type,&raw,bytes,value)?S_OK:E_INVALIDARG;
}
// Strict typed data only: no enumeration, paths, payloads or endpoint-selected authority.
// These keys are provisioned by elevated setup. Readers/writers NEVER create them.
template<class UserRead,class PreviewRead> inline HRESULT resolveCommonValues(UserRead&& user,PreviewRead&& preview,Values& result){
    Values next;
    for(DWORD id=MasterBypass;id<=GateHysteresisDb;++id){float value=0;
        auto hr=user(id,value);if(FAILED(hr))return hr;if(hr!=S_OK)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        float overrideValue=0;hr=preview(id,overrideValue);if(FAILED(hr))return hr;if(hr==S_OK)value=overrideValue;
        if(!assign(next,id,value))return E_INVALIDARG;
    }
    if(!validate(next))return E_INVALIDARG;result=next;return S_OK;
}
inline HRESULT readCommonValues(Values& result){
    CommonKey user,preview;auto hr=user.open(L"User",KEY_QUERY_VALUE);if(FAILED(hr))return hr;
    hr=preview.open(L"Volatile",KEY_QUERY_VALUE);if(FAILED(hr)&&hr!=HRESULT_FROM_WIN32(ERROR_NOT_READY))return hr;
    return resolveCommonValues([&](DWORD id,float& value){return readCommonProperty(user.handle,id,value);},
        [&](DWORD id,float& value){return preview.handle?readCommonProperty(preview.handle,id,value,true):S_FALSE;},result);
}
inline HRESULT setCommonValue(DWORD id,float value,bool persist){
    if(!decodeCommonValue(id,REG_BINARY,&value,sizeof(value),value))return E_INVALIDARG;
    CommonKey user,preview;auto hr=user.open(L"User",KEY_QUERY_VALUE|(persist?KEY_SET_VALUE:0));if(FAILED(hr))return hr;
    hr=preview.open(L"Volatile",KEY_QUERY_VALUE|KEY_SET_VALUE);if(FAILED(hr))return hr;
    wchar_t name[12]{};swprintf_s(name,L"%lu",id);
    auto error=RegSetValueExW(persist?user.handle:preview.handle,name,0,REG_BINARY,reinterpret_cast<const BYTE*>(&value),sizeof(value));
    if(error!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(error);
    if(persist){error=RegDeleteValueW(preview.handle,name);if(error!=ERROR_SUCCESS&&error!=ERROR_FILE_NOT_FOUND)return HRESULT_FROM_WIN32(error);}
    return S_OK;
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
