#include <windows.h>
#include <initguid.h>
#include <servprov.h>
#include <audioengineextensionapo.h>
#include <audiomediatype.h>
#include <iostream>
#include <stdexcept>
#include <limits>
#include "endpoint.h"
#include "inference-loader.h"
using namespace dm::apo;
static void check(HRESULT hr,const char* stage){if(FAILED(hr)){std::cerr<<stage<<": 0x"<<std::hex<<uint32_t(hr)<<'\n';throw std::runtime_error(stage);}}
static DWORD property(const wchar_t* name){
    const wchar_t* names[]={L"MasterBypass",L"GainDb",L"GateEnabled",L"GateThresholdDbfs",L"GateAttackMs",L"GateHoldMs",L"GateReleaseMs",L"NcEnabled",L"GateHysteresisDb"};
    for(DWORD i=0;i<9;++i)if(!_wcsicmp(name,names[i]))return i+1;throw std::runtime_error("Unknown property");
}
static void exercise(const wchar_t* path){
    HMODULE dll=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);if(!dll)throw std::runtime_error("Production DLL load");
    auto factory=reinterpret_cast<HRESULT(WINAPI*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));if(!factory)throw std::runtime_error("Factory export");
    {
        ComPtr<IClassFactory> f;check(factory(GateClsid,IID_PPV_ARGS(&f)),"factory");ComPtr<IAudioProcessingObject> apo;check(f->CreateInstance(nullptr,IID_PPV_ARGS(&apo)),"create");
        APOInitSystemEffects3 init{};init.APOInit.cbSize=sizeof(init);init.APOInit.clsid=GateClsid;check(apo->Initialize(sizeof(init),reinterpret_cast<BYTE*>(&init)),"init");
        ComPtr<IAudioProcessingObjectConfiguration> config;ComPtr<IAudioProcessingObjectRT> rt;check(apo.As(&config),"config");check(apo.As(&rt),"rt");
        WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT,2,48000,384000,8,32,0};ComPtr<IAudioMediaType> media;check(CreateAudioMediaType(&wave,sizeof(wave),&media),"format");
        std::array<float,960> input{},output{};input.fill(.1f);
        APO_CONNECTION_DESCRIPTOR a{},b{};a.Type=b.Type=APO_CONNECTION_BUFFER_TYPE_EXTERNAL;a.u32Signature=b.u32Signature=APO_CONNECTION_DESCRIPTOR_SIGNATURE;
        a.pBuffer=reinterpret_cast<UINT_PTR>(input.data());b.pBuffer=reinterpret_cast<UINT_PTR>(output.data());a.u32MaxFrameCount=b.u32MaxFrameCount=480;a.pFormat=b.pFormat=media.Get();
        APO_CONNECTION_DESCRIPTOR* ins[]={&a};APO_CONNECTION_DESCRIPTOR* outs[]={&b};check(config->LockForProcess(1,ins,1,outs),"lock");
        HNSTIME latency=0;check(apo->GetLatency(&latency),"latency");if(latency!=ProcessingDelay::hns)throw std::runtime_error("GetLatency differs from shell");
        APO_CONNECTION_PROPERTY src{},dst{};src.u32Signature=dst.u32Signature=APO_CONNECTION_PROPERTY_SIGNATURE;src.pBuffer=a.pBuffer;dst.pBuffer=b.pBuffer;src.u32ValidFrameCount=480;src.u32BufferFlags=BUFFER_VALID;
        APO_CONNECTION_PROPERTY* sources[]={&src};APO_CONNECTION_PROPERTY* destinations[]={&dst};
        for(int i=0;i<10;++i)rt->APOProcess(1,sources,1,destinations);
        for(float value:output)if(value!=.1f)throw std::runtime_error("production unity delayed output");
        src.u32BufferFlags=BUFFER_SILENT;rt->APOProcess(1,sources,1,destinations);
        if(dst.u32BufferFlags!=BUFFER_VALID||output[0]!=.1f)throw std::runtime_error("silent input lost delayed PCM");
        check(config->UnlockForProcess(),"unlock");
    }FreeLibrary(dll);
    std::cout<<"PASS production DLL shell and GetLatency, delayed silent-input handling. Not an audiodg/Discord result.\n";
}
int wmain(int argc,wchar_t** argv){
    HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(FAILED(hr))return 1;int result=0;
    try{
        if(argc==3&&!wcscmp(argv[1],L"load-proof")){
            auto proof=loadProof(argv[2]);
            std::cout<<"load-proof HRESULT=0x"<<std::hex<<uint32_t(proof.result)<<std::dec<<" stage="<<uint32_t(proof.stage)<<" fixtureRuns="<<proof.runs<<" state="<<proof.stateElements<<" ORT="<<proof.ortVersion<<" maxError="<<proof.maxError<<" error="<<proof.error<<'\n';
            check(proof.result,"Local dependency Session/FFT/fixture proof (NOT audiodg)");
        }else if(argc==3&&!wcscmp(argv[1],L"exercise"))exercise(argv[2]);
        else {
            ComPtr<IMMDevice> endpoint;check(targetEndpoint(L"{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}",&endpoint),"target fifine");
            ComPtr<IAudioSystemEffectsPropertyStore> effects;check(openEffects(endpoint.Get(),&effects),"CAPX context activation");
            if(argc==2&&!wcscmp(argv[1],L"repair-test")){
                Values before;check(readValues(effects.Get(),before),"before repair fixture");
                if(!before.bypass||before.nc)throw std::runtime_error("Repair fixture requires MasterBypass ON and NC OFF");
                ComPtr<IPropertyStore> store;check(effects->OpenUserPropertyStore(STGM_READWRITE,&store),"repair user store");
                PROPVARIANT original{};check(store->GetValue(key(GainDb),&original),"save original gain property");
                try {
                    PROPVARIANT bad{};bad.vt=VT_R4;bad.fltVal=std::numeric_limits<float>::quiet_NaN();
                    check(store->SetValue(key(GainDb),bad),"write malformed fixture");check(store->Commit(),"commit malformed fixture");
                    Values invalid;if(SUCCEEDED(readValues(effects.Get(),invalid)))throw std::runtime_error("Malformed value was accepted");
                    check(setValue(effects.Get(),GainDb,before.gainDb,true),"repair malformed gain");
                    Values after;check(readValues(effects.Get(),after),"read repaired gain");
                    if(after.gainDb!=before.gainDb||after.bypass!=before.bypass)throw std::runtime_error("Repair changed requested state");
                }catch(...){store->SetValue(key(GainDb),original);store->Commit();PropVariantClear(&original);throw;}
                HRESULT restore=store->SetValue(key(GainDb),original);if(SUCCEEDED(restore))restore=store->Commit();PropVariantClear(&original);check(restore,"restore original user value");
                std::cout<<"PASS CAPX malformed GainDb correction; original User property restored, bypass kept ON.\n";
            }else if(argc==2&&!wcscmp(argv[1],L"stores")){
                ComPtr<IPropertyStore> s;const char* names[]={"Default","User","Volatile"};
                for(int layer=0;layer<3;++layer)for(DWORD access:{DWORD(STGM_READ),DWORD(STGM_READWRITE)}){
                    HRESULT open=layer==0?effects->OpenDefaultPropertyStore(access,&s):layer==1?effects->OpenUserPropertyStore(access,&s):effects->OpenVolatilePropertyStore(access,&s);
                    std::cout<<names[layer]<<" access="<<access<<" HRESULT=0x"<<std::hex<<uint32_t(open)<<std::dec<<'\n';
                }
            }else if(argc==2&&(!wcscmp(argv[1],L"nc")||!wcscmp(argv[1],L"performance")||!wcscmp(argv[1],L"nc-fault"))){
                ComPtr<IPropertyStore> store;check(effects->OpenVolatilePropertyStore(STGM_READWRITE,&store),"NC diagnostics store");
                PROPVARIANT request{};request.vt=VT_UI8;request.uhVal.QuadPart=GetTickCount64();
                const auto pid=!wcscmp(argv[1],L"nc-fault")?RequestNcFault:!wcscmp(argv[1],L"performance")?RequestPerformance:DWORD(RequestMeters);
                check(store->SetValue(key(pid),request),"NC diagnostic request");check(store->Commit(),"NC request commit");Sleep(150);
                const char* names[]={"State","Error","Runs","Stft","Istft","StateUpdates","Resets","WetPublished","AdoptedFrames","FallbackFrames","Jobs","Generation","AudioEpoch","HighWater","Faults","Discarded","ProcessTicks","ProcessMaximum","InvalidPackets"};
                std::cout<<'{';for(DWORD i=0;i<std::size(names);++i){PROPVARIANT v{};check(store->GetValue(key(NcStatus+i),&v),"NC metric read");if(i)std::cout<<',';std::cout<<'"'<<names[i]<<"\":";if(v.vt==VT_UI8)std::cout<<v.uhVal.QuadPart;else std::cout<<"null";PropVariantClear(&v);}
                for(DWORD i=0;i<2;++i){PROPVARIANT v{};store->GetValue(key(NcHopMean+i),&v);std::cout<<",\""<<(i?"HopP99Ms":"HopMeanMs")<<"\":";if(v.vt==VT_R8)std::cout<<v.dblVal;else std::cout<<"null";PropVariantClear(&v);}std::cout<<"}\n";
            }else if(argc==2&&!wcscmp(argv[1],L"proof")){
                ComPtr<IPropertyStore> store;check(effects->OpenVolatilePropertyStore(STGM_READ,&store),"proof result store");
                const char* names[]={"Stage","Hresult","FixtureRuns","MaxError"};
                std::cout<<'{';for(DWORD id=ProofStatus;id<=ProofMaxError;++id){PROPVARIANT v{};check(store->GetValue(key(id),&v),"proof result read");if(id!=ProofStatus)std::cout<<',';
                    std::cout<<'"'<<names[id-ProofStatus]<<"\":";if(v.vt==VT_UI4)std::cout<<v.ulVal;else if(v.vt==VT_R8)std::cout<<v.dblVal;else std::cout<<"null";PropVariantClear(&v);}std::cout<<"}\n";
            }else if(argc==2&&!wcscmp(argv[1],L"meters")){
                ComPtr<IPropertyStore> store;check(effects->OpenVolatilePropertyStore(STGM_READWRITE,&store),"meters store");
                PROPVARIANT request{};request.vt=VT_UI8;request.uhVal.QuadPart=GetTickCount64();check(store->SetValue(key(RequestMeters),request),"request meters");check(store->Commit(),"commit request");
                Sleep(150); // Diagnostic caller only, never APOProcess.
                const char* names[]={"Calls","Frames","InputPeak","OutputPeak","LimiterGain","GateOpen","Running","ConsumedGain"};
                std::cout<<'{';for(DWORD id=Calls;id<=ConsumedGain;++id){PROPVARIANT v{};check(store->GetValue(key(id),&v),"meter read");if(id!=Calls)std::cout<<',';
                    std::cout<<'"'<<names[id-Calls]<<"\":";if(v.vt==VT_UI8)std::cout<<v.uhVal.QuadPart;else if(v.vt==VT_UI4)std::cout<<v.ulVal;else if(v.vt==VT_R4)std::cout<<v.fltVal;else std::cout<<"null";PropVariantClear(&v);}std::cout<<"}\n";
            }else if(argc>=4&&!wcscmp(argv[1],L"set")){
                wchar_t* end=nullptr;float value=wcstof(argv[3],&end);if(!end||*end||end==argv[3])throw std::runtime_error("Invalid number");
                check(setValue(effects.Get(),property(argv[2]),value,!(argc==5&&!wcscmp(argv[4],L"volatile"))),"CAPX set/commit");
            }else if(argc!=2||wcscmp(argv[1],L"get"))throw std::runtime_error("Usage: get | meters | proof | set <property> <value> [volatile] | exercise <absolute DLL> | load-proof <absolute package directory>");
            Values v;check(readValues(effects.Get(),v),"CAPX read");
            std::cout<<"{\"MasterBypass\":"<<v.bypass<<",\"GainDb\":"<<v.gainDb<<",\"GateEnabled\":"<<v.gate<<",\"GateThresholdDbfs\":"<<v.thresholdDb
                <<",\"GateAttackMs\":"<<v.attackMs<<",\"GateHoldMs\":"<<v.holdMs<<",\"GateReleaseMs\":"<<v.releaseMs<<",\"NcEnabled\":"<<v.nc<<",\"GateHysteresisDb\":"<<v.hysteresisDb<<"}\n";
        }
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';result=1;}CoUninitialize();return result;
}
