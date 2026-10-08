using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using DotMic.Setup;

internal static class SecurityChecks
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--pure") { PureChecks(); return 0; }
        if (args.Length is >= 3 and <= 8 && args[0] == "--cleanup-owned-tests") {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
            try { foreach (string scope in args.Skip(2)) CleanupOwnedTest(scope);
                File.WriteAllText(Path.GetFullPath(args[1]), "PASS: named GUID fixtures removed; no audio/config changes."); return 0; }
            catch (Exception error) { File.WriteAllText(Path.GetFullPath(args[1]), error.ToString()); return 1; }
        }
        if (args.Length != 1 || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        try { return Run(args[0]); }
        catch (Exception error) { File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new { Result = "FAIL", Error = error.ToString() })); return 1; }
    }
    private static int Run(string output)
    {
        string name = "DOTMIC-AclChecks-" + Guid.NewGuid().ToString("D");
        string path = @"SOFTWARE\" + name;
        var results = new List<object>();
        var baseline = ObserveConfiguration();
        bool cleanup = false;
        Exception? failure = null;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parent = machine.OpenSubKey(@"SOFTWARE", writable: true) ?? throw new IOException("Existing test parent required; no parent is created or modified.");
        using var root = parent.CreateSubKey(name, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None);
        using var inheritedFixture = root.CreateSubKey("inherited", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None);
        using var protectedFixture = root.CreateSubKey("protected", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None);
        KeyImage? rootBaseline = null;
        var cleanupImages = new List<KeyImage>();
        try {
            RawRegistry.Privilege("SeSecurityPrivilege");
            RawRegistry.Privilege("SeTakeOwnershipPrivilege");
            RawRegistry.Privilege("SeRestorePrivilege");
            rootBaseline = RawRegistry.KeyOnly(path);
            cleanupImages.Add(RawRegistry.KeyOnly(path + "\\inherited"));
            cleanupImages.Add(RawRegistry.KeyOnly(path + "\\protected"));
            var fixtureParent = new RegistrySecurity();
            fixtureParent.SetAccessRuleProtection(true, false);
            fixtureParent.SetOwner(WindowsIdentity.GetCurrent().User!);
            fixtureParent.SetGroup(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            fixtureParent.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.FullControl, AccessControlType.Allow));
            fixtureParent.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            fixtureParent.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(fixtureParent);
            foreach (bool protect in new[] { false, true }) {
                string child = protect ? "protected" : "inherited";
                string scope = path + "\\" + child;
                var fixture = protect ? protectedFixture : inheritedFixture;
                fixture.SetValue("probe", "before", RegistryValueKind.String);
                var fixtureBaseline = RawRegistry.KeyOnly(scope);
                var restricted = new RegistrySecurity();
                restricted.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
                restricted.SetGroup(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                restricted.SetAccessRuleProtection(protect, false);
                restricted.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, AccessControlType.Allow));
                restricted.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, RegistryRights.ReadKey, AccessControlType.Allow));
                restricted.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.ReadKey, AccessControlType.Allow));
                try {
                    fixture.SetAccessControl(restricted);
                    var image = RawRegistry.KeyOnly(scope);
                    bool writeAllowedBefore = RawRegistry.CanWrite(scope);
                    if (writeAllowedBefore) throw new IOException("Every fixture must require temporary permission.");
                    var advancedKeys = new List<string>(); var pending = new List<string>();
                    var expected = new Dictionary<string, byte[]>(); var before = new Dictionary<string, byte[]>();
                    Exception? writeFailure = null, retryFailure = null;
                    try { RawRegistry.Write(new() { Path = scope, Name = "probe", Before = RawValue.Text("probe", "before"), After = RawValue.Text("probe", "after") },
                        [image], true, advancedKeys, pending, expected, before, () => { }); }
                    catch (Exception error) { writeFailure = error; }
                    var after = RawRegistry.KeyOnly(scope);
                    bool readbackMatches = new RawSecurityDescriptor(image.Security, 0).GetSddlForm(AccessControlSections.All)
                        == new RawSecurityDescriptor(after.Security, 0).GetSddlForm(AccessControlSections.All);
                    try { RawRegistry.RestoreSecurity(image); } catch (Exception error) { retryFailure = error; }
                    RawRegistry.Write(new() { Path = scope, Name = "probe", Before = RawValue.Text("probe", "after"), After = RawValue.Text("probe", "after-again") },
                        [image], true, advancedKeys, pending, expected, before, () => { });
                    var ownerOnly = new RegistrySecurity(); ownerOnly.SetOwner(WindowsIdentity.GetCurrent().User!);
                    fixture.SetAccessControl(ownerOnly);
                    var temporary = new RawSecurityDescriptor(image.Security, 0); temporary.Owner = WindowsIdentity.GetCurrent().User;
                    temporary.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2001b, WindowsIdentity.GetCurrent().User!, false, null));
                    if (!RawRegistry.SecurityRecognized(image, Bytes(temporary))) throw new IOException("Interrupted ownership-only state not recognized.");
                    RawRegistry.RestoreSecurity(image); // Incomplete ownership-only recovery, not just a no-op retry.
                    RawRegistry.RestoreSecurity(new() { Path = image.Path, Exists = true, SecurityMask = 15, Security = Bytes(temporary) });
                    if (!RawRegistry.SecurityRecognized(image, Bytes(temporary))) throw new IOException("Full temporary permission state not recognized.");
                    RawRegistry.RestoreSecurity(image, Bytes(temporary));
                    var externallyChanged = new RawSecurityDescriptor(image.Security, 0);
                    externallyChanged.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)RegistryRights.ReadKey, new(WellKnownSidType.WorldSid, null), false, null));
                    RawRegistry.RestoreSecurity(new() { Path = image.Path, Exists = true, SecurityMask = 15, Security = Bytes(externallyChanged) });
                    if (RawRegistry.SecurityRecognized(image, Bytes(temporary))) throw new IOException("An unrelated permission grant was silently accepted.");
                    bool conflictRejected = false;
                    try { RawRegistry.RestoreSecurity(image, Bytes(temporary)); } catch (IOException) { conflictRejected = true; }
                    if (!conflictRejected) throw new IOException("Guarded restore accepted an unrelated permission grant.");
                    bool changedDuringDecisionRejected = false;
                    try { RawRegistry.RestoreSecurity(image, Bytes(temporary), () => {
                        var changed = new RawSecurityDescriptor(Bytes(externallyChanged), 0);
                        changed.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)RegistryRights.ReadKey, new(WellKnownSidType.BuiltinUsersSid, null), false, null));
                        RawRegistry.RestoreSecurity(new() { Path = image.Path, Exists = true, SecurityMask = 15, Security = Bytes(changed) });
                        return true;
                    }); } catch (IOException) { changedDuringDecisionRejected = true; }
                    if (!changedDuringDecisionRejected) throw new IOException("ACL change during conflict decision was overwritten.");
                    RawRegistry.RestoreSecurity(image);
                    if (!advancedKeys.SequenceEqual([scope])) throw new IOException("Temporary permission path was not exercised.");
                    results.Add(new { Case = child, WriteError = writeFailure?.ToString(), RetryError = retryFailure?.ToString(),
                        OriginalFlags = new RawSecurityDescriptor(image.Security, 0).ControlFlags.ToString(),
                        ReadbackFlags = new RawSecurityDescriptor(after.Security, 0).ControlFlags.ToString(), ExactSddl = readbackMatches, Pending = pending.Count,
                        RepeatedWrite = "PASS", OwnershipOnlyRecovery = "PASS", FullTemporaryRecovery = "PASS", ExternalGrantRejected = true,
                        ChangedDuringDecisionRejected = true, WriteAllowedBefore = writeAllowedBefore, AdvancedKeys = advancedKeys.Count });
                    if (writeFailure != null || retryFailure != null || pending.Count != 0
                        || !SecurityDescriptorPolicy.SamePermissions(image.Security, RawRegistry.KeyOnly(scope).Security))
                        throw new IOException("Permission restoration regression: " + child, writeFailure ?? retryFailure);
                } finally { RawRegistry.RestoreSecurity(fixtureBaseline); }
            }
            string missing = path + "\\missing";
            bool missingRejected = false;
            try { RawRegistry.RestoreSecurity(new() { Path = missing, Exists = true, SecurityMask = 15, Security = RawRegistry.KeyOnly(path).Security }); }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 2) { missingRejected = true; }
            if (!missingRejected || RawRegistry.KeyOnly(missing).Exists) throw new IOException("Missing restoration key was recreated.");
            results.Add(new { Case = "missing", RejectedWithoutCreatingKey = true });
            if (!baseline.SequenceEqual(ObserveConfiguration())) throw new IOException("Observed existing configuration changed during fixture checks.");
        } catch (Exception error) { failure = error; }
        finally {
            // Only this run's explicitly named GUID key, never DOT MIC or its config parent.
            try { if (rootBaseline != null) RawRegistry.RestoreSecurity(rootBaseline);
                foreach (var image in cleanupImages) RawRegistry.RestoreSecurity(image);
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: true); cleanup = parent.OpenSubKey(name) == null; }
            catch (Exception error) { failure ??= error; }
            File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(new { Result = failure == null && cleanup ? "PASS" : "FAIL",
                Tests = results, Error = failure?.ToString(), Cleanup = cleanup, ExistingConfigurationUnchanged = baseline.SequenceEqual(ObserveConfiguration()),
                AudioOperations = false, TestKey = path }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure == null && cleanup ? 0 : 1;
    }
    private static byte[] ObserveConfiguration() => JsonSerializer.SerializeToUtf8Bytes(new[] { "StableId", "ApoPath", "OriginReceipt", "LatestReceipt", "ApplicationDir", "ApplicationReceipt", "ApplicationPackageDir" }
        .Select(name => RawRegistry.Value(Contract.ConfigPath, name)).ToArray());
    private static byte[] Bytes(RawSecurityDescriptor descriptor)
    {
        byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
    private static void PureChecks()
    {
        var original = new RawSecurityDescriptor("O:SYG:BAD:PAI(A;CI;KA;;;SY)(A;;KR;;;BA)S:AI(AU;SA;KR;;;WD)");
        byte[] expected = Bytes(original);
        var normalized = new RawSecurityDescriptor(expected, 0);
        normalized.SetFlags(normalized.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.SystemAclAutoInherited));
        if (!SecurityDescriptorPolicy.SamePermissions(expected, Bytes(normalized))) throw new IOException("Metadata-only normalization rejected.");
        void Reject(Action<RawSecurityDescriptor> mutate) {
            var actual = new RawSecurityDescriptor(expected, 0); mutate(actual);
            if (SecurityDescriptorPolicy.SamePermissions(expected, Bytes(actual))) throw new IOException("Real permission change accepted.");
        }
        Reject(sd => sd.Owner = new(WellKnownSidType.BuiltinAdministratorsSid, null));
        Reject(sd => sd.Group = new(WellKnownSidType.LocalSystemSid, null));
        Reject(sd => sd.SetFlags(sd.ControlFlags & ~ControlFlags.DiscretionaryAclProtected));
        Reject(sd => sd.SetFlags(sd.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired));
        Reject(sd => sd.SetFlags(sd.ControlFlags | ControlFlags.SystemAclAutoInheritRequired));
        Reject(sd => sd.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)RegistryRights.FullControl, new(WellKnownSidType.WorldSid, null), false, null)));
        Reject(sd => sd.DiscretionaryAcl![0].AceFlags |= AceFlags.Inherited);
        Reject(sd => { var ace = sd.DiscretionaryAcl![0]; sd.DiscretionaryAcl.RemoveAce(0); sd.DiscretionaryAcl.InsertAce(1, ace); });
        Reject(sd => sd.SystemAcl![0].AceFlags |= AceFlags.FailedAccess);
        Reject(sd => sd.DiscretionaryAcl = null);
        Console.WriteLine("PASS: only auto-inheritance metadata is normalized; owner/group, ACL protection, ACE flags/order/grants and audit changes remain significant. No registry or audio operations.");
    }
    private static void CleanupOwnedTest(string scope)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(scope, @"^SOFTWARE\\(?:DOTMIC-AclChecks-|DOT MIC\\AclChecks-)[0-9a-f-]{36}$")) throw new IOException("Not a dedicated GUID fixture.");
        RawRegistry.Privilege("SeSecurityPrivilege");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = machine.OpenSubKey(scope) ?? throw new IOException("Named fixture missing.");
        if (root.GetValueNames().Length != 0) throw new IOException("Fixture root contents changed; do not delete.");
        var images = new List<KeyImage>();
        foreach (string child in root.GetSubKeyNames()) {
            if (child is not ("inherited" or "protected")) throw new IOException("Unexpected data in fixture; do not delete.");
            using var key = root.OpenSubKey(child)!;
            if (key.GetSubKeyNames().Length != 0 || !key.GetValueNames().SequenceEqual(["probe"]) || key.GetValue("probe") is not ("before" or "after" or "after-again")) throw new IOException("Fixture contents changed; do not delete.");
            images.Add(RawRegistry.KeyOnly(scope + "\\" + child));
        }
        // Validate the complete fixture before any permission change or deletion.
        foreach (var image in images) {
            var descriptor = new RawSecurityDescriptor(image.Security, 0);
            descriptor.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)RegistryRights.FullControl, new(WellKnownSidType.BuiltinAdministratorsSid, null), false, null));
            RawRegistry.RestoreSecurity(new() { Path = image.Path, Exists = true, SecurityMask = 15, Security = Bytes(descriptor) });
        }
        int split = scope.LastIndexOf('\\');
        using var parent = machine.OpenSubKey(scope[..split], writable: true)!;
        parent.DeleteSubKeyTree(scope[(split + 1)..], throwOnMissingSubKey: true);
    }
}
