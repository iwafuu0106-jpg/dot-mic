using System.Text.Json;

namespace DotMic.Setup;

// Application/service lifecycle stays outside the endpoint transaction engine.
internal static class FleetSetup
{
    private sealed record Prepared(MultiSetupRequest Request, FleetPlan? Fleet, long Revision,
        AudioDependent[] Dependents, int TargetCount, string[] PermissionKeys, bool CloseApplicationApproved = false);
    internal static MultiSetupActions Actions(Func<string, string, bool>? decision = null) => new(
        () => Task.FromResult(Inspect()), request => Task.FromResult(Prepare(request)), (plan, advanced) => Apply(plan, advanced, decision ?? ((_, _) => false)),
        () => { UnelevatedLaunch.OpenApplication(); return Task.CompletedTask; });
    private static MultiSetupInspection Inspect()
    {
        PackageSource.Initialize();
        string? destination = ApplicationInstaller.ConfiguredRoot;
        var installed = destination == null ? null : ApplicationInstaller.ReadInstalled(destination);
        return new(destination, installed == null ? null : installed.ShortcutHash != null, "配布版: 0.5.0-rc.1。実機での導入・復旧は未検証です。");
    }
    private static string[] PermissionKeys(IEnumerable<Receipt> shared, IEnumerable<EndpointReceipt> endpoints) =>
        shared.SelectMany(r => r.Edits.Select(e => e.Path)).Concat(endpoints.SelectMany(r => r.Edits.Select(e => e.Path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !RawRegistry.CanWrite(path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static MultiSetupPreview Prepare(MultiSetupRequest request)
    {
        string package = PackageSource.DirectoryPath;
        if (request.Operation is MultiSetupOperation.Remove or MultiSetupOperation.Recover) {
            var inventory = FleetCoordinator.Inventory;
            // Import a legacy installation through scoped preparation first;
            // never give the old whole-device rollback sibling removal authority.
            if (inventory.SharedReceipt == null && !inventory.LegacyImported && RawRegistry.Value(Contract.ConfigPath, "OriginReceipt") != null) {
                FleetCoordinator.Prepare(package, true); inventory = FleetCoordinator.Inventory;
            }
            var shared = inventory.SharedRepairs.Prepend(inventory.SharedReceipt).Append(inventory.SharedCleanupReceipt)
                .OfType<string>().Select(Transaction.Read).ToArray();
            var devices = inventory.Devices.SelectMany(d => d.Receipts).Select(FleetStore.ReadEndpoint).ToArray();
            var dependents = shared.LastOrDefault(r => r.AudioRestartPending)?.AudioDependents.ToArray()
                ?? (shared.Length == 0 ? [] : AudioService.Dependents().ToArray());
            var permissions = PermissionKeys(shared, devices);
            int targets = inventory.Devices.Count(d => d.State != FleetEndpointState.Removed);
            int pending = request.Operation == MultiSetupOperation.Recover ? inventory.Devices.Count(d =>
                d.State is FleetEndpointState.RecoveryRequired or FleetEndpointState.Applying or FleetEndpointState.RemovalPending
                || d.Receipts.Any(path => FleetStore.ReadEndpoint(path).PendingSecurityRestore.Count > 0)) : targets;
            var prepared = new Prepared(request, null, inventory.Revision, dependents, targets, permissions);
            return new(targets, Math.Max(0, targets - pending), pending, 0, permissions.Length,
                shared.Any(r => !r.ProtectedAudioWasAlreadyOne), shared.Length > 0,
                dependents.Select(d => d.DisplayName).ToArray(), JsonSerializer.Serialize(inventory, Contract.Json), prepared,
                shared.Length > 0 || pending > 0, request.Operation);
        }
        Transaction.ValidatePayload(package);
        if (!PayloadPolicy.IsCandidate(Transaction.ValidatePayload(package)))
            throw new MultiSetupException("候補版のZIP全体を展開して、セットアップを開いてください。");
        var plan = FleetCoordinator.Prepare(package, request.Operation == MultiSetupOperation.Repair, retryFailedOnly: request.RetryFailedOnly);
        var current = FleetCoordinator.Inventory;
        bool sharedRecoveryRequired = current.SharedCleanupReceipt != null
            || plan.SharedRepairRequired && plan.SharedTransaction == null && current.SharedReceipt != null;
        var payload = Transaction.ValidatePayload(package);
        bool runtimeUpdate = RawRegistry.Value(Contract.ConfigPath, "ApoHash")?.Display != payload.ApoHash;
        // Candidate upgrade/application placement is shared work once, never per mic.
        bool applicationUpdate = ApplicationInstaller.ConfiguredRoot == null;
        if (!applicationUpdate) {
            var installed = ApplicationInstaller.ReadInstalled(ApplicationInstaller.ConfiguredRoot!);
            var candidate = payload.Files.Single(f => f.Path == "UI/DotMic.App.dll");
            applicationUpdate = installed?.Files.SingleOrDefault(f => f.Path == "内部ファイル/UI/DotMic.App.dll")?.Hash != candidate.Hash
                || installed != null && (installed.ShortcutHash != null) != request.DesktopShortcut;
            if (!ApplicationInstaller.ConfiguredRoot!.Equals(InstallPaths.Normalize(request.Destination), StringComparison.OrdinalIgnoreCase))
                throw new MultiSetupException("保存先を変更する場合は、削除してから導入し直してください。");
        }
        if (!sharedRecoveryRequired && plan.SharedTransaction == null && (runtimeUpdate || applicationUpdate)) plan.SharedTransaction = Transaction.PrepareShared(package);
        if (plan.SharedTransaction != null) {
            plan.SharedTransaction.ConfigureApplication(package, request.Destination, request.DesktopShortcut);
        }
        var active = Integration.Endpoints();
        int count = active.Count(FleetPolicy.Eligible);
        int already = current.Devices.Count(d => d.Desired && d.State == FleetEndpointState.Healthy
            && active.Any(e => e.EndpointId == d.Target.EndpointId));
        int replacements = plan.EndpointPlans.Count(r => r.Edits.Any(e => e.Path == r.Target.FxPath && e.Name == Contract.FxFormat + ",6"
            && e.Before != null && !e.Before.Same(RawValue.Text(e.Name, Contract.Clsid))));
        var sharedPlans = plan.SharedTransaction == null ? Array.Empty<Receipt>() : [plan.SharedTransaction.Receipt];
        var permissionKeys = PermissionKeys(sharedPlans, plan.EndpointPlans);
        var affectedServices = plan.SharedTransaction?.Receipt.AudioDependents.ToArray() ?? [];
        bool closeApplication = plan.SharedTransaction?.Receipt.Application != null && ApplicationUpdateExit.Running(request.Destination);
        var preparedPlan = new Prepared(request, plan, plan.Revision, affectedServices, count, permissionKeys, closeApplication);
        string details = JsonSerializer.Serialize(new { PlanErrors = plan.Errors, current, Shared = plan.SharedTransaction?.Receipt,
            Endpoints = plan.EndpointPlans, PermissionKeys = permissionKeys, ExcludedCaptureEndpoints = active.Where(e => !FleetPolicy.Eligible(e)) }, Contract.Json);
        bool recovery = sharedRecoveryRequired
            || current.Devices.Any(d => d.State == FleetEndpointState.RecoveryRequired);
        return new(count, already, plan.EndpointPlans.Count, replacements, permissionKeys.Length,
            plan.SharedTransaction != null && !plan.SharedTransaction.Receipt.ProtectedAudioWasAlreadyOne,
            plan.SharedTransaction != null, affectedServices.Select(s => s.DisplayName).ToArray(), details, preparedPlan,
            !recovery && (plan.SharedTransaction != null || plan.EndpointPlans.Count > 0 || current.SharedReceipt != null), request.Operation,
            recovery ? MultiSetupBlock.RecoveryRequired : MultiSetupBlock.None,
            Math.Max(0, active.Count(FleetPolicy.Eligible) - already - plan.EndpointPlans.Count),
            closeApplication);
    }
    private static async Task<MultiSetupResult> Apply(object opaque, bool advanced, Func<string, string, bool> decision)
    {
        var prepared = opaque as Prepared ?? throw new IOException("変更内容を確認し直してください。");
        if (FleetCoordinator.Inventory.Revision != prepared.Revision) throw new IOException("マイクの状態が変わりました。変更内容を確認し直してください。");
        if (prepared.Request.Operation != MultiSetupOperation.Recover && (prepared.Dependents.Length > 0 || prepared.Fleet?.SharedTransaction != null || prepared.Request.Operation == MultiSetupOperation.Remove))
            AudioService.ValidateDependents(prepared.Dependents);
        if (prepared.PermissionKeys.Length > 0 && !advanced) throw new OperationCanceledException("追加権限への承認が必要です。");
        if (prepared.Request.Operation == MultiSetupOperation.Remove) {
            // A stopped manager cannot recreate a binding while uninstall is pending.
            ResidentRegistration.Remove();
            var removed = await FleetCoordinator.Remove(PackageSource.DirectoryPath,
                new(true, true, true, true, AdvancedSharedPermission: advanced,
                    AdvancedEndpoints: advanced ? FleetCoordinator.Inventory.Devices.Select(d => d.Id).ToArray() : [],
                    ApprovedDependents: prepared.Dependents));
            return Result(removed, removing: true);
        }
        if (prepared.Request.Operation == MultiSetupOperation.Recover) {
            var recovered = await FleetCoordinator.Recover(new(true, true, true, true, AdvancedSharedPermission: advanced,
                AdvancedEndpoints: advanced ? FleetCoordinator.Inventory.Devices.Select(d => d.Id).ToArray() : [],
                ApprovedDependents: prepared.Dependents), decision);
            var recoveryResult = Result(recovered);
            return recoveryResult with { AppliedCount = 0, CanOpenApplication = false,
                OperationIssue = recovered.SharedError != null || recoveryResult.RecoveryRequired
                    ? "復旧が未完了です。「詳細」を確認してください。"
                    : "中断した変更を戻しました。修復を確認してください。" };
        }
        var plan = prepared.Fleet ?? throw new IOException("変更内容を確認し直してください。");
        if (plan.SharedTransaction?.Receipt.Application is { } application) {
            plan.SharedTransaction.ValidateBeforeApplicationExit();
            ApplicationUpdateExit.CloseForUpdate(application.Root, prepared.CloseApplicationApproved);
        }
        var before = FleetCoordinator.Inventory;
        Receipt? legacy = (before.LegacyLatestReceipt ?? before.LegacyOriginReceipt) is string authority ? Transaction.Read(authority) : null;
        CommonProfile.Initialize(legacy);
        var consent = new FleetConsent(true, true, true, true, AutomaticEnrollment: true, AdvancedSharedPermission: advanced,
            ReplaceEndpoints: plan.Devices.Select(d => d.Id).ToArray(),
            AdvancedEndpoints: advanced ? plan.Devices.Select(d => d.Id).ToArray() : [], ApprovedDependents: prepared.Dependents);
        var inventory = await FleetCoordinator.Apply(plan, consent);
        string managerError = "";
        if (inventory.SharedError == null && inventory.SharedReceipt != null) {
            try { ResidentRegistration.Install(); }
            catch (Exception error) { managerError = error.ToString(); }
        }
        var result = Result(inventory);
        return managerError.Length == 0 ? result : result with {
            OperationIssue = "自動適用を開始できませんでした。再試行してください。", Details = result.Details + "\n自動適用の開始: " + managerError
        };
    }
    private static MultiSetupResult Result(FleetInventory inventory, bool removing = false)
    {
        Receipt? shared = (inventory.SharedCleanupReceipt ?? inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt) is string head ? Transaction.Read(head) : null;
        int applied = inventory.Devices.Count(d => removing ? d.State == FleetEndpointState.Removed : d.Desired && d.State == FleetEndpointState.Healthy);
        int failed = inventory.Devices.Count(d => d.State is FleetEndpointState.ActionRequired or FleetEndpointState.RecoveryRequired);
        int pending = inventory.Devices.Count(d => d.State is FleetEndpointState.PendingActivation or FleetEndpointState.DeferredBusy or FleetEndpointState.RemovalPending or FleetEndpointState.Prepared or FleetEndpointState.Applying);
        return new(applied, failed, pending, JsonSerializer.Serialize(inventory, Contract.Json),
            !removing && ApplicationInstaller.ConfiguredRoot != null,
            inventory.Devices.Any(d => d.State == FleetEndpointState.RecoveryRequired)
                || inventory.SharedCleanupReceipt != null || shared != null && shared.Status != "Committed" && !FleetPolicy.RetirableShared(shared),
            inventory.SharedError == null ? "" : removing ? "元の設定への復元が未完了です。復旧を確認してください。" : "音声設定の変更を完了できませんでした。再試行してください。");
    }
    internal static bool Reconcile()
    {
        using var lease = OperationLock.TryAcquire();
        if (lease == null) return true;
        CommonProfile.Initialize();
        var inventory = FleetCoordinator.Reconcile(PackageSource.DirectoryPath, setupLockHeld: true).GetAwaiter().GetResult();
        foreach (var device in inventory.Devices.Where(d => d.Desired && d.State == FleetEndpointState.PendingActivation))
            if (ActivationObservation.Progressed(device.Target))
                inventory = FleetCoordinator.Observe(device.Target, true).GetAwaiter().GetResult();
        bool settingsPending = ProfilePropagation.Run(inventory);
        return settingsPending || inventory.Devices.Any(d => d.State is FleetEndpointState.PendingActivation or FleetEndpointState.DeferredBusy or FleetEndpointState.RemovalPending or FleetEndpointState.Applying)
            || inventory.SharedError != null;
    }
    internal static void LogManagerFailure(Exception error)
    {
        string directory = Path.Combine(Contract.RecoveryRoot, "Manager"); SecureStorage.Directory(directory);
        string path = Path.Combine(directory, "last-error.txt");
        if (File.Exists(path)) SecureStorage.RecoveryFile(path);
        File.WriteAllText(path, DateTime.UtcNow.ToString("O") + "\n" + error); SecureStorage.File(path);
    }
}
