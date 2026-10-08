namespace DotMic.Setup;

// Independent inspections: unrelated app/device errors must not disable receipt recovery.
internal sealed class SetupStartup
{
    internal sealed record Checks(Action ValidatePayload, Action ValidateRecoveryHelper, Func<string?> ReadApplicationRoot,
        Func<string, bool> ReadShortcut, Func<List<string>> ReadPendingRemovals, Func<EndpointIdentity[]> ReadEndpoints, Func<EndpointIdentity?> ReadConfiguredIdentity);

    internal bool PayloadReady { get; private set; }
    internal bool RecoveryReady { get; private set; }
    internal string? PayloadError { get; private set; }
    internal string? RecoveryError { get; private set; }
    internal string? ApplicationError { get; private set; }
    internal string? DeviceError { get; private set; }
    internal string? PendingRemovalError { get; private set; }
    internal string? ApplicationRoot { get; private set; }
    internal bool? DesktopShortcut { get; private set; }
    internal List<string> PendingRemovals { get; private set; } = [];
    internal EndpointIdentity[] Endpoints { get; private set; } = [];
    internal EndpointIdentity? SelectedEndpoint { get; private set; }
    internal bool MustResolveRemoval => PendingRemovalError != null || PendingRemovals.Count > 0;

    internal static SetupStartup Inspect(Checks checks, Func<long>? clock = null, Action<int>? sleep = null)
    {
        var result = new SetupStartup();
        try {
            result.ApplicationRoot = checks.ReadApplicationRoot();
            if (result.ApplicationRoot != null) result.DesktopShortcut = checks.ReadShortcut(result.ApplicationRoot);
        } catch (Exception error) { result.ApplicationError = error.Message; }
        try { result.PendingRemovals = checks.ReadPendingRemovals(); }
        catch (Exception error) { result.PendingRemovalError = error.Message; }
        try { checks.ValidatePayload(); result.PayloadReady = true; }
        catch (Exception error) { result.PayloadError = error.Message; }
        if (result.PayloadReady) result.RecoveryReady = true;
        else {
            try { checks.ValidateRecoveryHelper(); result.RecoveryReady = true; }
            catch (Exception error) { result.RecoveryError = error.Message; }
        }
        if (result.PayloadReady) {
            try { result.Endpoints = ReadEndpoints(checks.ReadEndpoints, clock ?? (() => Environment.TickCount64), sleep ?? Thread.Sleep); }
            catch (Exception error) { result.DeviceError = error.Message; }
            // The saved selection is optional; a stale/unreadable value must not
            // discard a successfully enumerated list of selectable microphones.
            try {
                var identity = checks.ReadConfiguredIdentity();
                if (identity != null) {
                    try { result.SelectedEndpoint = EndpointIdentity.Resolve(identity, result.Endpoints); }
                    catch (InvalidOperationException) { } // Stale/ambiguous preference leaves selection to the user.
                }
            }
            catch (Exception error) { result.DeviceError ??= error.Message; }
        }
        return result;
    }
    private static EndpointIdentity[] ReadEndpoints(Func<EndpointIdentity[]> read, Func<long> clock, Action<int> sleep)
    {
        long began = clock();
        while (true) {
            try { return read(); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070015)) {
                if (clock() - began >= 5000) throw new IOException("マイク情報の準備が完了しません。接続を確認して再試行してください。", error);
                sleep(200);
            }
        }
    }
}
