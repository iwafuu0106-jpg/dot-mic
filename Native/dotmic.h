#pragma once
#include <stdint.h>
#ifdef DOTMIC_EXPORTS
#define DM_API extern "C" __declspec(dllexport)
#else
#define DM_API extern "C" __declspec(dllimport)
#endif
// Offline fixture ABI only; no capture/render or product UI consumer.
// Former telemetry slots remain reserved for fixture ABI compatibility.
struct DmSettings { uint32_t size, version; float gainDb, thresholdDb, hysteresisDb, attackMs, holdMs, releaseMs; int32_t gate, nc, inputChannel; };
struct DmStatus { uint32_t size, version; float inputPeak, outputPeak, limiterReductionDb, waterSamples; int32_t running, ncState, gateOpen, workerStopped; uint64_t runs, wetBlocks, fallbackBlocks, xruns, faults, safetyClamps; double hopMeanMs, hopP99Ms; };
static_assert(sizeof(DmSettings)==44 && sizeof(DmStatus)==104,"C ABI layout mismatch");
// ncState: 0 Off (ack), 1 Starting, 2 FadingIn, 3 On, 4 FadingOut, 5 FaultBypassed.
DM_API void* dm_create(const wchar_t* modelPath);
DM_API void dm_destroy(void* handle);
DM_API void dm_stop(void* handle);
DM_API int dm_configure(void* handle, const DmSettings* settings);
DM_API int dm_status(void* handle, DmStatus* status);
DM_API int dm_error(void* handle, wchar_t* text, int capacity);
// Developer fixtures have no endpoint transport. Not an APO performance result.
// Not used by the product UI. Fixed 480-float caller-owned buffers, no audio file saving.
DM_API int dm_fixture_begin(void* handle);
DM_API int dm_fixture_step(void* handle, const float* input480, float* output480, uint64_t position, int consume);
