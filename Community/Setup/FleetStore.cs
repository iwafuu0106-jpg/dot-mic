using System.Text.Json;

namespace DotMic.Setup;

internal static class FleetStore
{
    internal static readonly string Root = Path.Combine(Contract.RecoveryRoot, "Fleet");
    internal static FleetInventory Load() => new ProtectedFleetRepository().Load();
    internal static void Save(FleetInventory inventory) => new ProtectedFleetRepository().Save(inventory);
    internal static EndpointReceipt ReadEndpoint(string path) => new ProtectedFleetRepository().ReadEndpoint(path);
    private sealed record Envelope(string Sha256, byte[] Data);
    private const int Budget = 16 * 1024 * 1024;
    internal static byte[] Encode<T>(T record)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, Contract.Json);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Contract.Hash(data), data), Contract.Json);
        if (bytes.Length > Budget) throw new IOException("Fleet journal exceeds its bounded budget.");
        return bytes;
    }
    internal static T Decode<T>(byte[] bytes)
    {
        if (bytes.Length > Budget) throw new IOException("Fleet journal exceeds its bounded budget.");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Contract.Json) ?? throw new IOException("Fleet envelope is missing.");
        if (envelope.Data.Length > Budget || Contract.Hash(envelope.Data) != envelope.Sha256) throw new IOException("Fleet checksum mismatch.");
        return JsonSerializer.Deserialize<T>(envelope.Data, Contract.Json) ?? throw new IOException("Fleet journal is invalid.");
    }
    internal static string SafeFile(string relative)
    {
        if (relative != "inventory.json" && !System.Text.RegularExpressions.Regex.IsMatch(relative, @"^endpoint-[0-9a-f]{32}\.json$")) throw new IOException("Fleet journal path is outside its scope.");
        string path = Path.Combine(Root, relative); SecureStorage.NoRedirection(path); return path;
    }
    internal static T Read<T>(string path)
    {
        if (!Path.GetFullPath(path).Equals(SafeFile(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fleet journal escapes protected storage.");
        SecureStorage.RecoveryFile(path);
        if (new FileInfo(path).Length > Budget) throw new IOException("Fleet journal exceeds its bounded budget.");
        return Decode<T>(File.ReadAllBytes(path));
    }
    internal static void Write<T>(string path, T record)
    {
        if (!Path.GetFullPath(path).Equals(SafeFile(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fleet journal escapes protected storage.");
        SecureStorage.Directory(Root);
        if (File.Exists(path)) SecureStorage.RecoveryFile(path);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, Contract.Json);
        byte[] bytes = Encode(record);
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        SecureStorage.File(stage); File.Move(stage, path, true);
        if (!JsonSerializer.SerializeToUtf8Bytes(Read<T>(path), Contract.Json).AsSpan().SequenceEqual(data)) throw new IOException("Fleet journal read-back mismatch.");
    }
    internal static void Validate(EndpointReceipt receipt)
    {
        if (receipt.Schema != 1 || receipt.Version != Contract.Version || receipt.Scope != "Endpoint" || receipt.TransactionId == Guid.Empty
            || receipt.Target == null || !FleetPolicy.IsEndpointPath(receipt.Target.FxPath) || receipt.Edits.Count > 2048 || receipt.Keys.Count > 2048
            || receipt.SourceReceipts.Count > 512 || receipt.Operation is not ("Install" or "RestoreComposition" or "Replica")
            || !Enum.IsDefined(receipt.State)) throw new IOException("Endpoint journal contract is invalid.");
        foreach (string path in receipt.SourceReceipts) if (!Path.GetFullPath(path).Equals(SafeFile(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) == "inventory.json") throw new IOException("Endpoint source link is out of scope.");
        if (receipt.ReplicaPrevious.Count > 18 || receipt.ReplicaPrevious.Any(e => !FleetReplica.IsControl(receipt.Target, e.Path, e.Name))) throw new IOException("Replica history is outside its bounded scope.");
        foreach (var edit in receipt.Edits) {
            if (!FleetPolicy.Within(edit.Path, receipt.Target.FxPath) || edit.Name.Length > 16384
                || edit.Before?.Data.Length > 1024 * 1024 || edit.After?.Data.Length > 1024 * 1024) throw new IOException("Endpoint edit escapes its scope.");
        }
        foreach (var key in receipt.Keys) if (!FleetPolicy.Within(key.Path, receipt.Target.FxPath)) throw new IOException("Endpoint security escapes its scope.");
        foreach (var path in receipt.AdvancedKeys.Concat(receipt.UndoPermissionKeys).Concat(receipt.PendingSecurityRestore)) if (!FleetPolicy.Within(path, receipt.Target.FxPath)) throw new IOException("Endpoint ACL escapes its scope.");
        foreach (var path in receipt.UndoPermissionKeys) if (!receipt.Edits.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("Undo permission authority has no owned endpoint edit.");
        foreach (var pair in receipt.UndoSecurity) if (!receipt.UndoPermissionKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value.Length > 65536) throw new IOException("Undo security approval is out of scope.");
        foreach (var path in receipt.PendingSecurityRestore) if (!receipt.PendingSecurityOriginal.ContainsKey(path) || !receipt.PendingSecurityExpected.ContainsKey(path)
            || !receipt.AdvancedKeys.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new IOException("Endpoint ACL recovery record is incomplete.");
        foreach (var entry in receipt.PendingSecurityOriginal.Concat(receipt.PendingSecurityExpected))
            if (!FleetPolicy.Within(entry.Key, receipt.Target.FxPath) || entry.Value.Length > 65536) throw new IOException("Endpoint ACL descriptor escapes its bounded scope.");
        if (receipt.LegacyReceipt != null) ValidateLegacyLink(receipt.LegacyReceipt);
    }
    internal static void ValidateLegacyLink(string path)
    {
        string full = Path.GetFullPath(path);
        if (Path.GetFileName(full) != "snapshot.json" || !full.StartsWith(Path.GetFullPath(Contract.RecoveryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Legacy receipt link is out of scope.");
        SecureStorage.NoRedirection(full);
    }
    internal static void Validate(FleetInventory inventory)
    {
        if (inventory.Schema != 1 || inventory.Version != Contract.Version || inventory.Revision < 0 || inventory.Devices.Count > 512
            || inventory.Devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != inventory.Devices.Count) throw new IOException("Fleet inventory contract is invalid.");
        foreach (var device in inventory.Devices) {
            if (device.Target == null || !FleetPolicy.IsEndpointPath(device.Target.FxPath) && (device.Receipts.Count > 0 || device.State != FleetEndpointState.ActionRequired)
                || device.Id != (device.IdentityAmbiguous ? FleetPolicy.AmbiguousId(device.Target) : FleetPolicy.Id(device.Target))
                || device.Receipts.Count > 512 || !Enum.IsDefined(device.State)) throw new IOException("Fleet device contract is invalid.");
            foreach (var path in device.Receipts) if (!Path.GetFullPath(path).Equals(SafeFile(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path) == "inventory.json") throw new IOException("Fleet endpoint link is out of scope.");
        }
        if (inventory.SharedRepairs.Count > 512 || inventory.RetiredSharedReceipts.Count > 512) throw new IOException("Too many shared repair journals.");
        foreach (string path in inventory.SharedRepairs.Concat(inventory.RetiredSharedReceipts)) ValidateLegacyLink(path);
        if (inventory.SharedReceipt != null) ValidateLegacyLink(inventory.SharedReceipt);
        if (inventory.SharedCleanupReceipt != null) ValidateLegacyLink(inventory.SharedCleanupReceipt);
        if (inventory.LegacyOriginReceipt != null) ValidateLegacyLink(inventory.LegacyOriginReceipt);
        if (inventory.LegacyLatestReceipt != null) ValidateLegacyLink(inventory.LegacyLatestReceipt);
    }
}
internal sealed class ProtectedFleetRepository : IFleetRepository
{
    public FleetInventory Load()
    {
        string path = FleetStore.SafeFile("inventory.json");
        if (!File.Exists(path)) {
            if (Directory.Exists(FleetStore.Root) && Directory.EnumerateFiles(FleetStore.Root, "endpoint-*.json").Any())
                throw new IOException("Fleet inventory is missing but endpoint journals remain; recover ownership before enrollment.");
            return new();
        }
        var record = FleetStore.Read<FleetInventory>(path); FleetStore.Validate(record); return record;
    }
    public void Save(FleetInventory inventory) { FleetStore.Validate(inventory); FleetStore.Write(FleetStore.SafeFile("inventory.json"), inventory); }
    public string PathFor(EndpointReceipt receipt) => FleetStore.SafeFile("endpoint-" + receipt.TransactionId.ToString("N") + ".json");
    public EndpointReceipt ReadEndpoint(string path)
    {
        var receipt = FleetStore.Read<EndpointReceipt>(path); FleetStore.Validate(receipt);
        if (!PathFor(receipt).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Endpoint journal identity mismatch.");
        return receipt;
    }
    public void SaveEndpoint(EndpointReceipt receipt) { FleetStore.Validate(receipt); FleetStore.Write(PathFor(receipt), receipt); }
}
