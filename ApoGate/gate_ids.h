#pragma once
#include <windows.h>
// Gate-only identities. Do not reuse the SYSVAD CLSID or identify a recording endpoint.
inline constexpr GUID GateClsid{0x8f611fc3,0x1a33,0x477d,{0x98,0x20,0xf8,0xb9,0xa1,0x1d,0x10,0x30}};
inline constexpr GUID GateEffectId{0x10b7a798,0x58ae,0x47e0,{0xbb,0x9e,0xae,0xce,0xce,0x2e,0xd0,0x7a}};
inline constexpr wchar_t TargetEndpoint[] = L"{0.0.1.00000000}.{e4a1bd89-305d-4679-bb29-0c3cf0666f28}";
inline constexpr wchar_t TargetDevnode[] = L"USB\\VID_3142&PID_00C1&MI_00\\9&28ED31B4&0&0000";
// Actual KS interface, not an IMMDevice endpoint ID. Used only for unique physical-target discovery.
inline constexpr wchar_t TargetKsInterface[] = L"{2}.\\\\?\\usb#vid_3142&pid_00c1&mi_00#9&28ed31b4&0&0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\global";
