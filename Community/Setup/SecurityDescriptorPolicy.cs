using System.Security.AccessControl;

namespace DotMic.Setup;

internal static class SecurityDescriptorPolicy
{
    internal static bool SamePermissions(byte[] expected, byte[] actual) => Normalize(expected) == Normalize(actual);

    private static string Normalize(byte[] bytes)
    {
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        // Windows can clear/recompute these bookkeeping flags on RegSetKeySecurity.
        // Do NOT ignore ACE order, inherited ACE flags, access masks, owner/group,
        // ACL presence or protection: those alter actual permissions or policy.
        const ControlFlags metadata = ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.SystemAclAutoInherited;
        descriptor.SetFlags(descriptor.ControlFlags & ~metadata);
        return descriptor.GetSddlForm(AccessControlSections.All);
    }
}
