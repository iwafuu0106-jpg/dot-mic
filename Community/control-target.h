#pragma once
#include <string>
#include <vector>
#include "../Apo/endpoint.h"
namespace dm::apo {
// Runtime IDs are opaque. Only a verified GUID suffix may become a Capture registry path.
inline bool captureFxPath(const std::wstring& runtime,std::wstring& path){
    auto start=runtime.find_last_of(L'{');if(start==std::wstring::npos||runtime.size()-start!=38)return false;
    GUID guid{};if(FAILED(CLSIDFromString(runtime.c_str()+start,&guid)))return false;
    wchar_t canonical[40]{};if(!StringFromGUID2(guid,canonical,40))return false;
    path=L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Capture\\"+std::wstring(canonical)+L"\\FxProperties";return true;
}
inline HRESULT communityManaged(IMMDevice* endpoint){
    LPWSTR id=nullptr;auto hr=endpoint->GetId(&id);if(FAILED(hr))return hr;std::wstring path;bool valid=id&&captureFxPath(id,path);CoTaskMemFree(id);if(!valid)return E_INVALIDARG;
    HKEY key=nullptr;auto opened=RegOpenKeyExW(HKEY_LOCAL_MACHINE,path.c_str(),0,KEY_QUERY_VALUE|KEY_WOW64_64KEY,&key);
    if(opened==ERROR_FILE_NOT_FOUND||opened==ERROR_PATH_NOT_FOUND)return S_FALSE;if(opened!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(opened);
    wchar_t clsid[40]{};DWORD type=0,bytes=sizeof(clsid);auto error=RegQueryValueExW(key,L"{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},6",nullptr,&type,reinterpret_cast<BYTE*>(clsid),&bytes);RegCloseKey(key);
    if(error!=ERROR_SUCCESS&&error!=ERROR_FILE_NOT_FOUND&&error!=ERROR_MORE_DATA)return HRESULT_FROM_WIN32(error);
    return error==ERROR_SUCCESS&&type==REG_SZ&&bytes>=2&&bytes<=sizeof(clsid)&&bytes%2==0&&clsid[bytes/2-1]==0&&_wcsicmp(clsid,L"{8F611FC3-1A33-477D-9820-F8B9A11D1030}")==0?S_OK:S_FALSE;
}
inline HRESULT communityEndpoints(std::vector<ComPtr<IMMDevice>>& result){
    ComPtr<IMMDeviceEnumerator> enumerator;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator));if(FAILED(hr))return hr;
    ComPtr<IMMDeviceCollection> collection;hr=enumerator->EnumAudioEndpoints(eCapture,DEVICE_STATE_ACTIVE,&collection);if(FAILED(hr))return hr;
    UINT count=0;hr=collection->GetCount(&count);if(FAILED(hr))return hr;if(count>4096)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    HRESULT partial=S_OK;
    for(UINT i=0;i<count;++i){ComPtr<IMMDevice> device;hr=collection->Item(i,&device);if(SUCCEEDED(hr))hr=communityManaged(device.Get());if(FAILED(hr)){if(SUCCEEDED(partial))partial=hr;continue;}if(hr==S_OK)result.push_back(device);}
    return partial;
}
// Authority was already changed before fanout. Preserve its success and each
// endpoint's independent state; return the first failure for bounded UI retry.
inline HRESULT communityFanout(DWORD id,float value,bool persist){
    std::vector<ComPtr<IMMDevice>> endpoints;auto result=communityEndpoints(endpoints);
    for(auto& endpoint:endpoints){ComPtr<IAudioSystemEffectsPropertyStore> effects;auto hr=openEffects(endpoint.Get(),&effects);
        if(SUCCEEDED(hr))hr=setValue(effects.Get(),id,value,persist);
        if(FAILED(hr)&&SUCCEEDED(result))result=hr;
    }return result;
}
}
