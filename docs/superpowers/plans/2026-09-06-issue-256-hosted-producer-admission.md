# Issue #256 Hosted Grimoire Producer Admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Protect every first-party hosted Grimoire, provider, and filesystem producer with an exact ordinary-work lease or an exact, proven nonordinary authority, including one Batch effect frontier per existing 64-line accounting page.

**Architecture:** Extend the closed `GrimoireWorkKind` vocabulary, then put producer-local work leases before scopes and atomic effect groups around the existing durable units. A bidirectional Roslyn inventory proves all 23 application hosted-service registrations and every relevant scope/open/provider/filesystem site, while explicit queue and task ownership preserves identity across maintenance instead of converting refusal into failure.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core Generic Host, EF Core, SQLCipher, Microsoft.Extensions.AI, xUnit, Roslyn syntax analysis, `TaskCompletionSource` barriers, Git, and GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-06-issue-256-hosted-producer-admission-design.md`

## Global Constraints

- Work only on `codex/issue-256-hosted-producer-admission`, based on `grimoire-fixes` commit `6d50266a`, until the reviewed merge step.
- Commit this implementation plan and its approved design corrections before Task 1. They are frozen
  implementation inputs and are not edited after feature-tip qualification.
- Preserve `SessionAttachmentIndexing = 1`, `EntryWeaving = 2`, and `SagaExtraction = 3`; every new `GrimoireWorkKind` is non-zero and accepted by the gate's exact closed list.
- Acquire ordinary work before the first owned DI scope or effect. Declare leases before scopes so reverse disposal closes scopes before releasing work.
- Never pass `IGrimoireWorkLease.MaintenanceRevocation` to providers, filesystem operations, compensation writes, or existing host/operator cancellation paths.
- A refused lease or effect group is maintenance deferral: do not increment attempts, begin/complete accounting, advance watermarks/cursors/checkpoints, write failure/cancellation/reconciliation state, or discard queue identity.
- An effect group that wins spans its external effect and durable disposition. The owning work lease remains held through any DB-only suffix and all owned scope disposal; the existing five-second drain timeout remains unchanged.
- `BatchProcessingService` uses one effect group per existing 64-line accounting page, plus a separate artifact-publication group. Advancing the encrypted input enumerator is repeatable read-only preparation under the page's work lease.
- Startup exceptions must be exact: blocking MCP startup, Batch recovery, and the first long-running-operation pass are awaited pre-readiness; runtime continuations use ordinary admission.
- Offline recovery classification is keyed by operation kind plus checkpoint version and owner/journal evidence. Descriptor names and class names do not confer authority.
- Use deterministic barriers, never sleeps, for ordering tests. A synchronously blocked participant must run on a dedicated `TaskCreationOptions.LongRunning` thread.
- Every ordinary producer task pins the applicable closed-gate, frontier-lost, frontier-won, reopen, `KeepClosed`, host/operator cancellation, and genuine-effect-failure cases. For DB-only producers, frontier cases reduce to admission plus scope-disposal drain; for periodic producers with no retained identity, reopening is the next ordinary cadence rather than an extra task.
- RED -> GREEN -> REFACTOR for every production increment. Run the new test and inspect its expected failure before editing production.
- Add no HTTP/CLI/config/schema contract, migration, `ErrorCodes` member, public status, provider-selection change, prompt change, retention-policy change, Tapestry algorithm change, or Batch accounting-policy change.
- Follow the repository C# style: one blank line after each line of code, file-scoped namespaces, primary constructors for DI, and zero build warnings.
- `docs/Arcanum.DESIGN.md` owns architecture/testing, `docs/Arcanum.Engineering.md` owns the inventory obligation, and the root `README.md` changes only for the intentional hosted-maintenance statement. API, command-reference, and Compendium docs do not change.
- Issue #256 closes after verified integration. Parent #239 remains open for #257.

## Baseline Evidence

- `dotnet build RetroDownfall.Arcanum.slnx` passed from the clean base with zero warnings and zero errors.
- One full-suite baseline run timed out only at `DataRetentionLeaseMaintainerTests.RunAsync_WhenTerminalActionCompletesDuringRenewal_ReturnsTheTerminalResult`; the exact test passed 20/20 in isolation and the class passed as a unit. Treat recurrence as a test-scheduling investigation, not permission to change production or lengthen its timeout blindly.

## File and Responsibility Map

- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs` — the exact hosted work-kind enum.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs` — validation and admission for the closed work-kind set.
- `tests/RetroDownfall.Arcanum.Tests/Support/RecordingGrimoireWorkAdmissionGate.cs` — reusable deterministic observation wrapper for lease/effect ordering in producer tests.
- `tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs` — the 23-service catalog, exact operation/site identities, authority proofs, and Roslyn discovery/validation.
- `tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs` — bijection, failure-mode, ordinary-frontier, lifecycle, recovery, and backup caller-authority assertions.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs` and `src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs` — admitted scheduled/watcher/on-demand indexing with owned task and exact file frontier.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/TapestryWeavingService.cs` — admitted sweep and one frontier per staged Tapestry generation.
- `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs` — admitted discovery, retained batch identity, 64-line page frontiers, and artifact frontier.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/UnseenServantService.cs` — admitted hydration/cleanup and retained due-job identities.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs` — bounded admitted units and corrected Simulacrum checkpoint ordering.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionSweepHostedService.cs` and `DataRetentionService.Pruning.cs` — hosted-only admission and one frontier per prune candidate.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/CampaignLoggerQueue.cs` and `Loremaster.cs` — explicit conclude/re-signal protocol and one frontier per Session summary.
- `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingLeaseRenewer.cs`, `Covenant/CovenantMaintenanceHostedService.cs`, and `Covenant/GrimoireSchemaTransitionHostedService.cs` — bounded DB-only admitted passes.
- `src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbeService.cs` — one admitted effect group per provider observation.
- `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpConnectionManager.cs`, `Mcp/ConnectionManager/McpConnectionManager.Partitions.cs`, and `Mcp/McpServerBootstrapHostedService.cs` — shared initializer owns its real authority/task lifetime through shutdown.
- `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — aliases narrow internal retention/MCP capabilities to their concrete scoped/singleton owners without widening public contracts.
- `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationStartupHostedService.cs`, `LongRunningOperationReconciler.cs`, new `LongRunningOperationRecoveryAdmission.cs`, and `GrimoireTransitions/GrimoireOfflineTransitionStartupRecovery.cs` — admitted runtime discovery, exact per-operation authority classification, and authenticated owner evidence.
- `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Engineering.md`, `README.md`, and the #239 design — owning documentation and delivery status.

---

### Task 1: Close the Work-Kind Vocabulary and Add Shared Test Observation

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/RecordingGrimoireWorkAdmissionGate.cs`

**Interfaces:**

- Produces the 13 new enum members named by the spec and accepted by `TryAcquireWorkLease`.
- Produces a test-only wrapper exposing requested kinds, scope/effect barrier callbacks, and inner leases without changing production interfaces.

- [ ] **Step 1: Write the closed-vocabulary RED tests**

Add `ExistingWorkKindValuesRemainStable`, `EveryHostedWorkKindIsAdmittedWhileOrdinary`, and `ZeroAndUndefinedWorkKindsAreRejected`. The admission theory uses this exact member sequence:

```csharp
public static TheoryData<GrimoireWorkKind> HostedWorkKinds => new()
{
    GrimoireWorkKind.SessionAttachmentIndexing,
    GrimoireWorkKind.EntryWeaving,
    GrimoireWorkKind.SagaExtraction,
    GrimoireWorkKind.WorkspaceIndexing,
    GrimoireWorkKind.TapestryWeaving,
    GrimoireWorkKind.BatchProcessing,
    GrimoireWorkKind.UnseenServant,
    GrimoireWorkKind.ApprenticeExecution,
    GrimoireWorkKind.DataRetentionSweep,
    GrimoireWorkKind.LoremasterSummarization,
    GrimoireWorkKind.A2ASendingLeaseRenewal,
    GrimoireWorkKind.CovenantMaintenance,
    GrimoireWorkKind.GrimoireSchemaTransition,
    GrimoireWorkKind.LongRunningOperationRecovery,
    GrimoireWorkKind.ProviderHealthProbe,
    GrimoireWorkKind.McpServerBootstrap,
};
```

- [ ] **Step 2: Run the focused tests and inspect RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionGateTests"
```

Expected: compile failure for the missing enum members, followed after enum declaration by `ArgumentOutOfRangeException` from the gate until its closed list changes.

- [ ] **Step 3: Add the minimal closed enum and gate validation**

Assign explicit byte values 4 through 16 in the order above. Replace the current three-member conditional with an exhaustive `switch` expression that returns `true` only for the 16 named values; throw `ArgumentOutOfRangeException` for zero or undefined bytes.

- [ ] **Step 4: Add the recording wrapper**

Implement `IGrimoireConnectionAdmissionGate` by forwarding every member to an inner gate. Wrap admitted work leases and effect groups so tests can synchronously count attempts and asynchronously block disposal:

```csharp
internal sealed class RecordingGrimoireWorkAdmissionGate(
    IGrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
{
    internal List<GrimoireWorkKind> RequestedWorkKinds { get; } = [];

    internal int EffectGroupAttempts { get; private set; }

    internal Func<ValueTask> BeforeEffectGroupDisposalAsync { get; set; } =
        static () => ValueTask.CompletedTask;

    public bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease)
    {
        RequestedWorkKinds.Add(kind);

        if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
        {
            lease = null;

            return false;
        }

        lease = new RecordingWorkLease(this, admitted!);

        return true;
    }
}
```

The wrapper must not invent admission behavior; closed/open state remains owned by a real `GrimoireConnectionAdmissionGate` or an explicit refusing test gate.

- [ ] **Step 5: Run GREEN and commit**

Run the focused gate tests and `git diff --check`, then commit:

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs tests/RetroDownfall.Arcanum.Tests/Support/RecordingGrimoireWorkAdmissionGate.cs
git commit -m "feat: add hosted Grimoire work kinds"
```

### Task 2: Establish the Bidirectional Inventory RED

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs`
- Read: `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs`
- Read: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Read: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`

**Interfaces:**

- Produces `HostedProducerAuthorityKind`, `HostedProducerServiceEntry`, `HostedProducerOperationEntry`, and `HostedProducerSite` test records.
- Produces discovery functions whose outputs are compared bijectively with the catalog; producer tasks do not edit these shared files.

- [ ] **Step 1: Define exact inventory records**

Use positional records with exact source-relative paths and member/site identities:

```csharp
internal enum HostedProducerAuthorityKind : byte
{
    OrdinaryHostedWork = 1,
    FiniteRequest = 2,
    PreReadinessStartup = 3,
    OwnerBoundRecovery = 4,
    StoppedHost = 5,
    OwnerBoundMaintenance = 6,
    EffectFree = 7,
}

internal enum HostedProducerSiteKind : byte
{
    ScopeCreation = 1,
    OrdinaryConnectionRoute = 2,
    ProviderCall = 3,
    FileSystemRead = 4,
    FileSystemEffect = 5,
    EffectFrontier = 6,
}

internal sealed record HostedProducerSite(
    string RootType,
    string OperationId,
    string SourcePath,
    string EnclosingType,
    string Member,
    HostedProducerSiteKind Kind,
    string Callee);

internal sealed record HostedProducerOperationEntry(
    string OperationId,
    string SourcePath,
    string EnclosingType,
    string Member,
    HostedProducerAuthorityKind Authority,
    GrimoireWorkKind? WorkKind,
    string? Proof,
    IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerServiceEntry(
    string ServiceType,
    IReadOnlyList<HostedProducerOperationEntry> Operations);

internal sealed record NonHostedProducerChainEntry(
    string ChainId,
    string SourcePath,
    string EnclosingType,
    string Member,
    HostedProducerAuthorityKind Authority,
    string Proof,
    IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerInventoryDiagnostic(
    string Code,
    string Identity,
    string Detail);

internal sealed record HostedProducerInventoryValidation(
    IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
{
    internal bool IsValid => Diagnostics.Count == 0;
}

internal sealed record HostedProducerDiscovery<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics);
```

Reject empty identities, directory/namespace wildcards, duplicate service/site keys, missing nonordinary proof, shared proof, nonordinary work kinds, and ordinary entries without a work kind.

The service catalog contains these exact 23 unwrapped application registrations, once each:

```csharp
private static readonly string[] ExpectedHostedServices =
[
    "GrimoireDatabaseHostedService",
    "CovenantFeatureConfigurationPublisher",
    "PidFileService",
    "LongRunningOperationStartupHostedService",
    "SessionAttachmentPendingGcHostedService",
    "CovenantMaintenanceHostedService",
    "GrimoireSchemaTransitionHostedService",
    "EntryWeavingService",
    "SessionAttachmentIndexingService",
    "WorkspaceIndexingService",
    "SagaExtractionService",
    "TapestryWeavingService",
    "ArcanumSettingsClampStartupLogger",
    "ArcanumSecurityStartupChecks",
    "FileEncryptionKeyBootstrapHostedService",
    "DataRetentionSweepHostedService",
    "A2ASendingLeaseRenewer",
    "Loremaster",
    "ApprenticeService",
    "McpServerBootstrapHostedService",
    "ProviderHealthProbeService",
    "UnseenServantService",
    "BatchProcessingService",
];
```

- [ ] **Step 2: Add registration and site discovery**

Resolve every semantically bound invocation of
`Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions.AddHostedService`
by comparing `(bound.ReducedFrom ?? bound).OriginalDefinition` with the framework's one-argument and
factory overloads, and take its inferred or explicit implementation type from the single
`bound.TypeArguments` value, independent of whether the factory body uses `GetRequiredService<T>`, `new T(...)`, a helper
method, or a typed factory local. Resolve closed generic arguments to
`AddInstallationResetRecoveryAwareHostedService<T>` the same way. The one open
`AddHostedService` invocation inside that helper's declaration is not counted as an application
registration: validate separately that the helper contains exactly one wrapper registration of
`InstallationResetRecoveryAwareHostedService<TService>` and emit
`HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED` if it drifts; each closed helper call contributes its
unwrapped `T` to the application catalog. An unresolved invocation, open type,
non-named type, or factory whose bound return type is not one concrete application `IHostedService`
emits `HOSTED_REGISTRATION_UNSUPPORTED_SHAPE`; it is never silently skipped. Fixtures cover the current
two `GetRequiredService<T>` registrations plus `new`, helper-method, and typed-local factory shapes,
as well as a mutated wrapper-helper body.

Expose these exact scanner and validator signatures:

```csharp
internal static HostedProducerDiscovery<string> DiscoverApplicationHostedServices(
    IReadOnlyList<CSharpCompilation> compilations);

internal static HostedProducerDiscovery<HostedProducerSite> DiscoverProducerSites(
    IReadOnlyList<CSharpCompilation> compilations,
    HostedProducerDiscovery<string> registrations);

internal static IReadOnlyList<HostedProducerServiceEntry> Catalog { get; }

internal static IReadOnlyList<NonHostedProducerChainEntry> NonHostedCatalog { get; }

internal static HostedProducerInventoryValidation Validate(
    IReadOnlyList<HostedProducerServiceEntry> catalog,
    IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog,
    HostedProducerDiscovery<string> registrations,
    HostedProducerDiscovery<HostedProducerSite> discoveredSites);

internal static HostedProducerInventoryValidation ValidateProductionTree();
```

`ValidateProductionTree` builds the Infrastructure, Api, and Cli source compilations; Batch's hosted
registration lives in Api and the live Backup command root lives in Cli, so neither may disappear
because only one project was scanned. `OperationId`
is an exact branch/root call-site identity, not a descriptive label. It distinguishes mixed authority
inside one member (for example MCP blocking versus nonblocking `StartAsync`, and LRO pre-readiness
versus periodic reconciliation). Normalize a site as
`rootType|operationId|path|type|member|kind|callee`; the same physical collaborator reached by two
authority roots therefore produces two independently validated sites rather than a false duplicate.
`NonHostedProducerChainEntry` is deliberately outside the 23-service registration bijection. It gives
the required Backup CLI, restore-safety, stopped-host, and owner-bound chains their own exact roots and
participates in site/proof bijection without pretending any of them is a hosted-service registration.

The symbol-based provider vocabulary is this closed set (concrete implementation calls normalize to
their interface slot):

```text
Microsoft.Extensions.AI.IChatClient.GetResponseAsync
Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync
Microsoft.Extensions.AI.IEmbeddingGenerator`2.GenerateAsync
RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteBufferedAsync
RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteStreamingAsync
RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.ExecutePromptAsync
RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.StreamPromptAsync
RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync
RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedBatchAsync
RetroDownfall.Arcanum.Core.Weave.Tapestry.ITapestrySummarizer.SummarizeAsync
RetroDownfall.Arcanum.Core.Resilience.IProviderHealthProbe.ProbeAsync
RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner.RunScheduledAsync
RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonJob.RunAsync
RetroDownfall.Arcanum.Infrastructure.Weave.TapestryWeaver.WeaveAsync
RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync
RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync
RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync
RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync
RetroDownfall.Arcanum.Api.OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync
RetroDownfall.Arcanum.Infrastructure.A2A.IA2AClientService.CancelRemoteTaskAsync
```

The closed filesystem/read wrapper set is:

```text
RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.OpenReadAsync
RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.InspectAsync
RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.HasEnvelope
RetroDownfall.Arcanum.Core.Storage.EncryptedBlobStoreCompatibilityExtensions.OpenCompatibleReadAsync
RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.OpenReadAsync
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCapturePath
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCaptureOpenFile
RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck
RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetPathIdentity
RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetHandleIdentity
RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.RunStartupPermissionSelfCheck
RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory.Create
System.IO.Directory.Exists
System.IO.Directory.EnumerateFileSystemEntries
System.IO.Directory.ResolveLinkTarget
System.IO.File.Exists
System.IO.File.GetAttributes
System.IO.File.ReadAllText
System.IO.FileInfo.Length
System.IO.FileInfo.LastWriteTimeUtc
```

The closed filesystem/effect wrapper set is:

```text
RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.WriteAsync
RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync
RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync
RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.ReconcileAsync
RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyProvider.GetForWriteAsync
RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RotateAsync
RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RetireAsync
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDelete
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined
RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryRestoreQuarantined
RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync
RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync
RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete
RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync
RetroDownfall.Arcanum.Core.DataLifecycle.IDataRetentionService.ApplyAsync
RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync
RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync
RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync
RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync
RetroDownfall.Arcanum.Core.Storage.IUploadedFileRepository.CreateForOwnedFileAsync
System.IO.Directory.CreateDirectory
System.IO.File.WriteAllText
System.IO.File.Delete
System.IO.File.Move
```

Treat every mutation-capable `SecureFilePermissions` member as one closed family:
`EnsureOwnerOnlyDirectoryExists`, `ApplyOwnerOnlyFile`, `ApplyOwnerOnlyDirectory`,
`CreateOwnerOnlyDirectoryAtPath`, `TryEnsureOwnerOnlyDirectoryExistsStrict`,
`TryApplyOwnerOnlyFileStrict`, `CreateOwnerOnlyTempFile`, `ApplyOwnerOnlyToSensitivePaths`, and
`TryApplyUnixFileMode`. The exact nonordinary/aggregate boundaries are
`IInstallationResetStartupRecovery.RecoverBeforeBootstrapAsync`,
`IGrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync`,
`GrimoireDatabaseBootstrapper.EnsureInitializedAsync`,
`LongRunningOperationReconciler.ReconcileAsync`,
`SessionAttachmentIndexProcessor.ProcessAsync`, and
`IBatchRecoveryService.ReconcileStrandedAsync`; each requires an exact lifecycle or nested
frontier-contract proof and cannot inherit authority from its namespace or type name.

Start semantic traversal at every discovered unwrapped service's authored `StartAsync`, `ExecuteAsync`,
and `StopAsync` roots; every exact externally invoked hosted operation catalogued by a service entry,
including workspace `QueueIndexNow`; and every exact `NonHostedProducerChainEntry` root, including
`BackupCommands.Create`. Preserve the exact branch/root call-site `OperationId` and follow first-party
calls across all three compilations through catalogued collaborators. Validation emits
`HOSTED_ROOT_UNRESOLVED` when any declared external or non-hosted root cannot be bound and
`HOSTED_EXTERNAL_OPERATION_UNCATALOGUED` when an externally invoked operation reaches a sensitive
site without its own catalogued root. Discover the latter independently by scanning every production
invocation whose symbol belongs to an unwrapped hosted type, or to an interface mapped to that
singleton by the production DI registrations, when the call originates outside the hosted lifecycle
traversal. Traverse that authored operation even before catalog comparison so a newly added endpoint
or collaborator call cannot evade the diagnostic merely because no root was declared. A high-level
provider/filesystem call is a leaf only when the
caller owns the effect group spanning that call and durable disposition. A callee-owned aggregate
(retention, LRO reconciliation, MCP, or recovery) must be traversed or carry an exact separately tested
frontier-contract proof; otherwise emit `HOSTED_AGGREGATE_PROOF_MISSING`. An interface/dynamic target
without an exact production DI binding or declared boundary emits `HOSTED_CALL_TARGET_UNRESOLVED`.

Discover `CreateScope`/`CreateAsyncScope`, property references, invocations, object creations,
`using`/`await using` disposal, every symbol in the closed sets, and joins to marked
connection-acquisition routes. A `FileStream`/`File.Open` construction is read-only only when constant
arguments prove `FileMode.Open` plus `FileAccess.Read`; unknown or mutating modes are effects or
`HOSTED_SITE_UNCLASSIFIED`. Treat `IEncryptedBlobStore.CreateWriterAsync` through
`EncryptedBlobWriter.CompleteAsync` as one aggregate publication region: it includes intermediate
`StreamWriter` write/flush calls and implicit or explicit disposal/cleanup, and validation requires
both endpoints under the same effect group. `SensitiveBoundaryTypes` is the exact union of the
containing types above plus `System.IO.File`, `Directory`, `FileStream`, `FileInfo`, and
`StreamWriter`; any reachable member not classified by these rules emits
`HOSTED_SITE_UNCLASSIFIED` rather than disappearing.

A fixture exercises every vocabulary member and each traversal rule. A second same-helper fixture
reaches one physical site from two authority roots and must yield two contextual identities. Source
discovery is complemented by a runtime `IServiceCollection` descriptor-count assertion so manual
`IHostedService` descriptors cannot evade the source-registration scanner.

- [ ] **Step 3: Add independent validator-failure tests**

Create one fixture mutation per failure: new/stale service, duplicate service, new/stale/duplicate site,
broad identity, missing/shared proof, wrong/missing work kind, external effect without
`TryBeginExternalEffectGroup`, unresolved/lookalike/open/abstract hosted registration,
`HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED`, `HOSTED_CALL_TARGET_UNRESOLVED`,
`HOSTED_AGGREGATE_PROOF_MISSING`, and reachable sensitive-type member outside the closed vocabulary.
Also mutate one declared external/non-hosted root so it emits `HOSTED_ROOT_UNRESOLVED`, and add an
endpoint-invoked sensitive operation without a root so it emits
`HOSTED_EXTERNAL_OPERATION_UNCATALOGUED`.
Add positive registration fixtures for direct generic, explicit/inferred factory, method group, typed
`Func`, fully-qualified static extension, and one-time reset-aware unwrapping. Each test asserts its
exact diagnostic code so one gap cannot mask another.

- [ ] **Step 4: Add the real-tree umbrella RED**

Seed all 23 service names and exact lifecycle operations from the spec, but leave ordinary frontier evidence to discovery. Add these real-tree assertions:

```csharp
[Fact]
public void EveryApplicationHostedServiceHasExactlyOneEntry()
{
    HostedProducerInventoryValidation validation =
        HostedGrimoireProducerInventory.ValidateProductionTree();

    Assert.DoesNotContain(
        validation.Diagnostics,
        static diagnostic => diagnostic.Code.StartsWith("HOSTED_SERVICE_", StringComparison.Ordinal));
}

[Fact]
public void EveryDiscoveredProducerSiteIsCataloguedExactlyOnce()
{
    HostedProducerInventoryValidation validation =
        HostedGrimoireProducerInventory.ValidateProductionTree();

    Assert.DoesNotContain(
        validation.Diagnostics,
        static diagnostic => diagnostic.Code.StartsWith("HOSTED_SITE_", StringComparison.Ordinal));
}
```

- [ ] **Step 5: Run and preserve the expected umbrella RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests"
```

Expected: registration counts and fixture validators pass; real-tree ordinary-frontier assertions list the currently unprotected producer sites. Do not commit the umbrella RED by itself and do not let delegated producer tasks edit these files.

### Task 3: Admit and Own Workspace Indexing

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Weave/IWorkspaceIndexingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/WorkspaceIndexingServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/WorkspaceReindexEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs`

**Interfaces:**

- `QueueIndexNow(string workspacePath)` retains the endpoint's immediate-response contract and transfers task ownership to the service.
- Scheduled, watcher, and queued on-demand work use `GrimoireWorkKind.WorkspaceIndexing`; one changed file is one external-effect group.

- [ ] **Step 1: Write and run the first RED**

Add `ClosedGateCreatesNoWorkspaceScope` using a scope factory that increments before returning. Call the smallest scheduled/watcher helper and assert zero scopes and `WorkspaceIndexing` was requested. Run `WorkspaceIndexingServiceTests`; expect a scope count of one before production admission exists.

- [ ] **Step 2: Add the lease before every workspace-owned scope**

Read `CurrentGeneration`, call `TryAcquireWorkLease(WorkspaceIndexing, out lease)`, return a typed local deferred result on refusal, and declare `await using IGrimoireWorkLease admitted = lease!;` before `AsyncServiceScope`.

- [ ] **Step 3: Pin the file frontier and snapshot restoration**

Add deterministic tests `RefusedChangedFilePreservesSnapshotAndResignalsOnce` and `WinningFileGroupDrainsThroughMetadataOrRollback`. Begin the effect group immediately before `IndexFileAsync` opens the file, then hold it through every embedding page and DB publication/rollback. Dispose the group, then the shared scope, while the work lease continues to block closure. On refusal, merge the snapshot's reconciliation bit and path/action pairs back into `PendingWorkspaceChanges`, wait on the predecessor generation, then signal exactly once.

- [ ] **Step 4: Move detached on-demand work into service ownership**

Add `OnDemandIndexIsServiceOwnedBeforeEndpointReturns`,
`SynchronousCompletionDoesNotLeaveStaleOnDemandEntry`, `RejectedPathDoesNotStartOrThrow`,
`StopObservesOnDemandWork`, and `ConcurrentQueueAndStopCannotLeaveUnobservedTask`. Replace
endpoint-owned `Task.Run` with `QueueIndexNow`; preserve the current validation warning/return path,
store one tracked task per normalized workspace, observe completion/faults, and remove it only in its
completion callback. Guard enqueue publication with a private `_onDemandAdmissionLock` and
`_acceptOnDemand` state. `StopAsync` takes that same lock, atomically changes the state to not
accepting, snapshots every already-published task, then releases the lock, cancels the host lifetime,
and awaits the snapshot. A racing `QueueIndexNow` therefore either publishes before the closed
snapshot and is drained or observes closed admission and returns without starting work; it can never
publish after the snapshot.

Minimal ownership shape:

```csharp
public void QueueIndexNow(string workspacePath)
{
    Result<string> validated = TryValidateWorkspacePath(workspacePath);

    if (validated.IsFailure)
    {
        logger.LogWarning(
            "On-demand workspace re-index rejected for {WorkspacePath}: {Reason}",
            workspacePath,
            validated.Error.Message);

        return;
    }

    string normalized = validated.Value;

    lock (_onDemandAdmissionLock)
    {
        if (!_acceptOnDemand)
        {
            return;
        }

        TaskCompletionSource<Task> handle = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_onDemandTasks.TryAdd(normalized, handle.Task.Unwrap()))
        {
            return;
        }

        Task work = RunTrackedOnDemandAsync(normalized, _serviceStopping.Token);

        handle.SetResult(work);
    }
}
```

The tracked body removes the dictionary entry by exact key/task identity in `finally`; publishing the
placeholder before starting the body prevents a synchronous completion from being resurrected. The
race test parks queue publication while `StopAsync` attempts to close admission, then exercises both
linearization orders and proves shutdown cannot finish with an unobserved task.

- [ ] **Step 5: Run GREEN and commit**

Run the workspace service, endpoint, and affected intelligence-provider test classes. Commit with `feat: admit and own workspace indexing work`.

### Task 4: Admit Tapestry Generations

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/TapestryWeavingService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestryWeaverTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestryWeavingAdmissionTests.cs`

**Interfaces:**

- `RunSweepAsync` acquires one `TapestryWeaving` lease before its sweep scope.
- Each `TapestryScope` begins exactly one sequential group before `WeaveAsync` can stage a generation.

- [ ] **Step 1: RED closed and frontier-lost cases**

Add `ClosedGateCreatesNoTapestrySweepScope` and `RevocationBeforeGenerationCreatesNoStagedGeneration`. Assert zero scope creation for lease denial, and zero `BeginGenerationAsync` calls when the effect group is refused.

- [ ] **Step 2: GREEN lease and group placement**

Acquire the sweep lease before the `AsyncServiceScope`. Pass it to the per-scope loop and begin the group before calling `TapestryWeaver.WeaveAsync`; return a local deferred outcome without abandoning a generation because no generation may yet exist.

- [ ] **Step 3: RED/GREEN winning frontier**

Add `WinningGenerationDrainsThroughPublicationAndScopeDisposal` and `DeferralKeepsPreviousGenerationCurrent`. Block publication and scope disposal independently; dispose the generation group after publication, then prove the still-held work lease prevents closure from entering closed state until the shared sweep scope releases. Preserve the previous complete generation for the refused case.

- [ ] **Step 4: Verify and commit**

Run both Tapestry test classes and gate tests. Commit with `feat: admit Tapestry generation effects`.

### Task 5: Admit Batch Accounting Pages and Artifact Publication

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/BatchProcessingServiceTests.cs`
- Verify unchanged startup semantics: `tests/RetroDownfall.Arcanum.Tests/Intelligence/BatchRecoveryServiceTests.cs`

**Interfaces:**

- Runtime discovery and retained batches use `BatchProcessing` leases; startup reconciliation remains pre-readiness.
- Each existing 64-line page owns one group around preparation, accounting, line scopes/providers/checkpoints, and accounting completion.
- Artifact publication owns a separate group around directory/staging/move/permissions/file row/final status/cleanup.

- [ ] **Step 1: RED admission before scopes**

Add `ClosedGateCreatesNoBatchTickOrBatchScope`; assert `TickAsync` and a retained in-flight batch create no scope when denied and request only `BatchProcessing`.

- [ ] **Step 2: RED exact page group count and accounting order**

Create 65 pending input lines. Add a recording accounting handle and effect-group observer. Assert two page groups, the first covers 64 lines, the second covers one, each group starts before `BeginBatchAsync`, and disposal occurs after `CompleteAsync` plus every private line scope.

- [ ] **Step 3: GREEN page loop**

Materialize the next encrypted input page under the work lease. Before `PreparePendingPageAsync` and `BeginBatchAsync`, call `TryBeginExternalEffectGroup`; on refusal return `DeferredForMaintenance`. Keep the group through `RunRequestLinesAsync`, terminal checkpoints, cancellation watcher teardown, `CompleteAsync`, and page-owned scope disposal.

One admitted retained-batch iteration may keep its lease and encrypted enumerator across consecutive
pages while the gate remains open. When the next page frontier is refused, unwind the async enumerator,
encrypted stream, page/batch scopes, and then the work lease before awaiting reopening. On reacquisition
create a new enumerator, rescan deterministically from physical line one, and let terminal line
checkpoints skip completed pages/lines before any provider or accounting call. Never retain the current
enumerator or blob-store scope across the lease-free wait.

The disposition is local and explicit:

```csharp
private enum BatchProcessingDisposition : byte
{
    Concluded = 1,
    DeferredForMaintenance = 2,
}
```

- [ ] **Step 4: RED/GREEN retained `InProgress` identity**

Add `RevocationBetweenPagesPreservesInProgressIdentity`,
`DeferralDisposesInputStreamBeforeReleasingLeaseAndWait`,
`ReopenRecreatesEnumeratorAndSkipsTerminalLines`, `ReopenResumesWithoutProviderReplay`, and
`StopDrainsRetainedBatchTask`. When deferred, keep the same `_inFlight` task/batch id, dispose the
enumerator/stream and scopes before the lease, await reopening without either, reacquire, and resume
from line checkpoints. Assert a second blob stream is opened, no new paid attempt occurs, and none of
the completed 64 provider calls or their accounting is replayed.

- [ ] **Step 5: RED/GREEN artifact frontier**

Add `ArtifactFrontierRefusalPreservesLineCheckpoints` and `ArtifactGroupStartsBeforeDirectoryCreationAndEndsAfterFinalStatus`. Move `EnsureOwnerOnlyDirectoryExists` inside this group. Refusal regenerates from checkpoints after reopen without provider calls; winning authority spans `BatchJsonlWriters.CreateAsync`, encryption, move, owner-only permissions, file-row insertion, final Batch status, and cleanup.

- [ ] **Step 6: Verify and commit**

Run Batch processing, recovery, accounting, and gate tests. Commit with `feat: defer batch pages during Grimoire maintenance`.

### Task 6: Admit Unseen Servant Jobs

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/UnseenServantService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/UnseenServantServiceTests.cs`
- Verify: `tests/RetroDownfall.Arcanum.Tests/Hosting/UnseenServantJobTrackerTests.cs`

**Interfaces:**

- Hydration and idempotency cleanup are DB-only `UnseenServant` units.
- One due daemon job owns a group through runner completion, in-memory completion, and watermark persistence.

- [ ] **Step 1: RED post-yield startup admission**

Add `DeniedHydrationCreatesNoScopeOrFallbackWarning` and `DeniedCleanupDoesNotStampCadence`. Start the service against a closed gate; prove the methods that run after `Task.Yield` are ordinary and produce neither scope nor warning/error side effects.

- [ ] **Step 2: GREEN DB-only units**

Acquire `UnseenServant` before the hydration and cleanup scopes, and return to cadence on refusal without changing cleanup timestamps.

- [ ] **Step 3: RED/GREEN one retained job**

Add `DeniedJobRetainsKeyWithoutWatermark`, `WinningJobDrainsThroughWatermark`, `ReopenRunsSameJobOnce`, and `KeepClosedJobWaitIsOwnedByStop`. Keep the same job/key in `_activeJobTasks`; release the lease before awaiting reopening; do not allocate new jitter or record a result when the frontier loses.

- [ ] **Step 4: Verify and commit**

Run the Unseen service, pacer, job tracker, and gate tests. Commit with `feat: admit Unseen Servant jobs`.

### Task 7: Admit Apprentice Units and Correct Simulacrum Ordering

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/ApprenticeServiceReliabilityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/ApprenticeCheckpointDelegationChainTests.cs`

**Interfaces:**

- Crash recovery is DB-only `ApprenticeExecution`; each active task retains generation and queue/concurrency ownership across denial.
- Plan, serial step, and complete Simulacrum are separate bounded effect units with fresh scopes.

- [ ] **Step 1: Write the ordering bug RED first**

Add `SimulacrumStampsChildrenAndShiftsFateBeforeCheckpointAdvance`. Block `StampCastSendingsAsync` and `AttemptShiftingFateAsync`; observe the repository and assert `CurrentStep` and the group checkpoint remain unchanged until both post-effects finish. Confirm it fails against the current early checkpoint.

- [ ] **Step 2: Apply only the ordering correction**

In `ExecuteSimulacrumGroupAsync`, stamp every child and perform optional Shifting Fate before calling `CompleteStepAsync` or writing the final group checkpoint. Run the single test to GREEN before adding admission.

- [ ] **Step 3: RED bounded scopes and denial identity**

Add `DeniedRecoveryCreatesNoScope`, `DeniedPlanOrStepPreservesGenerationAndQueueIdentity`, and `DeniedNextStepResumesOnce`. Assert the existing whole-run scope cannot be created before admission and that denial changes no current step, plan, receipt, or execution generation.

- [ ] **Step 4: GREEN unit loop**

Replace the whole-run scope with fresh scopes acquired only after a work lease. Begin a group for plan generation, each serial attempt, and the complete Simulacrum. Dispose scope/lease, await the predecessor generation, then continue the same tracked execution task.

- [ ] **Step 5: RED/GREEN winner, shutdown, and failure distinctions**

Add `WinningStepDrainsThroughCheckpointAndScopeDisposal` and `KeepClosedWaitIsObservedByStop`; retain existing host cancellation and genuine failure tests to prove neither becomes maintenance deferral.

- [ ] **Step 6: Verify and commit**

Run both Apprentice classes, checkpoint tests, and gate tests. Commit with `feat: admit Apprentice execution units`.

### Task 8: Admit Automatic Retention Candidates

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionHostedSweepOutcome.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationOwnership.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionSweepHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Operations/ILongRunningOperationSameOwnerLeaseResumption.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionSweepHostedServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionDurabilityBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/FakeLongRunningOperationStore.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationOwnershipTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`

**Interfaces:**

- Request-driven retention remains finite-request protected.
- The hosted path passes an admitted work lease internally; the durable pending journal is prepared first, then one candidate group covers mutation, proof, disposition/cursor, and journal clearing.
- `DataRetentionHostedSweepContinuation(Guid OperationId, string OwnerId, Guid OwnershipToken)` is retained by the hosted task across scope/lease disposal. `DataRetentionHostedSweepOutcome` is either `Concluded` or `DeferredForMaintenance` with that continuation.
- A narrow internal `ILongRunningOperationSameOwnerLeaseResumption` capability refreshes the exact same owner's lease after reopening without changing `AttemptCount`; the hosted path may call it only while `LongRunningOperationOwnership.IsClaimedBy(operationId, ownershipToken)` is true. The public `ILongRunningOperationStore` contract does not change.

The new declarations are exact:

```csharp
internal enum DataRetentionHostedSweepDisposition : byte
{
    Concluded = 1,
    DeferredForMaintenance = 2,
}

internal sealed record DataRetentionHostedSweepContinuation(
    Guid OperationId,
    string OwnerId,
    Guid OwnershipToken);

internal sealed record DataRetentionHostedSweepOutcome(
    DataRetentionHostedSweepDisposition Disposition,
    DataRetentionHostedSweepContinuation? Continuation,
    Result<DataRetentionApplyResult>? Result);

internal interface ILongRunningOperationSameOwnerLeaseResumption
{
    Task<bool> ResumeSameOwnerLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
}
```

`LongRunningOperationStore` implements both the ordinary Core store contract and this internal
capability. Register the capability as a scoped alias to the same concrete store instance. Inject it
as the final optional dependency of `DataRetentionService` so existing direct-construction tests remain
source-compatible; the hosted resume path fails closed when the capability is absent. A dedicated
retention fake implements the narrow capability for focused tests. No existing store decorator or
unrelated test double acquires the ability to revive an expired same-owner lease.

- [ ] **Step 1: RED host denial before scope**

Add `ClosedGateSkipsAutomaticSweepBeforeScope` and assert zero policy-store/service scope calls, no failure log, and no cadence corruption. Add `KeepClosedRetainedSweepWaitIsCancelledByStop` for a sweep that already owns a durable operation.

- [ ] **Step 2: GREEN host lease and hosted-only authority flow**

Make `RunOnceAsync` an exact same-operation loop. It carries a nullable `DataRetentionHostedSweepContinuation`, reads the predecessor generation, acquires `DataRetentionSweep` before `CreateAsyncScope`, and calls `DataRetentionService.ApplyOrResumeHostedPruneAsync(continuation, lease, cancellationToken)`. A fresh denial returns to cadence because no operation exists; a post-start deferral disposes scope/lease, waits for reopening, and reacquires for the same continuation instead of invoking public `ApplyAsync` again.

- [ ] **Step 3: RED candidate frontier and same-owner capability**

Before changing production, add `RefusedCandidateKeepsPendingJournalAndCandidateCursor`,
`WinningCandidateDrainsThroughFilesystemDisposition`, `MaintenanceDeferralIsNotReconciliationRequired`,
`ReopenResumesSameOperationWithoutAttemptIncrement`, `ExpiredLeaseResumesOnlyWithExactProcessClaim`,
`Same_owner_resumption_refreshes_an_expired_active_lease_without_incrementing_attempt`,
`Same_owner_resumption_refuses_a_different_owner_or_terminal_row`, exact scoped-alias registration,
and missing-capability fail-closed tests. Use barriers after the pending journal checkpoint, at first
external mutation, proof write, journal clear, and scope disposal. On refusal, require the pending
journal to remain durable and the cursor to continue naming that candidate; require no file mutation,
terminal status, `ReconciliationRequired`, or attempt increment. Run the new focused filters and
inspect the expected compile/behavior/registration failures before Step 4.

- [ ] **Step 4: GREEN candidate-local group**

Implement `ResumeSameOwnerLeaseAsync` on the narrow capability as a compare-and-update for the same
`OperationId`, bounded `LeaseOwner`, and `Running`, `Waiting`, or `Cancelling` state. It deliberately
omits the ordinary heartbeat's `LeaseExpiresAt > now` predicate, refreshes heartbeat/expiry/revision
even after expiry, and never changes `AttemptCount`; a different owner or terminal row returns false.
Extend `LongRunningOperationOwnership` with `IsClaimedBy(Guid operationId, Guid token)` and require that proof
before the hosted service calls the resume method. Add an architecture/registration test proving
exactly one scoped narrow-capability registration exists and that the ordinary store and narrow
capability resolve to the same scoped `LongRunningOperationStore`, and prove that omission in a
directly constructed service refuses hosted resumption rather than falling back to ordinary heartbeat.

For a fresh hosted prune, create the LRO and immediately take a process-local ownership claim. Begin each group only after `SavePruneCheckpointAsync` has durably stored the candidate's pending journal and immediately before `_leaseMaintainer.RunAsync` can mutate storage. On refusal, return `DeferredForMaintenance` with the exact operation/owner/claim and leave the journal/cursor at that candidate. On reopen, verify the claim, refresh the same-owner lease without a new attempt, call the existing checkpoint recovery path, and repeat if a later candidate defers. On terminal success, genuine failure, or host cancellation, release the exact process claim in `finally`; cancellation retains the existing durable recovery semantics.

On a winning candidate, keep the group through `ApplyCandidateAsync`, reconciliation proof, candidate-attributable cursor/disposition, and clearing the pending journal. Dispose the group before the shared scope; keep the work lease until scope disposal.

- [ ] **Step 5: Verify and commit**

Run sweep-host, durability-boundary, pruning/service, lease-maintainer, and gate tests. Commit with `feat: admit automatic retention candidates`.

### Task 9: Preserve Loremaster Queue Identity Through Maintenance

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/CampaignLoggerQueue.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/CampaignLoggerQueueTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Hosting/LoremasterTests.cs`

**Interfaces:**

- Queue read no longer clears pending identity. Internal `Conclude(Guid sessionId)` clears terminal identity; `TryResignalHeld(Guid sessionId)` writes to a dedicated unbounded single-reader re-signal lane while identity remains pending. The public producer interface stays unchanged.
- Discovery is DB-only `LoremasterSummarization`; one Session summary is one effect group.

- [ ] **Step 1: RED queue ownership semantics**

Add `ReadingDoesNotReleasePendingIdentity`, `ConcludeReleasesIdentityOnce`, `DirectResignalBypassesDeduplicatingIntake`, and `FullNormalChannelCannotRejectHeldResignal`. Assert ordinary enqueue stays deduplicated while a yielded Session is held, fill all 100 normal slots after dequeue, and prove the held Session is still accepted once by the independent lane.

- [ ] **Step 2: GREEN explicit conclude/re-signal API**

Move `_pending.TryRemove` out of the read iterator. Implement `Conclude` for success/genuine skip/genuine failure. Add a dedicated `Channel<Guid>` created with `Channel.CreateUnbounded` and `SingleReader = true`; `TryResignalHeld` requires the id to remain in `_pending` and writes only to that lane. `ReadAllAsync` drains the re-signal reader before the normal bounded reader, then awaits either reader with `Task.WhenAny`. Because the single consumer can hold only one yielded Session, the re-signal lane remains bounded by service behavior even though normal producers cannot fill it.

- [ ] **Step 3: RED/GREEN discovery and Session frontiers**

Add `DeniedSweepCreatesNoScope`, `DeniedSessionRetainsExactIdentity`, `WinningSummaryDrainsThroughRollup`, and `HostCancellationIsNotFailure`. Acquire discovery lease before its scope; for a Session, begin the group before provider summarization and keep it through rollup. Dispose the group, then the Session scope, while the work lease continues to block closure.

- [ ] **Step 4: Add reopen behavior and commit**

Add `ReopenDirectlyResignalsHeldSessionOnce`; wait without lease, require `TryResignalHeld` to succeed unless shutdown has completed the reader, and leave `Conclude` as the only terminal removal. Run both queue/service classes and commit with `feat: preserve Loremaster work through maintenance`.

### Task 10: Admit the Three DB-Only Periodic Hosts

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingLeaseRenewer.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingLeaseRenewerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingLedgerTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMaintenanceHostedServiceTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionHostedServiceTests.cs`

**Interfaces:**

- One renewal scope uses `A2ASendingLeaseRenewal`.
- One bounded three-sweep Covenant pass uses `CovenantMaintenance`.
- One bounded transition-journal pass uses `GrimoireSchemaTransition`.
- None needs an external-effect group.

- [ ] **Step 1: A2A RED/GREEN**

Add `RenewalDenialCreatesNoScopeAndKeepsHeldSendings` and `AdmittedRenewalDrainsThroughScopeDisposal`. Acquire the lease before `RenewHeldAsync` creates its scope; on denial keep every tracked Sending and return to cadence without lost/failed classification. Run and commit `feat: admit A2A lease renewal`.

- [ ] **Step 2: Covenant maintenance RED/GREEN**

Add `CovenantPassDenialCreatesNoScope` and `AdmittedCovenantPassDrainsAllThreeSweepsAndScopeDisposal`. Acquire one lease in `RunOnceAsync`; expected refusal returns `false` without Error logging. Run and commit `feat: admit Covenant maintenance sweeps`.

- [ ] **Step 3: Schema transition RED/GREEN**

Add `SchemaPassDenialCreatesNoScope` and `AdmittedSchemaPassDrainsThroughScopeDisposal`. Extract `internal Task<bool> RunOnceAsync(CancellationToken cancellationToken)` as the exact bounded journal-pass seam; acquire before its scope and return `false` on refusal so the loop resumes its cadence. Run and commit `feat: admit schema transition passes`.

### Task 11: Admit Provider Health Observations

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbeService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Resilience/ProviderHealthProbeServiceTests.cs`

**Interfaces:**

- Each configured provider probe uses one `ProviderHealthProbe` lease and one group spanning outbound probe plus tracker publication.

- [ ] **Step 1: RED denial and winner ordering**

Add `DeniedProbeLeavesPreviousHealthUnchanged` and `WinningProbeDrainsThroughTrackerPublication`. Assert no outbound call and no tracker update on denial; block the call and publication separately to prove closure waits for both.

- [ ] **Step 2: GREEN per-provider authority**

For each provider, read the generation, acquire a lease, then begin a group immediately before the probe. On refusal return for that provider without changing its prior health; on win publish success or genuine unhealthy status before disposal.

- [ ] **Step 3: Preserve genuine failures and commit**

Add or retain `RealProbeFailureStillMarksUnhealthy` and host-cancellation tests. Run the service and gate tests; commit with `feat: admit provider health probes`.

### Task 12: Own the Actual Shared MCP Initializer Lifetime

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpGlobalInitializationAuthority.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/IMcpGlobalInitializationCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpLifecycleAdmission.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpConnectionManager.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ConnectionManager/McpConnectionManager.Partitions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ConnectionManager/McpConnectionManager.Lifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpServerBootstrapHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerBootstrapIdempotencyTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerTransportFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerTrustGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerMaxServersCapTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/HostProcessToolsAdvertisementAfterStartupTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpServerBootstrapHostedServiceTests.cs`

**Interfaces:**

- The manager owns one initialization-lifetime CTS and the actual `_globalInitOperation` task.
- `McpGlobalInitializationAuthority` has exactly `PreReadinessStartup` and `OrdinaryHostedWork`. The first caller that creates the shared operation fixes that operation's authority; concurrent callers join it without replacing or weakening it.
- The real shared ordinary operation, not each waiter's `WaitAsync`, owns `McpServerBootstrap` admission and one effect group. A pre-readiness first starter owns the same real task under its exact startup proof without an ordinary lease.
- The existing public `McpConnectionManager` constructor remains accessibility-safe. An internal one-time `ConfigureGlobalAdmission(IGrimoireConnectionAdmissionGate)` method is called by the production DI factory; ordinary initialization fails closed if production omitted configuration.
- `McpServerBootstrapHostedService` depends on a narrow internal `IMcpGlobalInitializationCoordinator`, registered as an alias to the same manager singleton, so it can select startup authority without exposing an internal member through `IMcpConnectionManager`.
- A manager-owned `McpLifecycleAdmission` admits the actual shared initializer and every direct
  `StartAsync`, `RestartAsync`, and `ReloadAsync` operation. Restart/reload call private core helpers
  rather than nesting public admission. `StopAllAsync` closes this admission and drains all admitted
  start-capable operations before it tears down servers, so the public `IMcpConnectionManager`
  contract needs no new shutdown member.

- [ ] **Step 1: RED the lifecycle defect**

Add `CancelledWaiterDoesNotReleaseSharedInitializerAuthority`,
`StopCancelsAndObservesActualGlobalInitBeforeStopAll`, and
`DisposeCancelsAndObservesInitializerBeforeDisposingSynchronization`,
`DirectStartRacingStopCannotPublishAfterTeardown`,
`DirectRestartRacingStopCannotPublishAfterTeardown`, and
`ReloadRacingStopCannotPublishAfterTeardown`, and
`StopCancelsDirectStartBeforeWaitingForReloadGlobalLock`. Block initialization after the
caller waiter cancels; prove the current code returns/disposes early and lets `StopAllAsync` race a
later server start. Park each direct start-capable path immediately after its existing `_disposed`
check and prove the current shutdown registry snapshot can miss its later process/client. Block an
initializer on `_globalInitLock` and prove `DisposeAsync` cannot dispose the lock or initialization
CTS until that exact task reaches terminal state. The three-party test makes direct Start hold an
entry gate while awaiting manager cancellation, Reload hold `_globalInitLock` while waiting for that
entry gate, and Stop close admission and cancel Start before trying the global lock; all three must
terminate without publishing a client after teardown.

- [ ] **Step 2: RED idempotent shared authority**

Add `ConcurrentCallersJoinOneAuthorityBearingTask`, `FirstStarterFixesSharedAuthority`,
`GlobalCacheInvalidationDoesNotClearInFlightInitializationIdentity`,
`JoinedCallerRechecksPublicationAfterInitializerCompletes`,
`ReloadWaitsForInFlightInitializerBeforeInvalidatingRegistry`,
`DeniedInitializerWaitsWithoutStartingProcessOrNetwork`, `BlockingStartIsPreReadiness`,
`NonblockingWaiterIsObserved`, `InvalidationDuringProjectionCannotPublishStaleSurface`, and
`ProductionRegistrationConfiguresAdmission`. Put a barrier after an
SSE initializer publishes through the existing recording event-bus seam, seed/stop a separate running
global entry to invalidate caches, capture `_globalInitOperation` using the test class's existing
reflection style, and assert a concurrent caller joins the same task and no server starts twice. Use a
dedicated `TaskCreationOptions.LongRunning` participant for the synchronous barrier. Assert exactly one
underlying initializer and one authority across blocking, nonblocking, lazy, and reload callers.

- [ ] **Step 3: GREEN manager-owned operation**

Create `_globalInitializationLifetime` and this exact authority enum:

```csharp
internal enum McpGlobalInitializationAuthority : byte
{
    PreReadinessStartup = 1,
    OrdinaryHostedWork = 2,
}
```

Remove the unlocked `_globalInitialized` fast path. `EnsureGlobalLoadedAsync` is a loop that, under
`_globalInitLock`, compares the last published global-surface revision with the atomically incremented
`_toolSurfaceGeneration`: it returns only when they match, joins the current incomplete task when one
exists, or creates a task for the first starter's authority. After awaiting a task with the caller's
`WaitAsync`, it loops and rechecks publication under the lock; an invalidation after final publication
but before task completion therefore starts a new operation instead of returning stale state.

Under `_globalInitLock`, the first starter stores the requested authority and starts
`RunGlobalInitOperationAsync(authority, _globalInitializationLifetime.Token)`; an in-flight caller only
takes the existing task. Every creation or replacement of `_globalInitOperation` happens under that
lock. At the start of each projection attempt the initializer captures the surface generation it is
projecting. `InvalidateCachesForServer` atomically advances the surface generation and clears derived caches,
but never waits for `_globalInitLock`, writes `_globalInitOperation`, or claims publication; this avoids
the `entry.Gate` -> global-lock inversion against reload's global-lock -> entry-gate path. When
invalidation occurs inside an AlwaysOn `StartAsync`, the original initializer remains authoritative
through `FinalizeGlobalState`. Finalization reacquires the global lock and publishes the completed
surface tagged with its captured revision only when the current generation is still equal; otherwise
it discards that projection and the loop rebuilds. It never tags old projection bytes with a
then-current revision. When invalidation occurs after publication, the loop detects the mismatch.
`InvalidationDuringProjectionCannotPublishStaleSurface` parks between scan and publication, mutates a
different server, and proves the stale projection is discarded before any waiter returns. Reload marks a `Reloading`
manager state under the global lock, preventing new initializers, snapshots any active task, releases
the lock to join it, then reacquires the lock to mutate the registry; it never nulls an executing task.
`PreReadinessStartup` proceeds under the awaited host-start proof. `OrdinaryHostedWork`
reads the gate generation, acquires `McpServerBootstrap`, waits/reacquires on denial, and begins one
effect group immediately before any process/network startup. The actual operation holds that authority
until all initialization effects and surface publication are terminal.

Waiters retain their own cancellation only for joining:

```csharp
Task initialization = GetOrStartGlobalInitialization(
    McpGlobalInitializationAuthority.OrdinaryHostedWork);

await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
```

Waiter cancellation must not cancel or dispose the manager-owned task's authority. Public
`InitializeAsync` and reload/lazy paths request `OrdinaryHostedWork`; blocking hosted startup requests
`PreReadinessStartup`; nonblocking hosted startup requests `OrdinaryHostedWork`. The actual shared task
enters `McpLifecycleAdmission` when it is created, before any entry gate, process, or network action.
Direct `StartAsync`, `RestartAsync`, and `ReloadAsync` enter the same admission before their first entry
gate or mutation and use a token linked from caller cancellation and the manager shutdown token.
`RestartAsync` and `ReloadAsync` invoke non-admitting private core helpers so one public invocation owns
one direct lifecycle lease; a reload that joins the distinct shared initializer waits for that
initializer's own lease. If an ordinary call somehow starts first while blocking startup joins, the
join keeps ordinary authority. A pre-readiness first starter can only exist before readiness, so later
callers can safely join it.

Keep the public constructor free of internal parameter types. Register the production singleton through an explicit factory:

```csharp
services.AddSingleton(static sp =>
{
    McpConnectionManager manager = new(
        sp.GetRequiredService<ILogger<McpConnectionManager>>(),
        sp.GetRequiredService<IHumanPromptRegistry>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IUnseenServantPacer>(),
        sp.GetRequiredService<IEventBus>(),
        sp.GetRequiredService<ITrustedMcpWorkspaceStore>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>());

    manager.ConfigureGlobalAdmission(
        sp.GetRequiredService<IGrimoireConnectionAdmissionGate>());

    return manager;
});
```

Update every direct-constructor test listed above to call `ConfigureGlobalAdmission(new GrimoireConnectionAdmissionGate(TimeProvider.System))`; this pins the fail-closed configuration rule as well as preserving existing test behavior.

- [ ] **Step 4: GREEN hosted ownership and shutdown**

Define `IMcpGlobalInitializationCoordinator.InitializeGlobalAsync(McpGlobalInitializationAuthority,
CancellationToken)` and `StopAllAsync(CancellationToken)`; implement it on `McpConnectionManager` and
register it as an alias to the same singleton as `IMcpConnectionManager`. Make
`McpServerBootstrapHostedService` internal and change its constructor dependency to that internal
interface. Because `AddInstallationResetRecoveryAwareHostedService<T>` preserves and resolves public
constructors, give this internal class an explicit public constructor (the justified exception to the
primary-constructor preference) and pin it with `DiWiringSmokeTests`. Store the nonblocking bootstrap
waiter instead of discarding `Task.Run`.

`StopAllAsync` first atomically closes `McpLifecycleAdmission`; after that linearization point no actual
initializer, direct start, restart, or reload can enter. It immediately cancels the manager
shutdown/initialization lifetime outside every manager/entry lock, so an already-admitted operation
cannot hold `_globalInitLock` or an entry gate indefinitely while shutdown is waiting to reach the
cancellation step. It then acquires `_globalInitLock`, marks manager state `Stopping`, and snapshots
the actual task; a would-be initializer in the close-to-state window loses lifecycle admission before
any process/network effect. Outside the lock it awaits the actual task directly to terminal
(never caller-cancellation-wraps that join), and awaits the lifecycle admission's drained task. Only
after both joins does it snapshot and tear down server entries. An operation admitted before closure
observes the linked manager cancellation and must exit before teardown; an operation that loses the
race creates no process/client and uses existing behavior rather than a new wire error code (`Result`
start/restart paths return `Mcp.ServerNotRunning`, while task-only initialize/reload paths throw the
existing disposed/stopped exception). `DisposeAsync` calls the same stop-and-observe helper before
disposing the initialization CTS, lifecycle admission, global lock, or entry gates. The hosted service
observes its stored waiter in `finally` even when server teardown throws. Never await a denied reopen
waiter before manager cancellation. Keep repeated Stop idempotent and preserve genuine initializer
failures for logging/observation.

Pin shutdown with `StopAllWaitsForActualInitializerBeforeStoppingRegisteredServers`,
`DisposeWaitsForActualInitializerBeforeDisposingManagerLocks`, and
`InitializeAfterShutdownCannotCreateAnotherOperation`, `StartAfterShutdownCreatesNoClient`,
`RestartAfterShutdownCreatesNoClient`, and `ReloadAfterShutdownCreatesNoClient`; a seeded
`TrackingMcpClient` must not be disposed
until the parked actual initializer terminates. Hosted-service tests cover blocking/pre-readiness,
nonblocking/ordinary stored-waiter ownership, Stop ordering, teardown failure with waiter observation,
application-stopping cancellation, and repeated Start/Stop. DI tests prove the concrete manager and
both public/internal aliases are the same singleton.

- [ ] **Step 5: Verify and commit**

Run MCP bootstrap/idempotency, trust, registration-cap, hosted-service, and gate tests. Commit with `fix: own MCP bootstrap admission lifetime`.

### Task 13: Admit Runtime Long-Running-Operation Recovery Exactly

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationRecoveryAdmission.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningRecoveryOwnerEvidence.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Operations/ILongRunningOperationGenericRecoveryDiscovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Operations/ILongRunningOperationClassifiedRecoveryLeaseAcquisition.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/ILongRunningOperationMaintenanceLeaseAdoption.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationStartupHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantRecoveryAuthorityBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOfflineTransitionLaunchGapResumption.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRecoveryAdmissionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationReconcilerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationExactSettlementTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationStartupHostedServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionHandlerDispatchTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionStartupRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionStartupRecoveryChainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantRecoveryAuthorityBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantOfflineTransitionLaunchGapResumptionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/RecordingRecoveryDispatchSeam.cs`

**Interfaces:**

- `LongRunningOperationRecoveryAdmission.Classify(LongRunningOperation operation,
  LongRunningRecoveryOwnerEvidence? ownerEvidence)` returns a closed
  `LongRunningRecoveryAdmissionDecision` and binds nonordinary evidence to the loaded row's complete
  launch owner.
- Background page discovery gets a short ordinary lease before its outer scope and releases both before per-operation dispatch.
- Every per-operation durable claim compare-exchanges the materialized operation id, revision, kind,
  and checkpoint version and atomically returns the claimed row for reclassification.

Use these exact types:

```csharp
internal enum LongRunningRecoveryAdmissionKind : byte
{
    OrdinaryDbOnly = 1,
    OrdinaryExternalEffect = 2,
    OwnerBoundOffline = 3,
    OwnerBoundAwaitingExactOwner = 4,
    UnsupportedCheckpointVersion = 5,
}

internal readonly record struct LongRunningOperationRecoveryFingerprint(
    Guid OperationId,
    string Kind,
    int CheckpointVersion,
    long Revision);

internal abstract class LongRunningRecoveryOwnerEvidence
{
    private protected LongRunningRecoveryOwnerEvidence(
        CovenantExclusiveRecoveryOwner owner,
        LongRunningOperationRecoveryFingerprint expectedOperation)
    {
        Owner = owner;

        ExpectedOperation = expectedOperation;
    }

    internal CovenantExclusiveRecoveryOwner Owner { get; }

    internal LongRunningOperationRecoveryFingerprint ExpectedOperation { get; }
}

internal readonly record struct LongRunningRecoveryAdmissionDecision(
    LongRunningRecoveryAdmissionKind Kind,
    string? ErrorCode);

internal static LongRunningRecoveryAdmissionDecision Classify(
    LongRunningOperation operation,
    LongRunningRecoveryOwnerEvidence? ownerEvidence);
```

The opaque evidence type has no callable factory and no test-only constructor. It has exactly two
direct production subclasses, both private sealed nested types:
`GrimoireOfflineTransitionStartupRecovery.AuthenticatedJournalOwnerEvidence` and
`CovenantOfflineTransitionLaunchGapResumption.AdoptedLaunchOwnerEvidence`. Their containing recovery
methods alone construct them after asserting the live installation lock for the exact guarded
directory. `CovenantClosedRecoveryHandoff` exposes its bootstrapper-verified owner/fingerprint through
the narrow handoff interface, and the journal issuer constructs evidence only after `ConsumeAsync`
succeeds. Launch-gap resumption accepts an
opaque `CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner` whose private constructor is called
only by the adopter after its row scan and gate adoption. Both evidence implementations retain the
complete `CovenantExclusiveRecoveryOwner` rather than reducing it to a caller-constructible id/version tuple.

`CovenantArchitectureBoundaryTests.OwnerEvidenceHasExactlyTwoPathSpecificIssuers` uses Roslyn/symbol
inspection to assert those are the only direct subclasses, both are private sealed nested types, and
their only object creations remain in the exact authenticated recovery members. It also asserts one
production `ICovenantClosedRecoveryHandoff` implementation and that no call graph rooted at
`IInstallationResetMaintenanceLockAccessor.BorrowHeldLock` can construct an issuer. Adding a third
issuer or a general factory therefore fails the build-time architecture contract.
For an owner-bound row the classifier decodes
`GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(operation.CheckpointVersion,
operation.CheckpointPayload)` and compares the loaded row kind plus all three owner fields—operation
id, `CovenantExclusiveOperation`, and effect digest—to the decoded launch and evidence. Missing,
malformed, or merely id/version-matching evidence is `OwnerBoundAwaitingExactOwner`. Generic
startup/manual/background discovery always passes `null`; authenticated owner-bound entry points
alone can supply the capability while the installation lock remains held.
`OwnerBoundAwaitingExactOwner` is a supported row that remains
untouched for its owner path; `UnsupportedCheckpointVersion` is an invalid row that is durably settled
under DB-only ordinary authority. The discovery query materializes the complete
`LongRunningOperation` row, including kind, checkpoint version/payload/reference, and lease owner,
before releasing its scope and lease. Production generic discovery uses a narrow internal store
alias whose SQL excludes mutation V4 and factory-reset V2 before `LIMIT`; otherwise a full first page
of unchanged owner-bound rows would permanently starve ordinary rows behind it. The classifier still
tests the explicit no-evidence owner-awaited result. A second narrow scoped alias,
`ILongRunningOperationClassifiedRecoveryLeaseAcquisition`, performs `UPDATE ... WHERE Id = @id AND
Revision = @revision AND Kind = @kind AND CheckpointVersion = @version ... RETURNING ...`. It and
`ILongRunningOperationMaintenanceLeaseAdoption` share that exact predicate/returning implementation;
the maintenance variant alone omits expiry after asserting the installation lock. Both aliases
resolve to the same scoped `LongRunningOperationStore`.

- [ ] **Step 1: RED outer-scope ordering**

Add `BackgroundDiscoveryAcquiresBeforeOuterScope`,
`DiscoveryLeaseIsReleasedBeforeHandlerDispatch`, and
`OwnerBoundRowsDoNotStarveOrdinaryRowsBeyondPage`. The first awaited startup pass remains pre-readiness;
invoke the periodic helper directly and assert scope ordering and release. Add one scoped
`ILongRunningOperationGenericRecoveryDiscovery` registration aliased to the same concrete store, and
pin its pre-limit owner-tuple exclusion in store and architecture tests. Before production changes,
also add store/registration RED tests proving the classified-acquisition alias is the same scoped
store, returns the exact claimed row, and rejects revision, kind, or version drift without changing
`AttemptCount`, lease fields, or revision. The successful test pins the one acquisition transition:
the returned row has the same id/kind/version, `AttemptCount + 1`, and exactly
`expected.Revision + 1`.

- [ ] **Step 2: RED the exact classification matrix**

Assert this complete closed table; every integer outside the listed registry window is
`UnsupportedCheckpointVersion`:

```text
InferenceRun: V0 OrdinaryDbOnly
Subagent: V0 OrdinaryDbOnly
BudgetReservation: V0 OrdinaryDbOnly
Batch: V0 OrdinaryExternalEffect
Apprentice: V0/V1 OrdinaryDbOnly
AttachmentPromotion: V0 OrdinaryExternalEffect
WorkspaceIndex: V0 OrdinaryDbOnly
IdempotencyClaim: V0 OrdinaryDbOnly
BlobEncryptionMigration: V0/V1 OrdinaryExternalEffect
BlobEncryptionKeyRotation: V0/V1 OrdinaryExternalEffect
BackupCreate: V0 OrdinaryDbOnly; V1 UnsupportedCheckpointVersion; V2 OrdinaryExternalEffect
DataRetentionPrune: V0 OrdinaryExternalEffect; V1 UnsupportedCheckpointVersion; V2 OrdinaryExternalEffect
DataRetentionMutation: V0 OrdinaryDbOnly; V1 UnsupportedCheckpointVersion; V2 OrdinaryExternalEffect; V3 UnsupportedCheckpointVersion; V4 OwnerBoundOffline only with the exact CovenantReset full-owner capability, otherwise OwnerBoundAwaitingExactOwner
DataRetentionFactoryReset: V0 OrdinaryExternalEffect; V1 UnsupportedCheckpointVersion; V2 OwnerBoundOffline only with the exact HealthyCatalogFactoryErasure full-owner capability, otherwise OwnerBoundAwaitingExactOwner
CovenantIndexRebuild: V1 OrdinaryDbOnly
CovenantFamilyReinitialize: V1 OrdinaryDbOnly
A2AInboundSending: V0/V1 OrdinaryDbOnly
A2AOutboundSending: V0 OrdinaryDbOnly; V1 OrdinaryExternalEffect
```

Also enumerate every recovery descriptor exactly once and reject default/wildcard classification. The V1 prune test must include a null-payload row, closing the current path that can otherwise fall into no-checkpoint external recovery despite the unsupported version.
Exercise the ordinary, unsupported, and owner-awaited rows directly with null evidence. Exercise the
two `OwnerBoundOffline` successes by driving the authenticated journal and launch-gap paths and
capturing their opaque evidence at the dispatch seam; do not add a test mint/factory.

`CovenantErasureStartupRecoveryOwnerAdopter` deliberately fails closed before ordinary bootstrap when
it sees the retired mutation V3 launch shape. Preserve that half-erased-family readiness invariant and
its existing test. The durable unsupported-version settlement guarantee applies to rows that reach the
pre-readiness reconciler, finite manual reconciliation, or periodic runtime reconciliation; it does not
turn an adopter refusal into permission to publish readiness.

- [ ] **Step 3: GREEN the pure classifier**

Implement the matrix in the new focused file with an exhaustive tuple switch. Require the opaque,
path-specific full-owner capability for V4/V2 offline arms; generic discovery receives `null` and classifies
them as `OwnerBoundAwaitingExactOwner`, records an owner-awaited observation, and leaves the row
unchanged without taking its operation lease, ordinary authority, handler, or private scope. A
genuinely invalid version classifies as `UnsupportedCheckpointVersion` with
`LongRunningOperationErrorCodes.UnsupportedCheckpointVersion`; its background path takes a DB-only
`LongRunningOperationRecovery` lease before a private scope, acquires the durable row lease, and
compare-exchange settles `ReconciliationRequired` without invoking the handler. The initial awaited
pass uses its existing `PreReadinessStartup` authority and finite manual reconciliation uses its request
lifetime for the same DB-only settlement. This terminal guard must not be rediscovered forever.

- [ ] **Step 4: RED/GREEN per-operation authority**

Add `OrdinaryRecoveryAcquiresBeforePrivateScope`, `ExternalRecoveryHoldsGroupThroughSettlement`,
`EveryEffectfulHandlerIsClassifiedExternal`, `OwnerBoundWithoutEvidenceStaysUnchanged`,
`MatchingIdAndVersionWithoutAuthenticatedFullOwnerIsRefused`,
`WrongOwnerEffectDigestIsRefusedUnchanged`,
`OwnerEvidenceHasExactlyTwoPathSpecificIssuers`,
`ClassificationDriftBeforeConditionalClaimLeavesRowUnchangedAndDoesNotInvokeHandler`,
`ConditionalClaimReturnsNextRevisionAndReclassifiesBeforeHandler`,
`AuthenticatedJournalClaimAdvancesExpectedRevisionExactlyOnce`,
`LaunchGapClaimAdvancesExpectedRevisionExactlyOnce`,
`UnsupportedVersionSettlesUnderDbOnlyAuthorityWithoutHandler`, and
`UnsupportedSettlementIsNotRediscovered`, and
`ExternalFrontierLossDoesNotAcquireDurableLeaseOrIncrementAttempt`,
`ExternalWinnerHoldsGroupFromDurableClaimThroughSettlement`,
`ExternalKeepClosedWaitIsOwnedAndResumesSameIdentity`, and
`OfflineRecoveryDoesNotAcquireOrdinaryAuthorityOrSelfDeadlock`. For the background pass,
`LongRunningOperationStartupHostedService` acquires a short discovery lease before a temporary scope,
materializes rows, and releases both before dispatch. It then acquires the classified per-row work
lease. For an external decision it begins the effect group before creating the fresh scope or calling
the narrow classified acquisition; a lost frontier therefore creates no scope, durable claim, attempt
increment, or handler call. After a win it creates the scope, resolves that scope's
reconciler/store/handlers, and calls an internal `SettleDiscoveredRuntimeAsync`; no scoped object
escapes the scope that created it. The SQL claim predicates on the discovered id, revision, kind, and
checkpoint version and returns the claimed row in the same statement. A miss is deferral due to
classification drift and changes nothing. A hit is immediately reclassified from the returned row
before handler selection; it must retain the expected id/kind/version and have exactly the pre-claim
revision plus one, then yield the same authority decision or fail closed before the handler. The
classifier compares the decoded launch and complete owner binding but deliberately does not demand
that the post-claim row equal the evidence's pre-claim revision.
The group spans durable claim, handler, and compensation settlement, then disposes before the private
scope; the work lease remains held through scope disposal. A `KeepClosed` denial/wait retains the exact discovered operation identity in a
tracked host task, releases all authority before waiting, and reacquires without manufacturing another
durable attempt. DB-only handlers and unsupported-version settlement keep only the work lease and
drain to settlement. The initial pass uses the same classifier/orchestration with pre-readiness
authority instead of ordinary work leases/groups.

Add an internal owner-evidence overload of `SettleExactlyAsync`; retain the existing public overload as
a no-evidence path so no internal type leaks through the public API. Change
`IGrimoireOfflineTransitionHandlerDispatch.DispatchAsync` to take the opaque evidence and make
startup `PrepareAsync` return that capability rather than a bare `Guid`. Extend the narrow handoff
interface with the bootstrapper-verified `Owner` and exact operation fingerprint. After it has
authenticated and consumed the journal handoff, require
`handoff.OperationId == journal.Binding.OperationId`, assert the held installation lock again, and
construct the private nested `AuthenticatedJournalOwnerEvidence` from that verified handoff; do not
reconstruct an owner from public journal fields. Architecture tests pin
`CovenantClosedRecoveryHandoff` as the sole production handoff implementation.

`CovenantErasureStartupRecoveryOwnerAdopter.AdoptBeforeReadinessAsync` returns an opaque nested
`AdoptedOwner?` containing the already-authenticated full owner and the scanned row fingerprint; the
token's constructor is private to the adopter. Carry that token through
`GrimoireDatabaseBootstrapper.ProtectedMaintenanceRecovery` into
`CovenantOfflineTransitionLaunchGapResumption`. The launch-gap method asserts the still-held
installation lock and constructs its private nested `AdoptedLaunchOwnerEvidence`; it never accepts a
caller-constructed bare `CovenantExclusiveRecoveryOwner`.

Dispatch uses the evidence fingerprint in the installation-lock conditional adoption statement,
which returns the claimed row atomically; it then reclassifies that returned row and decodes its launch
binding before handler execution. A predicate miss changes no attempt/lease/revision and refuses.
Only a successful pre-claim revision predicate, the exact returned `+1` revision, and equality across
row kind, checkpoint version, operation id, exclusive-operation code, and effect digest return
`OwnerBoundOffline`. Add `AuthenticatedJournalSelectsMatchingFullOwnerCapability` and
`LaunchGapAdoptedOwnerSelectsMatchingFullOwnerCapability` for both owner kinds, plus
`MismatchedOfflineKindVersionOperationOrDigestIsRefusedUnchanged`; mismatches invoke no handler, do not
acquire ordinary authority, and do not rewrite the durable row. Tests receive capabilities only by
driving one of the two exact production recovery paths; no test seam or general lock borrower may
construct one.

The existing public `SettleExactlyAsync(Guid, string, CancellationToken)` becomes a no-evidence,
fail-closed compatibility overload: it never dispatches ordinary or owner-bound work and never writes
the row. Only the internal evidence overload may reach exact offline settlement, and only when the
classifier returns `OwnerBoundOffline`; add `PublicExactSettlementWithoutEvidenceIsFailClosed`.

Join the production handler call graph to the inventory scanner and require external classification for these exact effects: Batch artifact cleanup, attachment-promotion file reconciliation, blob migration/rotation encrypted replacement and key retirement, Backup V2 staging deletion, retention-prune V0/V2 quarantine/file mutation, retention-mutation V2 quarantine mutation, factory-reset V0 quarantine mutation, and A2A outbound V1 remote cancellation. The V0 Backup and V0 outbound-Sending handlers remain DB-only because they abandon before those effects.

- [ ] **Step 5: Verify and commit**

Run reconciler, recovery registry, crash recovery, maintenance adoption, startup-hosted-service, and gate tests. Commit with `feat: admit runtime durable-operation recovery`.

### Task 14: Complete the Exhaustive Inventory and Exact Backup/Lifecycle Proofs

**Files:**

- Complete: `tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs`
- Complete: `tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantResetBootstrapBarrierTests.cs`
- Read as proof: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs`
- Read as proof: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs`
- Read as proof: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`

**Interfaces:**

- Produces the final bijective 23-service and operation-site catalog.
- Produces a separate `NonHostedProducerChainEntry` catalog for Backup CLI, restore-safety,
  stopped-host, and owner-bound chains; those sites join the common site bijection but are excluded
  from hosted-registration validation.
- Joins live backup source opens to exact CLI/stopped-host/owner-bound callers without changing backup production semantics.

- [ ] **Step 1: Turn every ordinary inventory gap GREEN**

Populate exact operations/sites for Workspace, Tapestry, Batch, Unseen, Apprentice, retention, Loremaster, A2A renewal, Covenant maintenance, schema transition, LRO runtime, provider health, and MCP. Require the declared work kind and discovered frontier call in the same enclosing operation path.

- [ ] **Step 2: Add exact mixed-host and lifecycle rows**

Catalog Batch startup recovery, first LRO pass, and blocking MCP startup as exact pre-readiness operations while their runtime continuations remain ordinary. Catalog Grimoire database startup/recovery/stopped-host shutdown, pending attachment GC, file-key bootstrap, PID file, security startup checks, settings logger, feature publisher, and the wrapper support code exactly as the spec states.

- [ ] **Step 3: RED/GREEN backup caller-authority joins**

Add `BackupLiveSourceHasExactCliCallerAuthority`, `RestoreSafetyBackupHasExactStoppedHostOrOwner`,
`NonHostedBackupChainsDoNotEnterHostedRegistrationBijection`, and
`LiveSourceCannotBecomeReachableFromHostedOrUnadmittedCaller`. Prove
`BackupCommands.Create -> IGrimoireCliInitialization.RunExclusiveAsync -> installation lock/client mutation -> durable backup owner -> Covenant installation read lease -> snapshot completion`; keep the low-level source route ordinary.

- [ ] **Step 4: Narrow the obsolete six-service claim**

Change `CovenantResetBootstrapBarrierTests` so its six registrations are described only as startup-order participants. The new inventory is the sole completeness claim.

- [ ] **Step 5: Run GREEN and commit**

Run hosted inventory, connection inventory, bootstrap barrier, backup/restore, and gate tests. Expected: every validator and real-tree bijection passes with no ignored site. Commit with `test: inventory every hosted Grimoire producer`.

### Task 15: Document, Review, Qualify, Integrate, and Close

**Files:**

- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.Engineering.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`
- Modify when contract tests demand exact anchors: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs`
- Modify when tracker policy demands exact references: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationIssueReferenceTests.cs`

**Interfaces:**

- Publishes the exact inventory, effect boundaries, classifications, and regression catalog without changing API/CLI/config contracts.
- Produces final verification evidence for the feature and merged `grimoire-fixes` trees.

- [ ] **Step 1: Write documentation-contract RED tests**

Add exact required anchors/phrases for the complete hosted producer inventory, Batch 64-line page frontier, MCP actual shared-operation ownership, and #239 delivery status. Run both documentation test classes and inspect missing-anchor failures.

- [ ] **Step 2: Update owning docs to GREEN**

Update `Arcanum.DESIGN.md` §10.20.3, affected service sections, and §13.7; add the
source-inventory obligation to `Arcanum.Engineering.md`; extend the intentional README
background-maintenance paragraph; mark #256 delivered but #257/#239 open in the parent design. Do
not edit API, command-reference, or Compendium docs. Run the documentation contract tests to GREEN,
then commit all tracked documentation and documentation-test changes with
`docs: publish hosted Grimoire admission contract` before branch review or final qualification.

- [ ] **Step 3: Run focused clusters and inspect the branch**

Run every new/modified class plus:

```bash
git diff --check grimoire-fixes...HEAD
git status --short --branch
```

Inspect all changes for cancellation conflation, scope-before-lease ordering, identity loss, broad inventory proof, and effect disposal before durable disposition.

- [ ] **Step 4: Request independent code review**

Use `superpowers:requesting-code-review`. Address every verified correctness/security/test gap with a fresh failing test before its fix; re-run the affected cluster after each correction.
Commit every review correction and require a clean worktree before proceeding. No tracked file may
change after the final qualification begins.

- [ ] **Step 5: Run exact feature-tip qualification**

Run from the worktree root:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
```

If the baseline lease-maintainer timeout recurs, use `superpowers:systematic-debugging`: capture a reproducible suite/stress case, switch its timer to controlled time, and leave production unchanged unless evidence demonstrates a product bug.

- [ ] **Step 6: Record immutable evidence outside the qualified tree**

Capture `git rev-parse HEAD`, exact test totals, tool versions, and verification commands in this
plan's gitignored SDD ledger/report and later in the merge/issue record. Do not edit or commit this
tracked plan, the design, source, tests, or documentation after qualification: a commit cannot contain
its own SHA, and any later tracked commit would create a different unqualified tip. Require clean
tracked status and no unpushed surprise commits at the recorded feature SHA.

- [ ] **Step 7: Merge and verify the integrated tree**

Use `superpowers:finishing-a-development-branch`. Fetch `origin`, confirm `grimoire-fixes` has not diverged, merge the feature with `--no-ff`, and rerun the risk-proportionate merged-tree tests plus `git diff --check` before push.

- [ ] **Step 8: Push, clean up, and close only #256**

Push `grimoire-fixes`, verify `HEAD == origin/grimoire-fixes`, remove the exact isolated worktree, delete local and remote feature branches if present, close GitHub issue #256 with merge/verification evidence, and verify #239 remains open.

## Delivery Evidence

Immutable delivery evidence is intentionally recorded only after qualification, in the gitignored SDD
ledger/report and then the merge and issue records. This tracked plan is not rewritten after the final
feature-tip commands, because doing so would invalidate the SHA those commands qualified. Do not copy
baseline or intermediate totals into final evidence.
