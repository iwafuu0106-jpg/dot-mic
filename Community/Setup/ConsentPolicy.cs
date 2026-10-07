namespace DotMic.Setup;

internal enum Decision { Continue, Cancel, TransactionFailure }
internal static class ConsentPolicy
{
    // An OEM effect or unusual ACL is not an incompatibility classification.
    internal static Decision Decide(bool snapshotIntegrity, bool proceed, bool protectedAudioConsent, bool audioInterruptionConsent, bool needsTemporaryPermission, bool temporaryPermissionConsent)
    {
        if (!snapshotIntegrity) return Decision.TransactionFailure;
        if (!proceed || !protectedAudioConsent || !audioInterruptionConsent || (needsTemporaryPermission && !temporaryPermissionConsent)) return Decision.Cancel;
        return Decision.Continue;
    }
    internal static void Fixtures(string output)
    {
        static void Check(bool yes, string name) { if (!yes) throw new InvalidOperationException("Fixture failed: " + name); }
        var original = new RawValue("メーカーMFX", 999, [0, 255, 0, 128, 0]);
        var encoded = System.Text.Json.JsonSerializer.Serialize(original, Contract.Json);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<RawValue>(encoded, Contract.Json);
        Check(original.Same(decoded), "raw type/data preserved");
        Check(Decide(true, true, true, true, false, false) == Decision.Continue, "Existing APO Continue");
        Check(Decide(true, false, true, true, false, false) == Decision.Cancel, "Existing APO Cancel");
        Check(Decide(true, true, true, true, true, true) == Decision.Continue, "Special permission explicit Continue");
        Check(Decide(true, true, true, true, true, false) == Decision.Cancel, "Special permission declined");
        Check(Decide(false, true, true, true, true, true) == Decision.TransactionFailure, "Snapshot failure distinct");
        var prior = new EndpointIdentity("old", "opaque-stable", "container", "same name", "ks", "oldFx");
        var now = prior with { EndpointId = "new", FxPath = "newFx" };
        Check(EndpointIdentity.Resolve(prior, [now]) == now, "Runtime endpoint regeneration accepted");
        Check(EndpointIdentity.Resolve(prior, [now with { PhysicalInterface = "new driver interface" }]).EndpointId == "new", "StableId and container survive a driver interface change");
        Check(EndpointIdentity.Resolve(prior with { StableId = "" }, [now]) == now, "Unique container plus physical fallback without StableId");
        bool ambiguous = false; try { EndpointIdentity.Resolve(prior, [now, now with { EndpointId = "another" }]); } catch (InvalidOperationException) { ambiguous = true; }
        Check(ambiguous, "Ambiguous target needs reselection");
        File.WriteAllText(output, "PASS minimal raw/identity/Existing-APO Continue-Cancel/Advanced-permission Continue-Cancel/transaction-integrity fixtures. No registry/audio/security writes. No audio evidence.");
    }
}
