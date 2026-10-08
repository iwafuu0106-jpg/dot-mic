namespace DotMic.Setup;

internal readonly record struct ReplicaProof(bool Known, RawValue? Expected)
{
    internal bool Matches(RawValue? actual) => Known && (Expected == null ? actual == null : Expected.Same(actual));
}

internal static class FleetReplica
{
    private const string PropertyPrefix = "{91795F52-2DC0-4E20-A732-58816672E635},";
    internal static string Name(int id) => PropertyPrefix + id;
    internal static bool IsControl(EndpointIdentity target, string path, string name) =>
        (path.Equals(target.FxPath + "\\" + Contract.Context + @"\User", StringComparison.OrdinalIgnoreCase)
         || path.Equals(target.FxPath + "\\" + Contract.Context + @"\Volatile", StringComparison.OrdinalIgnoreCase))
        && name.StartsWith(PropertyPrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name[PropertyPrefix.Length..], out int id) && id is >= 1 and <= 9;
    internal static bool Valid(int id, float value) => id switch {
        1 or 3 or 8 => value is 0 or 1,
        2 => float.IsFinite(value) && value is >= -12 and <= 36,
        4 => float.IsFinite(value) && value is >= -80 and <= -10,
        5 => float.IsFinite(value) && value is >= 1 and <= 30,
        6 => float.IsFinite(value) && value is >= 50 and <= 500,
        7 => float.IsFinite(value) && value is >= 30 and <= 500,
        9 => float.IsFinite(value) && value is >= 2 and <= 12,
        _ => false
    };
    internal static string Revision(float[] values) => Contract.Hash(values.SelectMany(BitConverter.GetBytes).ToArray());
    internal static RawValue Serialized(int id, float value)
    {
        if (!Valid(id, value)) throw new IOException("Common parameter exceeds its bounded numeric contract.");
        byte[] data = new byte[12];
        BitConverter.GetBytes(4u).CopyTo(data, 0); BitConverter.GetBytes(1u).CopyTo(data, 4); BitConverter.GetBytes(value).CopyTo(data, 8);
        return new(Name(id), 3, data);
    }
    internal static ReplicaProof Prove(int id, bool volatileStore, RawValue? user, RawValue? transient)
    {
        bool ValidSource(RawValue? source) => source?.Type == 3 && source.Data.Length == 4 && Valid(id, BitConverter.ToSingle(source.Data));
        // A committed User value authorizes its matching endpoint User replica
        // and an absent Volatile override. Missing/corrupt User is not authority.
        if (!ValidSource(user)) return default;
        if (!volatileStore) return new(true, Serialized(id, BitConverter.ToSingle(user!.Data)));
        if (transient == null) return new(true, null);
        return ValidSource(transient) ? new(true, Serialized(id, BitConverter.ToSingle(transient.Data))) : default;
    }
    internal static ReplicaProof ReadExpected(EndpointIdentity target, string path, string name)
    {
        if (!IsControl(target, path, name)) return default;
        int id = int.Parse(name[PropertyPrefix.Length..]);
        var user = RawRegistry.ValueBounded(Contract.ConfigPath + @"\CommonSettings\User", id.ToString(), 4);
        bool volatileStore = path.EndsWith(@"\Volatile", StringComparison.OrdinalIgnoreCase);
        var transient = volatileStore ? RawRegistry.ValueBounded(Contract.ConfigPath + @"\CommonSettings\Volatile", id.ToString(), 4) : null;
        return Prove(id, volatileStore, user, transient);
    }
}
