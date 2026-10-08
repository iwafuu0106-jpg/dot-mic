namespace DotMic.Setup;

internal sealed class EndpointTransaction(IFleetPlatform platform, IFleetRepository repository)
{
    internal EndpointReceipt Prepare(EndpointIdentity target)
    {
        if (!FleetPolicy.IsEndpointPath(target.FxPath)) throw new IOException("Invalid capture endpoint location.");
        var receipt = new EndpointReceipt { Target = target };
        void Add(string path, RawValue? after, string? delete = null, bool onlyMissing = false)
        {
            string name = after?.Name ?? delete!;
            var before = platform.Value(path, name);
            if (onlyMissing && before != null || (after == null ? before == null : after.Same(before))) return;
            receipt.Edits.Add(new() { Path = path, Name = name, Before = before, After = after });
            string p = path;
            while (FleetPolicy.Within(p, target.FxPath)) {
                if (!receipt.Keys.Any(k => k.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) receipt.Keys.Add(platform.Key(p));
                if (p.Equals(target.FxPath, StringComparison.OrdinalIgnoreCase)) break;
                p = p[..p.LastIndexOf('\\')];
            }
        }
        Add(target.FxPath, RawValue.Text(Contract.FxFormat + ",0", Contract.Microphone));
        Add(target.FxPath, RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid));
        Add(target.FxPath, RawValue.Multi(Contract.ModeFormat + ",6", Contract.DefaultMode));
        Add(target.FxPath, null, Contract.Clsid + ",100");
        string context = target.FxPath + "\\" + Contract.Context;
        Add(context, RawValue.Text(Contract.FxFormat + ",0", Contract.Microphone), onlyMissing: true);
        string[] defaults = ["1", "0", "0", "-48", "5", "160", "120", "0", "6"];
        for (int id = 1; id <= defaults.Length; id++) {
            string name = "{91795F52-2DC0-4E20-A732-58816672E635}," + id;
            Add(context + @"\Default", id is 1 or 3 or 8 ? RawValue.Dword(name, uint.Parse(defaults[id - 1])) : RawValue.Text(name, defaults[id - 1]), onlyMissing: true);
        }
        foreach (string store in new[] { "User", "Volatile" }) {
            string path = context + "\\" + store;
            var original = platform.Key(path);
            original.Values = Enumerable.Range(1, 9).Select(id => platform.Value(path, FleetReplica.Name(id))).OfType<RawValue>().ToList();
            if (!receipt.Keys.Any(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) receipt.Keys.Add(original);
            if (original.Exists) continue;
            const string marker = "__DotMicFleetTransient";
            if (platform.Value(path, marker) != null) throw new IOException("Unexpected fleet setup marker.");
            Add(path, RawValue.Dword(marker, 1));
            receipt.Edits.Add(new() { Path = path, Name = marker, Before = RawValue.Dword(marker, 1), After = null });
        }
        // User/Volatile values and CommonSettings belong to the controller. Never
        // reset them during repair, arrival, or graph activation.
        return receipt;
    }
    internal bool Healthy(EndpointIdentity target)
    {
        string context = target.FxPath + "\\" + Contract.Context;
        return platform.Value(target.FxPath, Contract.FxFormat + ",6")?.Same(RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid)) == true
            && platform.Value(target.FxPath, Contract.ModeFormat + ",6")?.Same(RawValue.Multi(Contract.ModeFormat + ",6", Contract.DefaultMode)) == true
            && platform.Value(target.FxPath, Contract.Clsid + ",100") == null
            && platform.Value(context, Contract.FxFormat + ",0")?.Same(RawValue.Text(Contract.FxFormat + ",0", Contract.Microphone)) == true
            && Enumerable.Range(1, 9).All(id => platform.Value(context + @"\Default", "{91795F52-2DC0-4E20-A732-58816672E635}," + id) != null);
    }
    internal string? Blocked(EndpointReceipt receipt, bool replace, bool advanced)
    {
        if (!platform.Key(receipt.Target.FxPath).Exists) return "Endpoint FxProperties key is unavailable; no endpoint key will be fabricated.";
        if (!replace && receipt.Edits.Any(e => e.Path.Equals(receipt.Target.FxPath, StringComparison.OrdinalIgnoreCase)
            && e.Before != null && e.Name != Contract.Clsid + ",100" && !(e.After?.Same(e.Before) ?? false))) return "Existing endpoint effect values require explicit replacement consent.";
        if (!advanced && receipt.Edits.Any(e => !platform.CanWrite(e.Path))) return "Endpoint permissions require explicit consent.";
        var disabled = platform.Value(receipt.Target.FxPath, "{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},5");
        if (disabled != null && !disabled.Same(RawValue.Dword(disabled.Name, 0))) return "Windows audio enhancements are disabled or unknown; enable them explicitly.";
        return null;
    }
    internal void Apply(EndpointReceipt receipt, bool advanced)
    {
        void Save() => repository.SaveEndpoint(receipt);
        foreach (var edit in receipt.Edits.GroupBy(e => e.Path + "\0" + e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
            var now = platform.Value(edit.Path, edit.Name);
            if (edit.Before == null ? now != null : !edit.Before.Same(now)) throw new IOException("Endpoint plan changed before apply: " + edit.Name);
        }
        if (advanced) receipt.UndoPermissionKeys = receipt.Edits.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        receipt.State = FleetEndpointState.Applying; Save();
        try {
            foreach (var edit in receipt.Edits) {
                edit.Applied = true; Save(); // Intent precedes mutation, including interrupted permissions.
                platform.Write(edit, receipt, advanced, Save);
            }
            receipt.State = FleetEndpointState.PendingActivation; receipt.Error = null; Save();
        } catch (Exception error) {
            receipt.Error = error.Message;
            try { Restore(receipt); }
            catch (Exception recovery) { receipt.State = FleetEndpointState.RecoveryRequired; receipt.Error += " / " + recovery.Message; Save(); }
            throw;
        }
    }
    internal static EndpointReceipt Compose(IReadOnlyList<EndpointReceipt> receipts, IReadOnlyList<string> paths)
    {
        if (receipts.Count == 0 || receipts.Count != paths.Count || receipts.Any(r => !r.Target.FxPath.Equals(receipts[0].Target.FxPath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Endpoint restoration chain is outside one exact FxProperties scope.");
        var merged = new EndpointReceipt { Target = receipts[^1].Target, Operation = "RestoreComposition", SourceReceipts = paths.ToList() };
        var active = receipts.Where(r => r.State != FleetEndpointState.Removed).ToArray();
        foreach (var group in active.SelectMany(r => r.Edits).Where(e => e.Applied).GroupBy(e => e.Path + "\0" + e.Name, StringComparer.OrdinalIgnoreCase)) {
            var first = group.First(); var last = group.Last();
            // Repair is an explicitly journaled ownership reacquisition. Its
            // observed gap does not replace the earliest uninstall baseline.
            merged.Edits.Add(new() { Path = first.Path, Name = first.Name, Before = first.Before, After = last.After, Applied = true });
        }
        foreach (var key in active.SelectMany(r => r.Keys).GroupBy(k => k.Path, StringComparer.OrdinalIgnoreCase)) merged.Keys.Add(key.First());
        foreach (string store in new[] { "User", "Volatile" }) for (int id = 1; id <= 9; id++) {
            string path = merged.Target.FxPath + "\\" + Contract.Context + "\\" + store, name = FleetReplica.Name(id);
            if (merged.Edits.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && e.Name == name)) continue;
            var baseline = merged.Keys.FirstOrDefault(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Find(name);
            merged.Edits.Add(new() { Path = path, Name = name, Before = baseline, After = baseline, Applied = true });
        }
        merged.ReplicaPrevious = active.SelectMany(r => r.ReplicaPrevious).ToList();
        merged.AdvancedKeys = active.SelectMany(r => r.AdvancedKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        merged.UndoPermissionKeys = active.SelectMany(r => r.UndoPermissionKeys).Where(path => merged.Edits.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var approval in active.SelectMany(r => r.UndoSecurity)) if (merged.UndoPermissionKeys.Contains(approval.Key, StringComparer.OrdinalIgnoreCase)) merged.UndoSecurity[approval.Key] = approval.Value;
        return merged;
    }
    internal void RecoverSecurity(EndpointReceipt receipt, Func<string, string, bool>? resolveConflict = null) =>
        platform.RestoreSecurity(receipt, () => repository.SaveEndpoint(receipt), resolveConflict);
    internal bool Restore(EndpointReceipt receipt, bool approveNewPermissions = false, Func<string, string, bool>? resolveConflict = null)
    {
        void Save() => repository.SaveEndpoint(receipt);
        receipt.State = FleetEndpointState.RemovalPending; Save();
        // No enumeration, remapping, service transition, capture, or key creation.
        platform.RestoreSecurity(receipt, Save, resolveConflict);
        bool complete = true;
        foreach (var edit in receipt.Edits.Where(e => e.Applied).Reverse()) {
            var key = platform.Key(edit.Path);
            if (!key.Exists) {
                // A vanished key has no remaining binding. Its historical values are
                // retained in the journal; never recreate a disconnected endpoint.
                edit.Applied = false; Save(); continue;
            }
            var now = platform.Value(edit.Path, edit.Name);
            if (edit.Before == null ? now == null : edit.Before.Same(now)) { edit.Applied = false; Save(); continue; }
            if (edit.After == null ? now != null : !edit.After.Same(now)) {
                bool ownedControl = FleetReplica.IsControl(receipt.Target, edit.Path, edit.Name)
                    && (receipt.ReplicaPrevious.Any(e => e.Path.Equals(edit.Path, StringComparison.OrdinalIgnoreCase) && e.Name == edit.Name && (e.After == null ? now == null : e.After.Same(now)))
                        || platform.OwnedReplica(receipt.Target, edit.Path, edit.Name).Matches(now));
                if (!ownedControl && !(FleetReplica.IsControl(receipt.Target, edit.Path, edit.Name)
                    && resolveConflict?.Invoke(edit.Path, "Common parameter differs from its owned journal/source. Restore its recorded pre-install value? " + edit.Name) == true)) {
                    complete = false; receipt.Error = "Conflicting endpoint value retained: " + edit.Path + " / " + edit.Name; Save(); continue;
                }
            }
            try {
                if (approveNewPermissions) {
                    if (!receipt.UndoPermissionKeys.Contains(edit.Path, StringComparer.OrdinalIgnoreCase)) receipt.UndoPermissionKeys.Add(edit.Path);
                    receipt.UndoSecurity[edit.Path] = key.Security; Save();
                }
                bool previouslyApproved = receipt.AdvancedKeys.Contains(edit.Path, StringComparer.OrdinalIgnoreCase) || receipt.UndoPermissionKeys.Contains(edit.Path, StringComparer.OrdinalIgnoreCase);
                if (!approveNewPermissions && previouslyApproved && !platform.CanWrite(edit.Path)) {
                    var original = receipt.Keys.FirstOrDefault(k => k.Path.Equals(edit.Path, StringComparison.OrdinalIgnoreCase));
                    byte[]? approvedSecurity = receipt.UndoSecurity.GetValueOrDefault(edit.Path) ?? (original?.Exists == true ? original.Security : null);
                    if (original == null || approvedSecurity?.Length > 0 && !SecurityDescriptorPolicy.SamePermissions(approvedSecurity, key.Security))
                        throw new IOException("Undo permission scope changed; explicit approval is required for " + edit.Path);
                }
                platform.Write(new() { Path = edit.Path, Name = edit.Name, Before = now, After = edit.Before }, receipt,
                    approveNewPermissions || previouslyApproved, Save, existingOnly: true);
                edit.Applied = false; Save();
            } catch (Exception error) { complete = false; receipt.Error = error.Message; Save(); }
        }
        receipt.State = complete ? FleetEndpointState.Removed : FleetEndpointState.RemovalPending;
        if (complete) receipt.Error = null;
        Save(); return complete;
    }
}
