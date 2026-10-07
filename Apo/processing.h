#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>
#include "../Native/model_hash.h"

namespace dm::apo {
// One source-time contract, shared by shell, worker, tests and GetLatency.
struct ProcessingDelay {
    static constexpr uint32_t rate=48000, hop=480;
    static constexpr uint32_t alignment=DOTMIC_ALIGNMENT_SAMPLES;
    static constexpr uint32_t assembler=hop, schedulingReserve=2*hop;
    static constexpr uint32_t selector=alignment+assembler+schedulingReserve;
    static constexpr uint32_t lookahead=rate*3/1000;
    static constexpr uint32_t total=selector+lookahead;
    static constexpr int64_t hns=int64_t(total)*10000000/rate;
};
struct Values {
    float gainDb=0, thresholdDb=-48, hysteresisDb=6, attackMs=5, holdMs=160, releaseMs=120;
    uint32_t bypass=0, gate=0, nc=0;
};
struct Parameters {
    float gain=1, openPower=0.000015848932f, closePower=0.000003981072f;
    float attack=1.f/240, release=1.f/5760;
    uint32_t hold=7680, bypass=0, gate=0, nc=0;
};
inline bool validate(const Values& v) noexcept {
    auto range=[](float x,float a,float b){return std::isfinite(x)&&x>=a&&x<=b;};
    return range(v.gainDb,-12,36)&&range(v.thresholdDb,-80,-10)&&range(v.hysteresisDb,2,12)&&
        range(v.attackMs,1,30)&&range(v.holdMs,50,500)&&range(v.releaseMs,30,500)&&v.bypass<=1&&v.gate<=1&&v.nc<=1;
}
inline Parameters prepare(const Values& v) noexcept { // Non-RT only.
    return {std::pow(10.f,v.gainDb/20),std::pow(10.f,v.thresholdDb/10),std::pow(10.f,(v.thresholdDb-v.hysteresisDb)/10),
        1.f/(v.attackMs*48),1.f/(v.releaseMs*48),uint32_t(v.holdMs*48),v.bypass,v.gate,v.nc};
}
// Atomic words avoid C++ data races. Writer is serialized outside RT; readers never spin.
class Snapshot {
    static constexpr size_t words=sizeof(Parameters)/sizeof(uint32_t);
    std::atomic<uint32_t> sequence{0};
    std::array<std::atomic<uint32_t>,words> data{};
public:
    Snapshot(){publish(Parameters{});}
    void publish(const Parameters& p) noexcept {
        std::array<uint32_t,words> bits{};std::memcpy(bits.data(),&p,sizeof(p));
        sequence.fetch_add(1,std::memory_order_seq_cst);
        for(size_t i=0;i<words;++i)data[i].store(bits[i],std::memory_order_seq_cst);
        sequence.fetch_add(1,std::memory_order_seq_cst);
    }
    bool read(Parameters& p) const noexcept {
        for(int attempt=0;attempt<2;++attempt){
            auto a=sequence.load(std::memory_order_seq_cst);if(a&1)continue;
            std::array<uint32_t,words> bits{};
            for(size_t i=0;i<words;++i)bits[i]=data[i].load(std::memory_order_seq_cst);
            if(a==sequence.load(std::memory_order_seq_cst)){std::memcpy(&p,bits.data(),sizeof(p));return true;}
        }return false; // retain last coherent snapshot
    }
};
static_assert(std::atomic<uint32_t>::is_always_lock_free);
static_assert(std::atomic<uint64_t>::is_always_lock_free);
struct Counters {
    uint64_t calls=0, frames=0, nonfinite=0, limited=0, parameterMisses=0;
    float inputPeak=0, outputPeak=0, reduction=1;
    bool gateOpen=false;
};
class Shell {
    uint32_t channels=0; uint64_t cursor=0;
    std::vector<float> dry, rawLookahead, processed;
    std::array<float,ProcessingDelay::lookahead+2> peaks{};
    std::array<uint64_t,ProcessingDelay::lookahead+2> indices{};
    uint64_t head=0,tail=0;
    Parameters parameters{};
    float gain=1,target=1,step=0,gateGain=1,power=0,attenuation=1;
    uint32_t ramp=0,hold=0;
    bool opened=false,wasBypass=false,wasGate=false;
public:
    static constexpr float ceiling=0.891250938f;
    Counters counters{};
    float currentGain() const noexcept {return gain;}
    void lock(uint32_t count){
        channels=count;cursor=head=tail=0;parameters=Parameters{};gain=target=gateGain=attenuation=1;
        power=step=0;ramp=hold=0;opened=wasBypass=wasGate=false;counters={};
        dry.assign(size_t(ProcessingDelay::selector)*channels,0);
        rawLookahead.assign(size_t(ProcessingDelay::lookahead)*channels,0);
        processed.assign(size_t(ProcessingDelay::lookahead)*channels,0);
    }
    // Packet length is arbitrary. Input/output may alias. No allocation, I/O or wait.
    template<class Select> void process(const float* input,float* output,uint32_t frames,bool silent,const Snapshot& snapshot,Select&& select) noexcept {
        if(!snapshot.read(parameters))++counters.parameterMisses;
        if(parameters.gain!=target){target=parameters.gain;ramp=960;step=(target-gain)/960;}
        if(parameters.bypass){gain=target;ramp=0;power=0;opened=false;hold=0;gateGain=1;}
        else if(wasBypass){
            head=tail=0;attenuation=1;
            // Bypass filled the lookahead with unscaled PCM. Protect these old frames
            // with the same linked peak window before the first non-bypass output.
            constexpr auto capacity=ProcessingDelay::lookahead+2;
            uint64_t begin=cursor>=ProcessingDelay::lookahead?cursor-ProcessingDelay::lookahead:0;
            for(uint64_t i=begin;i<cursor;++i){float peak=0;
                for(uint32_t c=0;c<channels;++c){float x=processed[size_t(i%ProcessingDelay::lookahead)*channels+c];if(std::isfinite(x))peak=std::max(peak,std::abs(x));}
                while(head<tail&&peaks[(tail-1)%capacity]<=peak)--tail;
                peaks[tail%capacity]=peak;indices[tail%capacity]=i;++tail;
            }
        }
        if(parameters.gate&&!wasGate&&!parameters.bypass){gateGain=0;opened=false;hold=0;power=0;}
        wasGate=parameters.gate!=0;wasBypass=parameters.bypass!=0;
        float inPeak=0,outPeak=0,minReduction=1;
        ++counters.calls;counters.frames+=frames;
        for(uint32_t frame=0;frame<frames;++frame,++cursor){
            std::array<float,2> source{},delayed{},selected{};
            const size_t d=size_t(cursor%ProcessingDelay::selector)*channels;
            const size_t l=size_t(cursor%ProcessingDelay::lookahead)*channels;
            if(!silent)std::memcpy(source.data(),input+size_t(frame)*channels,channels*sizeof(float));
            std::memcpy(delayed.data(),dry.data()+d,channels*sizeof(float));
            std::memcpy(dry.data()+d,source.data(),channels*sizeof(float));
            // Dry source-time samples retain their exact bits, even while NC is enabled.
            select(cursor,source.data(),delayed.data(),selected.data(),channels,parameters);
            float peak=0,p=0;
            for(uint32_t c=0;c<channels;++c){float x=std::isfinite(selected[c])?selected[c]:0;float detector=std::min(std::abs(x),1.f);p=std::max(p,detector*detector);
                if(std::isfinite(source[c]))inPeak=std::max(inPeak,std::abs(source[c]));}
            if(!parameters.bypass){
                power+=0.004158f*(p-power);
                if(power>=parameters.openPower){opened=true;hold=parameters.hold;}
                else if(opened){if(power>=parameters.closePower)hold=parameters.hold;else if(hold)--hold;else opened=false;}
                float wanted=!parameters.gate||opened?1.f:0.f;
                gateGain=wanted>gateGain?std::min(wanted,gateGain+parameters.attack):std::max(wanted,gateGain-parameters.release);
                if(ramp){gain+=step;if(!--ramp)gain=target;}
            }
            std::array<float,2> x{};
            for(uint32_t c=0;c<channels;++c){
                if(parameters.bypass)std::memcpy(&x[c],&delayed[c],sizeof(float));
                else {float v=selected[c];if(!std::isfinite(v)){v=0;++counters.nonfinite;}x[c]=v*gateGain*gain;
                    if(!std::isfinite(x[c])){x[c]=std::copysign(3.402823466e+38f,v);++counters.nonfinite;}peak=std::max(peak,std::abs(x[c]));}
            }
            if(!parameters.bypass){
                constexpr auto capacity=ProcessingDelay::lookahead+2;
                uint64_t begin=cursor>=ProcessingDelay::lookahead?cursor-ProcessingDelay::lookahead:0;
                while(head<tail&&indices[head%capacity]<begin)++head;
                while(head<tail&&peaks[(tail-1)%capacity]<=peak)--tail;
                peaks[tail%capacity]=peak;indices[tail%capacity]=cursor;++tail;
                float maximum=peaks[head%capacity],wanted=maximum>ceiling?ceiling/maximum:1;
                constexpr float release=0.999652838f; // exp(-1/(48000*.060)), prepared constant
                attenuation=wanted<attenuation?wanted:release*attenuation+(1-release)*wanted;
                minReduction=std::min(minReduction,attenuation);
            }
            for(uint32_t c=0;c<channels;++c){
                float y=0;
                if(parameters.bypass)std::memcpy(&y,&rawLookahead[l+c],sizeof(float));
                else {y=processed[l+c]*attenuation;if(!std::isfinite(y)){y=0;++counters.nonfinite;}
                    if(std::abs(y)>ceiling){y=std::clamp(y,-ceiling,ceiling);++counters.limited;}}
                std::memcpy(rawLookahead.data()+l+c,&delayed[c],sizeof(float));
                std::memcpy(processed.data()+l+c,&x[c],sizeof(float));
                std::memcpy(output+size_t(frame)*channels+c,&y,sizeof(float));
                if(std::isfinite(y))outPeak=std::max(outPeak,std::abs(y));
            }
        }
        counters.inputPeak=inPeak;counters.outputPeak=outPeak;counters.reduction=minReduction;counters.gateOpen=opened;
    }
    void process(const float* input,float* output,uint32_t frames,bool silent,const Snapshot& snapshot) noexcept {
        process(input,output,frames,silent,snapshot,[](uint64_t,const float*,const float* dry,float* selected,uint32_t count,const Parameters&){std::memcpy(selected,dry,count*sizeof(float));});
    }
};
}
