#pragma once
#include <propkeydef.h>
// PKEY_AudioEndpoint_StableId compatibility constant for the older build SDK.
// Microsoft.Windows.SDK.CPP 10.0.28000.2705, c/Include/10.0.28000.0/um/mmdeviceapi.h.
// Official NuGet package SHA256: a74ca8f9af98bd61925d9e2932ad98f746306123167b8f0bba99cd9ea9f03807.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-stableid
// Probe reads only. Values are opaque/case-sensitive; never synthesize or write them.
inline constexpr PROPERTYKEY GateStableIdKey{
    {0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}}, 12};
