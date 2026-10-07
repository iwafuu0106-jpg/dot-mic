#pragma once
// Temporary, administrator-owned proof switch. Not production settings or IPC.
// Missing/invalid value means bit-exact no-op. Only read outside RT at graph setup.
inline constexpr wchar_t PcmProofRegistryKey[] = L"SOFTWARE\\DOT MIC\\ApoGate\\PCMProof-8F611FC3";
inline constexpr wchar_t PcmProofRegistryValue[] = L"QuarterGain";
