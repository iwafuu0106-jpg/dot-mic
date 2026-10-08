#pragma once
#include <string>
namespace dm::community {
// The endpoint devnode is deliberately NOT an input. Physical evidence comes
// only from resolving the topology-connected adapter and reading its property.
template<class QueryAdapter> inline std::wstring backingDeviceInstanceId(const std::wstring& adapterId,QueryAdapter&& query){
    if(adapterId.empty()||adapterId.size()>4096)return L"";
    auto instance=query(adapterId);return instance.size()<=4096?instance:L"";
}
}
