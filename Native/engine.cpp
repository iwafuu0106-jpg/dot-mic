#include "engine.h"
#include <bcrypt.h>
#include <fstream>
#include <iomanip>
#include "model_hash.h"
namespace dm {
// Non-real-time first load verification. OFF does not load or hash the model.
static void verifyModel(const std::wstring& path){
    std::ifstream f(path,std::ios::binary);if(!f)throw std::runtime_error("Model missing");
    BCRYPT_ALG_HANDLE alg=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;DWORD bytes=0,length=0;
    if(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)throw std::runtime_error("SHA256 init");
    BCryptGetProperty(alg,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&bytes,0);std::vector<UCHAR> obj(length);std::array<UCHAR,32> digest{};
    BCryptCreateHash(alg,&hash,obj.data(),length,nullptr,0,0);std::array<char,65536> chunk{};
    while(f){f.read(chunk.data(),chunk.size());BCryptHashData(hash,reinterpret_cast<PUCHAR>(chunk.data()),ULONG(f.gcount()),0);}BCryptFinishHash(hash,digest.data(),32,0);BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(alg,0);
    std::ostringstream hex;for(auto b:digest)hex<<std::hex<<std::setw(2)<<std::setfill('0')<<int(b);if(hex.str()!=DOTMIC_MODEL_SHA256)throw std::runtime_error("Model SHA256 mismatch");
}
void Engine::configure(const DmSettings& s){
    settings.gainDb=s.gainDb;settings.thresholdDb=s.thresholdDb;settings.hysteresisDb=s.hysteresisDb;settings.attackMs=s.attackMs;settings.holdMs=s.holdMs;settings.releaseMs=s.releaseMs;settings.gate=s.gate;settings.channel=s.inputChannel;
    int previous=settings.nc.exchange(s.nc);if(previous!=s.nc){if(!s.nc){ncFault=false;if(audioStopping){acceptJobs=false;workEvent.set();}else ncState=4;}else if(!ncFault){ncState=1;workEvent.set();}}
}
void Engine::stop(){
    audioStopping=true;running=0;acceptJobs=false;workEvent.set();
    // Run completion acknowledgement before any queue reset/destruction.
    workerIdle.reset();auto ticket=++stopRequest;workEvent.set();while(stopAck.load()<ticket)WaitForSingleObject(workerIdle.h,INFINITE);ncState=ncFault?5:0;
    inPeak=0;outPeak=0;reduction=1;gateOpen=0;
}
void Engine::worker(){
    uint64_t active=0;
    while(!quit){
        if(audioStopping||(!settings.nc&&!acceptJobs)||ncFault){
            acceptJobs=false;Block discard;while(jobs.pop(discard)){}stopped=true;stopAck=stopRequest.load();workerIdle.set();if(!settings.nc&&!ncFault)ncState=0;
            WaitForSingleObject(workEvent.h,INFINITE);continue;
        }
        if(!acceptJobs){
            stopped=false;workerIdle.reset();
            try{if(!model){verifyModel(modelPath);model=std::make_unique<Model>(modelPath.c_str());}model->reset();active=++generation;
                if(audioStopping||!settings.nc||ncFault)continue;acceptJobs=true;ncState=1;
            }catch(const std::exception& e){fault(e.what());continue;}
        }
        Block b;if(!jobs.pop(b)){WaitForSingleObject(workEvent.h,INFINITE);continue;}
        if(b.generation!=active)continue;
        try{
            auto t=std::chrono::steady_clock::now();int delay=testDelayMs.exchange(0);if(delay>0)Sleep(DWORD(delay));Block result;result.generation=active;
            // Fixed model source offset measured by the reference fixture (see profile).
            result.position=b.position>=DOTMIC_ALIGNMENT_SAMPLES?b.position-DOTMIC_ALIGNMENT_SAMPLES:UINT64_MAX;
            model->process(b.samples.data(),result.samples.data());++runs;
            double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t).count();auto n=timingCount.load(std::memory_order_relaxed);
            if(n<TimingCapacity){timings[n]=ms;timingCount.store(n+1,std::memory_order_release);}
            if(!audioStopping&&acceptJobs&&!ncFault&&!wet.push(result))fault("NC wet queue full; original audio");
        }catch(const std::exception& e){fault(e.what());}
    }
    stopped=true;workerIdle.set();
}
bool Engine::submit(Block block){
    block.generation=generation.load();if(!dry.push(block))return false;
    if(acceptJobs){if(!jobs.push(block))fault("NC jobs queue full; original audio");else workEvent.set();}return true;
}
bool Engine::take(RenderState& state,float* output){
    Block d,w;bool matching=false;if(!dry.pop(d))return false;uint64_t gen=generation.load();
    if(state.hasPending){if(state.pending.position==d.position&&state.pending.generation==gen){w=state.pending;matching=true;state.hasPending=false;}else if(state.pending.position<d.position||state.pending.generation!=gen)state.hasPending=false;}
    for(int i=0;!matching&&!state.hasPending&&i<16;i++){Block b;if(!wet.pop(b))break;if(b.generation!=gen||b.position==UINT64_MAX||b.position<d.position)continue;if(b.position==d.position){w=b;matching=true;}else{state.pending=b;state.hasPending=true;}}
    int current=ncState.load();bool wants=settings.nc&&!ncFault;
    if(wants&&acceptJobs&&!matching&&(current==2||current==3)){fault("NC result missed deadline; original audio");wants=false;}
    state.selector.process(d,matching?&w:nullptr,wants,ncFault,output);
    if(matching&&state.selector.mix()>0)++wetBlocks;else if(settings.nc)++fallbackBlocks;
    if(!ncFault){if(!settings.nc){if(state.selector.mix()==0){acceptJobs=false;workEvent.set();ncState=stopped?0:4;}}else if(state.selector.mix()>=.9999f)ncState=3;else if(state.selector.mix()>0)ncState=2;}
    state.dsp.process(output,Hop,settings);gateOpen=state.dsp.isOpen();return true;
}
void Engine::snapshot(DmStatus& s){
    s.size=sizeof(s);s.version=1;s.inputPeak=inPeak;s.outputPeak=outPeak;float r=reduction;s.limiterReductionDb=r>0?-20.f*std::log10(r):120.f;s.waterSamples=0; // Reserved fixture ABI slot; no clock servo.
    s.running=running;s.ncState=ncState;s.gateOpen=gateOpen;s.workerStopped=stopped;s.runs=runs;s.wetBlocks=wetBlocks;s.fallbackBlocks=fallbackBlocks;s.xruns=xruns;s.faults=faults;s.safetyClamps=clamps;
    std::lock_guard<std::mutex> lock(statsLock);auto now=std::chrono::steady_clock::now();
    if(now-statsTime>=std::chrono::seconds(1)){statsTime=now;auto n=std::min<uint64_t>(timingCount.load(std::memory_order_acquire),TimingCapacity);if(n){std::vector<double> copy(timings.begin(),timings.begin()+size_t(n));double sum=0;for(double v:copy)sum+=v;cachedMean=sum/n;size_t p=std::min<size_t>(size_t(n)-1,size_t(n*.99));std::nth_element(copy.begin(),copy.begin()+p,copy.end());cachedP99=copy[p];}}
    s.hopMeanMs=cachedMean;s.hopP99Ms=cachedP99;
}
}
