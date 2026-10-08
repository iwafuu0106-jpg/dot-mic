#include <windows.h>
#include <initguid.h>
#include "settings.h"
#include "format.h"
#include "async-path.h"
#include "diagnostics.h"
#include "observation.h"
#include "control-api.h"
#include <baseaudioprocessingobject.h>
#include <audioengineextensionapo.h>
#include "../ApoGate/gate_ids.h"
#include "initialization.h"
#include "../Community/control-target.h"
#include <iostream>
#include <stdexcept>
#include <cstdlib>
#include <new>
static thread_local bool inRt=false;
static size_t allocations=0;
void* operator new(size_t n){if(inRt)++allocations;if(auto p=std::malloc(n?n:1))return p;throw std::bad_alloc();}
void* operator new[](size_t n){return ::operator new(n);}
void operator delete(void* p) noexcept {std::free(p);}
void operator delete[](void* p) noexcept {std::free(p);}
void operator delete(void* p,size_t) noexcept {std::free(p);}
void operator delete[](void* p,size_t) noexcept {std::free(p);}
using namespace dm::apo;
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static float source(uint32_t seed,uint64_t position,uint32_t channel){return float(int((position*(seed+channel+1))%251)-125)/2048;}
struct FakeMedia:IAudioMediaType {
    WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT,2,48000,384000,8,32,0};DWORD mask=3;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void** out) override {if(out)*out=nullptr;return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override {return 1;}ULONG STDMETHODCALLTYPE Release() override {return 1;}
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* compressed) override {if(!compressed)return E_POINTER;*compressed=FALSE;return S_OK;}
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType*,DWORD* flags) override {if(!flags)return E_POINTER;*flags=0;return S_OK;}
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override {return &wave;}
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* format) override {if(!format)return E_POINTER;*format={KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,2,4,32,48000.f,mask};return S_OK;}
};
struct FakeStream {
    Shell shell;Snapshot snapshot;AsyncPath path;Values values;uint32_t seed;uint64_t time=0;
    std::array<float,1920> input{},output{};
    explicit FakeStream(uint32_t identity):seed(identity){values.nc=1;values.gainDb=identity==1?6.f:0.f;reset();}
    void reset(){shell.lock(2);path.prepare(2,seed);snapshot.publish(prepare(values));time=0;}
    void change(uint32_t packet){if(packet==8){values.nc=0;path.freezeOffPublication();}if(packet==13)values.nc=1;if(packet==18)path.latchFault();if(packet==23){values.gate=1;reset();}snapshot.publish(prepare(values));}
    void feed(uint32_t frames){
        for(uint32_t i=0;i<frames;++i)for(uint32_t c=0;c<2;++c)input[i*2+c]=source(seed,time+i,c);
        inRt=true;shell.process(input.data(),output.data(),frames,false,snapshot,[this](uint64_t t,const float* x,const float* dry,float* selected,uint32_t,const Parameters& p) noexcept {path.sample(t,x,dry,selected,p.nc&&!p.bypass);});inRt=false;time+=frames;
        // Deterministic model-alignment stand-in. No native queue, model or audio.
        HopBlock job;for(size_t n=0;n<AsyncPath::capacity&&path.popJob(job);++n){if(job.position<ProcessingDelay::alignment)continue;
            job.position-=ProcessingDelay::alignment;for(uint32_t i=0;i<ProcessingDelay::hop;++i)for(uint32_t c=0;c<2;++c)job.samples[i*2+c]=source(seed,job.position+i,c)*.5f;
            require(path.publishWet(job),"fake wet queue bounded");}
    }
};
static void isolation(){
    FakeStream isolatedA(1),isolatedB(7),interleavedA(1),interleavedB(7);const uint32_t packets[]={1,137,71,480,960};
    for(uint32_t packet=0;packet<80;++packet){auto frames=packets[packet%5];isolatedA.change(packet);interleavedA.change(packet);
        isolatedA.feed(frames);isolatedB.feed(frames);
        if(packet&1){interleavedB.feed(frames);interleavedA.feed(frames);}else{interleavedA.feed(frames);interleavedB.feed(frames);}
        require(std::memcmp(isolatedA.output.data(),interleavedA.output.data(),frames*2*sizeof(float))==0,"A interleaving changes DSP/queue history");
        require(std::memcmp(isolatedB.output.data(),interleavedB.output.data(),frames*2*sizeof(float))==0,"A OFF/reset/fault changes B");
    }
    require(isolatedB.path.currentGenerationWorker()==interleavedB.path.currentGenerationWorker(),"B generation isolation");
}
static void formats(){
    WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT,2,48000,384000,8,32,0};StreamFormat format;
    require(describeWave(&wave,sizeof(wave),format)&&format.dsp48(),"float48 descriptor");
    FakeMedia media;require(describeMedia(&media,format)&&format.mask==3,"plain media conventional channel mask");media.mask=1;require(!describeMedia(&media,format),"inconsistent media mask accepted");
    for(uint32_t rate:{8000u,16000u,44100u,48000u,96000u,192000u})for(WORD channels:{WORD(1),WORD(2),WORD(6),WORD(32)}){
        wave.nChannels=channels;wave.nSamplesPerSec=rate;wave.nBlockAlign=channels*4;wave.nAvgBytesPerSec=rate*wave.nBlockAlign;
        require(describeWave(&wave,sizeof(wave),format),"valid float format rejected");
        std::array<uint32_t,64> sourceBits{},out{};for(size_t i=0;i<sourceBits.size();++i)sourceBits[i]=i&1?0x7fc12345:0x80000000;
        inRt=true;auto ok=transparent(format,sourceBits.data(),out.data(),2,false);inRt=false;
        require(ok&&std::memcmp(sourceBits.data(),out.data(),2*format.frameBytes())==0,"transparent float payload bits");
    }
    WAVEFORMATEXTENSIBLE ext{};ext.Format={WAVE_FORMAT_EXTENSIBLE,2,44100,264600,6,24,22};ext.Samples.wValidBitsPerSample=20;ext.dwChannelMask=SPEAKER_FRONT_LEFT|SPEAKER_FRONT_RIGHT;ext.SubFormat=KSDATAFORMAT_SUBTYPE_PCM;
    require(describeWave(&ext.Format,sizeof(ext),format)&&!format.dsp48(),"PCM extensible safe fallback");
    std::array<unsigned char,24> pcm{},out{};for(size_t i=0;i<pcm.size();++i)pcm[i]=static_cast<unsigned char>(i*17);
    inRt=true;bool copied=transparent(format,pcm.data(),out.data(),4,false);bool aliased=transparent(format,out.data(),out.data(),4,false);inRt=false;
    require(copied&&aliased&&out==pcm,"PCM copy/in-place bypass");
    ext.dwChannelMask=SPEAKER_FRONT_LEFT;require(!describeWave(&ext.Format,sizeof(ext),format),"bad mask accepted");ext.dwChannelMask=3;
    require(!describeWave(&ext.Format,sizeof(WAVEFORMATEX),format),"truncated extension accepted");
    ext.Format.nBlockAlign=8;require(!describeWave(&ext.Format,sizeof(ext),format),"bad block alignment accepted");
    wave={WAVE_FORMAT_PCM,1,8000,8000,1,8,0};require(describeWave(&wave,sizeof(wave),format),"PCM8 descriptor");
    std::array<unsigned char,16> silence{};inRt=true;copied=transparent(format,nullptr,silence.data(),16,true);inRt=false;
    require(copied&&silence.front()==128&&silence.back()==128,"unsigned PCM silence");
    wave.nSamplesPerSec=0;require(!describeWave(&wave,sizeof(wave),format),"zero rate accepted");
}
static void profile(){
    float decoded=0;Values defaults;
    for(DWORD id=1;id<=9;++id){auto value=propertyValue(defaults,id);require(decodeCommonValue(id,REG_BINARY,&value,4,decoded)&&decoded==value,"known profile domain");}
    float value=0;require(!decodeCommonValue(0,REG_BINARY,&value,4,decoded)&&!decodeCommonValue(10,REG_BINARY,&value,4,decoded),"unknown IDs accepted");
    require(!decodeCommonValue(2,REG_SZ,&value,4,decoded)&&!decodeCommonValue(2,REG_BINARY,&value,3,decoded),"untyped/short profile accepted");
    value=std::numeric_limits<float>::quiet_NaN();require(!decodeCommonValue(2,REG_BINARY,&value,4,decoded),"NaN accepted");
    value=37;require(!decodeCommonValue(2,REG_BINARY,&value,4,decoded),"unsafe gain accepted");value=.5f;require(!decodeCommonValue(8,REG_BINARY,&value,4,decoded),"fractional flag accepted");
    auto user=[&](DWORD id,float& result)->HRESULT {result=propertyValue(defaults,id);return S_OK;};
    auto preview=[](DWORD id,float& result)->HRESULT {if(id!=2)return S_FALSE;result=9;return S_OK;};
    Values resolved;require(SUCCEEDED(resolveCommonValues(user,preview,resolved))&&resolved.gainDb==9&&resolved.thresholdDb==-48&&defaults.gainDb==0,"property-only preview overlay mutates authority");
    auto missing=[&](DWORD id,float& result)->HRESULT {return id==9?HRESULT_FROM_WIN32(ERROR_NOT_READY):user(id,result);};
    require(resolveCommonValues(missing,preview,resolved)==HRESULT_FROM_WIN32(ERROR_NOT_READY)&&resolved.gainDb==9,"missing profile returns partial/default authority");
    auto badPreview=[](DWORD id,float&)->HRESULT {return id==2?E_INVALIDARG:S_FALSE;};require(resolveCommonValues(user,badPreview,resolved)==E_INVALIDARG,"invalid preview falls back silently");
    std::wstring path;require(captureFxPath(L"opaque.{01234567-89AB-CDEF-0123-456789ABCDEF}",path),"opaque runtime GUID suffix");
    require(!captureFxPath(L"opaque.{01234567-89AB-CDEF-0123-456789ABCDEF}\\other",path),"registry scope escape accepted");
}
static void diagnostics(){
    MeterRecord a,b,result;a.active=b.active=true;a.nc[2]=3;b.nc[2]=7;mergeMeter(result,a);mergeMeter(result,b);
    require(result.active&&result.nc[2]==10,"independent meters aggregate");a.active=false;result={};mergeMeter(result,a);mergeMeter(result,b);
    require(result.active&&result.nc[2]==7,"unlock A incorrectly stops B");b.active=false;result={};mergeMeter(result,a);mergeMeter(result,b);require(!result.active,"last unlock stays running");
}
static void observationTests(){
    require(observationSnapshotValid(2,2,2,100,1,1,0),"stable completed typed snapshot");
    require(!observationSnapshotValid(1,1,2,100,1,1,0)&&!observationSnapshotValid(2,4,2,100,1,1,0)
        &&!observationSnapshotValid(0,0,2,100,0,0,0)&&!observationSnapshotValid(2,2,1,100,1,1,0)
        &&!observationSnapshotValid(2,2,2,0,1,1,0)&&!observationSnapshotValid(2,2,2,100,2,1,0),"partial/out-of-order/missing/version/domain failures never establish idle or activity");
    MeterRecord dsp,raw,mixed;dsp.active=true;dsp.dsp=true;dsp.epoch=1;dsp.nc[0]=3;dsp.nc[1]=42;
    raw.active=true;raw.frames=480;raw.epoch=2;raw.nc[1]=999;
    mergeMeter(mixed,dsp);mergeMeter(mixed,raw);
    require(mixed.dsp&&mixed.frames==480&&mixed.dspFrames==0,"RAW progression cannot prove locked DSP progression");
    require(mixed.nc[0]==3&&mixed.nc[1]==42,"OFF retained error cannot replace selected fault cause");
    ObservationBaseline mixedBaseline;ObservationSample mixedSample{100,mixed.epoch,mixed.dspFrames,100,true,true,mixed.dsp};
    require(mixedBaseline.observe(mixedSample,100)==ObservationState::Unconfirmed,"first mixed snapshot remains unconfirmed");
    mixedSample.published=200;
    require(mixedBaseline.observe(mixedSample,200)==ObservationState::Unconfirmed,"only transparent stream progressed");
    static_assert(sizeof(ControlObservation)==48&&offsetof(ControlObservation,calls)==32,"versioned managed/native ABI");
    ObservationBaseline baseline;ObservationSample observation{100,1,0,100,true,true,true};
    require(baseline.observe(observation,100)==ObservationState::Unconfirmed,"locked is not packet progression");
    observation.frames=480;observation.published=200;
    require(baseline.observe(observation,200)==ObservationState::Processing,"silent advancing frames establish activity");
    require(baseline.observe(observation,1300)==ObservationState::Unconfirmed,"stale publication/progress must not claim activity");
    observation.publisher=101;observation.published=1400;observation.frames=960;
    require(baseline.observe(observation,1400)==ObservationState::Unconfirmed,"another host cannot inherit prior progress");
    observation.frames=1440;observation.published=1500;observation.dsp=false;
    require(baseline.observe(observation,1500)==ObservationState::Passthrough,"transport activity is not DSP support");
    observation.readable=false;
    require(baseline.observe(observation,1600)==ObservationState::Unavailable,"missing/denied diagnostics are not idle");
    observation.readable=true;observation.running=false;
    require(baseline.observe(observation,1700)==ObservationState::Idle,"readable stopped lifecycle");
    observation.running=true;observation.epoch=2;observation.frames=0;observation.published=1800;
    require(baseline.observe(observation,1800)==ObservationState::Unconfirmed,"relock counter reset is not progression");
    EffectsInitialization parsed;
    APOInitSystemEffects first{};first.APOInit={sizeof(first),GateClsid};
    require(SUCCEEDED(parseEffectsInitialization(sizeof(first),reinterpret_cast<BYTE*>(&first),parsed))&&!parsed.modeObserved&&!parsed.services,"Init1 remains unknown mode and no RT queue");
    APOInitSystemEffects2 second{};second.APOInit={sizeof(second),GateClsid};second.AudioProcessingMode=AUDIO_SIGNALPROCESSINGMODE_RAW;
    require(SUCCEEDED(parseEffectsInitialization(sizeof(second),reinterpret_cast<BYTE*>(&second),parsed))&&parsed.modeObserved&&parsed.mode==AUDIO_SIGNALPROCESSINGMODE_RAW&&!parsed.services,"Init2 mode retained without inventing queue");
    APOInitSystemEffects3 third{};third.APOInit={sizeof(third),GateClsid};third.InitializeForDiscoveryOnly=TRUE;
    require(SUCCEEDED(parseEffectsInitialization(sizeof(third),reinterpret_cast<BYTE*>(&third),parsed))&&parsed.discovery,"Init3 discovery");
    third.APOInit.cbSize=1;
    require(FAILED(parseEffectsInitialization(sizeof(third),reinterpret_cast<BYTE*>(&third),parsed)),"bad cbSize rejected");
    first.APOInit.clsid=GUID_NULL;
    require(FAILED(parseEffectsInitialization(sizeof(first),reinterpret_cast<BYTE*>(&first),parsed))&&FAILED(parseEffectsInitialization(0,nullptr,parsed)),"wrong CLSID and null rejected");
}
int main(){try{observationTests();isolation();formats();profile();diagnostics();require(allocations==0,"RT heap allocation");std::cout<<"PASS versioned observation ABI, fresh frame progression, stale/cross-host/transparent status, Init1/2/3 parsing, two Shell/AsyncPath interleaving, OFF/fault/reset isolation, typed common profile, scope validation, transparent format fallback and diagnostic lifecycle. No registry/audio/model/RTQueue calls. NC remains float48 mono/stereo only.\n";return 0;}catch(const std::exception& e){inRt=false;std::cerr<<"FAIL "<<e.what()<<'\n';return 1;}}
