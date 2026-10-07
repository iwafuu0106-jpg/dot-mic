#pragma once
#include <windows.h>
#include <cstdint>
namespace dm::apo {
enum class ProofStage:uint32_t {NotRun,Library,Export,ModelHash,ModelSession,Fixture,Inference,Reference,Complete};
struct LoadProofReport {
    uint32_t size=sizeof(LoadProofReport),version=1;
    HRESULT result=E_PENDING;
    ProofStage stage=ProofStage::NotRun;
    uint32_t runs=0,stateElements=0,rate=48000,fft=960,hop=480;
    double maxError=0;
    char ortVersion[32]{},error[256]{};
};
using LoadProofFunction=HRESULT(__cdecl*)(const wchar_t*,const wchar_t*,LoadProofReport*);
// Opaque worker-owned model. All calls below are non-audio RTQueue callbacks.
using NcCreate=HRESULT(__cdecl*)(const wchar_t*,void**);
using NcDestroy=void(__cdecl*)(void*);
using NcReset=HRESULT(__cdecl*)(void*);
using NcProcess=HRESULT(__cdecl*)(void*,const float*,float*);
}
