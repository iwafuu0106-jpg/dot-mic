#pragma once
namespace dm::apo {
struct EffectsInitialization {
    IMMDeviceCollection* devices=nullptr;IServiceProvider* services=nullptr;
    GUID mode=GUID_NULL;bool discovery=false,modeObserved=false;
};
inline HRESULT parseEffectsInitialization(UINT32 size,const BYTE* data,EffectsInitialization& result){
    if(!data||size<sizeof(APOInitBaseStruct))return E_INVALIDARG;
    const auto* base=reinterpret_cast<const APOInitBaseStruct*>(data);
    if(base->cbSize!=size||base->clsid!=GateClsid)return E_INVALIDARG;
    EffectsInitialization next;
    if(size==sizeof(APOInitSystemEffects3)){
        const auto* init=reinterpret_cast<const APOInitSystemEffects3*>(data);
        next.devices=init->pDeviceCollection;next.services=init->pServiceProvider;
        next.mode=init->AudioProcessingMode;next.modeObserved=true;next.discovery=init->InitializeForDiscoveryOnly!=FALSE;
    }else if(size==sizeof(APOInitSystemEffects2)){
        const auto* init=reinterpret_cast<const APOInitSystemEffects2*>(data);
        next.devices=init->pDeviceCollection;next.mode=init->AudioProcessingMode;next.modeObserved=true;next.discovery=init->InitializeForDiscoveryOnly!=FALSE;
    }else if(size==sizeof(APOInitSystemEffects))next.devices=reinterpret_cast<const APOInitSystemEffects*>(data)->pDeviceCollection;
    else return E_INVALIDARG;
    result=next;return S_OK;
}
}
