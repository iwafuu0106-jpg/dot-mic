#pragma once
#include <cstdint>
namespace dm::apo {
struct ControlValues {
    uint32_t size=sizeof(ControlValues),version=1;
    float gain=0,threshold=-48,hysteresis=6,attack=5,hold=160,release=120;
    uint32_t bypass=1,gate=0,nc=0;
};
struct ControlStatus {
    uint32_t size=sizeof(ControlStatus),version=1;
    float input=0,output=0,limiter=1;
    uint32_t running=0,nc=0,gate=0;
    uint64_t runs=0,adopted=0,fallback=0,faults=0;
};
struct ControlObservation {
    uint32_t size=sizeof(ControlObservation),version=2,state=0,targets=0,observed=0,missing=0;
    int32_t result=0;uint32_t reason=0;
    uint64_t calls=0,frames=0;
};
}
