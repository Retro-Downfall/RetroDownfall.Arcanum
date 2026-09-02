# Issue #239 Host-wide Grimoire Admission Implementation Plan

> **SUPERSEDED — DO NOT EXECUTE.** The approved direction now uses an authenticated external
> `GrimoireOfflineTransition` journal and keeps ordinary admission closed through database
> reconciliation and retirement. Replace this plan only after the revised written design is reviewed.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every ordinary live-Grimoire access path wait or fail before SQLite can reopen the database during Covenant erasure, while preserving narrowly authorized erasure, recovery, renewal, streaming, and background-worker behavior.

**Architecture:** Add one process-local `IGrimoireConnectionAdmissionGate` beside the durable Covenant gate. The new gate owns generation-bound physical-open tickets, request and work leases, atomic external-effect groups, a two-stage closing owner, exact-connection and one-shot maintenance authorities, and the process-local maintenance/adoption interlock. Wire that gate at every EF options path and raw acquisition site, make `CovenantErasureCoordinator` own the full closing/finalization lifecycle, then teach API streams and the three database-opening workers to quiesce at durable boundaries.

**Tech Stack:** .NET 10, C# 13, EF Core 10 interceptors, Microsoft.Data.Sqlite/SQLCipher, ASP.NET Core middleware and SSE, xUnit, Native AOT source-generated JSON, Bash, Git, GitHub CLI/GraphQL.

**Spec:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`

## Global Constraints

- Work only on `codex/issue-239-grimoire-admission`, based on `origin/main` commit `988a469c765346132e5a2ea1bf3906519f6bdf00`, until the fully verified tree is fast-forwarded to `main`.
- Preserve the two unrelated untracked issue-221 duplicate documents exactly as found; never stage, edit, delete, or move them.
- The approved spec is authoritative. Do not add a public route, CLI verb, configuration key, schema object, migration, or durable gate journal.
- `ICovenantOperationGate` remains durable destructive-operation authority. `IGrimoireConnectionAdmissionGate` controls only process-local live-Grimoire work and connection admission.
- Every ordinary physical open is rejected before SQLite or is physically closed when a close-during-open generation race is lost. Stage 2 waits for open attempts, not arbitrary EF connection lifetime.
- Request and work leases drain before connection admission closes. An external-effect group that has begun drains through its full durable disposition; a revoked group makes zero provider calls.
- The direct reset/factory request may promote only its own request lease and exact scoped `DbConnection`; every other request still drains.
- Lease renewal remains on its existing independent unpooled connection. Maintenance I/O and expired-lease adoption share one process-local interlock and revalidate durable ownership after winning it.
- Fresh live maintenance connections require an owner/generation/path/mode/purpose-bound one-shot runtime capability and return a tracked physically closed lease. Source classification is never runtime authority.
- Final durable terminal state is written only on the spec-prescribed side of final physical close, pool drain, residual proof, Covenant disposition, and Grimoire disposition. Hold the adoption interlock through reopen/closed disposition and terminal CAS.
- Maintenance refusal is expected control flow: `/api` uses `Grimoire.MaintenanceUnavailable`; `/v1` uses `service_unavailable`; neither exposes internal state nor logs at Error.
- Preserve C# house style, source-generated JSON, AOT safety, exact cancellation semantics, and zero errors/warnings.
- Observe RED before writing each production behavior. Focused RED/GREEN tests may run as needed; the complete qualification matrix runs only once on the reviewed final tree.
- Use `/opt/homebrew/bin/rg --no-config` (or `RIPGREP_CONFIG_PATH=/dev/null`) and `--disable-build-servers -m:1` for large .NET commands.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs` — internal gate, lease, owner, capability, lane, tracked-handle, and maintenance-exception contracts.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs` — singleton generation state machine and process-local adoption interlock.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs` — pre-open admission plus post-open drain enrollment and exactly-once cleanup.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs` and `DependencyInjection/ServiceCollectionExtensions.cs` — all production pooled/non-pooled EF options paths receive the same singleton gate and drain.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs` — consumes one-shot maintenance-open authorities and returns tracked leases.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs` — exact owner renewal ticket, independent open, shared lane/adoption interlock, and terminal/adoption coordination.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs` — owns both exclusive lifetimes, exact connection permit, lane, phase capabilities, physical finalization, and typed post-disposition finalizer.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService*.cs` — request promotion and disposition-dependent terminal CAS policy.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`, `Operations/LongRunningOperationStartupHostedService.cs`, and Covenant recovery helpers — adopted-owner/exact-unopened-context recovery handoff before readiness.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs` — scoped async-disposable lease carrier shared from API middleware into `DataRetentionService` without reversing the project dependency direction.
- `src/RetroDownfall.Arcanum.Api/Middleware/GrimoireRequestAdmissionMiddleware.cs` and `ApiBootstrapper.cs` — pre-endpoint request lifetime, route-kind selection, and owner-matched promotion orchestration.
- `src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs` plus the five inventoried stream endpoints — complete-frame cooperative quiescence.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs`, `Hosting/SagaExtractionService.cs`, and `Weave/SessionAttachmentIndexingService.cs` / `SessionAttachmentIndexProcessor.cs` — worker leases, atomic effect groups, and non-spinning deferral.
- `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs` — pure gate and race contract.
- `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` — bidirectional exact call-site inventory.
- `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs` — every product EF options path shares the same gate/drain interceptor.
- Existing Covenant, API, stream, worker, bootstrap, and same-process test suites — integration coverage at their current deterministic seams.

---

### Task 1: Establish ordinary admission, generations, and two-stage closure

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`

**Interfaces:**
- `IGrimoireConnectionAdmissionGate.AcquireOrdinaryOpen(DbConnection)` returns one open-attempt ticket and throws `GrimoireMaintenanceUnavailableException` before native open when admission is closed.
- `BeginOrResumeExclusive(CovenantExclusiveRecoveryOwner)` performs the immediate atomic state change and returns an owner-bound closing token; Task 2 extends that entrypoint with exact initiator promotion and adds `DrainRequestAndWorkAsync` as the bounded await within stage 1.
- `CloseConnectionAdmissionAsync(IGrimoireClosingOwner, CancellationToken)` advances the generation, revokes unresolved opens, boundedly waits for their terminal callback, and returns an exclusive closed lease.
- Tickets expose explicit `MarkOpened`, `MarkFailed`, and `MarkRefusedAfterOpen` one-shot transitions; disposal is cleanup, not implicit success.

- [ ] **Step 1: Write pure state-machine tests before contracts exist**

Add tests named:

```text
Ordinary_open_ticket_is_available_before_closing
Connection_close_advances_generation_and_refuses_new_open_before_native_io
Close_waits_for_a_preexisting_native_open_attempt_to_resolve
Open_that_loses_the_generation_race_must_be_refused_after_physical_close
Opening_timeout_leaves_admission_closed_and_does_not_issue_a_closed_lease
Owner_generation_and_double_disposition_mismatches_are_rejected
Next_open_generation_completes_once_only_after_commit_reopen
Keep_closed_never_completes_the_next_open_generation
```

Use `TaskCompletionSource` barriers and a short injected timeout/time provider; do not use sleeps.

- [ ] **Step 2: Run the focused tests and record RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionGateTests"
```

Expected: compilation fails because the gate contracts/types do not exist. This is the required missing-behavior RED.

- [ ] **Step 3: Implement the minimal generation state machine**

Use one private lock for state transitions and immutable internal token identity. The implementation must represent ordinary, closing, and closed states separately and must never reopen from `Dispose`:

```csharp
internal interface IGrimoireConnectionOpenTicket : IDisposable
{

    long Generation { get; }

    Result MarkOpened();

    void MarkFailed();

    void MarkRefusedAfterOpen();

}
```

Maintain a set of unresolved physical-open tickets. Stage 2 first changes the generation/state, signals every unresolved ticket to refuse after native completion, then waits for all terminal callbacks. Return a typed closed lease only when the set is empty.

- [ ] **Step 4: Run the focused gate tests GREEN**

Run the Step 2 command. Expected: all gate tests pass with no warning.

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "feat: add Grimoire admission state machine"
```

---

### Task 2: Add request, work, effect-group, promotion, and async-disposal lifetimes

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`

**Interfaces:**
- `TryAcquireRequestLease(GrimoireRequestKind)` and `TryAcquireWorkLease(GrimoireWorkKind)` fail once stage 1 begins.
- `IGrimoireWorkLease.TryBeginExternalEffectGroup()` is atomic with revocation and returns a guard held through durable disposition.
- `MaintenanceRevocation` signals admitted quiesceable work without cancelling an effect group that already won.
- Initiator promotion requires the exact request-lease reference, Covenant owner, and exact scoped connection.
- Request/work acquisition installs a non-serializable flow-bound lifetime token. During stage 1, `AcquireOrdinaryOpen` accepts only a still-live admitted lifetime token (or the later exact maintenance permit); an unrelated raw caller cannot exploit the finisher window.
- `WaitForNextOpenGenerationAsync(long observedGeneration, CancellationToken stoppingToken)` is cancellation-aware; `KeepClosed` does not complete it, but host shutdown must cancel and fully observe every registered wait.

- [ ] **Step 1: Add deterministic RED tests for all lifetime races**

Cover:

```text
Exclusive_waits_for_another_request_through_async_scope_disposal
Promotion_removes_only_the_exact_owner_matched_initiating_request
Promotion_rejects_another_request_owner_or_connection
Revocation_wins_effect_race_and_provider_frontier_cannot_start
Effect_start_wins_race_and_closure_waits_through_durable_disposition
Denied_work_waits_for_a_later_open_generation_without_spinning
Stage_one_open_requires_the_exact_still_live_finisher_lifetime
Stage_one_timeout_stays_closing_denies_new_work_allows_finisher_opens_and_starts_no_destructive_work
The_same_owner_can_resume_a_timed_out_stage_one_transition
Only_proven_pre_erasure_safety_can_abort_a_timed_out_stage_one_transition
```

Model async scope disposal with an `IAsyncDisposable` sentinel whose barrier releases after the simulated database holder.

- [ ] **Step 2: Run the Task 1 filter and observe RED**

Expected: new tests fail because request/work/effect/promotion methods are absent.

- [ ] **Step 3: Implement leases and atomic effect groups**

Count ordinary request/work leases independently. Stage 1's synchronous begin atomically sets closing, promotes at most the exact initiator, signals maintenance revocation, and returns its closing token; `DrainRequestAndWorkAsync` then boundedly awaits all other lease/effect holders. A work lease may have at most one active effect guard at a time and must not release its drain count until the guard and its async scope have disposed.

A bounded stage-1 timeout keeps the state recoverably closing: new request/work acquisition remains refused, ordinary opens remain available only to already admitted finishers, and no stage-2/destructive authority exists. Only the same durable owner may resume; abort is a distinct owner-bound operation available only after a callback proves no destructive effect occurred.

- [ ] **Step 4: Re-run the focused gate tests GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "feat: drain Grimoire request and work lifetimes"
```

---

### Task 3: Add maintenance authorities and the adoption/I/O interlock

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`

**Interfaces:**
- Exact scoped permit binds by `ReferenceEquals` to one `DbConnection`, owner, and generation.
- One-shot renewal ticket authorizes one unpooled open, one exact-owner renewal CAS, and physical close.
- One-shot factory capability binds `CovenantMaintenanceConnectionPurpose`, `CovenantMaintenanceConnectionMode`, canonical path identity, owner, and generation.
- `AcquireMaintenanceIoLaneAsync` and `AcquireExpiredLeaseAdoptionInterlockAsync` are two typed entrypoints over the same process-local semaphore/owner state and both require a post-acquisition durable-owner revalidation callback.

- [ ] **Step 1: Add capability and two-winner race RED tests**

Cover exact connection reuse, foreign connection rejection, one-shot consumption, path/mode/purpose widening rejection, live tracked-handle refusal at disposition, lane-first blocking adoption, adoption-first preventing incumbent phase entry, adoption held through reopen/terminal CAS, and an overrun step selecting `KeepClosed` before any next phase.

- [ ] **Step 2: Run the focused gate filter and observe RED**

- [ ] **Step 3: Implement narrow non-serializable authorities**

All authority objects stay `internal`, carry opaque reference identity, and expose no public constructor. A capability validates before SQLite construction/open and becomes consumed exactly once. A tracked handle reports physical closure before its lane can dispose. The closed owner refuses disposition while any ticket, permit-open connection, renewal, factory lease, or unresolved ordinary open remains live.

- [ ] **Step 4: Re-run the focused gate filter GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "feat: authorize narrow Grimoire maintenance opens"
```

---

### Task 4: Enforce admission in every EF options path

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs`

**Interfaces:**
- The lifecycle interceptor acquires a ticket in `ConnectionOpening[Async]`, revalidates/enrolls in `ConnectionOpened[Async]`, and releases state exactly once in `ConnectionFailed[Async]`, `ConnectionClosed[Async]`, and `ConnectionDisposed[Async]`.
- `ArcanumDbContextOptionsConfigurator.Configure` receives both the singleton gate and drain for every ordinary product composition.

- [ ] **Step 1: Write interceptor and composition RED tests**

Tests must prove pre-open refusal makes zero provider opens; a close-during-open race physically closes the new handle before throwing; drain enrollment occurs only after successful revalidation; sync/async failure, close, and dispose callbacks clean exactly once; both production `AddDbContext` and `AddDbContextPool` paths and the API test host share the singleton gate/drain. Name `ArcanumDbContextFactory`, fallback `OnConfiguring`, installation bootstrap, and stopped-host reset as explicit non-serving exemptions.

- [ ] **Step 2: Run the new interceptor/composition tests and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireDbContextCompositionTests|FullyQualifiedName~CovenantConnectionDrainTests"
```

- [ ] **Step 3: Extend the interceptor and composition**

Keep the existing reference-counted drain enrollment semantics. Store one lifecycle state per physical `DbConnection` in a `ConditionalWeakTable`; do not let one logical close unregister another holder. Pass the same singleton instances through both production options callbacks and the test-host replacement.

- [ ] **Step 4: Run the Task 4 filter GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContext.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs
git commit -m "feat: enforce Grimoire admission for EF opens"
```

---

### Task 5: Authorize every acquisition and make erasure one atomic lifecycle

**Files:**
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/ProductionSourceInventory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantConnectionSource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionLeaseMaintainer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureTransition.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionMutationRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionFactoryResetRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Inspect and either runtime-protect or classify by exact method/path authority every current raw-open match in Api `Health/GrimoireLivenessProbe.cs`, `Workspaces/WorkspaceDivinationEndpoints.cs`, `Tower/MemoryEndpoints.cs`, `Tower/SessionDivinationEndpoints.cs`, `Intelligence/WizardIntelligenceProvider.cs`, and Infrastructure `Repositories/SessionEntryPersistence.cs`, `Repositories/GrimoireRepository.TurnCommit.cs`, `Diagnostics/GrimoireDiagnostics.cs`, `Weave/EmbeddingsResetService.cs`, `Covenant/CovenantCampaignScopeProbe.cs`, `Backup/*.cs`, `Hosting/GrimoireDatabaseBootstrapper.cs`, and `Data/SqliteNativeRuntimeValidator.cs`.
- Modify: corresponding focused tests, every `ICovenantMaintenanceConnectionFactory` fake, `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`, `Data/DataRetentionLeaseMaintainerTests.cs`, `Operations/LongRunningOperationReconcilerTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`, `Data/Covenant/CovenantErasureSameProcessTests.cs`, and `Data/DataRetentionCovenantResetRecoveryTests.cs`.
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionSourceTests.cs` for the raw source's pre-open/post-open lifecycle.

**Interfaces:**
- The bidirectional source inventory keys each `Open` / `OpenAsync` and maintenance-factory acquisition by repository path, enclosing method, acquisition kind, path authority, and named rationale.
- Live ordinary raw opens use `AcquireOrdinaryOpen` before native open and the same post-open revalidation/drain cleanup as EF. Non-live staged/design-time/native/bootstrap/stopped-host matches have exact named path authority, never broad exemptions.
- The closed erasure owner issues each one-shot factory capability; canonical factory calls return `ITrackedCovenantMaintenanceConnection`. No compatibility/default authority overload exists.
- `LongRunningOperationStore` exposes narrow internal renewal overloads for the coordinator-created closing and closed bindings. A closing-phase renewal ticket is owner-bound but registers as an ordinary-generation physical-open attempt that stage 2 must drain; only the closed binding can issue exclusive renewal tickets. Ordinary `ILongRunningOperationStore.RenewLeaseAsync` remains unchanged for backups and unrelated operation kinds.
- A validated Covenant adoption candidate includes kind and revision in the expired-owner CAS predicate while the shared adoption interlock is held, followed by exact re-read/revalidation.
- An Infrastructure-owned scoped `GrimoireRequestAdmissionScope` is empty for non-HTTP/recovery work and later populated by API middleware.
- Callers create only `GrimoireErasureInvocation`, carrying one scope's exact `DbConnection`, optional exact request lease, and typed finalizer. Immediately after atomic begin/promotion, the coordinator creates `GrimoireErasureClosingBinding` with durable owner, current generation, promoted token, and closing-state renewal authority. After stage 2 it upgrades to `GrimoireErasureExecutionBinding` with the exact connection permit. Callers can never mint gate authority.
- The typed finalizer reports the exact durable terminal/attention state and revision so reconciliation can re-read/accept it without a second CAS.

- [ ] **Step 1: Add the bidirectional inventory and observe the first RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
```

Expected: FAIL listing at least one concrete path+method acquisition that lacks runtime admission or exact non-live classification. Keep this test failing until the whole task is integrated; never add a temporary exemption.

- [ ] **Step 2: Add raw, factory, renewal, adoption, and coordinator RED tests**

Raw tests cover pre-native refusal, close-during-open physical closure, and drain cleanup. Factory tests cover purpose/mode/path binding, one-shot use, and physical tracked-lease closure. Renewal tests retain the independent `Pooling=False` invariant and cover native-open/policy failure. Adoption tests cover lane-first/adoption-first and expected kind/revision mismatch.

Coordinator tests cover stage 1→stage 2 ordering; direct-reset and factory initiator promotion; caller invocation versus both coordinator-only bindings; exact scoped binding; unresolved-open wait; every phase's factory authority and lane; pre-lane renewal; heartbeat transition from ordinary pre-close renewal to owner-bound closing renewal and then exclusive closed renewal; overrun `KeepClosed`; all crash windows; and reconciler acceptance of handler-owned terminalization. Pause another admitted request/effect group beyond a heartbeat interval and prove exact renewal succeeds while stage 1 remains closing without minting ordinary raw-open authority. Race a closing-phase renewal native open with stage 2 and prove the open is tracked, physically closed/refused if it loses the generation, and awaited before the closed lease issues.

Add these named disposition cases:

```text
Commit_keeps_row_recoverable_until_exact_handle_close_pool_drain_proof_and_reopen
Completed_CAS_occurs_after_Grimoire_reopens_while_adoption_interlock_is_held
KeepClosed_writes_ReconciliationRequired_before_final_close_and_never_reopens
Proven_abort_closes_exact_handle_reopens_writer_and_both_gates_before_Failed_CAS
Pre_disposition_proof_failure_explicitly_keeps_Grimoire_closed
Post_reopen_writer_or_CAS_failure_preserves_ReopenedVerified_with_gates_open
Final_disposition_refuses_an_open_exact_handle_or_live_factory_or_renewal_lease
```

- [ ] **Step 3: Run the combined focused cluster and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~CovenantConnectionSourceTests|FullyQualifiedName~CovenantMaintenanceConnectionFactoryTests|FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~DataRetentionLeaseMaintainerTests|FullyQualifiedName~LongRunningOperationReconcilerTests|FullyQualifiedName~CovenantErasureCoordinatorTests|FullyQualifiedName~CovenantErasureSameProcessTests|FullyQualifiedName~DataRetentionCovenantResetRecoveryTests"
```

- [ ] **Step 4: Implement ordinary raw admission, typed renewal, and adoption primitives**

Protect or convert every live raw match reported by the inventory. Keep exact non-live classifications bidirectional. The exclusive renewal sequence is: validate/consume ticket before construction → construct unpooled connection → native `OpenAsync` → apply/verify policy → exact-owner/live-lease CAS → physical `DisposeAsync` → release ticket/lane. Every failure consumes/releases once.

Synchronously renew through ordinary admission immediately before beginning stage 1. Atomic begin then returns the closing token before the request/work drain await, allowing the coordinator to construct its closing binding. Its owner-bound one-shot renewal ticket remains part of the current ordinary generation's unresolved-open set, so stage 2 revokes/waits it exactly like any other native open; it does not require a request/work flow token and grants no general ordinary authority. Only after `CloseConnectionAdmissionAsync` returns the closed lease may the coordinator upgrade to exclusive renewal tickets. Owner/generation mismatch fails closed. Unrelated maintainer callers retain the ordinary interface.

- [ ] **Step 5: Implement factory authority, coordinator issuance, and finalization together**

Do not add a permissive factory overload or derive authority from an ambient operation id. The coordinator renews for the normal durable interval before each potentially long sensitive step, then takes the maintenance/adoption interlock, re-reads exact owner/expiry, and starts the step. Iterative work yields at existing idempotent boundaries for later renewal. Each phase capability is minted immediately before its legal call, consumed once, and physically disposed before lane release.

Pass the same scoped invocation from direct reset, healthy-catalog factory activation, and both recovery scopes. Only `RunAsync` creates the closing binding after atomic begin and the owner/generation/permit phase binding after stage 2. `RunAsync` owns the lifecycle through return and applies these exact interlock-held branches:

1. `CommitAndReopen`: Covenant `CommitAndReopen`; while the row remains recoverable, physically close the exact scoped handle, revoke its permit, drain/clear pools, and prove residual state; Grimoire `CommitAndReopen`; reopen the persistent disclosure writer through ordinary admission; ordinary `Completed` CAS.
2. `KeepClosed`: Covenant `KeepClosed`; write/retain `ReconciliationRequired` while the exact permit remains valid; physically close the exact handle, revoke it, drain/clear pools, and prove residual state; Grimoire `KeepClosed`; no terminal state.
3. Proven pre-erasure abort: Covenant `RollbackAndReopen`; physically close the exact handle and every tracked/renewal handle; revoke the permit; Grimoire rollback/reopen; reopen the disclosure writer through ordinary admission (or restore its ordinary lazy-open state); ordinary terminal `Failed` CAS. Prove a subsequent disclosure write succeeds.

A close/proof failure before Grimoire disposition explicitly preserves the recoverable row and dispositions Grimoire `KeepClosed`; no owner falls through disposal. A writer/terminal-CAS failure after Grimoire reopen re-reads and returns exact `ReopenedVerified` recovery state with gates open. The adoption interlock remains held until each durable outcome is proven.

- [ ] **Step 6: Run the combined cluster GREEN**

Run the Step 3 command. Then run every additional focused class added or modified for the final inventory manifest, including API, repository, diagnostic, backup, bootstrap, and native-probe classifications; do not accept a zero-test filter. Expected: all pass, the inventory has no missing or stale entries, and no compatibility authority path remains.

- [ ] **Step 7: Commit the indivisible acquisition/erasure slice**

Review `git diff --name-only`, then stage every exact implementation/test file from this task individually; never stage a whole directory or either preserved untracked issue-221 document.

```bash
git commit -m "feat: enforce Grimoire admission through erasure"
```

---

### Task 6: Reconstruct closed admission before startup readiness

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationStartupHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/InstallationResetRecoveryAwareHostedService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantResetBootstrapBarrierTests.cs`

**Interfaces:**
- Bootstrap adopter returns the durable Covenant owner plus the checkpoint-derived operation identity; it also reconstructs the matching Grimoire closing owner.
- Before publishing API/worker readiness, startup resolves an unopened scoped `ArcanumDbContext`, obtains its exact `Database.GetDbConnection()` object, binds it to the adopted owner, and resumes recovery.

- [ ] **Step 1: Add adopted-owner/exact-context RED tests**

Prove every current Covenant checkpoint phase adopts its exact owner before readiness; a caller-supplied mismatched operation id fails; the install connection is physically closed before recovery; the recovery context is unopened at bind time; a still-live prior-process lease waits deterministically until expiry rather than releasing readiness; and no API or Entry/Attachment/Saga worker passes the barrier before recovery has disposed/reopened or kept closed.

- [ ] **Step 2: Run bootstrap/fresh-process filters and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureStartupRecoveryOwnerAdopterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~CovenantErasureFreshProcessRecoveryTests|FullyQualifiedName~CovenantResetBootstrapBarrierTests"
```

- [ ] **Step 3: Implement the bootstrap handoff without an ordinary open**

After the install handle is closed, run targeted Covenant recovery inside one retained `AsyncServiceScope`: obtain that scope's unopened `db.Database.GetDbConnection()` reference, bind it, wait for an unexpired previous lease to become adoptable, acquire it under the shared interlock, and complete or `KeepClosed` before readiness. Do not route this protected recovery through the generic hosted service's ten-second defer-to-background budget. Generic non-Covenant reconciliation may remain later. Add `SagaExtractionService` to the gated-hosted-service inventory alongside Entry and Attachment.

- [ ] **Step 4: Run bootstrap/fresh-process filters GREEN**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureStartupRecoveryOwnerAdopterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~CovenantErasureFreshProcessRecoveryTests|FullyQualifiedName~CovenantResetBootstrapBarrierTests"
```

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationStartupHostedService.cs src/RetroDownfall.Arcanum.Infrastructure/Hosting/InstallationResetRecoveryAwareHostedService.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs tests/RetroDownfall.Arcanum.Tests/Operations/CovenantResetBootstrapBarrierTests.cs
git commit -m "feat: restore Grimoire admission before startup readiness"
```

---

### Task 7: Add API request admission and stable maintenance errors

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Middleware/GrimoireRequestAdmissionMiddleware.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireRequestAdmissionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/ArcanumExceptionHandlerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Primitives/ArcanumErrorMapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`

**Interfaces:**
- Split the current combined pipeline into authentication → Grimoire admission → installation-reset/Covenant pre-binding. Admission applies by `/api` or `/v1` path, never by API-key metadata: authenticated `/metrics` bypasses it, while an anonymous/peer-authenticated `/api` route still receives it.
- A first-resolved scoped async-disposable holder owns the lease until reverse-order request-scope disposal and exposes a typed owner-promotion feature to `DataRetentionService`.
- The holder is Infrastructure-owned. API middleware populates it; direct reset and factory activation use it to create the Task 5 caller invocation; startup/recovery leaves it empty.

- [ ] **Step 1: Add pre-binding and disposal-order RED tests**

Use a minimal TestServer endpoint with a later-created async-disposable sentinel. Prove a refused request never executes, invalid API keys remain `401`, another admitted request blocks closure through endpoint and scope disposal, and only the exact direct/factory initiator promotes. With a valid key plus malformed Covenant context during closing, assert maintenance `503` wins and no installation-reset/Covenant refusal or issuer call runs. Add an authenticated `/metrics` bypass and a no-API-key-metadata `/api` route that is still admitted/refused.

- [ ] **Step 2: Add sanitized envelope RED tests**

`/api/**` must return source-generated `ApiResponse<string>` with status 503/code `Grimoire.MaintenanceUnavailable`; `/v1/**` must return the existing OpenAI error shape with type `service_unavailable`. Assert no path/owner/checkpoint/native detail and no Error-level log. If a response has started, the handler returns false and does not rewrite it.

- [ ] **Step 3: Run the API middleware/error/retention filters and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireRequestAdmissionTests|FullyQualifiedName~ArcanumExceptionHandlerTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~DataRetentionEndpointTests"
```

- [ ] **Step 4: Implement request admission and error mapping**

Split authentication from the existing installation-reset/Covenant pre-binding work and put path-selected admission between them. Register the holder as scoped and resolve it there before any later scoped service. Do not dispose it in the middleware `finally`; let the request service scope dispose it after later scoped objects. Add only source-generated response shapes already registered by the API.

Pass the populated Infrastructure scope into both `DataRetentionService.cs` direct reset and `DataRetentionService.FactoryActivation.cs` healthy-catalog factory erasure. Add an endpoint-to-coordinator promotion test for each. Recovery callers remain context-free.

- [ ] **Step 5: Run the same API filters GREEN**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireRequestAdmissionTests|FullyQualifiedName~ArcanumExceptionHandlerTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~DataRetentionEndpointTests"
```

- [ ] **Step 6: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs src/RetroDownfall.Arcanum.Api/Middleware/GrimoireRequestAdmissionMiddleware.cs src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireRequestAdmissionTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Middleware/ArcanumExceptionHandlerTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Primitives/ArcanumErrorMapperTests.cs tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs
git commit -m "feat: refuse API work during Grimoire maintenance"
```

Inspect the staged API paths and keep the commit limited to the files named above.

---

### Task 8: Quiesce exactly the five unbounded streams at frame boundaries

**Files:**
- Create: `src/RetroDownfall.Arcanum.Api/Streaming/GrimoireQuiesceableStreamMetadata.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Conclave/ApprenticeEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Middleware/GrimoireRequestAdmissionMiddleware.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Streaming/SseStreamWriterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/LogsEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SessionEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/ApprenticeEndpointTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireQuiesceableStreamContractTests.cs`

**Interfaces:**
- Route metadata marks exactly daemon, MCP, logs, session watch, and apprentice Chronicle as maintenance-quiesceable.
- `SseStreamWriter` accepts a separate between-frame quiescence token. It may cancel a pending wait, but an already-started frame uses only caller/host cancellation and completes before the stream ends.
- Request middleware selects `GrimoireRequestKind.QuiesceableStream` from endpoint metadata after routing and exposes its lease's `MaintenanceRevocation` through the Infrastructure-owned scoped holder.

- [ ] **Step 1: Add frame-boundary and exact-inventory RED tests**

Prove maintenance during a pending move ends promptly; maintenance during frame serialization/write finishes exactly one syntactically complete frame then ends; the five routes are marked; `/v1/chat/completions`, `/api/intelligence/ping-stream`, prompt/spell execute streams, and workflow NDJSON remain finite/billable and unmarked. Add endpoint-level cases for the logs connected comment, Session replay/buffer drain, and Chronicle synthetic replay/buffer drain, not only shared-writer cases.

- [ ] **Step 2: Run the stream filters and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SseStreamWriterTests|FullyQualifiedName~LogsEndpointTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~ApprenticeEndpointTests|FullyQualifiedName~GrimoireQuiesceableStreamContractTests"
```

- [ ] **Step 3: Implement split cancellation, route metadata, and direct-frame checks**

Do not link maintenance cancellation into a provider/billable stream or the write token for an in-progress frame. For each direct replay/sentinel frame, check maintenance between frames, write the current frame with only client/host cancellation, then stop. Cancel and await the Session and Chronicle producer pumps in `finally`. The maintenance path ends an existing successful response and does not ask the exception handler to replace it.

- [ ] **Step 4: Run the same stream filters GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Api/Streaming/GrimoireQuiesceableStreamMetadata.cs src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs src/RetroDownfall.Arcanum.Api/Conclave/ApprenticeEndpoints.cs src/RetroDownfall.Arcanum.Api/Middleware/GrimoireRequestAdmissionMiddleware.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs tests/RetroDownfall.Arcanum.Tests/Api/Streaming/SseStreamWriterTests.cs tests/RetroDownfall.Arcanum.Tests/Api/LogsEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SessionEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/ApprenticeEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireQuiesceableStreamContractTests.cs
git commit -m "feat: quiesce unbounded streams for Grimoire maintenance"
```

---

### Task 9: Protect Entry weaving as one durable effect group

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/EntryWeavingServiceTests.cs`

**Interfaces:**
- A work lease is acquired before scope creation and held through every upsert and async scope disposal.
- One effect group begins immediately before `EmbedBatchAsync` and spans all returned `UpsertEmbeddingAsync` calls.
- Change the internal tick seam to `Task<EntryWeavingTickOutcome> RunTickAsync(...)`, where `EntryWeavingTickOutcome.DeferredForMaintenance` is distinct from completion and genuine failure.

- [ ] **Step 1: Add deterministic RED tests**

Use three separate barriers: denied work admission creates no scope; admitted work whose revocation wins at `TryBeginExternalEffectGroup` may have read inside a scope but makes zero provider calls/writes and drains async scope disposal; effect-start-first makes closure wait through provider, all upserts, and async scope disposal. A deferred tick never enters the generic one-second fault loop.

- [ ] **Step 2: Run the focused worker test and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~EntryWeavingServiceTests"
```

- [ ] **Step 3: Implement minimal work/effect protection**

Use caller/host cancellation after the group starts, not maintenance revocation. Return a distinct deferred result that neither advances progress nor logs a fault.

- [ ] **Step 4: Run `EntryWeavingServiceTests` GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs tests/RetroDownfall.Arcanum.Tests/Weave/EntryWeavingServiceTests.cs
git commit -m "feat: quiesce entry weaving during Grimoire maintenance"
```

---

### Task 10: Protect attachment indexing at each durable batch

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexProcessor.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingQueueTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingTests.cs`

**Interfaces:**
- Both channel processing and `ReconcileAndEnqueueAsync` acquire work before scope creation.
- Every `EmbedBatchAsync` → `AppendReplaceBatchAsync` pair is one independently resumable effect group; completed batches remain durable.
- Extend `SessionAttachmentIndexOutcome` with an internal `SessionAttachmentIndexDisposition` value whose `DeferredForMaintenance` member is neither `Failed` nor `ShouldRetry`.
- Pass the exact outer lease into `ProcessAsync(SessionAttachmentIndexRequest, IGrimoireWorkLease, CancellationToken)`; the scoped processor must not acquire a second work lease.

- [ ] **Step 1: Add queue/refusal and batch-race RED tests**

Prove refusal preserves pending item and attempt count, never calls `MarkFailed`, does not spin, and re-signals only on a later open generation or normal reconciliation interval. Cover channel consumption and a separately denied `ReconcileAndEnqueueAsync` that creates no scope and resumes once after reopen/cadence. For a later batch, revocation-first makes no additional provider call and preserves the prior durable batch count; effect-start-first completes the provider result plus its success/failure durable classification. Block async scope disposal and prove stage 1 still waits. Reject a processor that acquires a second lease.

- [ ] **Step 2: Run the attachment filters and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SessionAttachmentIndexingQueueTests|FullyQualifiedName~SessionAttachmentIndexingTests"
```

- [ ] **Step 3: Implement deferral outside generic retry/failure handling**

Keep the work lease through async scope disposal. Do not manufacture a retry or discard already checkpointed batches. Register one owned, observed, cancellation-aware generation continuation that directly awaits `_channel.Writer.WriteAsync(request, stoppingToken)` for the retained `_pending` request; do not call the already-pending no-op `TryEnqueue`. Test an already-pending item, a full bounded queue at reopen, and hosted-service shutdown while admission stays `KeepClosed` (no leaked or late re-signal).

- [ ] **Step 4: Run both attachment test classes GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexingService.cs src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexProcessor.cs tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingQueueTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingTests.cs
git commit -m "feat: quiesce attachment indexing during maintenance"
```

---

### Task 11: Protect Saga extraction by page and re-signal exactly once

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs`

**Interfaces:**
- Replace both internal `Task<bool> ExtractForSessionAsync(...)` seams with `Task<SagaExtractionOutcome>`, whose values distinguish `Completed`, `Retry`, and `DeferredForMaintenance`.
- The page effect group spans the LLM request, every memory embedding, all durable inserts, and `SetWatermarkAsync`.
- A denied pending session registers one next-open-generation continuation that writes its existing id directly to the channel once.
- Pass the exact outer `IGrimoireWorkLease` into both `ExtractForSessionAsync` overloads; neither helper may acquire a second lease after scope creation.

- [ ] **Step 1: Add close/reopen and whole-page RED tests**

Prove work admission happens before removing `_pending` or creating a scope. A denied extraction preserves `_pending`, watermark, retry attempt, and eligible time; it does not spin; reopening reprocesses that same id without a user turn; repeated reopening signals do not duplicate it. Test revocation-before-page with zero LLM calls and page-start-before-revocation draining through embeddings, writes, watermark, and async scope disposal. Reject a helper that acquires a second lease.

- [ ] **Step 2: Run `SagaExtractionServiceTests` and observe RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaExtractionServiceTests"
```

- [ ] **Step 3: Implement deferred outcome and direct channel re-signal**

Do not call `EnqueueExtraction` for an id already in `_pending`; one owned, observed continuation must await the cancellation-aware next-generation signal and directly signal the channel while retaining existing pending state. Test that hosted-service shutdown completes under `KeepClosed` with no late write, while a normal reopen still signals exactly once.

- [ ] **Step 4: Run `SagaExtractionServiceTests` GREEN**

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs
git commit -m "feat: quiesce saga extraction during maintenance"
```

---

### Task 12: Prove the complete host race and publish the contract

**Files:**
- Create or modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`

**Interfaces:**
- Full-host tests use authenticated HTTP for direct reset and healthy-catalog factory erasure, with deterministic coordinator/provider pauses and `ResponseHeadersRead` for streams.
- Documentation records lifecycle, ordering, refusal, recovery, worker, stream, and source-inventory behavior without exposing internal authority details as public API.

- [ ] **Step 1: Add full-host RED races**

Cover direct reset and factory erasure independently. During stage 1, another finite request and started effect group drain. After stage 1, a new `/api` and `/v1` request receive the correct 503 without endpoint/SQLite execution; a worker cannot create a scope; the five watch streams finish a frame and end; a billable inference stream drains. After commit, ordinary requests/work resume; after `KeepClosed`, they remain refused.

- [ ] **Step 2: Run the focused host cluster and observe RED for any missing integration**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests|FullyQualifiedName~CovenantErasureSameProcessTests"
```

- [ ] **Step 3: Make the smallest integration corrections and run the host cluster GREEN**

Each correction begins with the focused failing test above or a narrower deterministic reproduction. Commit every production correction with its test before documentation, or add its exact path to the Step 6 staging list after inspecting `git diff --name-only`; do not leave an uncommitted production correction for qualification.

- [ ] **Step 4: Update owning documentation**

Update README orientation, DESIGN §§10.20.4–10.20.6 and persistence/testing inventories, and API `/api`/`/v1` 503 contracts. State that no route, schema, config, or CLI contract changed.

- [ ] **Step 5: Run documentation/inventory tests GREEN**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireDbContextCompositionTests|FullyQualifiedName~GrimoireQuiesceableStreamContractTests|FullyQualifiedName~Documentation"
```

- [ ] **Step 6: Commit the integrated contract**

```bash
git add README.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs
git commit -m "test: prove host-wide Grimoire maintenance admission"
```

---

### Task 13: Review, qualify once, merge, push, clean up, and close #239

**Files:**
- Inspect: complete `origin/main..HEAD` diff and every wrapper/workflow used below.
- Modify only when review or verification exposes a real issue; each observable fix begins with a focused RED test.

- [ ] **Step 1: Request one bounded read-only review of the complete branch**

Review against issue #239 and the approved spec. Resolve every Critical and Important finding. For behavior changes, first reproduce with a focused failing test, then make the smallest correction and rerun only that focused cluster.

- [ ] **Step 2: Inspect current CI/workflow wrappers and write down the exact locally applicable matrix**

Inspect `.github/workflows`, `scripts/coverage.sh`, `scripts/verify-aot-il-warnings.sh`, `scripts/verify-native-sqlcipher.sh`, and packaging/generated-contract helpers. Do not invoke `workspace_check` as a bootstrap verifier.

- [ ] **Step 3: Run the complete qualification matrix exactly once on the reviewed tree**

At minimum, include:

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
git diff --check origin/main...HEAD
```

Add the current CI-required formatting, shell/workflow, packaging, documentation, generated-contract, and every remaining test-project command discovered in Step 2. The coverage threshold run supplies the full Arcanum test suite; do not run that suite a second time without a changed tree.

Expected: every command exits 0; build/test output has zero errors and zero warnings; the tracked worktree is clean after verification artifacts are classified/removed safely.

- [ ] **Step 4: Fast-forward the verified commit into `main` without rebuilding**

Verify the primary main checkout is clean, fetch, ensure `main` still matches the feature base ancestry, then fast-forward only:

```bash
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum switch main
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum merge --ff-only codex/issue-239-grimoire-admission
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum push origin main
```

If `origin/main` advanced incompatibly, stop and rebase/merge on a codex branch, rerun the affected focused tests plus the complete qualification matrix on the changed tree, and only then continue.

- [ ] **Step 5: Close out GitHub and remove implementation branches**

Confirm the pushed `main` SHA contains the verified commit. Because this linked worktree checks out `codex/issue-239-grimoire-admission` and contains the preserved untracked issue-221 files, do not remove it: verify those files are still untracked and unchanged, detach this worktree at the pushed main SHA (or switch it back to its pre-task `remove-wards` branch if cleanly possible), delete every local/remote implementation branch created by this task, and verify the cleanup. Preserve unrelated branches and delete a remote feature ref only if this task created it.

Only after push and branch cleanup are proven, add the concise result/verification comment, close issue #239, and move its project item to Done as the final state-changing action.

- [ ] **Step 6: Report exact delivery evidence**

Report the issue URL, pushed main commit, deleted implementation branches, tests/qualification commands and outcomes, and any intentionally inapplicable gate. Do not claim completion before GitHub reports #239 closed/Done and `origin/main` resolves to the delivered commit.
