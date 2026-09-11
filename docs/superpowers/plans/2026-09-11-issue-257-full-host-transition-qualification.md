# Issue #257 Full-Host Grimoire Transition Qualification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove through authenticated, deterministic, full-host tests that direct Covenant reset and standalone healthy-catalog factory erasure correctly drain every Grimoire participant, implement all three legal dispositions, recover after restart, and thereby complete issues #257 and #239.

**Architecture:** Extend the existing `ArcanumWebApplicationFactory` only with test-owned restartable-profile lifetime support, then compose the production middleware, coordinator, SQLCipher catalog, admission gate, streams, hosted attachment-index worker, EF/raw connection paths, transition journal, and startup recovery around deterministic test observers. Repair the known `KeepClosed` translation only after a same-process RED demonstrates the live gate reopening, and make no other production change without an observed behavioral RED.

**Tech Stack:** .NET 10.0.401, C# 13, ASP.NET Core `WebApplicationFactory`, EF Core, Microsoft.Data.Sqlite/SQLCipher, Microsoft.Extensions.AI, xUnit, source-generated `System.Text.Json`, `TaskCompletionSource` barriers, Git, GitHub CLI, macOS Native AOT, Ollama, and `gemma4:e4b`.

**Spec:** `docs/superpowers/specs/2026-09-11-issue-257-full-host-transition-qualification-design.md`

## Global Constraints

- Work only in `/Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-257-full-host-qualification` on `codex/issue-257-full-host-qualification` until the delivery task explicitly moves immutable refs.
- The approved base is `a381a5f7d7edbb471cb081c0303ca68e94e20b26`; cached `origin/main` and `origin/grimoire-fixes` were verified identical before this worktree was created. Recheck remote equality before delivery.
- Preserve the dirty primary checkout exactly. Do not stash, reset, clean, overwrite, include, or expose its six tracked documentation deletions or two untracked documentation files.
- Commit this plan and the approved design-status correction before Task 1. The spec and plan then become frozen implementation inputs; implementation discoveries go into code, owning docs, tests, or issue evidence rather than silently rewriting acceptance.
- Follow RED -> GREEN -> REFACTOR for every production behavior change. Run the narrow new test first and inspect the expected failure. After a fix is green, temporarily restore the old behavior and prove that the new regression test turns red, then restore the fix and rerun green.
- Test-only harness code may be built incrementally to make the next behavior observable. A new production seam is allowed only after a deterministic failing test proves the current seams cannot observe a load-bearing boundary; it must be `internal`, inert when unset, AOT-safe, and separately tested.
- Use `TaskCompletionSource` barriers with `RunContinuationsAsynchronously`, bounded `WaitAsync`, manual time, controlled streams, and explicit cancellation. Never use a sleep to establish ordering. Put synchronously blocked test participants on a dedicated `TaskCreationOptions.LongRunning` thread.
- Start both transitions through the production authenticated HTTP pipeline. Authentication must precede admission, and admission must precede body/model binding and endpoint execution.
- Keep exactly one production `IGrimoireConnectionAdmissionGate` decision-maker per host. Test observers delegate to that singleton; they never synthesize open/closed decisions.
- The direct entry point is V4 Covenant reset. The healthy factory entry point is a standalone V2 factory launch: `ParentReceiptBindingDigest` remains null and no installation-reset parent receipt is produced or reconciled.
- Preserve the five stream classifications and exact framing. A quiesceable SSE stream finishes its current frame, emits exactly one `data: [DONE]\n\n`, then disposes. A finite attachment response and already-admitted billable stream drain normally and never receive maintenance cancellation.
- Maintenance denial is deferral, not failure. A refused hosted unit retains exact queue identity and causes no scope, effect group, provider call, mutation, attempt/billing change, failure record, or watermark advance.
- Add no public route, request/response DTO, CLI verb, configuration key, error-code member, schema/DDL object, migration, checkpoint version, or provider-selection policy. No JSON reflection or anonymous wire payloads.
- Every new full-host test class carries `[Collection("ApiHost")]` and `[Trait("Category", "Integration")]`; process-global environment mutation remains serialized.
- Follow repository C# style: file-scoped namespaces, positional records for data carriers, primary constructors for DI, one blank line after ordinary code lines, no blank lines immediately inside braces, and zero build warnings.
- `README.md` changes only for the intentional public statement. `docs/Arcanum.DESIGN.md` owns architecture/persistence/testing and `docs/Arcanum.API.md` owns the exact HTTP contract. Command and configuration docs do not change.
- Focused development runs may use the installed 10.0.400 SDK. The frozen final matrix uses only the official task-local 10.0.401 SDK at `/private/tmp/arcanum-dotnet-10.0.401`.
- `./scripts/coverage.sh --threshold` is the sole full Arcanum-suite run in final local qualification. Do not add a redundant `dotnet test` of the full Arcanum project to that matrix.
- Issue #242 receives no mutation: do not edit, reparent, move, or close it. Issue #265 is outside scope.
- Do not merge, push delivery refs, close issues, delete branches/worktrees, or run the post-delivery Ollama proof until the immutable-SHA review, complete local matrix, and seven-job CI run are green.

## Baseline and Responsibility Map

- Baseline focused cluster: 843 passed, 0 failed, 0 skipped across `CovenantErasureSameProcessTests`, `GrimoireRequestAdmissionTests`, `GrimoireStreamQuiescenceTests`, `GrimoireConnectionAcquisitionInventoryTests`, and `HostedGrimoireProducerInventoryTests`.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs` — Covenant-to-Grimoire disposition translation and journal-aware closure ordering.
- `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs` — the focused real-SQLCipher `KeepClosed` regression.
- `tests/RetroDownfall.Arcanum.Tests/Fixtures/RestartableArcanumProfileFixture.cs` — new test owner for shared profile bytes, `GrimoireFixture`, SQLCipher passphrase, and one credential store.
- `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs` — host-instance ownership, first-seed selection, shared credentials, and existing service/settings overrides.
- `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactoryTests.cs` and `ArcanumWebApplicationFactoryDisposalTests.cs` — restartability and default lifetime contracts.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs` — authenticated planning/apply helpers, profile/host orchestration, seeding, and participant launch/release.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs` — barriers and forwarding observers for gate stages, tickets, streams, scopes, blob reads, chat effects, worker effects, EF/raw opens, and recovery adoption.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs` plus `.Commit.cs`, `.Outcomes.cs`, and `.Recovery.cs` — scenario assertions split by lifecycle outcome while sharing one partial test type.
- Existing middleware, stream, worker, connection, recovery, credential, and inventory suites remain authoritative for their local matrices and are modified only when an exact missing assertion is found.
- `README.md`, `docs/Arcanum.DESIGN.md`, and `docs/Arcanum.API.md` — final public, architecture/testing, and wire-contract documentation.

---

### Task 1: Preserve `KeepClosed` Through the Real Grimoire Closure

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Modify after RED: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Read/rerun: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`

**Interfaces and result:**

- Drive the existing `SameProcessHarness` with `RouteFailure.KeepClosed`; do not unit-test the private translator.
- Prove the observable postcondition on the real `IGrimoireConnectionAdmissionGate` after `ApplyResetAsync` has parked the V4 journal and disposed the coordinator-owned closure.
- Capture the operation's successful checkpoint and state writes through a forwarding test-owned `ILongRunningOperationStore` observer before the gate parks. Once `KeepClosed` works, an ordinary `ListAsync`/`GetAsync` read is correctly refused and cannot be the test's evidence path.
- Preserve all three named `CovenantExclusiveLeaseDisposition` values exactly; reject an undefined value rather than folding it into rollback.

- [ ] **Step 1: Write the runtime RED**

Add a dedicated test named `Direct_retention_reset_KeepClosed_retains_Grimoire_admission_closure`. Its core ordering and assertions are:

```csharp
await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
    routeFailure: RouteFailure.KeepClosed);

SameProcessBefore before = await harness.SeedAndCaptureAsync();

await before.ReadLease.DisposeAsync();

IGrimoireConnectionAdmissionGate admission = harness.Services
    .GetRequiredService<IGrimoireConnectionAdmissionGate>();

long closedGeneration = admission.CurrentGeneration;

DataRetentionPlan confirmed = await harness.PlanResetAsync();

Result<DataRetentionApplyResult> result = await harness.ApplyResetAsync(confirmed.PlanId);

Assert.True(result.IsFailure);

Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, result.Error.Code);

Assert.Equal(checked(closedGeneration + 1), admission.CurrentGeneration);

Assert.False(admission.TryAcquireRequestLease(
    GrimoireRequestKind.Finite,
    out IGrimoireRequestLease? requestLease));

Assert.Null(requestLease);

Assert.False(admission.TryAcquireWorkLease(
    GrimoireWorkKind.SessionAttachmentIndexing,
    out IGrimoireWorkLease? workLease));

Assert.Null(workLease);

using SqliteConnection connection = new();

Assert.Throws<GrimoireMaintenanceUnavailableException>(
    () => admission.AcquireOrdinaryOpen(connection));
```

Also assert the last successfully forwarded operation write remains `LongRunningOperationState.ReconciliationRequired`, carries `ErrorCodes.Covenant.ErasureIncomplete`, and retains `CovenantOfflineTransitionLaunchV4.CurrentVersion`. Parse that captured checkpoint and the authenticated transition journal to assert the exact parked state/blocker/resume binding. Do not call `SameProcessHarness.ReadResetOperationAsync` after the gate parks: it resolves `ILongRunningOperationStore.ListAsync` through an ordinary Grimoire connection and must now be refused.

- [ ] **Step 2: Run the one test and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureSameProcessTests.Direct_retention_reset_KeepClosed_retains_Grimoire_admission_closure"
```

Expected RED: `TryAcquireRequestLease` succeeds because `GrimoireDispositionFor` translated `KeepClosed` to `RollbackAndReopen`. Stage two legitimately burns the old opening generation in both implementations, so the generation assertion expects `closedGeneration + 1`; admission state, not generation arithmetic alone, distinguishes parked from reopened.

- [ ] **Step 3: Make the minimal production correction**

Replace the two-arm conditional with an exhaustive identity translation and rewrite its XML remarks so they describe shared disposition semantics:

```csharp
private static CovenantExclusiveLeaseDisposition GrimoireDispositionFor(
    CovenantExclusiveLeaseDisposition covenant) =>
    covenant switch
    {
        CovenantExclusiveLeaseDisposition.RollbackAndReopen => covenant,
        CovenantExclusiveLeaseDisposition.CommitAndReopen => covenant,
        CovenantExclusiveLeaseDisposition.KeepClosed => covenant,
        _ => throw new ArgumentOutOfRangeException(nameof(covenant), covenant, null),
    };
```

Do not change `CloseAsync` ordering: park/reconcile first, spend the Grimoire closure second, then spend the Covenant lease.

- [ ] **Step 4: Prove GREEN, mutation RED, and restored GREEN**

Before the combined run, update the `RouteFailure.KeepClosed` row of `Direct_retention_reset_maps_noncommit_dispositions_to_typed_failure_and_durable_state` to assert the same captured write rather than performing a post-park ordinary database read. Run the single test green. Temporarily restore the old conditional, rerun the same command and require the admission assertion to fail, then restore the switch and rerun:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureSameProcessTests.Direct_retention_reset_KeepClosed_retains_Grimoire_admission_closure|FullyQualifiedName~CovenantErasureSameProcessTests.Direct_retention_reset_maps_noncommit_dispositions_to_typed_failure_and_durable_state|FullyQualifiedName~CovenantErasureCoordinatorTests"
```

- [ ] **Step 5: Review and commit**

```bash
git diff --check
git add src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs
git commit -m "fix: keep parked Grimoire transitions closed"
```

---

### Task 2: Separate Restartable Profile Ownership From Host Ownership

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/RestartableArcanumProfileFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactoryDisposalTests.cs`

**Interfaces and result:**

```csharp
internal sealed class RestartableArcanumProfileFixture : IAsyncDisposable
{
    internal string TempHome { get; }

    internal GrimoireFixture? Grimoire { get; }

    internal IOsCredentialStore CredentialStore { get; }

    internal IGrimoireDbPassphraseSource PassphraseSource { get; }

    internal bool ClaimInitialSeed();

    internal ArcanumWebApplicationFactory CreateFactory();

    public ValueTask DisposeAsync();
}
```

The shared fixture owns the directory, database template/passphrase lifetime, one retained `IGrimoireDbPassphraseSource`, and `InMemoryOsCredentialStore`. Each factory owns only one host plus its environment capture/restore. The default factory creates and owns a private profile so every existing caller keeps its current cleanup behavior.

- [ ] **Step 1: Write the restartability RED**

Add `Restartable_profile_preserves_catalog_files_and_credentials_across_hosts` under `[Collection("ApiHost")]`:

1. Create one `RestartableArcanumProfileFixture` and host 1.
2. Assert host 1 resolves the exact `profile.CredentialStore` and `profile.PassphraseSource` instances.
3. Create one ordinary Session through host 1's production `ISessionRepository` and set a credential sentinel on the shared store.
4. Dispose host 1 only; assert the profile directory, `arcanum.db`, `.kdf`, and marker bytes still exist.
5. Create host 2 from the same profile; assert it resolves those same two shared instances, query the Session by its retained id, and read the credential, proving all profile authority survived without reseeding.
6. Dispose host 2; assert the profile still exists.
7. Let the profile's `await using` finally dispose it; assert the exact temp directory is gone.

Use an existing durable domain row so the test changes no schema or DDL:

```csharp
ISessionRepository sessions = firstScope.ServiceProvider
    .GetRequiredService<ISessionRepository>();

Session marker = await sessions.CreateAsync(
    campaignId: null,
    title: "issue-257-restart-survives",
    CancellationToken.None);

Assert.Equal(
    OsCredentialStoreStatus.Ok,
    profile.CredentialStore.Set(
        "arcanum-tests",
        "issue-257-restart",
        "survives").Status);
```

Read `marker.Id` in host 2 through the same encrypted catalog. Assert its exact title plus the credential result's status/value without logging or retaining the secret beyond the assertion, then delete the test sentinel from the shared in-memory store.

- [ ] **Step 2: Write the owner-only cleanup RED**

Add `Host_disposal_never_deletes_an_externally_owned_restartable_profile` and retain the existing sync/async default-factory tests. Assert host stop observes the profile's Grimoire path, host disposal restores the parent environment, and only `RestartableArcanumProfileFixture.DisposeAsync` deletes shared resources.

- [ ] **Step 3: Run focused tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumWebApplicationFactoryTests|FullyQualifiedName~ArcanumWebApplicationFactoryDisposalTests"
```

Expected RED: the required shared-profile constructor/type does not exist; once minimally scaffolded against current disposal, host 1 deletes the directory and host 2 cannot observe the marker/credential.

- [ ] **Step 4: Implement test-only profile ownership**

Refactor the factory constructors to this ownership shape:

```csharp
public ArcanumWebApplicationFactory()
    : this(new RestartableArcanumProfileFixture(), ownsProfile: true)
{
}

internal ArcanumWebApplicationFactory(RestartableArcanumProfileFixture profile)
    : this(profile, ownsProfile: false)
{
}
```

Replace `_tempHome`/`_grimoireFixture` allocation with profile values. Create one `GrimoireDbPassphraseSource` when the profile is created, initialize it once from the owned `GrimoireFixture.Passphrase`, expose it only as `IGrimoireDbPassphraseSource`, and never copy or log its value. In `ConfigureTestServices`, replace both authority interfaces with the exact shared objects after production registration:

```csharp
services.RemoveAll<IOsCredentialStore>();

services.AddSingleton(_profile.CredentialStore);

services.RemoveAll<IGrimoireDbPassphraseSource>();

services.AddSingleton(_profile.PassphraseSource);
```

Call `SeedGrimoireDatabaseIfAvailable` only when `_profile.ClaimInitialSeed()` succeeds. Host-only disposal must stop the host, call base disposal, clear SQLite pools, and restore that factory's captured environment. It disposes/deletes the profile only when `_ownsProfile` is true. The profile owner clears pools, disposes `GrimoireFixture`, and deletes only its exact temp root, idempotently.

- [ ] **Step 5: Run GREEN and the existing isolation contracts**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumWebApplicationFactoryTests|FullyQualifiedName~ArcanumWebApplicationFactoryDisposalTests|FullyQualifiedName~TestCredentialStorePolicyTests|FullyQualifiedName~DiWiringSmokeTests"
```

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Fixtures/RestartableArcanumProfileFixture.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactoryTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactoryDisposalTests.cs
git commit -m "test: add restartable full-host profiles"
```

---

### Task 3: Build Deterministic Full-Host Participant Observation

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObserversTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarnessTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`

**Interfaces and result:**

Use these test-only vocabulary types so scenario code names its entry point and barriers explicitly:

```csharp
internal enum GrimoireTransitionEntryPoint : byte
{
    DirectCovenantReset = 1,

    StandaloneFactoryReset = 2,
}

internal sealed class MaintenanceCheckpoint
{
    private readonly TaskCompletionSource _reached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitUntilReachedAsync() =>
        _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();

        await _released.Task.WaitAsync(cancellationToken);
    }

    internal void Release() => _released.TrySetResult();
}
```

`GrimoireMaintenanceAdmissionObserver` implements every member of `IGrimoireConnectionAdmissionGate`, delegates to the real `GrimoireConnectionAdmissionGate`, and records:

- stage-one entry/completion around `DrainRequestAndWorkAsync`;
- stage-two entry/completion around `CloseConnectionAdmissionAsync`;
- request/work acquisition attempts, kind, generation, revocation, and disposal;
- effect-group begin/terminal/disposal count;
- opening-ticket generation plus exactly one of opened, failed, or refused-after-open; and
- owner/closed-lease generation and exactly one final disposition.

The observer wraps capabilities only to count their forwarded methods. It must never convert a refusal to success or choose a disposition. Keep an explicit wrapper-to-inner mapping and unwrap the initiating request or closing owner before calling `BeginOrResumeExclusive`, `DrainRequestAndWorkAsync`, `CloseConnectionAdmissionAsync`, or `AbortClosingAsync`: the real gate deliberately rejects an interface implementation that is not its own concrete capability.

Add one factory-only `AdditionalDbContextInterceptors` collection. In the existing `AddDbContextPool` options lambda, add its pre-created interceptors after `CovenantConnectionEnrolmentInterceptor` in the same `AddInterceptors` chain. Registering a `DbConnectionInterceptor` in the service collection is insufficient: the current test factory builds pooled `ArcanumDbContext` options explicitly and does not enumerate interceptor services. The default empty collection must preserve every existing factory caller unchanged.

- [ ] **Step 1: Write observer-contract tests first**

Add tests proving:

- request/work refusal and success are identical to the inner gate;
- wrapped leases preserve exact `Kind`, `Generation`, and `MaintenanceRevocation`;
- wrapped request and closing-owner capabilities are unwrapped to the exact inner capability at every gate re-entry, while a foreign wrapper remains refused;
- one external-effect group can be begun only when the real lease permits it;
- every opening-ticket terminal method reaches the inner ticket exactly once and reuse still fails;
- stage-one/stage-two observation occurs outside the delegated call and does not complete early; and
- owner/closed-lease disposal does not invent a reopening disposition.

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceParticipantObserversTests"
```

Expected RED is missing test support. These are test-infrastructure tests; no production edit is authorized by compilation failure.

- [ ] **Step 2: Add controlled participants using existing production seams**

Implement adjacent internal types in `GrimoireMaintenanceParticipantObservers.cs`:

- `ControlledEventBus` for one `DaemonEvent` and one `McpServerEvent`, recording producer cancellation and enumerator disposal;
- `ControlledLogQueryService` for one complete `LogEntry` frame and enumerator disposal;
- `ControlledEncryptedBlobStore` forwarding to a separately registered real `EncryptedBlobStore` and wrapping only the designated download blob's read stream; the stream pauses after an observed prefix, then completes and records disposal;
- `ControlledChatClientFactory` returning a real `ChatClientLease` over a controlled `IChatClient` that pauses after the provider effect begins and records completion/disposal;
- `ControlledWeaveService` that pauses one real `SessionAttachmentIndexingService` provider effect and records only call/start/completion/cancellation; durable status, chunk, attempt, failure, and watermark mutations are observed through the real repositories after release;
- `PausingStatsCommandInterceptor : DbCommandInterceptor`, scoped to the stats command, so the finite request pauses after request admission and native connection opening but before its final Grimoire result is materialized;
- `PausingEfOpenInterceptor : DbConnectionInterceptor`, registered after `CovenantConnectionEnrolmentInterceptor`, so its `ConnectionOpeningAsync` pause occurs after the production opening ticket exists but before native open;
- `PausingOrdinaryConnectionFactoryTestSeam : IGrimoireOrdinaryConnectionFactoryTestSeam`, forwarding construction/pool-clear observations and pausing `BeforeNativeOpenAsync`;
- `ObservingCovenantConnectionDrain : ICovenantConnectionDrain`, forwarding to one real `CovenantConnectionDrain` while counting enrolments and observing `ClearExactPoolAfterClose` on the exact physically closed handle;
- `RecordingLongRunningOperationStore : ILongRunningOperationStore`, forwarding every call while retaining immutable snapshots of successful checkpoint and state transitions for assertions made while ordinary database access is closed;
- `PostAdmissionEndpointProbe`, installed through a test `IStartupFilter` that calls `next(app)` first and then appends its middleware, whose contract test proves it runs inside `UseArcanumApiKeyAuthentication` and immediately before the selected endpoint; use it to prove an admitted `/v1/models` request executes the probe and a maintenance-refused request never does;
- `PausingMaintenanceLeaseAdoption : ILongRunningOperationMaintenanceLeaseAdoption`, which delegates to `LongRunningOperationStore`, pauses only after `Acquired == true`, and returns the exact result after release;
- a scoped disposal sentinel resolved by the same test startup filter for each selected request, plus log capture using `TestCapturingLogger<T>` patterns.

If an `IStartupFilter` cannot prove the required post-admission ordering in its observer-contract test, add a test-only application-pipeline hook to `ArcanumWebApplicationFactory` and prove that hook's order. Do not infer endpoint non-execution from a `503`, and do not add a production hook for this test concern.

Do not attempt to decorate `ICovenantOperationGate` to count dispositions. Its acquisition methods return sealed concrete lease objects, and `CompleteAsync(disposition)` goes directly to the captured registration without re-entering the gate interface. In the full-host tests, prove the accepted Covenant answer through the exact durable operation/journal transition plus the real gate's closed/open owner postcondition; use `CovenantOperationGateTests` as the local authority that a lease accepts only one disposition. If an integrated RED proves those observable effects cannot distinguish the required boundary, first add a focused gate RED, then introduce only a separately tested internal, optional completion observer that the real `CovenantOperationGate` invokes after its actual completion decision. The default must be inert and production behavior must remain unchanged.

Do not replace the real session or apprentice hubs. `SessionEventHub.GetSubscriberCount(sessionId) == 0` and `ChronicleHub.TrackedApprenticeCount == 0`, combined with the endpoint's awaited pump and existing `SseStreamWriterQuiescenceTests`, provide the full-host subscription/disposal proof without a new production hook.

- [ ] **Step 3: Compose the harness over the production host**

`GrimoireMaintenanceAdmissionHarness` must:

- own or borrow one `RestartableArcanumProfileFixture`;
- create one factory/loopback authenticated client at a time;
- apply this settings override before host construction:

```csharp
factory.SettingsOverride = settings => settings with
{
    Features = settings.Features with
    {
        AttachmentRetrieval = true,
    },

    Integrations = settings.Integrations with
    {
        Embeddings = settings.Integrations.Embeddings with
        {
            Provider = "test",
            Model = "mistral:latest",
            Dimensions = 64,
        },
    },

    Execution = settings.Execution with
    {
        MaxSseConnections = 8,
        MaxSseConnectionsPerType = 8,
    },
};
```

- replace only the `IGrimoireConnectionAdmissionGate` interface registration with an observer over the existing concrete singleton;
- retain the one production `ICovenantOperationGate` unchanged and derive its accepted disposition from durable operation/journal evidence plus the real gate postcondition;
- replace `ICovenantConnectionDrain` with one `ObservingCovenantConnectionDrain` over one explicitly registered real `CovenantConnectionDrain`, so the gate, EF interceptor, and transition coordinator still share the exact same drain;
- decorate `ILongRunningOperationStore` with `RecordingLongRunningOperationStore`, leaving the concrete `LongRunningOperationStore` and its separate maintenance-adoption registration intact;
- restore `IArcanumIntelligenceProvider` and `IContextPreviewService` to the existing scoped `WizardIntelligenceProvider`, then replace only `IChatClientFactory` for the billable-stream participant;
- replace `IWeaveService`, `IEventBus`, and `ILogQueryService` only with controlled doubles needed at external-effect or frame boundaries;
- before replacing `IEncryptedBlobStore`, register the real concrete `EncryptedBlobStore`; register the controlled interface decorator over that concrete object so removing the interface descriptor does not remove the decorator's inner implementation;
- assign both pre-created EF test interceptors through the factory's `AdditionalDbContextInterceptors` hook, which appends them after the production enrolment interceptor, and replace the existing no-op raw-open seam;
- install and contract-test the post-admission endpoint/scope probe;
- seed one Session and Apprentice plus three distinct attachment identities through existing repositories/stores: a download-only blob targeted by the controlled reader, an admitted indexing request A, and a post-close deferral request B; never let the download wrapper intercept either worker read;
- plan through the correct authenticated route using source-generated JSON; and
- dispose response streams, clients, scopes, factories, and optionally the shared profile in reverse ownership order even after an assertion failure.

The planning/apply helper must keep standalone factory reset free of installation handoff:

```csharp
private static FactoryResetRequest FactoryApply(
    string planId,
    Guid operationId) =>
    new(
        "factory-reset",
        ExpectedPlanId: planId,
        RequestedOperationId: operationId,
        InstallationResetHandoff: null);
```

- [ ] **Step 4: Prove test-support behavior and existing lower-level boundaries**

Build the full-host harness in attributable RED/GREEN slices before composing a destructive transition. Add focused integration support tests, one at a time, that start an ordinary authenticated host and prove:

1. the finite stats command, attachment reader, billable chat, endpoint, and request-scope probes each reach and release their exact barrier;
2. all five SSE observers produce one complete deterministic frame, receive revocation from the real gate, emit one terminal frame, and dispose cleanly after their controlled producers are released;
3. indexing request A reaches the real hosted service's `IWeaveService` boundary, while a separately queued request retains its exact identity when the real gate is closed through the forwarding observer; and
4. the factory's additional-interceptor hook places the EF observers after `CovenantConnectionEnrolmentInterceptor`, and the EF and raw open observers reach their pre-native-open barriers and forward terminal callbacks/pool-clear observations without changing the inner result.

Each support test validates test wiring only and cannot authorize a production behavior change. A failing slice must be fixed before the next observer family is added; Task 4's final named test composes only observers already green through these focused host tests.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceParticipantObserversTests|FullyQualifiedName~GrimoireMaintenanceAdmissionHarnessTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireOrdinaryConnectionFactoryTests.Generation_race|FullyQualifiedName~SseStreamWriterQuiescenceTests|FullyQualifiedName~SessionAttachmentIndexingAdmissionTests"
```

If a full-host-only boundary still cannot be observed, add one focused failing test before adding a production seam; otherwise production remains unchanged in this task.

- [ ] **Step 5: Review and commit**

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObserversTests.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarnessTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs
git commit -m "test: compose full-host Grimoire transition observers"
```

---

### Task 4: Prove the Direct Covenant Commit-and-Reopen Cluster

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Commit.cs`
- Modify test support from Task 3 as the first integrated RED requires.
- Modify production only when the compiled integration test demonstrates a concrete defect.

**Test:** `Authenticated_direct_reset_drains_the_full_host_and_reopens_only_the_next_generation`

This is one composed-host acceptance test, not a theory with hidden entry-point differences. It drives:

```csharp
new MemoryResetRequest(
    MemoryResetScope.Covenant,
    ExpectedPlanId: plan.Data!.PlanId)
```

through `POST /api/data/memory/reset/plan` and `POST /api/data/memory/reset`, using `ArcanumJsonContext.Default.MemoryResetRequest` for both request bodies and the existing generated response metadata for reads.

- [ ] **Step 1: Compose the final participant-cluster RED from the green harness slices**

Do not add all participants to an unproven harness at once. After every Task 3 focused host slice is green, compose those same observers into the final named destructive-transition test. Start every participant and await its deterministic ready barrier before launching reset:

| Participant | Start/ready boundary | Release/terminal proof |
| --- | --- | --- |
| finite request | authenticated `GET /api/grimoire/stats`, paused in a `DbCommandInterceptor` after request admission and before final materialization | complete `200`, handler/command once, response/scope disposed |
| attachment download | authenticated `GET /api/sessions/{sessionId}/attachments/{attachmentId}/content` with `ResponseHeadersRead`, paused in the forwarding encrypted-blob read stream after a prefix | all expected plaintext bytes arrive, stream and request scope dispose normally |
| billable stream | authenticated streaming `POST /v1/chat/completions`, real `WizardIntelligenceProvider`, paused controlled `IChatClient` after external effect begins | provider answer, usage/accounting/audit, terminal frame, lease, response, and scope complete normally |
| daemon SSE | `GET /api/events/daemon`, one injected `DaemonEvent` frame | complete frame, exactly one `[DONE]`, producer cancellation, enumerator/scope disposal |
| MCP SSE | `GET /api/events/mcp`, one injected `McpServerEvent` frame | complete frame, exactly one `[DONE]`, producer cancellation, enumerator/scope disposal |
| log SSE | `GET /api/events/logs`, one controlled `LogEntry` after the connection sentinel | complete frame, exactly one `[DONE]`, producer cancellation, enumerator/scope disposal |
| session SSE | `GET /api/sessions/{sessionId}/stream`, persisted replay/live sentinel | complete frame, exactly one `[DONE]`, zero `SessionEventHub` subscribers, request scope disposed |
| chronicle SSE | `GET /api/apprentices/{apprenticeId}/chronicle`, buffered live event | complete frame, exactly one `[DONE]`, zero tracked Chronicle hub entries, request scope disposed |
| hosted worker | enqueue indexing request A; pause the real `SessionAttachmentIndexingService` inside its won effect group at `IWeaveService` | provider/durable disposition completes, scope disposes, request A concludes exactly once |
| EF physical open | test-owned `ArcanumDbContext.Database.OpenConnectionAsync` with no request/work lease; pause in the test interceptor after the production interceptor acquired its ticket | generation-losing handle closes, exact pool clears, ticket terminalizes once and cannot be reused |

Use `HttpCompletionOption.ResponseHeadersRead` for every streaming participant. Frame readers must distinguish complete `\n\n`-terminated frames from a prefix; reaching a substring is not proof that the frame finished.

- [ ] **Step 2: Prove stage-one refusal and drain**

After the forwarding gate records entry into `DrainRequestAndWorkAsync`:

1. Send a new authenticated `GET /api/grimoire/stats`; assert `503`, source-generated `ApiResponse<string>`, `ErrorCodes.Grimoire.MaintenanceUnavailable`, exact sanitized message, and no handler/SQLite observation.
2. Send a new authenticated `GET /v1/models`; assert `503`, OpenAI type `service_unavailable`, code `grimoire_maintenance`, exact sanitized message, and no endpoint observer.
3. Enqueue the distinct indexing request B while admission is closed. After request A is released, hold the gate observer after the real stage-one drain returns but before it returns control to the coordinator; await request B's refused work-lease attempt and assert its exact object/attempt identity appears once in `SessionAttachmentIndexingService.DeferredRequests`, with no scope, effect, provider, mutation, attempt/billing, failure row, or watermark change. Re-signalling B must deduplicate against that retained identity rather than adding another queue entry.
4. Assert every quiesceable stream's prior revocation token is cancelled, its in-flight frame is complete, no later data frame appears, and its terminal/disposal proof completes.
5. Assert stage one remains incomplete until the finite stats request, attachment response, billable stream, and already-won worker effect are explicitly released and reach their durable dispositions.

Release one participant at a time and prove the drain does not skip the others. Release request A before awaiting B's deferral, while the post-drain observer still prevents stage two from advancing. The test may use one helper to avoid repetitive frame parsing, but failures must name the participant whose barrier or terminal assertion failed.

- [ ] **Step 3: Prove the stage-two EF generation race**

After stage one completes and `CloseConnectionAdmissionAsync` begins:

1. Assert the EF opening ticket already exists and names the losing generation.
2. Release the interceptor so native open returns into the changed generation.
3. Assert `OpenConnectionAsync` fails with `GrimoireMaintenanceUnavailableException`.
4. Assert the handle is closed, the exact pool was cleared, the ticket reached exactly one terminal outcome and rejects reuse, and the production drain reports zero enrolled handles.
5. Hold the observer after the real close returns but before the coordinator receives closed authority; assert neither `arcanum.db-wal` nor `arcanum.db-shm` exists, then release closed authority.

- [ ] **Step 4: Prove commit, retirement, and fresh generation**

Await the authenticated reset response and assert success. Read evidence through the legal post-reopen path and assert:

- V4 launch/checkpoint identity and direct Covenant operation kind;
- canonical protected reset and managed-file result applied as planned;
- private reopened-catalog verification completed before runtime authority publication;
- runtime authority published before the exact database/parent reconciliation suffix;
- database/parent reconciliation completed before either disposition was spent;
- the Grimoire observer records one `CommitAndReopen`; the completed durable operation, terminal journal progression, and reopened Covenant owner state prove that the Covenant lease accepted `CommitAndReopen`, while `CovenantOperationGateTests` proves the lease's one-shot rule;
- normal retirement wrote and reread the `Closed` anchor before deleting the exact journal file, while retaining the closed-anchor tombstone and stable journal key for authenticated absence;
- fresh authenticated `/api/grimoire/stats`, fresh work lease, and fresh physical open succeed at exactly `losingGeneration + 1`; and
- all old stream revocation tokens remain cancelled, the prior work lease cannot begin a new effect group, the losing ticket remains terminal, and the disposed maintenance owner cannot be reused.

Capture `CovenantErasureCoordinator`, request-admission, stream, worker, and connection logs. Assert no Error and no Warning other than an explicitly expected, asserted diagnostic; do not blanket-filter by message text.

- [ ] **Step 5: Run RED/GREEN and fix only observed integration defects**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_direct_reset_drains_the_full_host_and_reopens_only_the_next_generation"
```

The first test-first run may initially fail to compile while its harness is completed. Once compiled, record every concrete failing boundary. If it passes against production, retain it as the missing acceptance proof and do not fabricate a production edit. For each real defect, add/narrow a deterministic assertion before changing production, make the smallest correction, temporarily restore the defect to prove RED, then restore GREEN.

- [ ] **Step 6: Run the direct cluster and its component authorities**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_direct_reset|FullyQualifiedName~GrimoireMaintenanceRefusalTests|FullyQualifiedName~GrimoireStreamQuiescenceTests|FullyQualifiedName~SseStreamWriterQuiescenceTests|FullyQualifiedName~SessionAttachmentIndexingAdmissionTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~CovenantOperationGateTests"
```

- [ ] **Step 7: Review and commit**

Stage the two scenario files, Task 3 support changes, and only the production files whose RED tests required correction:

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Commit.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs
git add -u src tests/RetroDownfall.Arcanum.Tests
git commit -m "test: prove direct reset across the full host"
```

Before committing, inspect `git diff --cached --name-status` and unstage any unrelated path; `git add -u` is permitted only inside this clean task worktree.

---

### Task 5: Prove the Standalone Factory Commit-and-Reopen Cluster

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Commit.cs`
- Modify Task 3 support only where both entry points genuinely share behavior.
- Modify production only after the new factory-host RED demonstrates a defect.

**Test:** `Authenticated_standalone_factory_erasure_drains_the_full_host_and_reopens_only_the_next_generation`

- [ ] **Step 1: Add the factory-specific RED over the same complete participant set**

Plan with authenticated loopback `POST /api/data/factory-reset/plan` and:

```csharp
new InstallationResetDataPlanRequest(InstallationResetDataScope.Global)
```

Apply with a new requested operation identity and:

```csharp
new FactoryResetRequest(
    "factory-reset",
    ExpectedPlanId: plan.Data!.PlanId,
    RequestedOperationId: operationId,
    InstallationResetHandoff: null)
```

Repeat every Task 4 participant and admission assertion only after the corresponding Task 3 harness slices and Task 4 composed cluster are green. Do not weaken the cluster into a theory: factory erasure has additional ordinary cleanup and different durable launch/reconciliation evidence.

- [ ] **Step 2: Use the raw physical-open race**

For this cluster call the production `IGrimoireOrdinaryConnectionFactory.OpenFreshAsync` from a test-owned non-request/non-work context and pause at `IGrimoireOrdinaryConnectionFactoryTestSeam.BeforeNativeOpenAsync`. Assert:

- `BeforeProviderConstruction` and opening-ticket acquisition precede the pause;
- stage two changes generation while native open is paused;
- the losing handle closes and `AfterExactPoolClear` identifies that exact connection;
- the returned `Result` carries `GrimoireMaintenanceUnavailableException.Code`;
- the opening ticket is terminal exactly once and is not reusable; and
- enrolled handle count and sidecars are zero before closed authority reaches the coordinator.

- [ ] **Step 3: Add factory-specific commit assertions**

In addition to the shared drain/refusal/stream/worker/next-generation contract, assert:

- the launch/checkpoint is V2 standalone factory erasure;
- protected and ordinary cleanup both match the confirmed plan;
- private healthy-catalog verification completes before runtime authority publication;
- runtime authority publication precedes exact database reconciliation;
- `ParentReceiptBindingDigest` is null;
- no parent receipt is created, consumed, or reconciled;
- exact database reconciliation completes before the Grimoire/Covenant `CommitAndReopen` dispositions;
- normal retirement then writes and rereads the `Closed` anchor before deleting only the exact journal file and retains the closed-anchor tombstone plus stable journal key; and
- stale Grimoire/Covenant maintenance capabilities remain unusable.

- [ ] **Step 4: Run RED/GREEN and the route-level factory suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_standalone_factory_erasure_drains_the_full_host_and_reopens_only_the_next_generation"
```

If the compiled test passes, it supplies the missing integration proof and no production edit is needed. If it fails, apply one deterministic RED/GREEN correction at a time, including mutation proof for every production change.

Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_standalone_factory|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~CovenantErasureSameProcessTests.Factory|FullyQualifiedName~GrimoireOrdinaryConnectionFactoryTests"
```

- [ ] **Step 5: Review and commit**

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Commit.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs
git add -u src tests/RetroDownfall.Arcanum.Tests
git diff --cached --name-status
git commit -m "test: prove factory erasure across the full host"
```

---

### Task 6: Prove Rollback and `KeepClosed` for Both Authenticated Entry Points

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Outcomes.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs`
- Modify production only after a scenario-specific RED.

**Outcome tests:**

- `Authenticated_transition_rolls_back_and_reopens_only_when_pre_effect_is_proven`
- `Authenticated_transition_keeps_both_gates_closed_when_effect_outcome_is_uncertain`

Each is a theory over `GrimoireTransitionEntryPoint.DirectCovenantReset` and `StandaloneFactoryReset`, but every assertion message and durable-evidence branch must name the entry point.

- [ ] **Step 1: Write direct-reset pre-effect rollback RED**

Configure the existing `CovenantErasureFaultSeam` to return an exact test error at:

```csharp
CovenantErasureFaultBoundary.BeforePhaseBegin,
CovenantResetPhase.CanonicalApplied
```

The production DI factory constructs `CovenantErasureCoordinator` explicitly and does not resolve the optional seam. When a scenario requests a fault, re-register the concrete scoped coordinator with the same production dependency list plus that exact `CovenantErasureFaultSeam`, following the existing `SameProcessHarness.CreateAsync` pattern. Adding the delegate to DI by itself has no effect. A factory without a requested fault must retain the ordinary production coordinator registration.

This is after closed proof and before the first destructive effect. Assert:

- the HTTP result uses the existing mapped typed failure;
- canonical rows and managed files are byte/logically unchanged;
- the operation terminalizes as `Failed` with the exact `grimoire.offline_transition_not_applied` durable code/digest;
- the Grimoire observer records one `RollbackAndReopen`; the exact failed-before-effect durable row, retired journal, and reopened Covenant owner state prove the Covenant lease accepted `RollbackAndReopen`, with `CovenantOperationGateTests` retaining one-shot authority;
- normal retirement deletes the exact journal file only after writing/rereading the `Closed` anchor, and retains that anchor plus the stable key;
- the original dataset remains readable; and
- new HTTP/request/work/open admission succeeds only in the successor generation.

Run only that theory row and inspect RED before any correction.

- [ ] **Step 2: Add standalone-factory rollback RED**

Repeat the same fault boundary through authenticated V2 factory apply. Assert protected and ordinary datasets plus managed files remain unchanged, database reconciliation records the failed no-effect outcome, `ParentReceiptBindingDigest` remains null, no parent receipt exists, and the same rollback/reopen/retirement/generation contract holds.

- [ ] **Step 3: Write the uncertain `KeepClosed` RED for both entry points**

Configure the seam to fail at:

```csharp
CovenantErasureFaultBoundary.AfterPhaseBegin,
CovenantResetPhase.CanonicalApplied
```

At this boundary the durable journal says the effect may have begun, so rollback is forbidden. For each entry point assert:

- the HTTP route returns its existing sanitized typed failure with no path, owner, operation, phase, generation, SQLite, or exception detail;
- the last successful write captured by `RecordingLongRunningOperationStore` remains `LongRunningOperationState.ReconciliationRequired`; do not attempt an ordinary operation-store read while admission is parked;
- the journal records `KeepClosed`, the exact blocker, resume state, owner, and payload binding;
- direct reset uses V4; standalone factory uses V2 with null `ParentReceiptBindingDigest` and no parent receipt;
- the Grimoire observer records one `KeepClosed`; the exact `ReconciliationRequired` row, active journal, retained owner, and closed Covenant postcondition prove the Covenant lease accepted `KeepClosed`, with `CovenantOperationGateTests` retaining one-shot authority;
- request/work/open acquisition and authenticated `/api`/`/v1` remain refused after the response completes; and
- disposing retained test references and the first host does not publish an open generation.

The Task 1 regression must remain green. If either full-host row reopens, narrow that failure before changing any additional production code.

- [ ] **Step 4: Prove refusal is side-effect free while parked**

While each host remains parked:

1. Enqueue a fresh attachment-index request after the parked response, await its refused work-lease attempt, and snapshot the exact retained `DeferredRequests` object plus attempt/billing/failure/watermark counters.
2. Re-signal that same attachment id with a different attempt value and attempt fresh API, OpenAI, work, and physical-open operations.
3. Assert the original deferred object/attempt identity remains exactly once, the duplicate added no queue entry, no scope/effect/provider/mutation occurs, all counters remain unchanged, and the sanitized refusal log level is below Error.
4. Dispose any owner/lease wrappers held by the test and repeat the closed assertions.

- [ ] **Step 5: Run each RED/GREEN slice, then the outcome cluster**

Use the test runner's exact display names after discovery to select each theory row during development. The combined stable command is:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_transition_rolls_back|FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Authenticated_transition_keeps_both_gates_closed|FullyQualifiedName~CovenantErasureSameProcessTests.Direct_retention_reset_KeepClosed"
```

Then rerun the existing lifecycle/fault authorities:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureSameProcessTests|FullyQualifiedName~CovenantOperationGateTests|FullyQualifiedName~GrimoireOfflineTransitionPhaseSessionTests|FullyQualifiedName~GrimoireOfflineTransitionLifecycleTests|FullyQualifiedName~GrimoireMaintenanceRefusalTests|FullyQualifiedName~SessionAttachmentIndexingAdmissionTests"
```

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Outcomes.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs
git add -u src tests/RetroDownfall.Arcanum.Tests
git diff --cached --name-status
git commit -m "test: prove both noncommit Grimoire dispositions"
```

---

### Task 7: Prove Restart Recovery, Retirement, and the Next Generation

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Recovery.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/RestartableArcanumProfileFixture.cs` only if recovery reveals a missing owner lifetime.
- Modify production only after a deterministic restart RED.

**Recovery test:** `Parked_authenticated_transition_recovers_before_second_host_readiness`

Use a theory over direct V4 and standalone-factory V2. Do not duplicate the existing malformed/foreign/mismatched journal matrix; this task proves that a real second host composes the already-authenticated evidence with readiness and next-generation admission.

- [ ] **Step 1: Park host 1 and prove its live postcondition**

For each entry point:

1. Create a shared `RestartableArcanumProfileFixture` and host 1.
2. Seed exact protected/ordinary/managed-file evidence.
3. Inject `AfterPhaseBegin`/`CanonicalApplied` uncertainty and call the authenticated apply route.
4. Assert the recording operation-store observer's last successful write is `ReconciliationRequired`, then assert the active authenticated journal/key/anchor, exact owner/binding, and `KeepClosed` on both gates without opening an ordinary database connection.
5. While host 1 still runs, prove authenticated `/api` and `/v1` refusal plus request/work/open refusal.
6. Record the closed generation and profile/database/credential identities.
7. Dispose only host 1; assert every shared profile resource still exists and `WaitForNextOpenGenerationAsync` remains incomplete. Stage two has already burned the old opening generation, so the assertion is that no successor is published as *open*, not that the numeric generation never advanced.

Remove the first-host fault from the next factory's service overrides. Do not edit journal, database, managed files, installation identity, key, anchor, or launch payload between hosts.

- [ ] **Step 2: Pause host 2 after authenticated owner adoption**

Create host 2 from the same profile. Replace the scoped `ILongRunningOperationMaintenanceLeaseAdoption` registration with:

```csharp
services.RemoveAll<ILongRunningOperationMaintenanceLeaseAdoption>();

services.AddScoped<ILongRunningOperationMaintenanceLeaseAdoption>(sp =>
    new PausingMaintenanceLeaseAdoption(
        sp.GetRequiredService<LongRunningOperationStore>(),
        recoveryCheckpoint));
```

Replace the production readiness interface with one pre-created object, rather than adding an unrelated concrete instance:

```csharp
GrimoireDbReadiness readiness = new();

services.RemoveAll<IGrimoireDbReadiness>();

services.AddSingleton(readiness);

services.AddSingleton<IGrimoireDbReadiness>(readiness);
```

Register the final hosted-service startup sentinel after the production hosted services. Start host 2 on a dedicated thread because `WebApplicationFactory.CreateClient` synchronously waits for host startup:

```csharp
Task<HttpClient> starting = Task.Factory.StartNew(
    secondFactory.CreateAuthenticatedClient,
    CancellationToken.None,
    TaskCreationOptions.LongRunning,
    TaskScheduler.Default);

await recoveryCheckpoint.WaitUntilReachedAsync();

Assert.False(starting.IsCompleted);

Assert.False(readiness.IsReady);

Assert.False(ordinaryBootstrapStarted.Task.IsCompleted);
```

The decorator pauses only after the real adoption returns `Acquired == true`. Reaching it therefore proves startup authenticated the journal, reconstructed the closed gate owner, loaded the exact row fingerprint, and adopted that owner before convergence. Do not claim an HTTP refusal from host 2 while ASP.NET startup is intentionally incomplete.

- [ ] **Step 3: Release and prove V4 direct recovery**

Release the adoption barrier and await host/client startup. Assert:

- the exact existing operation resumes; no replacement operation is created;
- V4 direct binding, owner, revision, and resume evidence match the parked journal;
- the phase handler converges the destructive transition, privately verifies it, publishes runtime authority, and only then completes exact database/parent reconciliation;
- the Grimoire observer records the recovered disposition once; the operation/journal terminal evidence and Covenant gate postcondition prove the Covenant lease accepted that same disposition, with `CovenantOperationGateTests` retaining one-shot authority;
- the operation reaches its correct terminal state/digest;
- normal retirement writes and rereads the `Closed` anchor before deleting the exact journal file, using the ordering observer plus `GrimoireOfflineTransitionJournalStoreTests` as the local ordering authority;
- readiness and the post-Grimoire hosted-service sentinel complete only after retirement;
- the journal file is absent after startup while the closed-anchor tombstone and stable journal key remain present; and
- fresh authenticated request, work lease, and physical open succeed at the successor generation.

- [ ] **Step 4: Release and prove V2 standalone-factory recovery**

Repeat the same restart lifecycle with factory-erasure evidence. Additionally assert:

- checkpoint/launch version is V2;
- the same requested/server operation identity is resumed;
- protected plus ordinary cleanup converges, private verification precedes runtime-authority publication, and exact database reconciliation follows that publication;
- `ParentReceiptBindingDigest` is still null;
- no parent receipt is looked up, produced, consumed, or reconciled; and
- readiness/retirement/next-generation ordering matches the direct case.

- [ ] **Step 5: Run RED/GREEN one entry point at a time**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Parked_authenticated_transition_recovers_before_second_host_readiness"
```

If a row fails, preserve the shared profile as failure evidence until the exact journal/row/gate state is recorded. Add a narrower deterministic assertion, then correct only the proven boundary. Every production correction gets its own mutation RED proof.

- [ ] **Step 6: Run the existing startup/authentication authorities**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceAdmissionTests.Parked_authenticated_transition|FullyQualifiedName~GrimoireOfflineTransitionStartupRecoveryTests|FullyQualifiedName~GrimoireOfflineTransitionStartupRecoveryChainTests|FullyQualifiedName~CovenantErasureStartupRecoveryOwnerAdopterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireDbReadinessTests|FullyQualifiedName~CovenantRecoveryAuthorityBootstrapperTests|FullyQualifiedName~GrimoireOfflineTransitionJournalStoreTests|FullyQualifiedName~GrimoireOfflineTransitionFullResetTerminalTests|FullyQualifiedName~CovenantOperationGateTests"
```

Require all missing, foreign, malformed, wrong-version, wrong-owner, wrong-revision, and mismatched-parent cases in those lower-level suites to remain fail-closed.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git add tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Recovery.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionHarness.cs tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceParticipantObservers.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/RestartableArcanumProfileFixture.cs
git add -u src tests/RetroDownfall.Arcanum.Tests
git diff --cached --name-status
git commit -m "test: prove authenticated transition restart recovery"
```

---

### Task 8: Close Credential, Inventory, and Documentation Drift

**Files — credential contract:**

- Modify only for a missing assertion: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests.cs`
- Modify only for a missing assertion: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`
- Modify only for a missing assertion: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionFullResetTerminalTests.cs`
- Modify only for a missing assertion: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalAuthenticationTests.cs`

**Files — exact inventories:**

- Modify only after an inventory RED: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireAdmissionRouteInventoryTests.cs`
- Modify only after an inventory RED: `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireStreamingRouteInventoryTests.cs`
- Modify only after an inventory RED: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`
- Modify only after an inventory RED: `tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs`
- Modify the corresponding `Support/GrimoireStreamingRouteInventory.cs`, `Support/GrimoireConnectionAcquisitionInventory.cs`, `Support/HostedGrimoireProducerInventory.cs`, or `Support/ProductionSourceInventory.cs` only when its test identifies exact drift.

**Files — owning documentation:**

- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs`
- Modify if exact wording is contract-relevant: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantApiDocumentationTests.cs`

- [ ] **Step 1: Pin the complete credential contract**

Audit the four existing credential suites before adding a parallel test. Add only assertions that are absent, pinning:

```text
grimoire-transition-journal-key-{profile digest}
grimoire-transition-journal-anchor-{profile digest}
```

The assertions must prove:

- both are profile-scoped and distinct from each other;
- neither collides with restore/reset/master-key accounts;
- ordinary credential cleanup excludes both active transition accounts;
- normal transition retirement writes and rereads the `Closed` anchor before deleting only the exact journal file, retaining the tombstone and stable key;
- later full installation reset first proves the journal file absent, then compare-removes the closed anchor before the stable journal key; and
- missing/foreign/malformed key or anchor evidence remains indeterminate/fail-closed rather than treated as absence.

Where an added assertion characterizes already-correct code and is green immediately, temporarily perturb only the test's expected account/catalog fixture to prove the assertion detects drift, then restore it. Do not change production merely to manufacture a RED.

- [ ] **Step 2: Run all four exact inventories before editing catalogs**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireAdmissionRouteInventoryTests|FullyQualifiedName~GrimoireStreamingRouteInventoryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests"
```

For any RED, record the discovered production identity and fix the smallest stale/missing catalog or real unadmitted call site. A production call-site fix requires a focused behavioral RED in addition to the inventory failure. Preserve exact bidirectionality: uncatalogued discovery, stale catalog entry, duplicate identity, wildcard identity, and missing authority proof must all still fail independently.

- [ ] **Step 3: Write documentation-contract REDs**

Extend `DocumentationStructureTests` and, where appropriate, `CovenantApiDocumentationTests` to require the owning documents to say all of the following without tracker-number dependence:

- host-wide admission closes and drains admitted finite/billable/hosted work;
- all five named SSE routes quiesce between complete frames and emit one terminal frame;
- physical opens race by generation and losing handles close before exclusive authority;
- `CommitAndReopen`, proven `RollbackAndReopen`, and `KeepClosed` are the only legal endings;
- authenticated startup recovery completes before readiness and fails closed on unverifiable evidence;
- `/api` maintenance refusal is `Grimoire.MaintenanceUnavailable` and `/v1` is `service_unavailable`/`grimoire_maintenance`; and
- no route/DTO/CLI/config/schema/migration surface changed.

Run the documentation tests and inspect RED before editing prose:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~CovenantApiDocumentationTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```

- [ ] **Step 4: Update each owning document**

- In `README.md`, add one concise public paragraph explaining that destructive Grimoire transitions close host-wide admission, drain already-admitted work, resume from authenticated durable evidence, and otherwise remain closed.
- In `docs/Arcanum.DESIGN.md` §5.4 and §§10.20.3–10.20.6, remove the stale statement that the transition journal is not wired to a handler/startup. Document live ordering, physical-open generation loss, worker deferral, the three dispositions, standalone V2 versus V4 recovery, retirement-before-readiness, full-host test coverage, and immutable-SHA qualification.
- In `docs/Arcanum.API.md` §§8.31–8.32, document authentication-before-admission, admission-before-binding/handler, exact `/api` and `/v1` `503` envelopes, exemptions, the five quiesceable routes, and finite/billable drain behavior.
- State explicitly that this change introduces no route, wire DTO, CLI/config surface, schema/DDL, or migration.

Do not edit `docs/Arcanum.Command.Reference.md` or `docs/Compendium.README.md` because their owned surfaces do not change.

- [ ] **Step 5: Run credential, inventory, docs, and full-host clusters**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests|FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~GrimoireOfflineTransitionFullResetTerminalTests|FullyQualifiedName~GrimoireOfflineTransitionJournalAuthenticationTests|FullyQualifiedName~GrimoireAdmissionRouteInventoryTests|FullyQualifiedName~GrimoireStreamingRouteInventoryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~CovenantApiDocumentationTests|FullyQualifiedName~GrimoireMaintenanceAdmissionTests"
```

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git add README.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs tests/RetroDownfall.Arcanum.Tests/Api/CovenantApiDocumentationTests.cs
git add -u tests/RetroDownfall.Arcanum.Tests src
git diff --cached --name-status
git commit -m "docs: publish full-host Grimoire transition contract"
```

---

### Task 9: Freeze, Review, Qualify, Deliver, Close, and Run the Ollama Vision Proof

**Files:**

- Read/review: the complete `origin/main...HEAD` diff.
- Read/freeze: `.github/workflows/ci.yml` and every script invoked below.
- No source, test, documentation, workflow, package, or build-input mutation is permitted after `feature_sha` freezes. Any correction creates a new SHA and restarts this entire task from Step 1.

- [ ] **Step 1: Run one bounded complete-diff review**

Inspect the complete diff against issue #257, parent #239, the approved spec, and this plan. Use independent read-only reviewers for:

1. disposition/journal/recovery correctness;
2. full-host participant coverage and deterministic test quality; and
3. credentials/inventories/docs plus delivery/qualification safety.

Classify findings as Critical, Important, or Minor. Resolve every Critical and Important finding. A behavioral fix begins with a focused failing test and gets mutation proof; a material correction repeats the complete review. Record Minor findings that are intentionally deferred only when they are unrelated to #239/#257 and have no correctness or coverage consequence.

Run pre-freeze hygiene:

```bash
set -euo pipefail

git status --short --branch
git diff --check origin/main...HEAD
git log --oneline --decorate origin/main..HEAD
```

- [ ] **Step 2: Install and pin the private qualification toolchain**

Execute Steps 2 through 11 in one shell so the immutable inputs stay in scope. Every operational block repeats strict mode so copying a block into a new shell remains fail-closed. Establish the evidence directory first, then install the official SDK without changing the system installation:

```bash
set -euo pipefail

feature_tree=/Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-257-full-host-qualification
feature_branch=codex/issue-257-full-host-qualification
repo_root=/Users/mat/Source/apps/RetroDownfall.Arcanum
repo_slug=Retro-Downfall/RetroDownfall.Arcanum
delivery_evidence=$(mktemp -d /private/tmp/arcanum-issue-257-delivery.XXXXXX)
case "$delivery_evidence" in
  /private/tmp/arcanum-issue-257-delivery.*) ;;
  *) exit 1 ;;
esac

sdk_root=/private/tmp/arcanum-dotnet-10.0.401

if test ! -x "$sdk_root/dotnet"; then
  sdk_installer=$(mktemp /private/tmp/dotnet-install-10.0.401.sh.XXXXXX)
  case "$sdk_installer" in
    /private/tmp/dotnet-install-10.0.401.sh.*) ;;
    *) exit 1 ;;
  esac

  sdk_install_status=0
  curl -fL --proto '=https' --tlsv1.2 https://dot.net/v1/dotnet-install.sh -o "$sdk_installer" \
    || sdk_install_status=$?

  if test "$sdk_install_status" -eq 0; then
    bash "$sdk_installer" --version 10.0.401 --install-dir "$sdk_root" --no-path \
      || sdk_install_status=$?
  fi

  rm -f -- "$sdk_installer"
  test ! -e "$sdk_installer"
  if test "$sdk_install_status" -ne 0; then
    exit "$sdk_install_status"
  fi
fi

export DOTNET_ROOT=/private/tmp/arcanum-dotnet-10.0.401
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export ARCANUM_TEST_OS_CREDENTIAL_STORE=true

test "$(dotnet --version)" = "10.0.401"
```

Install missing lint tools rather than omitting their gates:

```bash
set -euo pipefail

command -v rg >/dev/null 2>&1 || brew install ripgrep
command -v jq >/dev/null 2>&1 || brew install jq

if ! command -v ld64.lld >/dev/null 2>&1; then
  brew install lld
  export PATH="$(brew --prefix lld)/bin:$PATH"
fi

command -v shellcheck >/dev/null 2>&1 || brew install shellcheck
command -v go >/dev/null 2>&1 || brew install go
go install github.com/rhysd/actionlint/cmd/actionlint@v1.7.12
```

Record exact versions in delivery evidence:

```bash
set -euo pipefail

{
  dotnet --info
  rg --version
  jq --version
  ld64.lld --version
  python3 --version
  shellcheck --version
  go version
  "$(go env GOPATH)/bin/actionlint" --version
} | tee "$delivery_evidence/tool-versions.txt"
```

- [ ] **Step 3: Freeze the immutable feature SHA**

```bash
set -euo pipefail

cd "$feature_tree"
test "$(git branch --show-current)" = "$feature_branch"
test -z "$(git status --porcelain)"

git fetch origin main grimoire-fixes

base_main=$(git rev-parse refs/remotes/origin/main)
base_grimoire=$(git rev-parse refs/remotes/origin/grimoire-fixes)

test "$base_main" = "$base_grimoire"
git merge-base --is-ancestor "$base_main" HEAD

feature_sha=$(git rev-parse HEAD)
test "${#feature_sha}" -eq 40

issue_242_before=$(gh issue view 242 --repo "$repo_slug" --json state,stateReason,projectItems | jq -S -c .)

require_remote_ref_absent() {
  local ref="$1"
  local deleted_ref
  local ls_remote_status

  if deleted_ref=$(git -C "$repo_root" ls-remote --exit-code origin "$ref"); then
    printf 'Remote ref still exists: %s\n' "$ref" >&2
    return 1
  else
    ls_remote_status=$?
  fi

  test "$ls_remote_status" -eq 2
  test -z "$deleted_ref"
}
```

If either remote base moved from the implementation base, reconcile the feature branch with current `main`, rerun the complete review, freeze a new SHA, and restart this task. Never rewrite published main history.

- [ ] **Step 4: Run the exact complete local matrix on that unchanged SHA**

```bash
set -euo pipefail

test "$(git rev-parse HEAD)" = "$feature_sha"
test "$(dotnet --version)" = "10.0.401"

dotnet tool restore
dotnet restore RetroDownfall.Arcanum.slnx
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
./scripts/packaging/macos/common_test.sh
python3 -m unittest scripts/coverage_threshold_test.py
./scripts/coverage.sh --threshold
./scripts/verify_aot_il_warnings_test.sh
./scripts/verify-aot-il-warnings.sh
dotnet build tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj -c Debug --no-incremental -p:RestoreLockedMode=true --disable-build-servers -m:1
./scripts/benchmark-grimoire-admission.sh --smoke
dotnet build tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj -c Debug --no-incremental --disable-build-servers -m:1
covenant_benchmark_receipt=/private/tmp/arcanum-issue-257-covenant-benchmark-run.json
rm -f -- "$covenant_benchmark_receipt"
covenant_benchmark_status=0
./scripts/benchmark-covenant.sh --gate --record "$covenant_benchmark_receipt" \
  || covenant_benchmark_status=$?
test -f "$covenant_benchmark_receipt" || test "$covenant_benchmark_status" -ne 0
rm -f -- "$covenant_benchmark_receipt"
test ! -e "$covenant_benchmark_receipt"
if test "$covenant_benchmark_status" -ne 0; then
  exit "$covenant_benchmark_status"
fi
./scripts/verify-shipping-publish.sh --rid osx-arm64
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
python3 scripts/align_csharp_blanklines.py --repo . --check
find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR
"$(go env GOPATH)/bin/actionlint"
git diff --check origin/main...HEAD
git status --short --branch
test -z "$(git status --porcelain --untracked-files=all)"
test "$(git rev-parse HEAD)" = "$feature_sha"

printf -- '- `%s`: PASS\n' \
  'dotnet tool restore' \
  'dotnet restore RetroDownfall.Arcanum.slnx' \
  'dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1' \
  'dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1' \
  'dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1' \
  './scripts/packaging/macos/common_test.sh' \
  'python3 -m unittest scripts/coverage_threshold_test.py' \
  './scripts/coverage.sh --threshold' \
  './scripts/verify_aot_il_warnings_test.sh' \
  './scripts/verify-aot-il-warnings.sh' \
  'dotnet build tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj -c Debug --no-incremental -p:RestoreLockedMode=true --disable-build-servers -m:1' \
  './scripts/benchmark-grimoire-admission.sh --smoke' \
  'dotnet build tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj -c Debug --no-incremental --disable-build-servers -m:1' \
  './scripts/benchmark-covenant.sh --gate --record <validated temporary receipt>' \
  './scripts/verify-shipping-publish.sh --rid osx-arm64' \
  './scripts/verify-native-sqlcipher.sh --rid osx-arm64' \
  "python3 -m unittest discover -s scripts/tests -p 'test_*.py'" \
  'python3 scripts/align_csharp_blanklines.py --repo . --check' \
  "find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR" \
  '$(go env GOPATH)/bin/actionlint' \
  'git diff --check origin/main...HEAD' \
  > "$delivery_evidence/local-matrix.md"
```

Require every command to exit zero and every build/test/publish/AOT/native/benchmark/lint leg to have zero unclassified errors or warnings. The coverage command is the only full Arcanum-suite execution.

Preflight the trusted workspace-check capability by executing only the five selected tests without the require flag, capturing the normal console summary, and distinguishing real execution from skips:

```bash
set -euo pipefail

workspace_check_filter='FullyQualifiedName~Macos_ci_requires_real_workspace_check_production_surface|FullyQualifiedName~MacOsSandbox_DeniesLaunchServicesBrokerEscape|FullyQualifiedName~Real_dotnet_build_uses_seeded_assets|FullyQualifiedName~Real_dotnet_lint_uses_seeded_project_state|FullyQualifiedName~Real_dotnet_test_uses_seeded_assets'
workspace_check_log="$delivery_evidence/workspace-check-preflight.log"

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1 --logger 'console;verbosity=normal' --filter "$workspace_check_filter" 2>&1 \
  | tee "$workspace_check_log"

workspace_check_summary=$(rg --no-config -o 'Failed:[[:space:]]+[0-9]+, Passed:[[:space:]]+[0-9]+, Skipped:[[:space:]]+[0-9]+, Total:[[:space:]]+[0-9]+' "$workspace_check_log" | tail -1)
test -n "$workspace_check_summary"

if printf '%s\n' "$workspace_check_summary" | rg --no-config -q 'Failed:[[:space:]]+0, Passed:[[:space:]]+5, Skipped:[[:space:]]+0, Total:[[:space:]]+5'; then
  ARCANUM_REQUIRE_MACOS_WORKSPACE_CHECK=true dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1 --logger 'console;verbosity=normal' --filter "$workspace_check_filter" 2>&1 \
    | tee "$delivery_evidence/workspace-check-qualified.log"
  rg --no-config -q 'Failed:[[:space:]]+0, Passed:[[:space:]]+5, Skipped:[[:space:]]+0, Total:[[:space:]]+5' "$delivery_evidence/workspace-check-qualified.log"
  workspace_check_result='PASS — all five selected production workspace-check tests executed with zero skips.'
else
  printf '%s\n' "$workspace_check_summary" | rg --no-config -q 'Failed:[[:space:]]+0, Passed:[[:space:]]+1, Skipped:[[:space:]]+4, Total:[[:space:]]+5'
  workspace_check_result='INAPPLICABLE — this host reported four production runtime/containment eligibility skips; the dedicated CI job is required.'
fi

printf -- '- `macOS workspace-check runtime preflight`: %s\n' "$workspace_check_result" \
  >> "$delivery_evidence/local-matrix.md"

test -z "$(git status --porcelain --untracked-files=all)"
test "$(git rev-parse HEAD)" = "$feature_sha"
```

If the preflight is inapplicable, retain its exact captured summary/log as the observed reason and require the dedicated CI job. Do not call a zero-exit skipped run a pass, and do not substitute the CI-only SQLCipher asset guard for local native verification. If any matrix command creates an unignored task output, classify it, remove only that exact generated output, and rerun the affected verifier before the clean-tree assertion; never hide or wholesale-clean a changed source/build input.

- [ ] **Step 5: Push and qualify the immutable feature SHA in CI**

```bash
set -euo pipefail

test -z "$(git status --porcelain --untracked-files=all)"
test "$(git rev-parse HEAD)" = "$feature_sha"
git push --set-upstream origin "HEAD:refs/heads/$feature_branch"

remote_feature_sha=$(git ls-remote --exit-code origin "refs/heads/$feature_branch" | awk 'NR == 1 { print $1 }')
test "$remote_feature_sha" = "$feature_sha"

prior_run_ids=$(gh run list --repo "$repo_slug" --workflow ci.yml --event workflow_dispatch --commit "$feature_sha" --limit 1000 --json databaseId,headSha | jq -c --arg sha "$feature_sha" '[.[] | select(.headSha == $sha) | .databaseId]')
dispatch_not_before=$(date -u +'%Y-%m-%dT%H:%M:%SZ')
gh workflow run ci.yml --repo "$repo_slug" --ref "$feature_branch"
```

Resolve the manually dispatched run by immutable commit, then wait for terminal success:

```bash
set -euo pipefail

run_id=

for attempt in {1..24}; do
  run_id=$(gh run list --repo "$repo_slug" --workflow ci.yml --event workflow_dispatch --commit "$feature_sha" --limit 1000 --json databaseId,headSha,createdAt | jq -r --arg sha "$feature_sha" --arg boundary "$dispatch_not_before" --argjson prior "$prior_run_ids" '[.[] | select(. as $run | .headSha == $sha and .createdAt >= $boundary and ($prior | index($run.databaseId)) == null)] | sort_by(.createdAt) | last | .databaseId // empty')
  test -n "$run_id" && break
  sleep 5
done

test -n "$run_id"
gh run watch "$run_id" --repo "$repo_slug" --exit-status
gh run view "$run_id" --repo "$repo_slug" --json headSha,status,conclusion,jobs,url > "$delivery_evidence/ci-run.json"
```

Require the exact SHA and all seven current job names to be terminal success:

```bash
set -euo pipefail

expected_jobs='[
  "Native SQLCipher asset status",
  "Build, test, coverage",
  "macOS workspace-check runtime",
  "Windows test suite",
  "Windows test suite (arm64)",
  "Native AOT IL and executable gates",
  "Formatting and shell lint"
]'

jq -e --arg sha "$feature_sha" --argjson expected "$expected_jobs" '
  .headSha == $sha and
  .status == "completed" and
  .conclusion == "success" and
  ([.jobs[].name] | sort) == ($expected | sort) and
  (.jobs | length) == ($expected | length) and
  all(.jobs[]; .status == "completed" and .conclusion == "success")
' "$delivery_evidence/ci-run.json"
```

The Windows x64 and ARM64 jobs must execute the new full-host tests and shipping gates. A skipped Windows job is a failure for #239.

- [ ] **Step 6: Record the approved historical acceptance deviation**

Before moving either delivery ref, post this accepted criterion to #257:

```bash
set -euo pipefail

gh issue comment 257 --repo "$repo_slug" --body "Acceptance deviation approved by the issue owner on 2026-09-11: the original condition that all #239 work remain off main cannot be satisfied because #243-#256 were already merged and released. The accepted non-destructive replacement is to start #257 from current main, add only #257, freeze one reviewed, locally qualified, CI-green SHA, and fast-forward that identical SHA through grimoire-fixes to main. Published history will not be rewritten."
```

- [ ] **Step 7: Fast-forward the identical SHA through remote `grimoire-fixes` to `main`**

Re-fetch and fail closed if either base moved:

```bash
set -euo pipefail

git fetch origin main grimoire-fixes
test "$(git rev-parse refs/remotes/origin/main)" = "$base_main"
test "$(git rev-parse refs/remotes/origin/grimoire-fixes)" = "$base_main"
test "$(git rev-parse HEAD)" = "$feature_sha"
git merge-base --is-ancestor refs/remotes/origin/main "$feature_sha"
git merge-base --is-ancestor refs/remotes/origin/grimoire-fixes "$feature_sha"
```

Deliver the umbrella ref, prove it, then remove the remote feature ref:

```bash
set -euo pipefail

git push origin "$feature_sha:refs/heads/grimoire-fixes"
remote_grimoire_sha=$(git ls-remote --exit-code origin refs/heads/grimoire-fixes | awk 'NR == 1 { print $1 }')
test "$remote_grimoire_sha" = "$feature_sha"

delivered_sha="$feature_sha"
test "${#delivered_sha}" -eq 40

git push origin --delete "$feature_branch"
require_remote_ref_absent "refs/heads/$feature_branch"
```

Fast-forward remote main to that exact SHA and prove it from the server and a fresh fetch:

```bash
set -euo pipefail

git fetch origin main grimoire-fixes
test "$(git rev-parse refs/remotes/origin/main)" = "$base_main"
test "$(git rev-parse refs/remotes/origin/grimoire-fixes)" = "$delivered_sha"
git merge-base --is-ancestor refs/remotes/origin/main "$delivered_sha"

git push origin "$delivered_sha:refs/heads/main"
remote_main_sha=$(git ls-remote --exit-code origin refs/heads/main | awk 'NR == 1 { print $1 }')
test "$remote_main_sha" = "$delivered_sha"

git fetch origin main
test "$(git rev-parse refs/remotes/origin/main)" = "$delivered_sha"

git push origin --delete grimoire-fixes
require_remote_ref_absent refs/heads/grimoire-fixes
```

- [ ] **Step 8: Remove task-created worktree/branches without changing the dirty checkout**

First remove the clean feature worktree, but retain the local feature branch until local `main` points at the delivered SHA:

```bash
set -euo pipefail

cd /private/tmp
git -C "$repo_root" worktree remove "$feature_tree"
test ! -e "$feature_tree"
```

Capture the dirty primary checkout without publishing file contents:

```bash
set -euo pipefail

primary_evidence=$(mktemp -d /private/tmp/arcanum-issue-257-primary.XXXXXX)
case "$primary_evidence" in
  /private/tmp/arcanum-issue-257-primary.*) ;;
  *) exit 1 ;;
esac

test "$(git -C "$repo_root" branch --show-current)" = "grimoire-fixes"
git -C "$repo_root" diff --cached --quiet

git -C "$repo_root" status --porcelain=v2 -z > "$primary_evidence/before.status"
git -C "$repo_root" diff --name-only -z > "$primary_evidence/before.tracked-paths"
git -C "$repo_root" ls-files --others --exclude-standard -z > "$primary_evidence/before.untracked-paths"
git -C "$repo_root" diff --binary --no-ext-diff \
  | shasum -a 256 \
  | awk '{ print $1 }' \
  > "$primary_evidence/before.tracked.diff.sha256"

while IFS= read -r -d '' file_name; do
  file_digest=$(shasum -a 256 "$repo_root/$file_name" | awk '{ print $1 }')
  printf '%s\t%s\n' "$file_digest" "$file_name"
done < "$primary_evidence/before.untracked-paths" | LC_ALL=C sort > "$primary_evidence/before.untracked.sha256"
```

Update local `main` without touching the primary working tree, then prove every dirty path is collision-free:

```bash
set -euo pipefail

git -C "$repo_root" branch -f main "$delivered_sha"
test "$(git -C "$repo_root" rev-parse refs/heads/main)" = "$delivered_sha"

while IFS= read -r -d '' file_name; do
  old_blob=$(git -C "$repo_root" rev-parse "HEAD:$file_name")
  new_blob=$(git -C "$repo_root" rev-parse "main:$file_name")
  test "$old_blob" = "$new_blob"
done < "$primary_evidence/before.tracked-paths"

while IFS= read -r -d '' file_name; do
  if git -C "$repo_root" cat-file -e "main:$file_name" 2>/dev/null; then
    printf 'Untracked-file collision in delivered main: %s\n' "$file_name" >&2
    exit 1
  fi
done < "$primary_evidence/before.untracked-paths"
```

Only after those guards pass, switch the primary checkout and compare exact before/after inventories and digests:

```bash
set -euo pipefail

git -C "$repo_root" switch main
test "$(git -C "$repo_root" branch --show-current)" = "main"
test "$(git -C "$repo_root" rev-parse HEAD)" = "$delivered_sha"
git -C "$repo_root" diff --cached --quiet

git -C "$repo_root" status --porcelain=v2 -z > "$primary_evidence/after.status"
git -C "$repo_root" ls-files --others --exclude-standard -z > "$primary_evidence/after.untracked-paths"
git -C "$repo_root" diff --binary --no-ext-diff \
  | shasum -a 256 \
  | awk '{ print $1 }' \
  > "$primary_evidence/after.tracked.diff.sha256"

while IFS= read -r -d '' file_name; do
  file_digest=$(shasum -a 256 "$repo_root/$file_name" | awk '{ print $1 }')
  printf '%s\t%s\n' "$file_digest" "$file_name"
done < "$primary_evidence/after.untracked-paths" | LC_ALL=C sort > "$primary_evidence/after.untracked.sha256"

cmp "$primary_evidence/before.status" "$primary_evidence/after.status"
cmp "$primary_evidence/before.tracked.diff.sha256" "$primary_evidence/after.tracked.diff.sha256"
cmp "$primary_evidence/before.untracked-paths" "$primary_evidence/after.untracked-paths"
cmp "$primary_evidence/before.untracked.sha256" "$primary_evidence/after.untracked.sha256"

git -C "$repo_root" branch -d "$feature_branch"
git -C "$repo_root" branch -d grimoire-fixes
```

If any blob, collision, inventory, or digest guard fails, preserve the local branches and primary checkout exactly as they are; never force the switch or deletion. Do not prune the two unrelated stale detached-worktree registrations.

- [ ] **Step 9: Post evidence and close #257, then #239**

Post concise evidence under exact headings `Local`, `AOT`, `Native`, and `CI`. Include the immutable SHA, local commands/results, SDK path/version, AOT and SQLCipher results, CI URL/run ID/head SHA/seven job conclusions, delivery-ref equality, and dirty-checkout digest equality. Do not include credential values, user document contents, temp secrets, or internal transition owner/path detail. Construct the evidence only from already-validated variables and the redacted CI result:

```bash
set -euo pipefail

ci_url=$(jq -er '.url' "$delivery_evidence/ci-run.json")
ci_head_sha=$(jq -er '.headSha' "$delivery_evidence/ci-run.json")
ci_job_results=$(jq -er '.jobs | sort_by(.name)[] | "- \(.name): \(.conclusion)"' "$delivery_evidence/ci-run.json")
test "$ci_head_sha" = "$delivered_sha"

evidence_body="$delivery_evidence/issue-257-evidence.md"
{
  printf '## Local\n\n'
  printf -- '- Immutable SHA: `%s`\n' "$delivered_sha"
  printf -- '- Official private SDK: `/private/tmp/arcanum-dotnet-10.0.401` (`10.0.401`).\n'
  printf -- '- Exact local matrix results:\n'
  sed -n 'p' "$delivery_evidence/local-matrix.md"
  printf -- '- Every applicable local leg completed with zero unclassified warnings or errors.\n'
  printf -- '- Remote `grimoire-fixes` and `main` both resolved to the immutable SHA during delivery; the primary checkout dirty-path inventory and SHA-256 digests matched exactly before and after its guarded switch to `main`.\n\n'
  printf '## AOT\n\n'
  printf -- '- `verify_aot_il_warnings_test.sh`, `verify-aot-il-warnings.sh`, and `verify-shipping-publish.sh --rid osx-arm64`: PASS.\n\n'
  printf '## Native\n\n'
  printf -- '- `verify-native-sqlcipher.sh --rid osx-arm64`: PASS.\n\n'
  printf '## CI\n\n'
  printf -- '- Run: %s (ID `%s`, head `%s`).\n' "$ci_url" "$run_id" "$ci_head_sha"
  printf '%s\n' "$ci_job_results"
} > "$evidence_body"

gh issue comment 257 --repo "$repo_slug" --body-file "$evidence_body"
```

Then close and verify:

```bash
set -euo pipefail

gh issue close 257 --repo "$repo_slug" --reason completed
gh issue view 257 --repo "$repo_slug" --json state,stateReason,projectItems \
  | jq -e '.state == "CLOSED" and .stateReason == "COMPLETED"'

gh api graphql \
  -f owner='Retro-Downfall' \
  -f name='RetroDownfall.Arcanum' \
  -F number=239 \
  -f query='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){issue(number:$number){subIssues(first:100){nodes{number state stateReason}}}}}' \
  | jq -e '.data.repository.issue.subIssues.nodes | ((map(.number) | sort) == [243,244,245,246,247,248,249,250,251,252,253,254,255,256,257] and all(.[]; .state == "CLOSED" and .stateReason == "COMPLETED"))'

gh issue close 239 --repo "$repo_slug" --reason completed
gh issue view 239 --repo "$repo_slug" --json state,stateReason,projectItems \
  | jq -e '.state == "CLOSED" and .stateReason == "COMPLETED"'

issue_242_after=$(gh issue view 242 --repo "$repo_slug" --json state,stateReason,projectItems | jq -S -c .)
test "$issue_242_after" = "$issue_242_before"
gh issue view 242 --repo "$repo_slug" --json state \
  | jq -e '.state == "OPEN"'
```

Require all fifteen parent children (#243 through #257) and both target issues to report `CLOSED`/`COMPLETED`. If the live relationship count or membership differs, inspect the parent before closing it rather than trusting the historical range. If a project item has appeared, inspect its real field/options and move it to the repository's actual Done value; do not invent a project or status. Verify #242 remains unchanged and open.

- [ ] **Step 10: Run the fresh delivered-main Native AOT Ollama proof**

This is separate from issue acceptance and occurs only after delivered `main`, issue closure, and branch cleanup. Do not start, stop, or reconfigure the app-owned Ollama instance.

Preflight the exact image, listener, and model:

```bash
set -euo pipefail

test "$(shasum -a 256 /Users/mat/Downloads/stop-sign.jpg | awk '{ print $1 }')" = "88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0"
curl --noproxy '*' -fsS http://127.0.0.1:11434/v1/models | jq -e 'any(.data[]; .id == "gemma4:e4b")'
```

Create a fresh detached worktree at the exact delivered SHA and publish with a fresh artifacts path:

```bash
set -euo pipefail

export DOTNET_ROOT=/private/tmp/arcanum-dotnet-10.0.401
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export ARCANUM_TEST_OS_CREDENTIAL_STORE=true

test "${#delivered_sha}" -eq 40
test "$(dotnet --version)" = "10.0.401"

git -C "$repo_root" fetch origin main
test "$(git -C "$repo_root" rev-parse refs/remotes/origin/main)" = "$delivered_sha"

qualification_parent=$(mktemp -d /private/tmp/arcanum-issue-257-ollama.XXXXXX)
case "$qualification_parent" in
  /private/tmp/arcanum-issue-257-ollama.*) ;;
  *) exit 1 ;;
esac
qualification_tree="$qualification_parent/tree"
qualification_root="$qualification_parent/output"
qualification_log="$delivery_evidence/ollama-proof.log"

cleanup_qualification() {
  original_status=$?
  trap - EXIT
  set +e
  cd /private/tmp
  cleanup_status=0
  if test -d "$qualification_tree"; then
    git -C "$repo_root" worktree remove "$qualification_tree" \
      || git -C "$repo_root" worktree remove --force "$qualification_tree" \
      || cleanup_status=$?
  fi
  if test ! -e "$qualification_tree"; then
    rm -rf -- "$qualification_parent" || cleanup_status=$?
  else
    cleanup_status=1
  fi
  if test "$original_status" -ne 0; then
    exit "$original_status"
  fi
  exit "$cleanup_status"
}

trap cleanup_qualification EXIT

git -C "$repo_root" worktree add --detach "$qualification_tree" "$delivered_sha"
cd "$qualification_tree"

test "$(git rev-parse HEAD)" = "$delivered_sha"
test -z "$(git status --porcelain)"

mkdir -p "$qualification_root"

dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release -r osx-arm64 --self-contained true --artifacts-path "$qualification_root/artifacts" --disable-build-servers -m:1 -o "$qualification_root/publish" 2>&1 | tee "$qualification_root/publish.log"

if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$qualification_root/publish.log"; then
  exit 1
else
  scan_status=$?
fi

test "$scan_status" -eq 1

./scripts/verify-local-ollama-aot.sh --executable "$qualification_root/publish/RetroDownfall.Arcanum.Cli" --image /Users/mat/Downloads/stop-sign.jpg --model gemma4:e4b --endpoint http://127.0.0.1:11434/v1/ --configuration Release 2>&1 | tee "$qualification_log"

test -z "$(git status --porcelain)"
cd /private/tmp
git -C "$repo_root" worktree remove "$qualification_tree"
test ! -e "$qualification_tree"
rm -rf -- "$qualification_parent"
trap - EXIT
```

Require verifier exit zero and its exact `local-ollama-aot-qualification:v1` receipt. The proof must establish three persisted user/assistant turns, corrected attachment v2 selection after restart, encrypted `ARCABLOB` attachment storage, independent image inference, and the stop-sign facts `OBJECT=STOP SIGN`, `COLOR=RED`, `SHAPE=OCTAGON`, `SIDES=8`, and `TEXT=STOP`.

If this proof reveals a #239/#257 regression, reopen both issues, create a new `codex/` repair branch from delivered main, reproduce RED, fix through TDD, and repeat the complete review/local/CI/exact-SHA delivery cycle. If it reveals a different Arcanum defect, diagnose it, repair it through a separately named TDD branch within the user's authorized scope, fully qualify the new delivered SHA, and do not call the overall request complete until the proof passes. If Ollama/model/image availability is the sole external blocker, preserve the evidence and report that precise blocker without misclassifying issue acceptance.

- [ ] **Step 11: Verify final immutable and remote state**

```bash
set -euo pipefail

test "$(git -C "$repo_root" branch --show-current)" = "main"
test "$(git -C "$repo_root" rev-parse HEAD)" = "$delivered_sha"
test "$(git -C "$repo_root" rev-parse refs/remotes/origin/main)" = "$delivered_sha"
test "$(git -C "$repo_root" ls-remote --exit-code origin refs/heads/main | awk 'NR == 1 { print $1 }')" = "$delivered_sha"
require_remote_ref_absent "refs/heads/$feature_branch"
require_remote_ref_absent refs/heads/grimoire-fixes

test -z "$(git -C "$repo_root" for-each-ref --format='%(refname)' "refs/heads/$feature_branch" refs/heads/grimoire-fixes)"

gh issue view 257 --repo "$repo_slug" --json state,stateReason \
  | jq -e '.state == "CLOSED" and .stateReason == "COMPLETED"'
gh issue view 239 --repo "$repo_slug" --json state,stateReason \
  | jq -e '.state == "CLOSED" and .stateReason == "COMPLETED"'
issue_242_final=$(gh issue view 242 --repo "$repo_slug" --json state,stateReason,projectItems | jq -S -c .)
test "$issue_242_final" = "$issue_242_before"

git -C "$repo_root" diff --cached --quiet
git -C "$repo_root" status --porcelain=v2 -z > "$primary_evidence/final.status"
git -C "$repo_root" diff --name-only -z > "$primary_evidence/final.tracked-paths"
git -C "$repo_root" ls-files --others --exclude-standard -z > "$primary_evidence/final.untracked-paths"
git -C "$repo_root" diff --binary --no-ext-diff \
  | shasum -a 256 \
  | awk '{ print $1 }' \
  > "$primary_evidence/final.tracked.diff.sha256"

while IFS= read -r -d '' file_name; do
  file_digest=$(shasum -a 256 "$repo_root/$file_name" | awk '{ print $1 }')
  printf '%s\t%s\n' "$file_digest" "$file_name"
done < "$primary_evidence/final.untracked-paths" | LC_ALL=C sort > "$primary_evidence/final.untracked.sha256"

cmp "$primary_evidence/before.status" "$primary_evidence/final.status"
cmp "$primary_evidence/before.tracked-paths" "$primary_evidence/final.tracked-paths"
cmp "$primary_evidence/before.tracked.diff.sha256" "$primary_evidence/final.tracked.diff.sha256"
cmp "$primary_evidence/before.untracked-paths" "$primary_evidence/final.untracked-paths"
cmp "$primary_evidence/before.untracked.sha256" "$primary_evidence/final.untracked.sha256"

worktree_inventory=$(git -C "$repo_root" worktree list --porcelain)
if printf '%s\n' "$worktree_inventory" | rg --no-config -F -x "worktree $feature_tree"; then
  exit 1
fi
if printf '%s\n' "$worktree_inventory" | rg --no-config -F -x "worktree $qualification_tree"; then
  exit 1
fi

git -C "$repo_root" status --short --branch
printf '%s\n' "$worktree_inventory"

case "$primary_evidence" in
  /private/tmp/arcanum-issue-257-primary.*) ;;
  *) exit 1 ;;
esac
case "$delivery_evidence" in
  /private/tmp/arcanum-issue-257-delivery.*) ;;
  *) exit 1 ;;
esac
rm -rf -- "$primary_evidence" "$delivery_evidence"
test ! -e "$primary_evidence"
test ! -e "$delivery_evidence"
```

The final primary status must still show the user's exact pre-existing documentation changes on local `main`. Report completion only after both GitHub issues are closed completed, all delivery refs are verified, task branches/worktrees are gone, and the detached delivered-main Ollama proof has passed.

## Completion Checklist

- [ ] Direct V4 and standalone-factory V2 authenticated full-host commit clusters pass.
- [ ] Proven pre-effect rollback and uncertain `KeepClosed` pass for both entry points.
- [ ] Restart recovery authenticates/adopts before readiness and opens exactly the successor generation.
- [ ] Credential, route, stream, connection, and hosted-producer inventories are exact and green.
- [ ] Public/API/design documentation matches the implemented lifecycle and declares unchanged surfaces.
- [ ] Complete diff review has no unresolved Critical or Important finding.
- [ ] Exact local matrix is green on one immutable 10.0.401 SHA with zero unclassified warnings/errors.
- [ ] All seven CI jobs, including Windows x64 and ARM64, succeed on that same SHA.
- [ ] The identical SHA moves through remote `grimoire-fixes` to remote `main`; task branches and `grimoire-fixes` are removed under the dirty-checkout guard.
- [ ] Issues #257 and #239 are closed as completed; #242 is unchanged.
- [ ] Fresh detached-main Native AOT `gemma4:e4b` three-turn/vision qualification passes against the pinned stop-sign image.
