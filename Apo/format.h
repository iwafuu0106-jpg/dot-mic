#pragma once
#include <windows.h>
#include <audiomediatype.h>
#include <ks.h>
#include <ksmedia.h>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
namespace dm::apo {
struct StreamFormat {
    bool floating=false;uint32_t channels=0,bytes=0,bits=0,rate=0,mask=0;
    uint32_t frameBytes() const noexcept {return channels*bytes;}
    bool dsp48() const noexcept {return floating&&bytes==4&&bits==32&&rate==48000&&channels>=1&&channels<=2;}
    bool operator==(const StreamFormat& other) const noexcept {return floating==other.floating&&channels==other.channels&&bytes==other.bytes&&bits==other.bits&&rate==other.rate&&mask==other.mask;}
};
inline bool describe(const UNCOMPRESSEDAUDIOFORMAT& input,StreamFormat& output) noexcept {
    bool floating=input.guidFormatType==KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    if(!floating&&input.guidFormatType!=KSDATAFORMAT_SUBTYPE_PCM)return false;
    if(!std::isfinite(input.fFramesPerSecond)||input.fFramesPerSecond<8000||input.fFramesPerSecond>192000||std::floor(input.fFramesPerSecond)!=input.fFramesPerSecond)return false;
    if(input.dwSamplesPerFrame<1||input.dwSamplesPerFrame>32||input.dwBytesPerSampleContainer<1||input.dwBytesPerSampleContainer>8)return false;
    auto container=input.dwBytesPerSampleContainer*8;
    if(!input.dwValidBitsPerSample||input.dwValidBitsPerSample>container)return false;
    if(floating){if((container!=32&&container!=64)||input.dwValidBitsPerSample!=container)return false;}
    else if(container>32||(container==8&&input.dwValidBitsPerSample!=8))return false;
    if(input.dwChannelMask){auto mask=input.dwChannelMask;uint32_t count=0;while(mask){count+=mask&1;mask>>=1;}if(count!=input.dwSamplesPerFrame||(input.dwChannelMask&~0x3ffffu))return false;}
    output={floating,input.dwSamplesPerFrame,input.dwBytesPerSampleContainer,input.dwValidBitsPerSample,uint32_t(input.fFramesPerSecond),input.dwChannelMask};return true;
}
inline bool describeWave(const WAVEFORMATEX* wave,size_t available,StreamFormat& output) noexcept {
    if(!wave||available<sizeof(WAVEFORMATEX)||wave->cbSize>available-sizeof(WAVEFORMATEX))return false;
    UNCOMPRESSEDAUDIOFORMAT format{};format.dwSamplesPerFrame=wave->nChannels;format.dwBytesPerSampleContainer=wave->wBitsPerSample/8;format.dwValidBitsPerSample=wave->wBitsPerSample;format.fFramesPerSecond=float(wave->nSamplesPerSec);
    if(wave->wBitsPerSample%8)return false;
    if(wave->wFormatTag==WAVE_FORMAT_IEEE_FLOAT){if(wave->cbSize)return false;format.guidFormatType=KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;}
    else if(wave->wFormatTag==WAVE_FORMAT_PCM){if(wave->cbSize)return false;format.guidFormatType=KSDATAFORMAT_SUBTYPE_PCM;}
    else if(wave->wFormatTag==WAVE_FORMAT_EXTENSIBLE){if(wave->cbSize!=sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX))return false;auto ext=reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wave);format.guidFormatType=ext->SubFormat;format.dwValidBitsPerSample=ext->Samples.wValidBitsPerSample;format.dwChannelMask=ext->dwChannelMask;}
    else return false;
    if(!describe(format,output)||wave->nBlockAlign!=output.frameBytes()||uint64_t(wave->nAvgBytesPerSec)!=uint64_t(output.rate)*output.frameBytes())return false;
    return true;
}
inline bool describeMedia(IAudioMediaType* media,StreamFormat& output) noexcept {
    if(!media)return false;auto wave=media->GetAudioFormat();if(!wave)return false;
    UNCOMPRESSEDAUDIOFORMAT raw{};StreamFormat normalized;
    if(!describeWave(wave,sizeof(WAVEFORMATEX)+wave->cbSize,output)||FAILED(media->GetUncompressedAudioFormat(&raw))||!describe(raw,normalized))return false;
    // Plain WAVEFORMATEX has no channel mask; the media implementation may
    // supply the conventional mask in its uncompressed descriptor.
    if(!output.mask)output.mask=normalized.mask;
    return output==normalized;
}
// Zero-latency transparent fallback. No conversion, DSP, allocation, or NC.
// Uses bytes (not float casts), preserves all valid sample bits and allows aliasing.
inline bool transparent(const StreamFormat& format,const void* input,void* output,uint32_t frames,bool silent) noexcept {
    if(!format.frameBytes()||size_t(frames)>std::numeric_limits<size_t>::max()/format.frameBytes())return false;
    auto bytes=size_t(frames)*format.frameBytes();if(bytes&&!output)return false;
    if(silent){if(bytes)std::memset(output,!format.floating&&format.bytes==1?0x80:0,bytes);}
    else {if(bytes&&!input)return false;if(bytes)std::memmove(output,input,bytes);}return true;
}
}
