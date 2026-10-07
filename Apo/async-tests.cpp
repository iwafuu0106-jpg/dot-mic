#include "async-path.h"
#include <iostream>
#include <stdexcept>
#include <cstdlib>
#include <new>
static thread_local bool inRt=false;
static thread_local size_t allocations=0;
void* operator new(size_t n){if(inRt)++allocations;if(auto p=std::malloc(n?n:1))return p;throw std::bad_alloc();}
void* operator new[](size_t n){return ::operator new(n);}
void operator delete(void* p) noexcept {std::free(p);}
void operator delete[](void* p) noexcept {std::free(p);}
void operator delete(void* p,size_t) noexcept {std::free(p);}
void operator delete[](void* p,size_t) noexcept {std::free(p);}
using namespace dm::apo;
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static std::array<float,2> source(uint64_t position){return {float(int(position%251)-125)/1024,float(int(position%127)-63)/2048};}
static HopBlock wetBlock(uint64_t position,uint64_t generation){
    HopBlock block;block.position=position;block.generation=generation;block.channels=2;
    for(uint32_t i=0;i<ProcessingDelay::hop;++i){auto x=source(position+i);block.samples[i*2]=x[0]*.5f;block.samples[i*2+1]=x[1]*.5f;}return block;
}
static void alignedPackets(){
    AsyncPath path;path.prepare(2);uint64_t time=0;uint32_t packets[]={1,137,71,480,960};
    for(uint32_t packet=0;time<15000;++packet){
        auto frames=std::min<uint64_t>(packets[packet%5],15000-time);
        for(uint64_t i=0;i<frames;++i,++time){auto x=source(time);auto dry=time>=ProcessingDelay::selector?source(time-ProcessingDelay::selector):std::array<float,2>{};std::array<float,2> y{};
            inRt=true;path.sample(time,x.data(),dry.data(),y.data(),true);inRt=false;
            if(time>=ProcessingDelay::selector)require(y[0]==dry[0]*.5f&&y[1]==dry[1]*.5f,"source-aligned wet across arbitrary packets");
        }
        // Deterministic fake worker represents model's fixed alignment only;
        // no ORT/FFT call and no performance claim in this transport test.
        for(size_t i=0;i<AsyncPath::capacity;++i){HopBlock job;if(!path.popJob(job))break;
            require(job.position%ProcessingDelay::hop==0&&job.channels==2,"assembled hop identity");
            if(job.position>=ProcessingDelay::alignment)require(path.publishWet(wetBlock(job.position-ProcessingDelay::alignment,job.generation)),"wet queue bounded on-time");
        }
    }
    require(path.counters.jobOverflow==0&&path.counters.fallbackFrames==0,"on-time worker no overflow/fallback");
}
static void lateAndOff(){
    AsyncPath path;path.prepare(2);std::array<float,2> output{};
    for(uint64_t t=0;t<=ProcessingDelay::selector;++t){auto x=source(t);auto dry=t>=ProcessingDelay::selector?source(t-ProcessingDelay::selector):std::array<float,2>{};
        inRt=true;path.sample(t,x.data(),dry.data(),output.data(),true);inRt=false;
        HopBlock unused;path.popJob(unused);
    }
    auto generation=path.currentGenerationWorker();require(path.publishWet(wetBlock(0,generation)),"late wet publish");
    for(uint64_t t=ProcessingDelay::selector+1;t<ProcessingDelay::selector+ProcessingDelay::hop;++t){auto x=source(t),dry=source(t-ProcessingDelay::selector);
        inRt=true;path.sample(t,x.data(),dry.data(),output.data(),true);inRt=false;
        require(output==dry,"miss remains same-time dry for entire hop");
    }
    auto t=ProcessingDelay::selector+ProcessingDelay::hop;auto x=source(t),dry=source(ProcessingDelay::hop);
    inRt=true;path.sample(t,x.data(),dry.data(),output.data(),true);inRt=false;
    require(output==dry&&path.counters.staleWet==1,"late block discarded, never conversation catch-up");
    // An old-generation future block must not be consumed on a new epoch.
    require(path.publishWet(wetBlock(2*ProcessingDelay::hop,generation)),"old-generation fixture");
    inRt=true;path.discontinuityRt();inRt=false;
    t+=ProcessingDelay::hop;x=source(t);dry=source(2*ProcessingDelay::hop);
    inRt=true;path.sample(t,x.data(),dry.data(),output.data(),true);inRt=false;
    require(output==dry&&path.counters.staleWet==2,"generation mismatch uses same-time dry");
    uint32_t payload[2]={0x80000000,0x7fc12345};std::memcpy(dry.data(),payload,sizeof(payload));
    auto submitted=path.counters.submitted;
    for(uint64_t i=0;i<ProcessingDelay::hop*2;++i){
        inRt=true;path.sample(t+i+1,x.data(),dry.data(),output.data(),false);inRt=false;
        require(std::memcmp(dry.data(),output.data(),sizeof(payload))==0,"OFF bit-exact dry including signed zero/NaN");
    }
    require(!path.isEnabledWorker()&&submitted==path.counters.submitted,"OFF emits no new jobs");
}
static void boundedOverflowAndPartial(){
    AsyncPath path;path.prepare(2);std::array<float,2> x{},output{};
    for(uint64_t t=137;t<(AsyncPath::capacity+3)*ProcessingDelay::hop;++t){
        inRt=true;path.sample(t,x.data(),x.data(),output.data(),true);inRt=false;
    }
    require(path.counters.submitted==AsyncPath::capacity&&path.counters.jobOverflow==1&&path.isFaulted(),"bounded job overflow latches fault, stops new jobs");
    HopBlock job;size_t count=0;while(path.popJob(job)){if(!count)require(job.position==ProcessingDelay::hop,"partial enable waits for complete source hop");
        require(job.generation!=path.currentGenerationWorker(),"overflow invalidates old queued jobs");++count;}
    require(count==AsyncPath::capacity,"bounded job count");
    for(size_t i=0;i<AsyncPath::capacity;++i)require(path.publishWet(wetBlock(i*ProcessingDelay::hop,path.currentGenerationWorker())),"bounded wet fill");
    require(!path.publishWet(wetBlock(0,0))&&path.wetOverflowCount()==1,"bounded wet overflow fails closed to dry");
}
int main(){try{
    alignedPackets();lateAndOff();boundedOverflowAndPartial();require(allocations==0,"RT transport heap allocation");
    std::cout<<"PASS D1 bounded job/wet SPSC, arbitrary packets, source/generation matching, whole-hop same-time dry, no stale replay, OFF no jobs, RT allocations0. NOT integrated NC/RTQueue.\n";return 0;
}catch(const std::exception& e){std::cerr<<"FAIL "<<e.what()<<'\n';return 1;}}
