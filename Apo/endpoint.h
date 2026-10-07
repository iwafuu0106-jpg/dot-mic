#pragma once
#include "settings.h"
#include <devicetopology.h>
#include "../ApoGate/gate_ids.h"
#include "../ApoGate/stableid-key.h"
namespace dm::apo {
inline bool isTarget(IMMDevice* device){
    ComPtr<IMMEndpoint> flow;EDataFlow direction=eAll;
    if(FAILED(device->QueryInterface(IID_PPV_ARGS(&flow)))||FAILED(flow->GetDataFlow(&direction))||direction!=eCapture)return false;
    ComPtr<IDeviceTopology> topology;ComPtr<IConnector> connector;LPWSTR connected=nullptr;
    if(FAILED(device->Activate(__uuidof(IDeviceTopology),CLSCTX_ALL,nullptr,&topology))||FAILED(topology->GetConnector(0,&connector))||FAILED(connector->GetDeviceIdConnectedTo(&connected)))return false;
    bool match=connected&&_wcsicmp(connected,TargetKsInterface)==0;CoTaskMemFree(connected);return match;
}
inline HRESULT targetEndpoint(const wchar_t* stable,IMMDevice** result){
    *result=nullptr;ComPtr<IMMDeviceEnumerator> enumerator;HRESULT hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator));if(FAILED(hr))return hr;
    if(stable&&*stable){ComPtr<IMMDevice> endpoint;DWORD state=0;
        if(SUCCEEDED(enumerator->GetDevice(stable,&endpoint))&&isTarget(endpoint.Get())&&SUCCEEDED(endpoint->GetState(&state))&&state==DEVICE_STATE_ACTIVE){*result=endpoint.Detach();return S_OK;}}
    ComPtr<IMMDeviceCollection> list;hr=enumerator->EnumAudioEndpoints(eCapture,DEVICE_STATE_ACTIVE,&list);if(FAILED(hr))return hr;
    UINT count=0;hr=list->GetCount(&count);if(FAILED(hr))return hr;ComPtr<IMMDevice> chosen;
    for(UINT i=0;i<count;++i){ComPtr<IMMDevice> endpoint;hr=list->Item(i,&endpoint);if(FAILED(hr))return hr;
        if(isTarget(endpoint.Get())){if(chosen)return HRESULT_FROM_WIN32(ERROR_DUP_NAME);chosen=endpoint;}}
    if(!chosen)return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);*result=chosen.Detach();return S_OK;
}
}
