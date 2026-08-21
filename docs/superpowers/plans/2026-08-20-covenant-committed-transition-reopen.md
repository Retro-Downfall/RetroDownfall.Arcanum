# Covenant Committed Authority Transition and Same-Process Reopen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the production Covenant erasure coordinator publish a completely verified fresh runtime generation, restart its warm disclosure writer, and reopen status, CRUD, and inference safely in the same process.

**Architecture:** Carry the sidecar-free verified candidate directly into a fail-closed authority/capability publisher while the exact exclusive gate remains held. Add one singleton serialized disclosure writer, a comprehensive bounded inventory, a two-pass healthy-catalog guard, and recovery-handler integration; retain the public route refusal for the subsequent route slice.

**Tech Stack:** .NET 10, C# 14, xUnit, Microsoft.Data.Sqlite with SQLCipher, EF Core, source-generated JSON, Microsoft.Extensions.DependencyInjection.

**Spec:** `docs/superpowers/specs/2026-08-20-covenant-committed-transition-reopen-design.md`

## Global Constraints

- Read `README.md` and every file under `docs/` before production edits; that review is complete for this plan.
- Preserve `Cli → Api → Infrastructure → Core`; do not introduce ASP.NET, SQLite, EF, CLI, or provider dependencies into Core.
- Preserve Native AOT: no reflection-based JSON, no anonymous API DTOs, and no unregistered `/api` payloads.
- Use `Result`/`Result<T>` for domain and infrastructure failures; logs and errors remain content-free.
- Preserve the ten frozen erasure phases and both frozen checkpoint wire shapes.
- Do not activate the Covenant memory-reset or healthy-catalog factory-erasure routes.
- Use the existing thematic names; every new type remains under the established Covenant metaphor.
- Follow the repository C# style, including one blank line after each line of code.
- Every production behavior change starts with a test observed failing for the intended behavioral reason.
- Use real SQLCipher-backed integration tests whenever storage, catalog, sidecar, or connection behavior is claimed.
- Use deterministic fakes only for otherwise unobservable fault injection or concurrency control; assert the production component's state and outcome, not fake call existence.
- Publish live keys, authority, and availability through one immutable composite state and one compare-exchange; no sequential provider swaps are permitted.
- Keep inventory total memory bounded to one page and dispose every inventory connection before canonical exclusive maintenance begins.
- Do not make intermediate commits. The user explicitly requires the commit only after all verification is green.
- Preserve the existing untracked `.idea/.idea.RetroDownfall.Arcanum/.idea/.name` file in the `long-term-memory` worktree.

---

## File structure

### Core contracts

- Modify `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnvelopeContracts.cs` to carry the complete committed capability transition.
- Modify `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationLeaseContracts.cs` so every lease binds the runtime authority generation.
- Modify `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantEnvelopeCodec.cs` only where contract documentation must describe retirement and whole-generation publication.
- Modify `src/RetroDownfall.Arcanum.Core/Security/CovenantAuthoritySnapshot.cs` to carry its runtime publication generation.
- Modify `src/RetroDownfall.Arcanum.Core/Intelligence/OperatorAuthorityContext.cs` and `CovenantReadAuthorityEpoch.cs` so in-process capabilities bind that generation.
- Modify `src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantArtifactErasureContracts.cs` so ordinary purge authority joins on runtime generation as well as durable epoch.

### Infrastructure runtime publication

- Create `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantRuntimeGenerationProvider.cs` as the one atomic holder read by keys, authority, and availability.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeMasterKeyProvider.cs` to prepare, publish, abandon, and retire salted key generations.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeCodec.cs` to copy key material safely across publication/retirement races.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthorityTransitionPublisher.cs` to publish keys, authority, and availability fail-closed.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantContextProvider.cs` to bind a boundary read epoch to its acquired turn lease.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthoritySnapshotProvider.cs` to support explicit authority withdrawal.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantAvailability.cs` to publish one complete committed snapshot generation.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs` to return the complete verified candidate.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureTransition.cs` as the production adapter over canonical erasure, storage health, and authority publication.

### Infrastructure lifecycle and inventory

- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs` so its SQL transaction logic is a narrow committer.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs` as the singleton warm owner and lifecycle.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs` to expose one internal bounded page reader using the existing decoder.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureRequestReader.cs` for the shared adopted-write/work-item projection.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs` to use that shared managed-file reader.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs` for the exact/lower-bound nonrevocable fold.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs` for bounded comprehensive work construction.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs` for the canonical/optional-accelerator manifest proof.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs` to expose a pooling-disabled, non-immutable read-only connection.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantResetCheckpointInitiator.cs` to guard factory erasure before checkpoint creation.

### Coordinator, recovery, and composition

- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs` for verified-state handoff, lease-derived dataset identity, bounded post-proof lifecycle, exact exposure, and durable disposition failure.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs` for scoped coordinator injection and Covenant mutation recovery.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs` for Covenant factory-erasure recovery.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs` to use the shared disclosure fold.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` to register exactly one production graph in both Grimoire compositions.
- Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs` for pre-readiness reconstruction of the exact durable gate owner.
- Modify `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs` to run that adopter and close the production `PublishReadiness` boundary.

### Tests

- Modify `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeMasterKeyProviderTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeCodecTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityTransitionPublisherTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityStartupReconcilerTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Security/OperatorAuthorityContextIssuerTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantContextProviderTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArtifactErasureAuthorityTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantPersistedAvailabilityPublisherTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateFixture.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaTestInstaller.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantAvailabilityTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantLocalErasureStorageHealthTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureTransitionTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureWriterTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInventorySourceTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantHealthyCatalogErasureGuardTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureTransactionTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`.
- Modify `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`.
- Create `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`.
- Extend `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureFixture.cs` only with reusable real-SQLCipher setup required by those integration tests.

### Owning documentation

- Modify `README.md`.
- Modify `docs/Arcanum.DESIGN.md`.
- Modify `docs/Arcanum.Design.Human.md`.
- Modify `docs/Arcanum.OATH.md`.
- Modify `docs/ArcanumOATH.Human.md`.
- Modify `docs/Arcanum.DEBUGGING.Human.md`.
- Modify `docs/Arcanum.API.md`.
- Modify `docs/Arcanum.Command.Reference.md`.

---

### Task 1: Make every key generation fresh, zeroizable, and race-safe

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeMasterKeyProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeCodecTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeMasterKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeCodec.cs`

**Interfaces:**

- Produces: internal `CovenantEnvelopeBootstrapKeyInput` with nullable dataset identity for startup-only partial key generations.
- Produces: `Result<CovenantPreparedEnvelopeKeyGeneration> PrepareRekey(CovenantCommittedAuthorityTransition transition)`; Task 1's compile-safe adapter reads the current contract, and Task 2 replaces that mapping when it replaces the Core transition contract.
- Produces: `void PublishPrepared(CovenantPreparedEnvelopeKeyGeneration prepared)`.
- Produces: `void RetireCurrentGeneration()`.
- Transitional: the existing `Rekey` becomes a prepare/publish wrapper only for the Task 1 checkpoint; Task 2 removes every standalone live-key mutation entry point when ownership moves to the composite holder.
- Produces: synchronized key-copy APIs; no caller retains a span into generation-owned key arrays.
- Produces: a temporary `Initialize(Span<byte>, CovenantCommittedAuthorityTransition)` adapter that maps the current startup caller into `CovenantEnvelopeBootstrapKeyInput`; Task 2 migrates the reconciler and deletes this overload.
- Changes: the canonical `Initialize` path consumes `CovenantEnvelopeBootstrapKeyInput`; committed rekeying rejects a missing dataset. Task 2 changes its source from the removed legacy member to the sole `transition.Capability.DatasetGeneration` in the same change that introduces `Capability`.
- Preserves: partial startup key-family behavior, `ICovenantEnvelopeMasterKeyProvider.Current`, and diagnostic-key derivation behavior.

- [ ] **Step 1: Write the failing all-six-purpose rekey test**

Name the break: removing the per-publication salt or resetting counters under identical keys must make the test fail.

```csharp
[Fact]
public void Rekey_with_unchanged_recovery_epoch_invalidates_all_six_purposes()
{

    using Harness harness = Harness.Create();

    Dictionary<CovenantEnvelopePurpose, string> old = Enum
        .GetValues<CovenantEnvelopePurpose>()
        .ToDictionary(
            static purpose => purpose,
            purpose => harness.Codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5)).Value);

    Result rekeyed = harness.Keys.Rekey(
        Transition(
            canonicalEnvelopeEpoch: 4,
            recoveryEnvelopeEpoch: 2,
            dataset: NextDataset));

    Assert.True(rekeyed.IsSuccess);

    foreach ((CovenantEnvelopePurpose purpose, string token) in old)
    {

        Assert.False(harness.Codec.Decode(purpose, token).IsSuccess);

        Assert.True(harness.Codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5)).IsSuccess);

    }

}
```

- [ ] **Step 2: Run the focused test and verify the expected red result**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantEnvelopeMasterKeyProviderTests.Rekey_with_unchanged_recovery_epoch_invalidates_all_six_purposes
```

Expected: FAIL because at least `FamilyReinitialize`, `CampaignPathIdentity`, and `SessionCampaignBinding` still authenticate under the unchanged recovery epoch.

- [ ] **Step 3: Write failing prepare/abandon/retire and complete zeroization tests**

Add tests proving a prepared generation does not affect `Current`, abandoning zeroizes it without affecting current tokens, publishing swaps once, and retirement makes encoding/decoding fail while a later prepared generation can still publish. Use a narrow deterministic derivation-checkpoint seam to throw after the third purpose key and assert every allocated key, the diagnostic key if allocated, the random generation salt, every HKDF info buffer, and every binding temporary is zeroized; the production constructor uses the no-op seam. The seam exposes only zeroization observations, never key contents in a failure or log.

Add bootstrap-input tests for a null dataset and a healthy dataset. A null dataset must derive the three installation-keyed recovery families and diagnostic key while leaving Cursor, OperatorPreflight, and WardRetirement unkeyed. At the Task 1 checkpoint, committed rekey rejects a null legacy dataset; Task 2 moves that same invariant to the required nonempty `transition.Capability.DatasetGeneration`. No placeholder dataset may be invented.

```csharp
[Fact]
public void Retiring_live_keys_preserves_the_root_for_a_later_recovery_publication()
{

    using Harness harness = Harness.Create();

    string old = harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(5)).Value;

    harness.Keys.RetireCurrentGeneration();

    Assert.False(harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, old).IsSuccess);
    Assert.False(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [2], TimeSpan.FromMinutes(5)).IsSuccess);

    Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(
        Transition(canonicalEnvelopeEpoch: 4, dataset: NextDataset));

    Assert.True(prepared.IsSuccess);

    harness.Keys.PublishPrepared(prepared.Value);

    Assert.True(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [3], TimeSpan.FromMinutes(5)).IsSuccess);

}
```

- [ ] **Step 4: Implement prepared salted generations with exception-safe cleanup**

Add a validated internal `CovenantEnvelopeBootstrapKeyInput` whose dataset is nullable, and make it the canonical startup initialization path. To keep the Task 1 checkpoint buildable, retain the current `Initialize(Span<byte>, CovenantCommittedAuthorityTransition)` signature only as a delegating adapter that constructs this input; Task 2 migrates `CovenantAuthorityStartupReconciler` and every test caller to the new input/composite initializer, then deletes the adapter. Generate a random 32-byte generation salt for initialization and every `PrepareRekey`, include it in `BuildInfo` for all derived purpose keys, and keep it out of diagnostic derivation. Startup skips only dataset-keyed families when the bootstrap dataset is null. In Task 1, `PrepareRekey` requires the current transition's legacy dataset member to be nonnull and derives all six families; Task 2 deletes that mapping and reads only `transition.Capability.DatasetGeneration` as part of the atomic Core contract replacement. Never keep both sources. Allocate each info/binding buffer into an explicitly owned temporary and zero it plus the salt in `finally`. On any derivation exception, zero every allocated purpose key, diagnostic key, and still-owned temporary before returning a content-free failure. `CovenantPreparedEnvelopeKeyGeneration` owns the unpublished generation and disposes it unless ownership is transferred exactly once. `RetireCurrentGeneration` exchanges `_current` with null and disposes the retired keys while leaving `_root` intact.

- [ ] **Step 5: Run the provider suite and keep it green**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantEnvelopeMasterKeyProviderTests
```

- [ ] **Step 6: Write and observe failing key-copy retirement race tests**

Name both breaks: a codec retaining a `ReadOnlySpan<byte>` while another thread zeroizes its generation, or returning an old result after publication, must fail. Add deterministic barriers around key copy, cryptography, retirement, and post-crypto generation revalidation. Force publication after the codec copies/reserves but before it can materialize its result. Assert encode returns stale without returning a token, decode returns stale and zeroizes its plaintext, diagnostic-key copy remains race-safe, and every caller-owned temporary is zeroized.

- [ ] **Step 7: Replace borrowed spans with synchronized copies**

Move key copying and counter reservation behind synchronized generation methods that return an opaque captured key-generation identity. The codec and diagnostic source use fixed 32-byte caller-owned temporary buffers and zero them in `finally`. Retirement marks the generation unavailable and zeroizes owned arrays only while holding the same synchronization boundary. Encode and decode ask the provider whether that identity is still current after cryptography and before constructing a string or returning plaintext. On mismatch they zero result bytes and return `Covenant.StaleSnapshot`; no encoder may return an old token after the publication linearization point. In Task 2, move that identity check into the composite holder and bind it to the runtime authority generation without changing the behavioral tests.

- [ ] **Step 8: Run key-provider and codec suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantEnvelopeMasterKeyProviderTests|FullyQualifiedName~CovenantEnvelopeCodecTests"
```

Expected: PASS with no warnings.

---

### Task 2: Publish one atomic runtime authority generation

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnvelopeContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationLeaseContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Security/CovenantAuthoritySnapshot.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/OperatorAuthorityContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/CovenantReadAuthorityEpoch.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/OperatorAuthorityContextIssuer.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantArtifactErasureContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantRuntimeGenerationProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeMasterKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeCodec.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthoritySnapshotProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthorityStartupReconciler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantContextProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantAvailability.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantPersistedAvailabilityPublisher.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthorityTransitionPublisher.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantRuntimeGenerationProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeMasterKeyProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeCodecTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArtifactErasureAuthorityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantAvailabilityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityTransitionPublisherTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/OperatorAuthorityContextIssuerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityStartupReconcilerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantContextProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaTestInstaller.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantPersistedAvailabilityPublisherTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantAuthorityBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantPlaintextExportTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantProtectedResultTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreStartupRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantBackupDisclosureOrderingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathMarkerLifecycleTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantFamilyReinitializeCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantLeasedServiceResultTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/ProtectedAssistantArtifactReaderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/RecordingCovenantOperationGate.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Repositories/ProtectedArtifactTransferStoreTests.cs`

**Interfaces:**

- Produces: `CovenantCommittedCapabilityTransition` with the expected/runtime generations, complete schema health, canonical and accelerator positions, Session/cleanup verification facts, rebuild, diagnostics, and feature fields.
- Changes: `CovenantCommittedAuthorityTransition` carries the verified host-tools state, transition id, and one canonical `Capability`; legacy top-level capability-generation, dataset-generation, and enabled members are removed.
- Produces: one `CovenantRuntimeGenerationProvider` state read by keys, authority, and availability.
- Produces: runtime-generation-bound authority snapshots, operator contexts, read epochs, and lease snapshots.
- Changes: turn context acquisition compares the boundary read epoch with the acquired lease before any store read or mutation staging.
- Produces: exact-owner recovery resume/revalidation from a retired state without reopening ordinary admission.
- Produces: synchronous `Result ExecuteWhileHeld(Func<Result> callback)` on `ICovenantExclusiveOperationLease` and `ICovenantExclusiveLeaseRegistration`; the callback is the short gate-locked final publication boundary and performs no await.
- Produces: `Result<CovenantAvailabilitySnapshot> BuildCommittedTransition(CovenantAvailabilitySnapshot expected, CovenantCommittedCapabilityTransition transition, CovenantHealthTransition healthTransition)` without publishing.
- Consumes: prepared key-generation APIs from Task 1.

- [ ] **Step 1: Write the failing one-generation availability test**

Name the break: publishing canonical and accelerator halves separately or ignoring transition feature/generation fields must make the test fail.

```csharp
[Fact]
public void A_committed_reset_builds_the_complete_tuple_in_one_generation()
{

    CovenantAvailability availability = HealthyAvailability();
    CovenantAvailabilitySnapshot expected = availability.Current;

    Result<CovenantAvailabilitySnapshot> published = availability.BuildCommittedTransition(
        expected,
        ResetCapability(expected.Generation, expected.Generation + 1, NextDataset),
        CovenantHealthTransition.Reset);

    Assert.True(published.IsSuccess);
    Assert.Equal(expected.Generation + 1, published.Value.Generation);
    Assert.Equal(NextDataset, published.Value.DatasetGeneration);
    Assert.Equal(0, published.Value.CanonicalSequence);
    Assert.Null(published.Value.AppliedDatasetGeneration);
    Assert.Null(published.Value.AppliedSequence);
    Assert.True(published.Value.RebuildRequired);
    Assert.Equal(CovenantHealthTransition.Reset, published.Value.LastHealthTransition);

}
```

- [ ] **Step 2: Run the availability test and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantAvailabilityTests.A_committed_reset_builds_the_complete_tuple_in_one_generation
```

Expected: compile failure because the complete transition API does not exist.

- [ ] **Step 3: Add the complete Core transition contracts and atomic availability method**

Use a validated positional record for the content-free capability state. Require positive expected and target generations with `target == expected + 1`, a nonempty dataset, nonnegative canonical, Campaign, Session, and cleanup sequences, an all-or-nothing applied dataset/search/Campaign tuple, defined capability/FTS/rebuild codes, and coherent schema health metadata. `BuildCommittedTransition` requires reference identity with the captured expected availability and returns a complete successor without mutation.

Replace the legacy `CapabilityGeneration`, `DatasetGeneration`, and `CovenantEnabled` inputs/properties on `CovenantCommittedAuthorityTransition` with validated `CovenantHostToolsState HostToolsState`, `string? TransitionId`, and exactly one `CovenantCommittedCapabilityTransition Capability`. Do not retain duplicate projections. The publisher and key derivation read `transition.Capability.Generation`, `transition.Capability.DatasetGeneration`, and `transition.Capability.FeatureEnabled`; they may not select the same fact from another member or copy host-tools facts from the old runtime snapshot.

```csharp
public sealed record CovenantCommittedCapabilityTransition(
    long ExpectedGeneration,
    long Generation,
    bool FeatureEnabled,
    CovenantCapabilityState Canonical,
    int? CanonicalSchemaVersion,
    string? CanonicalInstalledFingerprint,
    CovenantCapabilityState Accelerator,
    int? AcceleratorSchemaVersion,
    string? AcceleratorInstalledFingerprint,
    Guid DatasetGeneration,
    long CanonicalSequence,
    long CoreCampaignDeletionSequence,
    long CanonicalAppliedCampaignDeletionSequence,
    long CanonicalAppliedSessionDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch,
    CovenantFtsSynchronizationState FtsSynchronization,
    bool RebuildRequired,
    long CleanupAppliedCampaignSequence,
    long CleanupAppliedSessionSequence,
    bool CleanupFullSweepRequired,
    string? CanonicalDiagnosticCode,
    string? AcceleratorDiagnosticCode);
```

- [ ] **Step 4: Write the failing composite-state, capability-coherence, and context-revocation tests**

Construct one shared runtime provider, then bind key, authority, and availability providers to it. Assert each `Current` projects the same predecessor state. Hold publication immediately before its compare-exchange and run codec, issuer, gate, and availability readers; release it and assert every observed tuple is either the entire predecessor or the entire successor. No intermediate key/authority/availability combination is legal, and the API offers no second capability/dataset input that could disagree with `Capability`.

Issue an `OperatorAuthorityContext`, `CovenantReadAuthorityEpoch`, ordinary lease, and exclusive lease under a predecessor whose durable authority epoch and master-key version are deliberately unchanged by the successor. After publication, assert all four predecessor values fail revalidation solely because their captured runtime authority generation differs. Assert an availability-only update preserves that runtime generation and does not revoke them.

Update `CovenantArtifactErasureAuthority.ForOrdinary` and its tests so the operator context and write lease must match both durable authority epoch and runtime authority generation. Keep installation identity and master-key version absent from the lease snapshot as before. A context/lease pair with equal durable epoch but different runtime generation must return `Covenant.StaleSnapshot` before issuer or lease revalidation.

Add a deterministic `CovenantContextProvider` barrier test: issue invocation epoch G, pause before turn-lease acquisition, publish G+1 with unchanged durable authority counters, then let the provider acquire a G+1 lease. It must compare the epoch to the lease, dispose the lease, return `Covenant.StaleSnapshot`, never call `ICovenantStore`, and never create a mutation collector. The matching case retains the G lease and proves an exclusive close must drain it before publication.

Add exact-state CAS tests. Build a committed successor from expected state object S, publish an availability-only successor S2 with the same runtime authority generation, then attempt final authority publication. Success must reject the stale state-reference, conditionally retire the matching authority generation, and preserve S2's feature/schema-health/diagnostic availability byte-for-byte. A different authority-generation winner must remain active. Add post-disposition, disposed-registration, and replaced-closure tests proving neither revalidation nor the final publication callback can succeed unless the exact closure object still names the exact live registration.

- [ ] **Step 5: Implement the composite holder and migrate provider reads**

`CovenantRuntimeGenerationState` holds an internal monotonic runtime authority-generation number, nullable live keys, an active-or-retired authority slot, an optional exact recovery-owner marker, and one nonnull availability snapshot. The active `CovenantAuthoritySnapshot` carries the same positive runtime generation. `OperatorAuthorityContext`, `CovenantReadAuthorityEpoch`, and `CovenantOperationLeaseSnapshot` capture it; every `Matches`/gate revalidation compares it as well as the existing durable facts. `CovenantReadAuthorityEpoch` also matches an acquired lease by runtime generation plus durable authority epoch. `CovenantContextProvider` performs that comparison immediately after acquisition and disposes a mismatch before store access. `CovenantEnvelopeMasterKeyProvider.Current`, `CovenantAuthoritySnapshotProvider.Current`, and `CovenantAvailability.Current` project from the same `Volatile.Read`. The public authority projection is null for a retired state even if content-free durable counters are retained internally for exact recovery.

Ordinary availability publication updates a copied composite state under the holder's single publication/key-copy lock while preserving runtime authority generation, key, and authority references. `CovenantOperationGate` captures/revalidates from one composite read rather than combining two provider reads. Bootstrap converts persisted authority plus the healthy canonical envelope, if any, into `CovenantEnvelopeBootstrapKeyInput`; it never fabricates a committed capability transition. It prepares offside and atomically initializes authority plus the partial-or-complete key generation only after the complete persisted availability snapshot exists. Degraded, absent-envelope, or master-version-mismatched canonical state publishes clean authority with only recovery-family keys; dataset-family issuance remains denied. Host-tools denial or preparation failure exposes neither. `CovenantPersistedAvailabilityPublisher` publishes canonical and accelerator facts in one availability generation rather than two.

Delete the Task 1 `Rekey` wrapper, temporary transition-based `Initialize` adapter, and every standalone `PublishPrepared`, `RetireCurrentGeneration`, authority `Publish`/withdraw, or committed availability mutation entry point once the composite holder owns live state and the startup reconciler has migrated. The key provider may prepare an owned unpublished generation and the authority/availability facades may project/read or build an immutable successor, but only `CovenantRuntimeGenerationProvider` may initialize, publish, retire, or dispose a live generation. Architecture tests use reflection plus real interleavings to prove no injectable facade can mutate one member independently.

Migrate every Task 1 key/codec harness in the same step. Live initialize/rekey/publish/retire and post-crypto-current checks must run through one real `CovenantRuntimeGenerationProvider`; only root establishment, preparation, abandonment, derivation-failure zeroization, and unpublished-generation disposal remain direct key-preparer tests. Replace assertions that call the transitional mutators with composite initialize/publish/retire operations, and bind the codec's opaque Task 1 identity check to the holder's runtime authority generation. Do not delete an API until all production and test call sites are migrated in this task.

Perform a complete constructor/interface sweep in this task. `RuntimeAuthorityGeneration` is a required positive positional member of `CovenantAuthoritySnapshot` and `CovenantOperationLeaseSnapshot`, never an optional/default property. Update every production join and every direct constructor/fake listed in this task. Add `ExecuteWhileHeld` to both exclusive lease interfaces and migrate every direct implementation; fake implementations may return an explicit unsupported failure only when the owning test can prove the callback is unreachable. No default interface method may silently bypass the gate proof.

- [ ] **Step 6: Run runtime-provider and availability suites green**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantRuntimeGenerationProviderTests|FullyQualifiedName~CovenantAvailabilityTests|FullyQualifiedName~CovenantAuthorityStartupReconcilerTests"
```

Add exact reconciler cases for unavailable, degraded, absent-envelope, and master-version-mismatched canonical startup. Each must atomically expose active clean authority plus usable FamilyReinitialize, CampaignPathIdentity, and SessionCampaignBinding keys, refuse all three dataset-keyed purposes, and publish no mixed intermediate state. Host-tools denial and derivation failure expose neither authority nor any key family.

Extend `CovenantPersistedAvailabilityPublisherTests` so a successful bootstrap publication advances availability by exactly one generation and an observation barrier never sees a canonical-only or accelerator-only intermediate tuple. Preserve the existing absent-tier generation-stability coverage.

- [ ] **Step 7: Extend publisher tests to assert one linearization point and all failure retirements**

Update the publisher harness to use one real composite provider. Assert the publication callback runs inside the gate's exact-live-registration boundary, all six old tokens fail, old operator/read contexts and old leases fail even with unchanged durable authority counters, the new authority snapshot matches the transition, and availability advances exactly once. For already-cancelled input, stale/revoked/post-disposition lease, replaced closure, thrown lease proof, validation, derivation, and final-publication failures, assert the observed old key and externally issuable authority are conditionally retired. If a deterministic race installed a complete replacement first, assert the failed publisher does not retire that replacement.

After a retirement, dispose the failed registration and resume the same closed scope with the exact durable recovery owner. Assert that recovery-only lease revalidates against the retired runtime generation and can publish successfully from the resident root. A wrong owner and every ordinary acquisition remain refused. Repeat failure/retry to prove the marker remains exact and no stale owner gains reach.

- [ ] **Step 8: Implement whole-generation compare-exchange and fail-closed retirement**

Capture the exact `CovenantRuntimeGenerationState` reference, its runtime authority generation, and exact recovery owner before awaiting lease revalidation. Validate the complete successor and prepare keys off to the side. Successful publication requires reference equality with that exact captured state (equivalently, both exact availability-reference and runtime-generation identity) under the holder lock; a same-authority availability update is stale and may not be overwritten. Failure retirement uses the deliberately broader runtime-generation predicate and copies the latest current availability into its retired successor. A different authority-generation winner is untouched.

Add an exact-held execution seam to the exclusive registration/lease. At the final linearization point, the gate takes `_sync`, requires that the same `Closure` is still installed and its `LiveRegistration` is reference-equal to this registration, and invokes only the short composite publication callback while holding `_sync`; that callback acquires the holder lock. The global lock order is gate then holder. Holder methods never call the gate, and gate code reads composite state lock-free, so there is no reverse edge. `RevalidateAsync` also rejects a claimed disposition, released registration, removed closure, null live registration, or replacement registration. This prevents publication after `CommitAndReopen`/`RollbackAndReopen` has already opened admission.

Inside that exact-held callback, publish one successor holding the prepared keys, runtime-stamped committed authority, and built availability; only after success transfer prepared ownership and dispose the predecessor keys. Any failure, including lease revalidation or exact-held proof failure, conditionally retires the observed generation, preserving the latest diagnosable availability and an internal content-free predecessor/owner marker while exposing no authority or purpose key to ordinary callers. Use the transition's verified host-tools state and transition id directly.

Refactor gate fact capture so ordinary acquisition still requires an active authority. `ResumeExclusiveAsync` may use retired facts only after the gate has matched the exact closure owner and the runtime state's exact recovery marker; its snapshot binds the current retired runtime generation. Exclusive revalidation accepts that one pairing. Wrong-owner resume, ordinary acquisition, issuer calls, and read epochs remain denied. Successful publication clears the marker and advances the runtime generation again.

- [ ] **Step 9: Run publisher, runtime, availability, codec, and issuer suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantAuthorityTransitionPublisherTests|FullyQualifiedName~CovenantRuntimeGenerationProviderTests|FullyQualifiedName~CovenantAvailabilityTests|FullyQualifiedName~CovenantEnvelopeCodecTests|FullyQualifiedName~OperatorAuthorityContextIssuerTests|FullyQualifiedName~CovenantContextProviderTests|FullyQualifiedName~CovenantOperationGateTests"
```

---

### Task 3: Carry the immutable verified candidate directly into production publication

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureTransition.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantLocalErasureStorageHealthTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureTransitionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`

**Interfaces:**

- Produces: complete `CovenantVerifiedCandidateState` from one immutable connection, including the core Campaign-deletion maximum.
- Produces: production `CovenantErasureTransition : ICovenantErasureTransition`.
- Consumes: one captured expected `CovenantRuntimeGenerationState` from Task 2.
- Changes: `VerifyReopenAsync` returns `Result<CovenantVerifiedCandidateState>` and `PublishCommittedAsync` consumes that value.
- Removes: `ReadCandidateDatasetGenerationAsync`.

- [ ] **Step 1: Write failing verified-candidate projection tests**

Name the break: omitting any runtime publication field or accepting malformed applied-tuple/authority enum state must make these tests fail.

Seed literal canonical, accelerator, authority, owner-deletion, and cleanup values in `CovenantCanonicalErasureFixture`; assert the returned candidate carries every value exactly, including host-tools state, transition id, canonical sequence, core Campaign-deletion maximum, canonical Campaign/Session watermarks, cleanup Campaign/Session cursors and full-sweep flag, applied tuple, accelerator epoch, and rebuild state.

- [ ] **Step 2: Run the focused storage-health tests and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantLocalErasureStorageHealthTests
```

Expected: the new assertions fail because the candidate records currently omit those fields.

- [ ] **Step 3: Extend the immutable read and its validation**

Read the complete `covenant_state`, `covenant_authority_state`, and cleanup projection through the existing immutable handle. Read `COALESCE(MAX(owner_deletion_events.Sequence), 0)` for Campaign owners in the same SQLite statement as the canonical/accelerator state, matching `CovenantPersistedAvailabilityPublisher`. Convert closed enum codes only after `Enum.IsDefined`; reject an incomplete applied tuple, cleanup mismatch, or malformed authority state with `Covenant.IntegrityFailure`.

- [ ] **Step 4: Write failing production transition delegation and projection tests**

```csharp
[Fact]
public async Task Publish_projects_only_the_verified_candidate_into_the_runtime_transition()
{

    TransitionHarness harness = new();
    CovenantVerifiedCandidateState candidate = harness.VerifiedCandidate();

    Result published = await harness.Subject.PublishCommittedAsync(
        harness.Lease,
        candidate,
        CancellationToken.None);

    Assert.True(published.IsSuccess);
    Assert.Equal(candidate.Dataset.DatasetGeneration, harness.Publisher.Transition!.Capability.DatasetGeneration);
    Assert.Equal(candidate.Authority.AuthorityEpoch, harness.Publisher.Transition.AuthorityEpoch);
    Assert.Equal(candidate.Dataset.CoreCampaignDeletionSequence, harness.Publisher.Transition.Capability.CoreCampaignDeletionSequence);
    Assert.Equal(candidate.Capability.AppliedSessionSequence, harness.Publisher.Transition.Capability.CleanupAppliedSessionSequence);
    Assert.Equal(candidate.Dataset.AcceleratorEpoch, harness.Publisher.Transition.Capability.AcceleratorEpoch);

}
```

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantErasureTransitionTests
```

Expected: compile failure because the production transition does not exist.

- [ ] **Step 5: Implement the production transition**

Delegate canonical and storage methods without opening extra connections. Capture one `CovenantRuntimeGenerationState` reference and construct `CovenantCommittedAuthorityTransition` entirely from the verified candidate plus that state's single availability snapshot. Carry expected/next generation, feature flag, both capability states, schema versions/fingerprints, diagnostics, and every persisted position; select FTS `Dirty` for a healthy accelerator after reset and `Unavailable` when the accelerator tier is absent/degraded. Publication must compare-exchange against the captured runtime state; a feature or schema-health mutation after capture returns stale and fail-closes the captured key/authority generation.

- [ ] **Step 6: Write failing coordinator tests for direct handoff and resumed immutable reverification**

Name the break: restoring the ordinary candidate-generation read or skipping verification at a persisted `ReopenedVerified` phase must fail.

```csharp
[Fact]
public async Task Resume_from_ReopenedVerified_reverifies_immutably_and_publishes_that_exact_candidate()
{

    CoordinatorHarness harness = new();

    await harness.CloseAndAdoptAsync();

    Result<CovenantErasureCompletion> completion = await harness.RunAsync(
        CovenantResetPhase.ReopenedVerified);

    Assert.True(completion.IsSuccess);
    Assert.Equal(1, harness.Transition.VerifyReopenCalls);
    Assert.Same(harness.Transition.VerifiedCandidate, harness.Transition.PublishedCandidate);
    Assert.Equal(0, harness.Transition.OrdinaryReadCalls);

}
```

- [ ] **Step 7: Refactor coordinator verification flow**

Always obtain a live `CovenantVerifiedCandidateState` after the durable storage phases. On a fresh pass, checkpoint `ReopenedVerified` only after successful verification. On a resumed pass at that phase, repeat verification without rewriting the checkpoint. Delete candidate-generation progress and resolution code.

- [ ] **Step 8: Run transition, storage-health, and coordinator suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantErasureTransitionTests|FullyQualifiedName~CovenantLocalErasureStorageHealthTests|FullyQualifiedName~CovenantErasureCoordinatorTests"
```

---

### Task 4: Make coordinator disposition and recovery exact

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantOperationGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantDisclosureWriterLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantBackupDisclosureOrderingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantContextProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSchemaRepairTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/FakeLongRunningOperationStore.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaTestInstaller.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/RecordingCovenantOperationGate.cs`

**Interfaces:**

- Changes: `RunAsync(LongRunningOperation operation, CovenantErasureCheckpointState checkpoint, string ownerId, CancellationToken cancellationToken)` derives dataset identity from the lease.
- Changes: `CovenantErasureCompletion` carries `string? BlockingErrorCode`, null only for an unblocked completion.
- Changes: `ICovenantOperationGate` adds required `ResumeOrAcquireExclusiveAsync(CovenantExclusiveRecoveryOwner, CancellationToken)`; update every direct implementation rather than hiding absence behind a default interface body.
- Produces: durable `ReconciliationRequired` transition on failed one-shot disposition.
- Produces: reversible old-generation writer restoration before any `RollbackAndReopen`.
- Produces: one typed first-phase gate operation that resumes an exact closure, acquires only after observing no closure, and never treats owner mismatch as absence.
- Produces: exact pre-readiness durable gate-owner reconstruction after process restart.
- Produces: lease acquisition of `Covenant.MaintenanceFailed` attention rows by the existing reconciler.
- Consumes: production coordinator from Task 3.

- [ ] **Step 1: Write the failing lease-derived inventory generation test**

Name the break: accepting a separate caller-provided dataset generation that disagrees with the exclusive closure must fail.

Remove the harness dataset argument and assert the inventory observes `lease.Snapshot.DatasetGeneration`. The test should initially fail to compile against the old signature.

- [ ] **Step 2: Remove the caller dataset parameter and use the lease snapshot**

Refuse an empty captured dataset with `Covenant.IntegrityFailure` before inventory. Pass only the lease-captured generation into the inventory source.

- [ ] **Step 3: Write the failing post-proof cancellation test**

Name the break: forwarding an already-cancelled request token into publication, writer reopen, or disposition after verified erasure must fail.

```csharp
[Fact]
public async Task Caller_cancellation_after_storage_proof_cannot_cancel_publication_or_reopen()
{

    CoordinatorHarness harness = new();
    using CancellationTokenSource caller = new();

    harness.Transition.OnVerified = caller.Cancel;

    Result<CovenantErasureCompletion> completion = await harness.RunAsync(
        CovenantResetPhase.InventoryPrepared,
        caller.Token);

    Assert.True(completion.IsSuccess);
    Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);
    Assert.False(harness.Transition.PublicationToken.IsCancellationRequested);
    Assert.False(harness.DisclosureWriter.ReopenToken.IsCancellationRequested);

}
```

- [ ] **Step 4: Introduce bounded coordinator-owned lifecycle tokens**

Use distinct bounded tokens for pre-effect writer restoration, publication/writer restart, the one disposition, and durable failure recording, so a timed-out restart still gets one chance to record `KeepClosed`. Do not place a total timeout on storage phases or recovery.

- [ ] **Step 5: Write the failing reversible-quiesce matrix**

Name the break: `RollbackAndReopen` while the warm writer remains closed must fail. Cover cancellation/failure after quiesce closes admission, inventory failure, and the under-gate factory catalog refusal. Assert the coordinator first reopens the writer against the unchanged published dataset; only restoration success permits `RollbackAndReopen`. A restoration failure selects exactly one `KeepClosed` disposition.

- [ ] **Step 6: Strengthen the failed one-shot disposition test and observe red**

Name the break: returning the failure without durable recoverability, releasing twice, or trying a fallback disposition must fail.

Assert returned failure, disposition-token timeout/`OperationCanceledException`, and an injected exception each produce exactly one `CompleteAsync(CommitAndReopen)`, exactly one registration disposal, zero `KeepClosed` attempts, admission still closed, and an operation row in `ReconciliationRequired` with `Covenant.MaintenanceFailed`.

- [ ] **Step 7: Record recoverable lifecycle failure without a second disposition**

Inject `TimeProvider`. Normalize returned and thrown disposition failures at one boundary. Under a fresh recording token, reread the operation and invoke `ILongRunningOperationStore.TryTransitionAsync` with its current revision, validated owner, `LongRunningOperationState.ReconciliationRequired`, and a content-free error code. Retry revision races. Stop successfully only after a fresh read proves `ReconciliationRequired`; if the bounded persistence window ends, prove the row still carries the active resumable checkpoint and return the original gate failure. Never log exception text and never make a second gate call.

- [ ] **Step 8: Write failing mutation and factory recovery tests**

Name the break: parking every supported Covenant checkpoint without attempting the production coordinator must fail.

For each declared phase and both checkpoint shapes, assert the handler validates and uses `operation.LeaseOwner`, reconstructs the recorded owner, resumes through the coordinator, and returns `Completed` only for `CommitAndReopen`. Missing/blank lease ownership returns attention with a closed code. Assert legacy checkpoint versions still take their existing paths.

- [ ] **Step 9: Implement recovery mapping**

Make `RecoverCovenantResetMutation` and `RecoverCovenantFactoryErasure` asynchronous. Decode the checkpoint, require the current leased operation owner, invoke the scoped coordinator, and map `CommitAndReopen` to completed, `RollbackAndReopen` to failed, and `KeepClosed` or a failed coordinator result to attention. Use only `BlockingErrorCode`, the failed result's closed code, or `Covenant.MaintenanceFailed`; never synthesize exception text. Never start a second operation or recompute an effect digest.

- [ ] **Step 10: Write failing pre-readiness owner-adoption tests**

Use real `LongRunningOperations` rows for both kinds and every erasure phase. Simulate a fresh process gate. Assert exactly one current valid Covenant checkpoint adopts its exact `(OperationId, Operation, EffectDigest)` before readiness; version-3 mutations without a Covenant arm and legacy factory rows are ignored without materializing their payloads; malformed Covenant evidence, two conflicting installation owners, or adoption after readiness refuses safely. Include unknown raw state/policy/version/type values, noncanonical operation ids/references, null/empty/text/4,097-byte current payloads, two valid owners, and a pre-existing conflicting gate owner. Assert bootstrap resolves both erasure and schema-repair ownership before either recovery effect, calls `PublishReadiness` immediately before `MarkReady`, and permits no later adoption. Assert the no-lock coexisting CLI neither scans/adopts nor freezes the gate, while the standalone lock-owning CLI does.

- [ ] **Step 11: Implement pre-readiness adoption and the missing readiness boundary**

For a bootstrap holding the installation lock, immediately after restore-authority recovery and before effectful local-erasure/schema-repair recovery, stream the two retention kinds' nonterminal rows through the already-open initialized install connection. Keep unknown nonterminal state values visible so they are rejected rather than filtered out. Project payload bytes with a SQL `CASE` only for a correctly typed current version whose length is 1–4,096 bytes; legacy rows are ignored without materializing an arbitrarily large payload. Validate the closed state/policy/kind tuple, canonical lower-case `N` operation id, exact checkpoint version/reference, current payload type/size, operation code, digest, and phase. Mutation v0-v2, mutation v3 without a Covenant arm, and factory v0 own no gate; unknown/future or malformed current evidence refuses readiness. Retain at most one parsed owner, close the reader, and adopt once; a second owner is fatal and installs neither.

Split schema-repair startup recovery into an effect-free read/decode/adoption prepass and a later execution that consumes that exact prepared intent without rereading or adopting. Run the erasure adopter and then the schema-repair prepass before local-erasure recovery, so an erasure/schema owner conflict aborts bootstrap before either protected recovery acts; after both prepasses, preserve the existing local-erasure-before-schema-repair effect order. A schema journal read, decode, or adoption failure is a failed `Result` and refuses readiness. `KeptClosed` is successful only after the exact schema-repair owner was installed and a later recovery step became uncertain. Normalize malformed journal values to a content-free failure without swallowing caller cancellation.

After protected recovery, call `gate.PublishReadiness()` immediately before `readiness.MarkReady()`, with no await or other work between them. A no-lock CLI coexisting with a host is not the recovery authority and neither scans/adopts nor freezes this boundary in its separate process. Do not add a new operation kind, descriptor, or checkpoint version.

For `InventoryPrepared`, add the required operation to `ICovenantOperationGate` and update the concrete gate plus every direct test implementation. Use one typed `ResumeOrAcquireExclusiveAsync` call that checks the closure under the gate lock: resume an exact adopted owner, take the acquisition arm only when no closure exists, and refuse a conflicting owner without acquisition. Do not branch on the current shared `Covenant.ManualRecoveryRequired` error or its text, because it cannot distinguish absence from mismatch. Add real-gate tests for exact-owner resume, no-owner acquisition, conflicting-owner refusal without acquisition, and a closure appearing at the decision boundary; the last schedule must fail closed rather than replacing or bypassing the winner. Later phases remain resume-only. Extend `FindExpiredAsync` and `TryAcquireLeaseAsync` so a `ReconciliationRequired` row carrying `Covenant.MaintenanceFailed` is eligible for the two Covenant-bearing retention kinds beside the existing data-reconciliation code; unrelated kinds and other attention codes remain excluded.

- [ ] **Step 12: Run coordinator, recovery, adopter, gate, and bootstrap suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantErasureCoordinatorTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~CovenantErasureStartupRecoveryOwnerAdopterTests|FullyQualifiedName~CovenantOperationGateTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~CovenantSchemaRepairTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests"
```

---

### Task 5: Implement the production warm disclosure writer lifecycle

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureJournalTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureWriterTests.cs`

**Interfaces:**

- Produces: `ICovenantDisclosureTransactionWriter` for one existing immediate SQL transaction.
- Produces: `CovenantDisclosureWriter : ICovenantDisclosureJournal, ICovenantDisclosureWriterLifecycle, IAsyncDisposable`.
- Consumes: maintenance connection factory, initializer, connection drain, availability, boot identity, and transaction writer.

- [ ] **Step 1: Extract the transaction writer under existing green tests**

Rename the implementation class in `CovenantDisclosureJournal.cs` to `CovenantDisclosureTransactionWriter`, make its entry point accept an already-open `SqliteConnection`, and update the existing real-SQLCipher journal tests to call it directly. Run those tests before and after the mechanical refactor; no behavior changes in this step.

- [ ] **Step 2: Write failing lifecycle admission tests**

Name the breaks: accepting after quiesce begins, returning before an admitted write ends, reopening twice, and admitting a mismatched dataset must each fail.

```csharp
[Fact]
public async Task Quiesce_closes_admission_before_waiting_for_the_inflight_commit()
{

    WriterHarness harness = await WriterHarness.CreateAsync();
    Task<Result<CovenantDisclosureReceipt>> inFlight = harness.BeginBlockedAcknowledgement();

    Task<Result> quiesce = harness.Subject.QuiesceAsync(CancellationToken.None).AsTask();

    Result<CovenantDisclosureReceipt> rejected = await harness.Subject.AcknowledgeAsync(
        harness.Draft(2),
        CovenantDisclosureEffectCategory.ProviderDispatch,
        harness.Sensitivity,
        CancellationToken.None);

    Assert.True(rejected.IsFailure);
    Assert.False(quiesce.IsCompleted);

    harness.ReleaseCommit();

    Assert.True((await inFlight).IsSuccess);
    Assert.True((await quiesce).IsSuccess);
    Assert.Equal(ConnectionState.Closed, harness.WarmConnection.State);

}
```

Use a deterministic transaction-writer fake only to hold the admitted operation; assert writer admission, task completion, and real connection lifecycle rather than fake call counts.

- [ ] **Step 3: Run the new writer suite and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantDisclosureWriterTests
```

Expected: compile failure because the lifecycle owner does not exist.

- [ ] **Step 4: Implement serialized admission, quiesce, reopen, and disposal**

Set `_accepting = false` under the state lock before awaiting the serializer. Check admission both before and after acquiring it. Quiesce closes, disposes, and unregisters once. Reopen acquires the serializer and first normalizes any existing connection left by a cancelled quiesce; otherwise it opens and initializes a new `ReadWrite` connection, reads `covenant_state.DatasetGeneration`, compares it with the one atomically published availability snapshot, enrolls it, and only then reopens admission. This supports both old-generation restoration before rollback and fresh-generation restart after publication. A partial failure cleans up and remains closed.

- [ ] **Step 5: Add real-SQLCipher warm restart coverage**

Use `CovenantCanonicalErasureFixture` to acknowledge a receipt, quiesce, replace the canonical dataset through the real erasure transaction/publication seam, reopen, and acknowledge a second receipt. Assert the second receipt exists in the fresh connection and that no `-wal`/`-shm` survivor is created before reopen.

Add a real old-generation restoration case: quiesce, leave the dataset/runtime generation unchanged, reopen under the held gate, and acknowledge again. Add a cancelled-quiesce case proving reopen waits for the admitted transaction and neither leaks nor double-enrolls the existing connection.

- [ ] **Step 6: Run journal and lifecycle suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantDisclosureJournalTests|FullyQualifiedName~CovenantDisclosureWriterTests"
```

---

### Task 6: Refuse damaged factory catalogs before any erasure effect

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantResetCheckpointInitiator.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMaintenanceConnectionFactoryTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantHealthyCatalogErasureGuardTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantResetCheckpointInitiatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureTransactionTests.cs`

**Interfaces:**

- Produces: pooling-disabled, non-immutable `OpenReadOnlyAsync` maintenance connections.
- Produces: owning `Task<Result> RequireHealthyAsync(CancellationToken cancellationToken)` and borrowed `Task<Result> RequireHealthyWithinAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)`.
- Consumes: maintenance connection factory, SQLite initializer, manifest inspector, and shipped manifests.

- [ ] **Step 1: Write the catalog matrix tests**

Name the break: treating partial absence, drift, or unexpected Covenant objects as an eligible healthy-catalog erasure must fail.

Use real scratch catalogs for these literal cases:

- healthy canonical plus healthy accelerator succeeds;
- healthy canonical plus wholly absent accelerator succeeds;
- missing canonical table, trigger, or explicit index refuses;
- changed definition refuses;
- accelerator metadata with missing objects refuses;
- any accelerator object without complete tier/metadata refuses;
- unexpected Covenant-owned table, trigger, or index refuses;
- unreadable catalog refuses.
- committed definition damage resident in a live WAL refuses (proving the guard did not use immutable mode).
- absent, duplicated, unhealthy, wrong-version, wrong-source, wrong-installed-fingerprint, or diagnostic-bearing canonical metadata refuses even when every canonical object definition is valid.

Every refusal assertion checks the safe message contains `restore`, `Covenant-family reinitialize`, and `full installation reset`, and contains no object name or database path.

- [ ] **Step 2: Run the guard tests and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantHealthyCatalogErasureGuardTests
```

Expected: compile failure because the guard does not exist.

- [ ] **Step 3: Implement safe pre-erasure read-only connection ownership and the manifest proof**

Add a maintenance-factory API whose connection string is read-only, pooling-disabled, cache-private, and deliberately not `immutable=1`; initialize it in `ReadOnly` mode. Update every production and test implementation of `ICovenantMaintenanceConnectionFactory`, including the scratch database and ordered canonical-erasure fake; do not add a compatibility default that silently opens the wrong mode. The owning guard overload opens/disposes one such connection and transaction. The borrowed overload requires a caller-owned open connection and active read transaction, performs no connection/transaction lifecycle operation, and runs the identical proof. Require a valid canonical manifest plus exactly one healthy canonical `grimoire_feature_schemas` row matching the trusted version, source, installed fingerprint, zero health code, and empty diagnostic. For the accelerator, query the shipped manifest's trusted object names and metadata row; accept only zero objects plus no metadata or a valid complete manifest plus exact healthy metadata. Collapse inspector details into one content-free `Covenant.IntegrityFailure` remedy message.

- [ ] **Step 4: Write and observe the pre-checkpoint refusal test**

Assert `PrepareFactoryErasureInventoryAsync` on a damaged catalog returns failure, writes no checkpoint, and issues no `GateAdmission`.

- [ ] **Step 5: Inject the guard into factory checkpoint initiation**

Run it before digest derivation and before `GateAdmission.CommitInventoryAsync`. Leave Covenant memory-reset preparation unchanged.

- [ ] **Step 6: Run guard, initiator, schema-manifest, and canonical-erasure suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantMaintenanceConnectionFactoryTests|FullyQualifiedName~CovenantHealthyCatalogErasureGuardTests|FullyQualifiedName~CovenantResetCheckpointInitiatorTests|FullyQualifiedName~CovenantSchemaManifestTests|FullyQualifiedName~CovenantCanonicalErasureTransactionTests"
```

---

### Task 7: Build comprehensive bounded erasure inventory and exact disclosure exposure

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureRequestReader.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInventorySourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureJournalTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`

**Interfaces:**

- Produces: `ArtifactSensitivityLedger.ReadPageWithinAsync(SqliteConnection callerOwnedConnection, ..., Guid? afterLabelId, int limit, ...)` using the existing label decoder without opening its scoped EF connection.
- Produces: shared managed-file request reader.
- Produces: `CovenantDisclosureExposure(long PossibleAttempts, CovenantDisclosureCountKind CountKind)`.
- Produces: production `CovenantErasureInventorySource : ICovenantErasureInventorySource` with comprehensive preflight, bounded database-page reads, and bounded managed-page reads.

Use these bounded shapes (all cursors are typed `Guid?`): `CovenantErasureInventorySummary` carries checked database/managed counts plus exposure; `CovenantDatabaseErasureBatch` carries next cursor, completion, and at most one protected page; `CovenantManagedFileErasureBatch` carries next cursor, completion, and at most 256 requests. The source exposes `PreflightBeforeCanonicalAsync`, `PreflightRemainingManagedAsync`, `ReadNextDatabaseBatchAsync`, `ReadNextManagedFileBatchAsync`, and `ReadDisclosureExposureAsync`.

- [ ] **Step 1: Write failing page completeness and classification tests**

Name the breaks: stopping at 256, duplicating or skipping a label at a page boundary, retaining more than one page, using a second purge classification, holding a scoped EF connection, and binding database pages to the wrong dataset must fail.

Seed 769 literal labels spanning database and managed-file executors. Assert four keyset pages, every label exactly once, no materialization above 256, at most one page retained/processed at a time, deterministic label order, and `ExpectedDatasetGeneration` equal to the closed lease generation. After every owned inventory connection is disposed, run the real exclusive canonical erasure and prove it does not block on an inventory handle.

- [ ] **Step 2: Write failing managed-file identity tests**

Seed an adopted managed write with and without an existing nonterminal work item. Assert the existing work-item id is reused, a new request receives one nonempty work-item id, every request carries the durable erasure operation id, and a label without an adopted ownership-bearing producer returns `ManualArtifactErasureRequired` during the complete preflight before any page is handed to a kernel.

- [ ] **Step 3: Run inventory tests and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantErasureInventorySourceTests
```

Expected: compile failure because the production source and shared readers do not exist.

- [ ] **Step 4: Extract shared caller-owned readers and implement bounded multi-pass inventory**

Move the existing label reconstruction into one internal row materializer used by point and page reads. Its page API accepts a caller-owned `SqliteConnection`; it must never call `ICovenantConnectionSource`. Move the adopted managed-write query into one reader used by direct purge and erasure inventory. Inventory opens one pooling-disabled, non-immutable initialized maintenance read connection per preflight/page call and disposes it before returning. Continue keyset pagination until a short page and classify every label through `CovenantSensitiveArtifactPurgePolicy.Resolve`.

The complete first pass uses one read transaction, validates the old dataset against the lease, revalidates the factory catalog on that same snapshot, proves every managed producer, and retains only exposure/count facts. While phase remains `InventoryPrepared`, a second pass streams database-owned pages into the database kernel before canonical erasure. After `CanonicalApplied`, a third pass streams managed requests; it does not compare the new unpublished candidate dataset to the lease's old dataset. A resumed pass at `CanonicalApplied` first exhaustively revalidates all remaining managed producers. The durable phase proves the complete old-dataset preflight already succeeded. Existing work items are reused; a request without one receives a fresh id immediately before the kernel, and any committed insert is discovered on replay.

- [ ] **Step 5: Write failing disclosure algebra tests**

Name the breaks: counting locally revocable buckets, taking a maximum instead of a sum, or erasing lower-bound semantics must fail.

Use real `external_disclosure_state` rows for empty, exact-only, lower-bound-only, locally-revocable-only, and mixed exact/lower-bound/nonrevocable cases. Hand-derive literal expected totals and kinds.

- [ ] **Step 6: Implement the shared exposure reader and richer completion fact**

Return exact zero for no buckets. Read the at-most-eight `RevocabilityCode = 2` rows in deterministic destination order, validate each closed count-kind code, add `JoinedCount` in checked C#, and join the kind to `LowerBound` when any row is lower-bound. Do not use SQLite `SUM`, whose overflow would occur before safe mapping. Malformed/overflow input returns content-free `Covenant.IntegrityFailure`. Make erasure preflight and completion carry the exposure value; derive `ExternalDisclosuresNotRevocable` from `PossibleAttempts > 0`. A resumed post-canonical pass rereads the preserved disclosure rows through the same fold. Reuse the reader from retention status.

- [ ] **Step 7: Revalidate factory catalog on the inventory snapshot**

For `HealthyCatalogFactoryErasure`, call `CovenantHealthyCatalogErasureGuard.RequireHealthyWithinAsync` with the complete preflight's caller-owned connection and read transaction before the first label page. A failure returns no work and lets the coordinator restore the old writer before a pre-erasure rollback. Add a deterministic interleaving test that mutates catalog/data after the read transaction begins and proves the guard and dataset/label scan observe the same snapshot; assert the owning overload is never invoked and no second handle opens.

- [ ] **Step 8: Run inventory, retention, purge, and coordinator suites**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantErasureInventorySourceTests|FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~CovenantSensitivePurgeBoundaryTests|FullyQualifiedName~CovenantErasureCoordinatorTests"
```

---

### Task 8: Register the complete production graph without enabling routes

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`

**Interfaces:**

- Consumes: all production components from Tasks 1–7.
- Produces: exactly one composite runtime holder, one warm writer behind both public interfaces, a shared scoped coordinator/transition/inventory graph, an explicitly shared startup adopter, and host-only checkpoint initiation/DataRetention recovery registrations.

- [ ] **Step 1: Write failing lifetime and identity tests**

Name the breaks: distinct runtime holders behind key/authority/availability/gate, two writer instances, a per-scope drain, a singleton capturing a scoped service, an absent transition or startup adopter, a coordinator missing in either composition, a checkpoint initiator incorrectly resolvable in CLI composition, or duplicate host recovery handlers must fail.

Resolve the CLI Grimoire and full host service collections in two scopes. Assert:

```csharp
Assert.Same(
    provider.GetRequiredService<ICovenantDisclosureJournal>(),
    provider.GetRequiredService<ICovenantDisclosureWriterLifecycle>());

Assert.Same(
    provider.GetRequiredService<CovenantRuntimeGenerationProvider>(),
    provider.GetRequiredService<ICovenantRuntimeGenerationProvider>());

Assert.Same(
    provider.GetRequiredService<ICovenantConnectionDrain>(),
    scope.ServiceProvider.GetRequiredService<ICovenantConnectionDrain>());

Assert.NotSame(
    firstScope.ServiceProvider.GetRequiredService<CovenantErasureCoordinator>(),
    secondScope.ServiceProvider.GetRequiredService<CovenantErasureCoordinator>());
```

- [ ] **Step 2: Run architecture tests and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantArchitectureBoundaryTests
```

Expected: missing-service failures for the unregistered production graph.

- [ ] **Step 3: Register explicit lifetimes in `AddCovenantPersistence`**

Register maintenance connection factory, composite runtime holder, warm writer, drain, canonical erasure, local storage health, runtime publishers, and `CovenantErasureStartupRecoveryOwnerAdopter` explicitly in shared `AddCovenantPersistence`, as singletons only where all dependencies are singleton. Register connection-bound inventory, transition, and coordinator as scoped in shared Covenant persistence so both compositions resolve the same safe graph. Keep `CovenantResetCheckpointInitiator`, its host-only `ICovenantErasureEffectDigestCalculator` dependency, `DataRetentionService`, and the existing mutation/factory recovery handlers in the full host composition only, exactly once. Resolve journal/lifecycle interfaces from the same writer and every runtime-reader interface from the same composite holder. Run `ValidateOnBuild` for both compositions and assert the CLI graph neither requires nor resolves the checkpoint initiator. Assert no resolved key, authority, or availability facade exposes a live-state mutation method that could bypass the composite holder.

- [ ] **Step 4: Keep the route refusal observable**

Strengthen the endpoint/plan test so the public Covenant reset still returns `Data.CovenantResetRequiresErasureCoordinator`, but its safe text says the production coordinator is not yet wired to that route rather than claiming it does not exist.

- [ ] **Step 5: Run composition and endpoint tests**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~DataRetentionPlanningAcceptanceTests"
```

---

### Task 9: Prove a complete same-process reset with real storage

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs`
- Modify production files only when a newly observed failing acceptance test identifies a real defect; every such fix starts a new red/green cycle.

**Interfaces:**

- Consumes: production graph from Tasks 1–8.
- Proves: same-process status, CRUD, inference, and disclosure operation over the fresh dataset.

- [ ] **Step 1: Build the real integrated fixture**

Use one file-backed SQLCipher database, one service provider, and one in-process runtime graph. Seed old canonical content, availability, authority, envelope keys, operation row/checkpoint, sensitivity labels, and disclosure evidence. Capture the concrete runtime holder, key-root owner, writer, old lease, old operator context, and six old tokens before running the coordinator.

- [ ] **Step 2: Write the failing successful-reset acceptance test**

Name the break: any missing publication field, unavailable post-reset service, stale old capability, or writer not restarted must fail.

```csharp
[Fact]
public async Task Successful_erasure_reopens_status_crud_inference_and_disclosure_on_the_fresh_dataset()
{

    await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

    SameProcessBefore before = await harness.SeedAndCaptureAsync();

    Result<CovenantErasureCompletion> reset = await harness.RunAsync();

    Assert.True(reset.IsSuccess);
    Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, reset.Value.Disposition);
    Assert.NotEqual(before.DatasetGeneration, harness.Availability.Current.DatasetGeneration);

    await harness.AssertEveryOldCapabilityRejectedAsync(before);
    await harness.AssertFreshStatusAsync();
    await harness.AssertFreshCrudAsync();
    await harness.AssertFreshInferenceContextAsync();
    await harness.AssertFreshDisclosureWriteAsync();

}
```

- [ ] **Step 3: Run the acceptance test and verify red**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter FullyQualifiedName~CovenantErasureSameProcessTests.Successful_erasure_reopens_status_crud_inference_and_disclosure_on_the_fresh_dataset
```

Expected: fail at the first unimplemented or incorrectly composed production boundary; record that exact reason before changing production code.

- [ ] **Step 4: Drive the acceptance test green one defect at a time**

For each failure, add or narrow a focused regression test at the owning component, observe it fail, implement the smallest correction, rerun the focused suite, then rerun the same-process test. Do not weaken the acceptance assertions. `AssertFreshStatusAsync` must call the real retention-status service, not read availability directly; CRUD seeds and reads fresh content; inference proves fresh visibility and old-content absence; the harness asserts the concrete holder, root owner, and writer identities are unchanged.

Include an in-flight boundary race in the real fixture: mint invocation read epoch G, pause it before lease acquisition, complete the reset/publication to G+1, and release it. The old invocation must return stale before any store read; a newly issued invocation must acquire a G+1 turn lease and see only fresh content.

- [ ] **Step 5: Add the post-commit failure acceptance matrix**

Use deterministic fault injection around the real gate and publisher to prove:

- publication observes installation admission closed;
- every publication failure, including lease revalidation, validation, preparation, and a lost publication race, leaves admission closed and all capabilities from the observed old authority generation unusable without retiring a newer winner;
- writer restart failure makes one `KeepClosed` disposition;
- failed, cancelled, or throwing `CommitAndReopen` records attention, disposes once, and makes no second disposition;
- none of those failures changes the proven canonical/local-erasure facts;
- exact and lower-bound disclosure exposure remains independent.

- [ ] **Step 6: Add the fresh-process recovery matrix**

Recreate the runtime provider and operation gate over the same file-backed database. For both checkpoint shapes and every phase, assert bootstrap adopts the exact durable owner before readiness, ordinary admission never opens early, and host recovery resumes. Add crash boundaries after immutable verification, atomic runtime publication, writer enrollment before admission, disposition failure, and disposition failure before ledger terminalization. All old process tokens fail because the process generation changed; recovery either commits and reopens the complete fresh generation or stays durably closed.

- [ ] **Step 7: Run all Covenant erasure, same-process, and fresh-process tests**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --filter "FullyQualifiedName~CovenantErasure|FullyQualifiedName~CovenantAuthorityTransitionPublisherTests|FullyQualifiedName~CovenantRuntimeGenerationProviderTests|FullyQualifiedName~CovenantDisclosureWriterTests|FullyQualifiedName~CovenantHealthyCatalogErasureGuardTests"
```

---

### Task 10: Update every owning document and remove stale lifecycle claims

**Files:**

- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.Design.Human.md`
- Modify: `docs/Arcanum.OATH.md`
- Modify: `docs/ArcanumOATH.Human.md`
- Modify: `docs/Arcanum.DEBUGGING.Human.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`

**Interfaces:**

- Documents the landed internal behavior without claiming route activation.
- Preserves the authoritative ownership split among design, API, command, config, and orientation documents.

- [ ] **Step 1: Update architecture and operator narratives**

Document verified candidate handoff, composite whole-generation publication, all-six-token retirement including the recovery-keyed families, synchronized key-copy semantics, reversible warm-writer ordering, bounded lifecycle tokens, pre-readiness owner adoption, bounded multi-pass inventory, non-immutable catalog refusal, exact disclosure exposure, durable failed-disposition recovery, and same/fresh-process proof in `Arcanum.DESIGN.md` and both human companions.

- [ ] **Step 2: Update delivery/status documents**

Advance the cumulative Covenant status in `README.md` and both OATH documents through this internal lifecycle slice. State explicitly that the public reset and factory-erasure routes remain refused pending the route-integration slice.

- [ ] **Step 3: Update troubleshooting and stale surface wording**

Add breakpoint/recovery guidance to `Arcanum.DEBUGGING.Human.md`. In API and command references, change only the stale claim that no coordinator exists; retain the exact refusal contract and existing public status semantics.

- [ ] **Step 4: Review documentation boundaries**

Confirm no config key, JSON command map, public DTO, schema migration, constraint ceiling, or historical review snapshot changed. Search for stale phrases such as `no erasure coordinator`, `nothing publishes an authority transition`, `all ten phases park`, and claims that recovery-keyed tokens survive a committed dataset reset, then inspect every match semantically rather than applying a mechanical replacement.

```bash
env -u RIPGREP_CONFIG_PATH rg -n "no erasure coordinator|nothing publishes an authority transition|all ten phases|not yet wire|deliberately absent|survive.*reset|reset.*survive" README.md docs
```

---

### Task 11: Verify, review, integrate, and deliver

**Files:**

- Review every changed source, test, and document.
- Do not stage the existing `.idea` file in the `long-term-memory` worktree.

**Interfaces:**

- Produces: one reviewed commit merged into and pushed from `long-term-memory`.
- Produces: completed child issue state and no merged issue branch.

- [ ] **Step 1: Run formatting and whitespace checks**

```bash
dotnet format RetroDownfall.Arcanum.slnx --no-restore --verify-no-changes
git diff --check
```

Expected: both exit 0 with no diagnostics.

- [ ] **Step 2: Run the warning-free solution build**

```bash
dotnet build RetroDownfall.Arcanum.slnx --no-restore
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run both complete test projects**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --no-restore
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --no-build --no-restore
```

Expected: zero failed tests; only documented environment/platform skips.

- [ ] **Step 4: Run coverage and AOT/IL warning gates**

```bash
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
```

Expected: coverage thresholds pass and the first-party Native AOT closure reports no IL/AOT warnings.

- [ ] **Step 5: Perform specification-compliance review**

Dispatch a fresh reviewer with the accepted design, issue acceptance criteria, and diff. Fix every valid gap with a new failing test before production changes. Rerun affected focused suites.

- [ ] **Step 6: Perform code-quality and security review**

Dispatch a second fresh reviewer after specification compliance is green. Review concurrency, connection disposal, key zeroization, gate linearizability, cancellation, recovery CAS behavior, content-free errors, DI lifetimes, AOT, and documentation accuracy. Resolve every valid finding test-first.

- [ ] **Step 7: Repeat the complete verification after review fixes**

Run Steps 1–4 again and retain the fresh output. Inspect `git status --short`, `git diff --stat`, and the complete diff before staging.

- [ ] **Step 8: Create the single green feature commit**

Stage only intended issue-branch files and commit with a message describing committed authority publication and same-process reopen. Confirm the commit contains tests and documentation and the worktree is clean.

- [ ] **Step 9: Merge into `long-term-memory` and verify the merge result**

In `/private/tmp/RetroDownfall.Arcanum-long-term-memory`, preserve the untracked IDE file, merge `issue-127` with `--no-ff`, and rerun formatting, build, both test projects, coverage, and AOT/IL verification against the merge result.

- [ ] **Step 10: Push and close the completed child issue**

Push only `long-term-memory`. Mark the completed child issue closed with reason `completed`; leave all parent issues open.

- [ ] **Step 11: Remove merged feature branches and worktree**

Remove the `issue-127` worktree, delete the merged local `issue-127` branch, delete a remote issue branch only if one was created, and verify `long-term-memory` matches its remote with the pre-existing untracked IDE file still untouched.
