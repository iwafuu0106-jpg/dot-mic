#include "inference-api.h"
#include <bcrypt.h>
#include <fstream>
#include <iomanip>
#include "../Native/model.h"
#include "../Native/model_hash.h"
namespace {
struct Hash {
    BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE handle=nullptr;
    std::vector<UCHAR> object;
    ~Hash(){if(handle)BCryptDestroyHash(handle);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
};
void checked(NTSTATUS status){if(status<0)throw std::runtime_error("Model SHA256 API failure");}
void verifyModel(const wchar_t* path){
    std::ifstream file(path,std::ios::binary);if(!file)throw std::runtime_error("Model file unavailable");
    Hash hash;checked(BCryptOpenAlgorithmProvider(&hash.algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0));DWORD length=0,bytes=0;
    checked(BCryptGetProperty(hash.algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&bytes,0));
    hash.object.resize(length);checked(BCryptCreateHash(hash.algorithm,&hash.handle,hash.object.data(),length,nullptr,0,0));
    std::array<char,65536> chunk{};while(file){file.read(chunk.data(),chunk.size());checked(BCryptHashData(hash.handle,reinterpret_cast<PUCHAR>(chunk.data()),ULONG(file.gcount()),0));}
    if(!file.eof())throw std::runtime_error("Model read failure");std::array<UCHAR,32> digest{};
    checked(BCryptFinishHash(hash.handle,digest.data(),DWORD(digest.size()),0));std::ostringstream text;
    for(auto byte:digest)text<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(byte);
    if(text.str()!=DOTMIC_MODEL_SHA256)throw std::runtime_error("Model SHA256 mismatch");
}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_nc_create(const wchar_t* path,void** output) noexcept {
    if(!path||!output)return E_POINTER;*output=nullptr;
    try {verifyModel(path);if(strcmp(OrtGetApiBase()->GetVersionString(),"1.23.2"))return E_FAIL;
        *output=new dm::Model(path);return S_OK;}catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) void __cdecl dm_nc_destroy(void* model) noexcept {delete static_cast<dm::Model*>(model);}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_nc_reset(void* model) noexcept {
    if(!model)return E_POINTER;try{static_cast<dm::Model*>(model)->reset();return S_OK;}catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_nc_process(void* model,const float* input,float* output) noexcept {
    if(!model||!input||!output)return E_POINTER;
    try {static_cast<dm::Model*>(model)->process(input,output);return S_OK;}catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __cdecl dm_apo_load_proof(const wchar_t* modelPath,const wchar_t* fixturePath,dm::apo::LoadProofReport* output) noexcept {
    using namespace dm::apo;
    if(!output||output->size!=sizeof(LoadProofReport)||output->version!=1||!modelPath||!fixturePath)return E_INVALIDARG;
    LoadProofReport report;
    try {
        report.stage=ProofStage::ModelHash;verifyModel(modelPath);
        strncpy_s(report.ortVersion,OrtGetApiBase()->GetVersionString(),_TRUNCATE);
        if(strcmp(report.ortVersion,"1.23.2"))throw std::runtime_error("ORT version mismatch");
        report.stage=ProofStage::ModelSession;
        // Reuses the product's actual metadata, norm/state, FFT960/hop480 and
        // CPU-only 1/1 SEQUENTIAL, spinning-OFF Session/tensor implementation.
        dm::Model model(modelPath);report.stateElements=56436;
        report.stage=ProofStage::Fixture;
        std::array<float,dm::Hop> input{},reference{},actual{};
        std::ifstream fixture(fixturePath,std::ios::binary);if(!fixture)throw std::runtime_error("Fixture unavailable");
        fixture.read(reinterpret_cast<char*>(input.data()),sizeof(input));fixture.read(reinterpret_cast<char*>(reference.data()),sizeof(reference));
        if(!fixture||fixture.peek()!=std::ifstream::traits_type::eof())throw std::runtime_error("Fixture must contain one input/reference hop");
        for(int i=0;i<dm::Hop;++i)if(!std::isfinite(input[i])||!std::isfinite(reference[i]))throw std::runtime_error("Invalid fixture tensor");
        report.stage=ProofStage::Inference;model.process(input.data(),actual.data());report.runs=1;
        report.stage=ProofStage::Reference;
        for(int i=0;i<dm::Hop;++i){if(!std::isfinite(actual[i]))throw std::runtime_error("Nonfinite fixture output");report.maxError=std::max(report.maxError,std::abs(double(actual[i])-reference[i]));}
        if(report.maxError>2e-5)throw std::runtime_error("First-hop streaming reference mismatch");
        report.stage=ProofStage::Complete;report.result=S_OK;
    }catch(const Ort::Exception& e){report.result=E_FAIL;strncpy_s(report.error,e.what(),_TRUNCATE);}
    catch(const std::exception& e){report.result=E_FAIL;strncpy_s(report.error,e.what(),_TRUNCATE);}
    catch(...){report.result=E_FAIL;strcpy_s(report.error,"Unknown load proof failure");}
    *output=report;return report.result;
}
