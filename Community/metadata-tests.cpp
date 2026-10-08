#include "endpoint-metadata.h"
#include <iostream>
#include <stdexcept>
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int main(){try {
    using dm::community::backingDeviceInstanceId;
    const std::wstring endpoint=L"SWD\\MMDEVAPI\\{0.0.1.00000000}.{01234567-89AB-CDEF-0123-456789ABCDEF}";
    const std::wstring adapter=L"\\\\?\\USB#VID_1234&PID_5678#interface";
    unsigned reads=0;
    auto usb=backingDeviceInstanceId(adapter,[&](const std::wstring& id){++reads;require(id==adapter&&id!=endpoint,"endpoint devnode used instead of topology adapter");return std::wstring(L"USB\\VID_1234&PID_5678\\serial");});
    require(usb==L"USB\\VID_1234&PID_5678\\serial"&&usb!=endpoint&&reads==1,"physical USB confused with SWD endpoint");
    auto hda=backingDeviceInstanceId(L"hda-adapter",[](const std::wstring&){return std::wstring(L"HDAUDIO\\FUNC_01&VEN_1234\\codec");});
    require(hda==L"HDAUDIO\\FUNC_01&VEN_1234\\codec","HDA backing identity lost");
    auto missing=backingDeviceInstanceId(adapter,[](const std::wstring&){return std::wstring{};});
    require(missing.empty(),"failed adapter query fabricated endpoint/physical evidence");
    auto absent=backingDeviceInstanceId(L"",[&](const std::wstring&){++reads;return endpoint;});
    require(absent.empty()&&reads==1,"absent topology queried endpoint fallback");
    auto oversized=backingDeviceInstanceId(std::wstring(4097,L'x'),[&](const std::wstring&){++reads;return endpoint;});
    require(oversized.empty()&&reads==1,"unbounded adapter identifier queried");
    auto longValue=backingDeviceInstanceId(adapter,[](const std::wstring&){return std::wstring(4097,L'x');});require(longValue.empty(),"unbounded property accepted");
    // A topology adapter may itself be software-backed: report the actual value,
    // do not rewrite it into USB/HDA evidence or apply name/manufacturer policy.
    auto software=backingDeviceInstanceId(L"software-adapter",[](const std::wstring&){return std::wstring(L"ROOT\\VIRTUALAUDIO\\0000");});
    require(software==L"ROOT\\VIRTUALAUDIO\\0000","software adapter fabricated physical identity");
    std::cout<<"PASS topology adapter PnpId vs SWD/MMDEVAPI endpoint identity, USB/HDA, failed/missing resolution, bounds and honest software metadata. Pure fixtures; no Windows/registry/audio calls.\n";return 0;
}catch(const std::exception& error){std::cerr<<"FAIL "<<error.what()<<'\n';return 1;}}
