#include "processing.h"
#include <iostream>
#include <stdexcept>
#include <cstdlib>
#include <new>
static thread_local bool inRt=false;
static thread_local size_t rtAllocations=0;
void* operator new(size_t n){if(inRt)++rtAllocations;if(auto p=std::malloc(n?n:1))return p;throw std::bad_alloc();}
void* operator new[](size_t n){return ::operator new(n);}
void operator delete(void* p) noexcept {std::free(p);}
void operator delete[](void* p) noexcept {std::free(p);}
void operator delete(void* p,size_t) noexcept {std::free(p);}
void operator delete[](void* p,size_t) noexcept {std::free(p);}
using namespace dm::apo;
static void require(bool ok,const char* what){if(!ok)throw std::runtime_error(what);}
static std::vector<float> run(const Values& values,const std::vector<float>& input,uint32_t channels=1){
    Shell shell;shell.lock(channels);Snapshot snapshot;snapshot.publish(prepare(values));std::vector<float> output(input.size());
    size_t done=0;const uint32_t packets[]={137,480,71,960};size_t n=0;
    while(done<input.size()/channels){auto frames=uint32_t(std::min<size_t>(packets[n++%4],input.size()/channels-done));inRt=true;shell.process(input.data()+done*channels,output.data()+done*channels,frames,false,snapshot);inRt=false;done+=frames;}
    require(rtAllocations==0,"RT heap allocation");return output;
}
int main(){try{
    constexpr size_t count=16000;
    std::vector<float> input(count*2);for(size_t i=0;i<input.size();++i){uint32_t bits=uint32_t(i*2654435761u);std::memcpy(&input[i],&bits,4);}
    Values bypass;bypass.bypass=1;bypass.gainDb=36;bypass.gate=bypass.nc=1;
    auto output=run(bypass,input,2);
    require(std::memcmp(output.data()+ProcessingDelay::total*2,input.data(),(count-ProcessingDelay::total)*2*sizeof(float))==0,"bypass bit-exact fixed delay");
    input.assign(count,0.05f);Values v;auto unity=run(v,input);require(unity.back()==input.back(),"gain 0dB");
    v.gainDb=6;output=run(v,input);require(std::abs(output.back()-0.05f*std::pow(10.f,6.f/20))<1e-6f,"gain +6dB");
    Shell shell;shell.lock(1);Snapshot snapshot;std::vector<float> ramp(960+ProcessingDelay::total+100);
    shell.process(input.data(),unity.data(),uint32_t(count),false,snapshot);snapshot.publish(prepare(v));
    std::vector<float> flat(ramp.size(),.05f);shell.process(flat.data(),ramp.data(),uint32_t(flat.size()),false,snapshot);
    for(size_t i=1;i<ramp.size();++i)require(std::abs(ramp[i]-ramp[i-1])<0.00006f,"gain smooth ramp");
    std::vector<float> stereo(count*2);for(size_t i=0;i<count;++i){stereo[i*2]=2;stereo[i*2+1]=.5f;}
    output=run(Values{},stereo,2);for(size_t i=ProcessingDelay::total;i<count;++i){require(std::abs(output[i*2])<=Shell::ceiling+1e-6f,"limiter ceiling");require(std::abs(output[i*2]-4*output[i*2+1])<1e-6f,"linked limiter");}
    // Regression: leave bypass exactly as the selector becomes silent, with hot
    // pre-bypass frames still in the limiter ring. They must be linked, not clipped independently.
    Shell transition;transition.lock(2);Snapshot transitionParameters;transitionParameters.publish(prepare(bypass));
    for(size_t i=0;i<count;++i){stereo[i*2]=1;stereo[i*2+1]=.25f;}
    inRt=true;transition.process(stereo.data(),output.data(),uint32_t(count),false,transitionParameters);inRt=false;
    std::vector<float> silence(ProcessingDelay::selector*2,0),drain(silence.size());
    inRt=true;transition.process(silence.data(),drain.data(),ProcessingDelay::selector,false,transitionParameters);inRt=false;
    transitionParameters.publish(prepare(Values{}));
    inRt=true;transition.process(silence.data(),drain.data(),ProcessingDelay::lookahead,false,transitionParameters);inRt=false;
    for(size_t i=0;i<ProcessingDelay::lookahead;++i){require(std::abs(drain[i*2]-Shell::ceiling)<1e-6f,"bypass exit ceiling");require(std::abs(drain[i*2]-4*drain[i*2+1])<1e-6f,"bypass exit linked protection");}
    Values gate;gate.gate=1;input.assign(count,.00001f);output=run(gate,input);require(output.back()==0,"gate close");
    input.assign(count,.1f);output=run(gate,input);require(std::abs(output.back()-.1f)<1e-6f,"gate open");
    require(ProcessingDelay::hns==830000,"latency contract");
    require(rtAllocations==0,"RT allocation including bypass transition");
    std::cout<<"PASS bypass bits/fixed delay, gain 0/+6, 20ms ramp, linked limiter ceiling, gate open/close; delay="<<ProcessingDelay::total<<" samples\n";
    return 0;
}catch(const std::exception& e){std::cerr<<"FAIL "<<e.what()<<'\n';return 1;}}
