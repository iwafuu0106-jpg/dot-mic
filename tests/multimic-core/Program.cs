using System.Text.Json;
using DotMic.Setup;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static EndpointIdentity Mic(int number, bool? busy = false) => new("runtime-" + number, "stable-" + number, "container-" + number,
    "Mic " + number, "interface-" + number,
    @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{11111111-1111-1111-1111-" + number.ToString("D12") + @"}\FxProperties",
    4, @"USB\VID_TEST\" + number, "", 1, busy);
static FleetConsent Consent() => new(true, true, true, true, true);
var a = Mic(1); var b = Mic(2);
Check(new EndpointIdentity("", "", "", "", "", "").FormFactor == -1, "six-argument legacy identity remains compatible");
Check(FleetPolicy.Eligible(a) && !FleetPolicy.Eligible(a with { FormFactor = 0 }) && !FleetPolicy.Eligible(a with { PnpId = @"ROOT\VIRTUAL" }), "physical capture policy");
var repo = new MemoryRepository(); var platform = new MemoryPlatform { Active = [a, b] }; var manager = new FleetManager(platform, repo);
var initial = manager.Prepare("fake-package");
Check(initial.EndpointPlans.Count == 2 && initial.SharedTransaction != null, "all eligible endpoints planned");
platform.FailEndpoint = b.FxPath;
var installed = await manager.Apply(initial, Consent());
Check(platform.SharedApplies == 1 && platform.SharedRemoves == 0, "shared apply once, never compensated for endpoint failure");
Check(installed.Devices.Single(d => d.Target.EndpointId == a.EndpointId).State == FleetEndpointState.PendingActivation, "registry verification is pending activation");
Check(platform.Value(a.FxPath, Contract.FxFormat + ",6")?.Same(RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid)) == true, "sibling survives rollback");
Check(platform.Value(b.FxPath, Contract.FxFormat + ",0") == null, "failed endpoint compensated only within its scope");
Check(platform.Writes.All(e => e.Path.StartsWith(a.FxPath, StringComparison.OrdinalIgnoreCase) || e.Path.StartsWith(b.FxPath, StringComparison.OrdinalIgnoreCase)), "no global edits in endpoint transactions");
string user = a.FxPath + "\\" + Contract.Context + @"\User";
var control = RawValue.Dword("custom-control", 42); platform.Put(user, control);
await manager.Reconcile("fake-package", true);
Check(platform.Value(user, control.Name)?.Same(control) == true && platform.SharedApplies == 1, "reconcile preserves controls and shared resources");
Check(manager.Prepare("fake-package", true).EndpointPlans.Count == 0, "healthy endpoints are not repaired repeatedly");
manager.Observe(a, true);
Check(repo.Load().Devices.Single(d => d.Target.EndpointId == a.EndpointId).State == FleetEndpointState.Healthy, "only explicit processing observation promotes healthy state");
var conflict = RawValue.Text(Contract.FxFormat + ",6", "third-party"); platform.Put(a.FxPath, conflict);
platform.Active = []; platform.ForbidEnumeration = true;
var removed = await manager.Remove(Consent(), false);
Check(removed.HasUnresolvedBindings && platform.SharedRemoves == 0 && platform.Value(a.FxPath, conflict.Name)?.Same(conflict) == true, "offline conflict retained, shared cleanup blocked");
platform.Put(a.FxPath, RawValue.Text(conflict.Name, Contract.Clsid));
removed = await manager.Remove(Consent(), false);
Check(!removed.HasUnresolvedBindings && platform.SharedRemoves == 1 && platform.ExistingOnlyWrites > 0, "offline exact-key detach permits shared cleanup");

var busyRepo = new MemoryRepository(); var busyPlatform = new MemoryPlatform { Active = [Mic(3, true), Mic(4, null)] };
var busyManager = new FleetManager(busyPlatform, busyRepo);
var busyResult = await busyManager.Apply(busyManager.Prepare("fake"), Consent());
Check(busyResult.Devices.All(d => d.State == FleetEndpointState.DeferredBusy) && busyPlatform.Writes.Count == 0, "busy and unknown activity defer without writes/capture");
var oemRepo = new MemoryRepository(); var oemPlatform = new MemoryPlatform { Active = [a] };
oemPlatform.Put(a.FxPath, RawValue.Text(Contract.FxFormat + ",6", "OEM"));
var oemManager = new FleetManager(oemPlatform, oemRepo);
var oem = await oemManager.Apply(oemManager.Prepare("fake"), Consent());
Check(oem.Devices.Single().State == FleetEndpointState.ActionRequired && oemPlatform.Writes.Count == 0, "OEM replacement requires explicit endpoint consent");
var approved = Consent() with { ReplaceEndpoints = [FleetPolicy.Id(a)] };
oem = await oemManager.Apply(oemManager.Prepare("fake", true), approved);
Check(oem.Devices.Single().State == FleetEndpointState.PendingActivation, "explicit scoped replacement consent");
await oemManager.Remove(Consent(), true);
Check(oemPlatform.Value(a.FxPath, Contract.FxFormat + ",6")?.Display == "OEM", "raw OEM baseline restored");

var permissionRepo = new MemoryRepository(); var permissionPlatform = new MemoryPlatform { Active = [a], Writable = false };
var permissionManager = new FleetManager(permissionPlatform, permissionRepo);
var permissionResult = await permissionManager.Apply(permissionManager.Prepare("fake"), Consent());
Check(permissionResult.Devices.Single().State == FleetEndpointState.ActionRequired && permissionPlatform.Writes.Count == 0, "no automatic permission broadening");

var missingRepo = new MemoryRepository(); var missingPlatform = new MemoryPlatform { Active = [a] };
string missingUser = a.FxPath + "\\" + Contract.Context + @"\User";
string missingVolatile = a.FxPath + "\\" + Contract.Context + @"\Volatile";
missingPlatform.AbsentKeys.Add(missingUser); missingPlatform.AbsentKeys.Add(missingVolatile);
var missingManager = new FleetManager(missingPlatform, missingRepo);
await missingManager.Apply(missingManager.Prepare("fake"), Consent());
Check(missingPlatform.Key(missingUser).Exists && missingPlatform.Value(missingUser, "__DotMicFleetTransient") == null, "missing stores created without controls or leaked markers");
missingPlatform.MissingRoots.Add(a.FxPath); missingPlatform.Active = []; missingPlatform.ForbidEnumeration = true;
int writesBeforeMissingRemove = missingPlatform.Writes.Count;
await missingManager.Remove(Consent(), false);
Check(missingPlatform.Writes.Count == writesBeforeMissingRemove && missingPlatform.SharedRemoves == 1, "vanished endpoint keys never recreated during offline removal");

var repairRepo = new MemoryRepository(); var repairPlatform = new MemoryPlatform { Active = [a] };
var repairManager = new FleetManager(repairPlatform, repairRepo);
await repairManager.Apply(repairManager.Prepare("fake"), Consent());
repairPlatform.Shared = false;
var repairPlan = repairManager.Prepare("fake", true);
Check(repairPlan.EndpointPlans.Count == 0 && repairPlan.SharedTransaction != null, "shared-only repair does not reapply healthy endpoints");
var repaired = await repairManager.Apply(repairPlan, Consent());
Check(repaired.SharedRepairs.Count == 1 && repairPlatform.SharedApplies == 2, "shared repair preserves original ownership journal");
await repairManager.Remove(Consent(), false);
Check(repairPlatform.SharedRemoves == 1, "shared repair chain restores the composed earliest baseline in one scope");

var identityRepo = new MemoryRepository(); var identityPlatform = new MemoryPlatform { Active = [a with { StableId = "" }] };
var identityManager = new FleetManager(identityPlatform, identityRepo);
await identityManager.Apply(identityManager.Prepare("fake"), Consent());
identityPlatform.Active = [a];
await identityManager.Reconcile("fake", true);
Check(identityRepo.Load().Devices.Count == 1, "stable ID becoming available does not duplicate physical identity");
await identityManager.Remove(Consent(), false);
identityPlatform.Active = [a with { EndpointId = "regenerated", FxPath = Mic(9).FxPath }];
await identityManager.Reconcile("fake", true);
Check(identityRepo.Load().Devices.Count == 1 && !identityRepo.Load().AutomaticEnrollment, "removal tombstone suppresses regenerated arrivals");

var legacyRepo = new MemoryRepository(); var legacyPlatform = new MemoryPlatform { Active = [], LegacyPath = "protected-old-snapshot", Legacy = new() { Target = a, Status = "Committed" } };
legacyPlatform.Legacy.Edits.Add(new() { Path = a.FxPath, Name = Contract.FxFormat + ",6", Before = null, After = RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid), Applied = true });
legacyPlatform.Put(a.FxPath, RawValue.Text(Contract.FxFormat + ",6", Contract.Clsid));
var legacyManager = new FleetManager(legacyPlatform, legacyRepo);
legacyManager.Prepare("fake");
var migrated = legacyRepo.Load();
Check(migrated.LegacyImported && migrated.LegacyOriginReceipt == legacyPlatform.LegacyPath && migrated.Devices.Single().Receipts.Count == 1, "offline legacy links/baseline preserved");
Check(legacyRepo.ReadEndpoint(migrated.Devices.Single().Receipts.Single()).Edits.Single().Before == null, "migration preserves original absent raw baseline");
legacyPlatform.ForbidEnumeration = true;
await legacyManager.Remove(Consent(), false);
legacyPlatform.ForbidEnumeration = false;
legacyManager.Prepare("fake");
Check(legacyPlatform.Imports == 1, "removed legacy origin never reimported");

var interruptedRepo = new MemoryRepository(); var interruptedPlatform = new MemoryPlatform { Active = [a] };
var interruptedManager = new FleetManager(interruptedPlatform, interruptedRepo);
await interruptedManager.Apply(interruptedManager.Prepare("fake"), Consent());
var interruptedInventory = interruptedRepo.Load();
string interruptedPath = interruptedInventory.Devices.Single().Receipts.Single();
var interruptedReceipt = interruptedRepo.ReadEndpoint(interruptedPath); interruptedReceipt.State = FleetEndpointState.Applying; interruptedRepo.SaveEndpoint(interruptedReceipt);
await interruptedManager.Reconcile("fake", true);
Check(interruptedPlatform.SharedApplies == 1 && interruptedPlatform.SharedRemoves == 0, "interrupted endpoint recovery never touches shared ownership");

var protectedRepo = new MemoryRepository(); var protectedPlatform = new MemoryPlatform { Active = [a], Writable = false };
var protectedManager = new FleetManager(protectedPlatform, protectedRepo);
var protectedConsent = Consent() with { AdvancedEndpoints = [FleetPolicy.Id(a)] };
await protectedManager.Apply(protectedManager.Prepare("fake"), protectedConsent);
Check(protectedRepo.ReadEndpoint(protectedRepo.Load().Devices.Single().Receipts.Single()).UndoPermissionKeys.Count > 0, "explicit apply journals exact undo authority");
protectedPlatform.Active = []; protectedPlatform.ForbidEnumeration = true;
var protectedRemoved = await protectedManager.Remove(Consent(), false);
Check(!protectedRemoved.HasUnresolvedBindings && protectedPlatform.SharedRemoves == 1 && protectedPlatform.ExistingOnlyWrites > 0, "protected owned values undo with recorded exact-key permission authority");
var protectedFailRepo = new MemoryRepository(); var protectedFailPlatform = new MemoryPlatform { Active = [a], Writable = false, FailEndpoint = a.FxPath };
var protectedFailManager = new FleetManager(protectedFailPlatform, protectedFailRepo);
var protectedFailed = await protectedFailManager.Apply(protectedFailManager.Prepare("fake"), protectedConsent);
Check(protectedFailed.Devices.Single().State == FleetEndpointState.ActionRequired && protectedFailPlatform.Value(a.FxPath, Contract.FxFormat + ",0") == null,
    "protected apply failure compensates owned values without fresh broad consent");
var newPermissionRepo = new MemoryRepository(); var newPermissionPlatform = new MemoryPlatform { Active = [a] };
var newPermissionManager = new FleetManager(newPermissionPlatform, newPermissionRepo);
await newPermissionManager.Apply(newPermissionManager.Prepare("fake"), Consent());
newPermissionPlatform.Writable = false;
var permissionPending = await newPermissionManager.Remove(Consent(), false);
Check(permissionPending.HasUnresolvedBindings && newPermissionPlatform.SharedRemoves == 0, "new undo permission scopes require consent");
permissionPending = await newPermissionManager.Remove(protectedConsent, false);
Check(!permissionPending.HasUnresolvedBindings, "explicit new undo permission approval completes retained exact changes");

var gapRepo = new MemoryRepository(); var gapPlatform = new MemoryPlatform { Active = [a] };
gapPlatform.Put(a.FxPath, RawValue.Text(Contract.FxFormat + ",6", "original-OEM"));
var gapManager = new FleetManager(gapPlatform, gapRepo);
await gapManager.Apply(gapManager.Prepare("fake"), Consent() with { ReplaceEndpoints = [FleetPolicy.Id(a)] });
gapPlatform.Erase(a.FxPath, Contract.FxFormat + ",6");
await gapManager.Apply(gapManager.Prepare("fake", true), Consent());
var gapRemoved = await gapManager.Remove(Consent(), false);
Check(!gapRemoved.HasUnresolvedBindings && gapPlatform.Value(a.FxPath, Contract.FxFormat + ",6")?.Display == "original-OEM", "repair gap composes earliest OEM baseline with latest owned expectation");
var foreignGapRepo = new MemoryRepository(); var foreignGapPlatform = new MemoryPlatform { Active = [a] };
foreignGapPlatform.Put(a.FxPath, RawValue.Text(Contract.FxFormat + ",6", "OEM-before"));
var foreignGapManager = new FleetManager(foreignGapPlatform, foreignGapRepo);
await foreignGapManager.Apply(foreignGapManager.Prepare("fake"), approved);
foreignGapPlatform.Erase(a.FxPath, Contract.FxFormat + ",6");
await foreignGapManager.Apply(foreignGapManager.Prepare("fake", true), Consent());
foreignGapPlatform.Put(a.FxPath, RawValue.Text(Contract.FxFormat + ",6", "foreign-now"));
var foreignGapRemoved = await foreignGapManager.Remove(Consent(), false);
Check(foreignGapRemoved.HasUnresolvedBindings && foreignGapPlatform.Value(a.FxPath, Contract.FxFormat + ",6")?.Display == "foreign-now", "composed repair history never overwrites a foreign current value");

var premutationRepo = new MemoryRepository(); var premutationPlatform = new MemoryPlatform { Active = [a], FailSharedStatus = "Prepared" };
var premutationManager = new FleetManager(premutationPlatform, premutationRepo);
var premutation = await premutationManager.Apply(premutationManager.Prepare("fake"), Consent());
Check(premutation.SharedReceipt == null && premutation.RetiredSharedReceipts.Count == 1, "prepared failure retires published head without mutation");
await premutationManager.Apply(premutationManager.Prepare("fake"), Consent());
Check(premutationRepo.Load().Devices.Single().State == FleetEndpointState.PendingActivation, "retry succeeds after premutation failure");

var compensationRepo = new MemoryRepository(); var compensationPlatform = new MemoryPlatform { Active = [a, b], Repository = compensationRepo };
var compensationManager = new FleetManager(compensationPlatform, compensationRepo);
await compensationManager.Apply(compensationManager.Prepare("fake"), Consent());
string predecessorToken = compensationPlatform.SharedToken;
var failedRepairPlan = compensationManager.Prepare("fake", true); failedRepairPlan.SharedTransaction = compensationPlatform.PrepareShared("candidate");
compensationPlatform.FailSharedStatus = "RolledBack";
var compensated = await compensationManager.Apply(failedRepairPlan, Consent());
Check(compensationPlatform.CompensationWithSiblings && compensationPlatform.SharedToken == predecessorToken && compensated.SharedRepairs.Count == 0,
    "authorized failed shared repair restores predecessor while sibling bindings survive");
Check(compensated.Devices.All(d => d.State == FleetEndpointState.PendingActivation), "shared compensation leaves completed endpoint states untouched");
var retryRepair = compensationManager.Prepare("fake", true); retryRepair.SharedTransaction = compensationPlatform.PrepareShared("candidate");
await compensationManager.Apply(retryRepair, Consent());
Check(compensationRepo.Load().SharedRepairs.Count == 1, "retry succeeds after verified complete shared compensation");
var pendingRepair = compensationManager.Prepare("fake", true); pendingRepair.SharedTransaction = compensationPlatform.PrepareShared("candidate");
compensationPlatform.FailSharedStatus = "RecoveryRequired";
var pendingShared = await compensationManager.Apply(pendingRepair, Consent());
string pendingHead = pendingShared.SharedRepairs.Last(); compensationPlatform.ReadShared(pendingHead).RegistrationPending = true;
Check(pendingShared.SharedRepairs.Count == 2, "incomplete shared compensation keeps the poisoned head for explicit recovery");
await compensationManager.Recover(Consent(), (_, _) => false);
Check(compensationRepo.Load().SharedRepairs.Last() == pendingHead, "declined partial registration recovery retains active journal");
var recoveredShared = await compensationManager.Recover(Consent(), (_, _) => true);
Check(recoveredShared.SharedRepairs.Count == 1 && recoveredShared.Devices.All(d => d.State == FleetEndpointState.PendingActivation), "interactive shared recovery restores predecessor, not successful endpoint bindings");
Check(!FleetPolicy.RetirableShared(new() { Scope = "Shared", Status = "RolledBack" })
    && !FleetPolicy.RetirableShared(new() { Scope = "Shared", Status = "ApplicationCleanupPending", CompensationVerified = true })
    && !FleetPolicy.RetirableShared(new() { Scope = "Shared", Status = "RolledBack", CompensationVerified = true, Application = new() { CleanupPending = true } }), "retirement requires verified complete compensation with no app cleanup pending");
var invalidPair = new Receipt { Scope = "Shared", Status = "RecoveryRequired", PredecessorReceipt = "foreign-predecessor" };
Check(!Transaction.CanCompensateShared(invalidPair, "head", new() { SharedReceipt = "original", SharedRepairs = ["head"] }), "compensation cannot bypass cleanup guard with arbitrary predecessor authority");

var prepareErrorRepo = new MemoryRepository(); var prepareErrorPlatform = new MemoryPlatform { Active = [a, b, Mic(3)] };
prepareErrorPlatform.FailedPrepareRoots.Add(Mic(3).FxPath);
var prepareErrorManager = new FleetManager(prepareErrorPlatform, prepareErrorRepo);
var prepareFailures = await prepareErrorManager.Apply(prepareErrorManager.Prepare("fake"), Consent());
Check(prepareFailures.Devices.Count == 3 && prepareFailures.Devices.Count(d => d.State == FleetEndpointState.ActionRequired) == 1
    && prepareFailures.Devices.Single(d => d.Target.EndpointId == Mic(3).EndpointId).Error?.Contains("fake prepare failure") == true, "prepare failures are durable, counted endpoint failures");
prepareErrorPlatform.FailedPrepareRoots.Clear();
Check(prepareErrorManager.Prepare("fake", true, retryFailedOnly: true).EndpointPlans.Count == 1, "retry-failed-only includes failed preparation record");
await prepareErrorManager.Apply(prepareErrorManager.Prepare("fake", true), Consent());
Check(prepareErrorRepo.Load().Devices.All(d => d.State == FleetEndpointState.PendingActivation), "repair retries preparation failures without losing their identity");
prepareErrorPlatform.Active = [a, b, Mic(3), Mic(4)];
Check(prepareErrorManager.Prepare("fake", true).EndpointPlans.Count == 1 && prepareErrorManager.Prepare("fake", true, retryFailedOnly: true).EndpointPlans.Count == 0,
    "repair covers unhealthy unknown endpoints; failed-only remains a distinct policy");

var ambiguityRepo = new MemoryRepository(); var ambiguityPlatform = new MemoryPlatform { Active = [a, a with { EndpointId = "duplicate-runtime", FxPath = Mic(8).FxPath, PhysicalInterface = "another-physical" }] };
var ambiguityManager = new FleetManager(ambiguityPlatform, ambiguityRepo);
var ambiguity = await ambiguityManager.Apply(ambiguityManager.Prepare("fake"), Consent());
Check(ambiguity.Devices.Count == 2 && ambiguity.Devices.All(d => d.IdentityAmbiguous && d.State == FleetEndpointState.ActionRequired)
    && ambiguity.Devices.Select(d => d.Id).Distinct().Count() == 2 && ambiguityPlatform.Writes.Count == 0, "ambiguous same-stable/container endpoints are distinct durable failures, never merged");
Check(FleetPolicy.Eligible(a with { PnpId = "", JackSubType = Contract.Microphone })
    && FleetPolicy.Eligible(a with { FormFactor = 6 }) && FleetPolicy.Eligible(a with { FormFactor = -1 })
    && !FleetPolicy.Eligible(a with { PnpId = @"SWD\VIRTUAL" }), "shared eligibility uses backing identity, supports physical forms and rejects known software adapters");
Check(!FleetPolicy.Eligible(a with { PnpId = @"ROOT\VIRTUAL", JackSubType = Contract.Microphone }), "a known software backing device is not made physical by its jack label");

var noOpRepo = new MemoryRepository(); var noOpPlatform = new MemoryPlatform { Active = [a] }; var noOpManager = new FleetManager(noOpPlatform, noOpRepo);
await noOpManager.Apply(noOpManager.Prepare("fake"), Consent() with { AutomaticEnrollment = false });
var noOpPlan = noOpManager.Prepare("fake");
Check(noOpPlan.EndpointPlans.Count == 0 && noOpPlan.SharedTransaction == null, "no-op automatic enrollment fixture");
await noOpManager.Apply(noOpPlan, Consent());
Check(noOpRepo.Load().AutomaticEnrollment, "no-op apply durably saves changed automatic-enrollment policy");

var service = new AudioDependent("approved", "Approved", 4); var addedService = new AudioDependent("new", "Unapproved", 4);
var serviceReceipt = new Receipt { Scope = "Shared", AudioDependents = [service] };
try { FleetPolicy.ApproveDependents(serviceReceipt, Consent(), [service]); throw new Exception("implicit service consent accepted"); } catch (OperationCanceledException) { }
try { FleetPolicy.ApproveDependents(serviceReceipt, Consent() with { ApprovedDependents = [service] }, [service, addedService]); throw new Exception("service expansion accepted"); } catch (IOException) { }
Check(FleetPolicy.ApproveDependents(serviceReceipt, Consent() with { ApprovedDependents = [service] }, [service], true).Count == 1, "explicit matching service approval accepted");
serviceReceipt.AudioRestartPending = true;
Check(FleetPolicy.ApproveDependents(serviceReceipt, Consent() with { ApprovedDependents = [service] }, []).Count == 1, "interrupted service recovery retains original approved stopped service");
try { FleetPolicy.ApproveDependents(serviceReceipt, Consent() with { ApprovedDependents = [service, addedService] }, []); throw new Exception("interrupted service scope silently broadened"); } catch (IOException) { }
var metadata = new[] { RawValue.Dword("Flags", 1), RawValue.Text("APOInterface0", "interface") };
Check(!FleetPolicy.RegistrationHealthy(metadata, [new() { Exists = true }])
    && !FleetPolicy.RegistrationHealthy(metadata, [new() { Exists = true, Values = [RawValue.Text("Flags", "1"), metadata[1]] }])
    && !FleetPolicy.RegistrationHealthy(metadata, [new() { Exists = true }, new() { Exists = true, Values = metadata.ToList() }])
    && FleetPolicy.RegistrationHealthy(metadata, [new() { Exists = true, Values = metadata.ToList() }]), "empty/wrong-type APO shells are not healthy; exact registration metadata is required");
using (var lockStream = typeof(PayloadPolicy).Assembly.GetManifestResourceStream("DotMic.Setup.ApoPayload.lock.json")!) {
    var originalLock = JsonSerializer.Deserialize<PayloadFile[]>(lockStream, Contract.Json)!;
    Check(PayloadPolicy.ResolveLock(originalLock.Single(f => f.Path == "APO/DotMic.ApoGate.dll").Hash).Length == originalLock.Length, "published immutable dependency lock remains supported");
}

var aclRepo = new MemoryRepository(); var aclPlatform = new MemoryPlatform { Active = [a] }; var aclManager = new FleetManager(aclPlatform, aclRepo);
await aclManager.Apply(aclManager.Prepare("fake"), Consent());
var aclInventory = aclRepo.Load(); string aclPath = aclInventory.Devices.Single().Receipts.Single(); var aclReceipt = aclRepo.ReadEndpoint(aclPath);
aclReceipt.PendingSecurityRestore.Add(a.FxPath); aclReceipt.PendingSecurityExpected[a.FxPath] = []; aclReceipt.PendingSecurityOriginal[a.FxPath] = []; aclReceipt.AdvancedKeys.Add(a.FxPath); aclRepo.SaveEndpoint(aclReceipt);
int aclWritesBefore = aclPlatform.Writes.Count;
await aclManager.Recover(Consent(), (_, _) => true);
Check(aclRepo.ReadEndpoint(aclPath).PendingSecurityRestore.Count == 0 && aclPlatform.Writes.Count == aclWritesBefore
    && aclPlatform.Value(a.FxPath, Contract.FxFormat + ",6")?.Display == Contract.Clsid, "completed endpoint pending ACL recovery does not undo its binding");

void Reject(EndpointReceipt receipt) { try { FleetStore.Validate(receipt); throw new Exception("scope accepted"); } catch (IOException) { } }
Reject(new() { Target = a, Edits = [new() { Path = Contract.ComPath, Name = "foreign" }] });
Reject(new() { Target = a with { FxPath = a.FxPath + "\\..\\other" } });
Reject(new() { Target = a, Keys = [new() { Path = Contract.AudioPath }] });
Reject(new() { Target = a, PendingSecurityRestore = [a.FxPath] });
var envelopeReceipt = new EndpointReceipt { Target = a };
var encoded = FleetStore.Encode(envelopeReceipt);
Check(FleetStore.Decode<EndpointReceipt>(encoded).TransactionId == envelopeReceipt.TransactionId, "pure envelope round trip");
var damaged = System.Text.Json.Nodes.JsonNode.Parse(encoded)!; damaged["Sha256"] = "0000";
try { FleetStore.Decode<EndpointReceipt>(JsonSerializer.SerializeToUtf8Bytes(damaged)); throw new Exception("checksum corruption accepted"); } catch (IOException) { }
try { FleetStore.Decode<EndpointReceipt>(new byte[16 * 1024 * 1024 + 1]); throw new Exception("unbounded envelope accepted"); } catch (IOException) { }
var deploymentOrder = new List<string>();
SharedDeployment.Run(() => deploymentOrder.Add("Stop"), () => deploymentOrder.Add("OwnedCopy"), () => deploymentOrder.Add("Register"),
    () => deploymentOrder.Add("Compensate"), () => deploymentOrder.Add("Start"));
Check(deploymentOrder.SequenceEqual(new[] { "Stop", "OwnedCopy", "Register", "Start" }), "shared runtime update stops before owned copy, registers before its single start");
deploymentOrder.Clear();
try {
    SharedDeployment.Run(() => deploymentOrder.Add("Stop"), () => { deploymentOrder.Add("OwnedCopy"); throw new IOException("copy failed"); },
        () => deploymentOrder.Add("Register"), () => deploymentOrder.Add("Compensate"), () => deploymentOrder.Add("Start"));
    throw new Exception("copy failure swallowed");
} catch (IOException) { }
Check(deploymentOrder.SequenceEqual(new[] { "Stop", "OwnedCopy", "Compensate", "Start" }), "copy failure restores predecessor before original services start");
deploymentOrder.Clear();
try {
    SharedDeployment.Run(() => { deploymentOrder.Add("Stop"); throw new IOException("stop interrupted"); }, () => deploymentOrder.Add("OwnedCopy"),
        () => deploymentOrder.Add("Register"), () => deploymentOrder.Add("Compensate"), () => deploymentOrder.Add("Start"));
    throw new Exception("stop failure swallowed");
} catch (IOException) { }
Check(deploymentOrder.SequenceEqual(new[] { "Stop", "Start" }), "partial stop rejection restores original services without copying");
try {
    SharedDeployment.Run(() => { }, () => throw new IOException("copy-primary"), () => { }, () => throw new IOException("compensate-secondary"), () => throw new IOException("start-secondary"));
    throw new Exception("combined deployment failure swallowed");
} catch (AggregateException error) { Check(error.Flatten().InnerExceptions.Count == 3, "copy, compensation and service restore failures all preserved"); }
var oldSharedPayload = new Receipt { Scope = "Shared", Files = [new() { Relative = "APO/DotMic.ApoGate.dll", Hash = "old" }] };
var changedSharedPayload = new Receipt { Scope = "Shared", Files = [new() { Relative = "APO/DotMic.ApoGate.dll", Hash = "candidate", BeforeHash = "old", Existed = true }] };
Check(SharedDeployment.NeedsStoppedReplacement(changedSharedPayload, oldSharedPayload)
    && !SharedDeployment.NeedsStoppedReplacement(changedSharedPayload, null), "only changed predecessor-owned payload uses stop-before-copy path");
changedSharedPayload.Files[0].Hash = "old";
Check(!SharedDeployment.NeedsStoppedReplacement(changedSharedPayload, oldSharedPayload), "unchanged shared payload avoids a second audio interruption");

var invalidationRepo = new MemoryRepository(); var invalidationPlatform = new MemoryPlatform { Active = [a] }; var invalidationManager = new FleetManager(invalidationPlatform, invalidationRepo);
await invalidationManager.Apply(invalidationManager.Prepare("fake"), Consent()); invalidationManager.Observe(a, true);
int bindingWrites = invalidationPlatform.Writes.Count;
var runtimePlan = invalidationManager.Prepare("fake", true); runtimePlan.SharedTransaction = invalidationPlatform.PrepareShared("candidate");
var invalidated = await invalidationManager.Apply(runtimePlan, Consent());
Check(invalidated.Devices.Single().State == FleetEndpointState.PendingActivation && invalidationPlatform.Writes.Count == bindingWrites,
    "successful shared update invalidates old processing proof without rebinding endpoints");

float[] profile = [0, 12, 0, -48, 5, 160, 120, 0, 6];
string userA = a.FxPath + "\\" + Contract.Context + @"\User", volatileA = a.FxPath + "\\" + Contract.Context + @"\Volatile";
var replicaRepo = new MemoryRepository(); var replicaPlatform = new MemoryPlatform { Active = [a, b], CommonValues = profile };
replicaPlatform.Put(userA, FleetReplica.Serialized(2, 3)); replicaPlatform.Put(volatileA, FleetReplica.Serialized(2, 4));
var replicaManager = new FleetManager(replicaPlatform, replicaRepo);
await replicaManager.Apply(replicaManager.Prepare("fake"), Consent());
// Simulate the native controller changing User before the first service relay.
replicaPlatform.Put(userA, FleetReplica.Serialized(2, 12));
Check(!replicaManager.ReplicateProfiles(profile), "valid native common-authority update is adopted into scoped replica journal");
var firstReplica = replicaRepo.Load().Devices.First(d => d.Target.EndpointId == a.EndpointId).Receipts.Select(replicaRepo.ReadEndpoint).Single(r => r.Operation == "Replica");
Check(firstReplica.Edits.Count == 18 && firstReplica.Edits.Single(e => e.Path == userA && e.Name == FleetReplica.Name(2)).Before?.Same(FleetReplica.Serialized(2, 3)) == true,
    "replica receipt journals all nine User and Volatile baselines from initial enrollment, not current controller replicas");
profile = (float[])profile.Clone(); profile[1] = 18; replicaPlatform.CommonValues = profile;
Check(!replicaManager.ReplicateProfiles(profile), "second profile revision updates durable owned expectations");
Check(replicaRepo.Load().Devices.All(d => d.Receipts.Select(replicaRepo.ReadEndpoint).Count(r => r.Operation == "Replica") == 1), "repeated parameter changes use bounded per-endpoint journals");
var replicasRemoved = await replicaManager.Remove(Consent(), false);
Check(!replicasRemoved.HasUnresolvedBindings && replicaPlatform.Value(userA, FleetReplica.Name(2))?.Same(FleetReplica.Serialized(2, 3)) == true
    && replicaPlatform.Value(volatileA, FleetReplica.Name(2))?.Same(FleetReplica.Serialized(2, 4)) == true,
    "uninstall restores earliest User and Volatile originals after multiple legitimate control changes");

var partialReplicaRepo = new MemoryRepository(); var partialReplicaPlatform = new MemoryPlatform { Active = [a, b], CommonValues = profile, FailControlEndpoint = a.EndpointId };
var partialReplicaManager = new FleetManager(partialReplicaPlatform, partialReplicaRepo);
await partialReplicaManager.Apply(partialReplicaManager.Prepare("fake"), Consent());
Check(partialReplicaManager.ReplicateProfiles(profile), "failed CAPX endpoint requests retry");
Check(partialReplicaRepo.Load().Devices.Single(d => d.Target.EndpointId == a.EndpointId).Receipts.Select(partialReplicaRepo.ReadEndpoint).Single(r => r.Operation == "Replica").ReplicationPending
    && !partialReplicaRepo.Load().Devices.Single(d => d.Target.EndpointId == b.EndpointId).Receipts.Select(partialReplicaRepo.ReadEndpoint).Single(r => r.Operation == "Replica").ReplicationPending,
    "one CAPX failure retains its pending journal while sibling propagation succeeds");
partialReplicaPlatform.FailControlEndpoint = null;
Check(!partialReplicaManager.ReplicateProfiles(profile), "partial replica retry completes from journaled expectations");
partialReplicaPlatform.Put(userA, FleetReplica.Serialized(2, 25));
int beforeForeignReplica = partialReplicaPlatform.ControlCalls;
Check(partialReplicaManager.ReplicateProfiles(profile) && partialReplicaPlatform.ControlCalls == beforeForeignReplica
    && partialReplicaPlatform.Value(userA, FleetReplica.Name(2))?.Same(FleetReplica.Serialized(2, 25)) == true, "external valid-but-unowned parameter is retained, never clobbered by relay");
var conflictReplicaRemoval = await partialReplicaManager.Remove(Consent(), false);
Check(conflictReplicaRemoval.HasUnresolvedBindings && partialReplicaPlatform.SharedRemoves == 0, "external parameter conflict keeps shared cleanup blocked");
await partialReplicaManager.Recover(Consent(), (_, _) => true);
Check(!partialReplicaRepo.Load().HasUnresolvedBindings, "explicit scoped recovery can restore conflicting parameter baseline without manual registry editing");
var sharedGapRepo = new MemoryRepository(); var sharedGapPlatform = new MemoryPlatform { Active = [a] }; var sharedGapManager = new FleetManager(sharedGapPlatform, sharedGapRepo);
await sharedGapManager.Apply(sharedGapManager.Prepare("fake"), Consent());
string sharedOriginPath = sharedGapRepo.Load().SharedReceipt!;
var sharedOrigin = sharedGapPlatform.ReadShared(sharedOriginPath);
string comDefault = Contract.ComPath + @"\InprocServer32", oldPnp = @"C:\Windows\System32\old-pnp-apo.dll";
sharedOrigin.Edits.Add(new() { Path = comDefault, Name = "", Before = RawValue.Text("", oldPnp), After = RawValue.Text("", "ours"), Applied = true });
sharedOrigin.Edits.Add(new() { Path = Contract.ApoPath, Name = "OEM", Before = RawValue.Dword("OEM", 17), After = RawValue.Dword("OEM", 1), Applied = true });
sharedOrigin.Files.Add(new() { Relative = "APO/DotMic.ApoGate.dll", BeforeHash = "original-file", Hash = "ours-file", Existed = true, Applied = true });
sharedOrigin.Application = new() { Root = @"C:\FakeApp", Files = [new() { Relative = "DOT MIC.exe", BeforeHash = "original-app", Hash = "ours-app", Existed = true, Applied = true }] };
sharedGapPlatform.Put(comDefault, RawValue.Text("", "ours")); sharedGapPlatform.Erase(comDefault, "");
sharedGapPlatform.Erase(Contract.ApoPath, "OEM"); sharedGapPlatform.Shared = false;
var sharedGapPlan = sharedGapManager.Prepare("fake", true);
sharedGapPlan.SharedTransaction!.Receipt.Edits = [new() { Path = comDefault, Name = "", Before = null, After = RawValue.Text("", "ours"), Applied = true },
    new() { Path = Contract.ApoPath, Name = "OEM", Before = null, After = RawValue.Dword("OEM", 1), Applied = true }];
sharedGapPlan.SharedTransaction.Receipt.Files = [new() { Relative = "APO/DotMic.ApoGate.dll", BeforeHash = null, Hash = "new-file", Existed = false, Applied = true }];
sharedGapPlan.SharedTransaction.Receipt.Application = new() { Root = @"C:\FakeApp", Files = [new() { Relative = "DOT MIC.exe", BeforeHash = null, Hash = "new-app", Existed = false, Applied = true }] };
await sharedGapManager.Apply(sharedGapPlan, Consent());
string immutableSharedOrigin = JsonSerializer.Serialize(sharedOrigin, Contract.Json);
string repairPath = sharedGapRepo.Load().SharedRepairs.Single(); string immutableSharedRepair = JsonSerializer.Serialize(sharedGapPlatform.ReadShared(repairPath), Contract.Json);
var sharedGapRemoved = await sharedGapManager.Remove(Consent(), false);
Check(sharedGapRemoved.SharedReceipt == null && sharedGapRemoved.SharedCleanupReceipt == null && sharedGapRemoved.SharedError == null
    && sharedGapPlatform.Value(comDefault, "")?.Display == oldPnp && sharedGapPlatform.Value(Contract.ApoPath, "OEM")?.Same(RawValue.Dword("OEM", 17)) == true,
    "shared repair after deletion restores original OEM COM/default and APO metadata without intermediate null-baseline conflict");
Check(sharedGapPlatform.SharedFileHashes["APO|APO/DotMic.ApoGate.dll"] == "original-file" && sharedGapPlatform.SharedFileHashes[@"C:\FakeApp|DOT MIC.exe"] == "original-app",
    "shared composition preserves earliest APO and application file baselines across deletion/repair gaps");
Check(JsonSerializer.Serialize(sharedOrigin, Contract.Json) == immutableSharedOrigin
    && JsonSerializer.Serialize(sharedGapPlatform.ReadShared(repairPath), Contract.Json) == immutableSharedRepair, "shared cleanup never rewrites immutable origin/repair journals");
Check(sharedGapPlatform.ReadShared(sharedGapPlatform.LastCleanup!).SharedSourceReceipts.SequenceEqual(new[] { sharedOriginPath, repairPath }), "archived cleanup retains the complete immutable source chain");
Check(sharedGapPlatform.SharedOrder.SequenceEqual(new[] { "Validate", "Journal", "Stop", "Unload", "Registry", "Files", "Application", "Start" }), "inverse registration and files share one approved journaled stop/unload/start interval");

var stopFailureRepo = new MemoryRepository(); var stopFailurePlatform = new MemoryPlatform { Active = [a] }; var stopFailureManager = new FleetManager(stopFailurePlatform, stopFailureRepo);
await stopFailureManager.Apply(stopFailureManager.Prepare("fake"), Consent());
var stopOrigin = stopFailurePlatform.ReadShared(stopFailureRepo.Load().SharedReceipt!);
stopOrigin.Edits.Add(new() { Path = comDefault, Name = "", Before = RawValue.Text("", oldPnp), After = RawValue.Text("", "ours"), Applied = true });
stopFailurePlatform.Put(comDefault, RawValue.Text("", "ours")); stopFailurePlatform.FailSharedStop = true;
var stoppedRemoval = await stopFailureManager.Remove(Consent(), false);
Check(stoppedRemoval.SharedCleanupReceipt != null && stoppedRemoval.SharedReceipt != null && stoppedRemoval.SharedError != null
    && stopFailurePlatform.Value(comDefault, "")?.Display == "ours" && !stopFailurePlatform.SharedOrder.Contains("Registry") && !stopFailurePlatform.SharedOrder.Contains("Files"),
    "stop failure makes no inverse registration/file mutations and retains retry ownership");
var resumedCleanup = await stopFailureManager.Recover(Consent(), (_, _) => true);
Check(resumedCleanup.SharedReceipt == null && resumedCleanup.SharedCleanupReceipt == null && stopFailurePlatform.Value(comDefault, "")?.Display == oldPnp,
    "scoped recovery retries the same composed cleanup baseline after a failed stop");

var foreignSharedRepo = new MemoryRepository(); var foreignSharedPlatform = new MemoryPlatform { Active = [a] }; var foreignSharedManager = new FleetManager(foreignSharedPlatform, foreignSharedRepo);
await foreignSharedManager.Apply(foreignSharedManager.Prepare("fake"), Consent());
foreignSharedPlatform.ReadShared(foreignSharedRepo.Load().SharedReceipt!).Edits.Add(new() { Path = comDefault, Name = "", Before = RawValue.Text("", oldPnp), After = RawValue.Text("", "ours"), Applied = true });
foreignSharedPlatform.Put(comDefault, RawValue.Text("", "foreign-now"));
var foreignSharedRemoval = await foreignSharedManager.Remove(Consent(), false);
Check(foreignSharedRemoval.SharedCleanupReceipt != null && foreignSharedPlatform.Value(comDefault, "")?.Display == "foreign-now", "composed shared cleanup retains foreign registry values, not automatic replacement authority");

var restoreOrder = new List<string>(); Receipt? crashJournal = null;
var restoreReceipt = new Receipt { Scope = "Shared", Operation = "SharedCleanupComposition", Status = "CleanupPrepared" };
try {
    SharedDeployment.Restore(() => restoreOrder.Add("Validate"), () => {
        restoreReceipt.AudioRestartPending = true; restoreReceipt.AudioRootBeforeStop = 4;
        crashJournal = JsonSerializer.Deserialize<Receipt>(JsonSerializer.Serialize(restoreReceipt, Contract.Json), Contract.Json); restoreOrder.Add("Journal");
    }, () => { restoreOrder.Add("Stop"); throw new IOException("interrupted stop"); }, () => restoreOrder.Add("Unload"), () => restoreOrder.Add("Registry"),
        () => restoreOrder.Add("Files"), () => restoreOrder.Add("Application"), () => restoreOrder.Add("Start"));
    throw new Exception("stop failure swallowed");
} catch (IOException) { }
Check(crashJournal?.AudioRestartPending == true && crashJournal.AudioRootBeforeStop == 4
    && restoreOrder.SequenceEqual(new[] { "Validate", "Journal", "Stop", "Start" }), "before-stop crash journal records original service state before any inverse mutation");
restoreOrder.Clear();
SharedDeployment.Restore(() => Check(crashJournal!.AudioRestartPending && crashJournal.AudioRootBeforeStop == 4, "retry reads persisted original service state"),
    () => restoreOrder.Add("Journal"), () => restoreOrder.Add("Stop"), () => restoreOrder.Add("Unload"), () => restoreOrder.Add("Registry"),
    () => restoreOrder.Add("Files"), () => restoreOrder.Add("Application"), () => { crashJournal!.AudioRestartPending = false; restoreOrder.Add("Start"); });
Check(!crashJournal!.AudioRestartPending && restoreOrder.SequenceEqual(new[] { "Journal", "Stop", "Unload", "Registry", "Files", "Application", "Start" }),
    "persisted before-mutation journal allows ordered cleanup retry and original-state restoration");
restoreOrder.Clear();
SharedDeployment.Restore(() => restoreOrder.Add("Validate"), () => restoreOrder.Add("Journal"), () => restoreOrder.Add("Stop"), () => restoreOrder.Add("Unload"),
    () => restoreOrder.Add("Registry"), () => restoreOrder.Add("Files"), () => restoreOrder.Add("Application"), () => restoreOrder.Add("Start"), alreadyStopped: true);
Check(restoreOrder.SequenceEqual(new[] { "Unload", "Registry", "Files", "Application" }), "nested predecessor compensation verifies unload but never stops/starts audio a second time");
restoreOrder.Clear();
try {
    SharedDeployment.Restore(() => throw new IOException("dependency rejection"), () => restoreOrder.Add("Journal"), () => restoreOrder.Add("Stop"), () => restoreOrder.Add("Unload"),
        () => restoreOrder.Add("Registry"), () => restoreOrder.Add("Files"), () => restoreOrder.Add("Application"), () => restoreOrder.Add("Start"));
    throw new Exception("dependency rejection swallowed");
} catch (IOException) { }
Check(restoreOrder.Count == 0, "unapproved dependencies reject shared recovery before service or registry/file transitions");

var absenceRepo = new MemoryRepository(); var absencePlatform = new MemoryPlatform { Active = [a], CommonValues = profile }; var absenceManager = new FleetManager(absencePlatform, absenceRepo);
absencePlatform.Put(volatileA, FleetReplica.Serialized(2, 4));
await absenceManager.Apply(absenceManager.Prepare("fake"), Consent());
absencePlatform.Put(userA, FleetReplica.Serialized(2, profile[1])); absencePlatform.Erase(volatileA, FleetReplica.Name(2));
Check(!absenceManager.ReplicateProfiles(profile), "native UI clearing original Volatile before first relay is recognized as a known owned absence");
var absenceRemoved = await absenceManager.Remove(Consent(), false);
Check(!absenceRemoved.HasUnresolvedBindings && absencePlatform.Value(volatileA, FleetReplica.Name(2))?.Same(FleetReplica.Serialized(2, 4)) == true,
    "uninstall restores original Volatile override after legitimate unjournaled UI commit and journaled relay");
var directAbsenceRepo = new MemoryRepository(); var directAbsencePlatform = new MemoryPlatform { Active = [a], CommonValues = profile }; var directAbsenceManager = new FleetManager(directAbsencePlatform, directAbsenceRepo);
directAbsencePlatform.Put(volatileA, FleetReplica.Serialized(2, 4)); await directAbsenceManager.Apply(directAbsenceManager.Prepare("fake"), Consent());
directAbsencePlatform.Erase(volatileA, FleetReplica.Name(2));
Check(!(await directAbsenceManager.Remove(Consent(), false)).HasUnresolvedBindings && directAbsencePlatform.Value(volatileA, FleetReplica.Name(2))?.Same(FleetReplica.Serialized(2, 4)) == true,
    "known native-cleared absence also restores initial Volatile baseline without an intervening relay");
var unknownAbsenceRepo = new MemoryRepository(); var unknownAbsencePlatform = new MemoryPlatform { Active = [a] }; var unknownAbsenceManager = new FleetManager(unknownAbsencePlatform, unknownAbsenceRepo);
unknownAbsencePlatform.Put(volatileA, FleetReplica.Serialized(2, 4)); await unknownAbsenceManager.Apply(unknownAbsenceManager.Prepare("fake"), Consent());
unknownAbsencePlatform.Erase(volatileA, FleetReplica.Name(2));
Check((await unknownAbsenceManager.Remove(Consent(), false)).HasUnresolvedBindings && unknownAbsencePlatform.Value(volatileA, FleetReplica.Name(2)) == null,
    "missing common User authority cannot authorize restoration over an unknown Volatile absence");
var validUserSource = new RawValue("2", 3, BitConverter.GetBytes(12f));
Check(FleetReplica.Prove(2, true, validUserSource, null).Matches(null)
    && !FleetReplica.Prove(2, true, null, null).Matches(null)
    && !FleetReplica.Prove(2, true, RawValue.Dword("2", 12), null).Matches(null)
    && !FleetReplica.Prove(2, true, new("2", 3, BitConverter.GetBytes(float.NaN)), null).Matches(null)
    && !FleetReplica.Prove(2, true, validUserSource, RawValue.Text("2", "corrupt")).Known
    && !FleetReplica.Prove(2, true, validUserSource, null).Matches(FleetReplica.Serialized(2, 12)), "typed replica proof separates exact expected absence from missing/corrupt authority and foreign values");
Console.WriteLine("PASS: fake-only fleet and new shared repair-gap composition, immutable baselines, stop-before-inverse recovery, stop/crash retries, and typed Volatile absence ownership. No live native, registry, service or capture operations were invoked.");

internal sealed class MemoryRepository : IFleetRepository
{
    private FleetInventory inventory = new();
    private readonly Dictionary<string, EndpointReceipt> receipts = [];
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json), Contract.Json)!;
    public FleetInventory Load() => Clone(inventory);
    public void Save(FleetInventory value) => inventory = Clone(value);
    public string PathFor(EndpointReceipt receipt) => receipt.TransactionId.ToString("N");
    public EndpointReceipt ReadEndpoint(string path) => Clone(receipts[path]);
    public void SaveEndpoint(EndpointReceipt receipt) => receipts[PathFor(receipt)] = Clone(receipt);
}
internal sealed class MemoryPlatform : IFleetPlatform
{
    public EndpointIdentity[] Active = [];
    public bool ForbidEnumeration, Writable = true, Shared;
    public string? FailEndpoint, LegacyPath, FailSharedStatus;
    public IFleetRepository? Repository;
    public string SharedToken = "none";
    public bool CompensationWithSiblings;
    public HashSet<string> FailedPrepareRoots = new(StringComparer.OrdinalIgnoreCase);
    public float[]? CommonValues;
    public string? FailControlEndpoint;
    public int ControlCalls;
    public bool FailSharedStop;
    public string? LastCleanup;
    public List<string> SharedOrder = [];
    public Dictionary<string, string> SharedFileHashes = new(StringComparer.OrdinalIgnoreCase);
    public Receipt? Legacy;
    public int SharedApplies, SharedRemoves, ExistingOnlyWrites, Imports;
    public List<Edit> Writes = [];
    public HashSet<string> AbsentKeys = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissingRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RawValue> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sharedReceipts = [];
    private readonly Dictionary<string, Receipt> sharedStates = [];
    private readonly Dictionary<string, string> sharedBefore = [];
    public void Put(string path, RawValue value) => values[path + "\0" + value.Name] = value;
    public void Erase(string path, string name) => values.Remove(path + "\0" + name);
    public EndpointIdentity[] Endpoints() => ForbidEnumeration ? throw new Exception("offline removal enumerated devices") : Active;
    public RawValue? Value(string path, string name) => MissingRoots.Any(root => FleetPolicy.Within(path, root)) ? null : values.GetValueOrDefault(path + "\0" + name);
    public ReplicaProof OwnedReplica(EndpointIdentity target, string path, string name)
    {
        if (CommonValues == null || !FleetReplica.IsControl(target, path, name)) return default;
        int id = int.Parse(name[(name.LastIndexOf(',') + 1)..]);
        return FleetReplica.Prove(id, path.EndsWith(@"\Volatile", StringComparison.OrdinalIgnoreCase), new(id.ToString(), 3, BitConverter.GetBytes(CommonValues[id - 1])), null);
    }
    public void SetControl(EndpointIdentity target, uint property, float value)
    {
        ControlCalls++;
        if (target.EndpointId == FailControlEndpoint && property == 2) throw new IOException("fake CAPX notification failure");
        string context = target.FxPath + "\\" + Contract.Context;
        Put(context + @"\User", FleetReplica.Serialized((int)property, value)); Erase(context + @"\Volatile", FleetReplica.Name((int)property));
    }
    public KeyImage Key(string path) => FailedPrepareRoots.Contains(path) ? throw new IOException("fake prepare failure for " + path) : new() { Path = path, Exists = !AbsentKeys.Contains(path) && !MissingRoots.Any(root => FleetPolicy.Within(path, root)) };
    public bool CanWrite(string path) => Writable;
    public void Write(Edit edit, EndpointReceipt receipt, bool advanced, Action save, bool existingOnly = false)
    {
        if (FailEndpoint == edit.Path && edit.Name == Contract.FxFormat + ",6" && !existingOnly) { FailEndpoint = null; throw new IOException("fake endpoint failure"); }
        if (!Writable && !advanced) throw new UnauthorizedAccessException("fake permissions");
        if (!Writable && advanced && !receipt.AdvancedKeys.Contains(edit.Path)) { receipt.AdvancedKeys.Add(edit.Path); save(); }
        var now = Value(edit.Path, edit.Name);
        if (edit.Before == null ? now != null : !edit.Before.Same(now)) throw new IOException("fake baseline conflict");
        if (existingOnly) { if (!Key(edit.Path).Exists) throw new IOException("existing-only write would create a key"); ExistingOnlyWrites++; }
        else AbsentKeys.Remove(edit.Path);
        Writes.Add(edit);
        if (edit.After == null) values.Remove(edit.Path + "\0" + edit.Name); else Put(edit.Path, edit.After);
    }
    public void RestoreSecurity(EndpointReceipt receipt, Action save, Func<string, string, bool>? resolveConflict = null)
    {
        if (receipt.PendingSecurityRestore.Count == 0) return;
        if (!(resolveConflict?.Invoke(receipt.Target.FxPath, "fake ACL conflict") ?? false)) throw new IOException("fake ACL conflict retained");
        receipt.PendingSecurityRestore.Clear(); receipt.PendingSecurityExpected.Clear(); receipt.PendingSecurityOriginal.Clear(); save();
    }
    public bool SharedHealthy() => Shared;
    public bool SharedCommitted(string path) => sharedStates.GetValueOrDefault(path)?.Status == "Committed";
    public Receipt ReadShared(string path) => sharedStates[path];
    public string ComposeSharedCleanup(IReadOnlyList<string> sources)
    {
        string path = "fake-cleanup-" + Guid.NewGuid();
        var receipt = SharedCleanup.Compose(sources.Select(ReadShared).ToArray()); receipt.SharedSourceReceipts = sources.ToList();
        sharedStates[path] = receipt; LastCleanup = path; return path;
    }
    public Transaction PrepareShared(string package)
    {
        var transaction = new Transaction(new Receipt { Scope = "Shared" }, "fake-shared-" + Guid.NewGuid(), package, new(Contract.Version, "", []), false);
        sharedStates[transaction.ReceiptPath] = transaction.Receipt; sharedBefore[transaction.ReceiptPath] = SharedToken; return transaction;
    }
    public Task ApplyShared(Transaction transaction, FleetConsent consent)
    {
        SharedApplies++;
        if (FailSharedStatus != null) {
            string failure = FailSharedStatus; FailSharedStatus = null;
            if (failure != "Prepared") {
                transaction.Receipt.Status = "Applying"; SharedToken = "broken-candidate";
                if (failure == "RolledBack") {
                    var inventory = Repository?.Load() ?? new();
                    if (inventory.HasUnresolvedBindings) {
                        CompensationWithSiblings = Transaction.CanCompensateShared(transaction.Receipt, transaction.ReceiptPath, inventory);
                        if (!CompensationWithSiblings) throw new Exception("shared repair compensation unexpectedly blocked");
                    }
                    SharedToken = sharedBefore[transaction.ReceiptPath]; transaction.Receipt.CompensationVerified = true;
                }
                transaction.Receipt.Status = failure;
            }
            throw new IOException("fake shared " + failure + " failure");
        }
        foreach (var edit in transaction.Receipt.Edits.Where(e => e.Applied)) { if (edit.After == null) Erase(edit.Path, edit.Name); else Put(edit.Path, edit.After); }
        foreach (var file in transaction.Receipt.Files.Where(f => f.Applied)) SharedFileHashes["APO|" + file.Relative] = file.Hash;
        if (transaction.Receipt.Application is { } application) foreach (var file in application.Files.Where(f => f.Applied)) SharedFileHashes[application.Root + "|" + file.Relative] = file.Hash;
        Shared = true; SharedToken = "installed-" + SharedApplies; transaction.Receipt.Status = "Committed"; sharedReceipts.Add(transaction.ReceiptPath); return Task.CompletedTask;
    }
    private void RestoreShared(string path, Func<string, string, bool>? conflict = null)
    {
        var receipt = sharedStates[path];
        try {
            SharedDeployment.Restore(() => SharedOrder.Add("Validate"), () => {
                receipt.AudioRootBeforeStop = 4; receipt.AudioRestartPending = true; SharedOrder.Add("Journal");
            }, () => { SharedOrder.Add("Stop"); if (FailSharedStop) { FailSharedStop = false; throw new IOException("fake stop failure"); } },
            () => SharedOrder.Add("Unload"), () => {
                SharedOrder.Add("Registry");
                foreach (var edit in receipt.Edits.Where(e => e.Applied).Reverse()) {
                    var actual = Value(edit.Path, edit.Name);
                    if (edit.Before == null ? actual == null : edit.Before.Same(actual)) continue;
                    if (!SharedCleanup.Matches(edit, actual) && !(conflict?.Invoke(edit.Path, "fake shared foreign value") ?? false)) throw new IOException("foreign shared registry conflict retained");
                    if (edit.Before == null) Erase(edit.Path, edit.Name); else Put(edit.Path, edit.Before);
                }
            }, () => { SharedOrder.Add("Files"); RestoreFiles("APO", receipt.Files); },
            () => { SharedOrder.Add("Application"); if (receipt.Application is { } app) RestoreFiles(app.Root, app.Files); },
            () => { SharedOrder.Add("Start"); receipt.AudioRestartPending = false; });
            receipt.Status = "RolledBack"; receipt.CompensationVerified = true; Shared = false;
        } catch { receipt.Status = "RecoveryRequired"; throw; }
        void RestoreFiles(string root, IEnumerable<FileEdit> files)
        {
            foreach (var file in files.Where(f => f.Applied)) {
                string key = root + "|" + file.Relative; string? actual = SharedFileHashes.GetValueOrDefault(key);
                if (file.Existed ? actual == file.BeforeHash : actual == null) continue;
                if (actual != file.Hash) throw new IOException("foreign shared file conflict retained");
                if (file.Existed) SharedFileHashes[key] = file.BeforeHash!; else SharedFileHashes.Remove(key);
            }
        }
    }
    public Task RemoveShared(string path, FleetConsent consent, bool keepProtectedAudio) { SharedRemoves++; RestoreShared(path); sharedReceipts.Remove(path); return Task.CompletedTask; }
    public Task RecoverShared(string path, FleetConsent consent, Func<string, string, bool> resolveConflict, bool compensation)
    {
        var receipt = sharedStates[path];
        if (receipt.Operation == "SharedCleanupComposition") { RestoreShared(path, resolveConflict); return Task.CompletedTask; }
        if (receipt.RegistrationPending && !resolveConflict("fake registration", "fake partial registration")) throw new IOException("registration recovery declined");
        if (compensation && Repository != null && !Transaction.CanCompensateShared(receipt, path, Repository.Load())) throw new IOException("invalid compensation predecessor");
        receipt.RegistrationPending = false; receipt.PendingSecurityRestore.Clear(); receipt.Status = "RolledBack"; receipt.CompensationVerified = true;
        SharedToken = sharedBefore.GetValueOrDefault(path) ?? "predecessor"; return Task.CompletedTask;
    }
    public string? LegacyOrigin() => LegacyPath;
    public string? LegacyLatest() => LegacyPath;
    public Receipt ReadLegacy(string path) => Legacy!;
    public string ImportShared(string path) { Imports++; Shared = true; string sharedPath = "fake-imported-shared-" + Imports; sharedReceipts.Add(sharedPath); sharedStates[sharedPath] = new() { Scope = "Shared", Status = "Committed" }; return sharedPath; }
}
