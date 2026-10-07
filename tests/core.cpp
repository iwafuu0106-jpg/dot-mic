#include "engine.h"
#include "model_hash.h"
#include <fstream>
#include <iostream>
#include <numeric>
#include <psapi.h>
#include <samplerate.h> // Retained fixed-ratio SRC check; no output clock servo.
using namespace dm;
static void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
static std::vector<float> load(const wchar_t* path){std::ifstream f(path,std::ios::binary|std::ios::ate);require(bool(f),"fixture missing");auto size=f.tellg();std::vector<float> data(size_t(size)/4);f.seekg(0);f.read(reinterpret_cast<char*>(data.data()),size);return data;}
static DmSettings config(int nc=0){return {sizeof(DmSettings),1,0,-48,6,5,160,120,0,nc,0};}
static DmStatus status(Engine& e){DmStatus s{};s.size=sizeof(s);s.version=1;e.snapshot(s);return s;}
static void modelTest(const wchar_t* path){
    auto x=load(L"tests/fixture.f32"),reference=load(L"tests/reference.f32");require(x.size()==reference.size(),"fixture length");Model m(path);std::vector<float> actual(x.size());double sum=0,maxError=0;
    for(size_t i=0;i<x.size();i+=Hop){m.process(x.data()+i,actual.data()+i);for(int j=0;j<Hop;j++){double e=actual[i+j]-reference[i+j];maxError=std::max(maxError,std::abs(e));sum+=e*e;}}
    double rms=std::sqrt(sum/x.size());std::cout<<"model max_abs="<<maxError<<" rms="<<rms<<" alignment="<<DOTMIC_ALIGNMENT_SAMPLES<<"\n";
    require(maxError<=2e-5&&rms<=2e-6,"official streaming parity");m.reset();std::array<float,Hop> y{};m.process(x.data(),y.data());for(int i=0;i<Hop;i++)require(y[i]==actual[i],"reset reproducibility");
    m.reset();std::vector<float> identity(x.size());for(size_t i=0;i<x.size();i+=Hop)m.identity(x.data()+i,identity.data()+i);for(size_t i=480;i<x.size();i++)require(std::abs(identity[i]-x[i-480])<2e-6,"identity OLA");
}
static void dspTest(){
    Parameters coldParameters;coldParameters.gate=1;GateGain coldGate;std::array<float,480> quiet{};quiet.fill(.001f);coldGate.process(quiet.data(),480,coldParameters);require(quiet.back()==0&&!coldGate.isOpen(),"saved gate ON starts closed without noise leak");
    Parameters p;GateGain dsp;std::array<float,480> x{};x.fill(.01f);dsp.process(x.data(),480,p);require(std::abs(x.back()-.01)<1e-7,"unity gain");p.gainDb=6;x.fill(.01);dsp.process(x.data(),480,p);float mid=x.back();x.fill(.01);dsp.process(x.data(),480,p);require(mid>.01&&mid<.019953,"gain ramp continuity");require(std::abs(x.back()-.019952623)<1e-6,"+6 dB");
    p.gainDb=0;p.gate=1;p.thresholdDb=-40;p.hysteresisDb=6;
    for(int i=0;i<30;i++){x.fill(.02);dsp.process(x.data(),480,p);}require(dsp.isOpen(),"gate opens");
    for(int i=0;i<5;i++){x.fill(.007);dsp.process(x.data(),480,p);}require(dsp.isOpen(),"gate hysteresis");
    for(int i=0;i<8;i++){x.fill(0);dsp.process(x.data(),480,p);}require(dsp.isOpen(),"gate hold");
    for(int i=0;i<30;i++){x.fill(0);dsp.process(x.data(),480,p);}require(!dsp.isOpen(),"gate closes");p.gate=0;
    for(int i=0;i<4;i++){x.fill(.001);dsp.process(x.data(),480,p);}require(std::abs(x.back()-.001)<1e-7,"gate OFF unity");
    Limiter limiter;float peak=0;for(int i=0;i<20000;i++){float v=.01f*std::sin(i*.1f);if(i==6000)v=10;if(i==8000)v=std::numeric_limits<float>::infinity();if(i==8001)v=std::numeric_limits<float>::quiet_NaN();float y=limiter.process(v);require(std::isfinite(y),"finite limiter");peak=std::max(peak,std::abs(y));}require(peak<=Limiter::ceiling+1e-7,"final ceiling");require(limiter.clamps==0,"lookahead prevents safety clipping");
    int error=0;auto src=src_new(SRC_SINC_FASTEST,1,&error);require(src!=nullptr,"Fixed SRC init");std::array<float,480> input{};std::array<float,512> output{};long consumed=0,generated=0;for(int i=0;i<10;i++){SRC_DATA d{};d.data_in=input.data();d.input_frames=480;d.data_out=output.data();d.output_frames=512;d.src_ratio=44100./48000;require(src_process(src,&d)==0,"Fixed SRC process");consumed+=d.input_frames_used;generated+=d.output_frames_gen;}src_delete(src);require(consumed==4800&&generated>4300&&generated<4410,"Fixed SRC consumed/generated counts");
    std::cout<<"DSP gain/gate/finite/final limiter/fixed SRC OK\n";
}
static void queueTest(const wchar_t* path){
    Packetizer packet;Spsc<Block,4> ring;std::vector<float> input(480*23+73);std::iota(input.begin(),input.end(),0.f);size_t pos=0,outPos=0;const int packets[]={37,701,143,480,17};int c=0;
    while(pos<input.size()){int n=int(std::min<size_t>(packets[c++%5],input.size()-pos));packet.feed(input.data()+pos,n,[&](const Block& b){require(ring.push(b),"ring push");});pos+=n;Block b;while(ring.pop(b)){require(b.position==outPos,"sample position");for(float x:b.samples)require(x==float(outPos++),"packet order/wrap");}}
    require(packet.remainder()==73,"packet remainder");Block block;for(int i=0;i<4;i++)require(ring.push(block),"bounded queue capacity");require(!ring.push(block),"queue refuses overflow");
    Selector selector;Block d,w;d.samples.fill(.02);w.samples.fill(.01);std::array<float,480> selected{};selector.process(d,&w,true,false,selected.data());float a=selector.mix();selector.process(d,&w,false,false,selected.data());require(selector.mix()<a,"fade reverses current");selector.process(d,&w,true,false,selected.data());selector.process(d,nullptr,true,true,selected.data());require(selector.mix()==0&&std::isfinite(selected.back()),"missing wet immediate original");
    Engine e(path);auto cfg=config();e.configure(cfg);e.beginFixture();require(status(e).runs==0,"cold OFF no warmup");Engine::RenderState state;uint64_t position=0;
    auto step=[&](bool take){Block b;b.position=position;position+=Hop;b.samples.fill(.01);require(e.submit(b),"fixture enqueue");if(take)require(e.take(state,selected.data()),"nonblocking dequeue");};
    cfg.nc=1;e.configure(cfg);cfg.nc=0;e.configure(cfg);step(true);Sleep(40);require(status(e).workerStopped,"OFF during preparation ack");
    cfg.nc=1;e.configure(cfg);for(int i=0;i<200&&status(e).workerStopped;i++)Sleep(5);Sleep(30);
    for(int i=0;i<8;i++){step(false);Sleep(10);}for(int i=0;i<35;i++){step(true);Sleep(10);}auto on=status(e);require(on.ncState==3&&on.wetBlocks>0&&on.runs>0,"real NC reaches On");
    cfg.nc=0;e.configure(cfg);for(int i=0;i<4;i++){step(true);Sleep(10);}auto off=status(e);require(off.ncState==0&&off.workerStopped,"OFF after fade acknowledges stopped worker");auto runs=off.runs;for(int i=0;i<6;i++){step(true);Sleep(10);}require(status(e).runs==runs,"OFF Run count invariant");
    Block obsolete;obsolete.position=position-8*Hop;obsolete.generation=0;obsolete.samples.fill(100);e.injectStoppedWetForCheck(obsolete);step(true);require(std::abs(selected.back())<.1,"old generation rejected");
    cfg.nc=1;e.configure(cfg);Sleep(30);for(int i=0;i<15;i++){step(true);Sleep(10);}e.testDelayMs=100;for(int i=0;i<12;i++){auto start=std::chrono::steady_clock::now();step(true);require(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count()<10,"render must not wait for delayed worker");Sleep(10);}require(status(e).ncState==5,"100ms worker lateness/queue loss faults to original");
    cfg.nc=0;e.configure(cfg);e.stop();cfg.nc=1;e.configure(cfg);e.beginFixture();Sleep(30);for(int i=0;i<16;i++)step(true);Sleep(30);require(status(e).faults>0&&status(e).ncState==5,"job overflow common fault");e.stop();
    std::cout<<"handoff/NC real worker, OFF invariant, lateness/overflow/generation OK\n";
}
int wmain(int argc,wchar_t** argv){try{const wchar_t* model=argc>2?argv[2]:L"models/dpdfnet2_48khz_hr.onnx";std::wstring group=argc>1?argv[1]:L"all";
    require(group==L"all"||group==L"model"||group==L"dsp"||group==L"handoff","Unknown offline test group (nothing executed)");
    if(group==L"all"||group==L"model")modelTest(model);
    if(group==L"all"||group==L"dsp")dspTest();
    if(group==L"all"||group==L"handoff")queueTest(model);
    std::cout<<"PASS "<<std::string(group.begin(),group.end())<<"\n";return 0;
}catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<"\n";return 1;}}
