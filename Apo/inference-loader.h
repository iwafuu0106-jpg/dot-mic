#pragma once
#include "inference-api.h"
#include <cstring>
#include <string>
namespace dm::apo {
// Configuration/preparation thread only. Explicit package-relative DLL search;
// no PATH mutation, dependency fallback, model I/O or inference in APOProcess.
inline LoadProofReport loadProof(const std::wstring& directory) noexcept {
    LoadProofReport report;report.stage=ProofStage::Library;
    HMODULE module=nullptr;
    try {
        module=LoadLibraryExW((directory+L"\\DotMic.Inference.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!module){report.result=HRESULT_FROM_WIN32(GetLastError());strcpy_s(report.error,"LoadLibraryEx DotMic.Inference.dll/dependencies");return report;}
        report.stage=ProofStage::Export;
        auto function=reinterpret_cast<LoadProofFunction>(GetProcAddress(module,"dm_apo_load_proof"));
        if(!function){report.result=HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);strcpy_s(report.error,"Missing load-proof ABI export");}
        else report.result=function((directory+L"\\dpdfnet2_48khz_hr.onnx").c_str(),(directory+L"\\ort-load-fixture.f32").c_str(),&report);
    }catch(...){report.result=E_OUTOFMEMORY;strcpy_s(report.error,"Load-proof preparation exception");}
    if(module)FreeLibrary(module);return report;
}
}
