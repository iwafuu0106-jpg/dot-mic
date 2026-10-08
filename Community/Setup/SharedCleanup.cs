using System.Text.Json;

namespace DotMic.Setup;

internal static class SharedCleanup
{
    internal static Receipt Compose(IReadOnlyList<Receipt> sources)
    {
        if (sources.Count == 0 || sources.Count > 512 || sources.Any(r => r.Scope != "Shared" || r.Target != null || r.Status != "Committed"
            || r.RegistrationPending || r.AudioRestartPending || r.PendingSecurityRestore.Count != 0
            || r.Files.Any(f => f.StageStarted || f.RestoreStageStarted) || r.ApplicationRemovalPending || r.Application?.CleanupPending == true
            || r.Application?.ShortcutStageStarted == true || r.Application?.ShortcutRestoreStageStarted == true
            || (r.Application?.Files.Any(f => f.StageStarted || f.RestoreStageStarted) ?? false)))
            throw new IOException("Shared source journals require scoped recovery before cleanup composition.");
        // Clone first: published/source receipts are immutable restoration evidence.
        var records = JsonSerializer.Deserialize<Receipt[]>(JsonSerializer.SerializeToUtf8Bytes(sources, Contract.Json), Contract.Json)!;
        var receipt = new Receipt { Scope = "Shared", Operation = "SharedCleanupComposition", Status = "CleanupPrepared",
            ProtectedAudioWasAlreadyOne = records[0].ProtectedAudioWasAlreadyOne, RegistrationExpected = records[^1].RegistrationExpected };
        foreach (var group in records.SelectMany(r => r.Edits).Where(e => e.Applied).GroupBy(e => e.Path + "\0" + e.Name, StringComparer.OrdinalIgnoreCase)) {
            var first = group.First(); var last = group.Last();
            receipt.Edits.Add(new() { Path = first.Path, Name = first.Name, Before = first.Before, After = last.After, Applied = true });
        }
        receipt.Keys = records.SelectMany(r => r.Keys).GroupBy(k => k.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        receipt.AdvancedKeys = records.SelectMany(r => r.AdvancedKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        receipt.Files = Files(records.SelectMany(r => r.Files));
        var applications = records.Select(r => r.Application).OfType<ApplicationDeployment>().ToArray();
        if (applications.Length > 0) {
            if (applications.Select(a => a.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw new IOException("Shared application roots differ; retain source journals for scoped recovery.");
            var firstShortcut = applications.FirstOrDefault(a => a.DesktopShortcut || a.ShortcutApplied || a.ShortcutBeforeHash != null);
            var lastShortcut = applications.LastOrDefault(a => a.DesktopShortcut || a.ShortcutApplied || a.ShortcutBeforeHash != null);
            receipt.Application = new() { Root = applications[0].Root, Files = Files(applications.SelectMany(a => a.Files)),
                ParentDirectories = applications.SelectMany(a => a.ParentDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DesktopShortcut = applications.Any(a => a.DesktopShortcut), ShortcutBeforeHash = firstShortcut?.ShortcutBeforeHash,
                ShortcutHash = lastShortcut?.ShortcutHash, ShortcutApplied = applications.Any(a => a.ShortcutApplied) };
        }
        return receipt;
    }
    private static List<FileEdit> Files(IEnumerable<FileEdit> files) => files.GroupBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(group => {
        var first = group.First(); var last = group.Last();
        return new FileEdit { Relative = first.Relative, Existed = first.Existed, BeforeHash = first.BeforeHash, Hash = last.Hash, Applied = group.Any(f => f.Applied) };
    }).ToList();

    internal static bool Matches(Edit edit, RawValue? actual) => edit.After == null ? actual == null : edit.After.Same(actual);
}
