namespace DotMic.Setup;

internal static class ProfilePropagation
{
    // The caller holds setup-operation.lock; the facade takes only the fleet
    // lock for this call and releases it before later Observe/Reconcile calls.
    internal static bool Run(FleetInventory inventory) => inventory.AutomaticEnrollment
        && FleetCoordinator.ReplicateProfiles(CommonProfile.Read()).GetAwaiter().GetResult();
}
