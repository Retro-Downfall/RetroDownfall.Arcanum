# Covenant Runtime and Authority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate Covenant authority into Arcanum's live inference runtime so every eligible turn uses one canonical Campaign context and one deterministic turn plan, freezes and receipts every provider or tool disclosure, publishes staged mutations atomically with assistant finalization, and propagates protected sensitivity through every derived output.

**Architecture:** Core carries non-serializable invocation authority and the immutable protocol values supplied by Plan 02. API composes canonical Campaign resolution, prompt admission, frozen provider attempts, turn claims, MCP capabilities, Ward decisions, and branch lifecycle through `WizardIntelligenceProvider` and `TurnEngine`. Infrastructure renders the final prompt, bridges in-process MCP state, and extends the existing Grimoire transaction to commit the assistant response, sensitivity label, compact evidence, and sealed Covenant mutation batch once.

**Tech Stack:** .NET 10, C# 14, Native AOT, Microsoft.Extensions.AI, Microsoft.ML.Tokenizers, Model Context Protocol, ASP.NET Core class-library orchestration, EF Core for existing Grimoire entities, raw Microsoft.Data.Sqlite contracts supplied by Plan 02, SQLCipher, source-generated System.Text.Json, and xUnit.

## Global Constraints

- The approved source of truth is [`2026-08-13-covenant-design.md`](../specs/2026-08-13-covenant-design.md). If a plan step and the specification differ, stop that slice and resolve the plan before changing production code.
- Observe the expected failing test before each production change. Record the focused command and failure reason in the task notes or commit message draft.
- Preserve `Cli -> Api -> Infrastructure -> Core`. Core cannot reference provider, EF, SQLite, ASP.NET, or CLI types.
- Use source-generated JSON everywhere. Add every wire type to its owning context before an endpoint or command can compile.
- Keep the disabled, stateless path free of Covenant store access and allocations beyond the feature gate. An untainted Session may use its existing core query to retrieve the sensitivity summary. A tainted Session intentionally takes the protected path.
- Keep productive provider and tool loops unbounded by arbitrary turn counters. Persist and retain O(1) rolling evidence plus bounded diagnostic tails.
- All content-bearing Covenant reads, tainted-history reads, mutations, provider dispatches, MCP uses, accelerator work, reset, restore, and deletion hold the required generation-bound operation lease.
- Never use reflection-based serialization, dynamic schema discovery, numbered EF migrations, ambient authority, or SQL string interpolation.
- Keep one immutable provider-neutral call envelope. Hash, tokenize, attribute, and dispatch from that same frozen representation.
- Treat FTS5 as a derived inspection accelerator. Canonical prompt authority never depends on accelerator health or rank.
- Preserve exact absent-Covenant prompt bytes, cache descriptors, generic-search behavior, and productive-loop behavior.
- `CovenantScope` has exactly `Global` and `Campaign`. Agent mutation is Campaign-bound and Proposed-only, except for admitted Campaign retirement through `retire_covenant`.
- Every `IArcanumIntelligenceProvider`, `ITurnExecutionFacade`, and context-inspection call receives an explicit `ArcanumInvocationContext`. Subagent, A2A, batch, recovery, apprentice, daemon, and background paths pass `ArcanumInvocationContext.None`.
- One logical live turn loads at most 160 active Covenant rows with a row-161 invariant probe, builds one provider-independent plan, and reuses that plan across retry, fallback, compression, and tool-loop calls.
- Every physical provider attempt has a new immutable provider-call envelope, materialization snapshot, sensitivity digest, applicable admission lineage, and branch-attempt identity. Only a Covenant-derived attempt appends a durable disclosure receipt and advances the disclosure-backed global and branch chains.
- Model `ICovenantContextProvider` on Microsoft Agent Framework's pre-invocation context and post-invocation state lifecycle through Arcanum-owned contracts. Do not add a `Microsoft.Agents.AI` package reference in issue #74.
- Confirmed Covenant is all-or-fail. Proposed Covenant is the earliest eviction tier and preserves the longest complete prefix in plan order, with at most 32 bounded suffix removals.
- One turn collector holds at most four live provisional intents and follows `Open -> Sealing -> Sealed` or irreversible `Discarded` lifecycle.
- Proposal provenance is the exact producing `ProviderCallMaterializationSnapshot`, with at most 64 sources. Turn-cumulative attachment ambient state cannot authorize a proposal.
- Sensitive tool buffering permits 64 simultaneous call indexes, 256 strict UTF-8 name bytes, 65,536 raw argument bytes per call, and 262,144 aggregate name-plus-argument bytes per provider attempt.
- Generation provenance stores at most eight exact sorted generation IDs. The ninth distinct ID switches permanently to the specified 256-bit `BloomOverflow` representation supplied by Plan 02.
- A provider, network, process, external MCP, message, or persistent external sink receives no Covenant-derived bytes until its disclosure receipt is durably acknowledged. `CovenantSensitiveEgress` requires an attended interactive Ward over final complete arguments.
- Before the first buffered body or SSE byte, every Covenant-bearing or tainted-history response exposes its sensitivity to the boundary so Plan 04 can set `Cache-Control: no-store, private` and supported legacy proxy headers.
- This plan consumes the compiler, canonical encoder, digest vectors, immutable snapshot and plan models, raw-SQL Covenant store, mutation kernel, canonical search sequence, sensitivity lattice, schema objects, and FTS contracts from Plan 02. Do not implement canonical database store or search internals here.
- This plan exposes typed runtime results and request requirements to Plan 04. Do not implement public Covenant management endpoints, authentication middleware, CLI commands, Compendium screens, cursor/search APIs, backup, restore, reset, retention, or erasure surfaces here.
- Do not commit intermediate red states. One final branch commit is permitted only after all coordinated plans and required verification are green.

---

## Plan 02 Runtime Prerequisites

Plan 02 must provide these exact Core contracts before Tasks 1 through 24 begin:

```csharp
public interface ICovenantStore
{
    ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
        CanonicalCampaignContext campaign,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(
        CanonicalCampaignContext campaign,
        CovenantLane lane,
        string normalizedKey,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantListPage>> ReadListPageAsync(
        CovenantListQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantDetail>> ReadDetailAsync(
        CovenantDetailQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(
        CovenantVersionQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(
        CovenantSourceQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(
        CovenantMutationEffectQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);
}

public interface ICovenantLinker
{
    Result<CovenantTurnPlan> Link(CovenantTurnSnapshot snapshot);
}

public sealed class CovenantMutationKernel
{
    public ValueTask<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyBatchAsync(
        CovenantMutationBatch batch,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);
}

```

Plan 02 also owns the canonical definitions and encoders for `CovenantTurnSnapshot`, `CovenantTurnPlan`, `CovenantAdmissionReceipt`, `ProviderCallEnvelope`, `ProviderCallMaterializationSnapshot`, `FrozenProviderOptions`, `CovenantFinalReceipt`, `ContentSensitivity`, `GenerationProvenance`, `ArtifactSensitivityLabel`, `CovenantMutationIntent`, `CovenantMutationBatch`, `CovenantDisclosureDraft`, `CovenantDisclosureReceipt`, `ICovenantExclusivePostDispositionFinalizer`, the five management query/result pairs shown above, and all associated digests and enum codes. When Plan 02 lands a signature that differs from this contract, amend both plans before production work.

## Task 1: Add the non-serializable invocation authority value

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ArcanumInvocationContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ArcanumExecutionSurface.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/CovenantReadAuthorityEpoch.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/OperatorAuthorityContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/IOperatorAuthorityContextIssuer.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/CovenantAuthorityRequirement.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/CovenantTurnAuthority.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextTests.cs`

**Interfaces:**

- Consumes: existing `ToolPolicy` plus Plan 02's `CovenantContextPolicy`, `InvocationAttendance`, `SessionCampaignBinding`, `CanonicalCampaignContext`, and `CovenantDigest` values.
- Produces: non-serializable `CovenantReadAuthorityEpoch` and `OperatorAuthorityContext` contracts, `IOperatorAuthorityContextIssuer`, `ArcanumInvocationContext.None`, `ArcanumInvocationContext.Create`, and the closed nonserializable `CovenantTurnAuthority` cases `Unprotected`, `ProtectedHistory(CovenantTurnLease)`, and `CurrentCovenant(CovenantTurnLease)` for every provider, commit, turn-facade, and inspection entrypoint. Plan 02 owns the immutable `CanonicalCampaignContext` value.

```csharp
public enum CovenantAuthorityRequirement : byte
{
    ProtectedRead = 1,
    CovenantManage = 2,
    CampaignPathManage = 3,
    SessionBindingResolve = 4,
    LifecycleManage = 5,
    SensitivityRetentionPurge = 6
}

public interface IOperatorAuthorityContextIssuer
{
    Result<OperatorAuthorityContext> Issue(
        CovenantAuthorityRequirement requirement);
}
```

`OperatorAuthorityContext` is a sealed nonserializable value that carries exactly one requirement, the current clean authority epoch, current master-key version, installation identity, and one single-use issuer nonce. The six codes above are immutable and exhaustive. A context issued for one requirement cannot satisfy another. `SensitivityRetentionPurge` is the exact Core authority consumed by Plan 04's guarded artifact erasure path; it is distinct from the Infrastructure connection-local SQL authorization with the same semantic purpose.

Exact test methods:

- `None_IsAuthorityFreeAndContextDisabled`
- `Create_AttendedSessionTurnCarriesCanonicalCampaignAndEpoch`
- `Create_RejectsAuthorityForNonOperatorSurface`
- `OperatorAuthorityContext_IsNonSerializableAndBindsRequirementEpochAndKeyVersion`
- `AuthorityRequirement_CodesAreImmutableAndContextsCannotCrossRequirements`
- `InvocationContext_IsAbsentFromAllJsonSourceGenerationContexts`
- `TurnAuthority_UnprotectedHasNoLeaseAndProtectedCasesRequireExactlyOneTurnLease`
- `TurnAuthority_IsAbsentFromAllJsonSourceGenerationContexts`

- [ ] **Step 1: Write the failing authority tests**

```csharp
[Fact]
public void None_IsAuthorityFreeAndContextDisabled()
{
    ArcanumInvocationContext context = ArcanumInvocationContext.None;

    Assert.Equal(ArcanumExecutionSurface.InternalBackground, context.Surface);
    Assert.Equal(CovenantContextPolicy.None, context.ContextPolicy);
    Assert.Null(context.Campaign);
    Assert.Null(context.ReadAuthorityEpoch);
}

[Fact]
public void Create_RejectsAuthorityForNonOperatorSurface()
{
    Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
        ArcanumExecutionSurface.Subagent,
        campaign: null,
        InvocationAttendance.Unattended,
        CovenantContextPolicy.None,
        ToolPolicy.NoTools,
        CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), 7));

    Assert.False(result.IsSuccess);
    Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);
}
```

- [ ] **Step 2: Run the focused test and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumInvocationContextTests"
```

Expected: FAIL at compile time because `ArcanumInvocationContext`, `ArcanumExecutionSurface`, and `CovenantReadAuthorityEpoch` do not exist.

- [ ] **Step 3: Add the smallest authority-safe Core implementation**

```csharp
public enum ArcanumExecutionSurface
{
    SessionBackedOperatorTurn = 1,
    StatelessOperatorTurn = 2,
    ContextInspection = 3,
    Subagent = 4,
    A2A = 5,
    Batch = 6,
    Recovery = 7,
    InternalBackground = 8,
}

public sealed class ArcanumInvocationContext
{
    public static ArcanumInvocationContext None { get; } = new(
        ArcanumExecutionSurface.InternalBackground,
        null,
        InvocationAttendance.Unattended,
        CovenantContextPolicy.None,
        ToolPolicy.NoTools,
        null);

    public static Result<ArcanumInvocationContext> Create(
        ArcanumExecutionSurface surface,
        CanonicalCampaignContext? campaign,
        InvocationAttendance attendance,
        CovenantContextPolicy contextPolicy,
        ToolPolicy toolPolicy,
        CovenantReadAuthorityEpoch? readAuthorityEpoch)
    {
        bool operatorSurface = surface is
            ArcanumExecutionSurface.SessionBackedOperatorTurn or
            ArcanumExecutionSurface.StatelessOperatorTurn or
            ArcanumExecutionSurface.ContextInspection;

        if (!operatorSurface && readAuthorityEpoch is not null)
        {
            return Result<ArcanumInvocationContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This execution surface cannot carry Covenant authority."));
        }

        return Result<ArcanumInvocationContext>.Success(
            new(surface, campaign, attendance, contextPolicy, toolPolicy, readAuthorityEpoch));
    }
}
```

Keep construction non-serializable. This runtime classification is distinct from Plan 02's three-code `SessionTurnSurface` digest enum. Apprentice, daemon, subagent, A2A, batch, and recovery callers pass `ArcanumInvocationContext.None`; there is no separate authority-bearing Apprentice surface. `CovenantReadAuthorityEpoch` must use an internal production factory and may expose an internal test factory through existing test visibility. `CovenantTurnAuthority.Unprotected` carries no lease and permits no Covenant disclosure, receipt, or mutation. `ProtectedHistory` and `CurrentCovenant` require one exact `CovenantTurnLease`; only `CurrentCovenant` can stage or publish Covenant mutations.

- [ ] **Step 4: Run the focused test to verify green**

Run the Step 2 command.

Expected: PASS for all four `ArcanumInvocationContextTests`.

- [ ] **Step 5: Refactor the eligibility checks into one closed policy**

Add `ArcanumInvocationContext.CanReadCovenant` and `CanStageCovenantMutation` as derived properties. Keep their truth table in this file and rerun the Step 2 command.

Expected: PASS, with no JSON context referencing `ArcanumInvocationContext` or `CovenantReadAuthorityEpoch`.

## Task 2: Implement the six-purpose cryptographic envelope protocol

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnvelopeContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDiagnosticTag.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantEnvelopeCodec.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantAuthorityTransitionPublisher.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantDiagnosticTagger.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeMasterKeyProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/OperatorAuthorityContextIssuer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantEnvelopeCodec.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantDiagnosticTagger.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEnvelopeStateStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeMasterKeyProviderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/OperatorAuthorityContextIssuerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeCodecTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantEnvelopeVectorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityTransitionPublisherTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantDiagnosticTaggerTests.cs`

**Interfaces:**

- Consumes: Plan 01's committed `ICovenantAuthoritySnapshotProvider`, single-take zeroizable startup master-material lease, `covenant_authority_state`, and `covenant_state`; Plan 02 `ICovenantOperationGate`; and Task 1's immutable authority contracts.
- Produces: `ICovenantEnvelopeMasterKeyProvider`, the production `IOperatorAuthorityContextIssuer`, `ICovenantEnvelopeCodec`, `ICovenantAuthorityTransitionPublisher`, `ICovenantDiagnosticTagger`, six closed envelope purposes, current key/epoch snapshots, and content-free decode failures for Ward and every Plan 04 prepare/apply or cursor surface.

`CovenantDiagnosticTag` is an immutable validated Core value containing the nonsecret key version and exactly 16 bytes encoded as unpadded base64url. Its constructor is internal; only `ICovenantDiagnosticTagger.Create(CovenantDigest contentIdentityDigest)` can create a production value, and `FixedTimeEquals(CovenantDiagnosticTag left, CovenantDiagnosticTag right)` owns comparison. It is safe to serialize as a diagnostic correlation value, while a raw `CovenantDigest` is never substituted at a public metadata boundary.

- [ ] **Step 1: Write failing master-key reconciliation and envelope vectors**

Add exact methods:

```csharp
[Fact]
public async Task Startup_key_change_advances_core_version_authority_and_canonical_epochs()

[Fact]
public async Task Rotate_back_never_reuses_a_master_version_or_epoch()

[Theory]
[InlineData(CovenantEnvelopePurpose.Cursor, 1, "Arcanum.Covenant.Cursor.v1")]
[InlineData(CovenantEnvelopePurpose.OperatorPreflight, 2, "Arcanum.Covenant.OperatorPreflight.v1")]
[InlineData(CovenantEnvelopePurpose.WardRetirement, 3, "Arcanum.Covenant.WardRetirement.v1")]
[InlineData(CovenantEnvelopePurpose.FamilyReinitialize, 4, "Arcanum.Covenant.FamilyReinitialize.v1")]
[InlineData(CovenantEnvelopePurpose.CampaignPathIdentity, 5, "Arcanum.Campaign.PathIdentity.v1")]
[InlineData(CovenantEnvelopePurpose.SessionCampaignBinding, 6, "Arcanum.Session.CampaignBinding.v1")]
public void Purpose_codes_and_labels_are_immutable(
    CovenantEnvelopePurpose purpose,
    byte code,
    string label)

[Fact]
public void Deterministic_vector_matches_header_nonce_ciphertext_and_tag()

[Fact]
public void Header_and_body_times_must_match_exactly_after_authentication()

[Fact]
public void Database_snapshot_rollback_cannot_repeat_key_and_nonce_with_new_boot_salt()

[Fact]
public void Authority_issuer_rejects_tainted_stale_or_wrong_requirement_context()

[Fact]
public async Task Transition_reset_switches_dataset_keys_and_rejects_old_tokens_and_contexts()

[Fact]
public async Task Transition_restore_and_reinitialize_publish_committed_epochs_before_reopen()

[Fact]
public async Task Transition_failure_keeps_the_exclusive_gate_closed()

[Fact]
public void DiagnosticTag_MatchesVersioned128BitHmacVector()

[Fact]
public void DiagnosticTag_RotatesWithKeyVersionAndNeverReturnsRawSha256()
```

Cover AES-256-GCM, 96-bit nonce, 128-bit tag, both exact HKDF-SHA-256 salts, purpose labels, header AAD, counter one, maximum issuance, boot salt, current-only key acceptance, and temporary-key zeroization. Inject deterministic randomness only through an internal test seam.

- [ ] **Step 2: Run the red crypto tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantEnvelopeMasterKeyProviderTests|FullyQualifiedName~OperatorAuthorityContextIssuerTests|FullyQualifiedName~CovenantEnvelopeCodecTests|FullyQualifiedName~CovenantEnvelopeVectorTests|FullyQualifiedName~CovenantAuthorityTransitionPublisherTests|FullyQualifiedName~CovenantDiagnosticTaggerTests"
```

Expected: FAIL because no versioned master-key bridge or envelope codec exists.

- [ ] **Step 3: Reconcile master material before endpoint admission**

Take Plan 01's startup master-material lease exactly once after its core transaction commits. Verify its fingerprint against the published authority snapshot, advance the canonical envelope epoch only when canonical state is healthy, derive a zeroizable in-process HKDF root plus every current process-purpose key, then dispose and zero the startup lease. The root remains only inside the non-serializable master-key provider so a committed same-process epoch transition can derive fresh purpose keys without reading the secret store. `OperatorAuthorityContextIssuer` derives read or operator contexts only from that clean current snapshot and rejects stale, tainted, wrong-requirement, or host-tools-enabled epochs. No issue, decode, or transition path performs secret-store or database I/O, and Plan 03 never seeds or rewrites the core master version.

- [ ] **Step 4: Implement exact derivation, wire framing, and bounded parsing**

Generate a 256-bit boot salt before any issuance. Derive purposes 1 through 3 from dataset generation, canonical envelope epoch, and boot salt. Derive purposes 4 through 6 from core installation identity, recovery envelope epoch, and boot salt. Use `UInt32BE(purposeCode) || UInt64BE(counter)` as the nonce and the exact 46-byte `ACVE` header as AAD.

Reject non-ASCII, padding, invalid base64url alphabet, input over 4,096 bytes, wrong header length, version, purpose, key version, epoch, counter, timestamps, lifetime, ciphertext length, trailing bytes, plaintext caps, and authentication before allocating from attacker lengths. Return one content-free invalid result for cryptographic failures.

- [ ] **Step 5: Implement counter rollover and restart invalidation**

Use one atomic unsigned counter per purpose, starting at one. Before the `2^32 - 1` issuance bound, close and drain the affected purpose family, advance its canonical or core recovery epoch transactionally, derive a fresh key, and restart at one. Startup always advances the applicable recovery epoch and, when healthy, canonical epoch before issuance. Failure disables only the affected purpose family.

Implement `ICovenantAuthorityTransitionPublisher.PublishCommittedAsync` over a validated nonsecret committed transition containing installation identity, authority epoch and taint state, dataset generation, core master version, canonical and recovery envelope epochs, capability generation, and feature-enabled state. The caller presents the exact still-held exclusive operation-gate lease that protected the durable transition. Derive fresh purpose keys and construct new codec, issuer, availability, and gate snapshots off to the side, then publish them as one generation transition before the caller may reopen admission. Reset, restore, family reinitialize, and counter rollover use this live-process path. Any validation, derivation, or publication failure leaves old contexts unusable and the affected gate closed. The stopped-host Task 24 transition commits its taint markers and exits; the next host initializes directly into the tainted snapshot before any Covenant key or content service opens.

Derive a separate diagnostic HMAC key with exact label `Arcanum.Covenant.Diagnostics.v1` and the current nonsecret key version. `ICovenantDiagnosticTagger` accepts only a validated content-identity digest, returns a version plus the first 128 bits of HMAC-SHA-256, and compares tags in fixed time. It never exposes the diagnostic key or a raw SHA-256 identifier. Cursor, envelope, path-marker, and diagnostic labels remain separate. Literal vectors cover input framing, truncation, restart stability within the same key version, version rotation, and raw-hash rejection.

- [ ] **Step 6: Run the green crypto and AOT-safe API tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantEnvelopeMasterKeyProviderTests|FullyQualifiedName~OperatorAuthorityContextIssuerTests|FullyQualifiedName~CovenantEnvelopeCodecTests|FullyQualifiedName~CovenantEnvelopeVectorTests|FullyQualifiedName~CovenantAuthorityTransitionPublisherTests|FullyQualifiedName~CovenantDiagnosticTaggerTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. Cross-purpose, tamper, truncation, old-key, old-epoch, restart, rollback, counter, decode-bound, and concurrent-issuance vectors pass without serializing authority objects.

## Task 3: Require invocation context at every inference seam

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IArcanumIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IContextPreviewService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/ITurnExecutionFacade.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionRequest.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/LexiconEntityExtractor.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticRouter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Subagents/SubagentRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiV1BatchesEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ProvingGrounds/ProvingGroundsRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/InferenceExecuteWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SpellExecutionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Daemons/UnseenServantDaemonJob.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestrySummarizer.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/FakeIntelligenceProvider.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/InferenceExecuteWriterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/WebWorkflowEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Daemons/UnseenServantDaemonJobTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/ApprenticeServiceReliabilityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/BatchProcessingServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SubagentRunnerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/ProvingGrounds/ProvingGroundsArbiterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/ProvingGrounds/ProvingGroundsRunnerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/TheForge/PromptExecuteFlowTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextInventoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnExecutionCoordinatorTests.cs`

**Interfaces:**

- Consumes: `ArcanumInvocationContext` from Task 1.
- Produces:

```csharp
Task<Result<PromptTurnResult>> ExecutePromptAsync(
    PingRequest request,
    ArcanumInvocationContext invocationContext,
    CancellationToken cancellationToken,
    InferenceAuditContext? auditContext = null);

IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
    PingRequest request,
    ArcanumInvocationContext invocationContext,
    CancellationToken cancellationToken,
    InferenceAuditContext? auditContext = null);

Task<Result<ContextPreviewResult>> PreviewContextAsync(
    ContextPreviewRequest request,
    ArcanumInvocationContext invocationContext,
    CancellationToken cancellationToken);
```

Exact test methods:

- `EveryProductionInferenceCaller_SelectsAnInvocationSurface`
- `TurnExecutionCoordinator_PreservesReferenceIdenticalInvocationContext`
- `SubagentRunner_AlwaysPassesNone`
- `BackgroundInferenceCallers_AlwaysPassNone`

- [ ] **Step 1: Add the failing signature and inventory tests**

The inventory test must enumerate every production file listed above and assert that the callsite selects either an explicit operator context variable or `ArcanumInvocationContext.None`. It must also inventory direct `ITurnExecutionFacade` and `IContextPreviewService` calls.

```csharp
[Fact]
public async Task TurnExecutionCoordinator_PreservesReferenceIdenticalInvocationContext()
{
    ArcanumInvocationContext expected = InvocationContexts.AttendedSession();

    await coordinator.ExecuteBufferedAsync(request, expected, CancellationToken.None);

    Assert.Same(expected, runner.CapturedRequest!.InvocationContext);
}
```

- [ ] **Step 2: Run the focused tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumInvocationContextInventoryTests|FullyQualifiedName~TurnExecutionCoordinatorTests"
```

Expected: FAIL because the three public seams do not require context and `TurnExecutionRequest` cannot carry it.

- [ ] **Step 3: Change the signatures and classify every current caller**

Add the required parameter with no default value. Pass the same reference through `TurnExecutionCoordinator`. Use `None` for all listed unattended/internal callers. Leave operator-bound construction as an injected value from the boundary, which Plan 04 will supply.

In the same edit, update every test implementation and direct test invocation listed in **Files**. `FakeIntelligenceProvider`, every nested `IArcanumIntelligenceProvider` implementation, and `SubagentRunnerTests.FakeTurnExecutionFacade` must accept and capture the required context before any green command runs. Direct `WizardIntelligenceProvider` and coordinator calls pass an explicit test context. Do not defer these compilation fixes because the filtered test command still compiles the complete test project.

- [ ] **Step 4: Run the focused tests to verify green**

Run the Step 2 command.

Expected: PASS, and the compiler reports no omitted invocation-context argument.

- [ ] **Step 5: Inventory the migrated fakes and reject compatibility bypasses**

Use `ArcanumInvocationContextInventoryTests` to enumerate the production and test implementations listed in **Files** and reject an optional context parameter, default argument, or legacy overload. Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~BatchProcessingServiceTests|FullyQualifiedName~PromptExecuteFlowTests|FullyQualifiedName~WizardIntelligenceProviderTests|FullyQualifiedName~ArcanumInvocationContextInventoryTests"
```

Expected: PASS. There is no overload or default argument that permits a caller to omit authority classification.

## Task 4: Replace Ping request Campaign inference with canonical resolution

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/TheForge/CanonicalCampaignResolutionRequest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/ICanonicalCampaignContextResolver.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/ISessionCampaignBindingReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/ICampaignPathIdentityReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/ICampaignPathMarkerCodec.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/ICampaignRootIdentityKeyProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Api/TheForge/CanonicalCampaignContextResolver.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionCampaignBindingReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignPathIdentityReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/TheForge/PhysicalCampaignRootOpener.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/TheForge/PhysicalCampaignRootMarkerCapabilities.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/TheForge/CampaignPathMarkerCodec.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CampaignRootIdentityKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsRemediationCredentialCatalog.cs`
- Delete after all callers migrate: `src/RetroDownfall.Arcanum.Api/TheForge/PingRequestResolver.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SpellExecutionEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/TheForge/CanonicalCampaignContextResolverTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionCampaignBindingReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/CampaignPathIdentityReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/TheForge/PhysicalCampaignRootOpenerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/TheForge/CampaignPathMarkerCodecTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CampaignRootIdentityKeyProviderTests.cs`

**Interfaces:**

- Consumes: Plan 01's core immutable binding and Campaign path-identity tables, the existing OS credential-store boundary, `WorkspaceRootPolicy`, and no-follow OS handle primitives. Plan 04 owns path registration and administration.
- Produces: the SQLCipher-backed immutable binding reader, indexed Campaign identity reader, one shared path-marker codec/policy, the sealed Infrastructure no-follow physical root opener, its producer-owned retained marker capabilities, and:

```csharp
public interface ICanonicalCampaignContextResolver
{
    ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
        CanonicalCampaignResolutionRequest request,
        CancellationToken cancellationToken);
}

public sealed record CanonicalCampaignResolutionRequest(
    Guid? SessionId,
    Guid? ExplicitCampaignId,
    string? WorkingDirectory);
```

There is no Core marker-capability interface. `PhysicalCampaignRootOpener` is the one `public sealed partial` Infrastructure producer and is registered as itself for `CanonicalCampaignContextResolver` and Task 7. The partial declaration exists only so Plan 04 Task 7 can add its confirmed-orphan proof entry point inside the same sealed producer after that proof type exists. Its ordinary marker-lifecycle entry point is internal and exact:

```text
OpenForMarkerLifecycleAsync(
    Guid campaignId,
    long pathRevision,
    CovenantDigest expectedPhysicalIdentityDigest,
    string canonicalDisplayPath,
    CancellationToken cancellationToken)
    -> ValueTask<Result<PhysicalCampaignRootOpener.MarkerRootCapability>>
```

`MarkerRootCapability`, `MarkerTemporaryHandleCapability`, `MarkerHandleCapability`, and `MarkerCodecBytesLease` are `internal sealed` types nested under `PhysicalCampaignRootOpener`. Each capability has a private constructor reachable only by its containing producer, no default construction, and no interface that another Infrastructure type can implement. The bytes lease has the same construction restriction and owns the only mutable copy returned by a bounded read. Freeze this marker-only surface:

| Sealed owner | Exact retained-handle operation |
|---|---|
| `MarkerRootCapability` | `Guid CampaignId`, positive `long PathRevision`, and `CovenantDigest PhysicalIdentityDigest` |
| `MarkerRootCapability` | `CreateTemporaryExclusiveNoFollowAsync(string temporaryLeaf, CancellationToken) -> ValueTask<Result<MarkerTemporaryHandleCapability>>` |
| `MarkerRootCapability` | `OpenTemporaryNoFollowAsync(string temporaryLeaf, CancellationToken) -> ValueTask<Result<MarkerTemporaryHandleCapability>>` |
| `MarkerTemporaryHandleCapability` | `CovenantDigest PhysicalIdentityDigest`, checked `long Length`, and `ReadAllBoundedAsync(int maximumBytes, CancellationToken) -> ValueTask<Result<MarkerCodecBytesLease>>` |
| `MarkerTemporaryHandleCapability` | `WriteAllAsync(ReadOnlyMemory<byte> exactCodecBytes, CancellationToken) -> ValueTask<Result>` |
| `MarkerTemporaryHandleCapability` | `FlushToDiskAsync(CancellationToken) -> ValueTask<Result>` |
| `MarkerRootCapability` | `RenameTemporaryToMarkerNoReplaceAsync(MarkerTemporaryHandleCapability temporary, CovenantDigest expectedTemporaryPhysicalIdentityDigest, ReadOnlyMemory<byte> expectedExactCodecBytes, CancellationToken) -> ValueTask<Result>` |
| `MarkerRootCapability` | `CompareDeleteTemporaryAsync(MarkerTemporaryHandleCapability temporary, CovenantDigest expectedTemporaryPhysicalIdentityDigest, ReadOnlyMemory<byte> expectedExactCodecBytes, CancellationToken) -> ValueTask<Result>` |
| `MarkerRootCapability` | `OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken) -> ValueTask<Result<PhysicalCampaignMarkerOpenResult>>` |
| `MarkerHandleCapability` | `CovenantDigest PhysicalIdentityDigest`, checked `long Length`, and `ReadAllBoundedAsync(int maximumBytes, CancellationToken) -> ValueTask<Result<MarkerCodecBytesLease>>` |
| `MarkerCodecBytesLease` | bounded `ReadOnlyMemory<byte> Bytes` plus one-shot `void Dispose()` that clears its exact private backing array with `CryptographicOperations.ZeroMemory` before release |
| `MarkerRootCapability` | `CompareDeleteMarkerAsync(MarkerHandleCapability marker, CovenantDigest expectedMarkerPhysicalIdentityDigest, ReadOnlyMemory<byte> expectedExactCodecBytes, CancellationToken) -> ValueTask<Result>` |
| `MarkerRootCapability` | `RenameMarkerToQuarantineNoReplaceAsync(MarkerHandleCapability marker, string quarantineLeaf, CovenantDigest expectedMarkerPhysicalIdentityDigest, ReadOnlyMemory<byte> expectedExactCodecBytes, CancellationToken) -> ValueTask<Result>` |
| `MarkerRootCapability` | `FlushMarkerDirectoryAsync(CancellationToken) -> ValueTask<Result>` |
| all three capabilities | one-shot `ValueTask DisposeAsync()` |

The only open result is the closed nonserializable union:

```csharp
internal abstract record PhysicalCampaignMarkerOpenResult
{
    private PhysicalCampaignMarkerOpenResult()
    {
    }

    internal sealed record Absent : PhysicalCampaignMarkerOpenResult;

    internal sealed record Opened(
        PhysicalCampaignRootOpener.MarkerHandleCapability Marker)
        : PhysicalCampaignMarkerOpenResult;
}
```

`CanonicalCampaignContext` includes the immutable binding, Campaign availability generation, optional path revision, optional opaque root identity, and physical containment policy. Path revision and root identity are both absent or both present, and `GlobalOnly` carries neither. It contains no authored path string as authority.

`OpenForMarkerLifecycleAsync` rejects an empty Campaign ID, nonpositive path revision, malformed digest, noncanonical or over-limit display path, and every root, `.arcanum` directory, marker, containment, owner, mode or ACL, physical-tuple, or HMAC mismatch before returning a capability. The nonserializable root capability retains the exact root and `.arcanum` handles used for those checks. After that initial open, no marker operation accepts a root path or display path. The fixed marker leaf remains private to the producer. Temporary and quarantine leaves are bounded random single path segments with no separator, traversal, alternate stream, reserved name, or normalization ambiguity.

Create uses exclusive no-follow semantics. Recovery opens only the journaled temporary leaf relative to the retained `.arcanum` handle. An opened temporary capability exposes checked identity, length, and a bounded zeroizable read so recovery can compare the journaled exact bytes and physical identity on that same handle before adopting, renaming, or removing it. Rename, quarantine, marker compare-delete, and temporary compare-delete verify by private root-ownership identity that the supplied child capability belongs to the exact same retained root and `.arcanum` pair. They also recheck the same opened child handle, expected physical identity, and fixed-time exact codec bytes before mutation. `RenameTemporaryToMarkerNoReplaceAsync` rejects a merely opened or mutated temporary file because its expected identity and bytes are mandatory. `CompareDeleteTemporaryAsync` is the sole compensation and pre-rename-abort cleanup operation, and the caller separately flushes the marker directory before committing that cleanup phase. A leaf, path, digest, or byte vector alone grants no effect. Parent-directory flush remains a separate operation so Task 7 can commit its corresponding phase only after the durability barrier succeeds.

Every root, temporary, and marker capability uses `Interlocked.Exchange` for one-shot disposal, rejects every operation after disposal or transfer, and never exposes a `SafeHandle`, file descriptor, `FileStream`, OS adapter, parent path, target path, or generic filesystem callback. There is no public constructor, serializer registration, implementation interface, cast target, raw-handle accessor, or path-derived fallback. Every temporary or marker read is bounded by the codec maximum and returns one `MarkerCodecBytesLease`. The lifecycle uses `Bytes` only before disposal and disposes the lease in `finally`; disposal clears the exact backing buffer even on parse, comparison, cancellation, or mutation failure. Plan 04's sole private authority factory consumes only `PhysicalCampaignRootOpener.MarkerRootCapability` from this sealed producer.

Ordinary `OpenForMarkerLifecycleAsync` continues to reject marker HMAC, codec-byte, ownership, and physical-identity mismatch before granting lifecycle authority. Plan 04 Task 7 alone adds `PhysicalCampaignRootOpener.CampaignPathTakeover.cs`, which completes the sealed partial producer with `OpenForConfirmedOrphanTakeoverAsync(CampaignPathIdentityAdministration.ConfirmedOrphanTakeoverAuthority authority, CancellationToken cancellationToken) -> ValueTask<Result<PhysicalCampaignRootOpener.ConfirmedOrphanTakeoverOpen>>`. `ConfirmedOrphanTakeoverOpen` is an `internal sealed` nonserializable `IAsyncDisposable` aggregate with no public or default constructor. It owns the retained root capability and already-opened conflicting marker capability, exposes only their content-free identity echoes, and provides one internal one-shot `Transfer(out MarkerHandleCapability marker) -> MarkerRootCapability` used solely by Task 7's private authority factory. `DisposeAsync` uses `Interlocked.Exchange`, disposes the marker before the root, and is idempotent. A failed open, identity mismatch, cancellation, exception, or untransferred result disposes the aggregate in `finally`; after transfer the Task 7 aggregate becomes the sole owner. The authority argument is Task 7's nonserializable, single-operation proof produced only after durable orphan evidence is still current, no active Campaign owns the root, the operator-confirmed effect digest matches, and the apply token is authenticated. This entry point may relax only the valid-marker-HMAC requirement. It still proves no-follow containment, exact prepared root identity, owner, mode or ACL, expected orphan marker physical identity, and expected exact observed bytes from the same handles. Quarantine then accepts only that returned marker capability plus those exact expected values, followed by explicit directory flush. The ordinary opener never accepts this mismatch, and neither entry point accepts raw evidence, a path, or a confirmation boolean as takeover authority.

Exact test methods:

- `ResolveAsync_ExistingCampaignSessionAcceptsMatchingSources`
- `ResolveAsync_ExistingCampaignSessionRejectsDifferentExplicitCampaign`
- `ResolveAsync_ExistingGlobalOnlySessionRejectsCampaignSources`
- `ResolveAsync_LegacyUnresolvedSessionFailsBindingRequired`
- `ResolveAsync_NoSessionUsesExplicitCampaign`
- `ResolveAsync_NoSessionUsesMostSpecificRegisteredWorkspace`
- `ResolveAsync_ExplicitCampaignRejectsUnregisteredSuppliedWorkspace`
- `ResolveAsync_UnknownSessionFailsWithoutCreatingSession`
- `PathReader_UsesIdentityKeyIndexAndBoundedAncestorCandidates`
- `RootOpener_RejectsSymlinkReparseMountMarkerAndIdentitySwaps`
- `RootOpener_MarkerLifecycleCapabilityIsProducerOwnedSealedAndNonforgeable`
- `RootOpener_RetainedCapabilityRunsCreateWriteFsyncRenameReopenAndCompareDeleteWithoutPathReopen`
- `RootOpener_RecoveryReadsAndVerifiesTheSameTemporaryHandleBeforeRename`
- `RootOpener_RecoveryRejectsMutatedPreexistingTemporaryAndLeavesItUntouched`
- `RootOpener_CompareDeletesOnlyTheSameVerifiedTemporaryThenFlushesTheParent`
- `RootOpener_TemporaryMismatchRemainsUntouchedDuringAbortCleanup`
- `RootOpener_MarkerCodecBytesLeaseZerosItsExactBackingBufferOnDispose`
- `RootOpener_RejectsWrongRootTemporaryTransferAndWrongRootMarkerDelete`
- `RootOpener_QuarantineRequiresTheSameOpenedMarkerIdentityAndExactBytes`
- `RootOpener_ConfirmedOrphanTakeoverRequiresDurableProofAndTheExactObservedHandle`
- `RootOpener_OrdinaryLifecycleOpenStillRejectsTheConfirmedOrphanMismatch`
- `RootOpener_UntransferredTakeoverAggregateDisposesMarkerThenRootExactlyOnce`
- `RootOpener_TransferredTakeoverAggregateCannotTransferOrDisposeChildrenTwice`
- `RootOpener_EveryCapabilityRejectsUseAfterDisposeAndDisposesOnce`
- `RootOpener_ExportsNoRawHandlePathInterfaceOrDowncastSurface`
- `MarkerCodec_EncodesParsesAndVerifiesOneCanonicalPayload`
- `MarkerCodec_RejectsWrongHmacPhysicalTupleOwnerModeAndTrailingBytes`
- `RootIdentityKey_IsInstallationStableSeparateAndRotatedOnlyByFullReset`

- [ ] **Step 1: Encode every truth-table row as failing theory data**

```csharp
[Theory]
[MemberData(nameof(CanonicalCampaignCases))]
public async Task ResolveAsync_ImplementsFillAndVerifyTable(
    CanonicalCampaignResolutionCase testCase)
{
    Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
        testCase.Request,
        CancellationToken.None);

    testCase.Assert(result);
}
```

Include separate physical identity tests for most-specific nested root, sibling-prefix rejection, supplied unregistered path conflict, path revision capture, delete and recreate, move, mount replacement, symlink or reparse swap, and marker replay. Fake the OS handle adapter in resolver policy tests, then run the repository and root-opener tests against real temporary roots.

- [ ] **Step 2: Run the focused resolver suite and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CanonicalCampaignContextResolverTests|FullyQualifiedName~SessionCampaignBindingReaderTests|FullyQualifiedName~CampaignPathIdentityReaderTests|FullyQualifiedName~PhysicalCampaignRootOpenerTests|FullyQualifiedName~CampaignPathMarkerCodecTests|FullyQualifiedName~CampaignRootIdentityKeyProviderTests"
```

Expected: FAIL because the canonical resolver types are absent and `PingRequestResolver.ResolveCampaignAsync` ignores conflicting sources.

- [ ] **Step 3: Implement the fill-and-verify resolver**

Create a dedicated `CampaignRootIdentityV1` OS credential identity and lazily generate one random 256-bit installation secret under the host lock. Domain-separate it from API, envelope, cursor, and diagnostic keys. API-key rotation and Covenant reset preserve it; full installation reset deletes and regenerates it. Key loss returns every Campaign identity as unresolved until authenticated repair. `CampaignPathMarkerCodec` is the only marker encoder/parser. It binds version, installation identity, Campaign ID, random marker payload, same-handle physical tuple, and owner/mode policy into one bounded canonical payload plus HMAC. Both the opener here and the Plan 04 path-registration writer consume this codec, so read and write validation cannot diverge.

Read the exactly-one Session binding and every supplied identifier, resolve the physical working directory through the sealed `PhysicalCampaignRootOpener` and the indexed path-identity reader, then apply the approved table. The ordinary Core resolver port remains `ICanonicalCampaignContextResolver`; no marker-root capability crosses into Core. The opener uses no-follow relative operations, verifies ownership, ACL or mode, same-volume containment, reserved marker bytes, physical tuple, and installation-authenticated marker digest from the retained handles. Ordinary resolution disposes its handles before returning content-free context. Marker lifecycle transfers them only through the producer-owned capability above. Return `Session.CampaignBindingRequired` for legacy unresolved, a typed conflict for any mismatch, and a Global-only context only when no source establishes a Campaign.

```csharp
public async ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
    CanonicalCampaignResolutionRequest request,
    CancellationToken cancellationToken)
{
    Result<SessionCampaignBinding?> session = await bindings.FindAsync(
        request.SessionId,
        cancellationToken);

    if (!session.IsSuccess)
    {
        return session.Error;
    }

    Result<RegisteredCampaignIdentity?> workspace = await paths.ResolveMostSpecificAsync(
        request.WorkingDirectory,
        cancellationToken);

    return CanonicalCampaignResolutionPolicy.Resolve(
        session.Value,
        request.ExplicitCampaignId,
        workspace.Value);
}
```

- [ ] **Step 4: Run the focused resolver suite to verify green**

Run the Step 2 command.

Expected: PASS for every truth-table and physical-identity case.

- [ ] **Step 5: Refactor all inference wrappers onto the one resolver**

Remove later Campaign resolution from `WizardIntelligenceProvider.BuildTurnContextAsync` and replace all `PingRequestResolver` calls. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CanonicalCampaignContextResolverTests|FullyQualifiedName~SessionCampaignBindingReaderTests|FullyQualifiedName~CampaignPathIdentityReaderTests|FullyQualifiedName~PhysicalCampaignRootOpenerTests|FullyQualifiedName~CampaignPathMarkerCodecTests|FullyQualifiedName~CampaignRootIdentityKeyProviderTests|FullyQualifiedName~PromptExecuteFlowTests|FullyQualifiedName~SpellExecution|FullyQualifiedName~WizardIntelligenceProviderTests"
```

Expected: PASS. No production inference code resolves Campaign scope from `PingRequest.WorkingDirectory` after invocation-context construction.

## Task 5: Make Session creation and assistant begin honor canonical binding

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionTurnBeginStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/AssistantReplyBeginReceipt.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionTurnInputPreflight.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireTurnWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnDisconnectAndIdempotencyCharacterizationTests.cs`

**Interfaces:**

- Consumes: `CanonicalCampaignContext` from Task 4; the Plan 01 pending-claim, immutable-binding, and future-finalization reservation schema; and Plan 02's caller-owned `CovenantMutationTransaction` plus exact `CovenantQuotaGuard.ConsumeReservedFinalizationAsync`. This slice accepts a required internal claim ID; Task 12 later wires durable public claim acquisition before this method can be reached.
- Produces the narrow `ISessionTurnBeginStore` port implemented by `GrimoireRepository`; the broad legacy `IGrimoireRepository` remains unchanged so unrelated repository fakes do not acquire mandatory claim methods. The port exposes:

```csharp
Task<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
    Guid existingSessionId,
    CanonicalCampaignContext campaign,
    Guid pendingClaimId,
    string prompt,
    string model,
        CancellationToken cancellationToken);
```

Exact test methods:

- `BeginAssistantReplyAsync_MissingSessionReturnsTypedFailure`
- `BeginAssistantReplyAsync_CampaignDeletedBeforeBeginCreatesNoEntries`
- `BeginAssistantReplyAsync_BindingMismatchCreatesNoEntries`
- `BeginAssistantReplyAsync_RevalidatesAndEchoesFrozenPreRequestRevisions`
- `BeginAssistantReplyAsync_ReturnsContentFreeSensitivityPreflightWithoutHistoryBytes`
- `BeginAssistantReplyAsync_ConsumesReservationAndInsertsExactFutureAssistantEntryIdentity`
- `BeginAssistantReplyAsync_RetryCannotSubstituteAssistantEntryIdentity`
- `GrimoireTurnWriter_DoesNotDowngradeBeginFailureToHandleFreeTurn`

- [ ] **Step 1: Add the failing begin and creation tests**

```csharp
[Fact]
public async Task GrimoireTurnWriter_DoesNotDowngradeBeginFailureToHandleFreeTurn()
{
    repository.BeginResult = Result<AssistantReplyBeginReceipt>.Failure(
        new Error(
            ErrorCodes.Session.NotFound,
            "Session not found."));

    Result<TurnHandle> result = await writer.BeginAsync(
        request,
        canonicalCampaign,
        pendingClaim,
        CancellationToken.None);

    Assert.False(result.IsSuccess);
    Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);
}
```

- [ ] **Step 2: Run the focused tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireTurnWriterTests|FullyQualifiedName~TurnDisconnectAndIdempotencyCharacterizationTests"
```

Expected: FAIL because `BeginAssistantReplyAsync` creates a missing Session and `GrimoireTurnWriter` catches begin failures into an empty handle.

- [ ] **Step 3: Split atomic Session creation from assistant begin**

Make assistant begin require an existing Session, canonical binding, and existing pending-claim row. Task 12 is the sole owner of atomic new Session plus claim insertion; this port cannot create a Session. Recheck Campaign existence, claim identity, binding, the claim's immutable pre-request history revision, and its latest expected-current sensitivity revision inside the begin transaction. Read the claim's immutable reservation and future assistant Entry identities, call Plan 02 `CovenantQuotaGuard.ConsumeReservedFinalizationAsync(identity, transaction, cancellationToken)` with the same live caller-owned `CovenantMutationTransaction`, then insert only the user Entry and an assistant placeholder using that exact preallocated assistant Entry ID. A retry resolves the same consumed reservation and exact placeholder; no begin request, adoption path, or repository helper may generate or substitute another assistant ID. Failure rolls back the reservation transition and both Entries together. `AssistantReplyBeginReceipt` is the Core-owned record carrying Session, claim, reservation, user Entry, and exact assistant Entry IDs plus `SessionTurnInputPreflight`. The preflight contains only Session ID, immutable binding, revalidated pre-request history revision, latest expected-current sensitivity revision, tainted-artifact count, maximum sensitivity, bounded provenance digest, and producing-evidence digest. It contains no title, summary, Entry, attachment, tool, or exact label content. Those echoed values remain the Task 15 comparison point even though inserting the new rows advances the live history revision. Inject this focused port into `GrimoireTurnWriter`; do not add these methods to `IGrimoireRepository`. Focused tests seed the pending row and matching future-ID reservation directly through the SQLCipher fixture; Task 12 replaces that fixture setup with the production claim coordinator.

- [ ] **Step 4: Run the focused tests to verify green**

Run the Step 2 command.

Expected: PASS, including zero Entry and finalization side effects for every failed begin.

- [ ] **Step 5: Refactor `TurnHandle` into required IDs**

Remove nullable IDs and the handle-free success state. Keep interruption state explicit. Rerun the Step 2 command.

Expected: PASS, with every successful `TurnHandle` bound to one existing Session, user Entry, assistant Entry, and pending claim.

## Task 6: Build one attributed system-prompt string

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/SystemPromptBuildResult.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/SystemPromptAttributionSpan.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/PromptCacheContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/SystemPromptBuilder.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/SystemPromptDocument.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderResonanceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderUntrustedFenceTests.cs`

**Interfaces:**

- Consumes: Plan 02's linked Confirmed and Proposed section bytes and fragment hashes.
- Produces:

```csharp
public sealed record SystemPromptBuildResult(
    string Text,
    IReadOnlyList<SystemPromptAttributionSpan> AttributionSpans,
    IReadOnlyList<PromptCacheSegmentDescriptor> CacheSegments);

public readonly record struct SystemPromptAttributionSpan(
    PromptAttributionKind Kind,
    int Utf16Start,
    int Utf16Length,
    CovenantDigest SegmentDigest);
```

Exact test methods:

- `Build_WithoutCovenant_IsByteIdenticalToCharacterizationFixture`
- `Build_ConfirmedOrdersGlobalThenCampaignAfterWorkspaceBeforeCodex`
- `Build_ProposedAppearsAfterDataHeaderBeforeLexicon`
- `Build_ProposedUsesCompilerFenceWithoutReparsing`
- `Build_AttributionSpansPartitionRenderedText`
- `Build_CacheDescriptorsReferenceTheSameFinalString`

- [ ] **Step 1: Add byte, ordering, span, and cache failures**

Capture the current absent-Covenant bytes and cache descriptors as literal expectations before changing `SystemPromptBuilder`.

```csharp
[Fact]
public void Build_AttributionSpansPartitionRenderedText()
{
    SystemPromptBuildResult result = builder.Build(requestWithCovenant);

    Assert.Equal(0, result.AttributionSpans[0].Utf16Start);
    Assert.Equal(
        result.Text.Length,
        result.AttributionSpans.Sum(span => span.Utf16Length));
    AssertSpansAreOrderedAndNonOverlapping(result.AttributionSpans);
}
```

- [ ] **Step 2: Run the prompt suites and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SystemPromptBuilderTests|FullyQualifiedName~SystemPromptBuilderResonanceTests|FullyQualifiedName~SystemPromptBuilderUntrustedFenceTests"
```

Expected: FAIL because `Build` returns a string or duplicated segment document and has no Covenant attribution spans.

- [ ] **Step 3: Render once and record spans against that string**

Keep the existing prompt byte sequence for an absent plan. Add Proposed immediately after the existing DATA header and before Lexicon. Add Global Confirmed then Campaign Confirmed after Workspace and before Codex. Emit only nonempty Covenant headings. Build spans and cache descriptors from start/length indexes into the final `StringBuilder` output.

- [ ] **Step 4: Run the prompt suites to verify green**

Run the Step 2 command.

Expected: PASS for exact absent bytes, DATA `[None]` preservation, ordering, adaptive fence, and partition coverage.

- [ ] **Step 5: Refactor `SystemPromptDocument` into a compatibility wrapper**

Remove stored per-segment string copies. If a temporary wrapper is needed by callers, make it expose the same `SystemPromptBuildResult`. Rerun the Step 2 command.

Expected: PASS, and each descriptor references one range in `SystemPromptBuildResult.Text`.

## Task 7: Attribute tokens once and suppress explicit Covenant caching

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ModelTokenizationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/PromptCacheContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/PromptCachePlanner.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ModelTokenEstimatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/PromptCachePlannerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ManaCountingTests.cs`

**Interfaces:**

- Consumes: `SystemPromptBuildResult` from Task 6.
- Produces: pinned `ContextTokenSource.CovenantConfirmed`, `ContextTokenSource.CovenantProposed`, and `ContextTokenSource.SpecialOrUncovered` values plus span-based cache boundaries.

Exact test methods:

- `EstimateSystemMessage_CallsEncodeToTokensExactlyOnce`
- `EstimateSystemMessage_AssignsBoundaryCrossingTokenByFirstCodeUnit`
- `EstimateSystemMessage_AssignsZeroLengthAndUncoveredTokensToSpecial`
- `EstimateSystemMessage_CategoryCountsEqualWholePromptTotal`
- `ContextTokenSource_SpecialOrUncoveredHasPinnedCodeAndSourceGeneratedJsonRoundTrip`
- `Create_CovenantDescriptorsAreExplicitCacheIneligible`
- `Create_WithoutCovenant_PreservesExistingCachePlan`

- [ ] **Step 1: Add the failing tokenizer-spy and cache tests**

```csharp
[Fact]
public void EstimateSystemMessage_CallsEncodeToTokensExactlyOnce()
{
    CountingTokenizer tokenizer = new(tokens);

    ContextTokenBreakdown result = estimator.EstimateSystemMessage(
        promptResult,
        tokenizer);

    Assert.Equal(1, tokenizer.EncodeToTokensCallCount);
    Assert.Equal(result.Total, result.BySource.Values.Sum());
}
```

- [ ] **Step 2: Run the token and cache suites and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ModelTokenEstimatorTests|FullyQualifiedName~PromptCachePlannerTests|FullyQualifiedName~ManaCountingTests"
```

Expected: FAIL because `ModelTokenEstimator.EstimateSystemMessage` reparses headings and tokenizes fragments separately, and `PromptCachePlanner.Create` hashes copied segment strings.

- [ ] **Step 3: Classify one whole-prompt token sequence by UTF-16 offsets**

Call `EncodeToTokens` once over `SystemPromptBuildResult.Text`. Assign a nonempty token to the span containing its first covered UTF-16 code unit. Assign zero-length, out-of-range, and uncovered offsets to `SpecialOrUncovered`. Hash cache descriptor spans directly from the same string and mark both Covenant kinds sensitive and explicit-cache-ineligible.

- [ ] **Step 4: Run the token and cache suites to verify green**

Run the Step 2 command.

Expected: PASS, with source counts summing to the single local whole-prompt total.

- [ ] **Step 5: Remove heading and fence classification helpers**

Delete `ClassifySystemHeading` and any Covenant-specific Markdown parser. Keep fallback/provider usage as whole-call observations. Rerun the Step 2 command.

Expected: PASS, including heading-lookalike and info-string fence cases.

## Task 8: Admit Confirmed all-or-fail and Proposed by longest prefix

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantAdmissionPlanner.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/PreliminaryCovenantAdmission.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderAdapterPolicy.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderAdmissionContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderAdmissionPayloadProjection.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderAdmissionMaterializationCandidate.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderBudgetOptionProjection.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderAdmissionBudgetEstimator.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderBudgetOptionProjector.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderSemanticBudgetProjection.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ProviderBudget.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/IProviderAdmissionBudgetEstimator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ContextMaterializationLedger.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ModelTokenizationContracts.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantAdmissionPlannerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderAdmissionBudgetEstimatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderBudgetOptionProjectorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ContextMaterializationLedgerTests.cs`

**Interfaces:**

- Consumes: `CovenantTurnPlan` from Plan 02, the resolved provider/model/context-window facts, the call-scoped immutable materialization candidate vector, and `SystemPromptBuilder` from Task 6.
- Produces:

```csharp
public interface ICovenantAdmissionPlanner
{
    Result<PreliminaryCovenantAdmission> BuildPreliminary(
        CovenantTurnPlan plan,
        ProviderAdmissionContext context);
}
```

`ProviderAdmissionContext` binds immutable provider and model identity, context-window size, Core `ProviderAdapterPolicy`, tokenizer-profile identity, reserved output and reasoning tokens, `ProviderBudgetOptionProjection`, and `ProviderAdmissionPayloadProjection`. The payload projection deep-copies ordered ordinary messages, content parts, tools, and Core `ProviderAdmissionMaterializationCandidate` values before admission. It contains no mutable SDK object or Api type. `ProviderBudgetOptionProjector` runs before admission and freezes every sensitivity-independent provider-visible option, including response format, canonical JSON-schema bytes and strictness, ordered stops, tool choice and parallel policy, reasoning fields, user identity, penalties, seed, and output limits. Its digest is later verified by Task 9 before sensitivity-dependent cache suppression. `ProviderBudget` is the checked result for one trial projection, not an ambient integer. `IProviderAdmissionBudgetEstimator` owns tokenizer resolution and builds the exact `ProviderSemanticBudgetProjection` for each trial full prompt. That projection includes Familiar role labels, schema bytes, dialect arguments that consume context, and the Claude inline-system versus large-system folding rule. Task 10 consumes the same semantic projection when freezing the final call, so preliminary pressure and final estimator identity cannot diverge. No undefined tokenizer interface crosses the planner boundary.

`PreliminaryCovenantAdmission` binds the `ProviderBudgetOptionProjection` digest and returns the exact selected `ProviderAdmissionPayloadProjection`, its digest, and an ordered pressured-source identity vector in addition to the eligible Covenant decision vector. Ordinary RAG, Tapestry, attachment, or semantic eviction produces a new immutable selected payload rather than mutating the input ledger. The caller applies that selection once when building the final messages. Task 10 must reject a call whose ordered content parts or materialization occurrences differ from the admitted payload selection.

Exact test methods:

- `BuildPreliminary_ConfirmedNoFitReturnsContextBudgetExceeded`
- `BuildPreliminary_RemovesEveryProposedBeforeLaterTier`
- `BuildPreliminary_PreservesLongestCompleteProposedPrefix`
- `BuildPreliminary_IncludesHeadingFenceAndSeparatorCost`
- `BuildPreliminary_StopsAfterThirtyTwoBoundedRemovals`
- `Ledger_EvictionOrderDoesNotDependOnSourceEnumOrdinal`
- `BudgetEstimator_IncludesFamiliarMultiTurnRoleLabels`
- `BudgetEstimator_AccountsForInlineToFoldedSystemBoundary`
- `BudgetEstimator_BindsProviderModelContextAndTokenizerProfileIdentity`
- `BudgetOptions_MutationAfterProjectionCannotChangeAdmissionOrFinalFreeze`
- `BudgetOptions_StructuredSchemaBytesCrossingContextBoundaryAreCountedExactly`
- `BuildPreliminary_ReturnsExactSelectedPayloadAndPressuredOrdinarySourceVector`
- `AdmissionThenFreeze_ExhaustsProposedEvictsRagAndOmitsItFromSentCallAndLedger`

- [ ] **Step 1: Add failing pressure and eviction-order tests**

Use a deterministic fake tokenizer whose literal token counts include the full framed section. Assert every candidate receives `Admitted`, `Pressured`, or `RequiredNoFit`.

- [ ] **Step 2: Run the focused admission tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAdmissionPlannerTests|FullyQualifiedName~ProviderAdmissionBudgetEstimatorTests|FullyQualifiedName~ProviderBudgetOptionProjectorTests|FullyQualifiedName~ContextMaterializationLedgerTests"
```

Expected: FAIL because no Covenant planner exists and ledger eviction follows `ContextMaterializationSourceKind` ordinal.

- [ ] **Step 3: Implement bounded prefix admission**

Treat Proposed as the earliest eviction tier. For every trial, rebuild the complete system prompt and call `IProviderAdmissionBudgetEstimator` over that full provider-semantic projection plus the frozen ordinary messages, tools, and materialization candidates. Retokenize the exact Proposed section after each suffix removal, stop after at most 32 removals, preserve the longest complete plan-order prefix, and remove all Proposed before evicting any ordinary semantic or materialization tier. Confirmed is non-evictable within memory admission; after Proposed and every later evictable tier are exhausted, fail with `Hub.ContextBudgetExceeded` if all Confirmed still cannot fit.

- [ ] **Step 4: Run the focused admission tests to verify green**

Run the Step 2 command.

Expected: PASS for no-fit, longest prefix, exact framing cost, explicit eviction tier, and exact selected ordinary payload.

- [ ] **Step 5: Refactor pressure reasons into closed typed codes**

Remove free-form internal pressure branching and retain sanitized display text only at projection boundaries. Rerun the Step 2 command.

Expected: PASS with stable candidate ordering and reason codes.

## Task 9: Freeze provider options before hashing or adapter mapping

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderCallFreezer.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderCallSensitivityCalculator.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderAttemptEvidence.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ReasoningChatOptionsAdapter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/PromptCachingChatOptionsAdapter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAiRequestAugmentingHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatCompletionMapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiChatCompletionRequestValidator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderCallFreezerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderCallSensitivityCalculatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ModelCallExecutorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ReasoningChatOptionsAdapterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/PromptCachingChatOptionsAdapterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/RequestAugmentingHandlerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/OpenAi/OpenAiChatCompletionMapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/OpenAiV1EndpointTests.cs`

**Interfaces:**

- Consumes: Plan 02's canonical `FrozenProviderOptions`, `ProviderCallSensitivity`, max/merge sensitivity lattice, and provider-options and sensitivity encoders, plus Task 8's preliminary admission and `ProviderAdapterPolicy`.
- Produces:

```csharp
public interface IProviderCallFreezer
{
    Result<FrozenProviderOptions> FreezeOptions(
        ProviderBudgetOptionProjection options,
        ProviderAdapterPolicy adapterPolicy,
        ProviderCallSensitivity sensitivity);
}

public interface IProviderCallSensitivityCalculator
{
    Result<ProviderCallSensitivity> Calculate(
        ProviderAttemptEvidence evidence,
        IReadOnlyList<ArtifactSensitivityLabel> inputLabels);
}
```

`ProviderAttemptEvidence` is a closed nonserializable product of two orthogonal unions. `Admission` is `None` or `Current(PreliminaryCovenantAdmission)`. `SensitivityEvidence` is `None`, `InheritedHistory(ArtifactSensitivityLabel persistedSensitivity, CovenantDigest producingEvidenceDigest)`, or `CurrentMaterialized(CovenantDigest planDigest)`. Every `CovenantTurnAuthority.CurrentCovenant` physical attempt carries `Admission.Current`, including an empty selected-segment vector, because later proposal and retirement lineage binds the producing admission. `CurrentMaterialized` additionally requires at least one admitted Covenant occurrence. Admission alone does not make a call derived. `InheritedHistory` merges its persisted label and every immutable input label without inventing a current-materialization plan digest. An enabled clean zero-materialization call therefore uses `(Current, None)`, and a tainted zero-materialization call uses `(Current, InheritedHistory)`. Task 8's `ProviderAdapterPolicy` is an immutable, versioned Core value that contains only the resolved provider dialect and capabilities needed to validate and reconstruct frozen options and budget projections. No later task may infer it from mutable `AdditionalProperties`.

Exact test methods:

- `FreezeOptions_CanonicalizesNegativeZero`
- `FreezeOptions_PreservesStopOrderAndSortsLogitBiasByTokenId`
- `FreezeOptions_RejectsDuplicateLogitBiasToken`
- `FreezeOptions_RejectsNonFiniteFloat`
- `FreezeOptions_RejectsUnknownAdditionalProperty`
- `FreezeOptions_RejectsRawRepresentation`
- `FreezeOptions_FreezesReasoningDialectAndJsonSchemaStrict`
- `FreezeOptions_CoversEveryProviderVisibleOptionInThePolicyV1Matrix`
- `OpenAi_UserIsBoundedMappedAndHashed`
- `OpenAi_LogprobsAndTopLogprobsAreRejectedBeforeCovenantLoading`
- `FreezeOptions_MutationAfterFreezeCannotChangeAdaptedRequest`
- `FreezeOptions_BindsTask8BudgetOptionDigestAndRejectsAdmissionMismatch`
- `Calculate_UsesMaximumOfAdmissionMessagesSummaryToolsAndArtifacts`
- `Calculate_UnprotectedRejectsAnyDerivedInputLabel`
- `Calculate_PriorTaintWithoutCurrentPlanRemainsDerived`
- `Calculate_CurrentEmptyAdmissionHasProposalLineageWithoutDerivedSensitivity`
- `FreezeOptions_DerivedSensitivitySuppressesExplicitProviderCaching`

- [ ] **Step 1: Add failing frozen-option tests**

```csharp
[Fact]
public void FreezeOptions_MutationAfterFreezeCannotChangeAdaptedRequest()
{
    ChatOptions mutable = Fixtures.Options(maxOutputTokens: 512);
    ProviderBudgetOptionProjection budgetOptions =
        projector.Project(mutable, policy).Value;
    FrozenProviderOptions frozen =
        freezer.FreezeOptions(budgetOptions, policy, sensitivity).Value;

    mutable.MaxOutputTokens = 1;

    Assert.Equal(512, frozen.MaxOutputTokens);
    Assert.Equal(
        adapter.BuildWireOptions(frozen),
        adapter.BuildWireOptions(frozen));
}
```

- [ ] **Step 2: Run the focused freezer tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ProviderCallFreezerTests|FullyQualifiedName~ProviderCallSensitivityCalculatorTests|FullyQualifiedName~ModelCallExecutorTests|FullyQualifiedName~ReasoningChatOptionsAdapterTests|FullyQualifiedName~PromptCachingChatOptionsAdapterTests|FullyQualifiedName~RequestAugmentingHandlerTests|FullyQualifiedName~OpenAiChatCompletionMapperTests|FullyQualifiedName~OpenAiV1EndpointTests"
```

Expected: FAIL because adapters consume mutable `ChatOptions` and late `RawRepresentationFactory` or request-handler mutation can change provider-visible state.

- [ ] **Step 3: Calculate sensitivity, then project every allowed option into the immutable Core value**

Calculate one `ProviderCallSensitivity` before final option freezing by applying the exact input arm, taking its maximum with every immutable input artifact label, and merging bounded generation provenance through Plan 02. An unprotected input stays unprotected only when every input label is `None`. A protected-history input remains derived without a current plan or admission digest. A current-Covenant input incorporates its preliminary admission. Consume Task 8's immutable `ProviderBudgetOptionProjection`, verify its canonical digest against the preliminary admission context, and project max output tokens, temperature, top-p, frequency and presence penalties, seed, bounded end-user identity, ordered stops, tool choice and optional named tool, parallel-tool tri-state, response format, JSON-schema name/description/digest/strict tri-state, reasoning effort/budget/output/dialect, and sorted logit bias into the exact policy-v1 fields. The Task 8 projector already validates finite floating values, canonicalizes negative zero, retains stop order, sorts and deduplicates logit bias, canonicalizes response schema, and rejects unknown properties. Task 9 adds only sensitivity-dependent prompt-cache suppression and the sensitivity digest. `/v1` maps the bounded `user` field through this path. It rejects `logprobs` and `top_logprobs` as unsupported before Covenant loading until a complete response contract exists. Change `ReasoningChatOptionsAdapter`, `PromptCachingChatOptionsAdapter`, `WizardIntelligenceProvider`, and `ModelCallExecutor` in this same step so every production and test caller consumes only `FrozenProviderOptions` plus the same explicit `ProviderCallSensitivity`; no adapter may infer sensitivity from mutable messages or ambient state. Update both adapter test suites before the first green command because the filtered command compiles the complete test project.

- [ ] **Step 4: Run the focused freezer tests to verify green**

Run the Step 2 command.

Expected: PASS for every option vector and post-freeze mutation case.

- [ ] **Step 5: Remove late provider-visible request mutation**

Make `OpenAiRequestAugmentingHandler` observe or validate the frozen shape without adding `strict` or schema fields. Suppress explicit prompt-cache instructions when the required `ProviderCallSensitivity.Level` is Covenant-derived. Rerun the Step 2 command.

Expected: PASS, and no adapter-only property bypasses the frozen digest.

## Task 10: Freeze exact messages, tools, and materialization occurrences

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderCallMaterializationBuilder.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderCallDraft.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ResolvedProviderIdentity.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ResolvedProviderCallContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/FrozenProviderCall.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/FrozenFamiliarWireProjection.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/IResolvedProviderCallClient.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ResolvedProviderCallClient.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/Familiars/FrozenFamiliarProviderCallClient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderCallFreezer.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ContextMaterializationLedger.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/AttachmentMemoryProvenance.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ModelTokenizationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentToolAmbient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallPayloadFingerprint.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Familiars/FamiliarChatClient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Familiars/ClaudeCodeCliChatClient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Familiars/CodexCliChatClient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceContextBuilder.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatCompletionMapper.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderCallMaterializationSnapshotTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderCallFreezerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ModelTokenEstimatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ChatClientFactoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Familiars/FamiliarChatClientTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/OpenAi/OpenAiChatCompletionMapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/InferenceContextBuilderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ContextCompressionServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantContextPreviewTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Familiars/FamiliarTransportIntegrationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/ProviderApiKeyResolutionTests.cs`

**Interfaces:**

- Consumes: `SystemPromptBuildResult`, `FrozenProviderOptions`, the same Task 9 `ProviderCallSensitivity`, final ordered messages and tools, and Plan 02's materialization/ProviderCall encoders.
- Produces:

```csharp
public Result<FrozenProviderCall> Freeze(
    ProviderCallDraft draft,
    IReadOnlyList<ContextMaterializationCandidate> materializations);

public sealed record FrozenProviderCall(
    ProviderCallEnvelope Envelope,
    ProviderCallMaterializationSnapshot Materialization,
    CovenantDigest ProviderCallDigest);

public interface IModelTokenEstimator
{
    ContextTokenBreakdown EstimateFrozenContext(
        FrozenProviderCall call,
        ResolvedProviderCallContext context);
}
```

`ProviderCallDraft` is the immutable-at-entry API value containing the resolved `IChatClient`-independent system document, ordered messages, tools, `FrozenProviderOptions`, dispatch mode, adapter policy, `ProviderCallSensitivity`, and Task 8 preliminary admission's selected-payload and budget-option digests. Deep-copy mutable provider configuration into `ResolvedProviderIdentity`, including stable provider name and kind, dialect, endpoint or executable plus command identity, resolved model, context limit, and opaque destination digest. Extend the existing disposable `ChatClientLease` into immutable `ResolvedChatClientLease(IResolvedProviderCallClient DispatchClient, ProviderAdapterPolicy Policy, ResolvedProviderIdentity Provider, ...)`, retaining all current HTTP, Familiar process, and temporary-directory ownership. `IResolvedProviderCallClient` accepts only `FrozenProviderCall` plus the immutable resolved context and returns the existing typed buffered or streaming SDK outcome. `ChatClientFactory` resolves one closed SDK or Familiar implementation so policy, provider, model, dispatch client, and lifetime cannot diverge between freeze and dispatch. The SDK implementation reconstructs its provider request only from the frozen call. The Familiar implementation receives `FrozenFamiliarWireProjection` directly and never recomposes process bytes from mutable `ChatMessage` or `ChatOptions`. Raw `IChatClient` compatibility methods remain private to the adapter and are absent from the resolved lease and production dispatcher graph. The freezer requires the final ordered message/tool/materialization projection to equal Task 8's selected payload and requires `FrozenProviderOptions` to bind the same budget-option digest. It rejects any source reintroduced after pressure or any option mutation between admission and freeze. The frozen call binds its policy, provider-identity, selected-payload, and budget-option digests. This task consumes the repository's existing `ContextMaterializationCandidate` by its exact name and converts it once into Plan 02's provider-payload occurrence records.

Exact test methods:

- `Freeze_RecordsEverySystemAndMessagePartOccurrence`
- `Freeze_RecordsTextBinaryAndSourceRanges`
- `Freeze_OneSourceMayOwnMultipleOrderedOccurrences`
- `Freeze_MarksUnprovenancedMaterialization`
- `Freeze_RejectsMoreThanSixtyFourProposalSources`
- `Freeze_RetryExcludesSourceMissingFromRetryPayload`
- `Freeze_HashAndDispatchReadTheSameImmutableProjection`
- `Freeze_CoversEverySupportedRoleContentPartAndDispatchMode`
- `Freeze_BindsBinaryNameMediaTypeImageDetailAndExactBytes`
- `Freeze_BindsUriMediaTypeAndImageDetail`
- `Freeze_BindsTextReasoningProtectedData`
- `Freeze_BindsMessageIdOrRejectsItBeforeDispatch`
- `Freeze_BindsToolSchemasStrictnessAndCanonicalJson`
- `Freeze_RejectsUnknownMessageContentToolAndOptionProperties`
- `Adapter_SentBytesEqualTheFrozenProjectionForEverySupportedMatrixRow`
- `EstimatorHashAdapterAndDispatchConsumeTheSameFrozenProjection`
- `NoDispatchCacheOrAccountingCallerUsesMutableModelTokenizationRequest`
- `FamiliarSemanticProcessProjectionEqualsFrozenProjectionForEverySupportedCliDialect`
- `FamiliarProductionDispatchCannotRecomposeFromMutableSdkMessagesOrOptions`
- `FamiliarOpaquePathsEnvironmentScrubAndTimeoutAddNoProviderContent`
- `ChatClientFactoryResolvesClientAndMatchingProviderAdapterPolicyTogether`
- `ResolvedChatClientLeaseDisposesFamiliarProcessAndOwnedResourcesExactlyOnce`
- `MutationAfterProviderResolveCannotChangeIdentityProcessRequestOrFrozenDigest`
- `MutationAfterProviderResolveCannotChangeEstimatorCacheAccountingOrExecutorContext`
- `Freeze_RejectsPayloadOrOptionDigestThatDiffersFromPreliminaryAdmission`
- `ContextCompression_UsesResolvedImmutableIdentityAndCannotObserveProviderSettingsMutation`

- [ ] **Step 1: Add failing exact-occurrence tests**

```csharp
[Fact]
public void Freeze_RetryExcludesSourceMissingFromRetryPayload()
{
    FrozenProviderCall first = freezer.Freeze(firstDraft, sources).Value;
    FrozenProviderCall retry = freezer.Freeze(retryDraftWithoutAttachment, sources).Value;

    Assert.Single(first.Materialization.Sources);
    Assert.Empty(retry.Materialization.Sources);
}
```

- [ ] **Step 2: Run the focused snapshot tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ProviderCallMaterializationSnapshotTests|FullyQualifiedName~ProviderCallFreezerTests|FullyQualifiedName~ModelTokenEstimatorTests|FullyQualifiedName~ChatClientFactoryTests|FullyQualifiedName~FamiliarChatClientTests|FullyQualifiedName~FamiliarTransportIntegrationTests|FullyQualifiedName~ProviderApiKeyResolutionTests|FullyQualifiedName~OpenAiChatCompletionMapperTests|FullyQualifiedName~InferenceContextBuilderTests|FullyQualifiedName~ContextCompressionServiceTests|FullyQualifiedName~WizardIntelligenceProviderTests|FullyQualifiedName~WizardIntelligenceProviderFallbackTests|FullyQualifiedName~CovenantContextPreviewTests"
```

Expected: FAIL because current provenance is turn-cumulative and `ModelCallPayloadFingerprint` does not bind the complete provider-visible call.

- [ ] **Step 3: Freeze one ordered provider-neutral projection**

Clone supported `ChatMessage` and `AIContent` values into immutable Core records. The closed matrix is System/User/Assistant/Tool roles; Buffered/Streaming dispatch; Text, Binary, ToolCall, ToolResult, Json, Uri, and TextReasoning parts; binary bytes/name/media type/image detail; URI/media type/image detail; reasoning text/protected data; exact tool-call identity/name/canonical arguments; result identity/canonical content; optional allowed message ID; ordered tool name/description/input/output schema digests and strictness; and optional structured-output schema digest. Record exact system/message-part coordinates and source ranges. Reject any unlisted `AdditionalProperties`, `RawRepresentation`, content kind, or provider-visible property before digest or dispatch. Core owns `FrozenProviderCall`, `ResolvedProviderIdentity`, and `ResolvedProviderCallContext`, so `IModelCallExecutor` never references an Api type. Replace the mutable `ProviderSettings` field in `ModelCallContext` with the immutable resolved context and frozen provider identity, tokenizer-profile, context-limit, reserve, cache-plan, and precomputed-breakdown facts. Change `InferenceContextBuilder` and `ContextCompressionService` in this slice so `ContextCompressionRequest` borrows the resolved lease and consumes only that immutable identity/context, never `ChatClientLease.Provider` or mutable settings. Compute materialization and provider-call digests from that projection, then reconstruct every estimator, cache/accounting observer, compression attempt, and adapter request only from the same frozen records. Change `ModelTokenEstimator` in this slice so it never calls `ModelCallPayloadFingerprint` on mutable messages or options. Capture the final adapter request at the transport seam and assert its exact ordered wire bytes or typed provider request projection match the frozen matrix, including streaming mode, image detail, reasoning protected data, schema strictness, and every option from Task 9. Keep `AttachmentMemoryGateAmbient` only as non-authoritative compatibility state until its callers are removed.

For Familiar CLI providers, freeze a semantic wire projection containing prompt or stdin bytes, structured-output schema bytes and digest, resolved model, dialect arguments, and binary identity. `FrozenFamiliarProviderCallClient` and the prompt composer accept only this projection and the resolved lease identity. The existing `FamiliarChatClient` compatibility surface cannot be resolved or invoked by production dispatch. The frozen client may add an operation-owned random working directory or schema-file path, the closed environment-deny list, and timeout as non-content launch metadata. Bind the deterministic launch-template digest, compare every semantic process field exactly, and separately assert that opaque local paths, environment scrub, and timeout add no model-visible content or unbound argument. Do not require random local path bytes to equal the frozen provider call.

- [ ] **Step 4: Run the focused snapshot tests to verify green**

Run the Step 2 command.

Expected: PASS for exact call-scoped occurrences, unprovenanced detection, and mutation-after-freeze.

- [ ] **Step 5: Refactor fingerprinting and provenance authority**

Route `ModelCallPayloadFingerprint` through Plan 02's canonical encoder or remove it when no caller remains. Add `EstimateFrozenContext(FrozenProviderCall, ResolvedProviderCallContext)` to the Core estimator contract and migrate executor, cache, cost, and accounting paths to it. Restrict the legacy mutable `ModelTokenizationRequest` overload to explicitly pre-freeze preview code or remove it when no caller remains; an architecture test rejects it from any dispatch, cache, or accounting call graph. Delete proposal-authority decisions based on `AttachmentMemoryGateAmbient.Snapshot`. Rerun the Step 2 command.

Expected: PASS, with one provider-call digest implementation.

## Task 11: Finalize admission and acknowledge provider disclosure before dispatch

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderAttemptDispatcher.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderAttemptContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantDisclosureTransactionStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/DisclosureGroupCommitter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantDisclosureCompactor.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICovenantDisclosureWriterLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IModelCallExecutor.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ModelCallPurpose.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticRouter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/LexiconEntityExtractor.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/ITurnEventSource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderAttemptDispatcherTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ModelCallExecutorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ProviderAttemptCommitTrackerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantDisclosureStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantDisclosureCompactionTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/DisclosureGroupCommitterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/RequestAugmentingHandlerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SemanticRouterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/LexiconEntityExtractorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ModelTokenEstimatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: preliminary admission from Task 8, frozen call from Task 10, Plan 01 disclosure tables and `ICovenantSqliteConnectionFactory`, plus Plan 02's admission, disclosure, subject-aggregate, and rolling-chain encoders.
- Produces the internal transaction helper `ICovenantDisclosureTransactionStore`, the bounded single-writer `DisclosureGroupCommitter`, `ICovenantDisclosureWriterLifecycle`, open-subject compaction, and:

```csharp
public interface IProviderAttemptDispatcher
{
    Task<Result<ProviderAttemptResult>> DispatchBufferedAsync(
        ResolvedChatClientLease clientLease,
        ProviderAttemptDispatchRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<ProviderAttemptStream>> OpenStreamingAsync(
        ResolvedChatClientLease clientLease,
        ProviderAttemptDispatchRequest request,
        CancellationToken cancellationToken);
}

Task<ModelCallOutcome> ExecuteBufferedAsync(
    IResolvedProviderCallClient client,
    FrozenProviderCall call,
    ITurnBudget budget,
    ModelCallPurpose purpose,
    ModelCallContext context,
    CancellationToken cancellationToken);

IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(
    IResolvedProviderCallClient client,
    FrozenProviderCall call,
    ITurnBudget budget,
    ModelCallPurpose purpose,
    ModelCallContext context,
    CancellationToken cancellationToken);
```

Task 11 consumes Task 9's `ProviderAttemptEvidence`. It finalizes an admission receipt only for `Admission.Current`. The validation matrix is exact: `Unprotected` authority accepts only `(None, None)`; `ProtectedHistory` authority accepts only `(None, InheritedHistory)`; `CurrentCovenant` authority requires current admission and accepts `(Current, None)`, `(Current, InheritedHistory)`, or `(Current, CurrentMaterialized)`. Every `CurrentCovenant` case revalidates its turn lease before SDK invocation. Only derived sensitivity queues a disclosure receipt. Every other combination fails before provider or disclosure work.

`ProviderAttemptDispatchRequest` is one immutable, nonserializable attempt value containing `ProviderAttemptEvidence`, `FrozenProviderCall`, `ProviderAttemptIdentity`, `CovenantTurnAuthority`, the exact existing `ITurnBudget`, `ModelCallPurpose`, and immutable `ModelCallContext`. The current `ITurnBudget` is an empty compatibility marker, so this slice accepts only reference-identical `UnrestrictedTurnBudget.Instance` and rejects every other implementation before dispatch. It does not claim a nonexistent budget digest. The constructor validates resolved provider/model/tokenizer facts, cache plan, sensitivity, and call digest against the frozen call. The dispatcher forwards purpose and the supported singleton budget without a default for buffered and streaming calls. Extend `ModelCallPurpose` with explicit Summary, Title, and Saga maintenance values while retaining the existing Lexicon extraction value; Task 23 maps each maintenance step to its exact purpose.

`ProviderAttemptContracts.cs` owns the exact immutable `ProviderAttemptIdentity`, `ProviderAttemptResult`, and one-shot `ProviderAttemptStream` types before any caller compiles. The stream wraps only the executor's frozen-call event source and attempt evidence, borrows the still-owned turn lease, and has one terminal disposal path.

`ICovenantDisclosureTransactionStore` and `CovenantDisclosureStore` are internal Infrastructure helpers. Every method requires the committer-owned initialized writer connection and its live immediate-transaction capability. They cannot open a connection, allocate an ordinal, update subject state, or append a receipt independently, and they are never registered in DI. Register only `DisclosureGroupCommitter` behind its queueing interface plus `ICovenantDisclosureWriterLifecycle`; Plan 04 and runtime callers cannot resolve the helper store. An architecture test rejects direct store resolution or invocation outside the committer and bounded compactor.

Exact test methods:

- `DispatchBufferedAsync_FreezesBeforeAdmissionDigest`
- `DispatchBufferedAsync_AcknowledgesDisclosureBeforeProviderCall`
- `DispatchBufferedAsync_DisclosureFailurePreventsProviderCall`
- `DispatchBufferedAsync_ExtendsGlobalAndBranchChainsOnce`
- `DispatchBufferedAsync_RetryGetsNewAdmissionAndPhysicalOrdinal`
- `DispatchBufferedAsync_RechecksReadAuthorityEpochImmediatelyBeforeCall`
- `DispatchBufferedAndStreaming_RevalidateTheSameTurnLeaseBeforeSdkInvocation`
- `UnprotectedDispatch_FreezesAndCallsProviderWithoutCovenantGateStoreOrDisclosureWork`
- `InheritedHistoryDispatch_RequiresTurnLeaseAndReceiptWithoutCurrentPlanEvidence`
- `Dispatch_RejectsAttemptInputAndTurnAuthorityArmMismatch`
- `Dispatch_RejectsClientPolicyDigestMismatchBeforeDisclosureOrSdkCall`
- `Dispatch_BorrowsResolvedClientLeaseAndNeverDisposesCandidateScope`
- `MultiAttemptCandidate_ReusesBorrowedLeaseAndOwnerDisposesOnceAfterTerminal`
- `CurrentCovenantEmptyAdmissionFinalizesLineageWithoutDisclosureForCleanCall`
- `EveryExecutorCallerPassesResolvedClientFrozenCallBudgetPurposeAndExplicitContext`
- `Dispatcher_ForwardsEveryMainRetryToolAndMaintenancePurposeWithoutDefaulting`
- `Dispatcher_RejectsUnsupportedTurnBudgetImplementationAndContextMismatch`
- `SemanticRouterAndLexiconExtractor_RejectWrongResolvedPolicyOrIdentityBeforeDispatch`
- `LexiconEntityExtractor_UsesFrozenUnprotectedCallAndNoMutableExecutorOverload`
- `GroupCommitter_AllocatesOrdinalsAndReceiptsInOneFullTransaction`
- `GroupCommitter_CancellationAfterSealMayLeaveReceiptButNeverUnreceiptedDispatch`
- `Compactor_PreservesEveryDestinationRevocabilityAggregateForOpenSubject`
- `Compactor_DeletesTerminalSubjectOnlyAfterEveryReceiptIsJoined`
- `GroupCommitter_QueueCapacityIs128AndBatchIs16Within200Microseconds`
- `GroupCommitter_At65536RowsBackpressuresUntilOneBoundedFoldCompletes`
- `Compactor_StartsAt60000AndFoldsAtMost256ReceiptsAnd64Subjects`
- `Compactor_OpenSubjectRetainsAtMost64DiagnosticRowsUnlessPressureRequiresZero`
- `SubjectLifecycle_OpenOrphanedCompletedAndAbandonedTransitionsAreFenced`
- `WriterLifecycle_IdleWarmConnectionQuiescesBeforeOwnerDrainAndReopensAfterHealth`
- `WriterLifecycle_QueuedBatchSealsOrCancelsBeforeExactLeaseRelease`
- `DisclosureTransactionStore_IsInternalTransactionBoundAndNotDiResolvable`

- [ ] **Step 1: Add failing order-observer tests**

Use an ordered fake disclosure store and fake provider executor.

```csharp
[Fact]
public async Task DispatchBufferedAsync_AcknowledgesDisclosureBeforeProviderCall()
{
    await dispatcher.DispatchBufferedAsync(
        clientLease,
        request,
        CancellationToken.None);

    Assert.Equal(
        new[] { "freeze", "admission", "disclosure-ack", "provider-call" },
        observer.Events);
}
```

- [ ] **Step 2: Run the focused dispatcher tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ProviderAttemptDispatcherTests|FullyQualifiedName~ModelCallExecutorTests|FullyQualifiedName~ProviderAttemptCommitTrackerTests|FullyQualifiedName~CovenantDisclosureStoreTests|FullyQualifiedName~CovenantDisclosureCompactionTests|FullyQualifiedName~DisclosureGroupCommitterTests|FullyQualifiedName~RequestAugmentingHandlerTests|FullyQualifiedName~SemanticRouterTests|FullyQualifiedName~LexiconEntityExtractorTests|FullyQualifiedName~ModelTokenEstimatorTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because model execution can receive mutable call state and no durable disclosure store, group committer, or subject compactor exists.

- [ ] **Step 3: Implement the acyclic dispatch sequence**

First verify that the frozen provider-policy and provider-identity digests equal `ResolvedChatClientLease.Policy` and `ResolvedChatClientLease.Provider`. A mismatch fails before disclosure or provider-client work. For `Admission.Current`, finalize `CovenantAdmissionReceipt` only after the provider-call digest exists, including an empty selected vector. For `SensitivityEvidence.InheritedHistory`, bind persisted source labels and producing evidence without inventing current-materialization evidence. For `(None, None)`, require final call sensitivity `None`. Queue frozen effect fields to one bounded `DisclosureGroupCommitter` only when the resulting call is derived. The committer opens and retains exactly one `CovenantInitializedConnectionLease` from `ICovenantSqliteConnectionFactory` in the writer connection class. That warm connection allocates the subject and physical ordinals, builds the effect identity, updates the open-subject overall count and chain, and inserts the receipt in one `synchronous=FULL` batch transaction. Acknowledge only after commit when a receipt is required. Revalidate every protected authority's exact `CovenantTurnLease`, read-authority epoch, feature or capability generation, and current host-process policy, including a clean empty-materialization call under `CurrentCovenant`, then pass only `clientLease.DispatchClient` internally to `IModelCallExecutor` with the same frozen projection. Extend global and branch chains with checked positive ordinals for receipt-bearing attempts. `CovenantTurnAuthority.Unprotected` still freezes and dispatches through the same executor shape but performs zero Covenant gate, store, receipt, or compactor calls. Every dispatcher, provider-candidate, fallback, compression, and tool-loop scope borrows the `CovenantLogicalTurnState` turn lease and never disposes or transfers it. Only the logical-turn state disposes that lease once after guarded commit or discard and buffered or streaming terminalization. Separately, the Wizard provider-candidate or fallback-branch scope owns and reuses the `ResolvedChatClientLease` across retry, compression, and tool-loop attempts, then disposes that client lease once after every buffered or streaming terminal path, including mismatch and failure.

Change the buffered and streaming `IModelCallExecutor` signatures in this slice and migrate every current production and test caller before the green command. Preserve the existing typed `ModelCallOutcome`, `ModelCallUpdate`, `ITurnBudget`, and `ModelCallPurpose` contracts because purpose controls reasoning, event, cache, and accounting behavior. `WizardIntelligenceProvider` uses the dispatcher. Change `SemanticRouter` and `LexiconEntityExtractor` production methods to borrow the same `ResolvedChatClientLease` shape and accept an explicit immutable dispatch context. They freeze sensitivity-None calls with `ArcanumInvocationContext.None`, construct the exact SpellRouting or LexiconExtraction dispatch request, and use no Covenant loader. Remove their bare `IChatClient` and optional mutable-context production overloads. The candidate owner retains and disposes the lease. Resolved SDK and Familiar clients reconstruct their requests only from `FrozenProviderCall` and the immutable resolved identity. No production overload accepts mutable `ChatMessage`, `ChatOptions`, a raw `IChatClient`, or an omitted client, budget, purpose, or context.

The committer accepts at most 128 queued intents and seals up to 16 ready intents within 200 microseconds of the first arrival. It owns overall open-subject counts and chains exactly once. `CovenantDisclosureCompactor` begins at 60,000 detailed rows, folds at most 256 contiguous receipts and 64 subject rows per transaction, retains at most the newest 64 rows for an open subject when pressure permits, and may fold that tail to zero. At 65,536 detailed rows, append applies backpressure until one bounded fold makes room or returns an availability failure before disclosure. It owns database I/O for destination-by-revocability aggregates, evidence Bloom, terminal fold, and bounded diagnostic-tail deletion, and delegates every local aggregate update to Plan 02's `CovenantDisclosureStateAlgebra.IncrementLocal`. It never increments the append-owned overall count or chain and deletes terminal subject state only after every receipt and subject aggregate is represented in the global semilattice.

Persist the closed subject lifecycle `Open`, `Orphaned`, `Completed`, or `Abandoned`, creator boot, heartbeat, close time, checked provider/effect counts and ordinals, last folded ordinal, and chain. Startup orphans only a resumable PendingMaintenance subject, guarded adoption reopens it, finalization or stateless completion closes it, other prior-boot turns become Abandoned, and operation subjects follow their recovery handler. Completed or Abandoned subjects cannot dispatch.

`ICovenantDisclosureWriterLifecycle` closes queue admission, seals and commits or cancels the bounded current batch, drains every acknowledged waiter, disposes the exact warm `CovenantInitializedConnectionLease`, and only then reports quiescence. Reopen acquires a fresh writer lease after the database and availability snapshot are healthy. Reinitialize, restore, reset, and installation erasure must quiesce this writer before requesting the central connection owner's exclusive drain, then reopen it before general admission. Failure keeps affected admission closed.

- [ ] **Step 4: Run the focused dispatcher tests to verify green**

Run the Step 2 command.

Expected: PASS, with zero provider calls on disclosure, authority, or chain-overflow failure.

- [ ] **Step 5: Refactor buffered and streaming dispatch onto one preflight**

Share freeze, admission, disclosure, and chain advancement. Keep only the final SDK invocation mode-specific. Rerun the Step 2 command.

Expected: PASS for buffered and streaming attempts, retry and fallback physical ordinals, queue failure, cancellation, bounded compaction, and eight concurrent writers.

## Task 12: Coordinate durable public Session turn claims

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionTurnClaimStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionTurnClaimModels.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionTurnRequestDigest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionTurnExecutionDigest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionTurnClaimStore.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/SessionTurnClaimCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionRequest.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SessionTurnClaimCoordinatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionTurnClaimStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/SessionTurnRequestDigestTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/SessionTurnExecutionDigestTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnDisconnectAndIdempotencyCharacterizationTests.cs`

**Interfaces:**

- Consumes: Plan 01's `session_turn_claims`, `session_turn_maintenance_steps`, immutable Session-binding, maintenance-output, future-finalization reservation, and quota-state schema; Plan 02's canonical encoder, `CovenantMutationTransaction`, and exact `CovenantQuotaGuard.ReserveClaimAndFinalizationAsync`, `ConsumeReservedFinalizationAsync`, and `ReleaseReservedFinalizationAsync` methods; and Task 4 canonical Campaign context.
- Produces: the SQLCipher-backed `ISessionTurnClaimStore`, the exact `SessionTurnRequest.v1` runtime projection builder, a builder over Plan 02's existing `SessionTurnExecutionDigestInput` and `Arcanum.Covenant.SessionTurnExecution.v1` domain, maintenance checkpoint storage, and:
- Produces:

```csharp
public interface ISessionTurnClaimCoordinator
{
    Task<Result<SessionTurnClaimLease>> AcquireAsync(
        SessionTurnRequestIdentity request,
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken);

    Task<Result<SessionTurnClaim>> MarkBegunAsync(
        SessionTurnClaimLease lease,
        AssistantReplyBeginReceipt begin,
        CancellationToken cancellationToken);

    Task<Result<SessionTurnClaim>> CompleteAsync(
        SessionTurnClaimLease lease,
        SessionTurnClaimOutcome outcome,
        CancellationToken cancellationToken);
}
```

Exact test methods:

- `AcquireAsync_InsertsPendingMaintenanceBeforeAnyProviderDisclosure`
- `AcquireAsync_SameIdentityAndDigestReturnsExistingClaim`
- `AcquireAsync_SameIdentityDifferentDigestReturnsIdempotencyConflict`
- `AcquireAsync_DifferentActiveClaimReturnsSessionTurnBusy`
- `AcquireAsync_NewSessionCreatesOneBoundSessionWithoutEntries`
- `MarkBegun_RejectsChangedHistoryWatermark`
- `CompleteAsync_ReplaysCommittedDiscardedErasedAndRestoredInterrupted`
- `Store_LeaseTakeoverUsesBootOwnerExpiryAndRevisionCas`
- `Store_PersistsBoundedMaintenanceOutputAndTerminalFailure`
- `RequestDigest_DistinguishesRouteContextPolicyToolsAttendanceAndOptions`
- `SessionTurnExecutionDigest_BindsResolvedProviderPromptSpellAttachmentsCampaignPathAndPolicy`
- `AcquireAsync_NonterminalDependencyChangeFailsClosedWithoutDispatch`
- `CompleteAsync_TerminalReplayIgnoresMutableDependencyChangesAfterRequestAndAuthorityValidation`
- `AcquireAsync_ReservesClaimAndFutureGuardCapacityBeforeAnyDisclosure`
- `AcquireAsync_StoresFutureAssistantEntryIdentityWithoutCreatingEntry`
- `AcquireAsync_RetryOrAdoptionCannotReplaceFutureAssistantEntryIdentity`
- `AcquireAsync_PerSessionClaimOrGuardLimitFailsBeforeDisclosure`
- `AcquireAsync_InstallationClaimOrGuardLimitFailsBeforeDisclosure`
- `CompleteAsync_NeverBegunClaimReleasesExactReservedFinalization`

- [ ] **Step 1: Add failing claim-state tests**

```csharp
[Fact]
public async Task AcquireAsync_InsertsPendingMaintenanceBeforeAnyProviderDisclosure()
{
    SessionTurnClaimLease claim = (await coordinator.AcquireAsync(
        request,
        campaign,
        CancellationToken.None)).Value;

    Assert.Equal(SessionTurnClaimState.PendingMaintenance, claim.State);
    Assert.Empty(disclosures.Receipts);
}
```

- [ ] **Step 2: Run the focused claim tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SessionTurnClaimCoordinatorTests|FullyQualifiedName~SessionTurnClaimStoreTests|FullyQualifiedName~SessionTurnRequestDigestTests|FullyQualifiedName~SessionTurnExecutionDigestTests|FullyQualifiedName~TurnDisconnectAndIdempotencyCharacterizationTests"
```

Expected: FAIL because generic idempotency and assistant placeholders are the current turn identity, and no `PendingMaintenance` state exists.

- [ ] **Step 3: Implement claim acquisition and terminal replay**

Accept a canonical `Guid` client-turn identity at the internal boundary. `SessionTurnRequest.v1` binds only the stable client request identity: route and Session-turn surface, canonical client body, existing-Session identity or the canonical new-Session marker, normalized context policy, and every client-visible tool, attendance, attachment reference, and provider-option field. The existing Plan 02 `SessionTurnExecutionDigestInput` under `Arcanum.Covenant.SessionTurnExecution.v1` separately binds the resolved Prompt or Spell identity and revision, resolved provider/model/configuration generation, resolved attachment versions, immutable Campaign binding and path revision, effective tool and attendance policies, tokenizer/adapter identities, and the frozen provider-budget option projection. Do not add a second execution-dependency domain tag. Plan 04 later owns strict HTTP UUID parsing. Acquire or resume the exact claim, create a new bound Session and immutable binding in that same first transaction when needed, capture the pre-request history and sensitivity revision, and return the content-free `SessionTurnInputPreflight` used by Tasks 5 and 15.

Implement `SessionTurnClaimStore` with one partial unique active-claim constraint per Session, boot and owner identity, lease expiry, heartbeat, checkpoint revision CAS, bounded deterministic maintenance step outputs, terminal failure code, and sensitivity digest. The first immediate transaction generates one random future assistant Entry ID without inserting an Entry, binds that ID plus one reservation identity immutably to the claim request, creates the new Session and immutable binding when needed, then calls `CovenantQuotaGuard.ReserveClaimAndFinalizationAsync(request, transaction, cancellationToken)` before inserting `PendingMaintenance`. That same transaction atomically enforces the exact 16,384 per-Session and 1,048,576 installation-wide claim limits and the separate equal assistant-finalization guard limits, inserts the reservation, quota changes, and claim, then commits once. A limit failure creates no Session, claim, reservation, disclosure subject, placeholder, or provider effect. Same-identity replay, expired-lease adoption, and prior-boot adoption read and return the stored reservation and future assistant Entry IDs; they reject any attempted replacement and never generate a second ID. Task 5 assistant begin calls `CovenantQuotaGuard.ConsumeReservedFinalizationAsync(identity, transaction, cancellationToken)` and inserts the placeholder with that exact future ID in its one transaction. Terminalization of a never-begun claim calls `CovenantQuotaGuard.ReleaseReservedFinalizationAsync(identity, transaction, cancellationToken)` in the same claim transaction. Every quota call receives the live caller-owned `CovenantMutationTransaction` and never commits or retries it. The direct internal begin in Task 14 uses direct allocation without a public reservation. Preserve immutable pre-request history and input-sensitivity revisions separately from the claim's expected-current sensitivity revision. Each committed maintenance step atomically advances the expected-current revision by CAS and returns the resulting `SessionSensitivitySnapshot`, while the original input snapshot and backlog watermark remain immutable evidence. Same-ID takeover adopts only expired or prior-boot pending work after current authority, request-digest, and execution-dependency revalidation. A changed execution dependency on a nonterminal claim fails closed without provider dispatch or checkpoint advancement. Different-ID acquisition returns typed busy. Assistant begin rechecks the original pre-request history revision and latest expected-current sensitivity revision. Terminal replay validates only the same client identity, stable request digest, terminal evidence, and current protected-read authority. It never requires mutable provider, model, configuration, Prompt, Spell, attachment, path, or tool-policy dependencies to remain current.

- [ ] **Step 4: Run the focused claim tests to verify green**

Run the Step 2 command.

Expected: PASS for same-ID replay, digest conflict, busy Session, new Session, stale history, maintenance revision advancement, unrelated projection conflict, fenced takeover, checkpoint recovery, request-digest vectors, and terminal outcomes.

- [ ] **Step 5: Refactor generic cache eligibility into a typed decision**

Expose `SessionTurnClaimCoordinator.BypassGenericResponseCache` for every session-backed request and every stateless request that can inject Covenant. Plan 04 consumes it before cache lookup. Rerun the Step 2 command.

Expected: PASS, and no claim-backed request reads or writes the generic response cache.

## Task 13: Add a branch-scoped mutation collector with exact replay

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantMutationCollector.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationCollector.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationCollectorState.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMutationCollectorTests.cs`

**Interfaces:**

- Consumes: Plan 02's `CovenantMutationIntent`, canonical input digest, plan/admission digests, and target identity.
- Produces:

```csharp
public interface ICovenantMutationCollector
{
    Result<CovenantCollectorUseLease> TryAcquireUse(
        CovenantBranchWitness branch);

    ValueTask<Result<CovenantStagedReceipt>> StageAsync(
        CovenantCollectorUseLease lease,
        CovenantMutationIntent intent,
        CancellationToken cancellationToken);

    Task<Result<CovenantMutationBatch>> SealAsync(
        CovenantCommittedLineage lineage,
        CancellationToken cancellationToken);

    void AbandonBranch(CovenantBranchId branchId);

    void Discard();
}
```

Exact test methods:

- `StageAsync_ExactToolReplayReturnsOriginalReceipt`
- `StageAsync_ReusedToolIdentityWithDifferentDigestConflicts`
- `StageAsync_FifthLiveIntentReturnsCapacityExceeded`
- `AbandonBranch_DiscardsOnlyAbandonedIntents`
- `SealAsync_WaitsForUseLeasesAndRejectsLateStage`
- `SealAsync_FiltersToCommittedAncestry`
- `Discard_PreventsCapturedContinuationFromStaging`

- [ ] **Step 1: Add failing lifecycle, replay, and branch tests**

```csharp
[Fact]
public async Task StageAsync_ExactToolReplayReturnsOriginalReceipt()
{
    CovenantStagedReceipt first = (await collector.StageAsync(lease, intent, token)).Value;
    CovenantStagedReceipt replay = (await collector.StageAsync(lease, intent, token)).Value;

    Assert.Equal(first, replay);
    Assert.Equal(1, collector.LiveIntentCount);
}
```

- [ ] **Step 2: Run the focused collector tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantMutationCollectorTests"
```

Expected: FAIL because the collector and branch-aware use leases do not exist.

- [ ] **Step 3: Implement the linearizable lifecycle**

Synchronize replay lookup before target uniqueness. Reserve at most four live intents. Partition by branch, release slots on abandonment, reject tombstone reactivation supplied by preflight, and seal only committed ancestry after active leases drain. Make `Discarded` irreversible.

- [ ] **Step 4: Run the focused collector tests to verify green**

Run the Step 2 command.

Expected: PASS for replay, conflict, capacity, branch abandonment, seal, and discard races.

- [ ] **Step 5: Refactor live evidence to O(1) chain heads plus four witnesses**

Remove attempt-history lists from the collector. Retain only current receipt, rolling chain heads, and the at most four staged witnesses. Rerun the Step 2 command.

Expected: PASS with productive attempt counts above 64.

## Task 14: Commit assistant finalization and Covenant publication atomically

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Storage/IGrimoireTurnCommitter.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/TurnCommitRequest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/TurnCommitReceipt.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/TurnCommitCovenantEvidence.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionSensitivityStateReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionSensitivitySnapshot.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IInternalConversationStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/InternalConversationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionTurnInputReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionTurnInputContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IProtectedArtifactTransferStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ProtectedArtifactTransferRequests.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ImportedSessionSourceLease.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantTransactionFactory.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantFinalizationTransactionStores.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/AssistantFinalizationStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ArtifactSensitivityStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionSensitivityStateReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionTurnInputReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireTurnCommitter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedSessionTransferIntentStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedSessionTransferRecoveryService.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/IGrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/TheForge/ISessionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEngine.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireTurnCommitterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/AssistantFinalizationStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ArtifactSensitivityStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionSensitivityStateReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionTurnInputReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ProtectedArtifactTransferStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireTurnWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnEngineAbandonmentTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/ApprenticeServiceReliabilityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireFinalizationCallerInventoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/SessionForkEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ProtectedSessionTransferRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/MandatoryGrimoireRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Plan 01's finalization, erasure, sensitivity, claim, receipt, future-finalization reservation, quota-state, canonical Covenant, and core Session tables; Plan 02's mutation kernel, canonical encoders, `SessionTurnRequestDigestInput`, caller-owned `CovenantMutationTransaction`, exact `CovenantQuotaGuard.AllocateDirectFinalizationAsync`, explicit compound-lease dispositions, and `ICovenantExclusivePostDispositionFinalizer`; Task 12's request-digest builder and claim store; and Task 13's sealed batch.
- Produces: internal transaction-scoped finalization, artifact-label, and sensitivity-summary helpers, one canonical immediate-transaction factory over the existing scoped SQLCipher connection, a separate protected import/fork transfer port, plus:
- Produces:

```csharp
public interface IGrimoireTurnCommitter
{
    Task<Result<TurnCommitReceipt>> CommitAsync(
        TurnCommitRequest request,
        TurnCommitCovenantEvidence covenantEvidence,
        CancellationToken cancellationToken);
}

public sealed record TurnCommitRequest(
    Guid AssistantEntryId,
    Guid SessionId,
    AssistantTurnIdentity TurnIdentity,
    CanonicalCampaignContext Campaign,
    string FinalText,
    ArtifactSensitivityLabel AssistantSensitivity,
    AssistantFinalizationOutcome Outcome);

public interface IProtectedArtifactTransferStore
{
    Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitImportedSessionAsync(
        ImportedSessionTransferRequest request,
        ImportedSessionSourceLease sourceLease,
        CovenantProtectedTransferLease transferLease,
        CancellationToken cancellationToken);

    Task<ProtectedSessionTransferCompletion<SessionForkCommitReceipt>> ForkSessionAsync(
        ProtectedSessionForkRequest request,
        CovenantProtectedTransferLease transferLease,
        CancellationToken cancellationToken);
}

public sealed class ProtectedSessionTransferCompletion<T>
{
    public Result<T> Result { get; }

    public CovenantExclusiveLeaseDisposition Disposition { get; }

    public ICovenantExclusivePostDispositionFinalizer Finalizer { get; }
}

public interface IInternalConversationStore
{
    Task<Result<InternalSessionCreationReceipt>> CreateSessionAsync(
        InternalSessionCreationRequest request,
        CancellationToken cancellationToken);

    Task<Result<InternalAssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
        InternalAssistantReplyBeginRequest request,
        CancellationToken cancellationToken);
}
```

`ISessionSensitivityStateReader` projects `SessionSensitivitySnapshot(SessionId, HistoryRevision, SensitivityRevision, ArtifactSensitivityLabel Label, GenerationProvenance Provenance, CovenantDigest ProducingEvidenceDigest)` in the existing Session and history command and SQLite snapshot. It performs no per-artifact lookup. A missing or malformed sensitivity row fails closed when finalization evidence indicates protected content.

`ISessionTurnInputReader` owns the only inference history-loading contract. Its `ReadHistorySnapshotAsync` accepts the Task 5/12 content-free `SessionTurnInputPreflight` plus a closed nonserializable `SessionTurnHistoryReadAuthority` arm. The `VerifiedClean` arm causes the reader to open one SQLite read transaction, recheck the same binding and revisions, prove the projection and every joined exact label are clean, and only then materialize the thread. The `Protected` arm requires the exact live `ICovenantSnapshotReadLease`, validates it before the transaction, and reads the Session header, current title and summary artifacts, Entries, tool details, attachment metadata, exact labels, and projected sensitivity in that same transaction. Any revision, binding, missing-label, or projection mismatch returns a typed stale or authority failure without returning content. `SessionTurnInputSnapshot` carries the fully hydrated immutable thread plus the exact `SessionSensitivitySnapshot` observed in that transaction. No caller can use `VerifiedClean` to read a tainted row because the store repeats the proof before materialization. The claim and begin lifecycle already returns the preflight, so this contract adds no standalone sensitivity command to a disabled untainted Session.

`AssistantTurnIdentity` is a closed nonserializable union. `Claimed` carries the real pending-claim ID and consumed finalization reservation identity. `Internal` carries a server-generated internal turn ID and the matching `InternalAssistantReplyBeginReceipt`; it has no client retry claim or response-replay authority and is eligible only for explicit sensitivity `None` with `TurnCommitCovenantEvidence.Unprotected`. `IInternalConversationStore` provides a dedicated atomic Session creator that writes the immutable binding with no Entry, plus a guarded direct-internal begin. That begin generates the assistant Entry ID once, calls Plan 02 `CovenantQuotaGuard.AllocateDirectFinalizationAsync(request, transaction, cancellationToken)` with origin `Internal` and the same live caller-owned `CovenantMutationTransaction`, inserts the already-consumed capacity row plus user Entry and exact assistant placeholder, and commits them once. A failure leaves none of them visible, and replay cannot substitute an assistant identity. The begin request carries Plan 02's existing `SessionTurnRequestDigestInput` with the existing `Intelligence` surface code, the exact Session identity, canonical bounded internal body, `ContextPolicy.None`, unattended attendance, zero tools, and the effective client-visible option fields. The store rejects any other surface, context, attendance, tool, or Session binding, computes the ordinary `SessionTurnRequest.v1` digest without inventing an internal domain or fourth surface, and includes that digest in `InternalAssistantReplyBeginReceipt`. The common committer writes it into `assistant_entry_finalizations`. It never creates a placeholder merely to obtain a Session ID. `ApprenticeService` uses these exact operations and the common committer instead of the removed broad Grimoire begin, finalize, and discard methods.

`TurnCommitCovenantEvidence` is a closed nonserializable union with three exact arms. `Unprotected` contains no Covenant receipt, sensitivity, batch, tool receipt, or lease. `ProtectedHistory` contains the exact turn lease, persisted sensitivity and generation provenance, and disclosure-chain terminal evidence. It contains no `CovenantFinalReceipt`, current snapshot or plan digest, mutation batch, or Covenant tool receipt. `CurrentCovenant` contains the exact turn lease, one `CovenantFinalReceipt`, the sealed mutation batch, and Covenant tool receipts. Only `CurrentCovenant` may publish staged mutations. Imported and forked Sessions never fabricate a claim, internal turn identity, or Covenant final receipt.

`AssistantFinalizationStore`, `ArtifactSensitivityStore`, and the sensitivity-summary writer are internal Infrastructure transaction objects, not Core ports. Their constructors require the exact live `CovenantMutationTransaction` or finalization immediate-transaction capability and cannot open, commit, or retry a transaction. They are never registered in DI. Register only `IGrimoireTurnCommitter`, the read-only sensitivity/input readers, `IProtectedArtifactTransferStore`, `IInternalConversationStore`, and recovery services. Architecture and DI tests prove no caller can resolve or invoke a label or finalization helper independently.

`ImportedSessionSourceLease` is a nonserializable, async-disposable capability created by the backup importer. It pins one read-only source SQLCipher snapshot and no-follow source-attachment authority through destination commit. `ImportedSessionTransferRequest` contains the nonempty import operation ID, source Session ID, source-evidence digest, destination Session and explicit Campaign mapping, authenticated bounded source-manifest digest and counts, and its Core-computed transfer-effect digest. The store reads the complete source graph and labels through the lease, recomputes and compares the manifest, and rejects the entire import if the caller omitted any source row or attachment or if any source is Covenant-derived. It remaps the complete untainted Session graph and writes `CommittedImported` guards without claims or replay authority. `ProtectedSessionForkRequest` contains the nonempty fork operation ID, source Session ID, optional `UpToEntryId`, destination identity, immutable destination binding, and its Core-computed transfer-effect digest. The store reads the source graph itself under the held read lease and database snapshot. A partial fork rejects the whole operation if any source artifact is Covenant-derived. A full authenticated fork may copy derived content and all matching labels, provenance, finalization or erasure evidence, and current Summary and Title state.

Both request factories compute `TransferEffectDigest` with the one domain `Arcanum.Covenant.ProtectedSessionTransfer.v1` over this ordered preimage: transfer-kind code `Import=1` or `Fork=2`; RFC-4122 operation UUID; source Session UUID; cutoff-presence byte and optional cutoff Entry UUID; destination Session UUID; destination-binding digest; source-evidence-presence byte and optional digest; manifest-presence byte and optional manifest digest; and the canonical checked count-vector digest. The digest is never accepted as independent caller input. The operation ID and effect digest are immutable and are persisted with the transfer intent before filesystem work.

`CovenantProtectedTransferLease` is Plan 02's single nonserializable compound snapshot/exclusive lease. For a same-installation fork, its Global or Campaign scope must match both immutable source and destination binding. For selective import, it matches only the local destination scope; source authority comes from the external `ImportedSessionSourceLease` plus the authenticated source-to-destination Campaign mapping. Before initial acquisition, the caller constructs `CovenantExclusiveRecoveryOwner(request.OperationId, CovenantExclusiveOperation.ProtectedSessionTransfer, request.TransferEffectDigest)` and passes it with the scope to `AcquireProtectedTransferAsync`. Fork uses the compound lease's bounded snapshot-read capability to load the source graph. Selective import keeps the separate read-only source lease for the backup, while the compound lease fences every destination blob and database phase. Neither path acquires a separate Covenant read lease, so exclusive drain cannot wait on its own caller. For every imported or forked assistant finalization, the destination transaction calls `CovenantQuotaGuard.AllocateDirectFinalizationAsync(request, transaction, cancellationToken)` with origin `Imported` or `Forked`, the exact remapped assistant Entry ID, and that same live caller-owned `CovenantMutationTransaction` before inserting the matching already-consumed capacity and finalization rows.

`ProtectedSessionTransferCompletion<T>` is nonserializable, deep-copies no artifact, and never owns the caller's lease. A verified committed operation persists `ReopenPending(CommitAndReopen)` and returns that disposition plus its one-shot journal finalizer. Proven precommit cleanup returns the matching rollback shape. Uncertainty or failure before a finalizable proof retains the last proven journal phase, returns `KeepClosed`, and supplies only Plan 02's sealed no-op finalizer. Recovery reconstructs the same owner and persisted destination scope from the intent and calls `ResumeProtectedTransferAsync`; it never calls the initial acquisition method. The caller invokes the returned disposition with one recovery-owned token. Only after disposition succeeds does it invoke the returned finalizer, which advances `ReopenPending` to `Completed` or `Abandoned` and releases bounded child rows. Failed disposition or finalizer failure retains `ReopenPending`. Every such state leaves the durable owner adoptable on restart. The store cannot call `CompleteAsync`, dispose the lease, or return a null, default, delegate, or mismatched finalizer.

Exact test methods:

- `CommitAsync_ValidEmptyResponseCommitsOnce`
- `CommitAsync_PublishesAssistantLabelAndMutationsInOneTransaction`
- `CommitAsync_RevisionConflictRollsBackAssistantAndEveryIntent`
- `CommitAsync_SameGuardReturnsCommittedReceipt`
- `CommitAsync_ClaimedIdentityRequiresExactConsumedFutureAssistantId`
- `CommitAsync_DiscardedGuardCannotCommit`
- `CommitAsync_BusyRetryRepeatsWholeTransaction`
- `CommitAsync_StaleTurnLeaseRollsBackBeforeAssistantOrMutationPublication`
- `CommitAsync_RejectsEvidenceAndAssistantSensitivityMismatchBeforeWrite`
- `CommitAsync_UnprotectedPathTouchesNoOptionalCovenantStoreOrReceipt`
- `CommitAsync_ProtectedHistoryUsesDisclosureTerminalEvidenceWithoutFinalReceiptOrMutation`
- `TurnEngine_CancellationDiscardsCollectorAndFinalizesEmptyBatch`
- `TurnEngine_StreamDisconnectCannotPublishLateIntent`
- `FinalizationStore_AcceptsExactlyOneLiveLabelOrErasureReceipt`
- `ArtifactSensitivityStore_RejectsDowngradeAndGuardedDeleteWithoutArtifactPurge`
- `SessionSensitivityStateReader_LoadsSummaryInExistingHistoryCommand`
- `AssistantReplyBeginReceipt_ContentFreePreflightContainsNoProtectedArtifactBytes`
- `SessionTurnInputReader_LoadsHistoryAndExactLabelsInOneSnapshot`
- `SessionTurnInputReader_VerifiedCleanArmRejectsTaintBeforeMaterialization`
- `SessionTurnInputReader_RevisionLabelOrBindingRaceReturnsNoContent`
- `ImportedCommit_UsesCommittedImportedOriginWithoutClaimOrReplayAuthority`
- `ImportedCommit_RejectsDerivedArtifactInsideStoreEvenAfterCallerPrecheck`
- `ImportedCommit_RejectsCallerManifestThatOmitsTaintedArtifactOrAttachment`
- `ForkSession_CopiesBindingEntriesLabelsFinalizationSummaryAndTitleAtomically`
- `ForkSession_PartialCutoffRejectsAnyDerivedSourceArtifact`
- `ForkSession_GlobalOnlyUsesGlobalProtectedTransferScope`
- `ProtectedTransfer_CompoundLeaseProvidesSnapshotWithoutNestedAcquireOrSelfDeadlock`
- `ProtectedTransfer_InitialAndRecoveryAcquisitionUseExactOperationIdKindAndEffectDigest`
- `ProtectedTransfer_WrongOperationIdEffectOrScopeCannotResumeKeptClosedOwner`
- `ImportedCommit_CampaignDeleteOrResetDrainsBeforeAnyDestinationEffect`
- `ForkSession_LabelFailureRollsBackEveryCopiedArtifact`
- `ForkSession_WritesDestinationSensitivityStateFromTheCompleteCopiedGraph`
- `ImportedCommit_SeedsExactUntaintedDestinationSensitivityState`
- `ProtectedTransfer_SensitivityProjectionFailureRollsBackDestinationGraph`
- `ProtectedSessionTransfer_CrashAtEveryBlobAndDatabasePhaseRecoversIdempotently`
- `ProtectedSessionTransfer_DatabaseFailureLeavesNoVisibleDestinationOrUnownedBlob`
- `ProtectedSessionTransfer_ReopenPendingSurvivesCrashOrFailedDispositionAndResumesExactOwner`
- `ProtectedSessionTransfer_ChildrenRemainUntilSuccessfulPostDispositionFinalizer`
- `ProtectedSessionTransfer_CrashThenImmediateQueryOrResetStaysClosedUntilRecoveryCompletes`
- `SessionForkEndpoint_UsesProtectedTransferPortAndNeverWritesDirectly`
- `LegacySessionRepositoryForkAndGrimoireFinalizersAreAbsentFromInjectableInterfaces`
- `EveryBeginFinalizeAndDiscardCallerUsesOneGuardedTerminalPath`
- `ApprenticeSessionCreationUsesDedicatedCreatorWithoutPlaceholder`
- `ApprenticeInternalTurnUsesGuardedInternalBeginAndCommonCommitterWithSensitivityNone`
- `InternalTurnCannotCarryProtectedEvidenceOrClientReplayAuthority`
- `InternalTurnGuardUsesExistingIntelligenceSessionTurnRequestDigestDomain`
- `InternalBegin_AllocatesDirectFinalizationWithExactAssistantIdentityInOneTransaction`
- `ImportedAndForkedFinalizations_AllocateDirectCapacityInDestinationTransaction`
- `ProtectedTransferRecovery_UsesExactCommitRollbackOrKeepClosedDisposition`
- `FinalizationAndLabelTransactionHelpers_AreInternalAndNotDiResolvable`

- [ ] **Step 1: Add failing transaction and one-shot tests**

Use the real SQLCipher-backed Grimoire fixture and Plan 02's canonical test store.

```csharp
[Fact]
public async Task CommitAsync_RevisionConflictRollsBackAssistantAndEveryIntent()
{
    Result<TurnCommitReceipt> result = await committer.CommitAsync(
        requestWithStaleIntent,
        protectedEvidence,
        CancellationToken.None);

    Assert.False(result.IsSuccess);
    Assert.Equal(ErrorCodes.Covenant.RevisionConflict, result.Error.Code);
    Assert.True(await entries.IsPlaceholderAsync(requestWithStaleIntent.AssistantEntryId));
    Assert.Empty(await covenantVersions.ForMutationIdsAsync(intentIds));
}
```

- [ ] **Step 2: Run the focused finalization suites and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireTurnCommitterTests|FullyQualifiedName~AssistantFinalizationStoreTests|FullyQualifiedName~ArtifactSensitivityStoreTests|FullyQualifiedName~SessionSensitivityStateReaderTests|FullyQualifiedName~SessionTurnInputReaderTests|FullyQualifiedName~ProtectedArtifactTransferStoreTests|FullyQualifiedName~ProtectedSessionTransferRecoveryTests|FullyQualifiedName~GrimoireFinalizationCallerInventoryTests|FullyQualifiedName~GrimoireTurnWriterTests|FullyQualifiedName~TurnEngineAbandonmentTests|FullyQualifiedName~ApprenticeServiceReliabilityTests|FullyQualifiedName~GrimoireRepositoryTests|FullyQualifiedName~MandatoryGrimoireRepositoryTests|FullyQualifiedName~SessionRepositoryTests|FullyQualifiedName~SessionAttachmentIndexingTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~SessionForkEndpointTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because finalization returns Boolean or `Task`, infers pending state from empty content, and cannot publish a sealed Covenant batch atomically.

- [ ] **Step 3: Extend the existing Grimoire finalizer transaction**

Acquire the existing per-Session write lock. For either protected arm, revalidate its exact Task 15 `CovenantTurnLease`, then open one immediate transaction through `CovenantTransactionFactory` on the EF-owned scoped SQLCipher connection and revalidate captured dataset, Campaign, and authority facts again before the irreversible assistant or mutation write. Validate claim and canonical Campaign. A `Claimed` identity must resolve the exact consumed reservation whose immutable future assistant Entry ID equals `TurnCommitRequest.AssistantEntryId`; no retry, takeover, or finalizer may substitute another ID. Use the transaction-scoped stores to insert or resolve the one-shot guard, apply the mutation kernel only for `CurrentCovenant`, persist final text including an empty string, append the assistant sensitivity label, bind either protected-history disclosure terminal evidence or the current-Covenant final receipt as appropriate, update `session_sensitivity_state`, and commit once. The `Unprotected` arm never opens or calls an optional Covenant store and never fabricates final evidence. The finalization guard binds the sensitivity digest and accepts exactly one live label or one erasure receipt. Return typed failures without discarding their code.

Before opening the finalization transaction, validate the evidence and assistant label as one closed matrix. `Unprotected` requires exact sensitivity `None`. `ProtectedHistory` requires the assistant label and bounded provenance to equal the maximum inherited input and resulting disclosure evidence. `CurrentCovenant` requires them to equal the exact final-receipt sensitivity. Any mismatch fails before an Entry, guard, label, receipt, or mutation write.

Implement `IProtectedArtifactTransferStore` with the Plan 01 transfer journal and normalized per-blob child rows. Accept, validate, and hold the caller-acquired Plan 02 `CovenantProtectedTransferLease` for the matching Global or Campaign scope from `Prepared` through blob cleanup and database commit. The live operation never reacquires or nests another operation or read lease. Recovery alone reacquires the persisted destination transfer scope after a restart. Before the first filesystem byte, commit `Prepared` plus every blob ordinal, durable destination parent identity, bounded temporary and immutable final leaves, expected full hash and length, and initial phase. Copy each source attachment through no-follow handles into its operation-owned same-directory temporary file, fsync it, rename it without replacement to its final immutable operation-owned location, fsync the parent, reopen and verify identity and hash, then advance that blob's CAS phase. After every blob is verified, CAS the parent intent to `BlobsStaged`. These final blobs exist but are not reachable from any Session row. Use the compound lease's snapshot capability for a same-database fork, or the retained `ImportedSessionSourceLease` for backup import. Open one immediate destination transaction, revalidate the compound lease, load and validate the source graph and cutoff itself, recheck the destination mapping and label policy, insert the complete destination Session graph plus pointers to those already verified final blobs, insert imported or forked finalization evidence, recompute and insert destination `session_sensitivity_state` from the remapped labels, and CAS `DatabaseCommitted`, then commit once. A selective clean import and clean partial fork write the canonical clean projection. A full protected fork writes exact tainted count, maximum sensitivity, bounded provenance digest, and revision from the copied graph. Any projection failure rolls back every destination row. Visibility is atomic at the database commit and every visible pointer already resolves. Recovery can enumerate the durable blob children without a source lease, reacquires the same compound scope, compare-deletes exact operation-owned unreferenced final or temporary blobs before database commit, or verifies every referenced blob after it. It then persists `ReopenPending` with the exact selected disposition while retaining the immutable parent owner and every child needed to prove the result. The response or recovery boundary calls the matching gate disposition and, only after success, invokes the journal's one-shot finalizer to CAS `Completed` or `Abandoned` and clear bounded child rows. A crash or failure between journal preparation and successful disposition therefore remains resumable. It never deletes an unknown or mismatched file.

Selective import scans and rejects derived labels inside that transaction, remaps the complete untainted Session, and writes `CommittedImported` guards with one source-evidence digest and no retry authority. A full protected fork copies matching labels and provenance plus live finalization or erasure evidence and Summary and Title current state, and writes `CommittedForked`; a partial tainted fork fails before staging. Route the existing authenticated Session fork handler through this port. Remove `ISessionRepository.ForkAsync` from the injectable interface and remove assistant begin, finalize, and discard from `IGrimoireRepository`; Tasks 5 and 14 provide the only narrow production ports. Make any implementation helper private or internal and assert no production caller can resolve it through DI. Plan 04 Task 15 routes selective import through the same protected-transfer port. Register transfer recovery in the pre-readiness Grimoire bootstrap registry. Session reads, retention, reset, and general admission remain closed until every nonterminal transfer intent and child blob row is reconciled under its reacquired compound scope. A reset or owner deletion cannot delete the journal before filesystem reconciliation. Register only the top-level ports and recovery services named above in the existing Infrastructure DI composition.

- [ ] **Step 4: Run the focused finalization suites to verify green**

Run the Step 2 command.

Expected: PASS for atomic commit, valid empty response, rollback, one-shot replay, cancellation, and disconnect.

- [ ] **Step 5: Route every finalization path through `IGrimoireTurnCommitter`**

Replace `TryFinalizeAssistantEntryAsync` and its private Boolean helper with `Result<TurnCommitReceipt>` flow. Live claimed ordinary, cancelled, and interrupted paths pass their real claim, the exact `Unprotected`, `ProtectedHistory`, or `CurrentCovenant` evidence arm, and terminal outcome. A direct internal Session-backed turn uses `IInternalConversationStore`, passes its exact `Internal` identity with `Unprotected` and sensitivity `None`, and still reaches the same immutable finalization guard. Imported and forked paths use only `IProtectedArtifactTransferStore`; a server-internal Session creation path uses the dedicated Session creator and never creates an unfinalized assistant placeholder. Migrate `ApprenticeService`, the Grimoire and mandatory-repository tests, Session repository fork tests, and Session attachment indexing fixtures in this slice before the interface removal compiles. Rerun:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireTurnWriterTests|FullyQualifiedName~TurnEngineAbandonmentTests|FullyQualifiedName~TurnDisconnectAndIdempotencyCharacterizationTests|FullyQualifiedName~ProtectedArtifactTransferStoreTests|FullyQualifiedName~GrimoireFinalizationCallerInventoryTests|FullyQualifiedName~SessionEndpointTests"
```

Expected: PASS, with no production caller invoking `GrimoireRepository.FinalizeAssistantEntryAsync` directly.

## Task 15: Create one Covenant snapshot and plan per logical Wizard turn

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantContextProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLogicalTurnState.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantTurnStateRequest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPreviewStateRequest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantInspectionState.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLeasedServiceResult.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPreviewServiceResult.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantContextProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceContextBuilder.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionRequest.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/ITurnPipelineRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEngine.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnEnginePlanIntegrationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/InferenceContextBuilderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantLeasedServiceResultTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Task 1 invocation context, Task 5/12 content-free `SessionTurnInputPreflight`, Task 14 `ISessionTurnInputReader`, Plan 02 availability/store/linker, Task 12 claim, Task 13 collector, and Task 11 dispatcher.
- Produces:

```csharp
public interface ICovenantContextProvider
{
    ValueTask<Result<CovenantLogicalTurnState>> CreateTurnStateAsync(
        CovenantTurnStateRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantInspectionState>> CreatePreviewStateAsync(
        CovenantPreviewStateRequest request,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantInspectionState>> CreateManagementExplainStateAsync(
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public sealed class CovenantLogicalTurnState : IAsyncDisposable
{
    public CovenantTurnAuthority Authority { get; }

    public SessionTurnInputSnapshot? SessionInput { get; }

    public CovenantTurnSnapshot? Snapshot { get; }

    public CovenantTurnPlan? Plan { get; }

    public ICovenantMutationCollector? MutationCollector { get; }

    public ArtifactSensitivityLabel InputSensitivity { get; }
}

public sealed class CovenantInspectionState : IAsyncDisposable
{
    public CovenantInspectionAuthority Authority { get; }

    public SessionTurnInputSnapshot? SessionInput { get; }

    public ArtifactSensitivityLabel InputSensitivity { get; }

    public CovenantTurnSnapshot? Snapshot { get; }

    public CovenantTurnPlan? Plan { get; }

    public Result<CovenantReadLease> DetachLeaseForResponse();
}

public sealed class CovenantLeasedServiceResult<T> : IAsyncDisposable
{
    public Result<T> Result { get; }

    public Result<ICovenantOperationLease> TakeLease();
}

public sealed class CovenantPreviewServiceResult<T> : IAsyncDisposable
{
    public Result<T> Result { get; }

    public CovenantPreviewResponseOwnership Ownership { get; }

    public Result<ICovenantOperationLease> TakeLease();
}
```

`CovenantTurnStateRequest` contains the immutable `ArcanumInvocationContext` and one closed execution-fact arm: `Stateless`, `PendingClaim(SessionTurnClaimLease, SessionTurnInputPreflight)`, or `Begun(SessionTurnClaimLease, AssistantReplyBeginReceipt, bool mutationToolEligible, CovenantTurnLease? transferredMaintenanceLease)`. The claim or begin transaction supplies the latest content-free preflight. The context provider does not accept an already materialized Session, title, summary, Entry, attachment, or exact label. It validates the invocation context separately by session-backed execution surface and exact canonical Campaign binding because that context carries no Session revision fields. It rejects a Session fact without the matching preflight, a placeholder without its matching claim, a post-insert history watermark used as pre-request evidence, a stale expected-current sensitivity revision, or collector eligibility without a real begun assistant placeholder. A transferred maintenance lease must be the sole lease, match Session and canonical Campaign scope plus dataset, capability, authority, and availability generations, and be detached exactly once; the provider must not acquire a second turn lease.

For a Session-backed request, inspect only the preflight before authority selection. Explicit `None` with a tainted preflight fails before any history query. Disabled untainted work uses `SessionTurnHistoryReadAuthority.VerifiedClean`; the input reader proves the projection and joined labels clean in its one history snapshot before materializing bytes. Disabled authorized tainted work and enabled current-Covenant work acquire or accept the generation-bound turn lease first, then pass that lease's read capability to `ISessionTurnInputReader.ReadHistorySnapshotAsync`. The returned `SessionTurnInputSnapshot` must match the preflight, claim, and begin receipt on Session ID, immutable binding, pre-request history revision, and latest expected-current sensitivity revision. A concurrent revision, label, or pointer change returns no history. `InferenceContextBuilder` consumes that immutable snapshot and cannot call `GetSessionAsync`, `LoadThreadAsync`, or any repository history method again.

`CovenantLogicalTurnState` validates the three closed authority shapes. `Unprotected` has no lease, Covenant snapshot, plan, collector, or optional Covenant dependency call and may carry only a reader-proven clean Session input. `ProtectedHistory` has one turn lease, one same-snapshot Session input, and persisted input sensitivity, but no current Covenant snapshot, plan, admission, or collector. `CurrentCovenant` has one turn lease plus the authorized Session input when Session-backed, one Covenant snapshot and plan, and may carry a collector only when otherwise eligible.

`CovenantPreviewStateRequest` contains the invocation context plus one closed preview execution-fact arm: `Stateless` or `SessionBound(SessionTurnInputPreflight)`. The authenticated Session-binding query produces the optional content-free preflight without title, summary, Entry, attachment, tool, or exact-label bytes. A Session-bound request can never omit it. `CovenantInspectionState` is a closed nonserializable union. `UnprotectedDisabled` contains no lease, Covenant snapshot, or plan and may carry only a reader-proven clean `SessionTurnInputSnapshot`. `ProtectedHistory` contains one read lease, one same-snapshot Session input, and persisted sensitivity but no current Covenant snapshot or plan. `CurrentCovenant` contains one read lease, optional authorized Session input, snapshot, and plan. Session-backed arms expose that already authorized immutable input to the preview renderer, which cannot reload history through a repository. Only a leased arm can detach a lease. `CreateManagementExplainStateAsync` always returns the `CurrentCovenant` arm or a typed failure. `CovenantLeasedServiceResult<T>` remains the always-leased transfer object used by management explain. `CovenantPreviewServiceResult<T>` has closed `Unleased` and `LeaseOwned` arms, accepts no nullable or default lease, transfers a lease atomically at most once only from `LeaseOwned`, and disposes an unclaimed lease. Neither transfer type is serializable. Plan 04's HTTP boundary handles both arms explicitly.

Exact test methods:

- `ExecutePromptAsync_DisabledDoesNotCallAvailabilityStoreOrLinker`
- `ExecutePromptAsync_NoContextDoesNotCallCovenantDependencies`
- `ExecutePromptAsync_LoadsOneSnapshotAndLinksOnce`
- `ExecutePromptAsync_RetryFallbackCompressionAndToolLoopReusePlan`
- `ExecutePromptAsync_SessionEligibleCreatesOneCollector`
- `ExecutePromptAsync_StatelessCanReadButCannotStage`
- `ExecutePromptAsync_SubagentNoneCannotReadOrStage`
- `ExecutePromptAsync_PathGenerationChangeAbortsBeforeNextDispatch`
- `CovenantContextProvider_PreservesAgentFrameworkLifecycleWithoutExperimentalPackageDependency`
- `CovenantLogicalTurnState_DisposesLeaseAfterBufferedStreamingCancellationAndDiscard`
- `CreateTurnStateAsync_RejectsMissingWrongSessionOrStaleSensitivityClaimAndPlaceholderFacts`
- `CreateTurnStateAsync_RejectsPostInsertHistoryRevisionAndWrongScopeOrStaleTransferredLease`
- `CreateTurnStateAsync_TaintedOrCurrentReadsNoHistoryBeforeTurnAuthority`
- `CreateTurnStateAsync_RevisionOrLabelRaceReturnsNoHistory`
- `InferenceContextBuilder_ConsumesAuthorizedInputSnapshotWithoutRepositoryReload`
- `CreatePreviewStateAsync_DisabledPerformsNoCovenantStoreCall`
- `CreatePreviewStateAsync_DisabledUntaintedReturnsAuthorityFreeArm`
- `CreatePreviewStateAsync_DisabledTaintedReturnsLeaseOwnedProtectedHistoryArm`
- `CreatePreviewStateAsync_TaintedSessionLoadsHistoryAndLabelsUnderReturnedReadLease`
- `CreateManagementExplainStateAsync_DisabledButHealthyLoadsPlanWithoutCollector`
- `CovenantInspectionState_TransfersReadLeaseExactlyOnceToResponse`
- `CovenantLeasedServiceResult_TransfersOrDisposesExactlyOnceAndIsAbsentFromJsonContexts`
- `CovenantPreviewServiceResult_UnleasedArmCannotProduceALease`
- `DisabledTaintedSession_ReadsNoCanonicalStore_HoldsTurnLease_AndReceiptsDispatch`
- `DisabledTaintedSession_EmitsNoCurrentPlanDigestButPreservesGenerationProvenance`

- [ ] **Step 1: Add failing dependency-count and eligibility tests**

```csharp
[Fact]
public async Task ExecutePromptAsync_RetryFallbackCompressionAndToolLoopReusePlan()
{
    await provider.ExecutePromptAsync(
        request,
        InvocationContexts.AttendedSession(),
        CancellationToken.None);

    Assert.Equal(1, covenantStore.ReadSnapshotCount);
    Assert.Equal(1, covenantLinker.LinkCount);
    Assert.True(attempts.Count >= 4);
    Assert.Single(attempts.Select(attempt => attempt.PlanDigest).Distinct());
    Assert.Equal(attempts.Count, attempts.Select(attempt => attempt.AdmissionDigest).Distinct().Count());
}
```

- [ ] **Step 2: Run the focused Wizard integration tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~WizardIntelligenceProviderTests|FullyQualifiedName~WizardIntelligenceProviderFallbackTests|FullyQualifiedName~TurnEnginePlanIntegrationTests|FullyQualifiedName~InferenceContextBuilderTests|FullyQualifiedName~CovenantLeasedServiceResultTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because Wizard has no logical Covenant state and rebuild paths do not share a typed plan.

- [ ] **Step 3: Build logical turn state before prompt or tool work**

In `ExecutePromptCoreAsync`, pass only the real claim, assistant-begin receipt and its content-free preflight, invocation context, and eligibility facts into one `CovenantTurnStateRequest`. Do not call the existing history loader first. Explicit `None` with a tainted preflight fails before message materialization. Disabled or context-free untainted work returns an `Unprotected` state after the input reader proves the history clean in its one SQLite snapshot, without any gate or optional Covenant store call. Disabled but authorized tainted history acquires one generation-bound `CovenantTurnLease` before the history snapshot, returns `ProtectedHistory`, and never calls the canonical Covenant store or linker. Enabled current-Covenant work acquires one turn lease before the history snapshot, passes its `ICovenantSnapshotReadLease` capability to both `ReadHistorySnapshotAsync` and `ReadTurnSnapshotAsync`, calls `ICovenantLinker.Link` once, and returns `CurrentCovenant`; create a collector only for an attended session-backed operator turn whose request carries the matching begun assistant placeholder and mutation-tool eligibility. Keep the state from authorized history or Covenant snapshot creation through every Task 11 provider dispatch and Task 14 finalization or discard, and dispose it on every success, failure, disconnect, cancellation, and exception path only after the collector and final response are terminal. Keep attempt preparation and completion on the same state, mirroring Agent Framework's context-provider lifecycle through Arcanum-owned types.

`CreatePreviewStateAsync` owns the `/api/intelligence/context/inspect`, Prompt test, and Spell cast-preview path. It obeys the live feature switch, so a disabled untainted preview returns `UnprotectedDisabled` with zero Covenant store calls and no lease; a disabled tainted Session requires clean read authority and returns `ProtectedHistory` without reading canonical Covenant; an enabled authorized preview returns `CurrentCovenant`. It never creates a collector. `CreateManagementExplainStateAsync` is a separate authenticated memory-management loader that ignores only the live inference feature switch, requires clean read authority and a healthy canonical tier, remains available while disabled, and returns only the leased `CurrentCovenant` arm. A builder disposes its state locally or detaches a real lease exactly once to Plan 04's HTTP response boundary. Neither method is callable by live inference, subagent, apprentice, or unattended paths.

- [ ] **Step 4: Run the focused Wizard integration tests to verify green**

Run the Step 2 command.

Expected: PASS for dependency counts, eligibility, plan reuse, generation-change abort, disabled preview zero-call behavior, disabled management explain, and exact turn/read lease disposal across every terminal path.

- [ ] **Step 5: Refactor attempt inputs into one immutable seed**

Extend the existing `TurnContextSeed` or replace it with `CovenantLogicalTurnState` plus the current message seed. Remove the late Campaign path lookup from `BuildTurnContextAsync`. Rerun the Step 2 command.

Expected: PASS, and every attempt observes the same canonical context, snapshot digest, and plan digest.

## Task 16: Make preview and inspection use the live prompt functions without mutation authority

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IContextPreviewService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SpellExecutionEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantContextPreviewTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantContextPreviewEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderTests.cs`

**Interfaces:**

- Consumes: Task 15 context provider, Task 6 builder, Task 7 estimator/cache descriptors, and Task 8 preliminary admission.
- Produces: a fresh preview snapshot, plan, and non-dispatch admission projection with no collector or mutation tool, plus the final lease-owning `IContextPreviewService` contract:

```csharp
Task<Result<CovenantPreviewServiceResult<ContextPreviewResult>>> PreviewContextLeasedAsync(
    ContextPreviewRequest request,
    ArcanumInvocationContext invocationContext,
    CancellationToken cancellationToken);
```

This task removes the raw Task 3 `PreviewContextAsync` method after every direct caller migrates. Non-HTTP tests and callers dispose the closed preview result explicitly. HTTP routes serialize the `Unleased` arm normally and transfer the exact lease from the `LeaseOwned` arm to Plan 04's lease-bound response.

Exact test methods:

- `PreviewContextAsync_UsesFreshSnapshotPlanAndSharedRenderer`
- `PreviewContextAsync_MatchesLivePromptBytesForSameInputs`
- `PreviewContextAsync_ReportsConfirmedAndProposedTokensFromTypedSpans`
- `PreviewContextAsync_NeverCreatesCollectorOrCovenantTool`
- `PromptTestAndSpellCastPreview_UseCanonicalCampaignResolver`
- `PreviewContextAsync_TransfersReadLeaseThroughHttpSerializationAndResetDrain`

- [ ] **Step 1: Add failing live-versus-preview parity tests**

```csharp
[Fact]
public async Task PreviewContextAsync_MatchesLivePromptBytesForSameInputs()
{
    CovenantPreviewServiceResult<ContextPreviewResult> previewResult = (await provider.PreviewContextLeasedAsync(
        request,
        InvocationContexts.ContextInspection(),
        CancellationToken.None)).Value;
    ContextPreviewResult preview = previewResult.Result.Value;
    FrozenProviderCall live = await harness.BuildFirstLiveCallAsync(request);

    Assert.Equal(live.Envelope.SystemPrompt, preview.SystemPrompt);
    Assert.Equal(live.Envelope.PromptSpans, preview.AttributionSpans);
}
```

- [ ] **Step 2: Run the preview tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantContextPreviewTests|FullyQualifiedName~CovenantContextPreviewEndpointTests|FullyQualifiedName~SystemPromptBuilderTests"
```

Expected: FAIL because preview builds context through a separate path and cannot report Covenant typed spans.

- [ ] **Step 3: Share snapshot, linker, builder, tokenizer, and admission functions**

Create a fresh preview state through Task 15's feature-gated `CreatePreviewStateAsync` with `ArcanumExecutionSurface.ContextInspection`. Build the same preliminary call projection without freezing a dispatch receipt. Force mutation-tool eligibility false and never create a collector. An `UnprotectedDisabled` state returns the byte-identical absent-Covenant `ContextPreviewResult` in `CovenantPreviewServiceResult.Unleased` without a Covenant store call or lease. A `ProtectedHistory` or `CurrentCovenant` state returns its result and state-owned read lease in `CovenantPreviewServiceResult.LeaseOwned` for the Plan 04 HTTP boundary. A non-HTTP caller disposes the closed result directly. Authenticated memory explain uses the separate always-leased management loader owned by Plan 04. Add a reset barrier proving a real preview lease drains only after buffered JSON serialization completes, plus an unleased disabled case proving reset has no preview reader to drain.

- [ ] **Step 4: Run the preview tests to verify green**

Run the Step 2 command.

Expected: PASS for prompt bytes, ordering, token attribution, and zero mutation capability.

- [ ] **Step 5: Refactor Prompt and Spell previews onto the same adapter**

Keep HTTP authentication and response DTO work in Plan 04. Make the internal Prompt test and Spell cast-preview handlers consume canonical context and the shared preview service. Rerun the Step 2 command.

Expected: PASS for direct inspection, Prompt preview, and Spell preview parity.

## Task 17: Add typed MCP output schemas and structured results

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/Protocol/McpWireDtos.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/Protocol/JsonRpcModels.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.Schemas.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpWireDtosTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/JsonRpcModelsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs`

**Interfaces:**

- Consumes: hand-authored JSON schemas and source-generated `McpJsonSerializerContext`.
- Produces optional `outputSchema` on `McpToolDefinitionWire` and optional `structuredContent` on `McpToolsCallResultWire`.

Exact test methods:

- `ToolDefinition_WithOutputSchema_RoundTripsThroughSourceGeneratedContext`
- `ToolsCallResult_WithStructuredContent_RoundTripsThroughSourceGeneratedContext`
- `CovenantSchemas_OmitAuthorityScopeCampaignAndProvenanceFields`
- `CovenantResults_ContainCompactTextFallbackAndTypedStructuredContent`
- `McpJsonSerializerContext_ContainsEveryCovenantWireType`

- [ ] **Step 1: Add failing literal-wire tests**

Assert exact JSON property names because MCP wire DTOs are an allowed `JsonPropertyName` exception.

```csharp
[Fact]
public void CovenantSchemas_OmitAuthorityScopeCampaignAndProvenanceFields()
{
    JsonElement schema = CovenantToolSchemas.Propose;

    Assert.Equal(new[] { "key", "content" }, RequiredPropertyNames(schema));
    Assert.DoesNotContain("campaignId", AllPropertyNames(schema));
    Assert.DoesNotContain("attachmentIds", AllPropertyNames(schema));
}
```

- [ ] **Step 2: Run the MCP wire tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~McpWireDtosTests|FullyQualifiedName~JsonRpcModelsTests|FullyQualifiedName~ArcanumInternalToolServerTests"
```

Expected: FAIL because wire DTOs lack `outputSchema` and `structuredContent` and no Covenant schemas are registered.

- [ ] **Step 3: Add named wire DTOs and hand-authored schemas**

Add optional fields without anonymous payloads. Register the exact argument and result records in `McpJsonSerializerContext`. Keep `propose_covenant` input to `key` and `content`. Keep `retire_covenant` input to `key` and `lane`. Define compact success and expected-failure results with no raw provenance content.

- [ ] **Step 4: Run the MCP wire tests to verify green**

Run the Step 2 command.

Expected: PASS for exact JSON, schema omission, text fallback, and structured content.

- [ ] **Step 5: Refactor schema constants into one static partial**

Keep each schema as a static UTF-8 literal or source-generated DTO projection. Do not introduce runtime schema discovery. Rerun the Step 2 command.

Expected: PASS under source-generated serialization only.

## Task 18: Bridge a single-use Covenant MCP capability by request identity and nonce

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContext.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolInvocationBindingStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolInvocationRegistration.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InProcessMcpTransport.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ChannelClientTransport.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolInvocationBindingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/InProcessMcpTransportTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ExpiringRequestBindingStoreTests.cs`

**Interfaces:**

- Consumes: exact connection ID, request ID, random 128-bit nonce, Task 13 collector, canonical Campaign, base plan, producing admission, and call materialization. The context has an optional opaque retirement-authorization slot, but this task neither creates nor infers that authority.
- Produces:

```csharp
public interface ICovenantToolInvocationBindingStore
{
    Result<CovenantToolInvocationRegistration> TryRegister(
        McpRequestIdentity request,
        CovenantCapabilityNonce nonce,
        CovenantToolInvocationContext context);

    Result<CovenantToolInvocationUseLease> TryTake(
        McpRequestIdentity request,
        CovenantCapabilityNonce nonce);

    ValueTask DisposeAsync(
        CovenantToolInvocationRegistration registration);
}
```

Exact test methods:

- `TryRegister_DuplicateConnectionAndRequestIdCannotOverwrite`
- `TryTake_RequiresReferenceRegistrationAndExactNonce`
- `Dispose_DelayedCleanupCannotRemoveReusedRequestId`
- `TtlSweep_RemovesOnlyRegisteredNeverTakenValues`
- `UseLease_ResumeAfterClosingFailsBeforeStaging`
- `ConcurrentRequests_CannotObserveAnotherTurnContext`
- `SendSerializationOrCancellation_DisposesExactRegistration`

- [ ] **Step 1: Add failing registration-state and ABA tests**

```csharp
[Fact]
public async Task Dispose_DelayedCleanupCannotRemoveReusedRequestId()
{
    CovenantToolInvocationRegistration first = Register(identity, nonce1);
    await bindings.DisposeAsync(first);
    CovenantToolInvocationRegistration second = Register(identity, nonce2);

    await bindings.DisposeAsync(first);

    Assert.True(bindings.TryTake(identity, nonce2).IsSuccess);
}
```

- [ ] **Step 2: Run the binding tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantToolInvocationBindingTests|FullyQualifiedName~InProcessMcpTransportTests|FullyQualifiedName~ExpiringRequestBindingStoreTests"
```

Expected: FAIL because the existing ambient bridge permits overwrite-prone request dictionaries and has no nonce, take state, or use-lease drain.

- [ ] **Step 3: Implement `Registered -> Taken -> Closing -> Disposed`**

Use atomic `TryAdd` for registration and key-plus-reference conditional removal. Keep request IDs reserved through handler disposal. Let TTL remove only `Registered`. On disposal, reject new leases, cancel suspended leases, drain active uses, then remove the exact registration. Recheck nonce, state, collector generation, branch admission, and cancellation after every awaited authority operation.

- [ ] **Step 4: Run the binding tests to verify green**

Run the Step 2 command.

Expected: PASS for duplicate, ABA, TTL, cancellation, child-task, and cross-turn isolation cases.

- [ ] **Step 5: Refactor `SessionAttachmentAmbientSend` into explicit binding composition**

Keep its existing attachment binding and add the Covenant registration as a separately disposed component. Ensure `BindSdkToolsCall`, `ApplyAmbientBinding`, `BindRequestContexts`, and `UnbindRequestContexts` restore only their exact request. Rerun the Step 2 command.

Expected: PASS with no standalone `AsyncLocal` granting Covenant authority.

## Task 19: Stage `propose_covenant` and admitted `retire_covenant` through inert internal handlers

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantRetirementAuthorizationIssuer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/UnavailableCovenantRetirementAuthorizationIssuer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.Registry.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.Schemas.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantInternalToolTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerBootstrapIdempotencyTests.cs`

**Interfaces:**

- Consumes: Task 18 capability lease, Task 13 collector, Plan 02 compiler/head probe, and the producing admission and materialization snapshot.
- Produces `ICovenantRetirementAuthorizationIssuer`, a fail-closed default issuer, `ProposeCovenantResult`, and `RetireCovenantResult` structured content plus compact text fallback. Focused retirement tests inject an exact deterministic issuer; Task 21 replaces the default with the live Ward-bound implementation.

Exact test methods:

- `ListTools_DisabledOrUnavailableFiltersCachedCovenantHandlers`
- `DirectInvoke_HandlerRechecksLiveAvailabilityAndInvocationPolicy`
- `Propose_BindsServerOwnedCampaignLaneOriginAndProvenance`
- `Propose_UnprovenancedOrMoreThanSixtyFourSourcesFailsClosed`
- `Propose_AbsentPlanKeyUsesOneBoundedHeadProbe`
- `Propose_EmptyCurrentAdmissionStillBindsProducingReceiptAndAllowsBoundedHeadProbe`
- `Propose_RetiredLaneCannotReactivate`
- `Retire_RequiresExactMateriallyAdmittedAttemptTarget`
- `Retire_GlobalPressuredReviewOnlyOrDifferentBranchTargetIsIneligible`
- `BothTools_ExactReplayReturnsOriginalRedactedReceipt`

- [ ] **Step 1: Add failing live-filter and staging tests**

```csharp
[Fact]
public async Task Propose_BindsServerOwnedCampaignLaneOriginAndProvenance()
{
    ProposeCovenantResult result = await server.InvokeProposeAsync(
        capability,
        new("response.style", "Prefer concise summaries."),
        CancellationToken.None);

    CovenantMutationIntent intent = collector.SingleIntent;
    Assert.Equal(capability.Campaign.CampaignId, intent.CampaignId);
    Assert.Equal(CovenantLane.Proposed, intent.Lane);
    Assert.Equal(CovenantOrigin.AgentProposed, intent.Origin);
    Assert.Equal(capability.Materialization.Digest, intent.MaterializationDigest);
    Assert.Equal(CovenantStagedOutcome.Staged, result.Outcome);
}
```

- [ ] **Step 2: Run the focused internal-tool tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantInternalToolTests|FullyQualifiedName~ArcanumInternalToolServerTests|FullyQualifiedName~McpConnectionManagerBootstrapIdempotencyTests"
```

Expected: FAIL because Covenant handlers, live filtering, exact-attempt authority, and structured staging receipts do not exist.

- [ ] **Step 3: Add inert handlers and final per-turn filtering**

Always register inert handler definitions in the cached internal superset. Filter before provider freeze using live feature state, availability generation, invocation context, tool policy, and collector presence. Recheck all facts inside each handler. Derive Campaign, lane, origin, expected revision, plan and admission bindings, and provenance from the capability, never from arguments. Every current-Covenant provider attempt supplies its real admission receipt, including an empty selected vector, so a proposal for an absent plan key can bind the producing attempt before its one bounded head probe. `retire_covenant` calls `ICovenantRetirementAuthorizationIssuer`; the default implementation always returns a typed unavailable result, while tests inject exact admitted-target authorization.

- [ ] **Step 4: Run the focused internal-tool tests to verify green**

Run the Step 2 command.

Expected: PASS for filtering, direct stale invocation, proposal, retirement, replay, and expected structured failures.

- [ ] **Step 5: Refactor mutation staging through one helper**

Share compiler, capacity, replay, target uniqueness, and `StageAsync` handling between the two handlers. Keep retirement-specific admission and Ward evidence mandatory. Rerun the Step 2 command.

Expected: PASS, with no direct canonical store write in either MCP handler.

## Task 20: Buffer fragmented provider tool calls privately until classification

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/SensitiveToolCallAssembler.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/ProviderToolBufferLimits.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEvent.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/Projections/IntelligenceEventProjection.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/Projections/OpenAiSseProjection.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/Projections/BufferedTurnProjection.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/Models/IntelligenceEvent.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionEvent.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SensitiveToolCallAssemblerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnEngineProjectionCharacterizationTests.cs`

**Interfaces:**

- Consumes: raw provider tool-call index, fragmented name bytes, fragmented argument bytes, and final metadata.
- Produces:

```csharp
public interface ISensitiveToolCallAssembler
{
    Result AppendName(int callIndex, ReadOnlySpan<byte> bytes);

    Result AppendArguments(int callIndex, ReadOnlySpan<byte> bytes);

    Result<FrozenToolCall> Complete(int callIndex, ProviderToolCallMetadata metadata);

    void Clear();
}
```

Exact test methods:

- `Complete_AssemblesArgumentsBeforeBetweenAndAfterNameFragments`
- `Complete_KeepsInterleavedIndexesIsolated`
- `Complete_AllowsUtf8CodePointAcrossFragments`
- `Complete_RejectsMalformedOrIncompleteUtf8`
- `Complete_RejectsDuplicateChangedNamePrefixAndReusedIndex`
- `Append_EnforcesAllFourExactLimitsBeforeGrowth`
- `CovenantCall_EmitsNoPartialPublicOrDurablePayload`
- `OrdinaryCall_ReleasesProjectionOnlyAfterClassification`

- [ ] **Step 1: Add failing adversarial fragment and projection tests**

```csharp
[Fact]
public void Append_EnforcesAllFourExactLimitsBeforeGrowth()
{
    SensitiveToolCallAssembler assembler = new();

    Result result = assembler.AppendArguments(
        0,
        new byte[ProviderToolBufferLimits.PerCallArgumentBytes + 1]);

    Assert.False(result.IsSuccess);
    Assert.Equal(ErrorCodes.Hub.ProviderToolBufferExceeded, result.Error.Code);
    Assert.Equal(0, assembler.BufferedBytes);
}
```

- [ ] **Step 2: Run the focused streaming tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SensitiveToolCallAssemblerTests|FullyQualifiedName~TurnEngineProjectionCharacterizationTests"
```

Expected: FAIL because Wizard emits `ToolCall` with complete arguments before sensitive classification and has no fragmented-call limits.

- [ ] **Step 3: Implement checked private buffering and strict incremental UTF-8**

Charge raw bytes before allocation. Isolate each index. Validate one stable final name and metadata. Clear every buffer on any limit or identity failure. Classify only the frozen exact call. Release ordinary arguments after classification. Keep Covenant arguments private and emit only the compact staged receipt after successful staging.

- [ ] **Step 4: Run the focused streaming tests to verify green**

Run the Step 2 command.

Expected: PASS for fragmentation, UTF-8, identity tricks, all limits, and zero sensitive projection.

- [ ] **Step 5: Remove pre-classification `argsSnapshot` events**

Audit buffered response, SSE, progress, transcript, inference audit, and diagnostics projections. Rerun:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SensitiveToolCallAssemblerTests|FullyQualifiedName~TurnEngineProjectionCharacterizationTests|FullyQualifiedName~WizardIntelligenceProviderTests"
```

Expected: PASS, with Covenant key and content absent from every public or generic durable tool event.

## Task 21: Apply dynamic sensitive-egress Ward policy and receipt-first effects

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ToolRiskClassifier.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ToolRiskContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ICovenantSensitiveNetworkDispatcher.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantRetirementPreflight.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/CovenantSensitiveNetworkDispatcher.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumWebSearchTool.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Security/IWard.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolRiskClassifierTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPolicyTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolExecutionPipelinePathPreflightTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolExecutionObserverTimingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantSensitiveNetworkDispatcherTests.cs`

**Interfaces:**

- Consumes: final frozen tool name and canonical arguments, input sensitivity, destination class and identity, canonical Campaign context, producing admission, Task 11 disclosure store, Task 19 `ICovenantRetirementAuthorizationIssuer`, Task 2 Ward-retirement envelope purpose, and current authority epoch.
- Produces:

```csharp
public sealed record ToolRiskContext(
    string ToolName,
    CovenantDigest FinalArgumentDigest,
    ContentSensitivity Sensitivity,
    CovenantEgressDestination Destination,
    CovenantDigest DestinationIdentity,
    InvocationAttendance Attendance);

public static ToolRiskDecision Classify(ToolRiskContext context);
```

Exact test methods:

- `Classify_TaintedExternalOrPersistentSinkIsCovenantSensitiveEgress`
- `Classify_TaintedTrustedLocalReadOnlyToolRemainsOrdinary`
- `Execute_CovenantSensitiveEgressRejectsHeadlessAndConfiguredAutoApproval`
- `Execute_CovenantSensitiveEgressPersistsWardAndDisclosureBeforeSideEffect`
- `Execute_DisclosureCommitFailurePreventsSideEffect`
- `Retire_PreflightShowsExactAdmittedTargetAndGlobalFallbackEffect`
- `Retire_ConfiguredAutoApprovalRequiresExplicitAllowlist`
- `Execute_PathRevisionChangeFailsBeforeWorkspaceEffect`
- `Execute_ReadOnlyExternalMcpOrNetworkGetStillRequiresSensitiveEgressWard`
- `Network_CrossOriginRedirectRequiresFreshAttendedWardAndNeverForwardsCredentials`
- `Network_SameOriginRedirectReceiptsEveryHopBeforeBytesAndCountsEachEffect`
- `Network_DnsRebindingOrPrivateAddressChangeFailsBeforeConnect`
- `ReceiptReuse_RequiresSameOpenSubjectAuthorityAndEveryFrozenEffectField`

- [ ] **Step 1: Add failing risk, order, and retirement-preflight tests**

```csharp
[Fact]
public async Task Execute_CovenantSensitiveEgressPersistsWardAndDisclosureBeforeSideEffect()
{
    await pipeline.ExecuteAsync(taintedExternalCall, attendedContext, CancellationToken.None);

    Assert.Equal(
        new[] { "ward-approved", "disclosure-ack", "side-effect" },
        observer.Events);
}
```

- [ ] **Step 2: Run the focused Ward suites and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~WardAutoApprovalPolicyTests|FullyQualifiedName~WardAutoApprovalPipelineTests|FullyQualifiedName~ToolExecutionPipelinePathPreflightTests|FullyQualifiedName~ToolExecutionObserverTimingTests|FullyQualifiedName~CovenantSensitiveNetworkDispatcherTests"
```

Expected: FAIL because risk is based mainly on static tool names, configured auto-approval can precede sensitivity escalation, and side effects lack receipt-first ordering.

- [ ] **Step 3: Classify final arguments and acknowledge effects before dispatch**

Treat provider, network, process, external MCP, message, and content-bearing persistent destinations as `CovenantSensitiveEgress` when sensitivity is derived. Require attended interactive Ward, then persist Ward evidence and the exact physical-effect disclosure before dispatch. Reopen workspace targets with no-follow handles and recheck canonical root identity and revision. Implement and register the live `ICovenantRetirementAuthorizationIssuer`. Build retirement preflight from the exact admitted version, seal it with Task 2's Ward-retirement purpose, and bind its single-use token and receipt evidence into the capability.

For tainted HTTP, resolve and validate the destination at each actual request hop, append and acknowledge a destination-bound disclosure before sending bytes, and count every hop as one external effect. Same-origin redirects repeat the check and receipt. Cross-origin redirects are denied by default and require a fresh attended Ward when explicitly supported; credentials and sensitive headers never cross origins. DNS rebinding, address-class change, redirect loop, authority change, receipt failure, or cancellation stops before connect. Semantically read-only external MCP, process, network, or message calls remain egress. An acknowledged receipt can be reused only after indexed proof of the same open subject, authority generation, destination, sensitivity, Ward/admission evidence, and every frozen effect field.

- [ ] **Step 4: Run the focused Ward suites to verify green**

Run the Step 2 command.

Expected: PASS, with zero side effects after denial, cancellation, authority change, receipt failure, or path revision change.

- [ ] **Step 5: Refactor `ToolExecutionPipeline.TurnContext` into immutable authority inputs**

Replace Campaign ID and working-directory-derived policy fields with the same `CanonicalCampaignContext` and invocation context used by the turn. Keep `retire_covenant` intrinsic Forbidden Art behavior separate from dynamic sensitive egress. Rerun the Step 2 command.

Expected: PASS for ordinary Wards, configured retirement auto-approval, and attended sensitive egress.

## Task 22: Carry sensitivity through provider calls, branches, events, and final receipts

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ModelTokenizationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/ITurnEventSource.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEvent.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/Projections/BufferedTurnProjection.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/Projections/OpenAiSseProjection.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantSensitivityPropagationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs`

**Interfaces:**

- Consumes: Task 9's required call-sensitivity calculator and Plan 02's max/merge lattice, every input message or artifact label, and current plan/admission evidence.
- Produces required sensitivity on `ModelCallContext`, `ProviderAttemptResult`, `TurnEvent`, and `CovenantFinalReceipt`.

Exact test methods:

- `ProviderCall_CurrentAdmissionAddsCampaignPlanEvidenceToAttemptEvidence`
- `RetryCompressionToolResultAndFallbackRemainTainted`
- `BranchMerge_IsAssociativeCommutativeAndIdempotent`
- `LaterTurnWithEmptyPlanCannotLaunderTaintedHistory`
- `ProviderStream_ExposesSensitivityBeforeFirstEvent`
- `FinalReceipt_ContainsMaximumCommittedBranchSensitivity`

- [ ] **Step 1: Add failing cross-call and cross-branch propagation tests**

```csharp
[Fact]
public async Task LaterTurnWithEmptyPlanCannotLaunderTaintedHistory()
{
    FrozenProviderCall call = await harness.BuildContinuationAsync(
        sessionWithTaintedAssistant,
        emptyCovenantPlan);

    Assert.Equal(ContentSensitivity.CovenantDerived, call.Envelope.Sensitivity.Level);
    Assert.Contains(sourceGeneration, call.Envelope.Sensitivity.Provenance);
    Assert.Null(call.Envelope.CurrentCovenantPlanDigest);
}
```

- [ ] **Step 2: Run the focused propagation tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSensitivityPropagationTests|FullyQualifiedName~WizardIntelligenceProviderFallbackTests"
```

Expected: FAIL because provider calls and turn projections lack required sensitivity and branch provenance.

- [ ] **Step 3: Propagate the already-required call sensitivity at every branch boundary**

Invoke Task 9's calculator for every retry, compression, tool-loop, fallback, and later-turn call, then preserve its result in every continuation output and branch merge. Add current Campaign and plan evidence to attempt evidence only when the current call admits Covenant; add the admission digest only after Task 11 finalizes it, so no provider-call digest is circular. Attach a non-serializable stream sensitivity feature before yielding the first event.

- [ ] **Step 4: Run the focused propagation tests to verify green**

Run the Step 2 command.

Expected: PASS for current admission, inherited taint, retry, compression, fallback, branch merge, and first-event ordering.

- [ ] **Step 5: Refactor sensitivity into required constructor parameters**

Remove default `ContentSensitivity.None` from call, event, response, and final-receipt constructors used by live inference. Test fixtures must state their sensitivity explicitly. Rerun the Step 2 command.

Expected: PASS, and new call paths cannot silently omit a label.

## Task 23: Propagate or refuse sensitivity at every derived-output consumer

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionSummaryArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionTitleArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILabeledConversationArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILabeledSagaArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILabeledLexiconArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILabeledIdempotencyArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILabeledToolOutputArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ILoreArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IWorkspaceContextArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/CleanOriginArtifactWriteContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/ISensitivityMetadataAuditSink.cs`
- Create: `src/RetroDownfall.Arcanum.Core/CommLink/ISensitivityAwareCommLinkDispatcher.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IProtectedAssistantArtifactReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ISessionContextPinMaterializationReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/SessionContextPinMaterializationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/DerivedArtifactWriteContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/ManagedFileOwnershipContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Serialization/ManagedFilePersistenceJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Logging/CovenantProtectedLogScope.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Serialization/LongRunningOperationJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Serialization/InferenceAccountingJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IMaintenanceArtifactCommitter.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IMaintenanceInputReader.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/MaintenanceInputContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionSummaryArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionTitleArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/LabeledConversationArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/LoreArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/WorkspaceContextArtifactStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/MaintenanceArtifactCommitter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/MaintenanceInputReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedAssistantArtifactReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionContextPinMaterializationReader.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/IGrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/ISessionContextPinStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/TheForge/ISessionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/TheForge/IApprenticeRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/ISessionAttachmentStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/IIdempotencyStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/IIdempotencyClaimStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IInferenceAuditLogger.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/Models/InferenceAuditRecord.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/IGuardrailAuditLogger.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/Models/GuardrailAuditRecord.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/CommLink/ICommLinkDispatcher.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/Tapestry/ITapestryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/ISanctumBreachRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Logging/LogEntry.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/AttachmentMemoryProvenance.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/ITurnRunWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/IUnseenServantJobTracker.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/UnseenServantJobTracker.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/UnseenServantService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SessionEventHub.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ApprenticeRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/SessionContextPinMaterializer.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Subagents/SubagentContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumDelegateTaskTool.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/LexiconEntityExtractor.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Subagents/SubagentRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Daemons/DaemonEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/ApprenticeEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/LoreEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Operations/LongRunningOperationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/OperationCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Security/IdempotencyEndpointFilters.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestrySummarizer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryWeaver.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexProcessor.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionContextPinStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingLedger.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/DeferredBackupOperationServices.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Storage/BlobEncryptionLifecycleService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Chronosync/ChronosyncEngine.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/AttachmentSourceResolver.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/CommandOutputArtifactStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CommunicationTools.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.FileTools.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.ApplyPatch.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ApplyPatchToolExecutionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/SandboxedFileIo.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/MultiFileCommitCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/InferenceAuditLogger.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/GuardrailAuditLogger.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/ILogRingBuffer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/InMemoryLogRingBuffer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/SerilogLogRingBufferSink.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/ILogQueryService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/LogQueryService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/LoggingBootstrapper.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Logging/CovenantSanitizingLogSink.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/CommLink/CommLinkMultiplexer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/CommLink/WebhookCommLinkDispatcher.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2APushNotifications.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/ArcanumA2AAgentHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/IA2AClientService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AClientService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/ArcanumA2ATaskStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingRecoveryHandlers.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Daemons/IDaemonExecutionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Daemons/InMemoryDaemonExecutionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Daemons/DaemonRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/InferenceRunRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/SubagentRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingAmbient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMaintenanceAuthorityContext.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/IManagedSensitiveFileWriter.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ManagedSensitiveFileContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ManagedFileOwnershipEvidence.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantMaintenanceCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedSensitiveFileWriter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileOpenContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileCapabilityMint.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileHandleOperationKernel.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/IManagedFileCapabilityOpener.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/IManagedFileOwnershipVerifier.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/IManagedFileCreatedChildWriter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileOwnershipVerifier.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/IManagedFileCreatedChildRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileCapabilityOpener.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileWriteIntentRecoveryService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/IFileSystemWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/PhysicalFileSystemWriter.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantDerivedOutputInventoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/CovenantMaintenanceCoordinatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/LoremasterSensitivityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/LexiconEntityExtractorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/BudgetMonitorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DaemonInitiativeEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ApplyPatchToolTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/MultiFileCommitCoordinatorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/SandboxedFileIoTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaInstallerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/EntryWeavingServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/WorkspaceIndexingServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestryStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestryWeaverTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestrySummarizerSensitivityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/DivinationServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/SessionRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/DerivedArtifactStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ProtectedAssistantArtifactReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SessionContextPinMaterializerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SessionContextPinStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/AttachmentMemoryProvenanceStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/MaintenanceArtifactCommitterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/MaintenanceInputReaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/SessionEventHubTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireTurnWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ApprenticeRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/ApprenticeRepositoryConcurrencyTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/InferenceAuditLoggerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/GuardrailAuditLoggerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/Guardrails/GuardrailsPipelineTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/CommLink/CommLinkMultiplexerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2APushNotificationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2AServerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2AClientTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2AOutboundHttpClientTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2AParkedSendingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingLedgerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/CommLink/WebhookCommLinkDispatcherTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/IdempotencyClaimStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/IdempotencyStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/IdempotencyEndpointFilterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SagaMemoryStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SessionAttachmentStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/AttachmentSourceResolverTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/WebWorkflowEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SubagentRunnerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolRiskClassifierTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/ApprenticeServiceReliabilityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/UnseenServantJobTrackerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/UnseenServantServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/SanctumGuardTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SanctumBreachRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/InMemoryLogRingBufferTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/SerilogLogRingBufferSinkTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/LogQueryServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/LogsEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Logging/LoggingBootstrapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Daemons/DaemonRunnerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Daemons/InMemoryDaemonExecutionRepositoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/LongRunningOperationEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationProgressContractsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRequestedIdentityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/OperationCommandsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/BlobEncryptionLifecycleServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/FakeLongRunningOperationStore.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupCreateRecoveryHandlerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionAgeBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionDaemonHistoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionQuarantineRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/DurableOperationDiagnosticsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/TurnRunWriterRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/InferenceAccountingStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/BudgetReservationServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/RecoveryHandlerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/RecoveryHandlerFakes.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnAccountingHandleTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/BatchProcessingServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WeaveServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/LoreEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Chronosync/ChronosyncEngineTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/WorkspacesEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCoreArtifactSchemaTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Workspaces/ManagedSensitiveFileWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Workspaces/ManagedFileOwnershipVerifierTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/ManagedFileOwnershipContractsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Workspaces/ManagedFileCapabilityOpenerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/ManagedFileWriteIntentRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Workspaces/PhysicalFileSystemWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Task 12 pending claim and maintenance checkpoints, Task 22 sensitivity, Plan 02 artifact-label and protected-partition persistence, and Plan 01's exact `ManagedFileWriteIntentPhase`, `LocalErasureWorkItemState`, and `LocalErasureDeletionEvidenceCode` persistence contracts.
- Produces request-bound maintenance plus a crash-recoverable managed-file writer, exact Core `ManagedFileDurableLocationEvidence`, `ManagedFileWriteDurableLocationEvidence`, and `ManagedFileOwnershipEvidence` values, and one internal Infrastructure capability family whose opener, handle, verifier, writer lifecycle, and recovery lifecycle are shared by writer recovery and Plan 04 erasure:

```csharp
public sealed class CovenantMaintenanceAuthorityContext : IAsyncDisposable
{
    public Guid SessionId { get; }

    public Guid PendingClaimId { get; }

    public long HistoryWatermark { get; }

    public ArtifactSensitivityLabel Sensitivity { get; }

    public CovenantTurnLease Lease { get; }

    public long AuthorityEpoch { get; }

    public long AvailabilityGeneration { get; }

    public ToolPolicy EffectiveToolPolicy => ToolPolicy.NoTools;

    public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken);

    public Result<CovenantTurnLease> DetachLeaseForTurnExecution();
}

public interface IMaintenanceArtifactCommitter
{
    Task<Result<MaintenanceArtifactCommitReceipt>> CommitAsync(
        MaintenanceArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface IMaintenanceInputReader
{
    Task<Result<MaintenanceInputSnapshot>> ReadAsync(
        CovenantMaintenanceAuthorityContext authority,
        MaintenanceInputRequest request,
        CancellationToken cancellationToken);
}

public interface ISessionSummaryArtifactStore
{
    Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionSummaryArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ISessionTitleArtifactStore
{
    Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionTitleArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ILabeledConversationArtifactStore
{
    Task<Result<LabeledArtifactWriteReceipt>> AppendTranscriptAsync(
        TranscriptArtifactWrite request,
        CancellationToken cancellationToken);

    Task<Result<LabeledArtifactWriteReceipt>> AppendToolArtifactAsync(
        ToolArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ILabeledSagaArtifactStore
{
    Task<Result<LabeledArtifactWriteReceipt>> WriteAsync(
        LabeledSagaArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ILabeledLexiconArtifactStore
{
    Task<Result<LabeledArtifactWriteReceipt>> WriteAsync(
        LabeledLexiconArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ILabeledIdempotencyArtifactStore
{
    Task<Result<LabeledArtifactWriteReceipt>> StoreAsync(
        LabeledIdempotencyArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ILabeledToolOutputArtifactStore
{
    Task<Result<LabeledArtifactWriteReceipt>> StoreAsync(
        LabeledToolOutputArtifactWrite request,
        CancellationToken cancellationToken);
}

public interface ISensitivityMetadataAuditSink
{
    ValueTask<Result> WriteAsync(
        InferenceSensitivityAuditMetadata metadata,
        CancellationToken cancellationToken);
}

public interface ISensitivityAwareCommLinkDispatcher
{
    Task<Result<DisclosureDispatchReceipt>> DispatchAsync(
        SensitivityAwareCommLinkRequest request,
        CancellationToken cancellationToken);
}

public interface IProtectedAssistantArtifactReader
{
    Task<Result<ProtectedAssistantArtifact>> ReadAsync(
        Guid sessionId,
        Guid assistantEntryId,
        ICovenantSnapshotReadLease lease,
        CancellationToken cancellationToken);
}

public interface ISessionContextPinMaterializationReader
{
    Task<Result<SessionContextPinMaterializationSnapshot>> ReadAsync(
        SessionContextPinMaterializationRequest request,
        SessionTurnHistoryReadAuthority authority,
        CancellationToken cancellationToken);
}

public sealed record ManagedFileExpectedEvidence(
    CovenantDigest FullContentHash,
    ulong ContentLength);

public sealed record ManagedFileOwnershipEvidence(
    CovenantDigest PhysicalIdentityDigest,
    CovenantDigest FullContentHash,
    ulong ContentLength);

public sealed record ManagedFileDurableLocationEvidence(
    CovenantDigest CanonicalRootIdentityDigest,
    long PathRevision,
    ImmutableArray<string> NormalizedRelativeParentSegments,
    CovenantDigest ParentPhysicalIdentityDigest,
    string ChildLeaf);

public sealed record ManagedFileWriteDurableLocationEvidence(
    ManagedFileDurableLocationEvidence Target,
    string TemporaryLeaf);

internal sealed class ManagedFileOpenHandle : IAsyncDisposable
{
    private readonly ManagedFileCapabilityMint producerMint;

    private readonly ManagedFileHandleOperationKernel operationKernel;

    private int disposeState;

    internal ManagedFileOpenHandle(
        ManagedFileCapabilityMint producerMint,
        ManagedFileHandleOperationKernel operationKernel)
    {
        this.producerMint = producerMint ?? throw new ArgumentNullException(nameof(producerMint));
        this.operationKernel = operationKernel ?? throw new ArgumentNullException(nameof(operationKernel));
        operationKernel.AssertOwnedBy(producerMint);
    }

    private ManagedFileHandleOperationKernel GetActiveKernel(
        ManagedFileCapabilityMint expectedProducerMint)
    {
        if (!ReferenceEquals(expectedProducerMint, producerMint))
        {
            throw new InvalidOperationException("The managed-file handle belongs to another producer mint.");
        }

        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposeState) != 0,
            this);

        operationKernel.AssertOwnedBy(expectedProducerMint);
        return operationKernel;
    }

    internal ValueTask<Result<CovenantDigest>> ObservePhysicalIdentityAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).ObservePhysicalIdentityAsync(cancellationToken);

    internal ValueTask<Result> WriteAllAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).WriteAllAsync(content, cancellationToken);

    internal ValueTask<Result> FlushChildToDiskAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).FlushChildToDiskAsync(cancellationToken);

    internal ValueTask<Result<ManagedFileOwnershipEvidence>> VerifyAndAdoptAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
        ManagedFileExpectedEvidence expectedContent,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).VerifyAndAdoptAsync(
            expectedCreatedChildPhysicalIdentityDigest,
            expectedContent,
            cancellationToken);

    internal ValueTask<Result<ManagedFileVerification>> VerifyCurrentAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).VerifyCurrentAsync(expected, cancellationToken);

    internal ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).CompareDeleteAsync(expected, cancellationToken);

    internal ValueTask<Result<ManagedFileCreatedChildRenameResult>>
        RenameCreatedChildNoReplaceAsync(
            ManagedFileCapabilityMint expectedProducerMint,
            ManagedFileDurableLocationEvidence target,
            CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
            ManagedFileExpectedEvidence expectedContent,
            CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).RenameCreatedChildNoReplaceAsync(
            target,
            expectedCreatedChildPhysicalIdentityDigest,
            expectedContent,
            cancellationToken);

    internal ValueTask<Result> FlushRetainedParentAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).FlushRetainedParentAsync(cancellationToken);

    internal ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteCreatedChildAsync(
        ManagedFileCapabilityMint expectedProducerMint,
        CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
        CancellationToken cancellationToken) =>
        GetActiveKernel(expectedProducerMint).CompareDeleteCreatedChildAsync(
            expectedCreatedChildPhysicalIdentityDigest,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        await operationKernel.DisposeAsync().ConfigureAwait(false);
    }
}

internal abstract class ManagedFileOpenResult
{
    private protected ManagedFileOpenResult()
    {
    }

    public sealed class Absent : ManagedFileOpenResult
    {
        internal Absent()
        {
        }
    }

    public sealed class Opened : ManagedFileOpenResult
    {
        internal Opened(ManagedFileOpenHandle handle)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        }

        public ManagedFileOpenHandle Handle { get; }
    }
}

internal enum ManagedFileVerification : byte
{
    Match = 1,
    Mismatch = 2
}

internal enum ManagedFileCompareDeleteResult : byte
{
    Deleted = 1,
    Mismatch = 2
}

public interface IManagedSensitiveFileWriter
{
    ValueTask<Result<ManagedSensitiveFileWriteReceipt>> CreateNewAsync(
        CovenantToolInvocationUseLease invocationLease,
        ManagedSensitiveFileWriteRequest request,
        CancellationToken cancellationToken);
}

internal interface IManagedFileOwnershipVerifier
{
    ValueTask<Result<ManagedFileOwnershipEvidence>> VerifyAndAdoptAsync(
        ManagedFileOpenHandle handle,
        CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
        ManagedFileExpectedEvidence expected,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileVerification>> VerifyCurrentAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken);
}

internal interface IManagedFileCapabilityOpener
{
    ValueTask<Result<ManagedFileOpenHandle>> CreateTemporaryExclusiveNoFollowAsync(
        ManagedFileWriteDurableLocationEvidence location,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileOpenResult>> OpenNoFollowAsync(
        ManagedFileDurableLocationEvidence location,
        CancellationToken cancellationToken);
}

internal interface IManagedFileCreatedChildWriter
{
    ValueTask<Result<CovenantDigest>> ObserveCreatedChildPhysicalIdentityAsync(
        ManagedFileOpenHandle createdChildHandle,
        CancellationToken cancellationToken);

    ValueTask<Result> WriteAllAsync(
        ManagedFileOpenHandle createdChildHandle,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    ValueTask<Result> FlushCreatedChildToDiskAsync(
        ManagedFileOpenHandle createdChildHandle,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileCreatedChildRenameResult>>
        RenameCreatedChildNoReplaceAsync(
            ManagedFileOpenHandle temporaryHandle,
            ManagedFileDurableLocationEvidence target,
            CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
            ManagedFileExpectedEvidence expectedContent,
            CancellationToken cancellationToken);

    ValueTask<Result> FlushRetainedParentAsync(
        ManagedFileOpenHandle createdChildHandle,
        CancellationToken cancellationToken);
}

internal interface IManagedFileCreatedChildRecovery
{
    ValueTask<Result<CovenantDigest>> ObserveCreatedChildPhysicalIdentityAsync(
        ManagedFileOpenHandle createdChildHandle,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileCreatedChildRenameResult>>
        RenameCreatedChildNoReplaceAsync(
            ManagedFileOpenHandle temporaryHandle,
            ManagedFileDurableLocationEvidence target,
            CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
            ManagedFileExpectedEvidence expectedContent,
            CancellationToken cancellationToken);

    ValueTask<Result> FlushRetainedParentAsync(
        ManagedFileOpenHandle createdChildHandle,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileCompareDeleteResult>>
        CompareDeleteCreatedChildAsync(
            ManagedFileOpenHandle handle,
            CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
            CancellationToken cancellationToken);
}

internal enum ManagedFileCreatedChildRenameResult : byte
{
    RenamedNoReplace = 1,
    TargetAlreadyPresent = 2,
    Mismatch = 3
}
```

Task 23 owns the exact public progress contract so later lifecycle work consumes an already-green Core API:

```csharp
public enum LongRunningOperationProgressKind : ushort
{
    LegacyUnrecognized = 0,
    InferenceRun = 1,
    Subagent = 2,
    BudgetReservation = 3,
    Batch = 4,
    Apprentice = 5,
    AttachmentPromotion = 6,
    WorkspaceIndex = 7,
    IdempotencyClaim = 8,
    BlobEncryptionMigration = 9,
    BlobEncryptionKeyRotation = 10,
    BackupCreate = 11,
    DataRetentionPrune = 12,
    DataRetentionMutation = 13,
    DataRetentionFactoryReset = 14,
    A2AInboundSending = 15,
    A2AOutboundSending = 16,
    CovenantIndexRebuild = 17,
    CovenantFamilyReinitialize = 18,
}

public enum LongRunningOperationProgressPhase : byte
{
    Pending = 1,
    Running = 2,
    Waiting = 3,
    Applying = 4,
    Verifying = 5,
    CleaningUp = 6,
    Completed = 7,
    Failed = 8,
    Abandoned = 9,
    ReconciliationRequired = 10,
}

public enum LongRunningOperationProgressBlocker : byte
{
    None = 0,
    LeaseUnavailable = 1,
    CapacityUnavailable = 2,
    ExternalDependency = 3,
    CancellationRequested = 4,
    AuthorityUnavailable = 5,
    IntegrityFailure = 6,
    OperatorActionRequired = 7,
}

public sealed record LongRunningOperationPublicProgress(
    byte Version,
    LongRunningOperationProgressKind Kind,
    LongRunningOperationProgressPhase Phase,
    ulong CompletedCount,
    ulong? TotalCount,
    LongRunningOperationProgressBlocker Blocker,
    CovenantDiagnosticTag? EvidenceTag);

public sealed record LongRunningOperationCreateRequest(
    string Kind,
    LongRunningOperationRecoveryPolicy RecoveryPolicy,
    LongRunningOperationPublicProgress PublicProgress,
    DateTimeOffset CreatedAt,
    Guid? RequestedOperationId = null,
    CovenantDigest? ApplyRequestDigest = null,
    CovenantDigest? EffectDigest = null,
    Guid? RootOperationId = null,
    Guid? ParentOperationId = null,
    Guid? SessionId = null,
    Guid? RunId = null,
    Guid? InferenceRunId = null,
    Guid? BudgetReservationId = null,
    Guid? IdempotencyClaimId = null);

public interface ILongRunningOperationStore
{
    Task<Result<LongRunningOperation>> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<LongRunningOperation?>> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        LongRunningOperationPublicProgress publicProgress,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}

public interface ILongRunningOperationCoordinator
{
    Task<Result<LongRunningOperationLeaseResult>> StartAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> CheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        LongRunningOperationPublicProgress publicProgress,
        CancellationToken cancellationToken = default);
}
```

`LongRunningOperationPublicProgress.Version` must equal one. Counts are at most `Int64.MaxValue`, and a present total is at least the completed count. The closed phase and blocker matrix rejects a blocker on `Pending`, `Running`, `Applying`, `Verifying`, `CleaningUp`, or `Completed`; `Waiting`, `Failed`, `Abandoned`, and `ReconciliationRequired` may carry only an applicable closed blocker. `LegacyUnrecognized` is initializer-only and cannot be used for new work. `LongRunningOperationProgressKindCatalog` in `LongRunningOperationContracts.cs` owns the immutable one-to-one mapping between every currently registered `LongRunningOperationKinds` string and codes 1 through 16. Codes 17 and 18 are immutable reservations for the Plan 04 Covenant index-rebuild and family-reinitialize kinds and cannot be selected until that plan adds their kind constants and recovery descriptors. The recovery registry, progress catalog, and caller inventory must be exhaustive over every registered kind.

`LongRunningOperation`, `LongRunningOperationDto`, create, checkpoint, store, coordinator, endpoint, CLI, and every fake expose `PublicProgress`, never `PublicSummary` or `string publicSummary`. The existing physical `LongRunningOperations.PublicSummary` column remains only as a compatibility storage slot for the bounded source-generated V1 JSON. `LongRunningOperationJsonContext` is the sole durable encoder and decoder. `ArcanumJsonContext` explicitly registers the progress record, all three enums, and `CovenantDiagnosticTag` for the API boundary, and `OperationCommands` renders fixed local text from the enum and counts. Unknown JSON members, versions, enum codes, oversized counts, invalid tags, and a progress kind that does not match the row's registered kind fail closed before an operation is returned or recovered. `CoreGrimoireSchemaDataInitializer` replaces every legacy free-form summary with a safe V1 projection derived only from its known kind and durable state in the same core upgrade transaction. An unknown legacy kind becomes `LegacyUnrecognized` plus `ReconciliationRequired` without retaining or returning the old text. New arbitrary summary text can never enter the compatibility column.

The requested-operation fields are all null or all present. `LongRunningOperationStore` inserts the operation and Plan 01 `long_running_operation_request_identities` row in one transaction, compares both digests in fixed time, returns the existing operation for the same requested ID and digests, and returns `Security.IdempotencyConflict` for any mismatch. `DataRetentionApplyRequest` carries only an optional caller-owned requested operation ID. When present, `DataRetentionService` derives the stable apply-request and effect digests from the authenticated canonical data-retention plan through the planner's domain-separated digest methods and supplies the all-present triplet to `LongRunningOperationCreateRequest`; the HTTP or stopped-host caller never supplies digest bytes. `InstallationResetService` uses its already durable active-record operation ID for reset and factory-reset work. `DataRetentionService` stores that identity with the operation. `InstallationResetExistingGrimoire` resolves recoverable and completed operations only through the normalized identity row and exact effect digest, never through progress JSON, a plan-ID interpolation, or a scan of public text. Existing server-generated callers keep the all-null arm. Plan 04 consumes this contract and does not redefine it.

`SessionContextPinMaterializationRequest` contains the Task 14 content-free `SessionTurnInputPreflight`, ordered bounded pin IDs, and the canonical Campaign root identity and path revision. `SessionContextPinMaterializationSnapshot` contains ordered immutable materialization candidates, the exact label observed for every Entry, attachment, or managed-workspace source, and their maximum `ArtifactSensitivityLabel`. It carries no repository, DbContext, path opener, or deferred content callback. `SessionContextPinMaterializationReader` opens one SQLite snapshot, joins each pin to its source and exact label before materializing Entry or attachment bytes, and keeps the caller's `SessionTurnHistoryReadAuthority.Protected` lease live while it no-follow opens and verifies a managed workspace file. The `VerifiedClean` arm returns bytes only after the same snapshot proves every source clean. Missing labels, changed pin/source identity, a managed-file identity or path-revision change, an explicit-None request with a derived pin, and a background caller all return no materialized content. `SessionContextPinMaterializer` can use only this reader, and Task 9 merges the returned maximum label into the provider-call sensitivity before freeze.

`SessionContextPinArtifact` uses `RejectDerived`. Replace raw `ISessionContextPinStore.UpsertAsync(..., string targetIdentifier, string displayLabel, ...)` with one sealed `OperatorSessionContextPinWriteRequest` in `CleanOriginArtifactWriteContracts.cs`. It carries authenticated endpoint request identity, canonical Session and pin source identity, bounded target and display values, an unforgeable operator clean-origin proof, and explicit sensitivity `None`. Only `SessionEndpoints` can construct it. The store accepts no raw string overload, and model, tool, maintenance, apprentice, subagent, and background callers cannot persist a pin.

`AttachmentMemoryConsultation` uses `ProtectedReadAndPropagate`. `IAttachmentMemoryProvenanceStore.RecordConsultationsAsync` accepts a sealed label-bearing request and writes each consultation, exact source Entry and attachment identities, their labels, and producing evidence atomically. Its read accepts the live `CovenantMaintenanceAuthorityContext` or turn-history read authority, joins the consultation, source Entry, current attachment version, and exact labels in one SQLite snapshot before returning logical key or content-identity evidence, and returns their maximum label. `Loremaster` and every consumer merge that maximum into Task 9 call sensitivity. Missing labels, changed versions, explicit None, or a background call without matching authority returns no consultation bytes or identifiers.

`ManagedSensitiveFileWriteRequest` is a sealed nonserializable value containing a random operation ID, one validated `ManagedWorkspaceWriteTarget` with canonical root identity, path revision, and bounded normalized relative segments, copied exact bytes, expected full hash, required `ArtifactSensitivityLabel`, producing-evidence digest, Task 21 Ward-evidence digest, and the acknowledged matching `CovenantDisclosureReceipt`. `CreateNewAsync` accepts the exact still-live Task 18 `CovenantToolInvocationUseLease`, revalidates its Campaign, authority, collector, and cancellation state, and accepts only an absent target and one locally revocable managed-file disclosure whose frozen effect identity matches every request field. The interface has no overwrite, append, replace, or raw-path overload. Only this top-level port is registered; `ManagedFileWriteIntentStore` remains an internal transaction helper.

`ManagedFileExpectedEvidence` and `ManagedFileOwnershipEvidence` are immutable, nondefault, content-free values. Each digest is exactly 32 bytes and deep-copied. Content length uses checked `u64` and must equal the bounded request bytes. The ownership value adds the exact physical identity digest observed from the same final opened-file handle. `VerifyAndAdoptAsync` requires the independently persisted `CreatedChildPhysicalIdentityDigest` in addition to `ManagedFileExpectedEvidence`, compares all three facts from that same reopened target handle, and returns ownership only when its physical identity equals the created-child digest and its full hash and length equal the expected content. No constructor accepts a path, mutable buffer, live handle, or caller-supplied identity string.

`ManagedFilePersistenceJsonContext` is the sole Native-AOT-safe durable codec for `ManagedFileDurableLocationEvidence`, `ManagedFileWriteDurableLocationEvidence`, `ManagedFileExpectedEvidence`, `ManagedFileOwnershipEvidence`, and the pending full `ArtifactSensitivityLabel` projection. Its explicit source-generation inventory also contains `GenerationProvenance`, `ContentSensitivity`, every nested provenance discriminant, `ImmutableArray<Guid>`, and `ImmutableArray<string>`. No root type relies on transitive discovery. It uses strict unknown-member rejection, bounded canonical UTF-8, and no reflection fallback. The SQLCipher columns remain independently encrypted and the table schema owns the phase version, so the codec never serializes a live handle, authorization, lease, or phase transition. Contract tests assert nonnull generated `JsonTypeInfo` for every root and nested type, round-trip every closed provenance and location shape, reject default or oversized fields and unknown members, and prove deterministic bytes for equal values.

Infrastructure `ManagedFileOpenContracts.cs` is the sole owner of the internal `ManagedFileOpenResult`, `ManagedFileOpenHandle`, `ManagedFileVerification`, and `ManagedFileCompareDeleteResult`. The open result has exactly the two nested runtime arms declared above. Neither arm has a public constructor, and `Opened.Handle` is nonnull. `ManagedFileVerification` has only `Match=1` and `Mismatch=2`; `ManagedFileCompareDeleteResult` has only `Deleted=1` and `Mismatch=2`. These runtime-only types are not JSON-registered and no Core contract or assembly friend is needed to mint them. `ManagedFileOpenHandle` is the exact internal sealed, nonserializable, nondefault `IAsyncDisposable` capability declared above, with no public constructor, serializable property, raw handle, path, stream, callback, kernel, or downcast accessor. Its internal constructor has exactly one production call site, `ManagedFileCapabilityOpener`, enforced by an architecture test.

`ManagedFileCapabilityMint` is one internal sealed nonserializable reference capability with no public or parameterless constructor. The Infrastructure composition root creates one random family instance, passes that same object privately to the opener, verifier, and the two narrow `PhysicalFileSystemWriter` adapters, and never registers or returns the mint itself from DI. `ManagedFileCapabilityOpener` is the sole constructor caller for `ManagedFileHandleOperationKernel`, `ManagedFileOpenHandle`, and both result arms. A handle and its kernel retain the same mint reference. Every forwarding method calls `GetActiveKernel`, which requires reference equality with the private expected mint held by the adapter, rejects disposed state, and makes the kernel repeat the same ownership check before any native operation. A handle from another family, a default or caller-created token, or a closed handle fails before the kernel observes a file or parent.

`ManagedFileHandleOperationKernel` is internal sealed and contains the only raw child and retained-parent native handles plus the immutable opened `ManagedFileDurableLocationEvidence`. Its constructor and every raw-handle creation call have exactly one production owner, `ManagedFileCapabilityOpener`; architecture tests reject any other call site. The kernel has no DI registration, interface, public constructor, property, raw-handle export, callback, stream, path reopen, serialization, or downcast surface. Its exact internal methods correspond one-for-one to the handle forwarding methods declared above: observe physical identity; write all; flush child; verify-and-adopt; verify current; compare-delete adopted child and fsync parent; verified no-replace rename; flush retained parent; and compare-delete created child and fsync parent. Reads remain inside the kernel and return only the closed result or immutable digest/evidence. The kernel never returns file bytes or a raw view. `DisposeAsync` is one-shot and disposes the child before the parent in `finally`. The handle's `Interlocked.Exchange` invokes it exactly once, is idempotent, and rejects every later operation. A caller handles `Opened` or a newly created temporary handle with `await using`; every mismatch, failure, cancellation, and exception path disposes it in `finally`. `Absent` owns no capability and cannot be converted into `Opened`.

`IMaintenanceInputReader` requires the live `CovenantMaintenanceAuthorityContext` and its turn-lease read capability. It opens one SQLite read transaction and loads the current Summary artifact and label, exact unsummarized Entry backlog and labels, immutable Session binding, current title pointer when the step needs it, history watermark, history and sensitivity revisions, and producing evidence. It computes one canonical bounded backlog digest over that snapshot. It revalidates the authority before returning and exposes no raw repository object. Every maintenance dispatch and `MaintenanceArtifactWrite` binds that digest and those revisions. A concurrent Entry, exact label, Summary pointer, binding, or projection change fails before dispatch or forces a fresh authorized snapshot; it cannot advance a watermark from the stale view.

`MaintenanceArtifactWrite` is a closed union of Summary, Title, Saga, and Lexicon output. Every arm carries the pending claim ID, maintenance step code, expected checkpoint revision, deterministic output identity and digest, required sensitivity label, producing dispatch evidence, immutable pre-request input revisions, exact maintenance-input digest, and the claim's expected-current sensitivity revision. `CovenantMaintenanceCoordinator` maps those arms to the explicit SummaryMaintenance, TitleMaintenance, SagaExtraction, or LexiconExtraction `ModelCallPurpose` and passes that purpose in Task 11's immutable dispatch request. `MaintenanceArtifactCommitter` owns one immediate transaction that rechecks the input digest and revisions, writes the immutable output, exact label, current pointer or derived projection, `session_sensitivity_state`, the matching `session_turn_maintenance_steps` committed checkpoint, and the claim's next expected-current sensitivity revision by compare-and-swap. It returns the resulting `SessionSensitivitySnapshot` for the next maintenance step or assistant begin while preserving the claim's original input snapshot and backlog watermark. A conflict or failure leaves none of them visible. Summary, Title, Saga, and Lexicon maintenance callers cannot write through their legacy stores directly.

Every content-bearing write request above contains a required `ArtifactSensitivityLabel`, expected Session history and sensitivity revision, immutable artifact identity, and producing evidence digest. The audit port is asynchronous and accepts only a closed content-free metadata DTO; a failed required audit write returns a typed failure and never falls back to raw content. The CommLink port contains an explicit metadata-only or content-bearing case, and the latter requires Ward authorization plus acknowledged disclosure identity. `LexiconService`, `SagaMemoryStore`, `IdempotencyClaimStore`, `CommandOutputArtifactStore`, `InferenceAuditLogger`, and `CommLinkMultiplexer` implement the narrow ports. Migrate every relevant production writer and test fake to those ports and require an explicit sensitivity label, including `None`, so a legacy raw-content method cannot bypass the policy. Summary and title replacement each own one immediate transaction that writes the new immutable artifact, label, and current-state pointer, purges the old projection, and advances `session_sensitivity_state` together. Transcript and tool methods append content, finalization evidence, label, and summary projection together where applicable. Existing title updates in `SessionEndpoints` and `SessionRepository`, and existing summary and tool writes in `GrimoireRepository`, migrate to these narrow ports. No broad repository interface gains a new optional sensitivity parameter.

Close the remaining old injectable writers in the same compile slice. Tasks 5 and 14 have already removed assistant begin, finalize, and discard from `IGrimoireRepository`; this task removes raw transcript/tool append, completed-exchange, Campaign-log-watermark, and mutable rollup writes. Remove `SaveAsync` from `IIdempotencyStore`, raw `CompleteAsync(..., string responseBody, ...)` from `IIdempotencyClaimStore`, raw `LogAsync(InferenceAuditRecord)` from `IInferenceAuditLogger`, raw `LogAsync(GuardrailAuditRecord)` from `IGuardrailAuditLogger`, raw `RecordAsync(SanctumBreachRecord, ...)` from `ISanctumBreachRepository`, raw `DispatchAsync(CommLinkMessage)` from `ICommLinkDispatcher`, raw Lexicon upsert methods from `ILexiconService`, raw Saga insert/watermark methods from `ISagaMemoryStore`, raw `AppendNodesAsync(TapestryNodeWrite)` from `ITapestryStore`, `ScribeLoreAsync(string, string)` and `RecordWorkspaceContextAsync(WorkspaceContext)` from `IGrimoireRepository`, and raw path/content methods from `IFileSystemWriter`. Narrow `ISessionRepository.CreateAsync`, `AddEntryAsync`, and `UpdateSessionAsync` so caller-supplied titles, Entries, summaries, or Session state require a typed label-bearing or explicit clean-origin request. Narrow whole-entity `IApprenticeRepository` add/update methods to the clean-required Apprentice-state contract.

`ILoreArtifactStore`, `IWorkspaceContextArtifactStore`, and the narrowed `IFileSystemWriter` accept only sealed `OperatorLoreWriteRequest`, `TrustedWorkspaceContextWriteRequest`, or `OperatorWorkspaceFileWriteRequest` values. Each carries one closed nonserializable clean-origin arm, exact request or scan identity, canonical workspace identity when applicable, and explicit sensitivity `None`; no constructor accepts derived sensitivity. Only authenticated `LoreEndpoints`, the trusted `ChronosyncEngine` scan path, and authenticated `WorkspaceEndpoints` respectively may construct them. Model, MCP, apprentice, subagent, background inference, and generic DI callers cannot resolve a raw string/path writer. Keep read, query, and operator deletion methods where applicable. `IdempotencyEndpointFilters` completes a claim only through `ILabeledIdempotencyArtifactStore`, using the exact response sensitivity feature or explicit `None`. Internal implementations may expose no public content-bearing method that DI can resolve without a required label or closed metadata-only request. Update every production caller and test double before the focused green run. A compile-time architecture test enumerates these Core and Infrastructure interfaces and fails if a raw string, response body, tool arguments, matched text, facts, embedding content, path, URL, Session, Entry, Apprentice, Tapestry node, or CommLink message can reach a durable or external sink outside the narrow ports or the three exact clean-origin request constructors.

`InferenceAuditRecord` remains a read DTO for historical rows, while new writes use `InferenceSensitivityAuditMetadata` and cannot contain tool arguments, prompts, model output, or matched text. `GuardrailAuditLogger` receives only closed rule, action, count, and digest fields. `SanctumGuard` replaces requested path, resolved path, URL, and arbitrary `DetailsJson` values with closed breach codes and domain-separated digests before persistence. Ordinary `ILogger` calls in Saga extraction, Lexicon extraction, Guardrail, Sanctum, and every migrated sink use the same content-free codes, counts, and digests. They never log provider response text, entity names or facts, matched text, path, URL, or raw breach detail. `OperationalLogProjection` is the only ring-buffer and log-stream record for protected-scope events. It contains closed event/component/level codes, counts, and domain-separated digests, with no rendered message, exception string, stack trace, or arbitrary property. `SerilogLogRingBufferSink`, `ILogRingBuffer`, `LogQueryService`, and the streaming log endpoints accept and return that projection for protected events. Ordinary unrelated operational logs remain available through a disjoint unprotected record arm, and no protected event can select it.

`CovenantProtectedLogScope` marks every protected inference, maintenance, tool, A2A, daemon, and recovery log event with a closed component and event code before any `ILogger` call. The marker is non-authorizing metadata, and a closed protected logger-category catalog makes an absent marker detectable. `CovenantSanitizingLogSink` is the only path from `LoggingBootstrapper` to console, rolling-file, ring-buffer, or any later configured sink. For a protected marker it discards rendered message, template, exception, stack, and arbitrary properties and emits only the corresponding `OperationalLogProjection`; a missing or malformed required marker is dropped with one content-free counter. `LoggingBootstrapper` cannot register a raw Serilog sink in parallel. Tests capture console, file, and ring output and prove protected provider text, Entry content, matched text, entity, path, URL, task goal, AgentUrl, exception message, and property values never reach bytes.

`SessionLiveEntryStream` propagates sensitivity. `SessionEventHub` accepts and returns only `LabeledSessionEntryEvent`, never a bare `Entry`. `GrimoireTurnWriter` publishes the exact label and producing evidence returned by the atomic finalization commit; operator append publishes explicit `None`. Plan 04's `/sessions/{id}/stream` boundary holds its conditional-read lease through replay and live serialization, revalidates before each labeled event, and refuses a derived event without matching authority before writing its first byte.

`SubagentContext` and `ApprenticeState` reject derived input at the tool boundary that still owns parent sensitivity. `SubagentRunRequest` carries required input sensitivity, and `ToolExecutionPipeline` refuses a derived `delegate_task` before `ArcanumDelegateTaskTool` or `SubagentRunner` receives prompt or file arguments. It likewise refuses a derived `cast_sending` before `ArcanumInternalToolServer.CommunicationTools` calls `IConclaveArchmage.CastAsync`. No runner, repository, or endpoint may infer parent sensitivity after the request binding has been lost.

`A2APushNotification` remains metadata-only. Attended outbound `dispatch_sending` uses `A2AContentDispatch = WardAndDisclosure`, and `IA2AClientService` accepts only a sensitivity-aware request with Task 21 Ward and acknowledged disclosure evidence before any remote byte. Inbound or unattended `A2ATaskArtifact` and durable `ArcanumA2ATaskStore` use `RejectDerived`; the task store cannot persist arbitrary AgentTask history, final text, failure text, escalation text, or artifacts without an explicit clean proof. `A2ASendingRecovery` propagates the exact label into the encrypted LRO checkpoint. Its closed request carries the original acknowledged A2A disclosure and may persist a derived AgentUrl or remote-task value only under that evidence; its recovery handler revalidates the same destination digest and never turns the checkpoint into a new disclosure authority. `A2ASendingRecoveryHandlers` logs only destination and failure codes plus digests, never AgentUrl or exception text. `ArcanumA2AAgentHandler` reads the final assistant Entry only through `IProtectedAssistantArtifactReader`, which observes content and the exact label in one SQLite snapshot under an `ICovenantSnapshotReadLease`; it refuses a derived artifact before building a remote task artifact. A content read followed by a second label lookup is forbidden.

`DaemonExecutionHistory`, `DaemonEventStream`, and `UnseenServantJobProgress` are metadata-only. Replace `IDaemonExecutionRepository.FailAsync(..., string errorMessage, ...)`, `IUnseenServantJobTracker.RecordCompletion(..., string resultSummary)`, and arbitrary event text with closed outcome/failure codes, counts, and diagnostic tags across `DaemonRunner`, `UnseenServantService`, both repositories, job tracking, logging, and SSE projection.

`InferenceRunAccounting` is metadata-only. Replace `InferenceRunStart` with `InferenceRunAccountingStart(Guid RequestIdentity, Guid? SessionId, InferenceAccountingSurfaceCode Surface, ModelCallPurpose Purpose, Guid? IdempotencyClaimId, DateTimeOffset StartedAt)`. Replace `BillableOperationRecord` with `BillableOperationAccountingRecord`, whose provider, model, pricing-schedule, and optional provider-request correlation values are validated configuration identities or `CovenantDiagnosticTag` values; purpose, operation type, and status are closed enums; token counts, times, and cost are checked numbers; and pricing uses source-generated `BillablePricingSnapshotV1` rather than arbitrary JSON. `ITurnRunWriter` accepts only those records, run identity, and closed status. `InferenceAccountingJsonContext` is the only codec for the compatibility pricing column. `TurnAccountingHandle`, ambient propagation, Wizard, Batch, Weave, Inference/Subagent recovery, every fake, and direct store caller migrate in one compile slice. No request string, prompt, provider response, model output, exception, or raw provider request ID reaches `InferenceRuns` or `BillableOperations`.

`LongRunningOperationProgress` is metadata-only and uses the exact `LongRunningOperationPublicProgress` contract above. Migrate `SubagentRunner`, `A2ASendingLedger`, `BackupService`, `DeferredBackupOperationServices`, `DataRetentionService` and its pruning partial, `BlobEncryptionLifecycleService`, `InstallationResetService`, `InstallationResetExistingGrimoire`, the coordinator, store, endpoint, CLI, recovery callers, source-generated contexts, core initializer, and every affected fake and test in this compile slice. No caller persists exception text, protected content, AgentUrl, task goal, plan ID, requested identifier, or any interpolated value as progress. Installation Reset resolves replay and recovery by the normalized requested-operation identity and effect digest before the free-form field is removed, so the contract compiles and its tests pass before Plan 04 starts.

`SessionAttachmentArtifact` uses `RejectDerived`. Narrow `ISessionAttachmentStore` so raw bytes, synthesized provider text, or a managed-workspace snapshot require an explicit clean-origin proof and same-snapshot source-label decision. `WebResearchWorkflowService`, Session attachment endpoints, `AttachmentSourceResolver`, and lifecycle helpers refuse derived content before the base attachment row or file is created. `SessionAttachmentIndexRepository` and `SessionAttachmentIndexProcessor` become internal transaction/processing helpers behind the guarded indexing service and are not registered as resolvable concrete services. If retained as injectable ports, every begin, append/replace, complete, process, and fail request instead requires the exact clean base-attachment proof and generation capability. No raw chunks or embeddings method is DI-resolvable. Attachment indexing runs only after that base proof and cannot turn a rejected source into a clean projection.

`EmbeddingReset` uses `GuardedPurge`. This task owns only that closed inventory identity. Plan 04 Task 16 consumes it at the prebinding purge boundary and owns `EmbeddingsResetEndpoints`, `EmbeddingsResetService`, their guarded request, exact source-label join, transaction authorization, and tests. No Plan 03 implementation path depends forward on that lifecycle slice.

Tapestry source SQL joins labels before selecting a leaf. A tainted Entry, attachment, or workspace leaf is refused before summarization, clustering, corpus statistics, embedding, or node persistence. Protected Lexicon maintenance writes the base row and label, then removes any trigger-created `lexicon_fts` row in the same transaction before commit; unprotected operator writes may publish to generic FTS only through the labeled `None` arm. `CoreGrimoireSchemaDataInitializer` owns Lexicon external-content rebuild and exclusion as one core initialization transaction while Covenant and generic-search admission are closed. It rebuilds, removes every labeled Lexicon row, verifies the clean partition, and only then allows `GrimoireSchemaInstaller` to publish FTS availability. A reopen can never make protected facts queryable between rebuild and exclusion. Archive search joins the clean partition before `MATCH`, ranking, and limit, rather than filtering ranked results afterward.

`ManagedFileOwnershipContracts.cs` defines the two durable location shapes exactly as declared above. `ManagedFileDurableLocationEvidence` binds one canonical opaque Campaign-root identity digest, one positive path revision, a nondefault bounded immutable vector of normalized relative parent segments that may be empty, the physical-identity digest captured from that same retained no-follow parent handle, and one bounded single-segment child leaf. The canonical root identity and path revision equal the live `ManagedWorkspaceWriteTarget` and `CovenantToolInvocationUseLease` facts before persistence. Each relative segment is already normalized under `WorkspacePathPolicy`; the vector excludes the child leaf and never contains an empty, dot, dot-dot, rooted, separator-bearing, alternate-stream, reserved, or over-limit segment. `ManagedFileWriteDurableLocationEvidence` contains one validated `ManagedFileDurableLocationEvidence Target` plus one distinct bounded random single-segment `TemporaryLeaf` under that exact parent. Its internal `ForTemporaryLeaf()` preserves the canonical root identity, path revision, parent-segment vector, and same-handle parent physical identity byte-for-byte and substitutes only the temporary child leaf. Constructors deep-copy every digest and immutable vector and reject default vectors, nonpositive revisions, malformed identities, equal leaves, and mutable storage.

`ManagedFileCapabilityOpener` resolves the current Campaign root through the indexed canonical identity and exact positive path revision, then traverses only the stored normalized parent segments relative to that retained root with no-follow semantics. It rejects a changed root identity or path revision before child lookup. It computes the parent physical identity from the same retained parent handle used to open or create the child, compares it with `ParentPhysicalIdentityDigest`, and only then returns the closed result `Absent` or `Opened(ManagedFileOpenHandle)` for `ChildLeaf`. Its separate `CreateTemporaryExclusiveNoFollowAsync` accepts the complete write evidence, performs the same root, revision, segment, and parent-identity checks, creates only `TemporaryLeaf` with exclusive no-follow semantics, and returns a handle retaining that created child and the same verified parent. A copied location from another Campaign root, an old revision, a changed ancestor, a symlink or reparse point, and same-text segments under a different parent fail before `Absent` can be authoritative or creation can occur. `ManagedFileOpenHandle` is a nonserializable capability for one already-opened file plus that retained parent directory. The verifier reads identity and content from that same handle. `VerifyAndAdoptAsync` succeeds only for the exact persisted created identity and operation-owned expected bytes; same bytes under a replacement physical identity return mismatch. `VerifyCurrentAsync` never mutates. Internal `CompareDeleteAsync` accepts only `Opened`, unlinks only the exact adopted physical identity and full hash, fsyncs the retained parent directory, and reports `Deleted` or `Mismatch`; `AlreadyAbsent` is represented only by the opener. Neither component follows a link or trusts a path string after resolution.

`PhysicalFileSystemWriter` is the sole implementation of the two internal operation surfaces. Its writer adapter, recovery adapter, and `ManagedFileOwnershipVerifier` each retain the same private `ManagedFileCapabilityMint` reference as the opener. Every adapter method forwards only to the corresponding active-checked method on `ManagedFileOpenHandle` with that private mint; no adapter can access the operation kernel, raw child, retained parent, stream, or path and no native operation exists outside the kernel. `ManagedSensitiveFileWriter` receives only `IManagedFileCreatedChildWriter`, while `ManagedFileWriteIntentRecoveryService` receives only `IManagedFileCreatedChildRecovery`; neither can downcast to the other. The live writer obtains its new temporary handle only from `ManagedFileCapabilityOpener.CreateTemporaryExclusiveNoFollowAsync`. It then uses `ObserveCreatedChildPhysicalIdentityAsync` on that same handle, persists the returned identity at `TempCreated` before `WriteAllAsync`, calls `FlushCreatedChildToDiskAsync`, and performs the typed rename and retained-parent flush. Recovery's same-named observation method can inspect an already-opened temporary or target child without mutation and is the only way it may compare the persisted created identity. A compile-time architecture test constructs all three adapters with the producer mint and proves every interface method has one matching handle forwarder. Every operation rejects a closed or foreign-mint handle before kernel invocation or effect.

`RenameCreatedChildNoReplaceAsync` requires `temporaryHandle` to have been created or opened from the journal's `ForTemporaryLeaf()` evidence, requires `target` to match the handle's canonical root identity, path revision, normalized parent segments, and same retained-parent physical identity byte-for-byte, and rejects an equal or noncanonical child leaf. From that same opened temporary handle it rechecks `expectedCreatedChildPhysicalIdentityDigest`, the exact expected full content hash, and length before one no-replace rename to `target.ChildLeaf`. It returns only `RenamedNoReplace=1`, `TargetAlreadyPresent=2`, or `Mismatch=3`; the latter two perform no mutation. The live writer and recovery service commit their respective `TempFsynced -> RenamedNoReplace` transition only after `RenamedNoReplace` returns. A crash after the syscall but before that CAS follows the exact target rename-ahead observation rule.

`FlushRetainedParentAsync` accepts the still-open renamed handle, or the same recovery-reopened target handle after rename-ahead, and fsyncs only its retained verified parent. It does not reopen a path and never combines the rename with the durability barrier. The service commits `RenamedNoReplace -> ParentFsynced` only after this separate call succeeds. A crash after the fsync but before its CAS repeats the idempotent parent flush on the same journaled target evidence. `CompareDeleteCreatedChildAsync` accepts only an already-opened partial temporary handle at `TempCreated` or `TempWritten` plus the persisted created identity, compares the identity from that same handle, and on equality unlinks that child and fsyncs the retained parent before returning `Deleted`. It does not require a content hash because partial content cannot equal the final hash. It returns `Mismatch` without mutation for any identity change. Architecture tests reject every recovery operation call site other than `ManagedFileWriteIntentRecoveryService`, reject public or general DI exposure of either operation surface, prove that the live writer cannot resolve compare-delete, and pin every rename result code. Plan 04's Infrastructure kernel receives only the internal opener and ownership verifier, so local erasure cannot reach writer or recovery lifecycle operations and cannot downgrade its final identity-plus-hash comparison.

Exact test methods:

- `Maintenance_InsertsClaimBeforeSummaryTitleSagaOrLexiconDisclosure`
- `Maintenance_UsesFrozenBacklogAndNoTools`
- `Maintenance_RejectsProviderToolCall`
- `Maintenance_RetryUsesCheckpointAndNewPhysicalDispatchReceipt`
- `Maintenance_HoldsAndRevalidatesOneTurnLeaseThroughBacklogDispatchAndCheckpointCommit`
- `MaintenanceInputReader_LoadsSummaryBacklogLabelsAndRevisionsInOneSnapshot`
- `MaintenanceInputReader_ConcurrentLabelOrPointerChangeFailsBeforeDispatch`
- `Maintenance_ResetOrFeatureGenerationChangeAbortsTerminalizesAndDrainsBeforeErasure`
- `Maintenance_OutputLabelPointerAndCheckpointCommitOrRollbackTogether`
- `Maintenance_TaintedOutputAdvancesExpectedSensitivityAndAssistantBeginSucceeds`
- `Maintenance_UnrelatedSensitivityAdvanceFailsCasWithoutCheckpointOrOutput`
- `Grimoire_SummaryTitleAndToolWritesCommitLabelsAtomically`
- `DerivedArtifactPorts_ReplacePointersLabelsAndOldProjectionsInOneTransaction`
- `SessionTitleEndpoint_UsesNarrowLabeledStoreForEveryUpdate`
- `SagaAndLexicon_TaintedInputsProduceProtectedLabeledOutputs`
- `LexiconFts_ProtectedWriteRemovesTriggerProjectionBeforeCommit`
- `LexiconFts_ReopenRebuildCannotRepublishProtectedRows`
- `TapestryProjection_RefusesTaintedLeafBeforeSummaryStatisticsEmbeddingOrWrite`
- `Loremaster_BackgroundPathCannotReadOrDispatchTaintedHistory`
- `GenericIndexes_RefuseCovenantDerivedArtifactsBeforeRanking`
- `ArchiveSearch_ExcludesTaintedRowsBeforeMatchRankAndLimit`
- `AssistantCommit_RemovesTransientGenericFtsRowInTheSameTransaction`
- `EntryWeavingAndDivination_NeverSelectCovenantDerivedArtifacts`
- `UnauthorizedGenericSearch_RanksAndTimesTheSameWithOrWithoutTaintedDocuments`
- `SubagentAndApprentice_RejectDerivedHistoryBeforeContextOrStateWrite`
- `DelegateTaskAndCastSending_RejectDerivedAtParentToolBoundaryBeforeInvocation`
- `SessionContextPins_JoinEntryAttachmentAndManagedFileLabelsBeforeMaterialization`
- `SessionContextPins_DerivedSourceRequiresTurnLeaseAndPropagatesMaximumLabel`
- `SessionContextPins_ExplicitNoneBackgroundOrLabelRaceReturnsNoContent`
- `SessionContextPinArtifact_RequiresExactOperatorCleanOriginAndRejectsDerivedWrites`
- `AttachmentMemoryConsultation_JoinsLabelsBeforeIdentifiersAndPropagatesMaximumLabel`
- `AttachmentMemoryConsultation_ExplicitNoneBackgroundOrVersionRaceReturnsNoIdentifiers`
- `SessionLiveEntryStream_PropagatesFinalizationLabelAndRequiresLeaseBeforeBytes`
- `A2APushNotification_IsMetadataOnlyAndContentDispatchRejectsTaintedArtifact`
- `A2AOutboundAttendedContentRequiresWardAndReceiptWhileInboundArtifactRejectsDerived`
- `A2AAgentHandler_ReadsAssistantAndLabelInOneSnapshotAndWinsLabelWriteRace`
- `A2ATaskStore_RejectsDerivedHistoryArtifactFinalFailureAndEscalationText`
- `A2ASendingRecovery_LogsNoAgentUrlOrExceptionText`
- `A2ASendingRecovery_DerivedCheckpointRequiresLabelAndOriginalDisclosureEvidence`
- `GuardrailAndSanctumAudit_RecordOnlyClosedCodesAndDigests`
- `SagaLexiconSanctumAndGuardrailLogsContainNoDerivedTextPathUrlOrFacts`
- `AuditAndCommLink_RecordOnlyContentFreeSensitivityMetadata`
- `OperationalLogProjection_ProtectedEventsExposeOnlyCodesCountsAndDigests`
- `LoggingBootstrapper_EveryProtectedConsoleFileAndRingSinkSanitizesBeforeBytes`
- `DaemonExecutionHistoryAndEventStream_StoreNoRawFailureOrContentText`
- `UnseenServantJobProgress_StoresLogsAndReturnsOnlyClosedMetadata`
- `LongRunningOperationProgress_UsesClosedMetadataWithoutPublicSummaryText`
- `LongRunningOperationProgress_CodesVersionsCountsAndBlockerMatrixAreImmutable`
- `LongRunningOperationProgress_DurableAndApiJsonUseOnlySourceGeneratedContexts`
- `LongRunningOperationProgress_LegacySummaryUpgradeDropsTextBeforeReadiness`
- `EveryLongRunningOperationCallerUsesClosedProgressAndNoRawCheckpointSummary`
- `RequestedOperationIdentity_InsertReplayAndConflictAreAtomic`
- `InstallationReset_RecoversOnlyByRequestedIdentityAndEffectDigest`
- `OperationCli_RendersOnlyFixedProgressCodesAndCounts`
- `InferenceRunAccounting_StoresOnlyClosedCodesNumbersAndDiagnosticTags`
- `LegacyIdempotencyResponseWriter_IsAbsentFromCoreAndDi`
- `IdempotencyClaimCompletionRequiresExactResponseLabel`
- `RawPublicWriteInterfaces_AreAbsentAndEveryCallerUsesRequiredLabelPort`
- `LoreWorkspaceContextAndOperatorFileWritesUseOnlyExactCleanOriginRequests`
- `SessionApprenticeTapestryAndCampaignWatermarkRawWritersAreAbsentFromDi`
- `SessionAttachmentIndexRepositoryAndProcessorAreGuardedAndNotRawDiWriters`
- `EveryAssistantAndSummaryConsumerDeclaresSensitivityPolicy`
- `ManagedFileWriter_PersistsIntentBeforeFirstByteAndLabelsAfterFsync`
- `ManagedFileWriter_PersistsFullPendingLabelProjectionBeforeFirstByteAndClearsItOnAdoption`
- `ManagedFilePersistenceJsonContext_OwnsLabelProvenanceLocationAndEveryNestedType`
- `ManagedFileOpenResult_HasOnlyAbsentAndOpenedArms`
- `ManagedFileRuntimeResultCodesAreLiteralAndExhaustive`
- `ManagedFileCapabilityOpener_IsTheOnlyHandleMint`
- `ManagedFileCapabilityFamily_IsInternalToInfrastructureAndRequiresNoCoreFriendAssembly`
- `ManagedFileCapabilityMint_IsNotResolvableSerializableOrDefaultConstructible`
- `ManagedFileHandleOperationKernel_IsSealedAndOwnsEveryRawChildAndParentHandle`
- `ManagedFileOpenHandle_HasNoPublicDefaultSerializationRawHandleOrDowncastSurface`
- `ManagedFileOpenHandle_ExposesNoRawHandleKernelStreamPathOrCallback`
- `ManagedFileOpenHandle_ForwardsEveryDeclaredOperationOnlyAfterMintAndActiveChecks`
- `ManagedFileForeignMintFailsBeforeKernelInvocation`
- `ManagedFileOpenResult_ArmsCanOnlyBeMintedByTheInfrastructureOpener`
- `ManagedFileOpenHandle_DisposesChildThenParentExactlyOnceAndRejectsReuse`
- `ManagedFileOpenHandle_IsDisposedOnMismatchFailureCancellationAndException`
- `ManagedFileVerifierWriterAndRecoveryCompileThroughOnlyTypedHandleForwarders`
- `ManagedFileWriter_CreatesTemporaryExclusivelyAndObservesIdentityFromTheSameRetainedHandle`
- `ManagedFileWriter_WriteFlushRenameAndParentFlushUseOnlyTheTypedLifecycle`
- `ManagedFileWriter_CannotResolveRecoveryCompareDeleteAndRecoveryCannotResolveWriteOrCreate`
- `ManagedFileRecovery_RestartRecreatesExactLabelOnlyFromPendingProjection`
- `ManagedFileLocation_BindsRootRevisionParentSegmentsAndSameHandleParentIdentity`
- `ManagedFileWriter_RequiresLiveInvocationWardAndMatchingDisclosureEvidence`
- `ManagedFileWriter_PersistsCreatedChildIdentityFromCreateHandleAtTempCreated`
- `ManagedFileWriter_FinalOwnershipIsSetOnceWithLabelAndCannotBeRetainedWhileLive`
- `ManagedFileWriter_VerifyAndAdoptRequiresCreatedIdentityHashAndLengthFromOneHandle`
- `ManagedFileWriter_SameContentReplacementAfterParentFsyncCannotBeAdopted`
- `ManagedFileRecovery_SameContentReplacementAfterRestartCannotBeAdopted`
- `ManagedFileWriter_ReplacementAtSameLeafCannotReusePriorOwnershipEvidence`
- `ManagedFileWriter_AdoptsOrCompareDeletesOnlyExactCrashArtifact`
- `ManagedFileRecovery_PartialTemporaryCleanupRequiresExactCreatedChildIdentityAndParentFsync`
- `ManagedFileRecovery_RenameResultCodesAreLiteralAndExhaustive`
- `ManagedFileRecovery_RenameRequiresJournaledTargetSameParentCreatedIdentityHashAndLength`
- `ManagedFileRecovery_RenameSyscallPrecedesRenamedNoReplaceCas`
- `ManagedFileRecovery_ParentFsyncPrecedesParentFsyncedCas`
- `ManagedFileRecovery_CrashAfterRenameBeforeCasUsesOnlyExactRenameAheadObservation`
- `ManagedFileRecovery_CrashAfterParentFsyncBeforeCasRepeatsOnlyTheSameParentBarrier`
- `ManagedFileRecovery_TempFsyncedPresentTemporaryRenamesOnlyAfterIdentityHashAndLengthMatch`
- `ManagedFileRecovery_TempFsyncedRenameAheadRequiresTargetCreatedIdentityHashAndLength`
- `ManagedFileRecovery_PreparedWithEitherChildPresentPerformsNoFilesystemEffect`
- `ManagedFileWriter_ExistingOrChangedFileBecomesNonrevocableDisclosure`
- `ManagedFileOpener_RejectsChangedRootRevisionSegmentsOrParentIdentityBeforeChildOpen`
- `ManagedFileErasure_ForgedLocationOrOwnershipRequestIsRejectedBeforeOpen`
- `FileTools_ExclusiveNewFileUsesManagedWriterBeforeFirstByte`
- `FileTools_ReplaceOverwriteAndApplyPatchAreNonrevocableSensitiveEgress`
- `ManagedFileVerifier_UsesOneOpenedHandleForIdentityHashAndCompareDelete`
- `ManagedFileOpener_ReturnsAbsentOrSameParentOpenedWithoutFollowingLinks`
- `ManagedFileRecovery_BlocksWorkspaceAndResetAdmissionUntilEveryIntentIsTerminal`
- `CommLink_ContentBearingTaintRequiresWardAndReceipt`
- `IdempotencyAndToolArtifactWrites_PropagateOrRefuseSensitivityAtomically`
- `SessionAttachmentStore_RejectsDerivedBytesProviderTextAndManagedFileSnapshots`
- `WebResearchWorkflow_DerivedSynthesisCannotCreateBaseAttachment`
- `CoreInitializer_RebuildsAndExcludesProtectedLexiconRowsInOneTransactionBeforeReadiness`
- `EveryProtectedWriterUsesNarrowRequiredLabelPortAndDiRegistration`

- [ ] **Step 1: Add the failing closed-consumer inventory and sink tests**

```csharp
[Fact]
public void EveryAssistantAndSummaryConsumerDeclaresSensitivityPolicy()
{
      DerivedOutputConsumerInventory.AssertExact(
          new Dictionary<DerivedOutputIdentity, DerivedOutputSensitivityPolicy>
          {
              [DerivedOutputIdentity.TranscriptEntry] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.GrimoireSummary] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.SessionTitle] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.GenericToolArtifact] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.IdempotencyClaim] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.SessionLiveEntryStream] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.SessionContextPinArtifact] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SessionContextPinMaterialization] = DerivedOutputSensitivityPolicy.ProtectedReadAndPropagate,
              [DerivedOutputIdentity.AttachmentMemoryConsultation] = DerivedOutputSensitivityPolicy.ProtectedReadAndPropagate,
              [DerivedOutputIdentity.LoreArtifact] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.WorkspaceContextArtifact] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.OperatorWorkspaceFile] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SagaExtraction] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.LexiconExtraction] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.LexiconFts] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.TapestryProjection] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.WorkspaceIndex] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SessionAttachmentArtifact] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SessionAttachmentIndex] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.GenericEntryFts] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.EntryEmbedding] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.WorkspaceFileEmbedding] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SessionAttachmentEmbedding] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SagaMemoryEmbedding] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.TapestryNodeEmbedding] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.GrimoireArchiveSearch] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.DivinationProjection] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.EntryWeavingProjection] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.InferenceAudit] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.GuardrailAudit] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.SanctumBreachAudit] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.OperationalLogProjection] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.DaemonExecutionHistory] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.DaemonEventStream] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.UnseenServantJobProgress] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.LongRunningOperationProgress] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.InferenceRunAccounting] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.CommLinkMetadata] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.CommLinkContentDispatch] = DerivedOutputSensitivityPolicy.WardAndDisclosure,
              [DerivedOutputIdentity.A2APushNotification] = DerivedOutputSensitivityPolicy.MetadataOnly,
              [DerivedOutputIdentity.A2AContentDispatch] = DerivedOutputSensitivityPolicy.WardAndDisclosure,
              [DerivedOutputIdentity.A2ATaskArtifact] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.A2ATaskStore] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.A2ASendingRecovery] = DerivedOutputSensitivityPolicy.PropagateAtomically,
              [DerivedOutputIdentity.SubagentContext] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.ApprenticeState] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.LegacyIdempotencyResponse] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.SessionFork] = DerivedOutputSensitivityPolicy.ProtectedTransfer,
              [DerivedOutputIdentity.SelectiveSessionImport] = DerivedOutputSensitivityPolicy.RejectDerived,
              [DerivedOutputIdentity.ProtectedPhysicalRestore] = DerivedOutputSensitivityPolicy.ProtectedRestore,
              [DerivedOutputIdentity.ManagedWorkspaceFile] = DerivedOutputSensitivityPolicy.ManagedOwnershipOrDisclosure,
              [DerivedOutputIdentity.PlaintextSessionExport] = DerivedOutputSensitivityPolicy.RejectWholeSession,
              [DerivedOutputIdentity.PlaintextCampaignExport] = DerivedOutputSensitivityPolicy.ExcludeWithCounts,
              [DerivedOutputIdentity.EncryptedBackup] = DerivedOutputSensitivityPolicy.EncryptedDisclosure,
              [DerivedOutputIdentity.Retention] = DerivedOutputSensitivityPolicy.GuardedPurge,
              [DerivedOutputIdentity.EmbeddingReset] = DerivedOutputSensitivityPolicy.GuardedPurge,
          });
}
```

- [ ] **Step 2: Run the focused derived-output tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantDerivedOutputInventoryTests|FullyQualifiedName~CovenantMaintenanceCoordinatorTests|FullyQualifiedName~MaintenanceArtifactCommitterTests|FullyQualifiedName~MaintenanceInputReaderTests|FullyQualifiedName~LoremasterSensitivityTests|FullyQualifiedName~SagaExtractionServiceTests|FullyQualifiedName~SagaMemoryStoreTests|FullyQualifiedName~LexiconServiceTests|FullyQualifiedName~LexiconEntityExtractorTests|FullyQualifiedName~GrimoireSchemaInstallerTests|FullyQualifiedName~CovenantCoreArtifactSchemaTests|FullyQualifiedName~TapestryStoreTests|FullyQualifiedName~TapestryWeaverTests|FullyQualifiedName~TapestrySummarizerSensitivityTests|FullyQualifiedName~WorkspaceIndexingServiceTests|FullyQualifiedName~SessionAttachmentIndexingTests|FullyQualifiedName~SessionAttachmentStoreTests|FullyQualifiedName~AttachmentSourceResolverTests|FullyQualifiedName~WebWorkflowEndpointTests|FullyQualifiedName~SessionContextPinMaterializerTests|FullyQualifiedName~SessionContextPinStoreTests|FullyQualifiedName~EntryWeavingServiceTests|FullyQualifiedName~DivinationServiceTests|FullyQualifiedName~SessionRepositoryTests|FullyQualifiedName~ApprenticeRepositoryTests|FullyQualifiedName~ApprenticeRepositoryConcurrencyTests|FullyQualifiedName~GrimoireRepositoryTests|FullyQualifiedName~DerivedArtifactStoreTests|FullyQualifiedName~ProtectedAssistantArtifactReaderTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~SessionEventHubTests|FullyQualifiedName~GrimoireTurnWriterTests|FullyQualifiedName~BudgetMonitorTests|FullyQualifiedName~DaemonInitiativeEndpointTests|FullyQualifiedName~DaemonRunnerTests|FullyQualifiedName~InMemoryDaemonExecutionRepositoryTests|FullyQualifiedName~UnseenServantJobTrackerTests|FullyQualifiedName~UnseenServantServiceTests|FullyQualifiedName~ArcanumInternalToolServerTests|FullyQualifiedName~ApplyPatchToolTests|FullyQualifiedName~MultiFileCommitCoordinatorTests|FullyQualifiedName~SandboxedFileIoTests|FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~WardAutoApprovalPipelineTests|FullyQualifiedName~InferenceAuditLoggerTests|FullyQualifiedName~GuardrailAuditLoggerTests|FullyQualifiedName~GuardrailsPipelineTests|FullyQualifiedName~SanctumGuardTests|FullyQualifiedName~SanctumBreachRepositoryTests|FullyQualifiedName~InMemoryLogRingBufferTests|FullyQualifiedName~SerilogLogRingBufferSinkTests|FullyQualifiedName~LogQueryServiceTests|FullyQualifiedName~LogsEndpointTests|FullyQualifiedName~A2APushNotificationTests|FullyQualifiedName~A2AServerTests|FullyQualifiedName~A2AClientTests|FullyQualifiedName~A2AOutboundHttpClientTests|FullyQualifiedName~A2AParkedSendingTests|FullyQualifiedName~A2ASendingLedgerTests|FullyQualifiedName~SubagentRunnerTests|FullyQualifiedName~ApprenticeServiceReliabilityTests|FullyQualifiedName~WebhookCommLinkDispatcherTests|FullyQualifiedName~CommLinkMultiplexerTests|FullyQualifiedName~IdempotencyClaimStoreTests|FullyQualifiedName~IdempotencyStoreTests|FullyQualifiedName~IdempotencyEndpointFilterTests|FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~LongRunningOperationEndpointTests|FullyQualifiedName~BackupServiceTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~BlobEncryptionLifecycleServiceTests|FullyQualifiedName~LoreEndpointTests|FullyQualifiedName~ChronosyncEngineTests|FullyQualifiedName~WorkspacesEndpointTests|FullyQualifiedName~ManagedSensitiveFileWriterTests|FullyQualifiedName~ManagedFileOwnershipVerifierTests|FullyQualifiedName~ManagedFileOwnershipContractsTests|FullyQualifiedName~ManagedFileCapabilityOpenerTests|FullyQualifiedName~ManagedFileWriteIntentRecoveryTests|FullyQualifiedName~PhysicalFileSystemWriterTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~AttachmentMemoryProvenanceStoreTests|FullyQualifiedName~LoggingBootstrapperTests|FullyQualifiedName~LongRunningOperationProgressContractsTests|FullyQualifiedName~LongRunningOperationRequestedIdentityTests|FullyQualifiedName~InstallationResetExistingGrimoireTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~OperationCommandsTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~BackupCreateRecoveryHandlerTests|FullyQualifiedName~DataRetentionApplyBoundaryTests|FullyQualifiedName~DataRetentionWorkspaceResetTests|FullyQualifiedName~DataRetentionAgeBoundaryTests|FullyQualifiedName~DataRetentionDaemonHistoryTests|FullyQualifiedName~DataRetentionQuarantineRecoveryTests|FullyQualifiedName~DurableOperationDiagnosticsTests|FullyQualifiedName~TurnRunWriterRecoveryTests|FullyQualifiedName~InferenceAccountingStoreTests|FullyQualifiedName~BudgetReservationServiceTests|FullyQualifiedName~RecoveryHandlerTests|FullyQualifiedName~TurnAccountingHandleTests|FullyQualifiedName~BatchProcessingServiceTests|FullyQualifiedName~WeaveServiceTests|FullyQualifiedName~WizardIntelligenceProviderTests"
```

Expected: FAIL because downstream methods accept unlabeled content and ambient background inference can read tainted history.

- [ ] **Step 3: Make labels mandatory and add request-bound maintenance authority**

Before assistant begin, acquire one generation-bound `CovenantTurnLease` and derive a single-use maintenance context from that lease, the clean read authority, pending claim, Session, history watermark, sensitivity snapshot, captured authority epoch, and availability generation. The nonserializable context owns the lease while it freezes the pre-request tainted backlog, before every physical maintenance dispatch, and through each atomic output plus checkpoint commit. It revalidates at all three boundaries. Force `DisableAllTools` and no tool definitions. Persist a disclosure before each physical maintenance dispatch. Store each deterministic output and checkpoint under the claim. On success, detach the same lease exactly once into Task 15's `CovenantTurnStateRequest`; on failure, generation change, or reset, terminalize the claim and disclosure subject, dispose the lease, and let the exclusive operation drain before reporting completion. Require every listed sink to propagate the label atomically or refuse the input.

For an approved write to a new exclusively managed workspace file, persist Plan 01 `managed_file_write_intents` at exact phase `Prepared` with the immutable artifact and label identities, encrypted full pending `ArtifactSensitivityLabel` projection, expanded encrypted `ManagedFileWriteDurableLocationEvidence`, expected full hash and length, sensitivity digest, and effect identity before the first filesystem byte. The location is derived from the live canonical root identity, path revision, normalized relative parent segments, and physical identity captured from the same retained no-follow parent handle used for creation. Retain that handle only in process. Through it, create the evidence's temporary leaf exclusively and retain the new child handle. Before releasing that handle or writing a byte, compute its physical identity from the same handle and commit `Prepared -> TempCreated` with `CreatedChildPhysicalIdentityDigest` filled exactly once. Write and fsync it, atomically rename without replacement to the evidence's target leaf, fsync the parent, and advance the exact Plan 01 phase after each proven durability boundary. Reopen the result without following links and call the one shared `IManagedFileOwnershipVerifier.VerifyAndAdoptAsync` with the persisted created identity plus expected content. It computes final physical identity, full hash, and length from that same handle and refuses same-content replacement. One immediate transaction then borrows Plan 01 `ManagedFileIntentMutation`, rechecks the row at `ParentFsynced`, requires returned final physical identity to equal `CreatedChildPhysicalIdentityDigest`, fills `FinalOwnershipEvidence` exactly once, inserts the exact matching `artifact_sensitivity` row from the pending projection, securely clears that projection, and advances to `AdoptedAndLabeled`. The row remains the ownership catalog and cannot be deleted while that artifact or label exists. Route `write_file` through this path only when no-follow preflight proves an absent target and exclusive creation remains possible. An overwrite, `replace_text_block`, or `apply_patch` edit through `SandboxedFileIo`, `ApplyPatchToolExecutionService`, or `MultiFileCommitCoordinator` is always nonrevocable Covenant-sensitive egress, requires Task 21's attended Ward and receipt before the first edit, and never creates deletion authority.

Register `ManagedFileWriteIntentRecoveryService` in the pre-readiness bootstrap registry. After restart, it consumes Plan 01's exact phase, pending-label, created-identity, and ownership shapes and passes only the write evidence's `Target` or `ForTemporaryLeaf()` result to the internal `IManagedFileCapabilityOpener`. At `Prepared`, proven absence of both leaves terminalizes `Cleaned`; either leaf present is an unauthenticated create-before-CAS artifact, so recovery performs no filesystem effect and terminalizes `ManualNonrevocable`. At `TempCreated` or `TempWritten`, an opened partial temporary child may be removed only by `IManagedFileCreatedChildRecovery.CompareDeleteCreatedChildAsync` with the persisted created identity and its parent-fsync result. At `TempFsynced`, a present temporary and absent target resumes only through `RenameCreatedChildNoReplaceAsync`; `RenamedNoReplace` must return before the corresponding phase CAS. Temporary absence plus an opened target advances the journal as rename-ahead only when the target's same-handle physical identity equals `CreatedChildPhysicalIdentityDigest` and its full hash and length equal the same immutable evidence. Either successful rename arm retains the exact target-parent capability, calls `FlushRetainedParentAsync`, and commits `ParentFsynced` only after that separate durability barrier succeeds. From `RenamedNoReplace` or `ParentFsynced`, cleanup of the target additionally requires that exact identity, hash, and length through internal `CompareDeleteAsync`. Two present leaves, a changed root, revision, ancestor, parent identity, child identity, content, or any uncertain observation becomes `ManualNonrevocable` without mutation. Recovery can finish exact adoption only by passing the persisted created identity and expected content with the matching final handle to `VerifyAndAdoptAsync`; a same-content replacement under a different identity becomes `ManualNonrevocable` without label insertion. Successful adoption atomically inserts the label, fills final ownership with the identical created identity, and clears the pending projection. `Cleaned` and `ManualNonrevocable` also clear it atomically. Workspace-write, reset, and erasure admission remain closed until every nonterminal intent reaches `AdoptedAndLabeled`, `Cleaned`, or `ManualNonrevocable`. Any preexisting target, later modification, external MCP, or process write is nonrevocable disclosure evidence and never grants later deletion authority. Plan 04 erasure rereads the exact `AdoptedAndLabeled` row and live label in one transaction and copies its complete expanded location plus final ownership into the work item; a request value, leaf, matching content hash, or any internal created-child recovery result alone cannot grant erasure authority.

- [ ] **Step 4: Run the focused derived-output tests to verify green**

Run the Step 2 command.

Expected: PASS for maintenance ordering, checkpoint replay, no tools, protected Saga/Lexicon, labeled Grimoire writes, physical generic-index exclusion, rank invariance, and A2A or notification refusal.

- [ ] **Step 5: Refactor downstream policy into an exhaustive closed inventory**

Define a closed `DerivedOutputSensitivityPolicy` switch keyed by every inventory identity above. Transcript, summary, title, live Session events, A2A recovery, generic tool persistence, idempotency, Saga, and Lexicon atomically propagate the exact label or refuse. `ProtectedReadAndPropagate` requires a live read lease, same-snapshot source labels, and inclusion of the resulting maximum label in downstream call sensitivity; only Session context-pin materialization and attachment-memory consultation use it. Keep generic search, archive search, embedding, vector, generic FTS, Lexicon FTS, Tapestry projection, inbound A2A task artifacts, subagent, apprentice state, Session context-pin artifacts, clean-origin-only Lore/workspace writers, and unattended post-turn projections at `RejectDerived`. Use `WardAndDisclosure` for attended outbound A2A content. `A2APushNotification`, inference audit, Guardrail audit, Sanctum breach audit, daemon and Unseen Servant progress, inference-run accounting, protected operational logs, and genuinely content-free LRO progress use `MetadataOnly`; their DTOs accept only closed codes, counts, and domain-separated digests. In the same assistant-finalization transaction, remove the row inserted by the existing generic `Entries_fts` update trigger before commit. Protected Lexicon writes similarly remove their trigger-created `lexicon_fts` row before commit. Entry Weaving, Tapestry, Divination, and archive search exclude labeled entries in their source SQL, so unauthorized corpus statistics, ranks, displacement, and timing are independent of tainted documents. Split CommLink policy: genuinely content-free progress and diagnostics use `MetadataOnly`, while any tainted message, webhook body, destination argument, or other content-bearing dispatch requires Task 21's attended Ward plus acknowledged disclosure receipt or refuses before the side effect. Plan 04 consumes the explicit `SessionFork`, `SelectiveSessionImport`, `ProtectedPhysicalRestore`, `ManagedWorkspaceFile`, `PlaintextSessionExport`, `PlaintextCampaignExport`, `EncryptedBackup`, `Retention`, and `EmbeddingReset` policies. Rerun the Step 2 command.

Expected: PASS, and adding a new assistant or summary consumer fails the inventory until its policy is declared.

## Task 24: Close runtime races and run the Plan 03 integration gate

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEngine.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnExecutionCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/IHostProcessToolsTransitionService.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/HostProcessToolsTransitionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/HostProcessToolsMarkerPairContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsTransitionService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsStartupGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantRuntimeAuthorityEndToEndTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantRuntimeDisabledPathTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsTransitionServiceTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsStartupGateTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerPairJoinerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`

**Interfaces:**

- Consumes: every earlier task in this plan and Plan 02's real SQLCipher-backed canonical contracts.
- Produces: a complete internal runtime contract, including the exact `IHostProcessToolsTransitionService` and pure marker-pair join contract, for Plan 04's offline CLI and full-installation reset plus Plan 05 verification/documentation.

The offline transition port is exact and owns its installation-lock acquisition. The CLI never accepts or passes a lock, trusted environment result, marker bytes, database row, master key, or compensation capability:

```csharp
public interface IHostProcessToolsTransitionService
{
    Task<Result<HostProcessToolsTransitionResult>> EnableAsync(
        HostProcessToolsTransitionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record HostProcessToolsTransitionRequest(Guid TransitionId);

public enum HostProcessToolsTransitionOutcome : byte
{
    Completed = 1,
    AlreadyCompleted = 2,
    PendingManualRemediation = 3,
    Refused = 4
}

public sealed record HostProcessToolsTransitionResult(
    Guid TransitionId,
    HostProcessToolsTransitionOutcome Outcome,
    bool RestartRequired);

public enum HostProcessToolsAuthorityState : byte
{
    Clean = 1,
    PendingHostToolsTaint = 2,
    HostToolsTainted = 3
}

public enum HostProcessToolsMarkerPairDisposition : byte
{
    Clean = 1,
    PendingBlocked = 2,
    TaintedMatched = 3,
    MismatchBlocked = 4
}

public sealed record HostProcessToolsDatabaseMarkerEvidence(
    Guid InstallationId,
    HostProcessToolsAuthorityState State,
    Guid? TransitionId,
    ulong? TaintMasterKeyVersion,
    CovenantDigest? TaintFingerprint,
    CovenantDigest DatabaseMarkerDigest);

public sealed record HostProcessToolsOsMarkerEvidence(
    Guid InstallationId,
    Guid TransitionId,
    ulong TaintMasterKeyVersion,
    CovenantDigest TaintFingerprint,
    CovenantDigest MarkerBytesDigest,
    CovenantDigest DurableIdentityDigest);

public sealed record HostProcessToolsMatchedPair(
    HostProcessToolsDatabaseMarkerEvidence Database,
    HostProcessToolsOsMarkerEvidence OsMarker);

public sealed record HostProcessToolsMarkerPairJoinResult(
    HostProcessToolsMarkerPairDisposition Disposition,
    HostProcessToolsMatchedPair? MatchedPair);

public interface IHostProcessToolsMarkerPairJoiner
{
    HostProcessToolsMarkerPairJoinResult Join(
        HostProcessToolsDatabaseMarkerEvidence database,
        HostProcessToolsOsMarkerEvidence? osMarker);
}
```

`TransitionId` is the sole durable idempotency identity. The offline command generates one cryptographically random nonempty UUID once and passes it unchanged; no separate operation identity exists. The exact `HostProcessToolsTransitionOutcome` declaration above is the sole owner of its four literal codes. `EnableAsync` validates Development edition and the exact environment opt-in from trusted process configuration, acquires `ArcanumMaintenanceLock` once, performs the complete database and OS-marker protocol, and releases the lock only after its durable terminal or blocked state. `AlreadyCompleted` is returned only for that same persisted transition ID and exact joined marker pair, while a different transition ID cannot adopt Pending or Tainted state.

The evidence records above are validated Core values. `Clean` database evidence requires null transition, taint version, and taint fingerprint. Pending and tainted evidence require a nonempty transition ID, positive taint version, and 32-byte fingerprint. OS evidence requires every field. The pure `IHostProcessToolsMarkerPairJoiner.Join` performs no I/O and compares installation identity, transition ID, taint-time master-key version, fingerprint, and applicable evidence digests in fixed time. Clean database plus absent OS evidence yields `Clean`. Pending database plus absent or exactly matching OS evidence yields `PendingBlocked`; a present mismatch yields `MismatchBlocked`. Tainted database plus exactly matching OS evidence yields `TaintedMatched` with the sole nonnull `MatchedPair`; absent or mismatched OS evidence yields `MismatchBlocked`. Every other invalid shape is rejected by the evidence constructors before `Join`. `HostProcessToolsTransitionService`, the startup gate, and Plan 04 full-installation reset must consume this exact interface and delegate every initial, resume, completed-replay, and pre-reset pair classification to it. Only Plan 04's attested reset verifier may authorize compare-deletion after a `TaintedMatched` result.

Exact test methods:

- `BufferedTurn_CommitsResponseLabelMutationAndFinalReceiptOnce`
- `StreamingTurn_SetsSensitivityBeforeEventAndCommitsOnce`
- `FallbackTurn_ReusesPlanAndReceiptsEveryPhysicalAttempt`
- `CompressionAndToolLoop_UseCallScopedMaterialization`
- `Cancellation_DiscardsCollectorAndPublishesNoMutation`
- `CampaignDeletionAfterResolution_AbortsBeforePromptToolOrProvider`
- `PathRevisionChange_AbortsBeforeProviderOrWorkspaceDispatch`
- `AuthorityEpochChange_AbortsBeforeNextProviderOrToolDispatch`
- `FeatureDisableMidTurn_DiscardsStagedIntentAndStopsDispatch`
- `NoContextTaintedSession_ReturnsSensitiveHistoryRequiresContext`
- `DisabledUntaintedStatelessTurn_HasNoCovenantCallsToolsOrPromptBytes`
- `DisabledAbsentCovenantPromptAndCachePlan_AreByteIdentical`
- `ProductiveProviderAndToolLoops_ExceedLegacyCapsWithConstantLiveEvidence`
- `HostToolsEnable_RequiresStoppedCleanInstallationAndPersistsDatabaseAndOsMarkers`
- `HostToolsTransitionOutcome_CodesAreLiteralAndExhaustive`
- `HostToolsEnable_FailureBeforeOsWriteCompareDeletesOnlyExactOperationMarker`
- `HostToolsEnable_OsWriteUncertaintyLeavesPendingBlocked`
- `HostToolsEnable_ReadbackMismatchLeavesPendingBlocked`
- `HostToolsEnable_DatabaseCasFailureAfterOsWriteLeavesPendingBlocked`
- `HostToolsEnable_SameTransitionIdResumesIdempotently`
- `HostToolsEnable_DifferentTransitionIdCannotTakeOverPendingState`
- `HostToolsProcess_StartsPermanentlyTaintedBeforeOpeningCovenant`
- `HostToolsStartupGate_RunsBeforeDatabasePoolsEnvelopeKeysAndPromptServices`
- `HostToolsStartupGate_IsFirstDatabaseOpenAndAllowsOnlyAuthenticatedFileTopologyRecoveryBeforeIt`
- `HostToolsStartupGate_UsesOneNonPooledCoreOnlyReadAfterOsPrecheck`
- `HostToolsStartupGate_NewInstallationSeedsCoreAuthorityOnSameConnectionBeforeOptionalInitialization`
- `HostToolsStartupGate_ValidatedLegacyPreCovenantCatalogInstallsCoreAndSeedsAuthorityBeforeOptionalInitialization`
- `HostToolsStartupGate_CurrentCovenantCoreMissingAuthorityIsCorruption`
- `HostToolsStartupGate_CleanWithAbsentMarkerStartsNormally`
- `HostToolsStartupGate_PendingStateAlwaysBlocks`
- `HostToolsStartupGate_TaintedWithoutMarkerAndCleanWithMarkerBothBlock`
- `HostToolsStartupGate_MalformedMarkerBlocksBeforeDatabaseOpen`
- `HostToolsStartupGate_RejectsEachMismatchedInstallationTransitionVersionAndFingerprintField`
- `GrimoireCliInitialization_UsesTheSameStartupGateAndCannotBypassTaintedPolicy`
- `HostToolsStartupGate_MatchingTaintedPairStartsCoreOnlyModeAdvertisesHostToolAndNeverOpensCovenant`
- `HostToolsStartupGate_TaintedMarkerEditionAndEnvironmentMatrixNeverRestoresCovenant`
- `HostToolsStartupGate_RejectsMarkerForDifferentInstallationIdentity`
- `HostToolsMarkerPairJoiner_ExhaustivelyClassifiesCleanPendingMatchedAndMismatchEvidence`
- `OrdinaryResetReinitializeAndCredentialCleanupRetainHostToolsMarkers`
- `InstallationReset_OrdinaryCleanupRetainsHostToolsDatabaseAndOsMarkers`
- `RestartKeyRotationOrConfigurationChange_CannotClearPermanentTaint`

- [ ] **Step 1: Add failing end-to-end race and disabled-path tests**

Use deterministic barriers at resolution, snapshot, begin, disclosure acknowledgement, provider dispatch, tool dispatch, sealing, and commit. Use real local persistence; fake only provider, external effect, and operating-system boundaries.

```csharp
[Fact]
public async Task FeatureDisableMidTurn_DiscardsStagedIntentAndStopsDispatch()
{
    TurnExecution execution = harness.StartEligibleTurn();
    await execution.WaitForStagedIntentAsync();

    harness.DisableCovenantAndAdvanceAvailabilityGeneration();
    execution.ReleaseNextProviderAttempt();

    TurnResult result = await execution.Completion;

    Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);
    Assert.Empty(await harness.CanonicalVersionsForTurnAsync(execution.TurnId));
    Assert.Equal(1, harness.ProviderDispatchCount);
}
```

- [ ] **Step 2: Run the end-to-end tests and witness the red state**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRuntimeAuthorityEndToEndTests|FullyQualifiedName~CovenantRuntimeDisabledPathTests|FullyQualifiedName~HostProcessToolsTransitionServiceTests|FullyQualifiedName~HostProcessToolsStartupGateTests|FullyQualifiedName~HostProcessToolsMarkerPairJoinerTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~InstallationResetCredentialCatalogTests"
```

Expected: FAIL at the first missing race recheck or integration seam. Record the exact failing method before changing production.

- [ ] **Step 3: Add the smallest missing rechecks and cleanup edges**

Revalidate canonical Campaign availability, path revision, root identity, read-authority epoch, feature generation, collector generation, and cancellation at their specified provider, MCP, tool, and commit boundaries. On failure, stop further effects, dispose capability registrations, clear sensitive buffers, abandon the branch, discard the collector, and terminalize the claim with typed evidence.

Implement the offline host-tools transition entirely inside `HostProcessToolsTransitionService.EnableAsync`. The service, rather than its CLI caller, acquires `ArcanumMaintenanceLock` exactly once. It requires Development edition, the exact opt-in environment value, a stopped host, zero Covenant canonical and protected artifacts, and a process that has never opened Covenant. Validate the request's random transition ID and commit `PendingHostToolsTaint` with that same ID. The exact bounded OS-secret marker payload is installation identity, transition ID, taint-time master-key version, and taint fingerprint in a pinned field order and length. Write it through the retained OS-secret capability, read back the same opened identity and exact bytes, delegate pair classification to `HostProcessToolsMarkerPairJoiner`, then compare-and-swap `HostToolsTainted`, authority epoch, and recovery-envelope epoch. Compensation may compare-delete only an operation-owned marker when failure is proven to have occurred before any uncertain OS write and the same opened identity plus exact bytes still match. An OS-write uncertainty, readback failure, database CAS failure, marker mismatch, or compensation failure leaves `PendingHostToolsTaint` blocked for manual remediation and can never publish a clean snapshot. Retrying the same transition ID resumes by phase and exact readback; a different ID cannot take over. The service returns the typed content-free result and the offline command exits after the durable transition.

Register `HostProcessToolsStartupGate` as the first phase permitted to open, classify, or read the Grimoire database in both `GrimoireDatabaseHostedService` and `GrimoireCliInitialization`. The host or CLI bootstrap owner acquires `ArcanumMaintenanceLock` exactly once and passes that same live nonserializable object to the gate. An earlier guarded-root topology reconciler may run under the same lock only when it opens no database or pool, publishes no authority, and limits effects to an authenticated operation journal's exact live, staged, and rollback root renames plus parent durability. Plan 04 restore recovery supplies that filesystem-only phase. Under the lock, the host-tools gate then reads and validates the OS marker first and calls Plan 01 `CovenantAuthorityBootstrapper.PrepareUnderInstallationLockAsync(heldInstallationLock, cancellationToken)` to open exactly one non-pooled, core-only SQLCipher connection without reacquiring or disposing the lock. The gate classifies the catalog without opening a pool or optional service. An empty new database is allowed only with an absent OS marker and runs Plan 01 `InstallCoreOnlyAsync` plus authority seed on that same connection. An exact validated pre-Covenant legacy catalog is allowed only with an absent OS marker, no current Covenant core metadata or object, and a complete match to Plan 01's closed `SupportedPreCovenantCoreManifest`; it runs the same core install, authority seed, and legacy backfill transaction, then reads the resulting `Clean` row before continuing. A missing, extra, drifted, partial, unknown, or mixed legacy catalog fails closed. If current Covenant core metadata or any current Covenant core object proves #74 was already installed, a missing `covenant_authority_state` row is corruption. For an existing current catalog with the row present, read only that row, close the connection, and pass its validated evidence plus the optional decoded OS marker to `HostProcessToolsMarkerPairJoiner` before publishing any authority snapshot.

The join has four exact outcomes. `Clean` plus an absent marker permits normal initialization. `PendingBlocked` and `MismatchBlocked`, including missing, malformed, wrong-installation, or otherwise mismatched database and OS evidence, block startup before any shared pool, prompt builder, MCP server, worker, or envelope service opens. `TaintedMatched` always publishes a permanent no-Covenant host policy, then permits only the core pool and non-Covenant prompt, MCP, and worker services to start. It advertises and enables the host-process escape hatch only when the live edition is Development and the exact `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` opt-in still validates. Removing or changing that opt-in suppresses the tool but never restores Covenant authority. That process never opens optional Covenant tables, protected artifacts, Covenant key or envelope services, Covenant availability, Covenant prompt injection, or Covenant tools. Restart, API-key rotation, edition, environment, or configuration changes never clear the policy. Ordinary reset, family reinitialize, and credential cleanup retain both markers. Only the dedicated full-installation reinitialize path, after explicit external-remediation attestation, creates a new installation identity and may compare-delete the exact database and OS marker pair.

- [ ] **Step 4: Run the Plan 03 focused gate to verify green**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Covenant|FullyQualifiedName~WizardIntelligenceProvider|FullyQualifiedName~TurnEngine|FullyQualifiedName~GrimoireTurnWriter|FullyQualifiedName~ModelCallExecutor|FullyQualifiedName~SystemPromptBuilder|FullyQualifiedName~ModelTokenEstimator|FullyQualifiedName~PromptCachePlanner|FullyQualifiedName~ToolRiskClassifier|FullyQualifiedName~WardAutoApproval|FullyQualifiedName~ArcanumInternalToolServer|FullyQualifiedName~InProcessMcpTransport|FullyQualifiedName~HostProcessToolsTransitionServiceTests|FullyQualifiedName~HostProcessToolsStartupGateTests|FullyQualifiedName~HostProcessToolsMarkerPairJoinerTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~InstallationResetCredentialCatalogTests"
```

Expected: PASS with no unexpected skip, sensitive-content diagnostic output, or background-task exception.

- [ ] **Step 5: Refactor the final runtime composition and rerun ordinary tests**

Split focused attempt construction, dispatch, maintenance, and finalization helpers out of `WizardIntelligenceProvider` only where the extraction keeps behavior and DI wiring explicit. Remove obsolete `PingRequestResolver`, turn-cumulative proposal gates, late provider mutation, Boolean finalization, pre-classification tool events, and any optional invocation-context overload.

Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test RetroDownfall.Arcanum.slnx --filter "Category!=Perf"
git diff --check
```

Expected: all commands exit zero. Plan 04 can now add the public authority, HTTP, CLI, and lifecycle surfaces without changing the runtime contracts in this plan.

## Plan 03 Completion Evidence

- Record the witnessed red failure and focused green command for every task in the implementation log owned by Plan 05.
- Confirm Plan 02 owns every canonical database, compiler, digest, search, schema, quota, cursor, and mutation-kernel implementation touched by these runtime contracts.
- Confirm Plan 04 owns pre-binding API-key authentication, `X-Arcanum-Context-Policy` parsing, HTTP status mapping, no-store headers, public Covenant endpoints, CLI, Compendium, protected-read route inventory, backup, restore, reset, retention, and erasure.
- Confirm no `ArcanumInvocationContext`, read-authority token, maintenance-authority token, Ward preflight token, or MCP capability nonce appears in any JSON context or persistence DTO.
- Confirm no provider, MCP handler, workspace tool, external sink, or assistant finalizer can reconstruct Campaign authority from a working directory, ambient DI, deserialized payload, or missing argument.
- Confirm exact Covenant arguments appear only in the provider continuation, in-memory compiler artifact and intent, authenticated Ward disclosure, and encrypted canonical publication.
- Leave the branch uncommitted for the coordinated final verification and single commit required by the master plan.
