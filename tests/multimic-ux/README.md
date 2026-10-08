# Multi-microphone UI fixtures

From the repository root:

```powershell
dotnet run --project tests/multimic-ux/DotMic.MultiMic.Ux.Tests.csproj -c Release
dotnet run --project tests/multimic-ux/render/DotMic.MultiMic.Render.Tests.csproj -c Release -- artifacts/multimic-ux-fixtures
```

The render project links the actual `SetupForm`, presentation and STA worker. It injects fake delegates; production Program, PackageSource, FleetSetup, registry, service registration, native bridges, audio and shortcut creation are not linked or invoked. The legacy entry is tested for accessibility only; its test substitute throws if invoked. Forms use an offscreen, no-activation handle, a bounded STA continuation/message pump, and `DrawToBitmap`; no production `Application.Run` is used.

PNG matrix: widths 480/424 at simulated control/font scaling 100/150/200%. States cover ready with 0/3 microphones, conditional replacement/Protected Audio/permission/interruption, unapproved/approved permission, applying, complete, partial 2+1, recovery-required/options, preparation error, post-apply recovery, repair and uninstall. Additional captures show the fresh recovery plan (unapproved/approved/completed) and shared operation failures with zero microphone failures. Expanded options, primary bounds/enabled state, accessible names, keyboard traversal, scrolling, retained details and absence of technical data/selectors on the normal screen are asserted.

Recovery-required inspection, preview and result states all request a new `MultiSetupOperation.Recover` plan through injected actions. Earlier plans/permission consent are not reused. The recovery choice is absent from ordinary setup options. Shared/service failure text uses the optional `MultiSetupResult.OperationIssue`; microphone counts remain factual, and a fresh retry includes unresolved shared work. Only short user-facing text belongs in `OperationIssue`; technical data belongs in `Details`.

`fixture-results.json` records screenshots and fake interaction coverage. Inventory changes simulate hotplug/removal by supplying new DTOs; this does **not** test real device notifications, manager/service lifecycle, registry recovery, audio, unelevated process launch, actual per-monitor DPI switching, Windows high-contrast mode or accessibility tools.
