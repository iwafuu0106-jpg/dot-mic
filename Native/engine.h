#pragma once
#define NOMINMAX
#include <windows.h>
#include <thread>
#include <mutex>
#include <chrono>
#include "model.h"
namespace dm {
inline std::wstring wide(const std::string& s){int n=MultiByteToWideChar(CP_UTF8,0,s.data(),int(s.size()),nullptr,0);std::wstring w(n,0);MultiByteToWideChar(CP_UTF8,0,s.data(),int(s.size()),w.data(),n);return w;}
struct Event {HANDLE h;explicit Event(bool manual=false):h(CreateEventW(nullptr,manual,FALSE,nullptr)){if(!h)throw std::runtime_error("CreateEvent");}~Event(){CloseHandle(h);}void set(){SetEvent(h);}void reset(){ResetEvent(h);} };
// Offline fixture worker only. No endpoint, capture/render client or MMCSS pool.
class Engine {
    std::wstring modelPath;std::unique_ptr<Model> model;
    std::mutex errorLock;std::wstring errorText;
    Event workEvent,workerIdle{true};
    std::thread ncThread;std::atomic<bool> quit{false},audioStopping{true},acceptJobs{false},ncFault{false};
    std::atomic<uint64_t> generation{0},stopRequest{0},stopAck{0};std::atomic<bool> stopped{true};
    Spsc<Block,32> dry;Spsc<Block,4> jobs;Spsc<Block,16> wet;
    std::atomic<float> inPeak{0},outPeak{0},reduction{1};std::atomic<int> ncState{0},gateOpen{0},running{0};
    std::atomic<uint64_t> runs{0},wetBlocks{0},fallbackBlocks{0},xruns{0},faults{0},clamps{0};
    static constexpr size_t TimingCapacity=65536;
    std::array<double,TimingCapacity> timings{};std::atomic<uint64_t> timingCount{0};
    std::mutex statsLock;std::chrono::steady_clock::time_point statsTime{};double cachedMean=0,cachedP99=0;
    void setError(const std::string& text){std::lock_guard<std::mutex> l(errorLock);errorText=wide(text);}
    void fault(const std::string& text){if(!ncFault.exchange(true)){setError(text);++faults;}acceptJobs=false;ncState=5;workEvent.set();}
    void worker();
public:
    struct RenderState { GateGain dsp;Selector selector;Block pending;bool hasPending=false; };
    std::unique_ptr<RenderState> fixtureState;
    Parameters settings;
    std::atomic<int> testDelayMs{0};
    void injectStoppedWetForCheck(const Block& b){if(!stopped)throw std::runtime_error("Injection requires stopped worker");wet.push(b);}
    void resetTimingsStopped(){if(!audioStopping||!stopped)throw std::runtime_error("Timing reset requires stopped engine");timingCount=0;std::lock_guard<std::mutex> l(statsLock);statsTime={};cachedMean=cachedP99=0;}
    explicit Engine(const wchar_t* path):modelPath(path){workerIdle.set();ncThread=std::thread(&Engine::worker,this);}
    ~Engine(){stop();quit=true;workEvent.set();if(ncThread.joinable())ncThread.join();}
    void stop();
    void configure(const DmSettings& s);
    void snapshot(DmStatus& s);
    bool submit(Block block);
    bool take(RenderState& state,float* output);
    // Developer runner uses the exact queues/worker/selector without opening endpoints.
    void beginFixture(){stop();resetTimingsStopped();fixtureState=std::make_unique<RenderState>();dry.resetStopped();jobs.resetStopped();wet.resetStopped();audioStopping=false;ncFault=false;running=1;if(settings.nc){ncState=1;workEvent.set();}}
    std::wstring error(){std::lock_guard<std::mutex> l(errorLock);return errorText;}
};
}
