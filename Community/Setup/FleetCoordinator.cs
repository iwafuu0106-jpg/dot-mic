namespace DotMic.Setup;

// Interactive Prepare/Apply/Repair/Remove are called under Program's existing
// setup-operation.lock. Reconcile acquires it itself for a resident/task host.
// The fleet lock additionally serializes all new callers.
internal static class FleetCoordinator
{
    private static readonly SemaphoreSlim Serial = new(1, 1);
    private static async Task<T> Run<T>(Func<FleetManager, Task<T>> action)
    {
        await Serial.WaitAsync().ConfigureAwait(false);
        try {
            SecureStorage.Directory(FleetStore.Root);
            string path = Path.Combine(FleetStore.Root, "fleet-operation.lock");
            if (File.Exists(path)) SecureStorage.RecoveryFile(path);
            using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SecureStorage.File(path);
            return await action(new(new NativeFleetPlatform(), new ProtectedFleetRepository())).ConfigureAwait(false);
        } finally { Serial.Release(); }
    }
    internal static FleetPlan Prepare(string package, bool repairOnly = false, bool retryFailedOnly = false) => Run(manager => Task.FromResult(manager.Prepare(package, repairOnly, retryFailedOnly: retryFailedOnly))).GetAwaiter().GetResult();
    internal static Task<FleetInventory> Apply(FleetPlan plan, FleetConsent consent) => Run(manager => manager.Apply(plan, consent));
    internal static async Task<FleetInventory> Reconcile(string package, bool automatic = true, bool setupLockHeld = false)
    {
        if (setupLockHeld) return await Run(manager => manager.Reconcile(package, automatic)).ConfigureAwait(false);
        SecureStorage.Directory(Contract.RecoveryRoot);
        string path = Path.Combine(Contract.RecoveryRoot, "setup-operation.lock");
        if (File.Exists(path)) SecureStorage.RecoveryFile(path);
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SecureStorage.File(path);
        return await Run(manager => manager.Reconcile(package, automatic)).ConfigureAwait(false);
    }
    internal static Task<FleetInventory> Repair(string package, FleetConsent consent) => Run(manager => manager.Apply(manager.Prepare(package, repairOnly: true), consent));
    internal static Task<FleetInventory> Observe(EndpointIdentity target, bool processingConfirmed) => Run(manager => Task.FromResult(manager.Observe(target, processingConfirmed)));
    internal static Task<FleetInventory> Recover(FleetConsent consent, Func<string, string, bool>? resolveConflict = null) => Run(manager => manager.Recover(consent, resolveConflict ?? ((_, _) => false)));
    internal static Task<bool> ReplicateProfiles(float[] values) => Run(manager => Task.FromResult(manager.ReplicateProfiles(values)));
    internal static Task<FleetInventory> Remove(string package, FleetConsent consent, bool keepProtectedAudio = false) => Run(manager => manager.Remove(consent, keepProtectedAudio));
    internal static FleetInventory Inventory => FleetStore.Load();
}

internal sealed class FleetManager(IFleetPlatform platform, IFleetRepository repository)
{
    private readonly EndpointTransaction endpoints = new(platform, repository);
    private void Save(FleetInventory inventory) { inventory.Revision++; repository.Save(inventory); }
    private FleetInventory ImportLegacy()
    {
        var inventory = repository.Load();
        if (inventory.SharedReceipt != null || inventory.LegacyImported) return inventory;
        string? path = inventory.LegacyOriginReceipt ?? platform.LegacyOrigin();
        if (path == null) return inventory;
        var receipt = platform.ReadLegacy(path);
        if (receipt.Scope != "Legacy" || receipt.Status != "Committed" || receipt.RegistrationPending || receipt.PendingSecurityRestore.Count != 0 || receipt.AudioRestartPending)
            throw new IOException("Legacy recovery must finish before fleet enrollment; original receipt retained.");
        inventory.LegacyOriginReceipt = path; Save(inventory);
        var endpoint = new EndpointReceipt { TransactionId = receipt.TransactionId, Target = receipt.Target, LegacyReceipt = path,
            State = FleetEndpointState.PendingActivation,
            AdvancedKeys = receipt.AdvancedKeys.Where(k => FleetPolicy.Within(k, receipt.Target.FxPath)).ToList(),
            Keys = receipt.Keys.Where(k => FleetPolicy.Within(k.Path, receipt.Target.FxPath)).ToList(),
            Edits = receipt.Edits.Where(e => FleetPolicy.Within(e.Path, receipt.Target.FxPath)).ToList() };
        string endpointPath = repository.PathFor(endpoint);
        repository.SaveEndpoint(endpoint);
        var device = inventory.Devices.SingleOrDefault(d => d.Id == FleetPolicy.Id(receipt.Target));
        if (device == null) { device = new() { Id = FleetPolicy.Id(receipt.Target), Target = receipt.Target, State = FleetEndpointState.PendingActivation }; inventory.Devices.Add(device); }
        if (!device.Receipts.Contains(endpointPath, StringComparer.OrdinalIgnoreCase)) device.Receipts.Add(endpointPath);
        // Persist endpoint ownership before publishing shared removal authority.
        Save(inventory);
        var chain = new List<(string Path, Receipt Receipt)>();
        string? latestPath = platform.LegacyLatest();
        string? cursor = latestPath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        while (cursor != null && !cursor.Equals(path, StringComparison.OrdinalIgnoreCase)) {
            if (chain.Count >= 512 || !seen.Add(cursor)) throw new IOException("Legacy receipt chain is cyclic or exceeds its bounded budget.");
            var latest = platform.ReadLegacy(cursor);
            if (latest.Scope != "Legacy" || latest.Status != "Committed" || FleetPolicy.Id(latest.Target) != FleetPolicy.Id(receipt.Target)
                || latest.RegistrationPending || latest.PendingSecurityRestore.Count != 0 || latest.AudioRestartPending)
                throw new IOException("Legacy receipt chain requires recovery; origin baseline remains protected.");
            chain.Add((cursor, latest));
            cursor = latest.Edits.FirstOrDefault(e => e.Path.Equals(Contract.ConfigPath, StringComparison.OrdinalIgnoreCase)
                && e.Name.Equals("LatestReceipt", StringComparison.OrdinalIgnoreCase))?.Before?.Display;
            if (string.IsNullOrWhiteSpace(cursor)) throw new IOException("Legacy receipt chain cannot establish its original baseline.");
        }
        inventory.SharedReceipt = platform.ImportShared(path);
        foreach (var link in chain.AsEnumerable().Reverse()) {
            var latest = link.Receipt;
            var latestEndpoint = new EndpointReceipt { TransactionId = latest.TransactionId, Target = latest.Target, LegacyReceipt = link.Path,
                State = FleetEndpointState.PendingActivation,
                AdvancedKeys = latest.AdvancedKeys.Where(k => FleetPolicy.Within(k, latest.Target.FxPath)).ToList(),
                Keys = latest.Keys.Where(k => FleetPolicy.Within(k.Path, latest.Target.FxPath)).ToList(),
                Edits = latest.Edits.Where(e => FleetPolicy.Within(e.Path, latest.Target.FxPath)).ToList() };
            repository.SaveEndpoint(latestEndpoint);
            device.Receipts.Add(repository.PathFor(latestEndpoint)); device.Target = latest.Target;
            inventory.LegacyLatestReceipt = latestPath;
            inventory.SharedRepairs.Add(platform.ImportShared(link.Path));
        }
        inventory.LegacyImported = true; Save(inventory); return inventory;
    }
    internal FleetPlan Prepare(string package, bool repairOnly = false, bool allowSharedPreparation = true, bool retryFailedOnly = false)
    {
        var inventory = ImportLegacy();
        var plan = new FleetPlan { Package = package, Revision = inventory.Revision, RepairOnly = repairOnly, RetryFailedOnly = retryFailedOnly };
        if (inventory.SharedCleanupReceipt != null) {
            plan.SharedRepairRequired = true; plan.Errors.Add("Interrupted composed shared cleanup requires scoped recovery before repair."); return plan;
        }
        if (inventory.SharedReceipt == null) {
            if (allowSharedPreparation) plan.SharedTransaction = platform.PrepareShared(package);
            else plan.SharedRepairRequired = true;
        }
        else if (!SharedReady(inventory)) {
            plan.SharedRepairRequired = true;
            if (!platform.SharedCommitted(inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt))
                plan.Errors.Add("An interrupted shared transaction must be recovered before repair.");
            else if (allowSharedPreparation) plan.SharedTransaction = platform.PrepareShared(package);
        }
        var active = platform.Endpoints();
        foreach (var target in active.Where(FleetPolicy.Eligible)) {
            try { EndpointIdentity.Resolve(target, active); }
            catch (InvalidOperationException error) {
                plan.Errors.Add(target.FriendlyName + ": " + error.Message);
                plan.Devices.Add(new() { Id = FleetPolicy.AmbiguousId(target), Target = target, IdentityAmbiguous = true, State = FleetEndpointState.ActionRequired, Error = error.Message }); continue;
            }
            FleetDevice? prior;
            try { prior = FindDevice(inventory, target, active); }
            catch (IOException error) {
                plan.Errors.Add(target.FriendlyName + ": " + error.Message);
                plan.Devices.Add(new() { Id = FleetPolicy.AmbiguousId(target), Target = target, IdentityAmbiguous = true, State = FleetEndpointState.ActionRequired, Error = error.Message }); continue;
            }
            // Stable identity can survive physical-interface changes. Never match by
            // name/default/container alone or collapse separate capture endpoints.
            if (prior?.Desired == false && (repairOnly || !allowSharedPreparation || inventory.SharedReceipt != null)) continue;
            if (retryFailedOnly && (prior == null || prior.State is not (FleetEndpointState.ActionRequired or FleetEndpointState.RecoveryRequired or FleetEndpointState.DeferredBusy or FleetEndpointState.Disconnected))) continue;
            try {
                if (prior != null && endpoints.Healthy(target) && prior.Receipts.Count > 0
                    && prior.Target.FxPath.Equals(target.FxPath, StringComparison.OrdinalIgnoreCase)
                    && prior.State is FleetEndpointState.PendingActivation or FleetEndpointState.Healthy) continue;
            } catch (Exception error) {
                plan.Errors.Add(target.FriendlyName + ": " + error.Message);
                plan.Devices.Add(new() { Id = prior?.Id ?? FleetPolicy.Id(target), Target = target, State = FleetEndpointState.ActionRequired, Error = error.Message }); continue;
            }
            var plannedDevice = new FleetDevice { Id = prior?.Id ?? FleetPolicy.Id(target), Target = target }; plan.Devices.Add(plannedDevice);
            try { plan.EndpointPlans.Add(endpoints.Prepare(target)); }
            catch (Exception error) { plan.Errors.Add(target.FriendlyName + ": " + error.Message); plannedDevice.State = FleetEndpointState.ActionRequired; plannedDevice.Error = error.Message; }
        }
        return plan;
    }
    internal async Task<FleetInventory> Apply(FleetPlan plan, FleetConsent consent)
    {
        var inventory = repository.Load();
        if (inventory.Revision != plan.Revision) throw new IOException("Fleet inventory changed; prepare a fresh plan.");
        bool changed = false;
        if ((!plan.RepairOnly || consent.AutomaticEnrollment) && inventory.AutomaticEnrollment != consent.AutomaticEnrollment) { inventory.AutomaticEnrollment = consent.AutomaticEnrollment; changed = true; }
        foreach (var failure in plan.Devices.Where(d => d.Error != null)) {
            var existing = inventory.Devices.SingleOrDefault(d => d.Id == failure.Id);
            if (existing == null) inventory.Devices.Add(failure);
            else { existing.State = failure.State; existing.Error = failure.Error; }
            changed = true;
        }
        if (changed) Save(inventory);
        if (plan.SharedRepairRequired && plan.SharedTransaction == null) { inventory.SharedError = string.Join(" / ", plan.Errors); Save(inventory); return inventory; }
        if (plan.SharedTransaction != null) {
            if (!consent.SharedChanges || !consent.AudioRestart || !consent.ProtectedAudio || !consent.DependentServices) throw new OperationCanceledException("Shared installation requires explicit Windows-wide and service-interruption consent.");
            plan.SharedTransaction.Receipt.PredecessorReceipt = inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt;
            if (inventory.SharedReceipt == null) inventory.SharedReceipt = plan.SharedTransaction.ReceiptPath;
            else inventory.SharedRepairs.Add(plan.SharedTransaction.ReceiptPath);
            Save(inventory);
            try {
                await platform.ApplyShared(plan.SharedTransaction, consent).ConfigureAwait(false); inventory.SharedError = null;
                foreach (var device in inventory.Devices.Where(d => d.Desired && d.State == FleetEndpointState.Healthy)) device.State = FleetEndpointState.PendingActivation;
                Save(inventory);
            }
            catch (Exception error) { inventory.SharedError = error.Message; RetireAttempt(inventory, plan.SharedTransaction.ReceiptPath); Save(inventory); return inventory; }
        }
        if (!SharedReady(inventory)) { inventory.SharedError = "Shared installation is unavailable."; Save(inventory); return inventory; }
        foreach (var receipt in plan.EndpointPlans) {
            var active = platform.Endpoints();
            var device = FindDevice(inventory, receipt.Target, active);
            if (device == null) { device = new() { Id = FleetPolicy.Id(receipt.Target), Target = receipt.Target }; inventory.Devices.Add(device); }
            if (!device.Desired) {
                if (!plan.RepairOnly && plan.SharedTransaction != null && consent.SharedChanges) device.Desired = true;
                else continue;
            }
            if (device.Receipts.Any(p => repository.ReadEndpoint(p).State is FleetEndpointState.Applying or FleetEndpointState.RecoveryRequired or FleetEndpointState.RemovalPending)) {
                device.State = FleetEndpointState.RecoveryRequired; device.Error = "An interrupted endpoint journal must be recovered first."; Save(inventory); continue;
            }
            EndpointIdentity current;
            try { current = EndpointIdentity.Resolve(receipt.Target, active); }
            catch (InvalidOperationException error) { device.State = FleetEndpointState.ActionRequired; device.Error = error.Message; Save(inventory); continue; }
            if (!current.FxPath.Equals(receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase)) { device.State = FleetEndpointState.ActionRequired; device.Error = "Endpoint location changed; prepare a fresh plan."; Save(inventory); continue; }
            if (current.SharedModeBusy != false) { device.State = FleetEndpointState.DeferredBusy; device.Error = "Microphone is busy or activity is unknown; no settings were written."; Save(inventory); continue; }
            if (device.Receipts.Count == 0 && platform.Value(current.FxPath, Contract.FxFormat + ",6")?.Same(RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid)) == true) {
                device.UnownedBinding = true; device.State = FleetEndpointState.ActionRequired; device.Error = "An existing DOT MIC binding has no ownership receipt; automatic enrollment cannot claim it."; Save(inventory); continue;
            }
            bool replace = consent.ReplaceEndpoints?.Contains(device.Id) == true;
            bool advanced = consent.AdvancedEndpoints?.Contains(device.Id) == true;
            string? blocked;
            try { blocked = endpoints.Blocked(receipt, replace, advanced); }
            catch (Exception error) { device.State = FleetEndpointState.ActionRequired; device.Error = error.Message; Save(inventory); continue; }
            if (blocked != null) { device.State = FleetEndpointState.ActionRequired; device.Error = blocked; Save(inventory); continue; }
            repository.SaveEndpoint(receipt);
            device.Receipts.Add(repository.PathFor(receipt)); device.Target = current; device.Id = FleetPolicy.Id(current); device.State = FleetEndpointState.Applying; Save(inventory);
            try { endpoints.Apply(receipt, advanced); device.State = receipt.State; device.Error = null; }
            catch (Exception error) { device.State = receipt.State == FleetEndpointState.Removed ? FleetEndpointState.ActionRequired : FleetEndpointState.RecoveryRequired; device.Error = error.Message + (receipt.Error == null ? "" : " / " + receipt.Error); }
            Save(inventory); // An endpoint failure never compensates shared/sibling state.
        }
        return inventory;
    }
    internal async Task<FleetInventory> Reconcile(string package, bool automatic)
    {
        try { return await ReconcileCore(package, automatic).ConfigureAwait(false); }
        catch (Exception error) {
            // Never replace an unreadable inventory with an empty one. If loading
            // it itself fails, propagate and retain the protected damaged record.
            var inventory = repository.Load(); inventory.SharedError = error.Message; Save(inventory); return inventory;
        }
    }
    private async Task<FleetInventory> ReconcileCore(string package, bool automatic)
    {
        var inventory = ImportLegacy();
        foreach (var device in inventory.Devices) {
            if (!device.Desired) {
                try { device.State = RestoreDevice(inventory, device, new(), null) ? FleetEndpointState.Removed : FleetEndpointState.RemovalPending; }
                catch (Exception error) { device.State = FleetEndpointState.RecoveryRequired; device.Error = error.Message; }
                Save(inventory); continue;
            }
            foreach (string path in device.Receipts.AsEnumerable().Reverse()) {
                var receipt = repository.ReadEndpoint(path);
                if (!device.Desired && receipt.State == FleetEndpointState.Removed) continue;
                if (!device.Desired || receipt.State is FleetEndpointState.Applying or FleetEndpointState.RecoveryRequired or FleetEndpointState.RemovalPending) {
                    try {
                        bool restored = endpoints.Restore(receipt);
                        device.State = restored ? device.Desired ? FleetEndpointState.ActionRequired : FleetEndpointState.Removed : FleetEndpointState.RemovalPending;
                        device.Error = receipt.Error; Save(inventory);
                        if (!restored) break;
                    } catch (Exception error) { device.State = FleetEndpointState.RecoveryRequired; device.Error = error.Message; Save(inventory); break; }
                }
            }
            if (!device.Desired && HasBinding(device)) {
                device.UnownedBinding = true; device.State = FleetEndpointState.RemovalPending; device.Error = "A retained DOT MIC binding blocks shared cleanup."; Save(inventory);
            }
        }
        if (!automatic || !inventory.AutomaticEnrollment) return inventory;
        if (!SharedReady(inventory)) { inventory.SharedError = "Shared repair requires explicit consent; automatic reconciliation made no shared changes."; Save(inventory); return inventory; }
        var active = platform.Endpoints();
        foreach (var device in inventory.Devices.Where(d => d.Desired)) {
            try {
                var current = EndpointIdentity.Resolve(device.Target, active);
                if (device.State == FleetEndpointState.Disconnected && device.Receipts.Count > 0
                    && current.FxPath.Equals(device.Target.FxPath, StringComparison.OrdinalIgnoreCase) && endpoints.Healthy(current)) {
                    device.Target = current; device.Id = FleetPolicy.Id(current); device.State = FleetEndpointState.PendingActivation; device.Error = null; Save(inventory);
                }
            } catch (EndpointUnavailableException) {
                if (device.State is FleetEndpointState.Healthy or FleetEndpointState.PendingActivation) {
                    device.State = FleetEndpointState.Disconnected; device.Error = "Microphone disconnected; ownership receipts retained."; Save(inventory);
                }
            } catch (InvalidOperationException error) { device.State = FleetEndpointState.ActionRequired; device.Error = error.Message; Save(inventory); }
        }
        // Do not prepare/redeploy/register shared resources on hotplug.
        var plan = Prepare(package, allowSharedPreparation: false);
        return await Apply(plan, new(AutomaticEnrollment: true)).ConfigureAwait(false);
    }
    internal async Task<FleetInventory> Remove(FleetConsent consent, bool keepProtectedAudio)
    {
        var inventory = ImportLegacy(); inventory.AutomaticEnrollment = false;
        foreach (var device in inventory.Devices) device.Desired = false;
        Save(inventory); // Tombstones win over queued arrivals, even after a crash.
        foreach (var device in inventory.Devices) {
            bool complete;
            try { complete = RestoreDevice(inventory, device, consent, null); }
            catch (Exception error) { complete = false; device.Error = error.Message; }
            device.State = complete ? FleetEndpointState.Removed : FleetEndpointState.RemovalPending;
            if (complete) device.Error = null;
            Save(inventory);
        }
        if (inventory.HasUnresolvedBindings) { inventory.SharedError = "Shared cleanup blocked by unresolved endpoint bindings."; Save(inventory); return inventory; }
        if (inventory.SharedReceipt != null) {
            if (!consent.SharedChanges || !consent.AudioRestart || !consent.DependentServices || !consent.ProtectedAudio) {
                inventory.SharedError = "Endpoints detached; shared cleanup awaits explicit service/Protected Audio consent."; Save(inventory); return inventory;
            }
            try {
                if (inventory.SharedCleanupReceipt == null) {
                    if (inventory.RetiredSharedReceipts.Count >= 512) throw new IOException("Shared archive limit reached; retain cleanup ownership before archiving.");
                    inventory.SharedCleanupReceipt = platform.ComposeSharedCleanup(inventory.SharedRepairs.Prepend(inventory.SharedReceipt).OfType<string>().ToArray());
                    Save(inventory); // Publish cleanup ownership before its first mutation.
                }
                await platform.RemoveShared(inventory.SharedCleanupReceipt, consent, keepProtectedAudio).ConfigureAwait(false);
                CompleteSharedCleanup(inventory); Save(inventory);
            }
            catch (Exception error) { inventory.SharedError = error.Message; Save(inventory); }
        }
        return inventory;
    }
    private void CompleteSharedCleanup(FleetInventory inventory)
    {
        if (inventory.SharedCleanupReceipt == null) return;
        // The archived composition links every immutable source, without growing
        // the inventory by one entry per historical repair.
        if (!inventory.RetiredSharedReceipts.Contains(inventory.SharedCleanupReceipt, StringComparer.OrdinalIgnoreCase)) inventory.RetiredSharedReceipts.Add(inventory.SharedCleanupReceipt);
        inventory.SharedReceipt = null; inventory.SharedRepairs.Clear(); inventory.SharedCleanupReceipt = null; inventory.SharedError = null;
    }
    private bool HasBinding(FleetDevice device) => FleetPolicy.IsEndpointPath(device.Target.FxPath)
        && platform.Value(device.Target.FxPath, Contract.FxFormat + ",6")?.Same(RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid)) == true;
    private bool RestoreDevice(FleetInventory inventory, FleetDevice device, FleetConsent consent, Func<string, string, bool>? resolveConflict)
    {
        var records = device.Receipts.Select(path => (Path: path, Receipt: repository.ReadEndpoint(path))).ToArray();
        foreach (var record in records.Where(r => r.Receipt.PendingSecurityRestore.Count > 0)) endpoints.RecoverSecurity(record.Receipt, resolveConflict);
        foreach (var group in records.GroupBy(r => r.Receipt.Target.FxPath, StringComparer.OrdinalIgnoreCase).Reverse()) {
            var existing = group.LastOrDefault(r => r.Receipt.Operation == "RestoreComposition" && (r.Receipt.State != FleetEndpointState.Removed
                || r.Receipt.SourceReceipts.Any(path => group.Any(source => source.Path == path && source.Receipt.State != FleetEndpointState.Removed))));
            var sources = group.Where(r => r.Receipt.State != FleetEndpointState.Removed && r.Receipt.Operation != "RestoreComposition").ToArray();
            if (existing.Receipt == null && sources.Length == 0) continue;
            EndpointReceipt composed;
            if (existing.Receipt != null) composed = existing.Receipt;
            else {
                composed = EndpointTransaction.Compose(sources.Select(r => r.Receipt).ToArray(), sources.Select(r => r.Path).ToArray());
                repository.SaveEndpoint(composed); device.Receipts.Add(repository.PathFor(composed)); Save(inventory);
            }
            bool complete = endpoints.Restore(composed, consent.AdvancedEndpoints?.Contains(device.Id) == true, resolveConflict);
            if (!complete) { device.Error = composed.Error; return false; }
            foreach (string sourcePath in composed.SourceReceipts) {
                var source = repository.ReadEndpoint(sourcePath);
                foreach (var edit in source.Edits) edit.Applied = false;
                source.State = FleetEndpointState.Removed; source.Error = null; repository.SaveEndpoint(source);
            }
        }
        if (HasBinding(device)) { device.UnownedBinding = true; device.Error = "A retained DOT MIC binding blocks shared cleanup."; return false; }
        device.UnownedBinding = false; return true;
    }
    private bool RetireAttempt(FleetInventory inventory, string path)
    {
        if (!FleetPolicy.RetirableShared(platform.ReadShared(path))) return false;
        if (inventory.SharedRepairs.LastOrDefault()?.Equals(path, StringComparison.OrdinalIgnoreCase) == true) inventory.SharedRepairs.RemoveAt(inventory.SharedRepairs.Count - 1);
        else if (inventory.SharedReceipt?.Equals(path, StringComparison.OrdinalIgnoreCase) == true && inventory.SharedRepairs.Count == 0) inventory.SharedReceipt = null;
        else return false;
        if (!inventory.RetiredSharedReceipts.Contains(path, StringComparer.OrdinalIgnoreCase)) inventory.RetiredSharedReceipts.Add(path);
        return true;
    }
    internal async Task<FleetInventory> Recover(FleetConsent consent, Func<string, string, bool> resolveConflict)
    {
        var inventory = repository.Load();
        foreach (var device in inventory.Devices) {
            if (!device.Desired) {
                try { device.State = RestoreDevice(inventory, device, consent, resolveConflict) ? FleetEndpointState.Removed : FleetEndpointState.RemovalPending; }
                catch (Exception error) { device.State = FleetEndpointState.RecoveryRequired; device.Error = error.Message; }
                Save(inventory); continue;
            }
            foreach (string path in device.Receipts.AsEnumerable().Reverse()) {
                var receipt = repository.ReadEndpoint(path);
                if (receipt.State is not (FleetEndpointState.Applying or FleetEndpointState.RecoveryRequired or FleetEndpointState.RemovalPending) && receipt.PendingSecurityRestore.Count == 0) continue;
                try {
                    if (receipt.State is not (FleetEndpointState.Applying or FleetEndpointState.RecoveryRequired or FleetEndpointState.RemovalPending)) {
                        endpoints.RecoverSecurity(receipt, resolveConflict); continue;
                    }
                    bool complete = endpoints.Restore(receipt, consent.AdvancedEndpoints?.Contains(device.Id) == true, resolveConflict);
                    device.State = complete ? FleetEndpointState.ActionRequired : FleetEndpointState.RecoveryRequired;
                    device.Error = complete ? "Interrupted endpoint changes were compensated; prepare a fresh repair." : receipt.Error;
                } catch (Exception error) { device.State = FleetEndpointState.RecoveryRequired; device.Error = error.Message; }
                Save(inventory);
                if (device.State == FleetEndpointState.RecoveryRequired) break;
            }
        }
        if (inventory.SharedCleanupReceipt != null) {
            try {
                if (inventory.HasUnresolvedBindings) throw new IOException("Composed shared recovery is blocked by unresolved endpoint ownership.");
                await platform.RecoverShared(inventory.SharedCleanupReceipt, consent, resolveConflict, compensation: false).ConfigureAwait(false);
                CompleteSharedCleanup(inventory);
            } catch (Exception error) { inventory.SharedError = error.Message; }
            Save(inventory); return inventory;
        }
        string? head = inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt;
        if (head != null && !platform.SharedCommitted(head)) {
            try {
                if (!RetireAttempt(inventory, head)) {
                    var receipt = platform.ReadShared(head);
                    await platform.RecoverShared(head, consent, resolveConflict, receipt.PredecessorReceipt != null).ConfigureAwait(false);
                    if (!RetireAttempt(inventory, head)) throw new IOException("Shared recovery still has pending/conflicting ownership; its journal remains active.");
                }
                inventory.SharedError = null;
            } catch (Exception error) { inventory.SharedError = error.Message; }
            Save(inventory);
        }
        return inventory;
    }
    internal bool ReplicateProfiles(float[] values)
    {
        if (values.Length != 9 || values.Where((value, index) => !FleetReplica.Valid(index + 1, value)).Any()) throw new IOException("Invalid common profile.");
        var inventory = repository.Load();
        if (!inventory.AutomaticEnrollment) return false;
        bool pending = false;
        var active = platform.Endpoints();
        foreach (var device in inventory.Devices.Where(d => d.Desired && d.Receipts.Count > 0 && d.State is FleetEndpointState.Healthy or FleetEndpointState.PendingActivation)) {
            try {
                var target = EndpointIdentity.Resolve(device.Target, active);
                if (!target.FxPath.Equals(device.Target.FxPath, StringComparison.OrdinalIgnoreCase) || !endpoints.Healthy(target)) continue;
                var records = device.Receipts.Select(repository.ReadEndpoint).Where(r => r.State != FleetEndpointState.Removed && r.Target.FxPath.Equals(target.FxPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (records.Length == 0) continue;
                var composed = EndpointTransaction.Compose(records, device.Receipts.Where(path => {
                    var r = repository.ReadEndpoint(path); return r.State != FleetEndpointState.Removed && r.Target.FxPath.Equals(target.FxPath, StringComparison.OrdinalIgnoreCase);
                }).ToArray());
                var journal = records.LastOrDefault(r => r.Operation == "Replica");
                var controls = composed.Edits.Where(e => FleetReplica.IsControl(target, e.Path, e.Name)).ToArray();
                // Validate all properties before changing any: original snapshot,
                // journaled expectation or exact current common authority only.
                foreach (var edit in controls) {
                    var now = platform.Value(edit.Path, edit.Name);
                    bool Same(RawValue? value) => value == null ? now == null : value.Same(now);
                    if (!Same(edit.Before) && !Same(edit.After) && !composed.ReplicaPrevious.Any(e => e.Path == edit.Path && e.Name == edit.Name && Same(e.After))
                        && !platform.OwnedReplica(target, edit.Path, edit.Name).Matches(now)) throw new IOException("External parameter retained: " + edit.Name);
                }
                if (journal == null) {
                    journal = new() { Target = target, Operation = "Replica", State = FleetEndpointState.PendingActivation, Keys = composed.Keys,
                        Edits = controls.Select(e => new Edit { Path = e.Path, Name = e.Name, Before = e.Before, After = e.After, Applied = true }).ToList() };
                    repository.SaveEndpoint(journal); device.Receipts.Add(repository.PathFor(journal)); Save(inventory);
                }
                string revision = FleetReplica.Revision(values);
                bool matches = journal.Edits.All(e => {
                    int id = int.Parse(e.Name[(e.Name.LastIndexOf(',') + 1)..]);
                    var desired = e.Path.EndsWith(@"\Volatile", StringComparison.OrdinalIgnoreCase) ? null : FleetReplica.Serialized(id, values[id - 1]);
                    var now = platform.Value(e.Path, e.Name); return desired == null ? now == null : desired.Same(now);
                });
                if (journal.ProfileRevision == revision && !journal.ReplicationPending && matches) continue;
                journal.ReplicaPrevious = journal.Edits.Select(e => new Edit { Path = e.Path, Name = e.Name, After = platform.Value(e.Path, e.Name) }).ToList();
                foreach (var edit in journal.Edits) {
                    int id = int.Parse(edit.Name[(edit.Name.LastIndexOf(',') + 1)..]);
                    edit.After = edit.Path.EndsWith(@"\Volatile", StringComparison.OrdinalIgnoreCase) ? null : FleetReplica.Serialized(id, values[id - 1]);
                    edit.Applied = true;
                }
                journal.ProfileRevision = revision; journal.ReplicationPending = true; repository.SaveEndpoint(journal);
                for (uint id = 1; id <= 9; id++) {
                    // CAPX notifications are parameter-only. The complete intent
                    // and earliest baseline are durable before the first call.
                    foreach (var edit in journal.Edits.Where(e => e.Name == FleetReplica.Name((int)id))) {
                        var now = platform.Value(edit.Path, edit.Name);
                        bool Same(RawValue? expected) => expected == null ? now == null : expected.Same(now);
                        if (!Same(edit.After) && !journal.ReplicaPrevious.Any(e => e.Path == edit.Path && e.Name == edit.Name && Same(e.After))
                            && !platform.OwnedReplica(target, edit.Path, edit.Name).Matches(now)) throw new IOException("Parameter changed before CAPX notification: " + edit.Name);
                    }
                    platform.SetControl(target, id, values[id - 1]);
                    foreach (var edit in journal.Edits.Where(e => e.Name == FleetReplica.Name((int)id))) {
                        var actual = platform.Value(edit.Path, edit.Name);
                        if (edit.After == null ? actual != null : !edit.After.Same(actual)) throw new IOException("CAPX replica read-back failed: " + edit.Name);
                    }
                }
                journal.ReplicationPending = false; journal.Error = null; repository.SaveEndpoint(journal);
                if (device.Error?.StartsWith("Profile replica:", StringComparison.Ordinal) == true) device.Error = null;
                Save(inventory);
            } catch (InvalidOperationException) { /* Offline targets inherit on return. */ }
            catch (Exception error) { pending = true; device.Error = "Profile replica: " + error.Message; Save(inventory); }
        }
        return pending;
    }
    private bool SharedReady(FleetInventory inventory) => inventory.SharedReceipt != null
        && platform.SharedCommitted(inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt) && platform.SharedHealthy();
    private static FleetDevice? FindDevice(FleetInventory inventory, EndpointIdentity target, EndpointIdentity[] active)
    {
        var matches = inventory.Devices.Where(device => !device.IdentityAmbiguous).Where(device => {
            if (device.Id == FleetPolicy.Id(target)) return true;
            try { return EndpointIdentity.Resolve(device.Target, active).EndpointId == target.EndpointId; }
            catch (InvalidOperationException) { return false; }
        }).ToArray();
        if (matches.Length > 1) throw new IOException("Tracked endpoint identity is ambiguous; no device records were merged.");
        return matches.SingleOrDefault();
    }
    internal FleetInventory Observe(EndpointIdentity target, bool processingConfirmed)
    {
        var inventory = repository.Load();
        var device = FindDevice(inventory, target, [target]);
        if (device == null || !device.Desired || !device.Target.FxPath.Equals(target.FxPath, StringComparison.OrdinalIgnoreCase)) return inventory;
        // This API consumes a caller's actual processing observation. It never
        // starts a stream, requests meters, or infers activity from registry state.
        if (processingConfirmed && device.State == FleetEndpointState.PendingActivation && SharedReady(inventory) && endpoints.Healthy(target)) {
            device.State = FleetEndpointState.Healthy; device.Error = null; Save(inventory);
        }
        return inventory;
    }
}

internal sealed class NativeFleetPlatform : IFleetPlatform
{
    public EndpointIdentity[] Endpoints() => Integration.Endpoints();
    public RawValue? Value(string path, string name) => name.StartsWith("{91795F52-2DC0-4E20-A732-58816672E635},", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(@"\User", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\Volatile", StringComparison.OrdinalIgnoreCase))
        ? RawRegistry.ValueBounded(path, name, 12) : RawRegistry.Value(path, name);
    public ReplicaProof OwnedReplica(EndpointIdentity target, string path, string name) => FleetReplica.ReadExpected(target, path, name);
    public void SetControl(EndpointIdentity target, uint property, float value)
    {
        var expected = FleetReplica.ReadExpected(target, target.FxPath + "\\" + Contract.Context + @"\User", FleetReplica.Name((int)property));
        if (!expected.Matches(FleetReplica.Serialized((int)property, value))) throw new IOException("Common profile changed before replica notification; retry its current revision.");
        Integration.Set(target.EndpointId, property, value);
    }
    public KeyImage Key(string path) { RawRegistry.Privilege("SeSecurityPrivilege"); return RawRegistry.KeyOnly(path); }
    public bool CanWrite(string path) => RawRegistry.CanWrite(path);
    public void Write(Edit edit, EndpointReceipt receipt, bool advanced, Action save, bool existingOnly = false)
    {
        FleetStore.Validate(receipt);
        var snapshots = existingOnly ? new List<KeyImage> { Key(edit.Path) } : receipt.Keys;
        RawRegistry.Write(edit, snapshots, advanced, receipt.AdvancedKeys, receipt.PendingSecurityRestore, receipt.PendingSecurityExpected, receipt.PendingSecurityOriginal, save, existingOnly);
    }
    public void RestoreSecurity(EndpointReceipt receipt, Action save, Func<string, string, bool>? resolveConflict = null)
    {
        foreach (string path in receipt.PendingSecurityRestore.ToArray()) {
            RawRegistry.RestoreSecurity(new() { Path = path, Exists = true, SecurityMask = 15, Security = receipt.PendingSecurityOriginal[path] }, receipt.PendingSecurityExpected[path],
                resolveConflict == null ? null : () => resolveConflict(path, "Interrupted endpoint ACL differs from its recorded temporary/original permissions. Restore the recorded owner and ACL?"), existingOnly: true);
            receipt.PendingSecurityRestore.Remove(path); receipt.PendingSecurityExpected.Remove(path); receipt.PendingSecurityOriginal.Remove(path); save();
        }
    }
    public bool SharedHealthy()
    {
        string dll = Transaction.SafePath(Contract.InstallRoot, "APO/DotMic.ApoGate.dll");
        if (!RawValue.Text("", dll).Same(Value(Contract.ComPath + @"\InprocServer32", ""))
            || !RawValue.Text("ThreadingModel", "Both").Same(Value(Contract.ComPath + @"\InprocServer32", "ThreadingModel"))
            || Value(Contract.AudioPath, "DisableProtectedAudioDG")?.Same(RawValue.Dword("DisableProtectedAudioDG", 1)) != true) return false;
        string? files = Value(Contract.ConfigPath, "RequiredFiles")?.Display;
        if (files == null) return false;
        var required = System.Text.Json.JsonSerializer.Deserialize<PayloadFile[]>(files, Contract.Json);
        if (required == null || required.Length == 0 || required.Length > 256) return false;
        foreach (var file in required) {
            if (!file.Path.StartsWith("APO/", StringComparison.Ordinal)) return false;
            string path = Transaction.SafePath(Contract.InstallRoot, file.Path);
            if (!File.Exists(path)) return false;
            SecureStorage.Validate(path, false);
            if (Contract.FileHash(path) != file.Hash) return false;
        }
        var inventory = FleetStore.Load();
        string? receiptPath = inventory.SharedRepairs.LastOrDefault() ?? inventory.SharedReceipt;
        if (receiptPath == null) return false;
        var receipt = Transaction.Read(receiptPath);
        var pinned = PayloadPolicy.ResolveLock(receipt.Files.Single(f => f.Relative == "APO/DotMic.ApoGate.dll").Hash);
        if (pinned.Length != required.Length || !pinned.All(file => required.Any(entry => entry.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && entry.Hash == file.Hash))) return false;
        SecureStorage.Validate(Contract.InstallRoot, true); SecureStorage.Validate(Path.Combine(Contract.InstallRoot, "APO"), true);
        // Only load registration metadata from the independently pinned, protected
        // installed DLL after every locked dependency's hash has been checked.
        var expected = Integration.RegistrationPlan(dll);
        var actual = new[] { Contract.ApoPath, Contract.ApoClassesPath }.Select(root => {
            var key = Key(root); if (key.Exists) key.Values = expected.Select(value => Value(root, value.Name)).OfType<RawValue>().ToList(); return key;
        });
        return FleetPolicy.RegistrationHealthy(expected, actual);
    }
    public Transaction PrepareShared(string package) => Transaction.PrepareShared(package);
    public string ComposeSharedCleanup(IReadOnlyList<string> sources) => Transaction.ComposeSharedCleanup(sources).ReceiptPath;
    public bool SharedCommitted(string path) { var receipt = Transaction.Read(path); return receipt.Scope == "Shared" && receipt.Status == "Committed"; }
    public Receipt ReadShared(string path) => Transaction.Read(path);
    public async Task ApplyShared(Transaction transaction, FleetConsent consent)
    {
        RequireApprovedDependents(transaction.Receipt, consent, applying: true);
        transaction.AdvancedPermissionApproved = consent.AdvancedSharedPermission;
        transaction.Receipt.AudioRestartConsent = consent.AudioRestart;
        transaction.Receipt.ProtectedAudioConsent = consent.ProtectedAudio;
        transaction.Receipt.DependentServiceConsent = consent.DependentServices;
        transaction.Receipt.ReplacementConsent = consent.SharedChanges;
        transaction.Save(); await transaction.Apply().ConfigureAwait(false);
    }
    public async Task RemoveShared(string path, FleetConsent consent, bool keepProtectedAudio)
    {
        var tx = Transaction.Existing(path, consent.AdvancedSharedPermission);
        if (tx.Receipt.Scope != "Shared") throw new IOException("Legacy whole-device rollback is not fleet cleanup authority.");
        tx.Receipt.AudioRestartConsent = consent.AudioRestart; tx.Receipt.DependentServiceConsent = consent.DependentServices;
        tx.Receipt.ProtectedAudioConsent = consent.ProtectedAudio;
        tx.Receipt.SharedKeepProtectedAudio = keepProtectedAudio;
        RequireApprovedDependents(tx.Receipt, consent);
        tx.Save(); await tx.Rollback(false, (_, _) => false, keepProtectedAudio).ConfigureAwait(false);
        if (tx.Receipt.Status != "RolledBack") throw new IOException("Shared audio restoration completed with pending cleanup: " + tx.Receipt.Status + " / " + tx.ReceiptPath);
        if (tx.Receipt.Operation == "SharedCleanupComposition" && !tx.Receipt.CompensationVerified) throw new IOException("Composed shared cleanup baseline is not fully restored; keep its journal active.");
    }
    public async Task RecoverShared(string path, FleetConsent consent, Func<string, string, bool> resolveConflict, bool compensation)
    {
        var tx = Transaction.Existing(path, consent.AdvancedSharedPermission);
        if (tx.Receipt.Scope != "Shared") throw new IOException("Recovery cannot restore a legacy whole-device receipt.");
        if (compensation && FleetStore.Load().HasUnresolvedBindings && !Transaction.CanCompensateShared(tx.Receipt, path, FleetStore.Load()))
            throw new IOException("Shared compensation is not the active owned repair/predecessor pair.");
        if (!consent.SharedChanges || !consent.AudioRestart || !consent.ProtectedAudio || !consent.DependentServices) throw new OperationCanceledException("Shared recovery requires explicit consent.");
        RequireApprovedDependents(tx.Receipt, consent);
        tx.Receipt.AudioRestartConsent = consent.AudioRestart; tx.Receipt.DependentServiceConsent = consent.DependentServices;
        tx.Receipt.ProtectedAudioConsent = consent.ProtectedAudio; tx.Save();
        await tx.RecoverShared(resolveConflict, compensation, keepProtectedAudio: tx.Receipt.SharedKeepProtectedAudio).ConfigureAwait(false);
        if (!tx.Receipt.CompensationVerified) throw new IOException("Shared recovery has retained conflicts or pending cleanup; do not retire its journal.");
    }
    private static void RequireApprovedDependents(Receipt receipt, FleetConsent consent, bool applying = false)
    {
        // New removal scopes are authorized by the supplied preview, never by a
        // fresh service enumeration silently treated as approval.
        receipt.AudioDependents = FleetPolicy.ApproveDependents(receipt, consent, AudioService.Dependents(), applying);
    }
    public string? LegacyOrigin() => Value(Contract.ConfigPath, "OriginReceipt")?.Display;
    public string? LegacyLatest() => Value(Contract.ConfigPath, "LatestReceipt")?.Display;
    public Receipt ReadLegacy(string path) => Transaction.Read(path);
    public string ImportShared(string path) => Transaction.ImportSharedOrigin(path).ReceiptPath;

}
