# Covenant Domain and Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Covenant's deterministic Core protocol, canonical SQLCipher store, transactional mutation authority, evidence ledgers, owner cleanup, and failure-isolated FTS5 accelerator.

**Architecture:** Pure Core types compile authored values and link bounded snapshots without database or provider dependencies. Infrastructure uses parameterized raw SQLite commands inside explicit transactions for the authority-grade canonical tier. The FTS5 tier consumes a text-free canonical outbox and never participates in prompt authority or canonical commit success.

**Tech Stack:** .NET 10, C# 14, Native AOT, System.Security.Cryptography, Microsoft.Data.Sqlite, SQLCipher 4.17, SQLite FTS5, xUnit.

## Global Constraints

- Execute after the native runtime and schema-catalog foundations in Plan 01 are green. Pure Core tasks 1 through 6 may run in parallel with those foundations.
- Keep all files under `src/RetroDownfall.Arcanum.Core/Covenant/` free of SQLite, provider, HTTP, CLI, EF, and ambient-context types.
- Use one exact policy-v1 canonical contract. Every `CovenantDigest` and 32-byte Bloom is raw fixed-width data with no length prefix; strict UTF-8 strings and arbitrary bytes remain `u32`-length-prefixed. No authority digest may use `ToString()`, runtime enum names, default JSON, culture-sensitive formatting, or mutable collections.
- Store authored text and precompiled fragments only in the encrypted canonical tier. Outbox, receipts, cursors, logs, and diagnostics remain content-free unless the protected contract explicitly returns content.
- Canonical reads use one bounded query and one SQLite read snapshot. Canonical writes use a caller-owned immediate transaction and one mutation kernel.
- Every integer increment, byte count, allocation counter, epoch, sequence, row ID, and ordinal fails before overflow.
- Observe every focused red failure before adding production code.

---

## Task 1: Lock the closed domain vocabulary and limits

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLimits.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantModels.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ContentSensitivity.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/GenerationProvenance.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ArtifactSensitivityLabel.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/CovenantContextPolicy.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Intelligence/InvocationAttendance.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/SessionCampaignBinding.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/CanonicalCampaignContext.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDomainContractTests.cs`

**Interfaces:**

- Produces these literal Core enum contracts. `CovenantEnums.cs` is the sole owner of `CampaignPathIdentityOperation`; `SessionCampaignBinding.cs` is the sole owner of `SessionCampaignBindingKind`:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathIdentityOperation>))]
public enum CampaignPathIdentityOperation : byte
{
    Register = 1,
    Update = 2,
    RepairMoved = 3,
    Deregister = 4,
    TakeoverOrphan = 5
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionCampaignBindingKind>))]
public enum SessionCampaignBindingKind : byte
{
    GlobalOnly = 1,
    Campaign = 2,
    LegacyUnresolved = 3
}
```

- [ ] Write literal tests named `Policy_v1_enum_codes_are_immutable`, `Campaign_path_and_session_binding_codes_are_immutable`, `Hard_limits_match_the_approved_contract`, and `Invalid_cross_field_models_are_rejected`. Cover every code and limit listed in the specification, including scope, lane, lifecycle, origin, mutation, placement, the three-code Session-turn surface protocol, sensitivity, artifact, disclosure, Ward, claim, finalization, and recovery states. Pin the two cross-plan enums independently so their recovery and wire contracts cannot drift:

```csharp
[Fact]
public void Campaign_path_and_session_binding_codes_are_immutable()
{
    Assert.Equal((byte)1, (byte)CampaignPathIdentityOperation.Register);
    Assert.Equal((byte)2, (byte)CampaignPathIdentityOperation.Update);
    Assert.Equal((byte)3, (byte)CampaignPathIdentityOperation.RepairMoved);
    Assert.Equal((byte)4, (byte)CampaignPathIdentityOperation.Deregister);
    Assert.Equal((byte)5, (byte)CampaignPathIdentityOperation.TakeoverOrphan);

    Assert.Equal((byte)1, (byte)SessionCampaignBindingKind.GlobalOnly);
    Assert.Equal((byte)2, (byte)SessionCampaignBindingKind.Campaign);
    Assert.Equal((byte)3, (byte)SessionCampaignBindingKind.LegacyUnresolved);
}
```

Plan 03 owns its separate nonserialized runtime `ArcanumExecutionSurface` authority classification.

- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CovenantDomainContractTests`. Expected: compilation fails because the Covenant domain vocabulary does not exist.
- [ ] Add explicit `byte`, `ushort`, `uint`, or `ulong` backed enums with fixed numeric values. `CovenantEnums.cs` owns every policy-v1 code in the approved table, including the exact `CampaignPathIdentityOperation` contract above, Session-turn surface, maintenance step/checkpoint, claim state, backup phase, assistant finalization origin, and all provider option codes. `SessionCampaignBinding.cs` owns the exact `SessionCampaignBindingKind` contract above. Every policy enum later carried by a Plan 04 request or response, including scope, lane, lifecycle, origin, mutation outcome, plan decision, admission decision, placement, Campaign-path operation, and Session-binding kind, has an exact `StringOnlyJsonStringEnumConverter<TEnum>` attribute so numeric JSON fails. `CovenantContextPolicy.cs` defines `Default=1` and `None=2`; `InvocationAttendance.cs` defines `Attended=1` and `Unattended=2`. Existing `ToolPolicy` remains the public JSON enum, while the policy manifest pins its canonical `AllTools=1`, `NoTools=2`, `ReadOnlyTools=3`, and `NoForbiddenArts=4` mapping. Add validated readonly records for key identities, generations, revisions, content hashes, bounded counts, the sensitivity code, the eight-slot or Bloom provenance shape, and the artifact-label shape. Define exactly-one `SessionCampaignBinding` whose discriminant is `SessionCampaignBindingKind`, plus `CanonicalCampaignContext` carrying binding and captured Campaign availability/path identity facts. Add constants for every hard ceiling. Task 5 supplies the sensitivity algebra and digest behavior.
- [ ] Rerun the focused command. Expected: all contract tests pass with literal numeric expectations.
- [ ] Refactor repeated validation into small Core helpers, then rerun the focused command and `dotnet build src/RetroDownfall.Arcanum.Core/RetroDownfall.Arcanum.Core.csproj`.

## Task 2: Implement policy-v1 canonical encoding

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCanonicalEncoder.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ArcanumCanonicalJsonV1.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPolicyV1Manifest.cs`
- Create: `scripts/generate-covenant-v8-oracle.mjs`
- Create: `scripts/generate-covenant-v8-oracle.md`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCanonicalEncoderTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCanonicalJsonTests.cs`

- [ ] Write hand-derived byte vectors for unsigned and signed widths, two's-complement big-endian integers, normalized binary64, option presence, strict UTF-8, length-prefixed bytes, ordered lists, domain tags, and every policy-v1 enum code. Test NaN, infinity, malformed UTF-16, excessive lengths, and negative zero.
- [ ] Write RFC 8785-compatible JSON vectors for object key ordering, UTF-8, finite number restrictions, escaped characters, tool arguments, and JSON schemas.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCanonicalEncoderTests|FullyQualifiedName~CovenantCanonicalJsonTests"`. Expected: compilation fails on the absent encoders.
- [ ] Implement span-based writers that accept only validated typed values, reject nonfinite values, encode negative zero as positive zero, and never allocate a second full payload copy.
- [ ] Rerun the focused command. Expected: every literal vector matches exactly.
- [ ] Add a cross-culture loop over all installed test cultures and rerun. Expected: encoded bytes remain identical.

## Task 3: Pin Unicode 17 text safety and authored compilation

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantCompiler.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCompiler.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantUnicodePolicyV1.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantUnicode17Tables.g.cs`
- Create: `scripts/generate-covenant-unicode17.py`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCompilerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantUnicodePolicyTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/NormalizationTest.nfc.bin`
- Create: `tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/PROVENANCE.md`
- Create: `tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/LICENSE-Unicode-3.0.txt`

- [ ] Add red tests for the ASCII key grammar, 128-character key limit, exact 2,048-byte authored-content limit, whitespace-only refusal, strict UTF-8, NFC, Unicode 17 normalization, NUL, C0/C1 controls, bidi controls and marks, every rejected Format code point, line endings, authored preservation, renderer framing, and adaptive fence length.
- [ ] Add a checked-in corpus test that runs the same expected hashes under invariant globalization and the normal JIT host.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCompilerTests|FullyQualifiedName~CovenantUnicodePolicyTests"`. Expected: compilation fails because compiler policy does not exist.
- [ ] Implement key validation, vendored Unicode data lookup, policy-v1 whitespace normalization, exact byte accounting, authored hash, rendered hash, immutable compiled fragment, and fence selection.
- [ ] Rerun the focused command. Expected: exact strings, byte counts, fence lengths, and hashes pass.
- [ ] Add the compiler corpus entry point required by the Native AOT smoke project in Plan 05, without introducing a reflection dependency.

## Task 4: Implement exact digests and rolling chains

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCanonicalEncoder.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCanonicalEncoderTests.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEvidenceChains.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/FrozenProviderOptions.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ProviderCallEnvelope.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ProviderCallMaterializationSnapshot.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantFinalReceipt.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDisclosureContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDisclosureState.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDisclosureStateAlgebra.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestCorpus.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDigestVectorTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantEvidenceChainTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDisclosureStateAlgebraTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDigestCorpusTests.cs`

**Interfaces:**

- Consumes: Task 1 policy codes and validated scalar identities plus Task 2 canonical writers. This task extends the shared writer only with `WriteFixed32(ReadOnlySpan<byte>)` and a streaming `CovenantCanonicalHashWriter` whose primitives are byte-identical to the bounded encoder and whose SHA-256 finalization is single-use.
- Produces: pure Core preimage records `SectionDigestInput`, `MutationRequestDigestInput`, `PreflightBodyDigestInput`, `AuthorizationDigestInput`, `MutationDigestInput`, `SnapshotDigestInput`, `PlanDigestInput`, `MaterializationDigestInput`, `SensitivityDigestInput`, `ArtifactLabelDigestInput`, `SessionTurnRequestDigestInput`, `SessionTurnExecutionDigestInput`, `ProviderOptionsDigestInput`, `ProviderCallDigestInput`, `AdmissionDigestInput`, `WardEvidenceDigestInput`, `ProviderDispatchEffectDigestInput`, `MaintenanceDispatchEffectDigestInput`, `ToolEgressEffectDigestInput`, `ManagedFileEffectDigestInput`, `BackupDisclosureEffectDigestInput`, `ExternalDisclosureDigestInput`, `ExternalDisclosureStateDigestInput`, `CampaignPathApplyRequestDigestInput`, `SessionBindingApplyRequestDigestInput`, `FamilyReinitializeApplyRequestDigestInput`, `FinalReceiptDigestInput`, `TurnAggregateDigestInput`, and `CursorFilterDigestInput`. It also produces `ProviderCallSensitivity(ContentSensitivity Level, GenerationProvenance Provenance, CovenantDigest Digest)`, immutable `CovenantDisclosureState`, the only local-increment and restore-join algebra, and reflection-free `CovenantDigestCorpus.Run()` for Plan 05. Later domain models construct one of these inputs and call the single corresponding `CovenantDigests` method.

- [ ] Extend `CovenantCanonicalEncoderTests` with hand-derived vectors proving `WriteFixed32` writes 32 raw bytes while arbitrary bytes retain their `u32` prefix, rejects every other fixed length, and produces byte-identical output through `CovenantCanonicalHashWriter` without a whole-preimage replay buffer.
- [ ] Write independently derived literal SHA-256 vectors against every pure preimage record. Include all three `SectionDigestInput` placements and the empty-section representation, mutation request, authorization, final mutation, snapshot, plan with three eligible Section digests, materialization, frozen provider call, Admission with three admitted Section digests, Ward, turn request, final receipt, disclosure effect, cursor filter, and aggregate domains. These tests must not instantiate a model owned by Task 6, 9, 12, or a later plan.
- [ ] Write exact seed and update vectors for AttemptChain, BranchChain, and DisclosureChain, including zero ordinal, optional presence bytes, fork parent, prior digest, and checked `u64` counts.
- [ ] Add literal disclosure-state vectors for the sole empty value `Exact,false,0,0,zeroBloom`, malformed empty/nonempty refusal, checked local increment, Exact-to-LowerBound monotonicity, Boolean OR, timestamp maximum, raw 32-byte Bloom OR, and restore join using unsigned maximum. Prove restore join is associative, commutative, and idempotent, while local increment is applied exactly once and is deliberately not a semilattice join.
- [ ] Add provider-neutral vectors for positive signed generations, GlobalOnly absence, the paired path block, both-or-neither fork parents, GUID byte ordering, the raw-GUID-sorted resolved-attachment dependency vector, supplied-order preservation for provider-visible collections, ToolCall and ToolResult fields, optional tool output schema, named-tool and JSON-schema union refusal, and every valid strict tri-state.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCanonicalEncoderTests|FullyQualifiedName~CovenantDigestVectorTests|FullyQualifiedName~CovenantEvidenceChainTests|FullyQualifiedName~CovenantDisclosureStateAlgebraTests|FullyQualifiedName~CovenantDigestCorpusTests"`. Expected: compilation fails because the fixed writer, digest protocol, disclosure algebra, and digest corpus do not exist.
- [ ] Implement every named preimage record in `CovenantDigestModels.cs`, plus the immutable provider-options, provider-call, materialization, final-receipt, disclosure-draft, disclosure-receipt, and disclosure-state models named above. Each `SectionDigestInput` owns one placement-specific section vector or its zero-item representation. Plan and Admission each require Global Confirmed, Campaign Confirmed, and Campaign Proposed Section digests. `CovenantDisclosureStateAlgebra.IncrementLocal` uses checked addition and preserves LowerBound once set. `JoinRestore` uses unsigned maximum for overlap-safe counts, OR for `EverOccurred` and Bloom, maximum timestamp, and monotonically returns LowerBound for a nonidentical branch join. Implement the approved raw fixed-32 primitive and streaming SHA-256 writer over the exact field-order tables. Digest inputs accept only already bounded provider-neutral strings, bytes, messages, parts, tools, and schemas; downstream surface plans own their operational caps. Preimage records contain only already-owned validated scalar values, immutable arrays, and digests, so this task has no dependency on later snapshot, mutation, cursor, claim, or Ward models. Keep diagnostic IDs distinct from installation-keyed diagnostic HMACs.
- [ ] Implement `CovenantDigestCorpus.Run()` as the sole reflection-free Core corpus entry point for these Task 4 literals. It executes every domain, Section placement, provider union and subrecord, ordering vector, rolling recurrence, disclosure algebra case, and stable aggregate without filesystem, culture, runtime Unicode, provider SDK, or reflection dependencies so Plan 05 can call the exact runner in every shipping Native AOT RID.
- [ ] Rerun the focused command. Expected: every literal vector and recurrence passes.
- [ ] Add permutation tests that prove GUID ordering uses raw RFC 4122 bytes, exact generation IDs, Section items, materialization sources and occurrences, resolved attachment version identities, and logit biases use only their specified comparators, and every other collection preserves supplied order.

## Task 5: Implement bounded sensitivity provenance

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ContentSensitivity.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/GenerationProvenance.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ArtifactSensitivityLabel.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/GenerationProvenanceTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/ArtifactSensitivityLabelTests.cs`

- [ ] Write literal tests for zero through eight exact generation IDs deduplicated and sorted by unsigned lexicographic comparison of their raw RFC 4122 network-order bytes, the ninth-ID Bloom transition, first four UInt16BE words modulo 256, duplicate bit positions, duplicate inputs, overlap, permutation, associative, commutative, and idempotent merges.
- [ ] Test that Bloom overflow never claims exact set membership and that `Sensitivity.v1` binds only the sensitivity code plus exact leaves or Bloom bits. Test separately that `ArtifactLabel.v1` binds artifact kind and identity, optional Session, Campaign, and turn owners, revision, content digest, Sensitivity digest, paired producing plan and admission digests, and mutually exclusive maintenance-receipt evidence.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GenerationProvenanceTests|FullyQualifiedName~ArtifactSensitivityLabelTests"`. Expected: the Task 1 shapes compile, but Bloom transition, merge algebra, and exact label digest assertions fail.
- [ ] Implement the fixed eight-ID representation with exact IDs deduplicated and sorted by unsigned lexicographic raw RFC 4122 network-order bytes, 256-bit Bloom overflow, monotonic maximum-sensitivity merge, and exact artifact-label digest.
- [ ] Rerun the focused command. Expected: all algebraic and literal vectors pass.
- [ ] Add randomized property tests with a fixed seed and bounded case count. Expected: merge laws remain stable across repeated runs.

## Task 6: Build the pure snapshot linker and admission receipt contract

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantTurnSnapshot.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantTurnPlan.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantLinker.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLinker.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantAdmissionReceipt.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantLinkerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantAdmissionReceiptTests.cs`

- [ ] Add plan tests for Global fallback, Campaign shadowing, independent lanes, review-only Proposed, quarantined and invalid candidates, retired heads, randomized storage order, and cross-Campaign isolation.
- [ ] Add receipt-contract tests for an immutable eligible-only decision vector, stable plan reuse, final provider/model/tokenizer and materialization identities, frozen provider-call digest, checked token and byte counts, and exact admitted or pressured reason codes supplied by a later planner. Add integration vectors proving `CovenantTurnSnapshot`, `CovenantTurnPlan`, and `CovenantAdmissionReceipt` construct Task 4's snapshot, plan, and admission preimages without re-encoding fields.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantLinkerTests|FullyQualifiedName~CovenantAdmissionReceiptTests"`. Expected: compilation fails on missing linker and receipt types.
- [ ] Implement only pure deterministic snapshot resolution and validated immutable plan and receipt construction over immutable arrays. `CovenantAdmissionReceipt` accepts the final decision vector and provider-attempt facts, validates their closed shape against the plan, and delegates hashing to Task 4. It does not choose pressure outcomes or reference a provider SDK.
- [ ] Rerun the focused command. Expected: exact selected order, exclusion reasons, receipt validation, and digests pass.
- [ ] Add an allocation assertion for repeated plan reuse and verify no authored or rendered string is duplicated by the linker.

Plan 03 Task 8 is the sole owner of provider-specific context pressure. It resolves the provider, model, tokenizer profile, context window, frozen provider-visible options, complete system prompt, ordinary messages, tools, and materialization candidates before applying Confirmed all-or-fail and Proposed longest-prefix admission. Do not add a second preliminary pressure policy or tokenizer abstraction in this task.

## Task 7: Implement the generation-bound Covenant operation gate

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationLeaseContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantOperationGate.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateConcurrencyTests.cs`

**Interfaces:**

```csharp
public interface ICovenantOperationLease : IAsyncDisposable
{
    CovenantOperationLeaseSnapshot Snapshot { get; }

    CancellationToken Revocation { get; }

    ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken);
}

public interface ICovenantSnapshotReadLease : ICovenantOperationLease
{
}

public enum CovenantExclusiveLeaseDisposition : byte
{
    RollbackAndReopen = 1,
    CommitAndReopen = 2,
    KeepClosed = 3
}

public interface ICovenantExclusiveOperationLease : ICovenantOperationLease
{
    ValueTask<Result> CompleteAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);
}

public interface ICovenantExclusivePostDispositionFinalizer
{
    ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken);
}

public sealed class CovenantNoOpPostDispositionFinalizer
    : ICovenantExclusivePostDispositionFinalizer
{
    public static CovenantNoOpPostDispositionFinalizer Instance { get; } = new();

    private CovenantNoOpPostDispositionFinalizer()
    {
    }

    public ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}

public enum CovenantExclusiveOperation : byte
{
    CampaignPathMutation = 1,
    CampaignDelete = 2,
    ProtectedSessionTransfer = 3,
    SchemaRepair = 4,
    BackupRestore = 5,
    CovenantFamilyReinitialize = 6,
    CovenantReset = 7,
    HealthyCatalogFactoryErasure = 8
}

public readonly record struct CovenantExclusiveRecoveryOwner(
    Guid OperationId,
    CovenantExclusiveOperation Operation,
    CovenantDigest EffectDigest);

public interface ICovenantOperationGate
{
    ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken);
}
```

`CovenantOperationScope` and `ProtectedTransferScope` are separate validated nonserializable readonly values with the same closed persisted shape: `Global` requires no Campaign ID and `Campaign` requires one nonempty Campaign ID. `ProtectedTransferScope` is the only scope accepted by protected fork or selective import and is exactly the shape persisted by Plan 01 Task 11 for recovery. Installation coverage is not a third persisted Covenant scope. `CovenantInstallationReadLease` is the concrete nonserializable all-scopes capability, and its concrete type is the closed coverage marker. `CovenantExclusiveRecoveryOwner` requires a nonempty operation ID, one of the eight immutable operation codes above, and a 32-byte stable effect digest. `CampaignPathMutation` and `CampaignDelete` are valid only for Campaign-exclusive acquisition. `ProtectedSessionTransfer` is valid only for the compound transfer acquisition. The remaining five values are valid only for global exclusive acquisition. `CovenantOperationLeaseSnapshot` binds registration identity, dataset generation, capability generation, authority epoch, either installation coverage or one validated operation scope, optional Campaign availability and path revisions, the accelerator epoch and applied Campaign-deletion sequence where applicable, and the exact exclusive recovery owner for an exclusive lease.

`CovenantInstallationReadLease`, `CovenantReadLease`, `CovenantTurnLease`, and `CovenantProtectedTransferLease` implement `ICovenantSnapshotReadLease`. `CovenantCampaignExclusiveLease`, `CovenantProtectedTransferLease`, and `CovenantExclusiveLease` implement `ICovenantExclusiveOperationLease`. The exact `CovenantExclusiveLeaseDisposition` declaration above is the sole owner of its three literal codes. `CompleteAsync` is one-shot. Rollback may reopen only before a durable transition; commit may reopen only after the caller has published the matching health and generation transition; either successful reopen disposition clears the recovery-owner binding. Keep-closed releases the live exclusive registration while preserving the exact closed-scope recovery owner and leaves admission closed. Disposing before a successful disposition or failure of a one-shot disposition has the same closed-owner result and never attempts a second disposition.

`ICovenantExclusivePostDispositionFinalizer` is the sole Core hook for a durable lifecycle journal that must remain adoptable until gate disposition succeeds. It is one-shot, nonserializable, and called only after the exact lease returns success from `CompleteAsync` and before lease disposal. A failed disposition skips it. A finalizer failure cannot request another disposition and leaves the durable journal nonterminal. `CovenantNoOpPostDispositionFinalizer.Instance` is the exact sealed no-op singleton and is allowed only for a lifecycle with no post-disposition durable transition, including Plan 04's authenticated zero-marker restore arm. Null, delegate, ambient lookup, and a later plan's duplicate interface are forbidden. Plan 03 protected transfer and Plan 04 Campaign marker, schema-repair, restore, and response carriers consume this exact contract.

Every initial exclusive acquisition records the exact recovery owner before rejecting new affected work and draining registrations. Initial Campaign and Campaign-scoped transfer acquisition requires the Campaign to exist and match the live immutable scope. A different operation ID, operation code, effect digest, or scope cannot observe, replace, or resume that closed owner. The three `Resume...Async` methods never start new work. In the same process they succeed only against the exact kept-closed owner. During bootstrap after process restart, they may reconstruct that binding only while the gate is in its pre-readiness recovery phase and only from the exact owner and scope validated from the registered operation's durable journal or intent; once readiness is published, absence of an existing kept-closed owner is a refusal. `ResumeCampaignExclusiveAsync` and Campaign-scoped `ResumeProtectedTransferAsync` may accept a historical now-deleted Campaign only when the exact core deletion, marker-cleanup, or transfer journal proves the same Campaign scope, recovery owner, and terminal cleanup-only authority. That historical arm cannot publish a path, Session graph, or other new Campaign-owned state and may only finish or compensate operation-owned effects. Resume returns a new exclusive lease over the already-closed scope without reopening admission or redraining unrelated work. Wrong-ID takeover, wrong effect, wrong operation kind, wrong scope, unjournaled historical Campaign, duplicate live recovery, and ordinary `Acquire...` against a kept-closed owner all fail closed. A successful recovery later chooses one one-shot disposition under the same rules.

- [ ] Add red tests for every exact method above, all eight operation codes, every lease interface, both persisted scope truth tables, installation coverage, all three exclusive dispositions, the one-shot post-disposition finalizer, and the sealed no-op finalizer. Include kept-closed same-process resume, pre-readiness restart adoption from a validated durable owner, exact cleanup-only recovery for a journaled historical Campaign, initial acquisition refusal for a missing Campaign, historical recovery refusal without the matching journal, wrong-ID/effect/kind/scope takeover refusal, duplicate recovery refusal, failed-disposition finalizer suppression, finalizer failure without a second disposition, and the prohibition on post-readiness reconstruction without an existing closed owner. `AcquireInstallationReadAsync` returns the sole all-scopes snapshot-read capability used by installation-wide status, list, query, and full-backup snapshots. It conflicts with a closing Global scope and every closing Campaign scope. Any Global, Campaign, protected-transfer, reset, restore, or family-exclusive close rejects new installation-read acquisition and drains every existing installation-read registration before returning its exclusive lease. `AcquireProtectedTransferAsync` closes and drains the matching Global or Campaign scope once, then returns one compound exclusive lease that also exposes the bounded snapshot-read capability needed by fork or selective import. It replaces any read-then-exclusive sequence. `AcquireCampaignExclusiveAsync` remains the exact lease for path mutation and Campaign deletion. Every lease binds the exact snapshot fields above.
- [ ] Add linearizability tests for acquire versus close, one-time disposal, child cancellation, stale revalidation, Campaign-scoped isolation, installation-read versus each Campaign and Global close, all-scope exclusion, nested acquisition refusal, protected-transfer acquisition with no self-deadlock, deterministic scope ordering, and a late task attempting to use a disposed lease. Prove an all-scope consumer passes its one `CovenantInstallationReadLease` through every database read and serialization step without acquiring a nested Global or Campaign lease. Prove a caller cannot combine a separate read lease with a protected-transfer lease and that the compound lease alone supports snapshot reads until its final disposition. Add a 32-reader/eight-writer randomized schedule with a fixed seed.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantOperationGateTests|FullyQualifiedName~CovenantOperationGateConcurrencyTests"`. Expected: compilation fails because the gate and lease contracts do not exist.
- [ ] Implement immutable lease snapshots in Core and one process-wide Infrastructure gate. The gate tracks installation-read registrations in a disjoint all-scopes set that every Global or Campaign exclusive close includes in its atomic reject, signal, and drain set. Acquiring an installation read fails while any Global, Campaign, protected-transfer, or installation-wide close is pending; it never loops through or nests per-Campaign acquisitions. Initial close atomically records its `CovenantExclusiveRecoveryOwner`, rejects new affected acquisition, signals the affected cancellation sources, drains exact in-flight lease registrations, and returns an exclusive lease whose one-shot disposition controls reopening. Keep-closed and failed-disposition state retain that owner after the live registration is released. Resume follows the exact same-process and pre-readiness adoption rules above and cannot become a second initial acquisition. Protected transfer uses that same close-and-drain transition to construct its single compound snapshot/exclusive lease without a nested acquire. Every take, release, cleanup, resume, and revalidation compares the exact registration and recovery identities and captured generations so delayed cleanup cannot remove or adopt a reused slot.
- [ ] Keep the hot acquire and revalidate paths memory-only. They perform no secret-store or database I/O, use checked monotonic counters, and return typed stale, closing, cancellation, and active-work results without logging Campaign IDs or protected content. Global exclusive acquisition drains every Campaign scope; Campaign-exclusive acquisition leaves unrelated Campaign and Global-only turns available.
- [ ] Rerun the focused command. Expected: all lifecycle, ABA, cancellation, stale-generation, and concurrency assertions pass with no leaked registrations.
- [ ] Extend the tests with reset, restore, Campaign deletion, path-remap, accelerator-batch, cleanup-batch, and stream-serialization race fixtures. Expected: an exclusive operation cannot report completion while a matching reader, turn, MCP use, writer, accelerator batch, or cleanup batch can still disclose or commit under the old generation.

## Task 8: Implement the bounded canonical store

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantStore.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLaneHeadProbe.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantManagementReadContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantAvailability.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantAvailability.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantStoreTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantQueryPlanTests.cs`

- [ ] Add tests for `ReadTurnSnapshotAsync` one-command `LIMIT 161` loading, 160-candidate acceptance, overflow refusal, Global and Campaign selection, tombstones, owner existence, independent lanes, random insertion order, exact hashes, one read snapshot, lease mismatch, and cancellation. Add `ProbeLaneHeadAsync` tests for present, retired, absent, Campaign isolation, exact normalized-key index use, one-command execution, and rejection of a non-snapshot lease.
- [ ] Add bounded canonical management-read tests for list pages clamped to 1 through 200, exact scoped-key detail with both lane heads, descending version pages, one immutable version's maximum 64 exact sources, and mutation-effect snapshots with exact affected count and digest plus at most 50 examples. Test that all-scope list and Global effect scans across all current Campaign IDs and matching shadows accept exactly one caller-owned `CovenantInstallationReadLease`, reject a scoped lease before SQL, and perform no nested acquisition. Test Campaign effect scans with an exactly matching scoped or installation lease, empty results, typed cursor tuples, and a Campaign-registry or key-epoch change before snapshot completion.
- [ ] Add `EXPLAIN QUERY PLAN` assertions for the scoped active-head, stable list, descending version, provenance, Campaign-registry, and normalized-key effect indexes. Prove one command for the turn snapshot, one command per management page or detail, bounded streaming for Global effects, and no N+1 query with maximum provenance.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantStoreTests|FullyQualifiedName~CovenantQueryPlanTests"`. Expected: compilation fails because the store does not exist.
- [ ] Implement this exact prepared, parameterized Core port over immutable contracts:

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
```

Every method accepts only the Task 7 snapshot-read capability and validates its exact coverage and generations before opening its one read transaction. `ReadTurnSnapshotAsync` and `ProbeLaneHeadAsync` require the matching persisted Global or Campaign scope. An all-scopes list or a Global mutation-effect scan across current Campaign shadows requires `CovenantInstallationReadLease`. Scoped detail, version, source, and list reads accept either that installation lease or one exactly matching scoped read lease. A concrete lease with insufficient or mismatched coverage fails before SQL and can never be supplemented by a nested acquisition. `CovenantManagementReadContracts.cs` defines the five query/result pairs, stable keyset tuples, typed scope/lane/lifecycle filters, exact revision and epoch facts, effect-decision codes, dependent-head vector digest, exact affected-Campaign count, a maximum-50 example array, and truncation. It contains no API DTO, plaintext cursor token, provider type, or mutable collection. Validate compiler policy, artifact hash, stored aggregate shape, dataset, owner, and capability health before returning any result.
- [ ] Rerun the focused command. Expected: all result and plan assertions pass with the recorded command count.
- [ ] Add concurrent writer tests that prove a reader observes one generation-consistent SQLite snapshot.

## Task 9: Implement the transactional mutation kernel and replay ledger

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationTransaction.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationKernelTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationConcurrencyTests.cs`

- [ ] Add red tests for create, update, retire, reactivation, lane CAS, tombstone probe, Global Proposed refusal, authored and compiled no-change, immutable receipts for every `NoChange`, and same-ID same-request replay after later head changes. Add literal integration vectors proving the mutation contracts delegate request, preflight, authorization, and final mutation hashing to Task 4's preimage records.
- [ ] Add conflict tests for same mutation ID with different request digest, expired or rotated preflight replay, stale revision, wrong authority, key epoch and reclamation ABA, Campaign registry change, and `long` overflow. Add a compile-time contract test proving the kernel exposes only `ApplyBatchAsync(CovenantMutationBatch, CovenantMutationTransaction, CancellationToken)` and has no overload that accepts a separate authenticated command or compiled artifact.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantMutationKernelTests|FullyQualifiedName~CovenantMutationConcurrencyTests"`. Expected: compilation fails because the mutation kernel is absent.
- [ ] Define immutable `CovenantMutationIntent`, `CovenantMutationBatch`, `CovenantMutationReceipt`, and outcome contracts in Core. Each validated intent carries its canonical request, authenticated authorization evidence, exact compiled artifact when required, and all request, authorization, plan, admission, and provenance digests. A sealed batch carries only those validated intents plus their common transaction-bound generation facts. No separate mutable authenticated command or compiled-artifact parameter may cross the kernel boundary.

Implement exactly:

```csharp
public sealed class CovenantMutationKernel
{
    public ValueTask<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyBatchAsync(
        CovenantMutationBatch batch,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);
}
```

`ApplyBatchAsync` receives the caller-owned `CovenantMutationTransaction` over one centrally initialized `SqliteConnection` and immediate `SqliteTransaction`. Resolve receipt replay after authentication and request canonicalization, then apply CAS, quotas, immutable state, epochs, one search-sequence allocation, and deterministic outbox rows without opening, committing, or retrying a transaction.
- [ ] Rerun the focused command. Expected: all state, receipt, conflict, and rollback assertions pass.
- [ ] Add an eight-writer contention test and verify no write occurs outside the caller transaction.

## Task 10: Enforce quotas and bounded evidence storage

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantQuotaContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantQuotaGuard.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantTurnReceiptCompactor.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantQuotaTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantFinalizationCapacityTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantTurnReceiptCompactionTests.cs`

**Interfaces:**

- Consumes: Plan 01 Task 6 `TurnCapacityMutation` connection authorization; Plan 01 Task 10 `assistant_finalization_capacity_reservations`, `session_turn_quota_state`, and `installation_turn_quota_state`; Task 9's caller-owned `CovenantMutationTransaction`.
- Produces: one transaction-bound `CovenantQuotaGuard` for canonical mutation quotas plus public-claim reservation, direct guard allocation, reservation consume/release, and whole-Session capacity release used by Plans 03 and 04.

- [ ] Add exact boundary tests for 1,023 Set versions plus one retirement, active entry and lane maxima, 64 sources, mutation receipts, logical bytes, reserved retirement count and bytes, key churn, turn receipt tails, one aggregate row per Session, and installation-wide thresholds. Add exact 16,384 per-Session and 1,048,576 installation-wide public-claim and assistant-finalization capacity tests against Plan 01's separate counters.
- [ ] Add reservation tests for atomic claim-plus-future-guard reserve, exact `Reserved -> Consumed` and `Reserved -> Released` transitions, same-identity replay, different-identity conflict, direct Internal/Imported/Forked allocation, over-limit rollback, counter overflow and underflow, crash before commit, and exact Session-retention decrement. Prove a failed reserve or allocation leaves no claim, reservation, counter delta, placeholder, or guard. Plan 03 owns the separate assertion that no provider or filesystem side effect occurs after such a failure.
- [ ] Test that productive provider/tool evidence beyond 64 and 10,000 steps remains O(1) through the Task 4 rolling chains without adding a turn-step limit here.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantQuotaTests|FullyQualifiedName~CovenantFinalizationCapacityTests|FullyQualifiedName~CovenantTurnReceiptCompactionTests"`. Expected: compilation fails on missing quota contracts, transaction-bound guard, and compactor.

Implement this exact transaction-bound capacity API in addition to the canonical mutation-quota checks:

```csharp
public sealed class CovenantQuotaGuard
{
    public ValueTask<Result<AssistantFinalizationCapacityReservation>> ReserveClaimAndFinalizationAsync(
        SessionTurnCapacityReservationRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);

    public ValueTask<Result<AssistantFinalizationCapacityReservation>> ConsumeReservedFinalizationAsync(
        AssistantFinalizationCapacityIdentity identity,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);

    public ValueTask<Result<AssistantFinalizationCapacityReservation>> ReleaseReservedFinalizationAsync(
        AssistantFinalizationCapacityIdentity identity,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);

    public ValueTask<Result<AssistantFinalizationCapacityReservation>> AllocateDirectFinalizationAsync(
        DirectFinalizationCapacityRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);

    public ValueTask<Result> ReleaseSessionCapacityAsync(
        SessionTurnCapacityReleaseRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken);
}
```

`CovenantQuotaContracts.cs` defines immutable validated identities and requests that bind Session, claim where applicable, reservation, future assistant Entry, exact prior reservation state, direct origin, and expected per-Session counts for retention compare-and-swap. `DirectFinalizationCapacityRequest` accepts only `Internal`, `Imported`, or `Forked`; a public claim must use `ReserveClaimAndFinalizationAsync`. Every method requires the exact live caller-owned transaction, performs no commit or retry, borrows one Plan 01 `TurnCapacityMutation` authorization from that transaction's initialized connection, and holds it across its complete multi-statement reservation and counter transition. It disposes the authorization before returning and never lends it to a caller. The methods update the Plan 01 reservation row plus per-Session and installation counters as one compare-and-swap unit. The claim/finalization methods touch only always-present core tables and remain usable by the disabled unprotected Session path without probing an optional Covenant table. Replay returns the same reservation state without another counter change. `ReleaseSessionCapacityAsync` first uses that capacity authorization to decrement installation totals from the exact locked Session quota row, ends it, and is otherwise authorized only inside whole-Session retention before the parent cascade.

- [ ] Implement fixed logical-byte accounting, reserved mutation capacity, the exact transaction-bound claim/finalization methods above, bounded preflight checks, and a worker that folds at most 128 terminal receipts into one guarded mutable aggregate per Session.
- [ ] Rerun the focused command. Expected: exact at-limit success, over-limit refusal, replay, reservation transition, and retention-decrement assertions pass.
- [ ] Verify the mutation write path never performs an unbounded fold and that folded receipt detail is unnecessary for terminal replay.

## Task 11: Implement owner deletion journaling and cleanup

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOwnerDeletionReader.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantOwnerCleanupTests.cs`
- Extend: `tests/RetroDownfall.Arcanum.Tests/Repositories/CampaignRepositoryTests.cs`

- [ ] Add tests that ordinary Campaign and Session deletion causes exactly one Plan 01 trigger-owned core owner event, contains no repository-authored event insert, succeeds when the optional Covenant family is wholly absent, and fails capability operations under partial canonical damage.
- [ ] Add cleanup tests for event ordering, full-sweep coalescing, deleted-owner exclusion before physical cleanup, affected-key epoch updates, zero-head deletion without search-sequence advance, generation races, reset races, and applied owner-deletion cursor advancement.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantOwnerCleanupTests|FullyQualifiedName~CampaignRepositoryTests"`. Expected: owner cleanup assertions fail because the journal worker is absent.
- [ ] Consume the Plan 01 Campaign and Session delete triggers as the sole owner-event producer. Do not add an owner-event insert to `CampaignRepository`, `GrimoireRepository`, or any deletion service. Implement only the reader and a generation-bound authorized cleanup batch that rechecks event identity inside its immediate transaction. Repository tests prove the existing parent delete transaction commits one trigger event and no production SQL contains a second append path.
- [ ] Rerun the focused command. Expected: core deletion isolation and eventual encrypted cleanup pass.
- [ ] Inject each optional object/state failure and verify only the approved wholly-absent and degraded behaviors occur.

## Task 12: Compile safe FTS queries and define cursor bodies

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantSearchContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchQueryCompiler.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSearchQueryCompilerTests.cs`

**Interfaces:**

```csharp
public sealed class CovenantSearchQueryCompiler
{
    public Result<CovenantCompiledSearchTerms> Compile(string query);
}
```

`CovenantSearchContracts.cs` defines immutable `CovenantCompiledSearchTerms`, `CovenantSearchQuery`, `CovenantSearchPage`, `CovenantSearchHit`, `CovenantSearchKeyset`, and `CovenantSearchSourceSnapshot` values. `CovenantSearchQuery` contains the compiler output, typed scope/lane/lifecycle filters, a page size clamped to 1 through 200, and an optional validated Plan 02 cursor body. It never contains a raw MATCH fragment. `CovenantSearchPage` carries ordered hits, the exact final keyset tuple when another page exists, the one-snapshot canonical/applied/deletion/accelerator facts, closed `Fts` or `CanonicalFallback` execution mode, truncation, and typed rebuild guidance. Plan 04 maps these values and protects the cursor but does not redefine them.

- [ ] Add tests for 512 strict UTF-8 bytes, 32 terms, NFC, policy-v1 whitespace, controls, NUL, Format code points, double-quote doubling, quoted literals, suffix-only prefix markers, explicit `AND`, and raw FTS operator refusal.
- [ ] Add pure cursor-body contract tests for finite BM25 binary64, negative-zero normalization, nonfinite rejection, dataset, canonical/applied sequence, Campaign deletion sequence, accelerator epoch, key version, filter digest, and stale versus invalid source-change fields. Add a literal integration vector proving every cursor filter constructs Task 4's `CursorFilterDigestInput`. Cryptographic protection belongs to Plan 04 and consumes Plan 03's envelope codec.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSearchQueryCompilerTests"`. Expected: compilation fails on the absent compiler and cursor-body contracts.
- [ ] Implement `Compile` as the only normalization and escaping path. Its output contains only parameter values for the quoted-AND FTS expression and escaped canonical-fallback pattern, plus the normalized term vector needed for the filter digest. Implement immutable list, versions, FTS, and fallback cursor plaintext records. Do not implement AEAD or depend on a later plan. Do not place Campaign, query, key, or cursor data in URLs or logs.
- [ ] Rerun the focused command. Expected: exact compiler and pure cursor-body assertions pass.
- [ ] Add a fuzz corpus of malicious MATCH input with a fixed seed and bounded runtime.

## Task 13: Implement eligible FTS search and synchronize the canonical outbox

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantSearchIndex.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchIndex.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSearchIndexTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSearchOutboxWorkerTests.cs`

**Interfaces:**

```csharp
public interface ICovenantSearchIndex
{
    ValueTask<Result<CovenantSearchPage>> SearchAsync(
        CovenantSearchQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);
}
```

- [ ] Add tests for a contiguous sequence range, deterministic ordinal order, bounded coalescing, desired immutable version load, absent delta, owner deletion before worker, missing desired version, full rollback, no applied tuple across a gap, and 65,536-row dirty transition.
- [ ] Add `SearchAsync` tests for exact normalized-key, prefix, finite weighted BM25, stable entry/version ties, page size, final keyset, cursor continuation, lease coverage, and rejection of a raw or separately normalized query. An all-scopes query requires one caller-owned `CovenantInstallationReadLease`; a scoped query accepts either that lease or one exactly matching `CovenantReadLease`. Insufficient coverage fails before SQL, and the index never acquires a nested lease. Add concurrent mutation tests that verify generation, canonical sequence, applied tuple, Campaign deletion sequence, accelerator epoch, and FTS results all come from the same read snapshot.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSearchIndexTests|FullyQualifiedName~CovenantSearchOutboxWorkerTests"`. Expected: compilation fails on missing index and worker.
- [ ] Implement `SearchAsync` as the sole FTS query port. Validate and revalidate the caller-owned `ICovenantSnapshotReadLease`, require installation coverage for all-scopes filters and exact matching coverage for scoped filters, open one SQLite read transaction, read canonical generation and sequence, core Campaign-deletion sequence, applied tuple, and accelerator epoch, then run the eligible FTS query in that same snapshot. Return only the Task 12 result contract and exact final keyset. Implement a single-writer worker that leases the generation, reads a safe range, applies projection changes, consumes outbox rows, and advances the applied tuple in one immediate accelerator transaction.
- [ ] Rerun the focused command. Expected: every rollback, contiguity, and eligibility assertion passes.
- [ ] Verify canonical mutation succeeds when every accelerator command is fault-injected.

## Task 14: Add resumable rebuild and bounded fallback

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantIndexRebuildProgress.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchIndex.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantIndexRebuildTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSearchFallbackTests.cs`

**Interfaces:**

- Consumes: Task 7's exact caller-owned `CovenantAcceleratorLease`; Task 13 canonical outbox, accelerator projection, and availability publication contracts.
- Produces: the sole base-rebuild algorithm, its exact `CovenantIndexRebuildProgress` checkpoint projection, and this callable surface consumed unchanged by Plan 04 Task 10:

```csharp
public enum CovenantIndexRebuildPhase : byte
{
    BaseScan = 1,
    DeltaCatchUp = 2,
    Verifying = 3,
    Completed = 4,
    RestartRequired = 5
}

public sealed record CovenantIndexRebuildProgress(
    Guid DatasetGeneration,
    ulong AcceleratorEpoch,
    long BaseTargetSearchSequence,
    long CapturedCoreCampaignDeletionSequence,
    CovenantIndexRebuildPhase Phase,
    long? BaseScanAfterSearchRowId,
    long LastContiguousAppliedSequence,
    long BaseHeadsProcessed,
    long? BaseHeadsTotal,
    long DeltaRowsProcessed);

public sealed class CovenantIndexRebuilder
{
    public ValueTask<Result<CovenantIndexRebuildProgress>> AdvanceBatchAsync(
        CovenantIndexRebuildProgress? progress,
        CovenantAcceleratorLease acceleratorLease,
        CancellationToken cancellationToken);
}
```

`progress=null` is the only start arm. It atomically captures the nonempty 128-bit dataset `Guid`, base target search sequence, core Campaign-deletion sequence, and positive accelerator epoch, clears stale deltas, changes `FullRebuildRequired` to `Rebuilding`, and returns `BaseScan` with a null cursor, zero processed counts, and `LastContiguousAppliedSequence=BaseTargetSearchSequence`. A nonnull call is the only resume arm. `BaseScanAfterSearchRowId` is the positive stable `covenant_heads.SearchRowId` most recently committed; null is valid only before the first base row. Every count and sequence is nonnegative and at most `long.MaxValue`; a present total is at least `BaseHeadsProcessed`. `Completed` is terminal and idempotent. `RestartRequired` carries the stale captured identity and is terminal for that progress instance. Plan 04 ends that LRO, preserves its identity, and starts a new server-generated operation.

Every call borrows the exact caller-owned accelerator lease and never acquires, completes, or disposes it. One call commits at most 256 base heads or one bounded contiguous post-target delta batch. It rechecks dataset generation, accelerator epoch, base target, core Campaign-deletion sequence, rebuild state, and progress cursor inside the same immediate transaction as its batch. An identity change, reset, outbox overflow back to `FullRebuildRequired`, or stale worker discards the unpublished partial accelerator generation and returns `RestartRequired`. `Verifying` runs rank-1 integrity and atomically publishes the exact applied dataset, search sequence, Campaign-deletion sequence, accelerator epoch, and healthy eligibility before returning `Completed`. No partial generation is eligible.

- [ ] Add red rebuild tests for the one exact `AdvanceBatchAsync` signature, constructor invariants, atomic target capture, 256-head batches, write to an already-passed key, post-target deltas, concurrent delete, delta overflow, dataset and accelerator epoch change, every crash boundary, rank-1 finalization, and old-worker-after-reset refusal. Assert there is no second public rebuild, parser, cursor, or whole-operation method.
- [ ] Add fallback tests through the same `ICovenantSearchIndex.SearchAsync` signature for exact/list continuity, `WITH candidates AS MATERIALIZED`, stable indexed order, `LIMIT 2048`, escaped outer `LIKE ... ESCAPE '\\'`, final keyset, truncation guidance, and `EXPLAIN QUERY PLAN` barrier proof. Assert FTS missing, stale, corrupt, locked, or version-mismatched state returns a successful typed `CanonicalFallback` page rather than a second query API.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantIndexRebuildTests|FullyQualifiedName~CovenantSearchFallbackTests"`. Expected: compilation fails on missing rebuild and fallback implementations.
- [ ] Implement the exact `CovenantIndexRebuildPhase`, `CovenantIndexRebuildProgress`, and `CovenantIndexRebuilder.AdvanceBatchAsync` contract above, including the `FullRebuildRequired -> Rebuilding` transition, captured target, resumed post-target outbox writes, bounded immutable non-LRO progress, `RestartRequired`, final contiguous catch-up, rank-1 verification, and applied-tuple publication. Implement fallback behind the existing `CovenantSearchIndex.SearchAsync` port as a separate prepared command with the materialization barrier. It consumes the same Task 12 compiled terms and returns the same `CovenantSearchPage`; it does not expose another parser, normalizer, or public method.
- [ ] Rerun the focused command. Expected: all rebuild and fallback assertions pass. Plan 04 owns the long-running-operation adapter, source-generated checkpoint envelope, registry descriptor, and crash recovery handler after the base rebuilder is green.

## Task 15: Wire persistence services and run the domain boundary gate

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`

- [ ] Add architecture tests proving Core Covenant has no forbidden reference, one operation gate owns `AcquireInstallationReadAsync`, `CovenantInstallationReadLease` is the sole all-scopes lease implementation, one mutation kernel and one transaction-bound quota guard are registered, one search index owns the `ICovenantSnapshotReadLease` `SearchAsync` signature, one search worker owns accelerator writes, every direct test connection uses the central initializer, and no Covenant EF entity or migration exists.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CovenantArchitectureBoundaryTests`. Expected: registration and inventory assertions fail.
- [ ] Register the compiler, linker, store, availability, mutation kernel, quota guard, search index, outbox worker, base `CovenantIndexRebuilder`, cleanup worker, and turn-receipt compactor with the lifetimes specified in the design. Update the fixture to use production initialization. Plan 04 Task 10 alone registers the long-running rebuild descriptor and recovery handler.
- [ ] Rerun the focused command. Expected: all architecture assertions pass.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Covenant|FullyQualifiedName~GrimoireSchema|FullyQualifiedName~Sqlite"`. Expected: the complete domain and persistence boundary is green.

---

## Delivered scope and approved deviations (issue #82, 2026-08-15)

Tasks 7 through 15 of this plan were implemented as GitHub issue #82. Tasks 1 through 6 landed earlier as issue #79. The deviations below are the authority where they conflict with the task text.

1. **Infrastructure components are `internal`, not `public`.** The task snippets declare `CovenantMutationKernel`, `CovenantQuotaGuard`, and `CovenantIndexRebuilder` as `public sealed`. Every other Infrastructure type in this repository is `internal` with `InternalsVisibleTo` for the test and host assemblies, and `CovenantAvailability` and `CovenantStore` already follow that rule. Widening three types would have made the assembly's public surface inconsistent for no consumer, since nothing outside the solution references them. Their method signatures, parameter types, and one-entry-point shape are exactly as specified, and `CovenantMutationKernelTests` and `CovenantIndexRebuildTests` assert the single declared public instance method reflectively.

2. **Service registration uses explicit factories.** Those internal constructors mean the container's reflective activator cannot find them, so `AddCovenantPersistence` registers each component with a factory lambda. The registered set, the interface-to-implementation mapping, and the lifetimes are as specified, and `CovenantArchitectureBoundaryTests` asserts each is registered exactly once at its intended lifetime.

3. **The post-disposition finalizer is passed to a `CompleteAsync` overload.** Task 7's gate methods take no finalizer parameter, so there is nowhere to capture one at acquisition. `ICovenantExclusiveOperationLease.CompleteAsync(disposition, cancellationToken)` delegates to `CompleteAsync(disposition, finalizer, cancellationToken)` with `CovenantNoOpPostDispositionFinalizer.Instance`. The one-shot rule, the skip on failed disposition, and the prohibition on a second disposition after a finalizer failure are implemented and tested.

4. **The mutation-effect snapshot uses three commands inside one read transaction.** List, detail, version, and source reads are each exactly one command, including their dataset facts. The effect snapshot needs the local lane heads, the epochs, and the streaming Campaign scan, and folding them into one statement would have made a bounded-memory stream into a cross join. All three run inside the same read snapshot, which is what the contract actually depends on.

5. **The fallback keyset is class-then-identity, not the canonical list tuple.** `CovenantSearchPage` carries one keyset type so both execution modes share `ICovenantSearchIndex.SearchAsync`. The fallback therefore orders and continues by match class, entry ID, and version ID with a fixed zero score, rather than by the list tuple the specification names for fallback cursors. The candidate CTE still selects in the specified stable indexed order under the 2,048-row cap. Plan 04 owns the protected cursor envelope and can carry the list tuple in its body if the wire contract needs it.

6. **Policy v1 gains one additive domain tag.** `Arcanum.Covenant.DependentHeadVector.v1` (code 35) and `CovenantDigests.DependentHeadVector` were added for the ordered dependent-head vector a preflight binds. No existing preimage changes; the pinned tag list test was extended by one row. Reusing an existing tag would have removed the domain separation that makes a vector digest and, say, a snapshot digest non-interchangeable.

7. **The mutation target identity digest is caller-supplied.** `CovenantMutationTarget.IdentityDigest` is persisted as `covenant_mutation_receipts.TargetIdentityDigest` rather than recomputed in the kernel. It is evidence of what the operator or agent asked for; a digest the kernel derived from its own parsed fields would only prove the kernel agrees with itself.

8. **The version provenance digest is `CovenantDigests.Materialization` with empty occurrence vectors.** The persisted provenance table stores immutable source coordinates and deliberately no provider-payload occurrences, so the aggregate is that same canonical input with an empty occurrence list per source. The store recomputes it from the returned leaves on every detailed source read and reports agreement.

9. **The turn-receipt fold chain uses a local domain-separated construction.** `covenant_turn_receipts` does not store a final-sensitivity digest or a committed branch ID, so `CovenantDigests.TurnAggregate` cannot be fed without inventing values. `CovenantTurnReceiptCompactor` chains an `Arcanum.Covenant.TurnReceiptFold.v1`-tagged SHA-256 over the exact stored columns instead. Nothing outside that aggregate row consumes it.

10. **Canonical quota demand is a conservative upper bound.** The kernel charges one entry, one version, and one receipt per intent before applying anything, rather than probing each target first to learn whether it is a create, an update, or a no-op. An intent then consumes at most what it reserved, which is the safe direction for a ceiling, and it keeps the capacity check to one query per scope per batch.

11. **A null applied tuple over an empty projection is adopted rather than rebuilt.** The canonical seed leaves the applied tuple null, which the specification treats as rebuild-required. The synchronization worker instead adopts `(current generation, 0)` when `covenant_search_documents` is empty, because an empty projection is trivially correct for sequence zero. A projection with rows under a tuple this dataset never published still forces a rebuild. Without this, a fresh installation could never reach FTS eligibility without running a rebuild of nothing.

12. **The long-running-operation surface remains deferred, as the plan states.** Task 14 delivers `CovenantIndexRebuilder.AdvanceBatchAsync`, its exact phase and progress contract, and the bounded fallback. The LRO adapter, source-generated checkpoint envelope, registry descriptor, and crash-recovery handler are Plan 04 Task 10.

13. **Test fixtures seed through the same rules the kernel enforces.** `CovenantCanonicalFixture` compiles content with the real `CovenantCompiler`, allocates projection row IDs from `covenant_state`, reuses an existing head's row ID on an advance, and emits the outbox delta and search-sequence advance. A fixture that wrote rows by hand would either fail the loader's verification or drift from the compiler it is meant to exercise.
