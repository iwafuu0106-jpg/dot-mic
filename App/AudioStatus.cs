namespace DotMic;

// Managed display state, not the removed capture/render engine's C ABI.
internal struct AudioStatus
{
    internal float inputPeak, outputPeak, limiterReductionDb;
    internal int running, ncState, gateOpen;
    internal ulong runs, wetBlocks, fallbackBlocks, faults;
}
