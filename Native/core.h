#pragma once
#include <array>
#include <atomic>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include "dotmic.h"
namespace dm {
constexpr int Hop=480, Rate=48000;
struct Block { uint64_t position=0, generation=0; std::array<float,Hop> samples{}; };
class Packetizer {
    Block block;int count=0;uint64_t position=0;
public:
    template<class Consumer> void feed(const float* samples,int n,Consumer&& consumer){for(int i=0;i<n;i++){block.samples[count++]=finiteSample(samples[i]);if(count==Hop){block.position=position;consumer(block);position+=Hop;count=0;}}}
    int remainder()const{return count;}
private:
    static float finiteSample(float x){return std::isfinite(x)?x:0.f;}
};
template<class T, size_t N> class Spsc {
    std::array<T,N> items{}; alignas(64) std::atomic<uint64_t> write{0}; alignas(64) std::atomic<uint64_t> read{0};
public:
    bool push(const T& value) { auto w=write.load(std::memory_order_relaxed); if(w-read.load(std::memory_order_acquire)>=N)return false; items[w%N]=value; write.store(w+1,std::memory_order_release); return true; }
    bool pop(T& value) { auto r=read.load(std::memory_order_relaxed); if(r==write.load(std::memory_order_acquire))return false; value=items[r%N]; read.store(r+1,std::memory_order_release); return true; }
    size_t size() const { return size_t(write.load(std::memory_order_acquire)-read.load(std::memory_order_acquire)); }
    void resetStopped() { read.store(0);write.store(0); }
};
inline float finite(float x) { return std::isfinite(x)?x:0.f; }
struct Parameters {
    std::atomic<float> gainDb{0}, thresholdDb{-48}, hysteresisDb{6}, attackMs{5}, holdMs{160}, releaseMs{120};
    std::atomic<int> gate{0}, nc{0}, channel{0};
};
class GateGain {
    float gain=1,target=1,step=0,gateGain=1,power=0,openPower=0,closePower=0,threshold=999,hysteresis=999;
    int ramp=0,hold=0; bool opened=false,configured=false;
public:
    bool isOpen()const{return opened;}
    void process(float* data,int n,const Parameters& p) {
        const float t=std::pow(10.f,p.gainDb.load()/20.f);
        if(t!=target){target=t;ramp=960;step=(target-gain)/960.f;}
        float th=p.thresholdDb.load(),hy=p.hysteresisDb.load();
        if(th!=threshold||hy!=hysteresis){threshold=th;hysteresis=hy;openPower=std::pow(10.f,th/10.f);closePower=std::pow(10.f,(th-hy)/10.f);}
        bool enabled=p.gate.load()!=0;
        if(!configured){gateGain=enabled?0.f:1.f;configured=true;}
        int holdSamples=int(p.holdMs.load()*48);float attack=1.f/std::max(1.f,p.attackMs.load()*48),release=1.f/std::max(1.f,p.releaseMs.load()*48);
        constexpr float ema=0.004158f;
        for(int i=0;i<n;i++){
            float x=finite(data[i]);power+=ema*(x*x-power);
            if(power>=openPower){opened=true;hold=holdSamples;}else if(opened){if(power>=closePower)hold=holdSamples;else if(hold>0)--hold;else opened=false;}
            float desired=(!enabled||opened)?1.f:0.f;
            gateGain=desired>gateGain?std::min(desired,gateGain+attack):std::max(desired,gateGain-release);
            if(ramp>0){gain+=step;if(--ramp==0)gain=target;}
            data[i]=x*gateGain*gain;
        }
    }
};
class Limiter {
    std::array<float,512> delay{},peaks{};std::array<uint64_t,512> indices{};
    uint64_t cursor=0,head=0,tail=0;int lookahead;float attenuation=1,release;
public:
    static constexpr float ceiling=0.891250938f;
    uint64_t clamps=0;float reduction=0;
    explicit Limiter(int rate=Rate):lookahead(int(rate*.003)),release(std::exp(-1.f/(rate*.060f))){}
    float process(float raw){
        float x=finite(raw);float peak=std::abs(x);uint64_t begin=cursor>=uint64_t(lookahead)?cursor-lookahead:0;
        while(head<tail&&indices[head%512]<begin)++head;
        while(head<tail&&peaks[(tail-1)%512]<=peak)--tail;
        indices[tail%512]=cursor;peaks[tail%512]=peak;++tail;
        delay[cursor%512]=x;float maxPeak=peaks[head%512];float desired=maxPeak>ceiling?ceiling/maxPeak:1.f;
        attenuation=desired<attenuation?desired:release*attenuation+(1-release)*desired;
        float y=cursor>=uint64_t(lookahead)?delay[(cursor-lookahead)%512]*attenuation:0.f;++cursor;
        if(!std::isfinite(y)){y=0;++clamps;}if(std::abs(y)>ceiling){y=std::clamp(y,-ceiling,ceiling);++clamps;}
        reduction=attenuation;return y;
    }
};
// Offline fixture selector; production uses Apo/async-path.h.
class Selector {
    float alpha=0,last=0,offset=0;int declick=0;
public:
    float mix()const{return alpha;}
    void process(const Block& dry,const Block* wet,bool desired,bool fault, float* out){
        if(!wet&&alpha>0){offset=last-dry.samples[0];declick=48;alpha=0;}
        float target=desired&&wet&&!fault?1.f:0.f;
        for(int i=0;i<Hop;i++){
            alpha+=std::clamp(target-alpha,-1.f/960,1.f/960);
            float y=(1-alpha)*dry.samples[i]+alpha*(wet?wet->samples[i]:0);
            if(declick>0){y+=offset*declick/48.f;--declick;}out[i]=last=finite(y);
        }
    }
};
}
