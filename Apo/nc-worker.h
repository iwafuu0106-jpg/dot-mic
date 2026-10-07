#pragma once
#include <windows.h>
#include <unknwn.h>
#include <rtworkq.h>
#include <wrl/client.h>
#include <mutex>
#include <string>
#include "async-path.h"
#include "inference-api.h"
namespace dm::apo {
enum class NcState:uint32_t {Off,Starting,On,FaultBypassed,Stopping};
struct NcDiagnostics {
    NcState state=NcState::Off;HRESULT error=S_OK;
    uint64_t runs=0,stft=0,istft=0,stateUpdates=0,resets=0,wetPublished=0,discarded=0,faults=0;
    uint64_t generation=0,epoch=0,jobs=0,adopted=0,fallback=0,highWater=0;
    double meanMs=0,p99Ms=0;
};
// One engine RTQueue, one callback owner and one persistent Model. Audio RT
// only publishes fixed buffers + signals a precreated event. No COM/scheduling
// API, model call, wait, allocation or mutex on the APOProcess side.
class NcWorker final:public IRtwqAsyncCallback {
    std::atomic<ULONG> refs{1};
    DWORD queueId=0;uint32_t channelCount=0;uint64_t rtCommand=0;
    HANDLE wake=nullptr,done=nullptr;
    Microsoft::WRL::ComPtr<IRtwqAsyncResult> result;
    std::mutex callbackMutex; // RTQueue callback only, NEVER audio RT.
    std::atomic<bool> desired{false},ready{false},closing{false},inFlight{false},idle{true};
    std::atomic<bool> schedulerAlive{false};
    std::atomic<uint64_t> command{0};
    std::atomic<uint32_t> state{uint32_t(NcState::Off)},error{uint32_t(S_OK)};
    bool started=false;HMODULE library=nullptr;void* model=nullptr;
    std::wstring directory;
    NcCreate create=nullptr;NcDestroy destroy=nullptr;NcReset reset=nullptr;NcProcess process=nullptr;
    uint64_t modelGeneration=0,nextInput=0,warmStart=0;
    HopBlock job{},output{};
    std::array<float,ProcessingDelay::hop> input{},mono{};
    AsyncPath path;
    std::atomic<uint64_t> runs{0},stft{0},istft{0},updates{0},resets{0},published{0},discarded{0},faults{0};
    std::atomic<uint64_t> rtJobs{0},rtAdopted{0},rtFallback{0},rtHighWater{0};
    static constexpr size_t timingCapacity=12000;
    std::array<std::atomic<uint64_t>,timingCapacity> timings{};
    std::atomic<uint64_t> timed{0},totalTicks{0};LARGE_INTEGER frequency{};
    void(__cdecl* beforeRun)()=nullptr; // Set only by standalone T3 fixture; never CAPX/UI.
    void(*releaseModule)()=nullptr;
    void fail(HRESULT hr) noexcept {error=uint32_t(hr);state=uint32_t(NcState::FaultBypassed);ready=false;path.latchFault();++faults;}
    HRESULT arm() noexcept {
        RTWQWORKITEM_KEY key=0;return RtwqPutWaitingWorkItem(wake,0,result.Get(),&key);
    }
    void disposeModel() noexcept {if(model&&destroy)destroy(model);model=nullptr;ready=false;}
    void acknowledgeOff() noexcept {
        if(idle.load(std::memory_order_acquire)&&!desired.load(std::memory_order_acquire)&&!inFlight.load(std::memory_order_acquire)&&
           !path.isEnabledWorker()&&!path.fadeInProgress())state=uint32_t(NcState::Off);
    }
    void work() {
        const bool wanted=desired.load(std::memory_order_acquire);
        if(!wanted){ready=model!=nullptr;acknowledgeOff();HopBlock old;if(path.popJob(old))++discarded;return;}
        if(path.isFaulted())return;
        if(!model){
            HRESULT hr=create?create((directory+L"\\dpdfnet2_48khz_hr.onnx").c_str(),&model):E_NOINTERFACE;
            if(FAILED(hr)){fail(hr);return;}ready=true;
        }
        if(!path.popJob(job))return;
        if(job.generation!=path.currentGenerationWorker()||job.audioEpoch!=path.audioEpochWorker()||!path.isEnabledWorker()){++discarded;return;}
        if(modelGeneration!=job.generation){
            if(FAILED(reset(model))){fail(E_FAIL);return;}
            modelGeneration=job.generation;warmStart=nextInput=job.position;++resets;
        }
        if(job.position!=nextInput){fail(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));return;}
        // Current live jobs only. A missed deadline is never processed as a
        // backlog to catch up: invalidate this generation and require OFF->ON.
        if(job.position>=warmStart+ProcessingDelay::alignment&&job.position-ProcessingDelay::alignment<path.outputPositionWorker()){
            ++discarded;fail(HRESULT_FROM_WIN32(ERROR_TIMEOUT));return;
        }
        for(uint32_t i=0;i<ProcessingDelay::hop;++i){float x=job.samples[size_t(i)*job.channels];input[i]=std::isfinite(x)?x:0;}
        // The physical mic's channel0 is the existing product default. Its mono
        // NC result is replicated into the engine's mono/stereo transport.
        inFlight=true;LARGE_INTEGER a{},b{};QueryPerformanceCounter(&a);
        if(beforeRun)beforeRun();
        if(!desired.load(std::memory_order_acquire)||path.isFaulted()||job.generation!=path.currentGenerationWorker()){
            inFlight=false;++discarded;return;
        }
        ++stft;++runs;HRESULT hr=process(model,input.data(),mono.data());QueryPerformanceCounter(&b);
        if(SUCCEEDED(hr)){++istft;++updates;auto n=timed.load(std::memory_order_relaxed);timings[n%timingCapacity]=uint64_t(b.QuadPart-a.QuadPart);totalTicks.fetch_add(uint64_t(b.QuadPart-a.QuadPart));timed.store(n+1,std::memory_order_release);}
        nextInput=job.position+ProcessingDelay::hop;
        if(FAILED(hr)){fail(hr);inFlight=false;return;}
        for(float x:mono)if(!std::isfinite(x)){fail(E_FAIL);inFlight=false;return;}
        if(!desired.load(std::memory_order_acquire)||path.isFaulted()||job.generation!=path.currentGenerationWorker()){++discarded;inFlight=false;return;}
        if(job.position>=warmStart+ProcessingDelay::alignment){
            output.position=job.position-ProcessingDelay::alignment;output.generation=job.generation;output.audioEpoch=job.audioEpoch;output.channels=job.channels;
            for(uint32_t i=0;i<ProcessingDelay::hop;++i)for(uint32_t c=0;c<job.channels;++c)output.samples[size_t(i)*job.channels+c]=mono[i];
            if(path.publishWet(output))++published;else {++discarded;fail(HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));}
        }
        inFlight=false;
    }
public:
    explicit NcWorker(std::wstring pathDirectory,void(*moduleRelease)()=nullptr):directory(std::move(pathDirectory)),releaseModule(moduleRelease){QueryPerformanceFrequency(&frequency);}
    ~NcWorker(){if(wake)CloseHandle(wake);if(done)CloseHandle(done);if(library)FreeLibrary(library);if(started)RtwqShutdown();if(releaseModule)releaseModule();}
    HRESULT start(DWORD id,uint32_t channels,uint64_t epoch) {
        queueId=id;channelCount=channels;path.prepare(channels,epoch);
        HRESULT hr=RtwqStartup();if(FAILED(hr)){fail(hr);return hr;}started=true;
        wake=CreateEventW(nullptr,FALSE,FALSE,nullptr);done=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        if(!wake||!done){hr=HRESULT_FROM_WIN32(GetLastError());fail(hr);if(done)SetEvent(done);return hr;}
        library=LoadLibraryExW((directory+L"\\DotMic.Inference.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!library){hr=HRESULT_FROM_WIN32(GetLastError());fail(hr);SetEvent(done);return hr;}
        create=reinterpret_cast<NcCreate>(GetProcAddress(library,"dm_nc_create"));destroy=reinterpret_cast<NcDestroy>(GetProcAddress(library,"dm_nc_destroy"));
        reset=reinterpret_cast<NcReset>(GetProcAddress(library,"dm_nc_reset"));process=reinterpret_cast<NcProcess>(GetProcAddress(library,"dm_nc_process"));
        if(!create||!destroy||!reset||!process){fail(E_NOINTERFACE);SetEvent(done);return E_NOINTERFACE;}
        hr=RtwqCreateAsyncResult(nullptr,this,nullptr,&result);if(SUCCEEDED(hr))hr=arm();
        schedulerAlive=SUCCEEDED(hr);
        if(FAILED(hr)){fail(hr);result.Reset();SetEvent(done);}return hr;
    }
    void request(bool wanted) noexcept { // non-RT notification/configuration
        if(!wanted&&desired.load(std::memory_order_acquire))path.freezeOffPublication();
        auto old=desired.exchange(wanted,std::memory_order_acq_rel);if(old==wanted)return;
        ++command;
        if(wanted){if(!schedulerAlive.load()){fail(E_FAIL);return;}path.clearFaultForExplicitOn();error=uint32_t(S_OK);state=uint32_t(NcState::Starting);}
        else {state=uint32_t(NcState::Stopping);acknowledgeOff();}
        if(wake)SetEvent(wake);
    }
    void diagnosticFault() noexcept {fail(E_FAIL);if(wake)SetEvent(wake);}
    void testBeforeRun(void(__cdecl* hook)()) noexcept {beforeRun=hook;} // before first request in test process only
    void select(uint64_t time,const float* source,const float* dry,float* selected,const Parameters& p) noexcept {
        auto previous=path.counters.submitted;auto epochChanges=path.counters.epochChanges;
        auto requestedCommand=command.load(std::memory_order_acquire);
        if(rtCommand!=requestedCommand){rtCommand=requestedCommand;if(desired.load(std::memory_order_acquire))path.discontinuityRt();}
        const bool nc=p.nc&&!p.bypass&&desired.load(std::memory_order_acquire)&&ready.load(std::memory_order_acquire);
        path.sample(time,source,dry,selected,nc,true);
        // Bypass must be bit-exact regardless of a retiring NC crossfade.
        if(p.bypass)std::memcpy(selected,dry,channelCount*sizeof(float));
        if(path.isFaulted()&&desired.load(std::memory_order_acquire)){ready=false;auto previousState=state.exchange(uint32_t(NcState::FaultBypassed));if(previousState!=uint32_t(NcState::FaultBypassed)){error=uint32_t(HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));++faults;}}
        if(path.wetEffective()){uint32_t expected=uint32_t(NcState::Starting);state.compare_exchange_strong(expected,uint32_t(NcState::On));}
        if(path.counters.submitted!=previous||path.counters.epochChanges!=epochChanges)SetEvent(wake);
        if(!desired.load(std::memory_order_acquire)&&idle.load(std::memory_order_acquire))acknowledgeOff();
    }
    void endPacket() noexcept {
        rtJobs=path.counters.submitted;rtAdopted=path.counters.wetFrames;rtFallback=path.counters.fallbackFrames;rtHighWater=path.counters.highWater;
    }
    void stop() noexcept { // Called only after APOProcess has stopped.
        desired=false;closing=true;if(!result&&done)SetEvent(done);if(wake)SetEvent(wake);if(done)WaitForSingleObject(done,INFINITE);result.Reset();
    }
    NcDiagnostics diagnostics(bool percentiles=false) const {
        NcDiagnostics d;d.state=NcState(state.load());d.error=HRESULT(error.load());d.runs=runs;d.stft=stft;d.istft=istft;d.stateUpdates=updates;
        d.resets=resets;d.wetPublished=published;d.discarded=discarded;d.faults=faults;d.generation=path.currentGenerationWorker();d.epoch=path.audioEpochWorker();
        d.jobs=rtJobs;d.adopted=rtAdopted;d.fallback=rtFallback;d.highWater=rtHighWater;
        auto count=timed.load(std::memory_order_acquire);if(count)d.meanMs=1000.*double(totalTicks.load())/frequency.QuadPart/count;
        if(percentiles&&count){std::vector<uint64_t> values(size_t(std::min<uint64_t>(count,timingCapacity)));for(size_t i=0;i<values.size();++i)values[i]=timings[i].load();
            std::sort(values.begin(),values.end());d.p99Ms=1000.*values[(values.size()-1)*99/100]/frequency.QuadPart;}return d;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;if(iid!=__uuidof(IUnknown)&&iid!=__uuidof(IRtwqAsyncCallback))return E_NOINTERFACE;
        *out=static_cast<IRtwqAsyncCallback*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetParameters(DWORD* flags,DWORD* queue) override {if(!flags||!queue)return E_POINTER;*flags=0;*queue=queueId;return S_OK;}
    HRESULT STDMETHODCALLTYPE Invoke(IRtwqAsyncResult*) override {
        // Explicit serialization also covers a requeued callback starting before
        // this callback returns. Never two Run/state owners, even on an MTA queue.
        std::lock_guard<std::mutex> owner(callbackMutex);idle=false;
        if(closing.load()){schedulerAlive=false;disposeModel();idle=true;SetEvent(done);return S_OK;}
        const auto handled=command.load(std::memory_order_acquire);try{work();}catch(...){fail(E_OUTOFMEMORY);inFlight=false;}idle=true;acknowledgeOff();
        HRESULT hr;
        if(path.hasJobsWorker()){
            // Faulted queues are drained one stale item/callback, with no model work.
            if(path.isFaulted()){HopBlock old;if(path.popJob(old))++discarded;}
            hr=RtwqPutWorkItem(queueId,0,result.Get());
        }else {
            ResetEvent(wake);
            if(closing.load()||path.hasJobsWorker()||command.load(std::memory_order_acquire)!=handled)hr=RtwqPutWorkItem(queueId,0,result.Get());
            else hr=arm();
        }
        if(FAILED(hr)){schedulerAlive=false;fail(hr);disposeModel();SetEvent(done);}return S_OK;
    }
};
}
