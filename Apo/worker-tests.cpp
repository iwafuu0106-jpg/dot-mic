#include "nc-worker.h"
#include <fstream>
#include <iostream>
#include <stdexcept>
static thread_local bool audioRt=false;
static thread_local size_t audioAllocations=0;
void* operator new(size_t n){if(audioRt)++audioAllocations;if(auto p=std::malloc(n?n:1))return p;throw std::bad_alloc();}
void* operator new[](size_t n){return ::operator new(n);}
void operator delete(void* p) noexcept {std::free(p);}
void operator delete[](void* p) noexcept {std::free(p);}
void operator delete(void* p,size_t) noexcept {std::free(p);}
void operator delete[](void* p,size_t) noexcept {std::free(p);}
using namespace dm::apo;
static std::atomic<bool> stall{false};
static void __cdecl beforeRun(){if(stall.exchange(false))Sleep(100);}
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int wmain(int argc,wchar_t** argv){
    Microsoft::WRL::ComPtr<NcWorker> worker;DWORD queue=0;bool runtime=false;
    try {
        require(argc==3||argc==4,"Usage: worker-check <package-directory> <fixture.f32> [--off-only]");
        const bool offOnly=argc==4&&!wcscmp(argv[3],L"--off-only");
        require(SUCCEEDED(RtwqStartup()),"Test RTWorkQ startup");runtime=true;
        // Test host only. Production NEVER allocates a queue: uses APO service ID.
        require(SUCCEEDED(RtwqAllocateWorkQueue(RTWQ_MULTITHREADED_WORKQUEUE,&queue)),"Test RTWorkQ queue");
        worker.Attach(new NcWorker(argv[1]));require(SUCCEEDED(worker->start(queue,1,77)),"Worker event/RTQueue preparation");worker->testBeforeRun(beforeRun);
        std::ifstream file(argv[2],std::ios::binary|std::ios::ate);require(bool(file),"Existing synth fixture");auto bytes=file.tellg();
        std::vector<float> fixture(size_t(bytes)/sizeof(float));file.seekg(0);file.read(reinterpret_cast<char*>(fixture.data()),bytes);require(bool(file)&&fixture.size()>10000,"Fixture length");
        Shell shell;shell.lock(1);Snapshot snapshot;Values values;values.nc=1;snapshot.publish(prepare(values));
        std::array<float,480> input{},output{};uint64_t time=0;LARGE_INTEGER freq{};QueryPerformanceFrequency(&freq);double maxCallback=0;
        auto feed=[&](uint32_t hops,bool checkDry=false){
            for(uint32_t hop=0;hop<hops;++hop){for(uint32_t i=0;i<480;++i)input[i]=fixture[size_t((time+i)%fixture.size())];
                LARGE_INTEGER a{},b{};QueryPerformanceCounter(&a);audioRt=true;
                shell.process(input.data(),output.data(),480,false,snapshot,[&](uint64_t t,const float* source,const float* dry,float* selected,uint32_t,const Parameters& p) noexcept {worker->select(t,source,dry,selected,p);});
                worker->endPacket();audioRt=false;QueryPerformanceCounter(&b);maxCallback=std::max(maxCallback,1000.*double(b.QuadPart-a.QuadPart)/freq.QuadPart);
                for(uint32_t i=0;i<480;++i){require(std::isfinite(output[i]),"Finite callback output");if(checkDry&&hop>=2)require(output[i]==fixture[size_t((time+i-ProcessingDelay::total)%fixture.size())],"Fault output remains exact same-time dry, no late wet");}
                time+=480;Sleep(10); // Test sender pacing only; NEVER APOProcess/worker polling.
            }
        };
        worker->request(true);feed(100);auto first=worker->diagnostics(true);
        require(first.runs>0&&first.wetPublished>0&&first.adopted>0&&first.state==NcState::On&&first.faults==0,"T1 real model wet adoption");
        std::cout<<"T1 PASS runs="<<first.runs<<" published="<<first.wetPublished<<" adoptedFrames="<<first.adopted<<" generation="<<first.generation<<" epoch="<<first.epoch<<'\n';
        worker->request(false);values.nc=0;snapshot.publish(prepare(values));
        for(uint32_t i=0;i<50&&worker->diagnostics().state!=NcState::Off;++i)feed(1);
        auto off=worker->diagnostics();require(off.state==NcState::Off,"T2 effective OFF ack");feed(120);auto idle=worker->diagnostics();
        require(idle.runs==off.runs&&idle.stft==off.stft&&idle.istft==off.istft&&idle.stateUpdates==off.stateUpdates&&idle.jobs==off.jobs,"T2 1.2s OFF counts frozen");
        std::cout<<"T2 PASS effectiveOff runs/stft/istft/stateUpdates/jobs="<<off.runs<<'/'<<off.stft<<'/'<<off.istft<<'/'<<off.stateUpdates<<'/'<<off.jobs<<" unchanged after1.2s\n";
        if(offOnly){worker->stop();worker.Reset();RtwqUnlockWorkQueue(queue);queue=0;RtwqShutdown();runtime=false;std::cout<<"PASS targeted OFF boundary regression only; T3/T4 evidence reused, no repeated fault tests.\n";return 0;}
        worker->request(true);values.nc=1;snapshot.publish(prepare(values));feed(60);auto again=worker->diagnostics();
        require(again.state==NcState::On&&again.generation!=first.generation&&again.resets>first.resets&&again.adopted>idle.adopted&&again.faults==0,"T4 ON-OFF-ON new generation/reset/wet");
        std::cout<<"T4 PASS new generation="<<again.generation<<" stateResets="<<again.resets<<" adopted="<<again.adopted<<'\n';
        stall=true;feed(2);feed(18,true);auto delayed=worker->diagnostics();
        require(delayed.state==NcState::FaultBypassed&&delayed.runs<=again.runs+3,"T3 stall faults bounded backlog instead of catch-up");
        require(maxCallback<50&&audioAllocations==0,"T3 audio callback never waits/allocates during100ms worker stall");
        std::cout<<"T3 PASS100ms worker-only stall; callbackMaxMs="<<maxCallback<<" audioAllocations="<<audioAllocations<<" processCalls="<<shell.counters.calls<<" dry continued; no stale wet replay\n";
        worker->request(false);values.nc=0;snapshot.publish(prepare(values));feed(4);worker->stop();worker.Reset();RtwqUnlockWorkQueue(queue);queue=0;RtwqShutdown();runtime=false;
        std::cout<<"PASS D2 real CPU DPDFNet persistent Session/RTQueue one-hop scheduling/OFF stop/100ms stall/one ON-OFF-ON. Standalone fixture host, not Discord acceptance.\n";return 0;
    }catch(const std::exception& e){audioRt=false;std::cerr<<"FAIL "<<e.what()<<'\n';if(worker){worker->stop();worker.Reset();}if(queue)RtwqUnlockWorkQueue(queue);if(runtime)RtwqShutdown();return 1;}
}
