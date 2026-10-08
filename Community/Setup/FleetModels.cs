namespace DotMic.Setup;

internal enum FleetEndpointState { Prepared, Applying, PendingActivation, Healthy, DeferredBusy, ActionRequired, RecoveryRequired, RemovalPending, Removed, Disconnected }
internal sealed class FleetDevice
{
    public string Id { get; set; } = "";
    public EndpointIdentity Target { get; set; } = null!;
    public FleetEndpointState State { get; set; } = FleetEndpointState.Prepared;
    public string? Error { get; set; }
    public bool Desired { get; set; } = true;
    public bool IdentityAmbiguous { get; set; }
    public bool UnownedBinding { get; set; }
    public List<string> Receipts { get; set; } = [];
}
internal sealed class FleetInventory
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = Contract.Version;
    public long Revision { get; set; }
    public bool AutomaticEnrollment { get; set; }
    public string? SharedReceipt { get; set; }
    public string? SharedCleanupReceipt { get; set; }
    public List<string> SharedRepairs { get; set; } = [];
    public List<string> RetiredSharedReceipts { get; set; } = [];
    public string? LegacyOriginReceipt { get; set; }
    public string? LegacyLatestReceipt { get; set; }
    public bool LegacyImported { get; set; }
    public string? SharedError { get; set; }
    public List<FleetDevice> Devices { get; set; } = [];
    public bool HasUnresolvedBindings => Devices.Any(d => d.State != FleetEndpointState.Removed
        && (d.Receipts.Count > 0 || d.UnownedBinding || d.State is FleetEndpointState.RemovalPending or FleetEndpointState.RecoveryRequired));
}
internal sealed class EndpointReceipt
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = Contract.Version;
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public string Scope { get; set; } = "Endpoint";
    public string Operation { get; set; } = "Install";
    public List<string> SourceReceipts { get; set; } = [];
    public EndpointIdentity Target { get; set; } = null!;
    public FleetEndpointState State { get; set; } = FleetEndpointState.Prepared;
    public string? Error { get; set; }
    public string? LegacyReceipt { get; set; }
    public List<KeyImage> Keys { get; set; } = [];
    public List<Edit> Edits { get; set; } = [];
    public List<Edit> ReplicaPrevious { get; set; } = [];
    public bool ReplicationPending { get; set; }
    public string? ProfileRevision { get; set; }
    public List<string> AdvancedKeys { get; set; } = [];
    public List<string> UndoPermissionKeys { get; set; } = [];
    public Dictionary<string, byte[]> UndoSecurity { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PendingSecurityRestore { get; set; } = [];
    public Dictionary<string, byte[]> PendingSecurityExpected { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> PendingSecurityOriginal { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
internal sealed record FleetConsent(bool SharedChanges = false, bool AudioRestart = false, bool ProtectedAudio = false,
    bool DependentServices = false, bool AutomaticEnrollment = false, bool AdvancedSharedPermission = false,
    IReadOnlyCollection<string>? ReplaceEndpoints = null, IReadOnlyCollection<string>? AdvancedEndpoints = null,
    IReadOnlyList<AudioDependent>? ApprovedDependents = null);
internal sealed class FleetPlan
{
    internal string Package { get; init; } = "";
    internal long Revision { get; init; }
    internal bool RepairOnly { get; init; }
    internal bool RetryFailedOnly { get; init; }
    internal Transaction? SharedTransaction { get; set; }
    internal bool SharedRepairRequired { get; set; }
    internal List<FleetDevice> Devices { get; } = [];
    internal List<EndpointReceipt> EndpointPlans { get; } = [];
    internal List<string> Errors { get; } = [];
}
internal static class FleetPolicy
{
    // Shared with the controller: backing hardware/jack evidence, never names.
    internal static bool Eligible(EndpointIdentity target) => DotMic.Common.CaptureEligibility.IsTarget(target.State, target.FormFactor,
        target.PnpId, target.PhysicalInterface, target.JackSubType,
        !string.IsNullOrEmpty(target.StableId) || !string.IsNullOrEmpty(target.ContainerId) && !string.IsNullOrEmpty(target.PhysicalInterface));
    internal static string Id(EndpointIdentity target) => Contract.Hash(System.Text.Encoding.UTF8.GetBytes(
        (!string.IsNullOrEmpty(target.StableId) ? "stable:" + target.StableId : "physical:" + target.PhysicalInterface.ToUpperInvariant())
        + "|container:" + target.ContainerId.ToUpperInvariant()));
    internal static string AmbiguousId(EndpointIdentity target) => Contract.Hash(System.Text.Encoding.UTF8.GetBytes(Id(target) + "|ambiguous:" + target.EndpointId + "|" + target.FxPath));
    internal static bool DependentsMatch(IReadOnlyList<AudioDependent> approved, IReadOnlyList<AudioDependent> actual, bool interrupted = false) =>
        (interrupted || approved.Count == actual.Count) && actual.All(a => approved.Any(e => e.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase) && e.OriginalState == a.OriginalState));
    internal static List<AudioDependent> ApproveDependents(Receipt receipt, FleetConsent consent, IReadOnlyList<AudioDependent> current, bool applying = false)
    {
        var approved = consent.ApprovedDependents ?? throw new OperationCanceledException("The explicitly previewed dependent-service inventory is required.");
        if ((applying || receipt.AudioRestartPending) && !DependentsMatch(receipt.AudioDependents, approved)) throw new IOException("Approved dependent services differ from the prepared/interrupted service inventory.");
        if (!DependentsMatch(approved, current, receipt.AudioRestartPending)) throw new IOException("Dependent services changed after consent; preview again.");
        return approved.ToList();
    }
    internal static bool RegistrationHealthy(IReadOnlyList<RawValue> expected, IEnumerable<KeyImage> actual)
    {
        var present = actual.Where(key => key.Exists).ToArray();
        return expected.Count > 0 && present.Length > 0 && present.All(key => expected.All(value => value.Same(key.Find(value.Name))));
    }
    internal static bool RetirableShared(Receipt receipt) => receipt.Scope == "Shared"
        && (receipt.Status == "RolledBack" && receipt.CompensationVerified && !receipt.RegistrationPending && !receipt.AudioRestartPending
            && receipt.PendingSecurityRestore.Count == 0 && !receipt.Files.Any(f => f.StageStarted || f.RestoreStageStarted)
            && receipt.Application?.CleanupPending != true && !receipt.ApplicationRemovalPending
            && !(receipt.Application?.Files.Any(f => f.StageStarted || f.RestoreStageStarted) ?? false)
            || receipt.Status == "Prepared" && !receipt.Edits.Any(e => e.Applied)
            && !receipt.Files.Any(f => f.Applied || f.StageStarted || f.RestoreStageStarted) && !receipt.RegistrationPending
            && !receipt.AudioRestartPending && receipt.PendingSecurityRestore.Count == 0
            && (receipt.Application == null || !receipt.Application.Files.Any(f => f.Applied || f.StageStarted || f.RestoreStageStarted)
                && !receipt.Application.ShortcutApplied && !receipt.Application.ShortcutStageStarted && !receipt.Application.ShortcutRestoreStageStarted && !receipt.Application.CleanupPending));
    internal static bool IsEndpointPath(string path) => System.Text.RegularExpressions.Regex.IsMatch(path,
        @"^SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Capture\\\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}\\FxProperties$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
internal interface IFleetRepository
{
    FleetInventory Load();
    void Save(FleetInventory inventory);
    string PathFor(EndpointReceipt receipt);
    EndpointReceipt ReadEndpoint(string path);
    void SaveEndpoint(EndpointReceipt receipt);
}
internal interface IFleetPlatform
{
    EndpointIdentity[] Endpoints();
    RawValue? Value(string path, string name);
    ReplicaProof OwnedReplica(EndpointIdentity target, string path, string name);
    void SetControl(EndpointIdentity target, uint property, float value);
    KeyImage Key(string path);
    bool CanWrite(string path);
    void Write(Edit edit, EndpointReceipt receipt, bool advanced, Action save, bool existingOnly = false);
    void RestoreSecurity(EndpointReceipt receipt, Action save, Func<string, string, bool>? resolveConflict = null);
    bool SharedHealthy();
    bool SharedCommitted(string path);
    Receipt ReadShared(string path);
    Transaction PrepareShared(string package);
    string ComposeSharedCleanup(IReadOnlyList<string> sources);
    Task ApplyShared(Transaction transaction, FleetConsent consent);
    Task RemoveShared(string path, FleetConsent consent, bool keepProtectedAudio);
    Task RecoverShared(string path, FleetConsent consent, Func<string, string, bool> resolveConflict, bool compensation);
    string? LegacyOrigin();
    string? LegacyLatest();
    Receipt ReadLegacy(string path);
    string ImportShared(string path);
}
