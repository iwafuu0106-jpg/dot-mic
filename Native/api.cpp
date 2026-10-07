#define DOTMIC_EXPORTS
#include "engine.h"
using namespace dm;
static int copyText(const std::wstring& s,wchar_t* buffer,int capacity){int needed=int(s.size())+1;if(buffer&&capacity>=needed)memcpy(buffer,s.c_str(),needed*sizeof(wchar_t));return needed;}
DM_API void* dm_create(const wchar_t* path){try{if(!path)return nullptr;return new Engine(path);}catch(...){return nullptr;}}
DM_API void dm_destroy(void* h){try{delete static_cast<Engine*>(h);}catch(...){}}
DM_API void dm_stop(void* h){try{if(h)static_cast<Engine*>(h)->stop();}catch(...){}}
DM_API int dm_configure(void* h,const DmSettings* s){
    if(!h||!s||s->size!=sizeof(DmSettings)||s->version!=1)return -1;
    if(!std::isfinite(s->gainDb)||s->gainDb < -12||s->gainDb>36||!std::isfinite(s->thresholdDb)||s->thresholdDb < -80||s->thresholdDb> -10||!std::isfinite(s->hysteresisDb)||s->hysteresisDb<2||s->hysteresisDb>12||!std::isfinite(s->attackMs)||s->attackMs<1||s->attackMs>30||!std::isfinite(s->holdMs)||s->holdMs<50||s->holdMs>500||!std::isfinite(s->releaseMs)||s->releaseMs<30||s->releaseMs>500||s->gate<0||s->gate>1||s->nc<0||s->nc>1||s->inputChannel<0||s->inputChannel>2)return -1;
    try{static_cast<Engine*>(h)->configure(*s);return 0;}catch(...){return -1;}
}
DM_API int dm_status(void* h,DmStatus* s){try{if(!h||!s||s->size!=sizeof(DmStatus)||s->version!=1)return -1;static_cast<Engine*>(h)->snapshot(*s);return 0;}catch(...){return -1;}}
DM_API int dm_error(void* h,wchar_t* text,int capacity){try{return h?copyText(static_cast<Engine*>(h)->error(),text,capacity):-1;}catch(...){return -1;}}
DM_API int dm_fixture_begin(void* h){try{if(!h)return -1;static_cast<Engine*>(h)->beginFixture();return 0;}catch(...){return -1;}}
DM_API int dm_fixture_step(void* h,const float* input,float* output,uint64_t position,int consume){try{
    if(!h||!input||(consume&&!output))return -1;auto* e=static_cast<Engine*>(h);if(!e->fixtureState)return -1;Block b;b.position=position;std::copy_n(input,480,b.samples.data());if(!e->submit(b))return -1;
    return !consume||e->take(*e->fixtureState,output)?0:-1;
}catch(...){return -1;}}
