namespace DotMic.Setup;

// Substitutes for independently owned integration. Render fixtures inject actions and must never invoke these stubs.
internal static class InstallPaths { internal static string Default => "C:\\fake-install"; }
internal static class FleetSetup { internal static MultiSetupActions Actions(Func<string, string, bool>? decision = null) => throw new InvalidOperationException("Compile-only fake adapter."); }
internal sealed class LegacyRecoveryForm : Form { internal LegacyRecoveryForm(string[] args) => throw new InvalidOperationException("Legacy recovery is not executed by fake UI fixtures."); }
