namespace DotMic.Setup;

internal sealed class MultiSetupException(string message, Exception? source = null) : Exception(message, source)
{
    internal string UserMessage => Message;
}

// UI/backend boundary. Delegates are invoked on SetupWorker, never on the UI thread.
// Inspect/Prepare may save protected previews; only Apply accepts consent to change audio/service settings.
internal enum MultiSetupOperation { Install, Repair, Remove, Recover }
internal enum MultiSetupBlock { None, Payload, RecoveryRequired, Unavailable }
// IncludeAdvancedEndpoints asks for their impact preview, not permission approval.
internal sealed record MultiSetupRequest(MultiSetupOperation Operation, string[] Arguments, string Destination,
    bool DesktopShortcut, bool IncludeAdvancedEndpoints = true, bool RetryFailedOnly = false);
internal sealed record MultiSetupInspection(string? Destination = null, bool? DesktopShortcut = null,
    string Details = "", MultiSetupBlock Block = MultiSetupBlock.None);
internal sealed record MultiSetupPreview(int TargetCount, int AlreadyApplied, int Pending,
    int ReplacementCount, int PermissionCount, bool ProtectedAudioChanges, bool AudioInterruption,
    IReadOnlyList<string> DependentServices, string Details, object? Plan, bool CanApply,
    MultiSetupOperation Operation = MultiSetupOperation.Install, MultiSetupBlock Block = MultiSetupBlock.None,
    int DeferredCount = 0, bool CloseRunningApplication = false);
internal sealed record MultiSetupResult(int AppliedCount, int FailedCount, int PendingCount, string Details,
    bool CanOpenApplication = false, bool RecoveryRequired = false, string OperationIssue = "");

internal sealed record MultiSetupActions(
    Func<Task<MultiSetupInspection>> Inspect,
    Func<MultiSetupRequest, Task<MultiSetupPreview>> Prepare,
    Func<object, bool, Task<MultiSetupResult>> Apply,
    Func<Task> OpenApplication);

// Prepare dispatches Install/Repair/Remove/Recover; Recover uses inventory-owned scopes, never a user-selected fleet receipt.
// OperationIssue is short user-facing shared/service failure text; raw errors belong in Details, not microphone counts.
// Retry requests must freshly plan only failed/unapplied targets plus unresolved shared work.
// The backend revalidates the exact preview/permission/service scope at Apply and must never broaden consent.
// OpenApplication must use the primary session's unelevated launch helper, not elevated Process.Start.
