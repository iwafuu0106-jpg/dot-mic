#pragma once
#include "core.h"
#include <onnxruntime_cxx_api.h>
#include <kiss_fftr.h>
#include <vector>
#include <string>
#include <memory>
#include <sstream>
#include <stdexcept>
namespace dm {
class Model {
    Ort::Env env{ORT_LOGGING_LEVEL_WARNING,"DOT MIC"};Ort::SessionOptions options;std::unique_ptr<Ort::Session> session;
    Ort::MemoryInfo memory{Ort::MemoryInfo::CreateCpu(OrtArenaAllocator,OrtMemTypeDefault)};
    std::array<std::string,2> inputNames,outputNames;std::array<const char*,2> inputs{},outputs{};
    std::array<float,960> window{},history{},ola{},fftIn{},ifft{};
    std::array<kiss_fft_cpx,481> spectrum{};
    std::array<float,962> spec{},enhanced{};
    std::vector<float> initial,stateA,stateB;std::array<int64_t,4> specShape{1,1,481,2};std::array<int64_t,1> stateShape{};
    std::array<Ort::Value,2> in{Ort::Value{nullptr},Ort::Value{nullptr}},out{Ort::Value{nullptr},Ort::Value{nullptr}};
    kiss_fftr_cfg forward=nullptr,backward=nullptr;
    int parity=0;
    std::string metadata(const char* name){Ort::AllocatorWithDefaultOptions a;auto v=session->GetModelMetadata().LookupCustomMetadataMapAllocated(name,a);if(!v)throw std::runtime_error(std::string("Missing model metadata: ")+name);return v.get();}
    static void norm(std::vector<float>& target,size_t offset,size_t count,const std::string& text){std::stringstream s(text);std::string token;size_t i=0;while(std::getline(s,token,',')){if(i>=count)throw std::runtime_error("Invalid norm metadata");target.at(offset+i++)=std::stof(token);}if(i!=count)throw std::runtime_error("Norm size mismatch");}
    void tensors(){
        auto& current=parity?stateB:stateA;auto& next=parity?stateA:stateB;
        in[0]=Ort::Value::CreateTensor<float>(memory,spec.data(),spec.size(),specShape.data(),4);
        in[1]=Ort::Value::CreateTensor<float>(memory,current.data(),current.size(),stateShape.data(),1);
        out[0]=Ort::Value::CreateTensor<float>(memory,enhanced.data(),enhanced.size(),specShape.data(),4);
        out[1]=Ort::Value::CreateTensor<float>(memory,next.data(),next.size(),stateShape.data(),1);
    }
public:
    explicit Model(const wchar_t* path){
        options.SetIntraOpNumThreads(1);options.SetInterOpNumThreads(1);options.SetExecutionMode(ORT_SEQUENTIAL);options.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        options.AddConfigEntry("session.intra_op.allow_spinning","0");options.AddConfigEntry("session.inter_op.allow_spinning","0");
        session=std::make_unique<Ort::Session>(env,path,options);
        if(session->GetInputCount()!=2||session->GetOutputCount()!=2)throw std::runtime_error("Model I/O count mismatch");
        Ort::AllocatorWithDefaultOptions a;
        for(int i=0;i<2;i++){
            auto ni=session->GetInputNameAllocated(i,a),no=session->GetOutputNameAllocated(i,a);inputNames[i]=ni.get();outputNames[i]=no.get();inputs[i]=inputNames[i].c_str();outputs[i]=outputNames[i].c_str();
            auto ti=session->GetInputTypeInfo(i);auto to=session->GetOutputTypeInfo(i);
            if(ti.GetTensorTypeAndShapeInfo().GetElementType()!=ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT||to.GetTensorTypeAndShapeInfo().GetElementType()!=ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT)throw std::runtime_error("Model must be FP32");
        }
        stateShape[0]=std::stoll(metadata("state_size"));initial.resize(size_t(stateShape[0]),0);
        auto erb=std::stoul(metadata("erb_norm_state_size")),sp=std::stoul(metadata("spec_norm_state_size"));
        norm(initial,0,erb,metadata("erb_norm_init"));norm(initial,erb,sp,metadata("spec_norm_init"));
        for(int i=0;i<2;i++){
            auto ti=session->GetInputTypeInfo(i);auto to=session->GetOutputTypeInfo(i);auto si=ti.GetTensorTypeAndShapeInfo().GetShape();auto so=to.GetTensorTypeAndShapeInfo().GetShape();
            std::vector<int64_t> expected=i?std::vector<int64_t>{stateShape[0]}:std::vector<int64_t>{1,1,481,2};
            if(si!=expected||so!=expected)throw std::runtime_error("Model shape mismatch");
        }
        stateA=initial;stateB=initial;
        for(int i=0;i<960;i++){double s=std::sin(3.141592653589793*(i+.5)/960);window[i]=float(std::sin(3.141592653589793/2*s*s));}
        forward=kiss_fftr_alloc(960,0,nullptr,nullptr);backward=kiss_fftr_alloc(960,1,nullptr,nullptr);if(!forward||!backward)throw std::bad_alloc();
        tensors();
    }
    ~Model(){free(forward);free(backward);}
    void reset(){history.fill(0);ola.fill(0);stateA=initial;stateB=initial;parity=0;tensors();}
    void frontend(const float* x){std::copy(history.begin()+480,history.end(),history.begin());std::copy(x,x+480,history.begin()+480);for(int i=0;i<960;i++)fftIn[i]=history[i]*window[i];kiss_fftr(forward,fftIn.data(),spectrum.data());for(int i=0;i<481;i++){spec[i*2]=spectrum[i].r;spec[i*2+1]=spectrum[i].i;}}
    void backend(const float* y,float* x){for(int i=0;i<481;i++){spectrum[i].r=y[i*2];spectrum[i].i=y[i*2+1];}kiss_fftri(backward,spectrum.data(),ifft.data());for(int i=0;i<480;i++){ola[i]=ola[i+480]+ifft[i]*window[i]/960.f;ola[i+480]=ifft[i+480]*window[i+480]/960.f;x[i]=ola[i];}}
    void identity(const float* x,float* y){frontend(x);backend(spec.data(),y);}
    void process(const float* x,float* y){
        frontend(x);session->Run(Ort::RunOptions{nullptr},inputs.data(),in.data(),2,outputs.data(),out.data(),2);backend(enhanced.data(),y);
        for(int i=0;i<480;i++)if(!std::isfinite(y[i]))throw std::runtime_error("Nonfinite NC result");
        parity=1-parity;
        // Swap the two preallocated state tensor wrappers; no per-hop OrtValue allocation.
        std::swap(in[1],out[1]);
    }
};
}
