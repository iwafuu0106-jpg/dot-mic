# Native candidate contract

This is a new candidate, not the accepted 0.4.2 payload.

## Shared profile

`HKLM\SOFTWARE\DOT MIC\Community\CommonSettings\User` is durable authority.
`...\Volatile` is a per-property preview overlay. Setup creates/protects the keys;
native readers/writers never create keys or change security. Values are named
`1` through `9`, each `REG_BINARY`, exactly four little-endian IEEE float bytes:

| ID | Meaning | Initial value |
|---|---|---|
| 1 | MasterBypass | 0 |
| 2 | GainDb | 0 |
| 3 | GateEnabled | 0 |
| 4 | GateThresholdDbfs | -48 |
| 5 | GateAttackMs | 5 |
| 6 | GateHoldMs | 160 |
| 7 | GateReleaseMs | 120 |
| 8 | NcEnabled | 0 |
| 9 | GateHysteresisDb | 6 |

All nine User properties are required. Missing authority returns
`HRESULT_FROM_WIN32(ERROR_NOT_READY)`. Invalid type/length/domain returns
`E_INVALIDARG`; no paths or executable content are interpreted. Unknown registry
values are ignored. Invalid/missing authority makes the APO use safe bypass/NC OFF.

`dm_apo_read` and `dm_apo_read_ex` return common settings even with zero devices. Endpoint diagnostics
never become profile authority. `dm_apo_set` writes only the requested property,
then fans that property out to active endpoints already associated with our MFX.
The first fanout failure is returned without undoing the shared value or successful
replicas; retry is idempotent. No full-profile reset occurs. A successful persistent
write clears only that property's common preview and CAPX replica preview.

New export in `DotMic.Integration.dll`:

```cpp
HRESULT __cdecl dm_community_common_values(wchar_t* buffer, UINT capacity, UINT* required);
```

JSON is an effective validated profile: `{"1":0,"2":0,...,"9":6}`.
`required` includes the terminating wchar; null/short buffer returns
`ERROR_INSUFFICIENT_BUFFER`. This has the same buffer ABI as endpoint enumeration.
Enrollment may read this once and populate the new endpoint's CAPX replicas. This
export never selects a microphone or writes. Existing `dm_community_set` remains
endpoint-replica-only (it does not change common authority).

## Endpoint metadata

Enumeration retains the existing fields and adds `FormFactor` (integer, -1 unknown),
`PnpId`, `JackSubType`, `State`, and nullable boolean `SharedModeBusy`. SDK form
factors: Microphone=4, Headset=5, Handset=6, UnknownFormFactor=10. Session activity is
metadata, not a guarantee an exclusive/shared capture open will succeed. No name
classification, capture or recording is performed by enumeration. Individual
device/property/session failures do not abort siblings; endpoint and session
counts are bounded. FxPath is derived only from a parsed/canonical GUID suffix.

### PnpId provenance (corrected metadata contract)

`PnpId` is `PKEY_Device_InstanceId` from the BACKING ADAPTER's read-only property
store. Resolution is explicitly: endpoint IDeviceTopology connector 0 ->
GetDeviceIdConnectedTo -> `PhysicalInterface` adapter MMDevice ID -> enumerator
GetDevice(adapter ID) -> adapter OpenPropertyStore(STGM_READ) -> InstanceId.
Thus a physical USB/HDA endpoint normally exposes the adapter's `USB\...` or
`HDAUDIO\...` identity, not its `SWD\MMDEVAPI\...` endpoint devnode identity.

`EndpointPnpId` is a separate diagnostic-only string: InstanceId from the ENDPOINT
property store, often `SWD\MMDEVAPI\...`. It is NOT physical-device evidence and
must not replace or be interpreted as PnpId. Existing consumers may ignore it.

If topology, adapter resolution, adapter properties, or InstanceId read fails,
PnpId is empty. There is no endpoint fallback, manufactured hardware prefix, or
friendly-name/manufacturer/device-class filtering. PhysicalInterface, endpoint
FormFactor and JackSubType remain independently reported for fleet policy to
evaluate. A software-backed adapter reports its actual software identity; merely
being a topology adapter is not a guarantee of physical hardware. Identifiers and
property strings are bounded to 4096 wchar characters. Sibling enumeration is not
failed by an adapter-query failure.

`DotMic.MetadataCheck` is a pure injected-adapter-query fixture covering USB/HDA,
the SWD/MMDEVAPI endpoint distinction, failed/absent resolution, software adapters
and bounds. No Windows, registry or audio APIs are called by this fixture.

## Read-only activation observation

```cpp
HRESULT __cdecl dm_community_observe(const wchar_t* endpointId,
    wchar_t* buffer, UINT capacity, UINT* required);
```

The Integration DLL resolves the explicit capture endpoint, opens its effects
Volatile store with `STGM_READ`, and reads already-published typed counters. It
does not call the request-writing `dm_community_status`, write/commit properties,
sleep, restart audio, open capture, or activate an `IAudioClient`.

JSON: `{"Calls":123,"Frames":48000,"Running":1,"Error":0,"State":2,
"MeterRequestTicks":123456}`. Calls/Frames/Error are exact unsigned 64-bit
numbers, Running is 0 or 1. State is optional (JSON null if absent/invalid), as is
MeterRequestTicks, the existing RequestMeters value in GetTickCount64 units.
The request timestamp is NOT a publication timestamp and NOT freshness proof.
No new profile revision or snapshot epoch is introduced by this export.

Missing store or required counters returns `HRESULT_FROM_WIN32(ERROR_NOT_READY)`;
zero counters or Running=0 are valid observations. Missing/idle/frozen data should
remain PendingActivation, not a repair failure. Errors resolving a removed
endpoint or denied access propagate for the coordinator to classify. Invalid
types/running-domain return ERROR_INVALID_DATA, never fabricated healthy data.
Buffer sizing follows the existing required-wchar ABI (terminator included).

Readback is not an atomic snapshot. Healthy requires Running>0 and increasing
Frames between successful observations of the SAME runtime endpoint. Reset the
baseline on endpoint replacement, counter decrease/reset, missing observation or
scope change. Running=1 alone, a saved earlier observation or a request timestamp
is insufficient. If no ordinary UI/request activity produces new meter snapshots,
this read-only export deliberately cannot establish fresh activation; it stays
pending rather than forcing microphone activity. Even progression proves only
the associated APO graph ran, not that NC is enabled or supported for its format.

## Format support and limitations

Production accepts checked Init1/2/3 structures (size, cbSize, CLSID). Init1 keeps
mode unknown and uses transparent passthrough rather than inventing DEFAULT/RAW.
Init2 retains the mode but cannot start NC without an OS-provided RTQueue. There
is no substitute inference pool. Endpoint identity must be resolved before meter
publication; failed/missing identities never merge distinct endpoints.

Validated matching input/output descriptors: 8–192 kHz integer rates, 1–32 channels,
IEEE float32/64 or PCM8/16/24/32 (including extensible valid-bit/channel-mask rules).
Malformed/compressed/mismatched descriptors are rejected. Windows float conversion
is preferred but is not performed or guaranteed by this APO.

Actual gain/gate/limiter/NC is unchanged for float32, 48 kHz, mono/stereo, non-RAW.
NC still uses channel 0 and replicates its mono output to stereo: no new per-channel
NC policy is claimed. DPDFNet core remains 48 kHz/hop480/FFT960 on the RTQueue worker.

Other validated formats and RAW use bounded zero-latency transparent byte copy,
including in-place buffers and unsigned PCM8 silence. They do not run gain, gate,
limiter, SRC, or NC. NC requested on an unsupported non-RAW format reports
FaultBypassed/FORMAT_NOT_SUPPORTED; RAW ignores processing requests and reports OFF.
This is safe microphone passthrough, NOT completed multirate NC.

DSP-capable 48 kHz retains its fixed 83 ms delayed raw-dry/bypass contract.
Fallback/RAW GetLatency is zero. Each stream owns its format descriptor and all DSP
and transport state. Shared diagnostics hold only non-RT meter snapshots: current
active-instance counts are summed, peaks maximized, limiter minimized, state uses
fault/stopping/starting/ON priority. Generation/epoch/high-water/maximum timing and
hop statistics use maxima, not a claimed combined percentile. Scope is within one
APO-host process; cross-process CAPX diagnostic arbitration is not implemented.

## Build/test boundary

### Observation v2

`dm_apo_read_ex` adds a separate 48-byte `ControlObservation` record (version 2;
Calls offset 32), leaving existing `ControlValues`/`ControlStatus` layouts intact.
States: 0 idle, 1 DSP processing, 2 unconfirmed, 3 unavailable, 4 transparent
passthrough. It reports endpoint coverage, diagnostic HRESULT and exact counters.
Common settings remain usable when diagnostic enumeration/reads fail. A failed
request write does not skip a read-only attempt. Missing/wrongly typed values are
not zero evidence. Existing session state is queried without opening capture;
absent/stopped diagnostics plus unknown/active sessions cannot establish idle.

Volatile properties 180–188: version, publisher PID, aggregate stream epoch,
publication GetTickCount64, DSP applicability, passthrough reason (0 none, 1 RAW,
2 format unsupported, 3 mode unknown), request acknowledgment, sequence, and
DSP-associated frames. Odd sequence precedes mutation, even sequence completes it.
Readers collect meters/NC counters within matching nonzero even sequence reads.
This guards same-host publication, not arbitrary cross-host interleavings.

Per-endpoint baselines require the same publisher/epoch and advancing frames;
both publication and progression must be within one second. DSP processing uses
only DSP-associated frame progression: an advancing RAW stream cannot prove a
locked, inactive DSP stream is processing. Silence with advancing frames is
activity. Relock, publisher change, counter reset or read failure resets proof.
Notification loss leaves unconfirmed state; no arbitrary sleep establishes proof.
CAPX bridge reopening is attempted on the non-RT notification/configuration path,
never in APOProcess. NC state and error are selected together, first wins ties.
All of this remains unverified on real OEM notification/legacy-init paths.

CMake accepts `DOTMIC_DEPS_ROOT` for an existing read-only pinned cache. Candidate
outputs must be fresh. `DotMic.ApoMultiMicCheck` uses two Shell/AsyncPath objects,
fake wet publication, pure format/profile/domain tests and meter merging. It does
not open registry keys, endpoints, services, RTQueue, ONNX or microphones.
