#pragma once
#include "processing.h"
namespace dm::apo {
// Transport only. Shell owns the continuously maintained bit-exact dry ring;
// this path cannot change its delay, block RT, run ORT or choose a late sample.
struct HopBlock {
    uint64_t position=0,generation=0,audioEpoch=0,publication=0;
    uint32_t channels=0;
    std::array<float,ProcessingDelay::hop*2> samples{};
};
template<class T,size_t Capacity> class FixedSpsc {
    std::array<T,Capacity> items{};
    alignas(64) std::atomic<uint64_t> write{0};
    alignas(64) std::atomic<uint64_t> read{0};
public:
    bool push(const T& value) noexcept {
        auto w=write.load(std::memory_order_relaxed);
        if(w-read.load(std::memory_order_acquire)>=Capacity)return false;
        items[w%Capacity]=value;write.store(w+1,std::memory_order_release);return true;
    }
    bool pop(T& value) noexcept {
        auto r=read.load(std::memory_order_relaxed);
        if(r==write.load(std::memory_order_acquire))return false;
        value=items[r%Capacity];read.store(r+1,std::memory_order_release);return true;
    }
    bool emptyConsumer() const noexcept {return read.load(std::memory_order_relaxed)==write.load(std::memory_order_acquire);}
    uint64_t published() const noexcept {return write.load(std::memory_order_acquire);}
    bool popBefore(T& value,uint64_t limit) noexcept {if(read.load(std::memory_order_relaxed)>=limit)return false;return pop(value);}
    size_t sizeProducer() const noexcept {return size_t(write.load(std::memory_order_relaxed)-read.load(std::memory_order_acquire));}
    void resetStopped() noexcept {write.store(0);read.store(0);}
};
struct PathCounters {
    uint64_t submitted=0,jobOverflow=0,wetFrames=0,fallbackFrames=0,staleWet=0,epochChanges=0,highWater=0;
};
class AsyncPath {
public:
    static constexpr size_t capacity=4;
private:
    FixedSpsc<HopBlock,capacity> jobs,wet;
    HopBlock assembling{},pending{},selected{};
    uint32_t assembled=0,channels=0;
    bool active=false,hasPending=false,hasSelected=false;
    uint64_t rtGeneration=1,audioEpoch=0,retiringGeneration=0;
    std::array<HopBlock,capacity+2> retiring{};
    size_t retiredCount=0;
    float mix=0;
    float offInitialMix=0;uint32_t mixSamples=0,offRemaining=0;
    bool fadingOff=false;
    std::atomic<uint64_t> generation{1},outputPosition{0},wetOverflow{0};
    std::atomic<bool> enabled{false};
    std::atomic<bool> faulted{false},fadeBusy{false},effectiveWet{false};
    std::atomic<uint64_t> offPublicationLimit{0};
    void changeEpoch() noexcept {
        ++rtGeneration;generation.store(rtGeneration,std::memory_order_release);
        assembled=0;hasSelected=false;++counters.epochChanges;
    }
    void choose(uint64_t position) noexcept {
        hasSelected=false;outputPosition.store(position,std::memory_order_release);
        // At most one pending plus capacity queue entries. Never unbounded drain.
        // Future blocks are retained; late blocks are thrown away, not replayed.
        for(size_t attempt=0;attempt<=capacity;++attempt){
            if(!hasPending){if(!wet.pop(pending))return;hasPending=true;}
            if(pending.audioEpoch!=audioEpoch||pending.generation!=rtGeneration||pending.position<position||pending.channels!=channels){hasPending=false;++counters.staleWet;continue;}
            if(pending.position>position)return;
            selected=pending;hasPending=false;hasSelected=true;return;
        }
    }
public:
    PathCounters counters{}; // RT writer; read only after stopped or via atomic mirror.
    void prepare(uint32_t count,uint64_t epoch=0) noexcept { // Both owners stopped.
        channels=count;audioEpoch=epoch;assembled=0;active=hasPending=hasSelected=false;rtGeneration=1;
        mix=offInitialMix=0;mixSamples=offRemaining=0;fadingOff=false;retiredCount=0;faulted=false;fadeBusy=false;effectiveWet=false;
        jobs.resetStopped();wet.resetStopped();generation=1;outputPosition=0;wetOverflow=0;enabled=false;counters={};
    }
    void discontinuityRt() noexcept {changeEpoch();}
    bool popJob(HopBlock& block) noexcept {return jobs.pop(block);} // worker consumer
    bool hasJobsWorker() const noexcept {return !jobs.emptyConsumer();}
    bool publishWet(const HopBlock& block) noexcept { // worker producer
        auto result=block;result.publication=wet.published()+1;
        if(!wet.push(result)){wetOverflow.fetch_add(1,std::memory_order_relaxed);return false;}return true;
    }
    bool isEnabledWorker() const noexcept {return enabled.load(std::memory_order_acquire);}
    uint64_t currentGenerationWorker() const noexcept {return generation.load(std::memory_order_acquire);}
    uint64_t outputPositionWorker() const noexcept {return outputPosition.load(std::memory_order_acquire);}
    uint64_t wetOverflowCount() const noexcept {return wetOverflow.load(std::memory_order_relaxed);}
    uint64_t audioEpochWorker() const noexcept {return audioEpoch;}
    void latchFault() noexcept {faulted.store(true,std::memory_order_release);}
    void clearFaultForExplicitOn() noexcept {faulted.store(false,std::memory_order_release);}
    bool isFaulted() const noexcept {return faulted.load(std::memory_order_acquire);}
    bool fadeInProgress() const noexcept {return fadeBusy.load(std::memory_order_acquire);}
    bool wetEffective() const noexcept {return effectiveWet.load(std::memory_order_acquire);}
    void freezeOffPublication() noexcept {offPublicationLimit.store(wet.published(),std::memory_order_release);}
    // NC means the effective RT request (NcEnabled && !MasterBypass && ready).
    // Caller keeps packet time continuous; overflow/discontinuity starts a new
    // generation. D2 additionally gates the worker for non-RT OFF acknowledgment.
    void sample(uint64_t time,const float* source,const float* dry,float* output,bool nc,bool crossfade=false) noexcept {
        nc=nc&&!isFaulted();
        if(nc!=active){
            // OFF may only fade samples already admitted before its cutover.
            // Freeze the publication boundary; no in-flight/new results enter it.
            if(!nc&&crossfade&&mix>0&&!isFaulted()){
                retiredCount=0;retiringGeneration=rtGeneration;
                const auto limit=offPublicationLimit.load(std::memory_order_acquire);
                if(hasSelected&&selected.publication<=limit)retiring[retiredCount++]=selected;
                if(hasPending&&pending.publication<=limit)retiring[retiredCount++]=pending;
                HopBlock b;
                for(size_t i=0;i<capacity&&wet.popBefore(b,limit);++i)retiring[retiredCount++]=b;
                hasPending=false;fadingOff=true;fadeBusy=true;offInitialMix=mix;offRemaining=960;
            }else {mix=0;mixSamples=0;fadingOff=false;fadeBusy=false;}
            active=nc;changeEpoch();enabled.store(active,std::memory_order_release);effectiveWet=false;
        }
        if(active){
            if(time%ProcessingDelay::hop==0){assembled=0;assembling.position=time;assembling.generation=rtGeneration;assembling.audioEpoch=audioEpoch;assembling.channels=channels;}
            if(assembled||time%ProcessingDelay::hop==0){
                std::memcpy(assembling.samples.data()+size_t(assembled)*channels,source,channels*sizeof(float));
                if(++assembled==ProcessingDelay::hop){
                    if(jobs.push(assembling)){++counters.submitted;counters.highWater=std::max<uint64_t>(counters.highWater,jobs.sizeProducer());}
                    else {++counters.jobOverflow;latchFault();changeEpoch();enabled=false;active=false;hasSelected=false;mix=0;effectiveWet=false;}
                    assembled=0;
                }
            }
        }
        std::memcpy(output,dry,channels*sizeof(float));
        if(time<ProcessingDelay::selector)return;
        const auto sourcePosition=time-ProcessingDelay::selector;
        const auto offset=uint32_t(sourcePosition%ProcessingDelay::hop);
        if(!active){
            if(fadingOff){if(offRemaining)--offRemaining;mix=offInitialMix*float(offRemaining)/960;
                for(size_t i=0;i<retiredCount;++i){const auto& b=retiring[i];
                    if(b.audioEpoch==audioEpoch&&b.generation==retiringGeneration&&b.channels==channels&&sourcePosition>=b.position&&sourcePosition<b.position+ProcessingDelay::hop){
                        for(uint32_t c=0;c<channels;++c)output[c]=dry[c]+mix*(b.samples[size_t(sourcePosition-b.position)*channels+c]-dry[c]);break;
                    }
                }
                if(!offRemaining){fadingOff=false;fadeBusy=false;retiredCount=0;mixSamples=0;}
            }return;
        }
        if(!offset)choose(sourcePosition);
        if(hasSelected&&selected.generation==rtGeneration&&sourcePosition==selected.position+offset){
            if(crossfade){mixSamples=std::min(960u,mixSamples+1);mix=float(mixSamples)/960;for(uint32_t c=0;c<channels;++c)output[c]=dry[c]+mix*(selected.samples[size_t(offset)*channels+c]-dry[c]);effectiveWet=mixSamples==960;}
            else std::memcpy(output,selected.samples.data()+size_t(offset)*channels,channels*sizeof(float));++counters.wetFrames;
        }else {++counters.fallbackFrames;mix=0;mixSamples=0;effectiveWet=false;}
        // A miss stays dry for this whole hop, even if wet arrives mid-hop.
    }
};
}
