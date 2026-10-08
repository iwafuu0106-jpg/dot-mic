#pragma once
#include <cstdint>
namespace dm::apo {
enum class ObservationState:uint32_t { Idle,Processing,Unconfirmed,Unavailable,Passthrough };
inline bool observationSnapshotValid(uint64_t first,uint64_t last,uint64_t version,uint64_t publisher,uint64_t running,uint64_t dsp,uint64_t reason) noexcept {
    return first!=0&&first==last&&!(first&1)&&version==2&&publisher!=0&&running<=1&&dsp<=1&&reason<=3;
}
struct ObservationSample {
    uint64_t publisher=0,epoch=0,frames=0,published=0;
    bool readable=false,running=false,dsp=false;
};
struct ObservationBaseline {
    uint64_t publisher=0,epoch=0,frames=0,lastProgress=0;
    bool primed=false,progressed=false;
    ObservationState observe(const ObservationSample& sample,uint64_t now) noexcept {
        if(!sample.readable){*this={};return ObservationState::Unavailable;}
        if(!sample.running){*this={};return ObservationState::Idle;}
        if(!primed||publisher!=sample.publisher||epoch!=sample.epoch||sample.frames<frames){
            publisher=sample.publisher;epoch=sample.epoch;primed=true;progressed=false;
        }else if(sample.frames>frames){lastProgress=now;progressed=true;}
        frames=sample.frames;
        bool fresh=sample.published<=now&&now-sample.published<=1000&&progressed&&now>=lastProgress&&now-lastProgress<=1000;
        return fresh?(sample.dsp?ObservationState::Processing:ObservationState::Passthrough):ObservationState::Unconfirmed;
    }
};
}
