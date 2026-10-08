namespace DotMic;

internal sealed class PendingAudioWrites
{
    private readonly Dictionary<uint, float> preview = [], dirty = [];
    private int retries;
    internal bool HasPreview => preview.Count > 0;
    internal bool HasChanges => dirty.Count > 0;
    internal void Add(uint property, float value) { preview[property] = dirty[property] = value; retries = 0; }
    internal KeyValuePair<uint, float>[] Snapshot(bool commit) => (commit ? dirty : preview).ToArray();
    internal void Complete(IEnumerable<KeyValuePair<uint, float>> writes, bool commit)
    {
        foreach (var (id, value) in writes) {
            if (preview.TryGetValue(id, out var current) && current == value) preview.Remove(id);
            if (commit && dirty.TryGetValue(id, out current) && current == value) dirty.Remove(id);
        }
        if (commit) retries = 0;
    }
    // A failed preview is not resubmitted every 20 ms; the commit timer drains it.
    // Commit failures get three bounded retries. Explicit refresh can retry later.
    internal TimeSpan? RetryCommit()
    {
        if (!HasChanges || retries >= 3) return null;
        return TimeSpan.FromMilliseconds(500 * (1 << retries++));
    }
}
