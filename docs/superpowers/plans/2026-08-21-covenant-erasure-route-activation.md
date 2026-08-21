# Covenant Erasure Route Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task, `superpowers:test-driven-development` for every behavior, and `superpowers:systematic-debugging` for every unexpected failure. Do not commit intermediate red/green steps; the user authorized commits only after the complete verification matrix is green.

**Goal:** Activate issue #128's Covenant reset and healthy-catalog factory-erasure routes without weakening the existing factory-reset contract or the frozen erasure/recovery protocol.

**Architecture:** `DataRetentionService` remains the sole data-erasure durable-operation owner. It prepares the existing V3/V1 checkpoints and supplies a factory-only restart-idempotent continuation to `CovenantErasureCoordinator`; ordinary factory cleanup runs after the protected kernels but before handle/WAL/sidecar proof and reopen. Global/all installation reset first rebinds the authenticated host's Covenant-aware data plan into a durable `Prepared` installation record, names the host erasure with that installation operation ID through the request-identity ledger, and shuts the host down only after the named erasure completes or replays. API endpoints only validate/authorize/map envelopes, while protected plan results retain one read lease through serialization.

**Tech stack:** .NET 10, ASP.NET Core minimal APIs, Microsoft.Data.Sqlite/SQLCipher, xUnit, source-generated System.Text.Json, Native AOT.

**Spec:** `docs/superpowers/specs/2026-08-21-covenant-erasure-route-activation-design.md`

## Global constraints

- Preserve the frozen ten Covenant erasure phases and existing V1/V3 checkpoint versions.
- Never run Covenant erasure through the lease-free offline installation service or an ordinary-route fallback.
- Use the installation operation ID only as a normalized requested-operation identity, never as `RootOperationId` or a gate owner.
- Rebind the online data-plan ID and recompute the installation binding/plan before dry-run output, disclosure, or confirmation.
- Keep every fresh global/all failure before shutdown and destructive installation mutation unless the named online erasure has already completed.
- Maintain exact durable ownership from data LRO admission through terminalization and throughout V1/V3 recovery.
- Preserve Native AOT/source-generated JSON, content-free errors, strict response headers, and the repository's blank-line C# style.
- Update owning current documentation; do not modify dated #127 plans/specifications or historical reviews.
- Preserve the pre-existing untracked IDE file and stage only confirmed task paths.

---

## Task 1: Freeze route contracts with failing API tests

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/ApiEndpointNameTests.cs` if endpoint names are pinned there
- Modify later: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`

1. Add a test that discovers `PlanDataRetentionMemoryReset`, `ResetDataRetentionMemory`, and `FactoryResetDataRetention` and asserts `LifecycleManage` authority metadata.
2. Add a test that `POST /api/data/memory/reset/plan` accepts `MemoryResetScope.Covenant` and returns an `ApiResponse<DataRetentionPlan>` without applying data.
3. Add protected-response tests for exact cache headers and absent validators on success and typed failure.
4. Add a delayed-serialization test proving reset and factory plan responses retain and revalidate one installation read lease through the final JSON byte.
5. Run the focused tests and record the expected failures: the preview route is 404, authority metadata is absent, and planning releases its lease before serialization.
6. Add a lease-bearing plan admission to the retention service, use `CovenantProtectedJsonResult<DataRetentionPlan>` for Covenant-bearing reset/factory plans, and add the route/metadata. Register no new wire DTO because `MemoryResetRequest` and `DataRetentionPlan` are already in `ArcanumJsonContext`.
7. Delegate Covenant failure status resolution to `ArcanumErrorMapper` and pin erasure-incomplete as HTTP 503.
8. Rerun the focused tests to green.

## Task 2: Replace the Covenant reset refusal with a real plan

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Modify later: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify later: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`

1. Replace the old refusal assertions with a test that the Covenant plan has no activation conflict and exposes rows, managed files, local artifacts, affected Sessions, possible disclosures, and count kind without protected content.
2. Add a regression assertion that `Data.CovenantResetRequiresErasureCoordinator` is not part of the public contract.
3. Run only those tests and observe the old conflict failure.
4. Remove the conflict from `BuildCovenantResetMemoryPlanAsync` while retaining confirmation and aggregate inventory.
5. Remove the retired constant and update its pinning test.
6. Rerun focused tests to green.

## Task 3: Activate Covenant reset through the existing checkpoint/coordinator

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureSameProcessTests.cs` if the real gate timing fixture belongs there
- Modify later: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`

1. Add a service test proving Covenant apply saves a V3 `InventoryPrepared` checkpoint before the exclusive gate is acquired and passes the checkpoint-derived owner to the coordinator.
2. Add a held-lease test proving a drain failure changes no Covenant row or managed file.
3. Add success mapping and typed failure tests, including post-effect closed-admission behavior and original plan ID preservation.
4. Run each focused test before production changes and confirm its behavioral failure.
5. Add the narrow Covenant branch after LRO creation and before ordinary mutation-journal preparation:
   - derive the existing `CovenantErasureEffectDigestInput`;
   - call `PrepareCovenantResetInventoryAsync`;
   - decode/project the committed checkpoint state;
   - await `CovenantErasureCoordinator.RunAsync`;
   - map only successful `CommitAndReopen` to a reconciled content-free result without treating retained preview inventory as deleted rows;
   - leave terminal transition ownership in `ApplyAsync`.
6. Ensure caller cancellation cannot interrupt publication/reopen after storage proof; rely on the coordinator's existing lifecycle token ownership.
7. Rerun the focused reset and existing coordinator/recovery suites.

## Task 4: Compose healthy factory erasure with ordinary factory deletion

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantResetCheckpointInitiatorTests.cs`
- Modify later: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify later: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`

1. Add a test with both Covenant and ordinary data proving protected kernels run first, ordinary factory-owned data is removed while the exclusive lease remains held, and final health proof/reopen follows it.
2. Add a healthy-catalog revalidation test that changes catalog state between planning and the WAL-visible preflight and proves zero effects.
3. Add V1 recovery tests at `ManagedArtifactsProcessed`, `HandlesClosed`, and later phases. Prove pre-readiness adoption resumes the exact coordinator owner, the restart-idempotent ordinary continuation reruns only before `HandlesClosed` is durable, and completion follows full reconciliation.
4. Add a failure test proving a coordinator failure prevents ordinary deletion and retains the closed-state typed refusal.
5. Run the focused tests and observe failures at the missing V1 preparation/coordinator entry and recovery short-circuit.
6. Add a factory-only restart-idempotent continuation seam to `CovenantErasureCoordinator`. Invoke it after `ManagedArtifactsProcessed` and before `HandlesClosed`; require it for healthy-catalog factory erasure and forbid it for ordinary Covenant reset.
7. Prepare the V1 checkpoint through `PrepareFactoryErasureInventoryAsync`, rebuild the internal ordinary factory plan inside that continuation, and call the existing `ApplyFactoryResetAsync` under the same durable operation and exclusive lifecycle.
8. Preserve the original confirmed plan ID in the public result while reporting actual ordinary deletion counts from the internal post-protected plan.
9. Extend `RecoverCovenantFactoryErasureAsync` to pass the same continuation into coordinator recovery; retain V0 behavior exactly and do not add a checkpoint version or phase.
10. Add a regression proving no provider dispatch or protected writer can enter between ordinary cleanup and final reopen.
11. Rerun focused factory, coordinator, recovery, and installation-reset compatibility tests.

## Task 5: Repair reset preview and centralize CLI disclosure

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandsTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Modify later: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.DataLifecycle.cs`
- Modify later: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`
- Modify later: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Add if needed: `src/RetroDownfall.Arcanum.Cli/UX/CovenantExternalRetentionDisclosureWriter.cs`
- Modify if added: `src/RetroDownfall.Arcanum.Cli/DependencyInjection/CliServiceCollectionExtensions.cs`

1. Add a client/command test proving reset preview calls `/api/data/memory/reset/plan`, not `/api/data/prune/plan`.
2. Add ordering tests for exact shared text, exact/lower-bound count, every help target, then prompt.
3. Add decline tests proving neither reset nor factory apply starts after disclosure.
4. Add automated acknowledgement coverage proving disclosure still goes to diagnostics.
5. Run focused tests and observe the prune-route and missing-factory-disclosure failures.
6. Add `PlanDataMemoryResetAsync` to the typed API client.
7. Extract the existing formatter into one reusable disclosure writer and use it from reset and the healthy online global factory confirmation owner.
8. Preserve the CLI JSON one-document contract by writing disclosure only to diagnostics.
9. Rerun focused CLI and JSON-context coverage tests.

## Task 6: Add named factory-erasure replay to the data API

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantFactoryErasureApplyRequestDigestCalculator.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.DataLifecycle.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientTests.cs`
- Modify test stores that implement `ILongRunningOperationStore`, including `tests/RetroDownfall.Arcanum.Tests/Operations/FakeLongRunningOperationStore.cs`

**Interfaces:**

- Extend the existing request/result records without changing confirmation-only callers:

  ```csharp
  public sealed record DataRetentionApplyRequest(
      DataRetentionRequest Request,
      string? ExpectedPlanId = null,
      Guid? RequestedOperationId = null);

  public sealed record FactoryResetRequest(
      string Confirmation,
      string? ExpectedPlanId = null,
      Guid? RequestedOperationId = null);

  public sealed record DataRetentionApplyResult(
      Guid OperationId,
      string PlanId,
      long RowsDeleted,
      long FilesDeleted,
      long EstimatedBytesDeleted,
      long DerivedRecordsDeleted,
      bool Reconciled,
      DataRetentionBlocker[] Blockers,
      DataRetentionConflict[] Conflicts,
      Guid? RequestedOperationId = null);
  ```

- Add the replay lookup in the operation store:

  ```csharp
  public sealed record LongRunningOperationRequestIdentityMatch(
      LongRunningOperation Operation,
      LongRunningOperationRequestIdentity Identity);

  Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
      Guid requestedOperationId,
      CancellationToken cancellationToken = default);
  ```

- Add one pinned apply-request digest producer. The domain commits to healthy-catalog factory erasure;
  the input commits to the confirmed plan ID. The requested ID is already the unique ledger key and
  the five-count dataset-specific effect remains the separate effect digest.

  ```csharp
  public sealed record CovenantFactoryErasureApplyRequestDigestInput(string PlanId);

  public interface ICovenantFactoryErasureApplyRequestDigestCalculator
  {
      Result<CovenantDigest> Compute(
          CovenantFactoryErasureApplyRequestDigestInput input);
  }
  ```

- [ ] **Step 1: Write the store and digest RED tests.** Add literal-digest tests, lookup-by-requested-ID tests, null lookup tests, and a test proving the lookup returns both the real server operation and all three stored identity fields. Name the break: a replay after erasure must not need a new live inventory to find its original operation.

- [ ] **Step 2: Run the store/digest RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~CovenantFactoryErasureApplyRequestDigest"
  ```

  Expected: compile failure for the missing match type/method/calculator, followed by behavioral failure until the request-key query and pinned digest are implemented.

- [ ] **Step 3: Implement the minimal store/digest contracts.** Query the identity table by `RequestedOperationId`, join/read the referenced operation, validate a non-empty request ID, and compare apply digests with `CryptographicOperations.FixedTimeEquals` at the replay caller.

- [ ] **Step 4: Write API/client RED tests.** Prove the factory route accepts confirmation-only compatibility, requires `ExpectedPlanId` and `RequestedOperationId` as an all-or-none tuple, maps both values into `DataRetentionApplyRequest`, and the typed client emits null-omitted fields.

- [ ] **Step 5: Run the API/client RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~ArcanumApiClientTests"
  ```

  Expected: request JSON/mapping assertions fail because the current DTO carries only confirmation.

- [ ] **Step 6: Implement the wire mapping.** Keep `FactoryResetDataRetention` authority/headers unchanged and map the optional confirmed plan/requested identity into the service apply request.

- [ ] **Step 7: Write named-operation RED tests against the real same-process factory route.** Cover first create, completed replay with no second erasure, same requested ID/different plan as `Security.IdempotencyConflict`, null `RootOperationId`/`ParentOperationId`, requested ID echoed separately from the server operation ID, and V1 checkpoint verification using the requested identity while the gate owner remains derived from the server operation.

- [ ] **Step 8: Run the named factory RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~CovenantErasureSameProcessTests.Factory_named|FullyQualifiedName~CovenantResetCheckpointInitiatorTests.Factory"
  ```

  Expected: the route starts an unnamed single-flight row, passes null to checkpoint preparation, and cannot replay after the live inventory is gone.

- [ ] **Step 9: Implement named start/replay.** Before planning, resolve an existing requested identity and map its completed/active/failed state under the stored apply digest. For a fresh name, derive the current effect and apply digests, use `CovenantRequestedOperationStarter.StartRequestedAsync`, continue only for `Created`, map `Replayed` without preparing a second checkpoint, and pass the requested ID into `PrepareFactoryErasureInventoryAsync`. Retain `TryStartSingleFlightAsync` for confirmation-only direct calls.

- [ ] **Step 10: Rerun the focused store, API/client, initiator, and same-process matrices to green.**

## Task 7: Bind and durably prepare the installation handoff

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/InstallationResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationStartupProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationStartupProbeTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs`

**Interfaces:**

- Persist a nullable discriminator so V1 legacy JSON remains readable and startup-blocking:

  ```csharp
  public enum InstallationResetDataHandoff
  {
      HostFactoryErasure,
  }

  public sealed record InstallationResetOnlineDataHandoff(
      Guid RequestedOperationId,
      string InstallationPlanId,
      string DataPlanId,
      bool DataResetCompleted);

  internal sealed record InstallationResetOnlineDataCompletion(
      Guid ServerOperationId,
      Guid RequestedOperationId,
      string DataPlanId,
      long RowsDeleted,
      long FilesDeleted,
      long EstimatedBytesDeleted,
      long DerivedRecordsDeleted);

  public interface IInstallationResetOnlineDataHandoff
  {
      Result<InstallationResetPlan> BindOnlineDataPlan(
          InstallationResetPlanRequest request,
          InstallationResetPlan localPlan,
          DataRetentionPlan onlinePlan);

      Task<Result<InstallationResetOnlineDataHandoff>> PrepareAsync(
          InstallationResetApplyRequest request,
          InstallationResetPlan confirmedPlan,
          CancellationToken cancellationToken = default);

      Task<Result<InstallationResetOnlineDataHandoff?>> ReadAsync(
          InstallationResetApplyRequest request,
          CancellationToken cancellationToken = default);

      Task<Result> RecordCompletedAsync(
          InstallationResetOnlineDataHandoff handoff,
          DataRetentionApplyResult result,
          CancellationToken cancellationToken = default);

      Task<Result> RetirePreEffectAsync(
          InstallationResetOnlineDataHandoff handoff,
          CancellationToken cancellationToken = default);
  }
  ```

- Append `InstallationResetDataHandoff? DataHandoff = null` and `InstallationResetOnlineDataCompletion? OnlineDataCompletion = null` to `InstallationResetActiveRecord`. `RecordCompletedAsync` requires `Reconciled == true` before constructing that proof. Expand `ActiveInstallationReset` with operation ID, phase, handoff, and whether that proof is durable.

- [ ] **Step 1: Write binding RED tests.** Prove a real local ordinary plan and online Covenant-bound plan may have different plan IDs while identical ordinary items/candidates/counts; binding replaces the data-plan ID and database predicate, recomputes binding and installation plan IDs, and leaves roots, exclusions, backups, credentials, daemon target, and counts unchanged. Mutate each ordinary candidate dimension and prove `Data.PlanChanged` before output.

- [ ] **Step 2: Run the binding RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationResetServiceTests.BindOnlineDataPlan"
  ```

  Expected: compile failure for the missing binding contract, replacing the current fake-equal-ID assumption.

- [ ] **Step 3: Implement binding using the existing canonical `ComputeBindingId` and `ComputePlanId` producers.** Compare the two data plans excluding only `PlanId`, `GeneratedAt`, and the online-only Covenant aggregate; never copy a changed ordinary candidate into the accepted installation plan.

- [ ] **Step 4: Write durable prepare/compatibility RED tests.** Cover exact rebound-plan revalidation before active publication, owner-only `Prepared` publication before any host mutation, legacy records with null handoff remaining valid, rejection of workspace/later-phase handoffs, and idempotent read of the same active requested ID.

- [ ] **Step 5: Run the active-state RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationResetServiceTests.PrepareOnlineDataHandoff|FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~InstallationStartupProbeTests"
  ```

- [ ] **Step 6: Implement the minimal handoff state.** Keep active-record version 1, deserialize absent discriminator/completion as legacy null, and permit host handoff only for global/all `Prepared` records with exactly one accepted data-plan ID.

- [ ] **Step 7: Write completion-proof RED tests.** Require a reconciled result with the confirmed data plan and echoed requested ID; durably record it before shutdown. Prove `InstallationResetService.ApplyAsync` then executes the existing daemon mutation, marks point-of-no-return, advances to `DataResetComplete` from the stored proof without calling offline factory apply, and continues cleanup/credentials. A pre-proof `Data.PlanChanged` may retire; any proof mismatch or uncertainty preserves the active record.

- [ ] **Step 8: Run the completion RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationResetServiceTests.RecordCompleted|FullyQualifiedName~InstallationResetServiceTests.Prepared_online_handoff"
  ```

- [ ] **Step 9: Implement proof recording and offline continuation.** The reduced `InstallationResetExistingGrimoire` remains V0 recovery-only and never receives Covenant lifecycle dependencies. The stored named-host result is the sole authority for skipping its public factory apply.

- [ ] **Step 10: Rerun the full installation-service/store/probe contract matrix to green.**

## Task 8: Sequence the CLI handoff before shutdown

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Program.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetArgvPreflightTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`

**Interfaces:**

- Keep `IInstallationResetApplyBoundary.ApplyAsync` for resume and add a fresh-plan entry point:

  ```csharp
  Task<Result<InstallationResetResult>> ApplyFreshAsync(
      InstallationResetPlanRequest request,
      InstallationResetPlan confirmedPlan,
      CancellationToken cancellationToken);
  ```

- [ ] **Step 1: Write command RED tests.** Prove global/all online validation no longer exact-compares the unbound local ID, requires an authenticated Covenant plan even for `--dry-run`, binds before disclosure/output/confirmation, and passes only the rebound plan to `ApplyFreshAsync`. Prove unreachable/missing-key global/all stops before disclosure, confirmation, active publication, shutdown, or deletion; workspace retains its offline behavior.

- [ ] **Step 2: Run the command RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationFactoryResetCommandTests"
  ```

  Expected: the real-ID case returns `Data.PlanChanged` and unreachable global/all still proceeds with a null advisory plan.

- [ ] **Step 3: Implement command binding.** Inject `IInstallationResetOnlineDataHandoff`, bind the authenticated global/all plan before dry-run or confirmation, render disclosure from that exact online inventory, and leave workspace planning unchanged.

- [ ] **Step 4: Write boundary ordering RED tests.** Assert exact event order:

  ```text
  prepare-active -> host-factory-apply -> record-completion-proof
  -> quit-host -> acquire-maintenance-lock -> offline-continuation
  ```

  Pin the host request's confirmed data plan/requested ID, response proof checks, pre-effect PlanChanged retirement without shutdown, uncertain failure preservation without shutdown, completed replay after a crash, and workspace's existing quit/lock/offline sequence.

- [ ] **Step 5: Run the boundary RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationResetApplyBoundaryTests"
  ```

- [ ] **Step 6: Implement the boundary sequence.** For fresh or resumed incomplete host handoff, call `FactoryResetDataAsync(new FactoryResetRequest("factory-reset", handoff.DataPlanId, handoff.RequestedOperationId))`; validate and persist its proof before shutdown. If proof is already durable, skip the host call. Only exact pre-effect `Data.PlanChanged` retires the active handoff. Every other failure returns without shutdown and leaves replay state.

- [ ] **Step 7: Write startup RED tests.** Admit `serve` only for global/all `Prepared + HostFactoryErasure` records whose online completion proof is not durable. Continue blocking `run`, legacy records, proof-complete records, and every later phase. Matching factory-reset resume remains admitted.

- [ ] **Step 8: Run the startup RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~InstallationFactoryResetArgvPreflightTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests"
  ```

- [ ] **Step 9: Implement both startup gates and rerun command/boundary/startup tests to green.** `Program` and `GrimoireDatabaseHostedService` must apply the same narrow predicate; no later installation phase may start the host.

## Task 9: Maintain exact data-LRO ownership through preparation and recovery

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Update all `IDataRetentionService` fakes after making capability-bearing admission non-default.

**Interfaces:**

- Change ordinary factory execution to require its exact durable owner:

  ```csharp
  private Task<DataRetentionApplyResult> ApplyFactoryResetAsync(
      Guid operationId,
      string leaseOwner,
      DataRetentionPlan plan,
      CancellationToken cancellationToken);
  ```

- Remove the lease-free default implementation of capability-bearing `IDataRetentionService.PlanAdmissionAsync`; every implementation must explicitly return the required lease or a typed refusal.

- [ ] **Step 1: Write the pre-checkpoint lease RED test.** Pause a real named/unnamed factory request after LRO creation but before V1 checkpoint publication, advance a short fake-time heartbeat beyond the initial lease, and prove the same owner/revision is renewed while no V0 reconciler can adopt or delete ordinary data.

- [ ] **Step 2: Run the direct-factory RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~CovenantErasureSameProcessTests.Factory_maintains_lease_before_checkpoint"
  ```

  Expected: no renewal occurs until the coordinator begins.

- [ ] **Step 3: Move the maintainer boundary to immediately after durable start.** Extract the remaining replan, catalog proof, V1 preparation, coordinator/continuation, and terminal CAS into one maintained action; do not nest a second maintainer around the coordinator. Link caller cancellation into the action while keeping post-proof terminalization cancellation-immune.

- [ ] **Step 4: Write exact-owner ordinary-cleanup RED tests.** Steal/expire the operation immediately before the factory transaction's renewal and prove the former worker cannot renew the new owner or delete rows/files. Pin the SQL predicate to operation ID, `Running`, exact `LeaseOwner`, and `LeaseExpiresAt > now`.

- [ ] **Step 5: Run the owner-loss RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~CovenantErasureSameProcessTests.Factory_owner_loss|FullyQualifiedName~DataRetentionServiceTests.FactoryReset_lost_owner"
  ```

- [ ] **Step 6: Thread `leaseOwner` through direct continuation and V0/V1 recovery.** Replace the transaction's owner-blind manual renewal with the exact-owner/live-expiry compare-and-swap and keep the independent maintainer heartbeat outside the workload connection.

- [ ] **Step 7: Write V1/V3 recovery-maintenance RED tests.** Pause each real coordinator recovery longer than the reconciler's two-minute lease under fake time, prove renewal by the adopted owner, then force renewal failure and prove the first worker cancels without completing or overwriting the new owner.

- [ ] **Step 8: Run the recovery RED filter.**

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo --filter "FullyQualifiedName~DataRetentionCovenantResetRecoveryTests.Recovery_maintains|FullyQualifiedName~CovenantErasureSameProcessTests.Recovery_owner_loss"
  ```

- [ ] **Step 9: Wrap V0/V1 factory and V3 reset recovery work in `DataRetentionLeaseMaintainer.RunAsync`.** Use the exact reconciler-adopted `LeaseOwner`, pass the maintained token into coordinator/ordinary work, and map ownership loss to a content-free requires-attention result without claiming a terminal CAS the worker no longer owns.

- [ ] **Step 10: Write the plan-admission RED compile/behavior tests, remove the default implementation, update every fake explicitly, and rerun API planning/serialization tests.** A protected caller can no longer conceal a missing lease behind `PlanAsync`.

- [ ] **Step 11: Rerun the combined direct/recovery/factory/reset matrix and perform targeted mutation checks for owner predicate, pre-checkpoint maintainer scope, and recovery maintainer removal.**

## Task 10: Update current documentation

**Files:**

- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Arcanum.DEBUGGING.Human.md`
- Modify: `docs/Arcanum.Design.Human.md`
- Modify: `docs/Arcanum.OATH.md`
- Modify: `docs/ArcanumOATH.Human.md`

1. Remove every current-document statement that Covenant reset/factory activation is unavailable or owned by future issue #128.
2. Document the reset preview endpoint, operator authority, exact protected headers, direct coordinator handoff, combined factory stages, recovery semantics, and external-disclosure boundary.
3. Document global/all online-plan rebinding before confirmation, the durable `Prepared` requested-identity handoff and completion proof, authenticated-host fail-closed behavior, shutdown ordering, and the narrow proof-absent startup-recovery exception.
4. Document lease maintenance from factory admission through V1 terminalization, exact-owner ordinary cleanup, and V1/V3 recovery renewal.
5. Keep the two OATH copies byte-identical where the repository requires mirrored content.
6. Do not edit historical #127 plans/specs, dated reviews, `Arcanum.CommandMap.json`, or config docs unless an actual contract change requires it.
7. Run documentation/contract tests and repository searches for the retired conflict and stale activation language.

## Task 11: Full verification, review, and delivery

**Files:** all changed files

1. Use `superpowers:requesting-code-review` for an independent diff review; address findings test-first.
2. Run formatting and the complete required matrix from the repository root:

   ```bash
   dotnet build RetroDownfall.Arcanum.slnx --nologo
   dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --nologo
   dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --nologo
   ./scripts/coverage.sh --threshold
   ./scripts/verify-aot-il-warnings.sh
   ```

3. Confirm every command reports zero warnings and zero errors, and inspect `git diff --check` plus `git status`.
4. Commit the complete green change once on `long-term-memory` with an issue-closing message.
5. Push `long-term-memory`, mark GitHub issue #128 completed, and verify its remote state.
6. Query issues #124, #125, #126, #127, and #128 and audit every acceptance item in parent #119 against the final tests and documentation. Complete any remaining in-scope requirement before closing #119.
7. Mark #119 completed only when all five sub-issues and the parent delivery contract are green; verify the remote issue state.
8. Delete only feature branches already merged into `long-term-memory`. Preserve the pre-existing untracked IDE file.
