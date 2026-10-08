namespace DotMic.Common;

internal static class CaptureEligibility
{
    internal static bool IsTarget(uint state, int formFactor, string pnpId, string physicalInterface, string jackSubType, bool hasIdentity)
    {
        if (state != 1 || !hasIdentity) return false;
        // PnpId is the backing adapter's identity, never SWD\MMDEVAPI endpoint identity.
        bool softwareAdapter = pnpId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
            || pnpId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);
        if (softwareAdapter) return false;
        bool hardware = !string.IsNullOrWhiteSpace(pnpId);
        bool connectedAdapter = !string.IsNullOrWhiteSpace(physicalInterface);
        bool microphoneJack = jackSubType.Equals("{DFF21BE1-F70F-11D0-B917-00A0C9223196}", StringComparison.OrdinalIgnoreCase);
        return formFactor is 4 or 5 or 6 ? hardware || connectedAdapter || microphoneJack
            : formFactor is -1 or 8 or 10 && (hardware && connectedAdapter || microphoneJack);
    }
}
