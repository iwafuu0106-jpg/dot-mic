#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <devicetopology.h>
#include <functiondiscoverykeys_devpkey.h>
#include <audioenginebaseapo.h>
#include <wrl/client.h>
#include <string>
#include <sstream>
#include <vector>
#include <iostream>
#include <iomanip>
#include <locale>
#include "../ApoGate/gate_ids.h"
#include "../ApoGate/stableid-key.h"
#include "../Apo/settings.h"
#include "control-target.h"
#include "endpoint-metadata.h"
using Microsoft::WRL::ComPtr;
namespace {
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
std::wstring jsonQuote(const std::wstring& text){std::wstring result=L"\"";for(wchar_t c:text){if(c==L'\\'||c==L'\"'){result+=L'\\';result+=c;}else if(c<32){wchar_t escaped[8]{};swprintf_s(escaped,L"\\u%04x",unsigned(c));result+=escaped;}else result+=c;}return result+L"\"";}
std::wstring property(IPropertyStore* store,const PROPERTYKEY& key){PROPVARIANT v{};std::wstring text;if(store&&SUCCEEDED(store->GetValue(key,&v))){if(v.vt==VT_LPWSTR&&v.pwszVal){auto length=wcsnlen_s(v.pwszVal,4097);if(length<=4096)text.assign(v.pwszVal,length);}else if(v.vt==VT_CLSID&&v.puuid){wchar_t b[40]{};StringFromGUID2(*v.puuid,b,40);text=b;}}PropVariantClear(&v);return text;}
int formFactor(IPropertyStore* store){PROPVARIANT value{};int result=-1;if(store&&SUCCEEDED(store->GetValue(PKEY_AudioEndpoint_FormFactor,&value))&&value.vt==VT_UI4&&value.ulVal<=UnknownFormFactor)result=int(value.ulVal);PropVariantClear(&value);return result;}
const wchar_t* sharedBusy(IMMDevice* device){
 ComPtr<IAudioSessionManager2> manager;ComPtr<IAudioSessionEnumerator> sessions;
 if(FAILED(device->Activate(__uuidof(IAudioSessionManager2),CLSCTX_ALL,nullptr,&manager))||FAILED(manager->GetSessionEnumerator(&sessions)))return L"null";
 int count=0;if(FAILED(sessions->GetCount(&count))||count<0||count>256)return L"null";bool unknown=false;
 for(int i=0;i<count;++i){ComPtr<IAudioSessionControl> session;AudioSessionState state=AudioSessionStateInactive;if(FAILED(sessions->GetSession(i,&session))||FAILED(session->GetState(&state))){unknown=true;continue;}if(state==AudioSessionStateActive)return L"true";}
 return unknown?L"null":L"false"; // Session activity metadata only; never opens capture.
}
std::wstring connected(IMMDevice* device){ComPtr<IDeviceTopology> t;ComPtr<IConnector> c;LPWSTR id=nullptr;if(FAILED(device->Activate(__uuidof(IDeviceTopology),CLSCTX_ALL,nullptr,&t))||FAILED(t->GetConnector(0,&c))||FAILED(c->GetDeviceIdConnectedTo(&id)))return L"";std::wstring result;if(id){auto length=wcsnlen_s(id,4097);if(length<=4096)result.assign(id,length);}CoTaskMemFree(id);return result;}
std::wstring backingPnpId(IMMDeviceEnumerator* enumerator,const std::wstring& connectedAdapter){
 return dm::community::backingDeviceInstanceId(connectedAdapter,[&](const std::wstring& adapterId){
  ComPtr<IMMDevice> adapter;ComPtr<IPropertyStore> properties;
  if(FAILED(enumerator->GetDevice(adapterId.c_str(),&adapter))||FAILED(adapter->OpenPropertyStore(STGM_READ,&properties)))return std::wstring{};
  return property(properties.Get(),PKEY_Device_InstanceId);
 });
}
HRESULT list(std::wstring& result){Apartment a;if(FAILED(a.hr)&&a.hr!=RPC_E_CHANGED_MODE)return a.hr;ComPtr<IMMDeviceEnumerator> e;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e));if(FAILED(hr))return hr;ComPtr<IMMDeviceCollection> devices;hr=e->EnumAudioEndpoints(eCapture,DEVICE_STATE_ACTIVE,&devices);if(FAILED(hr))return hr;UINT n=0;hr=devices->GetCount(&n);if(FAILED(hr))return hr;if(n>4096)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);result=L"[";bool first=true;
 for(UINT i=0;i<n;++i){ComPtr<IMMDevice> d;if(FAILED(devices->Item(i,&d)))continue;LPWSTR id=nullptr;if(FAILED(d->GetId(&id))||!id)continue;std::wstring runtime=id;CoTaskMemFree(id);std::wstring fx;if(!dm::apo::captureFxPath(runtime,fx))continue;
  ComPtr<IPropertyStore> p;d->OpenPropertyStore(STGM_READ,&p);auto stable=property(p.Get(),GateStableIdKey),container=property(p.Get(),PKEY_Device_ContainerId),name=property(p.Get(),PKEY_Device_FriendlyName),ks=connected(d.Get());DWORD state=DEVICE_STATE_ACTIVE;d->GetState(&state);if(!first)result+=L",";first=false;
  result+=L"{\"EndpointId\":"+jsonQuote(runtime)+L",\"StableId\":"+jsonQuote(stable)+L",\"ContainerId\":"+jsonQuote(container)+L",\"FriendlyName\":"+jsonQuote(name)+L",\"PhysicalInterface\":"+jsonQuote(ks)+L",\"FxPath\":"+jsonQuote(fx)+L",\"FormFactor\":"+std::to_wstring(formFactor(p.Get()))+L",\"PnpId\":"+jsonQuote(backingPnpId(e.Get(),ks))+L",\"EndpointPnpId\":"+jsonQuote(property(p.Get(),PKEY_Device_InstanceId))+L",\"JackSubType\":"+jsonQuote(property(p.Get(),PKEY_AudioEndpoint_JackSubType))+L",\"State\":"+std::to_wstring(state)+L",\"SharedModeBusy\":"+sharedBusy(d.Get())+L"}";
 }result+=L"]";return S_OK;}
HRESULT properties(const wchar_t* path,bool apply,std::wstring* plan=nullptr){if(!path||!*path)return E_INVALIDARG;Apartment a;if(FAILED(a.hr)&&a.hr!=RPC_E_CHANGED_MODE)return a.hr;HMODULE module=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)return HRESULT_FROM_WIN32(GetLastError());HRESULT hr=E_FAIL;{using Factory=HRESULT(WINAPI*)(REFCLSID,REFIID,void**);auto get=reinterpret_cast<Factory>(GetProcAddress(module,"DllGetClassObject"));ComPtr<IClassFactory> factory;ComPtr<IAudioProcessingObject> apo;APO_REG_PROPERTIES* p=nullptr;if(!get)hr=HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);else if(SUCCEEDED(hr=get(GateClsid,IID_PPV_ARGS(&factory)))&&SUCCEEDED(hr=factory->CreateInstance(nullptr,IID_PPV_ARGS(&apo)))&&SUCCEEDED(hr=apo->GetRegistrationProperties(&p))){
  if(!p||p->clsid!=GateClsid||p->Flags!=APO_FLAG_DEFAULT||p->u32NumAPOInterfaces!=1||p->iidAPOInterfaceList[0]!=__uuidof(IAudioProcessingObject)||p->u32MinInputConnections!=1||p->u32MaxInputConnections!=1||p->u32MinOutputConnections!=1||p->u32MaxOutputConnections!=1||p->u32MaxInstances!=ULONG_MAX)hr=E_INVALIDARG;
  else if(apply){hr=RegisterAPO(p);if(SUCCEEDED(hr)){APO_REG_PROPERTIES actual{};actual.u32NumAPOInterfaces=1;hr=GetAPOProperties(GateClsid,&actual);if(SUCCEEDED(hr)&&(actual.clsid!=p->clsid||actual.Flags!=p->Flags||actual.u32MajorVersion!=p->u32MajorVersion||actual.u32MinorVersion!=p->u32MinorVersion||actual.u32MinInputConnections!=p->u32MinInputConnections||actual.u32MaxInputConnections!=p->u32MaxInputConnections||actual.u32MinOutputConnections!=p->u32MinOutputConnections||actual.u32MaxOutputConnections!=p->u32MaxOutputConnections||actual.u32MaxInstances!=p->u32MaxInstances||actual.u32NumAPOInterfaces!=1||actual.iidAPOInterfaceList[0]!=p->iidAPOInterfaceList[0]||wcscmp(actual.szFriendlyName,p->szFriendlyName)!=0||wcscmp(actual.szCopyrightInfo,p->szCopyrightInfo)!=0))hr=E_UNEXPECTED;}}
  else if(plan){wchar_t iid[40]{};StringFromGUID2(p->iidAPOInterfaceList[0],iid,40);std::wostringstream text;text<<L"{\"FriendlyName\":"<<jsonQuote(p->szFriendlyName)<<L",\"Copyright\":"<<jsonQuote(p->szCopyrightInfo)<<L",\"MajorVersion\":"<<p->u32MajorVersion<<L",\"MinorVersion\":"<<p->u32MinorVersion<<L",\"Flags\":"<<unsigned(p->Flags)<<L",\"MinInputConnections\":"<<p->u32MinInputConnections<<L",\"MaxInputConnections\":"<<p->u32MaxInputConnections<<L",\"MinOutputConnections\":"<<p->u32MinOutputConnections<<L",\"MaxOutputConnections\":"<<p->u32MaxOutputConnections<<L",\"MaxInstances\":"<<p->u32MaxInstances<<L",\"NumAPOInterfaces\":"<<p->u32NumAPOInterfaces<<L",\"APOInterface0\":"<<jsonQuote(iid)<<L"}";*plan=text.str();}
  CoTaskMemFree(p);
 }}FreeLibrary(module);return hr;}
HRESULT capture(const wchar_t* id,DWORD milliseconds){if(!id||!*id||milliseconds<500||milliseconds>5000)return E_INVALIDARG;Apartment a;if(FAILED(a.hr)&&a.hr!=RPC_E_CHANGED_MODE)return a.hr;ComPtr<IMMDeviceEnumerator> e;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e));if(FAILED(hr))return hr;ComPtr<IMMDevice> device;hr=e->GetDevice(id,&device);if(FAILED(hr))return hr;ComPtr<IMMEndpoint> endpoint;EDataFlow flow=eAll;if(FAILED(device.As(&endpoint))||FAILED(endpoint->GetDataFlow(&flow))||flow!=eCapture)return E_INVALIDARG;ComPtr<IAudioClient> client;hr=device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client);if(FAILED(hr))return hr;WAVEFORMATEX* mix=nullptr;hr=client->GetMixFormat(&mix);if(FAILED(hr))return hr;HANDLE event=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!event){CoTaskMemFree(mix);return HRESULT_FROM_WIN32(GetLastError());}
 hr=client->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_EVENTCALLBACK,0,0,mix,nullptr);CoTaskMemFree(mix);ComPtr<IAudioCaptureClient> reader;if(SUCCEEDED(hr))hr=client->SetEventHandle(event);if(SUCCEEDED(hr))hr=client->GetService(IID_PPV_ARGS(&reader));if(SUCCEEDED(hr))hr=client->Start();if(SUCCEEDED(hr)){auto until=GetTickCount64()+milliseconds;uint64_t received=0;while(GetTickCount64()<until){auto wait=WaitForSingleObject(event,100);if(wait==WAIT_FAILED){hr=HRESULT_FROM_WIN32(GetLastError());break;}UINT32 frames=0;if(FAILED(hr=reader->GetNextPacketSize(&frames)))break;while(frames){BYTE* discarded=nullptr;DWORD flags=0;UINT32 count=0;if(FAILED(hr=reader->GetBuffer(&discarded,&count,&flags,nullptr,nullptr)))break; // Never reads, records or renders microphone samples.
    received+=count;hr=reader->ReleaseBuffer(count);if(FAILED(hr)||FAILED(hr=reader->GetNextPacketSize(&frames)))break;}if(FAILED(hr))break;}client->Stop();if(SUCCEEDED(hr)&&received==0)hr=HRESULT_FROM_WIN32(ERROR_TIMEOUT);}CloseHandle(event);return hr;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_endpoints(wchar_t* buffer,UINT capacity,UINT* required) noexcept {if(!required)return E_POINTER;try{std::wstring text;auto hr=list(text);if(FAILED(hr))return hr;*required=UINT(text.size()+1);if(!buffer||capacity<*required)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);memcpy(buffer,text.c_str(),*required*sizeof(wchar_t));return S_OK;}catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_common_values(wchar_t* buffer,UINT capacity,UINT* required) noexcept {
 if(!required)return E_POINTER;try{dm::apo::Values values;auto hr=dm::apo::readCommonValues(values);if(FAILED(hr))return hr;
  std::wostringstream text;text.imbue(std::locale::classic());text<<std::setprecision(9)<<L"{";
  for(DWORD id=dm::apo::MasterBypass;id<=dm::apo::GateHysteresisDb;++id){if(id!=dm::apo::MasterBypass)text<<L",";text<<L"\""<<id<<L"\":"<<dm::apo::propertyValue(values,id);}text<<L"}";
  auto json=text.str();*required=UINT(json.size()+1);if(!buffer||capacity<*required)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);memcpy(buffer,json.c_str(),*required*sizeof(wchar_t));return S_OK;
 }catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_registration(const wchar_t* path,UINT apply) noexcept {try{return properties(path,apply!=0);}catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_registration_plan(const wchar_t* path,wchar_t* buffer,UINT capacity) noexcept {if(!path||!buffer||!capacity)return E_INVALIDARG;try{std::wstring plan;auto hr=properties(path,false,&plan);if(FAILED(hr))return hr;if(plan.size()+1>capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);wcscpy_s(buffer,capacity,plan.c_str());return S_OK;}catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_capture(const wchar_t* endpoint,UINT milliseconds) noexcept {try{return capture(endpoint,milliseconds);}catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_observe(const wchar_t* endpointId,wchar_t* buffer,UINT capacity,UINT* required) noexcept {
 if(!required)return E_POINTER;*required=0;if(!endpointId||!*endpointId||wcsnlen_s(endpointId,4097)>4096)return E_INVALIDARG;
 try {
  Apartment apartment;if(FAILED(apartment.hr)&&apartment.hr!=RPC_E_CHANGED_MODE)return apartment.hr;
  ComPtr<IMMDeviceEnumerator> enumerator;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator));if(FAILED(hr))return hr;
  ComPtr<IMMDevice> device;hr=enumerator->GetDevice(endpointId,&device);if(FAILED(hr))return hr;
  ComPtr<IMMEndpoint> endpoint;EDataFlow flow=eAll;hr=device.As(&endpoint);if(FAILED(hr))return hr;
  hr=endpoint->GetDataFlow(&flow);if(FAILED(hr))return hr;if(flow!=eCapture)return E_INVALIDARG;
  auto pending=[](HRESULT result){return result==HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)||result==HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)||result==HRESULT_FROM_WIN32(ERROR_NOT_FOUND)||result==HRESULT_FROM_WIN32(ERROR_NOT_READY);};
  ComPtr<IAudioSystemEffectsPropertyStore> effects;hr=dm::apo::openEffects(device.Get(),&effects);if(FAILED(hr))return pending(hr)?HRESULT_FROM_WIN32(ERROR_NOT_READY):hr;
  ComPtr<IPropertyStore> store;hr=effects->OpenVolatilePropertyStore(STGM_READ,&store);if(FAILED(hr))return pending(hr)?HRESULT_FROM_WIN32(ERROR_NOT_READY):hr;
  auto read=[&](DWORD id,VARTYPE expected,uint64_t& result)->HRESULT {
   PROPVARIANT value{};auto resultHr=store->GetValue(dm::apo::key(id),&value);
   if(SUCCEEDED(resultHr)){if(value.vt==VT_EMPTY||value.vt==VT_NULL)resultHr=HRESULT_FROM_WIN32(ERROR_NOT_READY);
    else if(value.vt!=expected)resultHr=HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    else result=expected==VT_UI4?uint64_t(value.ulVal):value.uhVal.QuadPart;}
   PropVariantClear(&value);return pending(resultHr)?HRESULT_FROM_WIN32(ERROR_NOT_READY):resultHr;
  };
  uint64_t calls=0,frames=0,running=0,error=0,state=0,requestTicks=0;
  hr=read(dm::apo::Calls,VT_UI8,calls);if(FAILED(hr))return hr;
  hr=read(dm::apo::Frames,VT_UI8,frames);if(FAILED(hr))return hr;
  hr=read(dm::apo::Running,VT_UI4,running);if(FAILED(hr))return hr;if(running>1)return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
  hr=read(dm::apo::NcError,VT_UI8,error);if(FAILED(hr))return hr;
  auto stateHr=read(dm::apo::NcStatus,VT_UI8,state);auto requestHr=read(dm::apo::RequestMeters,VT_UI8,requestTicks);
  // No mutation, Commit, wait, request, or IAudioClient activation. A stale
  // Running=1 is not activation evidence: the caller must observe progression.
  std::wostringstream text;text.imbue(std::locale::classic());text<<L"{\"Calls\":"<<calls<<L",\"Frames\":"<<frames<<L",\"Running\":"<<running<<L",\"Error\":"<<error<<L",\"State\":";
  if(SUCCEEDED(stateHr)&&state<=4)text<<state;else text<<L"null";
  text<<L",\"MeterRequestTicks\":";if(SUCCEEDED(requestHr))text<<requestTicks;else text<<L"null";text<<L"}";
  auto json=text.str();*required=UINT(json.size()+1);if(!buffer||capacity<*required)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
  memcpy(buffer,json.c_str(),*required*sizeof(wchar_t));return S_OK;
 }catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_status(const wchar_t* endpointId,wchar_t* buffer,UINT capacity) noexcept {
 if(!endpointId||!buffer||!capacity)return E_INVALIDARG;try{Apartment a;if(FAILED(a.hr)&&a.hr!=RPC_E_CHANGED_MODE)return a.hr;ComPtr<IMMDeviceEnumerator> enumerator;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator));if(FAILED(hr))return hr;ComPtr<IMMDevice> d;hr=enumerator->GetDevice(endpointId,&d);if(FAILED(hr))return hr;ComPtr<IAudioSystemEffectsPropertyStore> effects;hr=dm::apo::openEffects(d.Get(),&effects);if(FAILED(hr))return hr;ComPtr<IPropertyStore> s;hr=effects->OpenVolatilePropertyStore(STGM_READWRITE,&s);if(FAILED(hr))return hr;PROPVARIANT request{};request.vt=VT_UI8;request.uhVal.QuadPart=GetTickCount64();hr=s->SetValue(dm::apo::key(dm::apo::RequestMeters),request);if(SUCCEEDED(hr))hr=s->Commit();if(FAILED(hr))return hr;Sleep(30);
 HRESULT reads=S_OK;auto read=[&](DWORD id){PROPVARIANT v{};double x=0;auto valueHr=s->GetValue(dm::apo::key(id),&v);VARTYPE expected=id==dm::apo::Running?VT_UI4:id==dm::apo::ConsumedGain?VT_R4:VT_UI8;if(FAILED(valueHr)||v.vt!=expected)reads=FAILED(valueHr)?valueHr:HRESULT_FROM_WIN32(ERROR_NOT_READY);else if(v.vt==VT_UI8)x=double(v.uhVal.QuadPart);else if(v.vt==VT_UI4)x=v.ulVal;else x=v.fltVal;PropVariantClear(&v);return x;};std::wostringstream text;text<<L"{\"Calls\":"<<uint64_t(read(dm::apo::Calls))<<L",\"Frames\":"<<uint64_t(read(dm::apo::Frames))<<L",\"Running\":"<<read(dm::apo::Running)<<L",\"ConsumedGain\":"<<read(dm::apo::ConsumedGain)<<L",\"State\":"<<read(dm::apo::NcStatus)<<L",\"Error\":"<<uint64_t(read(dm::apo::NcError))<<L",\"Runs\":"<<uint64_t(read(dm::apo::NcRuns))<<L",\"AdoptedFrames\":"<<uint64_t(read(dm::apo::NcAdopted))<<L"}";if(FAILED(reads))return reads;auto result=text.str();if(result.size()+1>capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);wcscpy_s(buffer,capacity,result.c_str());return S_OK;}catch(...){return E_FAIL;}}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_community_set(const wchar_t* endpointId,UINT property,float value,UINT persist) noexcept {
 if(!endpointId)return E_INVALIDARG;try{Apartment a;if(FAILED(a.hr)&&a.hr!=RPC_E_CHANGED_MODE)return a.hr;ComPtr<IMMDeviceEnumerator> e;auto hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e));if(FAILED(hr))return hr;ComPtr<IMMDevice> d;hr=e->GetDevice(endpointId,&d);if(FAILED(hr))return hr;ComPtr<IAudioSystemEffectsPropertyStore> effects;hr=dm::apo::openEffects(d.Get(),&effects);return FAILED(hr)?hr:dm::apo::setValue(effects.Get(),property,value,persist!=0);}catch(...){return E_FAIL;}}
#ifdef DOTMIC_INTEGRATION_TOOL
int wmain(int argc,wchar_t** argv){HRESULT hr=E_INVALIDARG;if(argc==2&&wcscmp(argv[1],L"list")==0){std::wstring result;hr=list(result);if(SUCCEEDED(hr)){int size=WideCharToMultiByte(CP_UTF8,0,result.c_str(),int(result.size()),nullptr,0,nullptr,nullptr);std::string bytes(size,0);WideCharToMultiByte(CP_UTF8,0,result.c_str(),int(result.size()),bytes.data(),size,nullptr,nullptr);std::cout<<bytes<<"\n";}}else if(argc==3&&wcscmp(argv[1],L"properties")==0)hr=properties(argv[2],false);std::cerr<<"HRESULT=0x"<<std::hex<<unsigned(hr)<<"\n";return FAILED(hr)?1:0;}
#endif
