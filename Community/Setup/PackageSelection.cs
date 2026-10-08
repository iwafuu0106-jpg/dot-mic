namespace DotMic.Setup;

internal static class PackageSelection
{
    internal static string? ReadFallback(bool nearbyPayload, string? installedPackage, Func<string?> configuredPackage)
        => nearbyPayload || installedPackage != null ? null : configuredPackage();
}
