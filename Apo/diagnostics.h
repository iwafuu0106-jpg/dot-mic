#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
namespace dm::apo {
struct MeterRecord {
    bool active=false,dsp=false;uint64_t calls=0,frames=0,dspFrames=0,epoch=0;float input=0,output=0,limiter=1,gain=1;uint32_t gate=0,reason=0;
    std::array<uint64_t,19> nc{};double mean=0,p99=0;
};
inline int ncRank(uint64_t state) noexcept {return state==3?5:state==4?4:state==1?3:state==2?2:0;}
inline void mergeMeter(MeterRecord& result,const MeterRecord& entry) noexcept {
    if(!entry.active)return;result.active=true;result.calls+=entry.calls;result.frames+=entry.frames;
    result.dsp|=entry.dsp;result.reason=std::max(result.reason,entry.reason);
    if(entry.dsp)result.dspFrames+=entry.frames;
    result.epoch=(result.epoch^entry.epoch)*1099511628211ull;
    result.input=std::max(result.input,entry.input);result.output=std::max(result.output,entry.output);result.limiter=std::min(result.limiter,entry.limiter);result.gain=entry.gain;result.gate|=entry.gate;
    // First record wins ties; state and its cause are always selected together.
    if(ncRank(entry.nc[0])>ncRank(result.nc[0])){result.nc[0]=entry.nc[0];result.nc[1]=entry.nc[1];}
    for(size_t i=2;i<entry.nc.size();++i){if(i==11||i==12||i==13||i==17)result.nc[i]=std::max(result.nc[i],entry.nc[i]);else result.nc[i]+=entry.nc[i];}
    result.mean=std::max(result.mean,entry.mean);result.p99=std::max(result.p99,entry.p99);
}
}
