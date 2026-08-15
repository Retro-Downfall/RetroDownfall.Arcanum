# Covenant Surfaces and Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the authenticated Covenant HTTP, CLI, Compendium, diagnostics, search, administration, backup, restore, retention, reset, and recovery surfaces approved for issue #74.

**Architecture:** This is Plan 04 in the coordinated Covenant plan set. It consumes the schema and canonical persistence from Plans 01 and 02 plus the invocation authority, sensitivity, disclosure, and operation-lease primitives from Plan 03. Core owns typed contracts, Infrastructure owns storage and recoverable lifecycle work, API owns authenticated orchestration, and CLI and Compendium remain typed clients.

**Tech Stack:** .NET 10, C# 14, Native AOT, ASP.NET Core minimal APIs, System.Text.Json source generation, System.CommandLine, Avalonia, raw Microsoft.Data.Sqlite, SQLCipher 4.17 with SQLite FTS5, and xUnit.

## Global Constraints

#### Plan and dependency authority

- The approved source of truth is [`2026-08-13-covenant-design.md`](../specs/2026-08-13-covenant-design.md). If a step here differs from that specification, stop the slice and correct this plan before changing production code.
- Execute this plan after [`2026-08-14-covenant-native-and-schema.md`](2026-08-14-covenant-native-and-schema.md), [`2026-08-14-covenant-domain-and-persistence.md`](2026-08-14-covenant-domain-and-persistence.md), and [`2026-08-14-covenant-runtime-and-authority.md`](2026-08-14-covenant-runtime-and-authority.md) are green.
- Benchmarking, shipping-RID AOT matrices, documentation edits, architecture inventories, and final branch integration belong to [`2026-08-14-covenant-verification-and-docs.md`](2026-08-14-covenant-verification-and-docs.md), Plan 05.
- Preserve `Cli -> Api -> Infrastructure -> Core`. Core cannot reference ASP.NET, CLI, EF, SQLite, or provider types.
- Do not commit intermediate red states. The master plan permits one final branch commit after all coordinated plans are green.

#### Authentication and privacy

- `OperatorAuthorityContext` is created only after the existing constant-time API-key check at the authenticated HTTP boundary. Request bodies, MCP arguments, repositories, and internal inference cannot construct it.
- Pre-binding order is API-key authentication, Covenant authority metadata evaluation, `X-Arcanum-Context-Policy` validation, body-size enforcement, source-generated JSON decoding, canonicalization, and durable receipt lookup.
- The explicit no-context wire value is exactly `X-Arcanum-Context-Policy: none`. Duplicate values, comma-combined values, wrong case, and every other value return HTTP 400.
- Content-bearing Covenant reads, tainted-history reads, preflights, mutations, Campaign path operations, Session binding resolution, reset, and reinitialize require clean read or operator authority. Before the first buffered or SSE byte on every protected success or failure, emit exactly `Cache-Control: no-store, private`, `Pragma: no-cache`, and `Expires: 0`.
- Aggregate memory status and content-free source counts remain available without Covenant authority metadata. Authenticated generic `/api/memory/sources`, `/search`, and `/explain` keep their current contracts, and `MemorySearchScope.All` excludes Covenant.
- URLs, route values, logs, metrics, and traces never contain Covenant keys, authored or compiled content, raw hashes, raw FTS queries, query cursors, or Campaign inspection selectors.

#### Wire and serialization

- All `/api` request and response types are named positional records or explicit POCOs registered with `ArcanumJsonContext`. CLI-only `*Payload` types are registered with `CliJsonContext`.
- Use `StringOnlyJsonStringEnumConverter<TEnum>` for every new public enum. Numeric enum input fails with HTTP 400 before service invocation.
- Every failable endpoint returns `ApiResponse<T>.FromResult`, selects an explicit status through `ArcanumErrorMapper`, supplies explicit source-generated `JsonTypeInfo`, and has a unique `.WithName(...)`.
- Durable counts, sequence values, byte counts, and revisions use `long`. IDs are `Guid`; cryptographic digests use the fixed digest type produced by Plan 02 and never mutable, caller-owned arrays.
- Configuration POCOs use `{ get; set; }`. `FeatureSettings.Covenant` defaults to `false`.

#### Bounded management behavior

- Covenant free-text input is at most 512 strict UTF-8 bytes and 32 non-empty terms. Page size defaults to 50 and accepts only 1 through 200.
- One version has at most 64 exact sources. Cursor ciphertext is opaque unpadded base64url, expires after 15 minutes, accepts 30 seconds of clock skew, and fails closed on query, source, dataset, epoch, or purpose mismatch.
- FTS5 is a derived accelerator. Exact reads and canonical listing remain available when FTS is degraded. Fallback materializes at most 2,048 indexed candidates before parameterized `LIKE` evaluation.
- Schema repair may recreate only safely reconstructible absent objects or an ordinary index after complete constraint validation. Catalog drift, unknown objects, lost canonical triggers beside data, and newer versions return `Covenant.ManualRecoveryRequired` without alteration.

#### Idempotency and lifecycle

- Session-backed public intelligence, Prompt execute, and Spell execute require one canonical UUID `Idempotency-Key`. The durable Session turn claim is their sole request-level idempotency mechanism.
- Generic response caching is unavailable to Session-backed inference and every request that may inject Covenant. Stateless explicit no-context or Covenant-disabled requests may use it after sensitivity checks. Its fingerprint includes the normalized context policy.
- Covenant set and retire use one stable client mutation ID and durable mutation receipts. An HTTP `Idempotency-Key` has no semantic effect on Covenant memory routes.
- Reinitialize, Campaign path apply, and Session binding apply use receipt-first replay keyed by caller operation ID plus stable apply-request digest. A changed digest returns `Security.IdempotencyConflict` before token decoding.
- Every protected reader acquires a generation-bound Covenant operation lease before its snapshot, retains it through serialization or stream completion, and revalidates immediately before the first byte. Every writer rechecks dataset generation inside its immediate transaction. Reset, restore, reinitialize, and erasure coordinators close admission, request cancellation, and drain all affected ordinary leases before mutation; the coordinator-owned exclusive lease remains held through committed database health, `ICovenantAuthorityTransitionPublisher` publication, and disclosure-writer reopen. The coordinator then calls the lease's one-shot `CompleteAsync(CommitAndReopen, lifecycleCt)` as the only general-admission reopen. A proven precommit abort uses `RollbackAndReopen`; any postcommit uncertainty or failure uses `KeepClosed`. Disposition uses the coordinator's bounded recovery-owned cancellation token after durable mutation, never an already-canceled HTTP token. Disposal never substitutes for an explicit disposition.
- Every SQL connection that deletes protected canonical or accelerator content enables `PRAGMA secure_delete=ON`. FTS erasure also verifies the FTS5 secure-delete setting.

#### Test discipline

- Add the focused test first, run the exact red command, and observe the listed failure before touching production code.
- Tests use real SQLCipher and HTTP where the boundary is under test. Fakes stop at provider, operating-system credential, clock, and filesystem fault-injection boundaries.
- Each task ends with its focused suite green, `dotnet build RetroDownfall.Arcanum.slnx` green, and `git diff --check` clean.

---

### Task 1: Freeze API DTOs, errors, service ports, and JSON ownership

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Memory/CovenantApiContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Memory/CovenantAdministrationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantExternalRetentionDisclosure.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Operations/CovenantLeaseResponseContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Storage/IProtectedDerivedReadStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/TheForge/CampaignExportDto.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/TheForge/SessionDtos.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/CampaignPathIdentityContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/TheForge/SessionCampaignBindingAdministrationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/ArcanumErrorMapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantWireContractTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantErrorMappingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantExternalRetentionDisclosureTests.cs`

**Interfaces:**

- Consumes: Plan 01 `CovenantCapabilityState`, `CovenantFtsSynchronizationState`, and `CovenantHealthTransition`.
- Consumes: Plan 02 `CovenantScope`, `CovenantLane`, `CovenantLifecycle`, `CovenantOrigin`, `CovenantMutationOutcome`, `CovenantPlanDecision`, `CovenantAdmissionDecision`, `CovenantPlacement`, `CovenantDigest`, `CampaignPathIdentityOperation`, `SessionCampaignBindingKind`, canonical entity identifiers, `ICovenantSnapshotReadLease`, `ICovenantExclusivePostDispositionFinalizer`, and the all-scope `CovenantInstallationReadLease` returned by `AcquireInstallationReadAsync`.
- Consumes: Plan 03 `OperatorAuthorityContext`, `CovenantContextPolicy`, sensitivity, and the runtime evaluation output mapped into Task 1's closed shadow, materialization, source-availability, and integrity wire decisions.
- Produces: `ICovenantInspectionService`, `ICovenantOperatorMutationService`, `ICovenantMaintenanceService`, `ICampaignPathIdentityAdministration`, `ISessionCampaignBindingAdministration`, `IProtectedDerivedReadStore`, `CovenantLeasedServiceResult<T>`, `CovenantExclusiveLeasedServiceResult<T>`, and the one shared `CovenantExternalRetentionDisclosure` contract consumed by Tasks 11, 13, 15, 16, and 17.

- [ ] **Step 1: Write the failing wire-contract tests**

Create `CovenantWireContractTests` with these exact methods:

```csharp
[Fact]
public void Every_covenant_wire_type_has_source_generated_type_info()
{
    Type[] wireTypes =
    [
        typeof(CovenantScopeSelection), typeof(CovenantListRequest),
        typeof(CovenantQueryRequest), typeof(CovenantDetailRequest),
        typeof(CovenantVersionsRequest), typeof(CovenantSourcesRequest),
        typeof(CovenantExplainRequest), typeof(CovenantSetPrepareRequest),
        typeof(CovenantRetirePrepareRequest), typeof(CovenantSetRequest),
        typeof(CovenantRetireRequest), typeof(CovenantScope),
        typeof(CovenantLane), typeof(CovenantLifecycle),
        typeof(CovenantOrigin), typeof(CovenantMutationOutcome),
        typeof(CovenantPlanDecision), typeof(CovenantAdmissionDecision),
        typeof(CovenantPlacement), typeof(CovenantShadowState),
        typeof(CovenantMaterializationEligibility),
        typeof(CovenantPageEndpointKind), typeof(CovenantSearchExecutionMode),
        typeof(CovenantSearchDegradation), typeof(CovenantRebuildGuidance),
        typeof(CovenantTruncationReason), typeof(CovenantSourceRangeKind),
        typeof(CovenantSourceAvailability), typeof(CovenantIntegrityDecision),
        typeof(CovenantProvenanceSummaryDto), typeof(CovenantHeadEvaluationDto),
        typeof(CovenantHeadItemDto), typeof(CovenantPageQueryIdentityDto),
        typeof(CovenantPageSourceSnapshotDto), typeof(CovenantSearchHealthDto),
        typeof(CovenantPageTruncationDto), typeof(CovenantPageDto),
        typeof(CovenantHeadDetailDto), typeof(CovenantDetailDto),
        typeof(CovenantVersionItemDto), typeof(CovenantVersionPageDto),
        typeof(CovenantSourceDto), typeof(CovenantSourcesDto),
        typeof(CovenantExplainItemDto), typeof(CovenantExplainTokenAttributionDto),
        typeof(CovenantExplainDto), typeof(CovenantMutationPreflightDto),
        typeof(CovenantMutationResultDto), typeof(CovenantHeadItemDto[]),
        typeof(CovenantVersionItemDto[]), typeof(CovenantSourceDto[]),
        typeof(CovenantExplainItemDto[]), typeof(Guid[]),
        typeof(CovenantCapabilityState),
        typeof(CovenantFtsSynchronizationState), typeof(CovenantHealthTransition),
        typeof(CovenantCapabilityStatusDto),
        typeof(CovenantAggregateCountAvailability),
        typeof(CovenantAggregateCountDiagnostic),
        typeof(CovenantAggregateCountStatusDto),
        typeof(CovenantCapabilityStatusDto[]),
        typeof(CovenantAggregateCountStatusDto[]),
        typeof(CampaignPathIdentityStatusRequest),
        typeof(CampaignPathIdentityPrepareRequest),
        typeof(CampaignPathIdentityApplyRequest),
        typeof(CampaignPathIdentityStatusItemDto),
        typeof(CampaignPathIdentityStatusPageDto),
        typeof(CampaignPathIdentityPlanDto),
        typeof(CampaignPathIdentityResultDto),
        typeof(CampaignPathIdentityOperation),
        typeof(CampaignPathMarkerEffect),
        typeof(CampaignPathIdentityState),
        typeof(CampaignPathIdentityRemediation),
        typeof(CampaignPathIdentityStatusItemDto[]),
        typeof(SessionCampaignBindingStatusRequest),
        typeof(SessionCampaignBindingPrepareRequest),
        typeof(SessionCampaignBindingApplyRequest),
        typeof(SessionCampaignBindingStatusItemDto),
        typeof(SessionCampaignBindingStatusPageDto),
        typeof(SessionCampaignBindingPlanDto),
        typeof(SessionCampaignBindingResultDto),
        typeof(SessionCampaignBindingKind),
        typeof(SessionCampaignBindingRemediation),
        typeof(SessionCampaignBindingStatusItemDto[]),
        typeof(CovenantSchemaRepairRequest),
        typeof(CovenantSchemaRepairResultDto),
        typeof(CovenantSchemaRepairOutcome),
        typeof(CovenantDiagnosticTag), typeof(CovenantDiagnosticTag[]),
        typeof(CovenantFamilyReinitializePrepareRequest),
        typeof(CovenantFamilyReinitializeRequest),
        typeof(CovenantFamilyReinitializePlanDto),
        typeof(CovenantIndexRebuildRequest), typeof(LongRunningOperationDto),
        typeof(CampaignExportResult), typeof(CampaignExportExclusionCounts),
    ];

    Assert.All(wireTypes, type =>
        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type)));

    Type[] responseTypes =
    [
        typeof(ApiResponse<CovenantPageDto>),
        typeof(ApiResponse<CovenantDetailDto>),
        typeof(ApiResponse<CovenantVersionPageDto>),
        typeof(ApiResponse<CovenantSourcesDto>),
        typeof(ApiResponse<CovenantExplainDto>),
        typeof(ApiResponse<CovenantMutationPreflightDto>),
        typeof(ApiResponse<CovenantMutationResultDto>),
        typeof(ApiResponse<CovenantCapabilityStatusDto>),
        typeof(ApiResponse<CampaignPathIdentityStatusPageDto>),
        typeof(ApiResponse<CampaignPathIdentityPlanDto>),
        typeof(ApiResponse<CampaignPathIdentityResultDto>),
        typeof(ApiResponse<SessionCampaignBindingStatusPageDto>),
        typeof(ApiResponse<SessionCampaignBindingPlanDto>),
        typeof(ApiResponse<SessionCampaignBindingResultDto>),
        typeof(ApiResponse<CovenantSchemaRepairResultDto>),
        typeof(ApiResponse<CovenantFamilyReinitializePlanDto>),
        typeof(ApiResponse<LongRunningOperationDto>),
        typeof(ApiResponse<CampaignExportResult>),
    ];

    Assert.All(responseTypes, type =>
        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type)));
}

[Theory]
[InlineData("scope", "1")]
[InlineData("lane", "0")]
[InlineData("lifecycle", "2")]
public void Covenant_enums_reject_numeric_json(string property, string numericValue)
{
    Assert.Throws<JsonException>(() => DeserializeRequestWith(property, numericValue));
}

[Fact]
public void Covenant_page_uses_long_counts_revisions_and_byte_costs()
{
    Assert.Equal(typeof(long), typeof(CovenantHeadItemDto).GetProperty("Revision")!.PropertyType);
    Assert.Equal(typeof(long), typeof(CovenantHeadItemDto).GetProperty("FramedBytes")!.PropertyType);
    Assert.Equal(typeof(long), typeof(CovenantProvenanceSummaryDto).GetProperty("SourceCount")!.PropertyType);
    Assert.Equal(typeof(long), typeof(CovenantSourcesDto).GetProperty("SourceCount")!.PropertyType);
}

[Fact]
public void Version_source_and_explain_requests_pin_scope_before_storage_access()
{
    Assert.Equal(typeof(CovenantScope), typeof(CovenantVersionsRequest).GetProperty("Scope")!.PropertyType);
    Assert.Equal(typeof(Guid?), typeof(CovenantVersionsRequest).GetProperty("CampaignId")!.PropertyType);
    Assert.Equal(typeof(CovenantScope), typeof(CovenantSourcesRequest).GetProperty("Scope")!.PropertyType);
    Assert.Equal(typeof(Guid?), typeof(CovenantSourcesRequest).GetProperty("CampaignId")!.PropertyType);
    Assert.Equal(typeof(CovenantScope), typeof(CovenantExplainRequest).GetProperty("EvaluationScope")!.PropertyType);
    Assert.Equal(typeof(Guid?), typeof(CovenantExplainRequest).GetProperty("CampaignId")!.PropertyType);
}

[Fact]
public void Campaign_path_marker_effect_codes_are_immutable()
{
    Assert.Equal((byte)0, (byte)CampaignPathMarkerEffect.None);
    Assert.Equal((byte)1, (byte)CampaignPathMarkerEffect.Create);
    Assert.Equal((byte)2, (byte)CampaignPathMarkerEffect.Replace);
    Assert.Equal((byte)3, (byte)CampaignPathMarkerEffect.Delete);
    Assert.Equal((byte)4, (byte)CampaignPathMarkerEffect.QuarantineAndCreate);
}

[Fact]
public void Campaign_path_plan_exposes_typed_identity_evidence_without_marker_bytes()
{
    Assert.Equal(typeof(string), typeof(CampaignPathIdentityPlanDto).GetProperty("CurrentPhysicalIdentityDigest")!.PropertyType);
    Assert.Equal(typeof(string), typeof(CampaignPathIdentityPlanDto).GetProperty("ProspectivePhysicalIdentityDigest")!.PropertyType);
    Assert.Equal(typeof(CampaignPathMarkerEffect), typeof(CampaignPathIdentityPlanDto).GetProperty("MarkerEffect")!.PropertyType);
    Assert.Null(typeof(CampaignPathIdentityPlanDto).GetProperty("MarkerBytes"));
}
```

Create `CovenantErrorMappingTests.Every_covenant_error_code_has_the_approved_http_status`. Assert every exact code in the specification, including 410 for `Covenant.ArtifactErased`, 429 for `Hub.ContextBudgetExceeded`, 502 for `Hub.ProviderToolBufferExceeded`, and every required 503 recovery condition.

Create `CovenantExternalRetentionDisclosureTests` with exact methods `Known_openai_api_codex_and_claude_code_targets_use_official_data_handling_pages`, `Unknown_proxy_and_self_hosted_targets_use_providers_page_and_operator_guide`, `Provider_display_name_cannot_select_an_external_uri`, and `Disclosure_constants_match_the_approved_golden_copy`. These tests pin the three official URIs, the Plan 05 guide anchor, and the absence of any local cache-control claim.

- [ ] **Step 2: Run the red tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantWireContractTests|FullyQualifiedName~CovenantErrorMappingTests|FullyQualifiedName~CovenantExternalRetentionDisclosureTests"
```

Expected: FAIL at compile time with `CS0246` for `CovenantListRequest`.

- [ ] **Step 3: Add the smallest complete contract set**

Define the request shapes with explicit scope selection and typed defaults:

```csharp
public sealed record CovenantScopeSelection(
    CovenantScope? Scope,
    Guid? CampaignId,
    bool AllScopes = false);

public sealed record CovenantListRequest(
    [property: JsonRequired] CovenantScopeSelection Selection,
    CovenantLane? Lane = null,
    CovenantLifecycle? Lifecycle = null,
    Guid? EffectiveForCampaignId = null,
    int Limit = 50,
    string? Cursor = null);

public sealed record CovenantQueryRequest(
    [property: JsonRequired] CovenantScopeSelection Selection,
    [property: JsonRequired] string Query,
    CovenantLane? Lane = null,
    CovenantLifecycle? Lifecycle = null,
    Guid? EffectiveForCampaignId = null,
    int Limit = 50,
    string? Cursor = null);

public sealed record CovenantDetailRequest(
    [property: JsonRequired] CovenantScope Scope,
    Guid? CampaignId,
    [property: JsonRequired] string Key);

public sealed record CovenantVersionsRequest(
    [property: JsonRequired] CovenantScope Scope,
    Guid? CampaignId,
    [property: JsonRequired] Guid EntryId,
    [property: JsonRequired] CovenantLane Lane,
    int Limit = 50,
    string? Cursor = null);

public sealed record CovenantSourcesRequest(
    [property: JsonRequired] CovenantScope Scope,
    Guid? CampaignId,
    [property: JsonRequired] Guid VersionId);

public sealed record CovenantExplainRequest(
    [property: JsonRequired] CovenantScope EvaluationScope,
    Guid? CampaignId,
    [property: JsonRequired] string Provider,
    [property: JsonRequired] string Model,
    bool ShowContent = false);
```

`CovenantApiContracts.cs` owns these exact management response shapes and their closed wire vocabulary:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantShadowState>))]
public enum CovenantShadowState : byte
{
    NotEvaluated = 1,
    Effective = 2,
    Shadowed = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaterializationEligibility>))]
public enum CovenantMaterializationEligibility : byte
{
    NotEvaluated = 1,
    Eligible = 2,
    Ineligible = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantPageEndpointKind>))]
public enum CovenantPageEndpointKind : byte
{
    List = 1,
    Query = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSearchExecutionMode>))]
public enum CovenantSearchExecutionMode : byte
{
    CanonicalList = 1,
    Fts = 2,
    CanonicalFallback = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSearchDegradation>))]
public enum CovenantSearchDegradation : byte
{
    None = 1,
    AcceleratorUnavailable = 2,
    AcceleratorDirty = 3,
    AcceleratorIntegrityFailure = 4,
    AcceleratorTemporarilyUnavailable = 5
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantRebuildGuidance>))]
public enum CovenantRebuildGuidance : byte
{
    None = 1,
    Recommended = 2,
    Required = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantTruncationReason>))]
public enum CovenantTruncationReason : byte
{
    CanonicalFallbackCandidateLimit = 1
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSourceRangeKind>))]
public enum CovenantSourceRangeKind : byte
{
    WholeSource = 1,
    Utf16Range = 2,
    ByteRange = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSourceAvailability>))]
public enum CovenantSourceAvailability : byte
{
    Available = 1,
    HistoricalIdentityOnly = 2,
    Unavailable = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantIntegrityDecision>))]
public enum CovenantIntegrityDecision : byte
{
    Verified = 1,
    Quarantined = 2,
    Rejected = 3
}

public sealed record CovenantProvenanceSummaryDto(
    long SourceCount,
    string ProvenanceDigest);

public sealed record CovenantHeadEvaluationDto(
    CovenantShadowState Shadow,
    CovenantMaterializationEligibility Materialization,
    CovenantPlanDecision? PlanDecision,
    CovenantPlacement? Placement);

public sealed record CovenantHeadItemDto(
    Guid EntryId,
    Guid VersionId,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long Revision,
    CovenantLifecycle Lifecycle,
    CovenantOrigin Origin,
    string AuthoredHash,
    string RenderedHash,
    long FramedBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CovenantProvenanceSummaryDto Provenance,
    CovenantHeadEvaluationDto Evaluation);

public sealed record CovenantPageQueryIdentityDto(
    CovenantPageEndpointKind Endpoint,
    CovenantScopeSelection Selection,
    CovenantLane? Lane,
    CovenantLifecycle? Lifecycle,
    Guid? EffectiveForCampaignId,
    int Limit,
    string CanonicalFilterDigest);

public sealed record CovenantPageSourceSnapshotDto(
    Guid DatasetGeneration,
    long CanonicalSequence,
    long CoreCampaignDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch);

public sealed record CovenantSearchHealthDto(
    CovenantSearchExecutionMode ExecutionMode,
    CovenantCapabilityState Accelerator,
    CovenantFtsSynchronizationState FtsSynchronization,
    CovenantSearchDegradation Degradation,
    CovenantRebuildGuidance RebuildGuidance);

public sealed record CovenantPageTruncationDto(
    bool Truncated,
    CovenantTruncationReason? Reason,
    long? CandidateLimit);

public sealed record CovenantPageDto(
    CovenantHeadItemDto[] Items,
    CovenantPageQueryIdentityDto QueryIdentity,
    CovenantPageSourceSnapshotDto SourceSnapshot,
    CovenantSearchHealthDto SearchHealth,
    CovenantPageTruncationDto Truncation,
    string? NextCursor);

public sealed record CovenantHeadDetailDto(
    CovenantHeadItemDto Head,
    string? AuthoredContent,
    string? RenderedContent,
    CovenantSourceDto[] Sources);

public sealed record CovenantDetailDto(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantHeadDetailDto? Confirmed,
    CovenantHeadDetailDto? Proposed,
    CovenantPageSourceSnapshotDto SourceSnapshot);

public sealed record CovenantVersionItemDto(
    Guid VersionId,
    Guid EntryId,
    CovenantLane Lane,
    long Revision,
    CovenantLifecycle Lifecycle,
    CovenantOrigin Origin,
    string? AuthoredContent,
    string? RenderedContent,
    string AuthoredHash,
    string RenderedHash,
    long FramedBytes,
    int RequiredFenceLength,
    int CompilerPolicyVersion,
    int RendererPolicyVersion,
    Guid MutationId,
    Guid? PredecessorVersionId,
    CovenantProvenanceSummaryDto Provenance,
    DateTimeOffset CreatedAt);

public sealed record CovenantVersionPageDto(
    Guid EntryId,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    CovenantVersionItemDto[] Items,
    CovenantPageSourceSnapshotDto SourceSnapshot,
    string? NextCursor);

public sealed record CovenantSourceDto(
    long Ordinal,
    Guid AttachmentId,
    int AttachmentVersion,
    string LogicalKey,
    string AttachmentContentHash,
    CovenantSourceRangeKind RangeKind,
    long? RangeStart,
    long? RangeEnd,
    Guid? SourceTurnId,
    string MaterializationReferenceDigest,
    CovenantSourceAvailability Availability);

public sealed record CovenantSourcesDto(
    Guid VersionId,
    CovenantScope Scope,
    Guid? CampaignId,
    long SourceCount,
    string ProvenanceDigest,
    CovenantSourceDto[] Sources,
    CovenantPageSourceSnapshotDto SourceSnapshot);

public sealed record CovenantExplainItemDto(
    CovenantHeadItemDto Head,
    CovenantPlanDecision PlanDecision,
    CovenantAdmissionDecision? AdmissionDecision,
    CovenantPlacement? Placement,
    CovenantIntegrityDecision Integrity,
    int EstimatedTokens,
    CovenantSourceDto[] Sources,
    string? AuthoredContent,
    string? RenderedContent);

public sealed record CovenantExplainTokenAttributionDto(
    int ConfirmedTokens,
    int ProposedTokens);

public sealed record CovenantExplainDto(
    CovenantScope EvaluationScope,
    Guid? CampaignId,
    string Provider,
    string Model,
    CovenantPageSourceSnapshotDto SourceSnapshot,
    string SnapshotDigest,
    string PlanDigest,
    string PreviewReceiptDigest,
    CovenantExplainItemDto[] Items,
    CovenantExplainTokenAttributionDto TokenAttribution,
    bool ContentIncluded);

public sealed record CovenantMutationResultDto(
    Guid MutationId,
    CovenantMutationOutcome Outcome,
    Guid? ResultingVersionId,
    long? ResultingRevision,
    string ResponseReceiptDigest,
    DateTimeOffset CommittedAt);
```

All arrays are defensively copied on construction. Page and version arrays contain at most 200 items, provenance arrays contain at most 64 sources, and explain contains at most the 160 candidates admitted by the fresh management snapshot. Every digest is validated lowercase 32-byte hex, every count and revision is nonnegative `long`, every nonempty dataset and entity identity is validated, and every timestamp is UTC-normalized.

Global scope requires `CampaignId=null`; Campaign scope requires one nonempty Campaign ID. Apply that invariant to detail, versions, sources, and explain before lease acquisition. Versions and sources bind scope and Campaign into the cursor or request digest, pass the matching caller-owned lease to the store, and reject a store result whose entry or version ownership does not match that request before returning any DTO. Detail requires both present lane heads to have the requested scope, Campaign, key, and one shared entry ID. A Set version has nonnull authored and rendered content; a Retired version has both null. Source ordinals are contiguous from zero, `SourceCount` equals the returned array length, `WholeSource` requires null range bounds, and the two ranged kinds require `0 <= RangeStart < RangeEnd`.

`CovenantHeadEvaluationDto` uses `NotEvaluated` for an all-scope request without an evaluation Campaign. Otherwise its shadow, eligibility, Plan 02 decision, and optional placement must agree. `CovenantPageQueryIdentityDto.CanonicalFilterDigest` binds the complete canonical request, including normalized query terms for Query, without echoing raw search text. A false truncation has null reason and limit; a true truncation uses `CanonicalFallbackCandidateLimit` and the exact positive materialization-barrier limit. A `CanonicalList` page has `Degradation=None` and `RebuildGuidance=None`. FTS and fallback health copy the same-snapshot capability and synchronization facts.

Explain accepts exactly Global with a null Campaign or Campaign with one nonempty Campaign ID, plus bounded configured provider and model identities. It always creates a fresh snapshot, plan, and preview receipt. `ShowContent=false` requires every item content field to be null; `ShowContent=true` permits content only after clean read authority. Every item reports typed plan, pressure, placement, shadow, materialization, source-availability, and integrity decisions. Mutation results are copied from the immutable receipt: resulting version ID and revision are both present or both null, and the response-receipt digest is never recomputed at the API boundary.

Add these exact mutation requests:

```csharp
public sealed record CovenantSetPrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    long ExpectedRevision,
    Guid MutationId,
    bool Reactivate);

public sealed record CovenantSetRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    string Content,
    long ExpectedRevision,
    Guid MutationId,
    bool Reactivate,
    string PreflightToken);

public sealed record CovenantRetirePrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId);

public sealed record CovenantRetireRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId,
    string PreflightToken);

public sealed record CovenantMutationPreflightDto(
    Guid MutationId,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long CurrentRevision,
    string RequestDigest,
    string? AuthoredHash,
    string? RenderedHash,
    long? FramedBytes,
    string EffectDigest,
    long AffectedCampaignCount,
    Guid[] AffectedCampaignExamples,
    bool AffectedCampaignExamplesTruncated,
    DateTimeOffset ExpiresAt,
    string Token);
```

The prepare records carry exactly the same canonical client fields as their matching commit request, excluding only `PreflightToken`. `CovenantMutationPreflightDto` carries that normalized target identity, the canonical request digest, nullable compiled fields for Retire, current revision, complete effect digest, exact affected-Campaign count, at most 50 deeply copied examples, explicit truncation, expiry, and token. `AffectedCampaignExamplesTruncated` is true exactly when `AffectedCampaignCount` exceeds the example-array length; the array is sorted by canonical Guid bytes, contains no empty or duplicate ID, and its length is `min(AffectedCampaignCount, 50)`. Set requires all three compiled fields; Retire requires all three null. Every count, revision, and framed-byte value is a nonnegative `long`.

Define the content-free status contracts in `CovenantApiContracts.cs` so Task 4 only attaches them to `MemoryStatusDto`:

```csharp
public sealed record CovenantCapabilityStatusDto(
    long AvailabilityGeneration,
    bool FeatureEnabled,
    CovenantCapabilityState Canonical,
    int? CanonicalSchemaVersion,
    string? CanonicalInstalledFingerprint,
    string? CanonicalDiagnosticCode,
    CovenantCapabilityState Accelerator,
    int? AcceleratorSchemaVersion,
    string? AcceleratorInstalledFingerprint,
    string? AcceleratorDiagnosticCode,
    Guid? DatasetGeneration,
    long CanonicalSequence,
    long CoreCampaignDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch,
    CovenantFtsSynchronizationState FtsSynchronization,
    bool RebuildRequired,
    CovenantHealthTransition LastHealthTransition,
    CovenantAggregateCountStatusDto HeadCounts,
    string Retention);

public sealed record CovenantAggregateCountStatusDto(
    CovenantAggregateCountAvailability Availability,
    long? GlobalHeads,
    long? CampaignHeads,
    CovenantAggregateCountDiagnostic? Diagnostic);
```

Add closed `CovenantAggregateCountAvailability.Available` and `.Unavailable` plus nullable `CovenantAggregateCountDiagnostic.CanonicalUnavailable`, `.CanonicalDegraded`, and `.CountReadFailed`. Both are `byte` enums with values in the order shown starting at one and exact `StringOnlyJsonStringEnumConverter<TEnum>` attributes. Register both records, both enums, their arrays, and `ApiResponse<CovenantCapabilityStatusDto>` with `ArcanumJsonContext`.

Define the primary ports:

```csharp
public interface ICovenantInspectionService
{
    Task<Result<CovenantPageDto>> ListAsync(CovenantListRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantPageDto>> QueryAsync(CovenantQueryRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantDetailDto>> DetailAsync(CovenantDetailRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantVersionPageDto>> VersionsAsync(CovenantVersionsRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantSourcesDto>> SourcesAsync(CovenantSourcesRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantLeasedServiceResult<CovenantExplainDto>>> ExplainAsync(CovenantExplainRequest request, OperatorAuthorityContext authority, CancellationToken cancellationToken = default);
}

public interface ICovenantOperatorMutationService
{
    Task<Result<CovenantMutationPreflightDto>> PrepareSetAsync(CovenantSetPrepareRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantMutationPreflightDto>> PrepareRetireAsync(CovenantRetirePrepareRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantMutationResultDto>> SetAsync(CovenantSetRequest request, OperatorAuthorityContext authority, CovenantWriteLease writeLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantMutationResultDto>> RetireAsync(CovenantRetireRequest request, OperatorAuthorityContext authority, CovenantWriteLease writeLease, CancellationToken cancellationToken = default);
}
```

`CampaignPathIdentityContracts.cs` is the sole owner of the Campaign-path wire types, closed status and remediation vocabulary, internal probe continuation, and Core port. Define them here so Task 7 implements this contract without recreating it:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathIdentityState>))]
public enum CampaignPathIdentityState : byte
{
    Active = 1,
    LegacyUnresolved = 2,
    Missing = 3,
    Invalid = 4,
    OrphanCleanupPending = 5,
    OperationPending = 6
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathIdentityRemediation>))]
public enum CampaignPathIdentityRemediation : byte
{
    None = 0,
    Register = 1,
    RepairMoved = 2,
    RetryPendingOperation = 3,
    ReviewOrphan = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathMarkerEffect>))]
public enum CampaignPathMarkerEffect : byte
{
    None = 0,
    Create = 1,
    Replace = 2,
    Delete = 3,
    QuarantineAndCreate = 4
}

public sealed record CampaignPathIdentityStatusRequest(
    Guid? CampaignId,
    bool AllCampaigns = false,
    int Limit = 50,
    string? Cursor = null);

public sealed record CampaignPathIdentityPrepareRequest(
    Guid OperationId,
    CampaignPathIdentityOperation Operation,
    string? Path);

public sealed record CampaignPathIdentityApplyRequest(
    Guid OperationId,
    string ApplyRequestDigest,
    string Token);

public sealed record CampaignPathIdentityStatusItemDto(
    Guid CampaignId,
    CampaignPathIdentityState State,
    long PathRevision,
    CampaignPathIdentityRemediation Remediation);

public sealed record CampaignPathIdentityStatusPageDto(
    CampaignPathIdentityStatusItemDto[] Items,
    string? NextCursor);

public sealed record CampaignPathIdentityPlanDto(
    Guid OperationId,
    Guid CampaignId,
    CampaignPathIdentityOperation Operation,
    string? NormalizedDisplayPath,
    long CurrentPathRevision,
    string? CurrentPhysicalIdentityDigest,
    string? ProspectivePhysicalIdentityDigest,
    bool TargetIdentityAvailable,
    bool ExistingMarkerPresent,
    bool MarkerConflict,
    CampaignPathMarkerEffect MarkerEffect,
    long ActiveTurnBlockerCount,
    bool OldMarkerCleanupRequired,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset ExpiresAt,
    string Token);

public sealed record CampaignPathIdentityResultDto(
    Guid OperationId,
    Guid CampaignId,
    CampaignPathIdentityOperation Operation,
    CampaignPathIdentityState State,
    long PathRevision,
    CampaignPathIdentityRemediation Remediation);

public enum CampaignPathApplyContinuationKind : byte
{
    New = 1,
    ActiveIntent = 2
}

public sealed record CampaignPathApplyContinuation(
    CampaignPathApplyContinuationKind Kind,
    Guid OperationId,
    CovenantDigest ApplyRequestDigest,
    CovenantDigest EffectDigest);

public abstract record CampaignPathApplyProbe
{
    private CampaignPathApplyProbe()
    {
    }

    public sealed record Terminal(CampaignPathIdentityResultDto Result) : CampaignPathApplyProbe;

    public sealed record Continue(CampaignPathApplyContinuation Continuation) : CampaignPathApplyProbe;
}

public interface ICampaignPathIdentityAdministration
{
    Task<Result<CampaignPathIdentityStatusPageDto>> StatusAsync(CampaignPathIdentityStatusRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CampaignPathIdentityPlanDto>> PrepareAsync(Guid campaignId, CampaignPathIdentityPrepareRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CampaignPathApplyProbe>> ProbeApplyAsync(Guid campaignId, CampaignPathIdentityApplyRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<CampaignPathIdentityResultDto>> ApplyAfterProbeAsync(Guid campaignId, CampaignPathIdentityApplyRequest request, OperatorAuthorityContext authority, CampaignPathApplyContinuation continuation, CovenantCampaignExclusiveLease exclusiveLease, CancellationToken cancellationToken = default);
}
```

The status request constructor requires exactly one of a nonempty `CampaignId` or `AllCampaigns=true`, clamps `Limit` to 1 through 200, and accepts a cursor only with the same selector. Prepare requires a nonempty operation ID and permits `Path=null` only for `Deregister`; every other operation requires one bounded path. `CurrentPhysicalIdentityDigest` and `ProspectivePhysicalIdentityDigest` are canonical lowercase-hex 32-byte digests computed from server-opened no-follow roots. Current is null only when no current physical-root side exists, and prospective is null only when no prospective side exists. A successful prepare never substitutes null for a required side that could not be opened. `TargetIdentityAvailable` is true exactly when the prospective side exists and was opened, so it is false for `Deregister`.

`MarkerEffect` is the complete marker-byte effect disclosed for confirmation. `None` means apply performs no marker-file mutation, `Create` means the opened target has no marker and receives one, `Replace` means an exact marker already owned by this Campaign is compare-replaced, `Delete` means an exact marker owned by this Campaign is compare-deleted, and `QuarantineAndCreate` means `TakeoverOrphan` durably quarantines the conflicting orphan before a no-replace create. `Create` requires `ExistingMarkerPresent=false`; `Replace` and `Delete` require an existing marker owned by this Campaign; `QuarantineAndCreate` requires `Operation=TakeoverOrphan`, `ExistingMarkerPresent=true`, and `MarkerConflict=true`; no other effect permits `MarkerConflict=true`. The record exposes no raw marker bytes, marker payload, file name, or filesystem handle. Apply requires a nonempty operation ID, one canonical 32-byte digest encoded as lowercase hex, and a bounded nonempty token. The probe and continuation are nonserializable and are never registered with `ArcanumJsonContext`.

`SessionCampaignBindingAdministrationContracts.cs` is the sole owner of the binding administration wire types and Core port:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionCampaignBindingRemediation>))]
public enum SessionCampaignBindingRemediation : byte
{
    None = 0,
    Resolve = 1,
    CampaignMissing = 2
}

public sealed record SessionCampaignBindingStatusRequest(
    Guid? SessionId,
    bool AllSessions = false,
    int Limit = 50,
    string? Cursor = null);

public sealed record SessionCampaignBindingPrepareRequest(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind FinalKind,
    Guid? CampaignId);

public sealed record SessionCampaignBindingApplyRequest(
    Guid OperationId,
    Guid SessionId,
    string ApplyRequestDigest,
    string Token);

public sealed record SessionCampaignBindingStatusItemDto(
    Guid SessionId,
    SessionCampaignBindingKind Kind,
    Guid? HistoricalCampaignId,
    long Revision,
    SessionCampaignBindingRemediation Remediation);

public sealed record SessionCampaignBindingStatusPageDto(
    SessionCampaignBindingStatusItemDto[] Items,
    string? NextCursor);

public sealed record SessionCampaignBindingPlanDto(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind FinalKind,
    Guid? CampaignId,
    long PriorRevision,
    string PriorBindingRowDigest,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset ExpiresAt,
    string Token);

public sealed record SessionCampaignBindingResultDto(
    Guid OperationId,
    Guid SessionId,
    SessionCampaignBindingKind FinalKind,
    Guid? CampaignId,
    long Revision);

public interface ISessionCampaignBindingAdministration
{
    Task<Result<SessionCampaignBindingStatusPageDto>> StatusAsync(SessionCampaignBindingStatusRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<SessionCampaignBindingPlanDto>> PrepareAsync(SessionCampaignBindingPrepareRequest request, OperatorAuthorityContext authority, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<SessionCampaignBindingResultDto>> ApplyAsync(SessionCampaignBindingApplyRequest request, OperatorAuthorityContext authority, CovenantWriteLease writeLease, CancellationToken cancellationToken = default);
}
```

The status selector follows the same exactly-one and 1-through-200 rules as Campaign status. Prepare accepts only final `GlobalOnly` or `Campaign`; `GlobalOnly` requires null Campaign ID and `Campaign` requires one nonempty Campaign ID. Apply requires the same nonempty operation and Session IDs, lowercase 32-byte digest, and bounded token. Every revision is a nonnegative `long`.

`CovenantAdministrationContracts.cs` is also the sole owner of the maintenance request/result DTOs and service port:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSchemaRepairOutcome>))]
public enum CovenantSchemaRepairOutcome : byte
{
    NoChange = 1,
    InstalledAbsentCanonicalFamily = 2,
    RecreatedOrdinaryIndex = 3,
    RebuiltAcceleratorTier = 4,
    ManualRecoveryRequired = 5
}

public sealed record CovenantSchemaRepairRequest;

public sealed record CovenantSchemaRepairResultDto(
    Guid OperationId,
    CovenantSchemaRepairOutcome Outcome,
    long AvailabilityGeneration,
    ulong AcceleratorEpoch,
    CovenantDiagnosticTag? Diagnostic,
    CovenantDiagnosticTag? Remediation);

public sealed record CovenantFamilyReinitializePrepareRequest(Guid OperationId);

public sealed record CovenantFamilyReinitializeRequest(
    Guid OperationId,
    string ApplyRequestDigest,
    string Token);

public sealed record CovenantFamilyReinitializePlanDto(
    Guid OperationId,
    CovenantDiagnosticTag[] CatalogDefects,
    long CovenantRowCount,
    long TaintedDatabaseArtifactCount,
    long ManagedFileCount,
    long NonrevocableDisclosureCount,
    long PreservedCampaignCount,
    long PreservedSessionCount,
    long PreservedBindingCount,
    long PreservedUnrelatedMemoryCount,
    long RequiredFreeBytes,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset ExpiresAt,
    string Token);

public sealed record CovenantIndexRebuildRequest;

public interface ICovenantMaintenanceService
{
    Task<CovenantExclusiveLeasedServiceResult<CovenantSchemaRepairResultDto>> RepairSchemaAsync(CovenantSchemaRepairRequest request, OperatorAuthorityContext authority, CovenantExclusiveLease exclusiveLease, CancellationToken cancellationToken = default);
    Task<Result<CovenantFamilyReinitializePlanDto>> PrepareFamilyReinitializeAsync(CovenantFamilyReinitializePrepareRequest request, OperatorAuthorityContext authority, CovenantInstallationReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<LongRunningOperationDto>> StartFamilyReinitializeAsync(CovenantFamilyReinitializeRequest request, OperatorAuthorityContext authority, CovenantWriteLease writeLease, CancellationToken cancellationToken = default);
    Task<Result<LongRunningOperationDto>> StartIndexRebuildAsync(CovenantIndexRebuildRequest request, OperatorAuthorityContext authority, CovenantWriteLease writeLease, CancellationToken cancellationToken = default);
}
```

Schema repair and index rebuild accept only `{}` on the wire. Reinitialize prepare requires one nonempty client operation ID. Apply requires that same ID, one lowercase 32-byte apply-request digest, and a bounded token. Every count and byte value is a nonnegative `long`, `CatalogDefects` is deep-copied and bounded, and no maintenance DTO carries a path, key, content, marker identity, live lease, or free-form error.

Every protected reader or preflight endpoint in Tasks 4 through 10 and every lifecycle or export adapter modified in Tasks 14 through 17 either acquires its exact route-scoped operation lease before service invocation and passes it as a required argument, or receives the single lease-owned `CovenantLeasedServiceResult<T>` from the management-explain loader. Those routes transfer the complete success or error result plus that lease to Task 2's lease-bound response. Writer routes recheck the captured dataset inside their immediate transaction; a destructive coordinator enters on its exclusive lifecycle lease without acquiring an ordinary lease that it must drain, retains that lease through committed health publication and writer reopen, and selects exactly one Plan 02 exclusive disposition before disposal. Services may revalidate but never dispose a caller-owned route lease. An architecture test rejects `Results.Json`, plain `TypedResults`, or a protected service method without one of these explicit ownership shapes or a closed approved content-free lifecycle result on the Task 18 route inventory.

`CovenantLeasedServiceResult<T>` is a nonserializable Core transfer object used only when a lower layer must create the lease, currently management explain. It contains one `Result<T>` and supports one atomic `TakeLease()`; disposal before transfer releases the lease. It cannot be registered in a JSON context.

`CovenantExclusiveLeasedServiceResult<T>` is the corresponding nonserializable ownership transfer for an exclusive response. It contains one `Result<T>`, one `ICovenantExclusiveOperationLease`, one mandatory `CovenantExclusiveLeaseDisposition` selected from typed operation-phase evidence, and one mandatory Plan 02 `ICovenantExclusivePostDispositionFinalizer`. `TakeOwnership()` atomically transfers all four values at most once; it has no default or nullable disposition or finalizer, and unclaimed disposal selects no implicit reopen. Task 1 consumes the Plan 02 hook and never defines a second interface.

The finalizer is one-shot and nonserializable. The response boundary may invoke it only after the exact lease `CompleteAsync` returns success and before disposing that lease. It is skipped when disposition fails. The schema-repair implementation consumes only `ReopenPending`: successful `CommitAndReopen` compare-and-swaps it to `Completed`, successful proven `RollbackAndReopen` compare-and-swaps it to `Abandoned`, and successful `KeepClosed` verifies it remains `ReopenPending`. A failed disposition skips the finalizer and leaves `ReopenPending`. A finalizer failure never triggers a second disposition. A lifecycle with no durable post-disposition transition supplies the sealed Core no-op singleton; null, a delegate, or ambient service lookup is forbidden.

Define `ProtectedDerivedRead<T>` as a nonserializable Core transfer object containing the typed artifact result plus its complete sensitivity decision. Define `IProtectedDerivedReadStore` with closed methods for Session query, detail, Entry page, stream-replay page, attachment metadata/content metadata, export, and Session-scoped memory status, sources, and explain; Saga list and Divination hydration; Lexicon list, detail, and search; and generic memory search hydration. Each method accepts the caller-owned `ICovenantSnapshotReadLease` and returns `Result<ProtectedDerivedRead<T>>`. A route bounded to Global or one Campaign passes `CovenantReadLease`; a route whose request can span Campaigns passes the one Plan 02 `CovenantInstallationReadLease` obtained from `AcquireInstallationReadAsync`. The production implementation validates the required coverage before opening the snapshot and must read every returned Session title, summary, Entry, attachment record, Saga, Lexicon row, and exact `artifact_sensitivity` label in the same bounded SQLite snapshot. Attachment bytes are subsequently read through the same verified open-file identity while that lease remains held. Missing, duplicate, malformed, mismatched, or under-scoped committed-sensitive evidence is an integrity failure. No route may read an artifact through `ISessionRepository`, `ISagaMemoryStore`, `ILexiconService`, or `ISessionAttachmentStore` and then perform a second label query.

`CampaignExportResult` contains the existing `CampaignExportDto` plus one required `CampaignExportExclusionCounts`. The count record has checked `long` fields for excluded Sessions, titles, summaries, Entries, tool artifacts, attachments, and other derived artifacts. Counts and emitted Campaign bytes come from the same SQLite snapshot. A zero count is an observed zero, never an unavailable or failed inventory. `SessionExportResult` remains the existing wire shape, but the protected read port returns the approved typed protected-export policy failure before constructing it when any artifact in the Session graph is tainted. It never returns a partial Session payload.

Use these exact closed methods, with `ProtectedDerivedRead<T>` carrying one validated aggregate `ArtifactSensitivityLabel` and never becoming a wire type:

```csharp
public interface IProtectedDerivedReadStore
{
    Task<Result<ProtectedDerivedRead<SessionQueryResult>>> QuerySessionsAsync(SessionQueryRequest request, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<Session?>>> ReadSessionAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<IReadOnlyList<Entry>>>> ReadSessionEntriesAsync(Guid sessionId, int offset, int limit, DateTimeOffset? beforeCreatedAt, Guid? beforeId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<IReadOnlyList<Entry>>>> ReadSessionReplayAsync(Guid sessionId, long afterSequence, int limit, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<IReadOnlyList<SessionAttachmentRecord>>>> ReadSessionAttachmentsAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<SessionAttachmentRecord?>>> ReadSessionAttachmentAsync(Guid sessionId, Guid attachmentId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<SessionExportResult>>> ReadSessionExportAsync(Guid sessionId, SessionExportFormat format, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<CampaignExportResult>>> ReadCampaignExportAsync(Guid campaignId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<MemoryStatusDto>>> ReadSessionMemoryStatusAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<MemorySourcesDto>>> ReadSessionMemorySourcesAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<MemoryExplainDto>>> ReadSessionMemoryExplainAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<SagaMemoryDto[]>>> ReadSagaListAsync(string? query, Guid? sessionId, int limit, int offset, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<SagaSearchResult>>> ReadSagaDivinationAsync(SagaSearchRequest request, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<IReadOnlyList<LexiconEntryDto>>>> ReadLexiconListAsync(ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<LexiconEntryDto?>>> ReadLexiconDetailAsync(string name, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<IReadOnlyList<LexiconEntryDto>>>> ReadLexiconSearchAsync(IReadOnlyList<string> entities, int limit, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
    Task<Result<ProtectedDerivedRead<MemorySearchResponse>>> ReadGenericMemorySearchAsync(MemorySearchRequest request, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken = default);
}
```

Add the shared disclosure as exact constants plus typed help targets. `EnablementText` states that eligible content may be sent on every primary, fallback, retry, compression, and tool-loop attempt, potentially to different configured providers and models in one turn. `DestructiveOperationText` states that local disable, reset, restore purge, family reinitialize, and healthy-catalog factory erasure cannot revoke provider logs, automatic provider prompt caches, encrypted backup copies, unmanaged files, or other nonrevocable disclosures. A closed help-target kind distinguishes official provider retention documentation, the Compendium Providers configuration page, and the Plan 05 operator-guide anchor. `ResolveHelpTargets(IReadOnlyList<ProviderSettings>)` recognizes only exact trusted provider identities: `api.openai.com` uses `https://developers.openai.com/api/docs/guides/your-data#default-usage-policies-by-endpoint`, `CodexCli` uses `https://openai.com/policies/how-your-data-is-used-to-improve-model-performance/`, and `ClaudeCodeCli` uses `https://privacy.claude.com/en/collections/10672565-data-handling-retention`. Unknown, proxy, and self-hosted OpenAI-compatible endpoints receive the typed Providers-page target plus `docs/Arcanum.README.md#covenant-provider-retention-and-deletion`; a display name never selects an external URI. Every result also includes the operator-guide fallback. This contract contains no local provider-cache toggle and no claim that Arcanum can change provider retention.

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantRetentionHelpTargetKind>))]
public enum CovenantRetentionHelpTargetKind : byte
{
    OfficialProviderDocumentation = 1,
    ProvidersConfiguration = 2,
    OperatorGuide = 3,
}

public sealed record CovenantRetentionHelpTarget(
    CovenantRetentionHelpTargetKind Kind,
    string Label,
    string? Target);

public static class CovenantExternalRetentionDisclosure
{
    public const string EnablementText = "Enabling The Covenant sends eligible content on every primary, fallback, retry, compression, and tool-loop provider attempt. A single turn may use different configured providers or models. Provider logs and automatic prompt caches are outside local reset and cannot be revoked by Arcanum. Arcanum suppresses only its own explicit cache instructions for Covenant-bearing calls.";
    public const string DestructiveOperationText = "Local disable, reset, protected-state purge, family reinitialize, and factory erasure cannot revoke content retained in provider logs or automatic prompt caches, encrypted backup copies, unmanaged files, or other nonrevocable disclosures. Review each configured provider's retention and deletion documentation and complete any external deletion separately.";
    public const string OperatorGuideTarget = "docs/Arcanum.README.md#covenant-provider-retention-and-deletion";

    public static IReadOnlyList<CovenantRetentionHelpTarget> ResolveHelpTargets(
        IReadOnlyList<ProviderSettings> providers);
}
```

`OfficialProviderDocumentation` and `OperatorGuide` require a nonempty target. `ProvidersConfiguration` requires `Target=null` and is handled through typed Compendium navigation. Deduplicate help targets in stable provider configuration order, append the operator guide exactly once, reject userinfo and non-HTTPS official URIs, and never emit a configured provider endpoint as a help URI.

Add the full approved `ErrorCodes.Covenant` family plus the new Hub, Session, and Campaign codes. Extend `ArcanumErrorMapper.ResolveStatusCode` with every specified status. The `wireTypes` and `responseTypes` arrays in Step 1 are the exhaustive Task 1 source-generation inventory. Add one `[JsonSerializable(typeof(...))]` root for every listed type to `ArcanumJsonContext`; a transitive nested-type discovery does not replace an explicit listed root. This task alone owns every memory, Campaign-path, Session-binding, schema-repair, family-reinitialize, index-rebuild, and Campaign-export registration above, including each nested item, enum, array, `LongRunningOperationDto`, and required `ApiResponse<T>`. The nonserializable probe, continuation, authorities, leases, and service transfer objects are explicitly absent. Tasks 4 through 10 map and implement these contracts without adding another DTO or JSON registration.

- [ ] **Step 4: Run the green contract tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantWireContractTests|FullyQualifiedName~CovenantErrorMappingTests|FullyQualifiedName~CovenantExternalRetentionDisclosureTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
```

Expected: PASS, with numeric enum payloads rejected and every new type returning non-null source-generated metadata.

- [ ] **Step 5: Refactor and verify ownership**

Keep API memory DTOs under `Core/Memory` and Campaign or Session administration under `Core/TheForge`. Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: both commands exit zero. No `/api` DTO uses `[JsonPropertyName]`, anonymous objects, or reflection serialization.

### Task 2: Enforce Covenant authority and no-context policy before request binding

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantAuthorityRequirementMetadata.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantContextPolicyRequirementMetadata.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantRequestFeatures.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantAuthorityEndpointFilter.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantDerivedReadGuard.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantDerivedReadEndpointInventory.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantEndpointConventionBuilderExtensions.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Results/CovenantLeaseBoundJsonResult.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Results/CovenantLeaseBoundStreamResult.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Results/CovenantProtectedResponseHeaders.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedDerivedReadStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Security/ArcanumApiHeaders.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SpellExecutionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionDivinationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SagaEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/MemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CampaignEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/InferenceExecuteWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantAuthorityBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantContextPolicyHeaderTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/ApiSurfaceContractTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantDerivedReadEndpointInventoryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantDerivedReadRaceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SagaEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/InferenceExecuteWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/Streaming/SseStreamWriterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs`

**Interfaces:**

- Consumes: Plan 03 `CovenantAuthorityRequirement`, `IOperatorAuthorityContextIssuer.Issue(CovenantAuthorityRequirement requirement)`, and `OperatorAuthorityContext`, bound to the current clean authority epoch.
- Consumes: Plan 02's operation gate, `ICovenantSnapshotReadLease`, scoped `CovenantReadLease`, and all-scope `CovenantInstallationReadLease` from `AcquireInstallationReadAsync`; Plan 03 `ISessionSensitivityStateReader`; Plan 03 `IProtectedArtifactTransferStore` for fork; Plan 03 `CovenantPreviewServiceResult<ContextPreviewResult>`; Plan 03 `LabeledSessionEntryEvent`; and Task 1 `IProtectedDerivedReadStore` plus its nonserializable `ProtectedDerivedRead<T>` result.
- Produces: `HttpContext.RequireCovenantAuthority()`, `HttpContext.CovenantContextPolicy()`, `RequireCovenantReadAuthority()`, `RequireCovenantOperatorAuthority(CovenantAuthorityRequirement)`, `RequireConditionalCovenantReadAuthority()`, `AllowCovenantContext()`, one `CovenantLeaseBoundJsonResult<T>`/`CovenantLeaseBoundStreamResult` response boundary over Plan 02's `ICovenantOperationLease` with a mandatory exclusive-disposition arm, `CovenantProtectedResponseHeaders`, the production `ProtectedDerivedReadStore`, and a closed derived-read endpoint inventory.

- [ ] **Step 1: Write the failing pre-binding matrix**

Create exact tests:

```csharp
[Theory]
[InlineData("/api/intelligence/ping")]
[InlineData("/api/intelligence/ping-stream")]
[InlineData("/api/intelligence/context/inspect")]
[InlineData("/api/prompts/11111111-1111-1111-1111-111111111111/test")]
[InlineData("/api/prompts/example/execute")]
[InlineData("/api/spells/example/execute")]
[InlineData("/api/spells/example/cast")]
[InlineData("/v1/chat/completions")]
public async Task Wrong_api_key_returns_401_before_any_body_byte_is_read(string path)

[Theory]
[InlineData("NONE")]
[InlineData("None")]
[InlineData("default")]
[InlineData("none,none")]
public async Task Invalid_context_policy_returns_400_before_binding(string value)

[Fact]
public async Task Explicit_none_is_recorded_as_an_irrevocable_request_feature()

[Fact]
public async Task Protected_routes_emit_exact_private_header_tuple_on_success_and_failure()

[Theory]
[InlineData("/api/intelligence/ping")]
[InlineData("/api/intelligence/ping-stream")]
[InlineData("/api/prompts/example/execute")]
[InlineData("/api/spells/example/execute")]
[InlineData("/v1/chat/completions")]
public async Task Protected_buffered_and_streaming_responses_set_exact_private_no_cache_headers_before_first_byte(string path)

[Fact]
public async Task Endpoint_filter_rejects_a_stale_authority_epoch()

[Fact]
public void Every_session_entry_summary_title_attachment_search_fork_and_export_route_declares_a_derived_read_policy()

[Theory]
[InlineData("PlanDataRetentionPrune")]
[InlineData("ApplyDataRetentionPrune")]
[InlineData("DeleteDataRetentionSession")]
[InlineData("DeleteDataRetentionAttachment")]
[InlineData("ResetDataRetentionMemory")]
[InlineData("PlanFactoryResetDataRetention")]
[InlineData("FactoryResetDataRetention")]
[InlineData("EmbeddingsReset")]
[InlineData("ExportCampaign")]
[InlineData("RegisterCampaign")]
[InlineData("DeleteCampaign")]
public void Existing_lifecycle_and_campaign_export_routes_declare_the_exact_covenant_policy(string endpointName)

[Fact]
public async Task Tainted_entry_and_label_are_read_in_one_snapshot_and_lease_survives_serialization()

[Fact]
public async Task Plaintext_session_export_refuses_tainted_graph_before_archive_or_response_byte()

[Fact]
public async Task Campaign_export_payload_and_typed_exclusion_counts_share_one_snapshot()

[Theory]
[InlineData("SessionTitleAndSummary")]
[InlineData("SessionEntry")]
[InlineData("SessionAttachment")]
[InlineData("SessionMemoryStatus")]
[InlineData("SessionMemorySources")]
[InlineData("SessionMemoryExplain")]
[InlineData("Saga")]
[InlineData("LexiconCollection")]
[InlineData("LexiconDetail")]
[InlineData("LexiconSearch")]
[InlineData("GenericMemorySearch")]
public async Task Production_derived_read_returns_artifact_and_every_label_from_one_snapshot(string readKind)

[Fact]
public async Task Reset_racing_a_long_transcript_stream_drains_before_erasure_completion()

[Fact]
public async Task Live_session_stream_revalidates_each_labeled_event_and_refuses_before_that_event_bytes()

[Fact]
public async Task Lease_bound_json_serializes_ApiResponse_with_exact_source_generated_type_info_before_disposal()

[Fact]
public async Task Lease_bound_error_and_stream_hold_revalidate_and_dispose_through_completion()

[Fact]
public async Task Lease_bound_exclusive_result_requires_one_explicit_disposition_and_recovery_owned_token()

[Fact]
public async Task Exclusive_post_disposition_finalizer_runs_once_after_success_and_before_lease_disposal()

[Fact]
public async Task Failed_exclusive_disposition_skips_finalizer_and_leaves_recovery_journal_nonterminal()

[Fact]
public async Task Exclusive_finalizer_failure_never_attempts_a_second_disposition()

[Fact]
public async Task Explicit_none_untainted_cacheable_response_does_not_receive_protected_private_headers()

[Fact]
public async Task Feature_disabled_but_tainted_read_still_requires_clean_authority()

[Theory]
[InlineData("InferenceExecuteWriter")]
[InlineData("SseStreamWriter")]
public async Task Production_stream_writers_never_overwrite_the_exact_protected_header_tuple(string writer)
```

Extend `ApiSurfaceContractTests.CorsPolicy_allows_the_documented_request_headers_and_exposes_the_documented_response_headers` to expect `X-Arcanum-Context-Policy`.

- [ ] **Step 2: Run the red boundary tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAuthorityBoundaryTests|FullyQualifiedName~CovenantContextPolicyHeaderTests|FullyQualifiedName~ApiSurfaceContractTests.CorsPolicy"
```

Expected: FAIL because the header is absent from CORS and eligible endpoints have no pre-binding policy feature.

- [ ] **Step 3: Add the pre-binding boundary**

Implement closed metadata and feature types:

```csharp
public sealed record CovenantAuthorityRequirementMetadata(
    CovenantAuthorityRequirement Requirement);

internal sealed record CovenantAuthorityFeature(
    OperatorAuthorityContext Context,
    CovenantAuthorityRequirement Requirement,
    ulong AuthorityEpoch);

internal sealed record CovenantContextPolicyFeature(
    CovenantContextPolicy Policy);
```

Extend `ApiBootstrapper.UseArcanumApiKeyAuthentication` so its successful constant-time comparison performs these actions before `next()`:

1. Read `CovenantContextPolicyRequirementMetadata` from `context.GetEndpoint()`.
2. Validate exactly zero values or one lowercase ASCII `none` value. Reject multiple values, any comma, empty input, mixed case, and every other value with a typed HTTP 400.
3. Store one `CovenantContextPolicyFeature`; later code can read it and cannot replace it.
4. Read `CovenantAuthorityRequirementMetadata`, issue the current typed context, and store `CovenantAuthorityFeature`.
5. Call the single `CovenantProtectedResponseHeaders.Apply` helper before returning any protected success or failure and before the first buffered or SSE byte. It sets exactly `Cache-Control: no-store, private`, `Pragma: no-cache`, and `Expires: 0`.

Add `CovenantAuthorityEndpointFilter` to recheck the current epoch and materialize typed authority. `RequireCovenantReadAuthority` attaches `ProtectedRead`. `RequireCovenantOperatorAuthority(CovenantAuthorityRequirement requirement)` rejects `ProtectedRead`, requires one of the other five exact codes, and attaches that value with the filter. Covenant management mutation uses `CovenantManage`, Campaign path and deletion use `CampaignPathManage`, Session binding apply uses `SessionBindingResolve`, schema/reset/restore/reinitialize lifecycle surfaces use `LifecycleManage`, and labeled ordinary retention uses `SensitivityRetentionPurge`. Conditional protected reads issue `ProtectedRead` only when their content-free preflight proves a protected arm. A route cannot accept a context issued for another requirement.

Mark endpoint names `PostIntelligencePing`, `PostIntelligencePingStream`, `PostIntelligenceContextInspect`, `PostOpenAiChatCompletions`, `TestPrompt`, `Prompt_Execute`, `Prompt_ExecuteStream`, `Spell_Execute`, `Spell_ExecuteStream`, and `Spell_Cast` with `AllowCovenantContext`.

Add `ArcanumApiHeaders.ContextPolicy = "X-Arcanum-Context-Policy"` and include it in the existing CORS allow-list. Preserve API-key authentication as the first operation, so a wrong key plus malformed policy returns 401.

Attach `RequireConditionalCovenantReadAuthority` to every Session list/detail, including `GetCampaignSessions` and the post-update detail returned by `UpdateSession`, entries, attachment metadata/content, stream and replay, fork, export, Divination, Session-scoped memory status/sources/explain, Saga, Lexicon list/show/search, generic memory result, Campaign export, and other route whose DTO can carry a title, summary, Entry, tool detail, attachment-derived value, or snippet. Route these production reads except `ForkSession` through Task 1's `IProtectedDerivedReadStore`; the fork exception uses the compound transfer capability specified below. `ProtectedDerivedReadStore` owns one bounded SQLite read transaction per result and composes the existing Session, attachment, Saga, and Lexicon SQL through transaction-scoped helpers so the artifact and every exact label are observed in that same snapshot. After Plan 03's labeled title writer commits, `UpdateSession` obtains its response detail through this same port rather than returning an already-hydrated Session. For each protected read-port route, `CovenantDerivedReadGuard` validates the request's content-free scope selector first, calls `AcquireInstallationReadAsync` exactly once when the requested snapshot can span Campaigns, and otherwise calls `AcquireReadAsync` for the exact Global or Campaign scope. It passes that concrete lease as `ICovenantSnapshotReadLease` through every protected store read and response step. It never supplements an under-scoped lease with a nested acquisition. The guard evaluates the returned sensitivity decision even while `FeatureSettings.Covenant` is false and transfers the lease with the complete `Result<T>` to `CovenantLeaseBoundJsonResult<T>` or the direct-stream variant. The boundary revalidates before the first byte, calls the shared exact-header helper, serializes `ApiResponse<T>` with the matching `ArcanumJsonContext` `JsonTypeInfo<ApiResponse<T>>` for both success and error, awaits stream/serialization completion, and disposes in `finally`. Unlabeled committed-sensitive evidence fails closed. Generic search and indexes remain physically free of Covenant-derived documents per Plan 03, so post-ranking filtering is never used.

`CovenantLeaseBoundJsonResult<T>` has two closed construction paths. The ordinary path accepts one nonexclusive `ICovenantOperationLease`. The exclusive path accepts only `CovenantExclusiveLeasedServiceResult<T>` plus a bounded recovery-owned completion token supplied by the lifecycle coordinator, never `HttpContext.RequestAborted`. After serialization or a serialization exception, the exclusive path calls `CompleteAsync` exactly once with the transferred disposition and observes that result. Only when it succeeds does the boundary call `FinalizeAfterSuccessfulDispositionAsync` exactly once with the same disposition and lifecycle token. It then disposes the lease exactly once in `finally`. A failed disposition skips the finalizer, leaves the scope closed, and records a recoverable lifecycle failure. A finalizer failure records a typed recovery failure, leaves the durable journal nonterminal, and cannot call `CompleteAsync` or the finalizer again. Ordered-event tests pin `serialize -> CompleteAsync success -> finalizer -> lease disposal`, including serialization failure. An exclusive lease passed through the ordinary constructor, a missing disposition or finalizer, a second ownership take, or disposal without completion fails the architecture tests.

Register `IProtectedDerivedReadStore` once in Infrastructure DI. Refactor only the production read paths in `SessionRepository`, `SagaMemoryStore`, and `LexiconService` needed by the closed port; retain their existing public interfaces for unprotected internal callers. The port coordinates those existing query owners inside one shared snapshot and never copies Session SQL, Saga Divination ranking, Lexicon matching, or Plan 02 Covenant search/compiler logic. Add an architecture test that every endpoint in the conditional-read inventory calls this port or Plan 03's `IProtectedArtifactTransferStore` for fork. A read followed by a separate `IArtifactSensitivityStore` lookup fails the test.

`IntelligenceEndpoints` unwraps Plan 03's closed preview result without inventing a lease. Its authority-free arm returns the byte-identical disabled preview through the ordinary typed response path. Its lease-owned arm atomically transfers the exact read lease into `CovenantLeaseBoundJsonResult<ContextPreviewResult>`, which owns the matching source-generated `ApiResponse<ContextPreviewResult>` type information. The boundary revalidates and applies the protected header tuple before the first byte, awaits complete serialization, and disposes exactly once. No endpoint reloads Session history after Plan 03 produced the authorized preview snapshot.

The live Session stream consumes only Plan 03 `LabeledSessionEntryEvent` values. It holds the same conditional-read lease across bounded replay and live delivery and revalidates that lease plus each event's exact sensitivity evidence before the event. Before the first byte, a currently tainted Session or a response admitted under clean read authority receives the exact protected header tuple. A stream that began in the proven-clean, authority-free arm terminates with a typed error before any byte of a later protected event, because headers cannot be upgraded after streaming begins. It never accepts a bare `Entry` from `SessionEventHub` or performs a second label lookup.

Make `InferenceExecuteWriter.WriteBufferedAsync` call `CovenantProtectedResponseHeaders.ApplyIfProtected` before either success or error serialization. Make `InferenceExecuteWriter.WriteStreamAsync` and `SseStreamWriter.PrepareResponse` call `ApplyStreamingDefaultWithoutWeakening`. If the request feature marks the response protected, the helper applies the exact three-header tuple. Otherwise it preserves the existing streaming `Cache-Control: no-cache` default without adding `private`, `Pragma`, or `Expires`. Neither writer may assign `Cache-Control: no-cache` after the protected helper has run. An explicit `none`, untainted response that is eligible for generic response caching therefore does not inherit protected private headers.

Route `ForkSession` through Plan 03 `IProtectedArtifactTransferStore.ForkSessionAsync`. Its request factory creates one nonempty operation ID and the exact `Arcanum.Covenant.ProtectedSessionTransfer.v1` effect digest, then the route constructs `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.ProtectedSessionTransfer, effectDigest)` and passes it with the immutable Global or Campaign transfer scope to Plan 02 `AcquireProtectedTransferAsync`. The resulting single `CovenantProtectedTransferLease` supplies the same-snapshot source graph and its matching exclusive arm fences blob staging and destination commit. The route never nests a `CovenantReadLease` with a separately acquired exclusive lease. Consume the exact Plan 03 `ProtectedSessionTransferCompletion<SessionForkCommitReceipt>`. Its verified commit or proven precommit cleanup has already persisted the parent as `ReopenPending` with exact `CommitAndReopen` or `RollbackAndReopen`; uncertainty carries `KeepClosed` while the journal remains at its last proven earlier phase. The route transfers the completion result, compound lease, disposition, and exact finalizer through Task 1's exclusive lease-bound protected response. After the last JSON or mapped-error byte, the response owner calls the disposition once, invokes the finalizer only on success to reach `Completed` or `Abandoned`, and disposes exactly once. Failed disposition or finalizer retains `ReopenPending`; `KeepClosed` uses the sealed no-op finalizer and retains the earlier phase. The lifecycle token is recovery-owned and cannot inherit an aborted HTTP request after a durable commit.

Seed `CovenantDerivedReadEndpointInventory` with exact existing endpoint names rather than route-pattern guesses. `PlanDataRetentionPrune` and `PlanFactoryResetDataRetention` are operator-authorized preflight readers and hold read leases through serialization. `ApplyDataRetentionPrune`, `DeleteDataRetentionSession`, and `DeleteDataRetentionAttachment` are operator-authorized writers that recheck dataset generation in their immediate transactions. `ResetDataRetentionMemory` and `FactoryResetDataRetention` are operator-authorized destructive starters whose recovery workers own the exclusive transition. `ExportCampaign` is a conditional read whose lease covers exclusion inventory and archive serialization. `RegisterCampaign` and `DeleteCampaign` are operator-authorized Campaign path lifecycle routes completed in Task 7. Task 16 completes ordinary retention handlers, Task 17 completes the reset and factory coordinator handoff, and Task 18 rejects any registry drift.

- [ ] **Step 4: Run the green boundary tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAuthorityBoundaryTests|FullyQualifiedName~CovenantContextPolicyHeaderTests|FullyQualifiedName~CovenantDerivedReadEndpointInventoryTests|FullyQualifiedName~CovenantDerivedReadRaceTests|FullyQualifiedName~ApiSurfaceContractTests"
```

Expected: PASS, including zero body bytes read for unauthenticated oversized and malformed requests.

- [ ] **Step 5: Refactor and verify route inventory**

Add one data-driven inventory assertion covering every currently eligible endpoint name. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAuthorityBoundaryTests|FullyQualifiedName~ApiKeyEndpointFilterTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. The primary authority decision remains middleware-based and the filter remains defense in depth.

### Task 3: Separate durable Session-turn idempotency from response caching

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Security/InferenceIdempotencyPolicy.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Security/IdempotencyEndpointFilters.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Security/IdempotencyIdentity.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnIdempotencyAmbient.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SpellExecutionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.Watch.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantTurnIdempotencyTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/IdempotencyEndpointFilterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnDisconnectAndIdempotencyCharacterizationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientTests.cs`

**Interfaces:**

- Consumes: Plan 03 `ISessionTurnClaimCoordinator`, `ISessionSensitivityStateReader`, and the canonical `SessionTurnRequest` digest builder.
- Produces: `InferenceIdempotencyPolicy.DecideAsync`, returning `SessionTurnClaim`, `GenericResponseCache`, or `BypassResponseCache` before any cache lookup.

- [ ] **Step 1: Write the failing idempotency-policy tests**

Add exact methods:

```csharp
[Theory]
[InlineData("/api/intelligence/ping")]
[InlineData("/api/prompts/example/execute")]
[InlineData("/api/spells/example/execute")]
public async Task Session_backed_public_routes_require_one_canonical_uuid_key(string path)

[Fact]
public async Task Context_default_stateless_inference_bypasses_the_generic_response_cache()

[Fact]
public async Task Explicit_no_context_stateless_inference_may_replay_from_the_generic_cache()

[Fact]
public async Task Disabled_but_tainted_session_reads_sensitivity_then_uses_the_turn_claim()

[Fact]
public async Task Disabled_and_untainted_session_still_uses_the_turn_claim_after_one_summary_read()

[Fact]
public void Response_cache_fingerprint_distinguishes_default_from_none_context_policy()

[Fact]
public async Task Covenant_mutation_routes_ignore_http_idempotency_key()

[Fact]
public async Task No_context_cli_option_emits_exact_context_policy_header()
```

- [ ] **Step 2: Run the red policy tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantTurnIdempotencyTests|FullyQualifiedName~IdempotencyEndpointFilterTests|FullyQualifiedName~ArcanumApiClientTests.No_context_cli"
```

Expected: FAIL because Prompt and Spell still use `IdempotencyEndpointFilters` as a response cache and context policy is absent from the fingerprint.

- [ ] **Step 3: Implement the decision before cache access**

Add the closed decision:

```csharp
internal enum InferenceIdempotencyMode
{
    SessionTurnClaim = 1,
    GenericResponseCache = 2,
    BypassResponseCache = 3,
}
```

`InferenceIdempotencyPolicy.DecideAsync` applies this order:

1. Resolve one `Idempotency-Key` for a Session-backed public request. Missing, duplicate, comma-combined, or noncanonical UUID input returns HTTP 400.
2. Before returning a decision for a request naming an existing Session, perform the one indexed `ISessionSensitivityStateReader` summary read required to freeze current taint and clean-read-authority requirements. A missing, malformed, or stale projection fails closed before the response-cache filter or claim coordinator can run.
3. Return `SessionTurnClaim` for every Session-backed request, whether the Session is clean or tainted and whether Covenant is enabled or disabled. Publish the UUID plus the observed sensitivity revision to the Plan 03 claim coordinator and never enter `IIdempotencyClaimStore`.
4. Return `BypassResponseCache` when a stateless request uses `Default` and Covenant is enabled and healthy.
5. Return `GenericResponseCache` only for a stateless explicit `None` request or a stateless request processed while Covenant is disabled. No Session-backed arm is eligible for generic cache lookup or write.

Refactor `IdempotencyEndpointFilters.ForBoundArgument<TRequest>`, `ForRawBody`, and `InvokeCoreAsync` to consult the decision before `IIdempotencyClaimStore` lookup. Remove generic response-cache filters from Covenant set and retire routes.

Extend `IdempotencyIdentity.ComputeFingerprintHash` with one canonical byte for `CovenantContextPolicy.Default` or `CovenantContextPolicy.None`.

Update `ArcanumApiClient.AskAsync`, `AskStreamAsync`, `PreviewContextAsync`, `TestPromptAsync`, `ExecutePromptAsync`, `CastSpellAsync`, and watch request builders to emit `X-Arcanum-Context-Policy: none` when `CliInvocationOptions.NoContext` is true. Generate one client-turn UUID per logical CLI invocation and reuse it for transport retry and buffered or streaming continuation.

- [ ] **Step 4: Run the green idempotency suites**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantTurnIdempotencyTests|FullyQualifiedName~IdempotencyEndpointFilterTests|FullyQualifiedName~TurnDisconnectAndIdempotencyCharacterizationTests|FullyQualifiedName~ArcanumApiClientTests"
```

Expected: PASS. Each Session-backed call reads its current sensitivity summary before using one durable claim, and only eligible stateless calls can enter the generic response cache.

- [ ] **Step 5: Refactor and verify the existing cache contract**

Keep ordinary non-Covenant idempotent routes on their current cache behavior. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~IdempotencyEndpointFilterOwnershipTests|FullyQualifiedName~TurnIdempotencyAmbientTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero, including existing lease, heartbeat, replay, and fault tests.

### Task 4: Add protected status, list, detail, versions, sources, and explain routes

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantInspectionService.cs`
- Create: `src/RetroDownfall.Arcanum.Api/TheForge/CovenantMemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/MemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Memory/MemoryDtos.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantMemoryEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantInspectionServiceTests.cs`

**Interfaces:**

- Consumes: Task 1 `CovenantCapabilityStatusDto` and `CovenantAggregateCountStatusDto`; Plan 02 `ICovenantStore.ReadListPageAsync`, `ReadDetailAsync`, `ReadVersionPageAsync`, and `ReadSourcePageAsync` over the immutable management-read contracts and their `ICovenantSnapshotReadLease` parameter.
- Consumes: Plan 03 `ICovenantContextProvider` fresh preview path, `ICovenantAvailability`, and Plan 02's scoped `AcquireReadAsync` plus all-scope `AcquireInstallationReadAsync` operation-gate entries.
- Produces: `CovenantInspectionService` implementing Task 1 `ICovenantInspectionService`, plus `MapCovenantMemoryEndpoints`.

- [ ] **Step 1: Write failing protected-read and status tests**

Create exact methods:

```csharp
[Fact]
public async Task Memory_status_reports_content_free_covenant_health_without_read_authority()

[Fact]
public async Task Canonical_degraded_status_succeeds_with_unavailable_counts_and_typed_diagnostics()

[Theory]
[InlineData("/api/memory/covenant/list")]
[InlineData("/api/memory/covenant/detail")]
[InlineData("/api/memory/covenant/versions")]
[InlineData("/api/memory/covenant/sources")]
[InlineData("/api/memory/covenant/explain")]
public async Task Protected_reads_require_clean_read_authority_and_exact_private_headers(string path)

[Fact]
public async Task Detail_reads_both_lane_heads_without_calling_the_search_index()

[Fact]
public async Task Explain_uses_a_fresh_snapshot_plan_and_preview_without_publishing()

[Fact]
public async Task Generic_memory_all_never_queries_covenant()

[Fact]
public async Task Reset_waits_until_protected_response_serialization_releases_its_lease()

[Fact]
public async Task Stale_generation_before_first_byte_returns_no_protected_body()
```

- [ ] **Step 2: Run the red read-surface tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantMemoryEndpointTests|FullyQualifiedName~CovenantInspectionServiceTests|FullyQualifiedName~MemoryEndpointTests"
```

Expected: FAIL with 404 for `/api/memory/covenant/list` and a missing Covenant status member.

- [ ] **Step 3: Implement bounded canonical reads**

Add one `CovenantCapabilityStatusDto Covenant` property to `MemoryStatusDto`, using the exact Task 1 contract. Extend `MemoryEndpoints.BuildStatusAsync` to copy the typed, content-free Plan 01 `CovenantAvailabilitySnapshot` fields exactly. Query aggregate counts only when canonical state permits a bounded count read. When canonical state is degraded or unavailable, return HTTP 200 with `HeadCounts.Availability=Unavailable`, both nullable counts absent, and the closed typed diagnostic; never invent zero and never touch an optional canonical table. Do not add `Covenant` to `MemorySearchScope` or any generic search switch.

`CovenantMemoryEndpoints` canonicalizes and validates the content-free selector before lease acquisition. An `AllScopes` list calls `AcquireInstallationReadAsync` exactly once and passes the resulting `CovenantInstallationReadLease` as the caller-owned `ICovenantSnapshotReadLease`; Global or one-Campaign list, detail, versions, and sources call `AcquireReadAsync` for the exact scope. Versions and sources validate the request's explicit scope/Campaign pair and bind it into cursor or request identity before acquisition; the service verifies that the returned entry or version has that same owner before producing a DTO. Task 5 applies the same branch to query. `CovenantInspectionService` validates the concrete lease's coverage before storage access, performs one bounded store call, and revalidates the lease before returning. Insufficient coverage fails before SQL, and neither endpoint nor service acquires a nested lease. `DetailAsync` uses the exact scoped-key store read and never consults FTS. `ExplainAsync` validates its explicit Global or Campaign evaluation before it requests one fresh management-only snapshot and provider-specific preview through Plan 03 Task 15's `CreateManagementExplainStateAsync`, so authenticated explain remains available while live Covenant inference is disabled. It creates no collector, detaches the state lease into `CovenantLeasedServiceResult<CovenantExplainDto>`, and transfers it through response disposal without mutation or cache publication. The endpoint does not acquire a second nested lease for explain.

The endpoint transfers sole lease ownership together with the service's complete `Result<T>` to Task 2's `CovenantLeaseBoundJsonResult<T>`. Its `ExecuteAsync` revalidates immediately before the first byte, sets the exact private no-cache headers, maps status, wraps the result with `ApiResponse<T>.FromResult`, writes with the exact `JsonTypeInfo<ApiResponse<T>>`, awaits serialization, and disposes in `finally`. Errors and exceptions follow the same lease-owned boundary. Do not use `Results.Json(dto)` or dispose any route lease before `IResult.ExecuteAsync` completes.

Map the typed POST routes in `CovenantMemoryEndpoints`. Use endpoint names `Covenant_List`, `Covenant_Detail`, `Covenant_Versions`, `Covenant_Sources`, and `Covenant_Explain`; reserve `Covenant_Query` for Task 5. Apply read authority and the exact protected-header metadata. Return `CovenantLeaseBoundJsonResult<T>` constructed with the matching `ArcanumJsonContext` type information.

- [ ] **Step 4: Run the green read-surface tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantMemoryEndpointTests|FullyQualifiedName~CovenantInspectionServiceTests|FullyQualifiedName~MemoryEndpointTests"
```

Expected: PASS. Generic memory behavior remains unchanged, canonical degradation returns typed unavailable counts without optional-table I/O, and all protected reads emit exactly `Cache-Control: no-store, private`, `Pragma: no-cache`, and `Expires: 0`.

- [ ] **Step 5: Refactor and verify endpoint names**

Extend `ApiSurfaceContractTests.MemoryEndpoints_are_registered_with_unique_endpoint_names` with every Covenant endpoint name. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ApiSurfaceContractTests.MemoryEndpoints|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero.

### Task 5: Implement deterministic query, authenticated cursors, and FTS fallback

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCursorProtector.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantInspectionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CovenantMemoryEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantCursorProtectorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantSearchEndpointTests.cs`

**Interfaces:**

- Consumes: Plan 02 `ICovenantSearchIndex`, canonical list ordering, stable search row IDs, and canonical sequence snapshots.
- Consumes: Plan 03 Task 2 `ICovenantEnvelopeCodec` with purpose `Arcanum.Covenant.Cursor.v1` and current dataset, master version, envelope epoch, and boot identity.
- Produces: `CovenantCursorProtector.Protect`, `CovenantCursorProtector.Unprotect`, and public query integration over Plan 02's already-green compiler and fallback.

- [ ] **Step 1: Write failing cursor and public-integration tests, then retain the green Plan 02 search contract**

Add exact integration methods:

```csharp
[Fact]
public void Cursor_rejects_query_scope_dataset_sequence_epoch_and_purpose_changes()

[Fact]
public async Task Campaign_deletion_between_pages_returns_stale_cursor()

[Fact]
public async Task Degraded_fts_returns_success_with_fallback_health_truncation_and_rebuild_guidance()
```

- [ ] **Step 2: Run the red search tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSearchQueryCompilerTests|FullyQualifiedName~CovenantSearchIndexTests|FullyQualifiedName~CovenantCursorProtectorTests|FullyQualifiedName~CovenantSearchEndpointTests"
```

Expected: FAIL because authenticated cursor and public query integration do not exist. Plan 02 compiler tests remain green in this command.

- [ ] **Step 3: Implement the exact search contract**

Consume the already-green `CovenantSearchQueryCompiler.Compile` and `ICovenantSearchIndex.SearchAsync` implemented in Plan 02. Do not modify `CovenantSearchIndex`, add a second parser or normalization path, repeat ordering/fallback SQL, or own query-plan tests in this task. `CovenantInspectionService` maps the typed Plan 02 result and its exact final keyset tuple into the cursor fields. The endpoint passes one `CovenantInstallationReadLease` from `AcquireInstallationReadAsync` for an `AllScopes` query and one exact scoped `CovenantReadLease` from `AcquireReadAsync` for a Global or Campaign query. The service passes that lease as `ICovenantSnapshotReadLease` to the one Plan 02 search port, which rejects under-scoped coverage before SQL; no layer performs a nested acquisition. The red state in this task comes only from the absent cursor protector and HTTP query integration; the Plan 02 compiler/index tests included in the command remain green. Add an architecture assertion that this task delegates to the single Plan 02 compiler and search-index implementation.

Define closed cursor plaintexts for list, versions, FTS, and fallback. Bind endpoint kind, canonical filter digest, dataset generation, canonical and Campaign-deletion sequences, applied tuple, accelerator epoch, final keyset tuple, issued time, and expiry. Map authenticated source changes to `Covenant.StaleCursor`; map malformed, old-key, old-dataset, purpose, and authentication failures to `Covenant.InvalidCursor`.

Map `POST /api/memory/covenant/query` as `Covenant_Query`. Preserve typed successful degradation when FTS is missing, stale, corrupt, locked, or version-mismatched.

- [ ] **Step 4: Run the green search tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSearchQueryCompilerTests|FullyQualifiedName~CovenantSearchIndexTests|FullyQualifiedName~CovenantCursorProtectorTests|FullyQualifiedName~CovenantSearchEndpointTests"
```

Expected: PASS, with degraded FTS represented by a successful typed response.

- [ ] **Step 5: Refactor and verify bounded SQL**

Inspect every query for fixed internal identifiers and bound values. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSearch"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. API query text is never concatenated into SQL or FTS syntax.

### Task 6: Add operator prepare, set, and retire with receipt-first replay

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperatorMutationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CovenantMemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantOperatorMutationServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantMutationEndpointTests.cs`

**Interfaces:**

- Consumes: Plan 02 `ICovenantCompiler`, `ICovenantStore.ReadMutationEffectSnapshotAsync`, `CovenantMutationKernel`, mutation receipts, key epochs, and Campaign-registry epoch.
- Consumes: Plan 03 Task 2 `ICovenantEnvelopeCodec` purpose `Arcanum.Covenant.OperatorPreflight.v1`, clean `OperatorAuthorityContext`, and the operation-gate write lease.
- Produces: `CovenantOperatorMutationService` implementing Task 1 `ICovenantOperatorMutationService`.

- [ ] **Step 1: Write failing preflight and commit tests**

Add exact methods:

```csharp
[Fact]
public async Task Set_prepare_binds_every_commit_field_and_complete_effect_digest()

[Fact]
public async Task Global_prepare_streams_all_affected_campaigns_but_returns_at_most_50_examples()

[Fact]
public async Task Commit_replays_matching_receipt_before_expired_token_validation()

[Fact]
public async Task Same_mutation_id_with_changed_canonical_input_returns_idempotency_conflict()

[Fact]
public async Task Stale_revision_key_epoch_or_campaign_registry_epoch_changes_nothing()

[Fact]
public async Task Retire_reports_global_resurfacing_and_proposed_eligibility_before_commit()

[Fact]
public async Task Mutation_routes_never_enter_the_generic_idempotency_store()
```

- [ ] **Step 2: Run the red mutation tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantOperatorMutationServiceTests|FullyQualifiedName~CovenantMutationEndpointTests"
```

Expected: FAIL with 404 for `/api/memory/covenant/set/prepare` and no service registration.

- [ ] **Step 3: Implement preflight and commit orchestration**

The endpoint validates the content-free scope before acquisition. A Global effect scan that can include Campaign shadows or another all-scope effect calls `AcquireInstallationReadAsync` once; an exactly Campaign-scoped effect calls `AcquireReadAsync`. It passes the concrete lease as `ICovenantSnapshotReadLease` to prepare and transfers it with the result to the lease-bound response. Commit uses one exact caller-owned write lease. `PrepareSetAsync` and `PrepareRetireAsync` authenticate and canonicalize the complete future commit request, call `ReadMutationEffectSnapshotAsync` once, reject insufficient lease coverage before SQL, recheck all returned epochs, and issue a five-minute token. Neither endpoint nor service acquires a nested read lease. The store streams a Global scan in bounded memory and returns the exact count and digest with at most 50 examples. Bind authority epoch, dataset, request digest, expected revision, normalized-key dependency epoch, key-reclamation epoch, optional Campaign-registry epoch, compiled artifact hash, dependent-head vector digest, effect digest, issue time, and expiry.

`SetAsync` and `RetireAsync` begin with receipt replay:

```csharp
Result<CovenantMutationResultDto>? replay = await receipts.TryReplayAsync(
    request.MutationId,
    canonicalRequestDigest,
    cancellationToken);

if (replay is not null)
{
    return replay.Value;
}
```

For new work, decode and verify the token, revalidate the exact caller-owned `CovenantWriteLease` passed by the endpoint, open one immediate transaction, rerun O(1) revision and epoch checks against that lease snapshot, and call `CovenantMutationKernel`. Commit immutable version, head, quota state, outbox, and receipt atomically. Exact no-change set and repeated current tombstone return `NoChange` without a new version. `CovenantOperatorMutationService` never acquires a nested write lease and never completes or disposes the endpoint's lease. It only borrows and revalidates it; the lease-bound response remains its sole owner through JSON completion.

Map `POST /api/memory/covenant/set/prepare`, `POST /retire/prepare`, `PUT /api/memory/covenant`, and `POST /retire` with names `Covenant_SetPrepare`, `Covenant_RetirePrepare`, `Covenant_Set`, and `Covenant_Retire`. Require operator authority and the exact private no-cache headers. Return every success or error through `CovenantLeaseBoundJsonResult<T>`. Do not attach `IdempotencyEndpointFilters`.

- [ ] **Step 4: Run the green mutation tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantOperatorMutationServiceTests|FullyQualifiedName~CovenantMutationEndpointTests"
```

Expected: PASS, including replay after expiry or key rotation and conflict on changed input.

- [ ] **Step 5: Refactor and verify transaction authority**

Keep digest calculation and mutation semantics in Core or Plan 02 code, with Infrastructure responsible for transaction ownership. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantMutation|FullyQualifiedName~CovenantOperatorMutation"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. API handlers contain no SQL or CLI behavior.

### Task 7: Add Campaign path status and prepare/apply administration

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathIdentityAdministration.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICampaignPathMarkerLifecycle.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycleContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/TheForge/PhysicalCampaignRootOpener.CampaignPathTakeover.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICampaignPathStartupRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathStartupRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignPathMarkerIntentStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignPathOperationReceiptStore.cs`
- Create: `src/RetroDownfall.Arcanum.Api/TheForge/CampaignPathIdentityEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CampaignEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CampaignPathIdentityAdministrationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/CampaignPathIdentityEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/CampaignEndpointTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/CampaignPathStartupRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Task 1's exact Campaign-path DTOs, internal probe continuation, and `ICampaignPathIdentityAdministration` port; Task 2 lease-bound response and exact protected-header helper; Plan 03 Task 4's sealed concrete `PhysicalCampaignRootOpener`, producer-owned retained marker capabilities, indexed identity reader, and single shared `ICampaignPathMarkerCodec`; Plan 02 Campaign-scoped operation gate, `CovenantNoOpPostDispositionFinalizer.Instance`, and availability revision; and Plan 03 Task 2 `ICovenantEnvelopeCodec` purpose `Arcanum.Campaign.PathIdentity.v1`.
- Produces: the SQLCipher-backed marker-intent and completed-operation receipt stores, the exact Infrastructure `CampaignPathMarkerPhase` projection of Plan 01's schema codes, the internal `ICampaignPathMarkerLifecycle` that exclusively owns codec-based create, cleanup, restore-cleanup, and compare-delete work, pre-readiness `ICampaignPathStartupRecovery`, one implementation of Task 1's four-method administration port, and `MapCampaignPathIdentityEndpoints`.

- [ ] **Step 1: Write failing path-operation tests**

Add exact methods:

```csharp
[Fact]
public async Task Status_returns_only_content_free_state_revision_and_remediation()

[Theory]
[InlineData(CampaignPathIdentityOperation.Register)]
[InlineData(CampaignPathIdentityOperation.Update)]
[InlineData(CampaignPathIdentityOperation.RepairMoved)]
[InlineData(CampaignPathIdentityOperation.Deregister)]
[InlineData(CampaignPathIdentityOperation.TakeoverOrphan)]
public async Task Prepare_opens_the_target_and_binds_the_exact_effect(CampaignPathIdentityOperation operation)

[Fact]
public async Task Prepare_returns_current_and_prospective_identity_digests_and_typed_marker_effect()

[Fact]
public async Task Apply_replays_same_operation_and_digest_before_token_decode()

[Fact]
public async Task Terminal_apply_replay_never_acquires_or_drains_campaign_exclusive_authority()

[Fact]
public async Task New_or_active_apply_acquires_exclusive_only_after_probe_read_lease_is_released()

[Fact]
public async Task Active_intent_is_revalidated_under_exclusive_and_resumes_without_old_token_authority()

[Fact]
public async Task Restarted_active_intent_is_recovered_before_readiness_with_the_exact_persisted_owner()

[Fact]
public async Task Marker_recovery_wrong_operation_effect_kind_or_Campaign_scope_keeps_admission_closed()

[Fact]
public async Task Marker_recovery_for_deleted_Campaign_uses_the_historical_journal_scope()

[Fact]
public async Task One_restore_or_full_reset_owner_recovers_distinct_marker_intents_for_multiple_Campaigns()

[Fact]
public async Task Active_marker_intent_blocks_pools_workers_and_endpoints_until_reconciled()

[Fact]
public async Task Changed_digest_stale_path_revision_or_changed_physical_root_conflicts()

[Theory]
[InlineData(CampaignPathMarkerPhase.Prepared)]
[InlineData(CampaignPathMarkerPhase.TempCreated)]
[InlineData(CampaignPathMarkerPhase.TempWritten)]
[InlineData(CampaignPathMarkerPhase.TempFsynced)]
[InlineData(CampaignPathMarkerPhase.RenamedNoReplace)]
[InlineData(CampaignPathMarkerPhase.ParentFsynced)]
[InlineData(CampaignPathMarkerPhase.TargetReopenedOrAbsent)]
[InlineData(CampaignPathMarkerPhase.CodecOrAbsenceVerified)]
[InlineData(CampaignPathMarkerPhase.DatabaseStateCommitted)]
[InlineData(CampaignPathMarkerPhase.SensitiveMaterialDestroyed)]
[InlineData(CampaignPathMarkerPhase.ReopenPending)]
[InlineData(CampaignPathMarkerPhase.Completed)]
[InlineData(CampaignPathMarkerPhase.Compensated)]
[InlineData(CampaignPathMarkerPhase.ManualBlocker)]
[InlineData(CampaignPathMarkerPhase.OrphanReopenPending)]
[InlineData(CampaignPathMarkerPhase.Orphaned)]
public async Task Marker_protocol_restart_recovers_idempotently_from_every_crash_point(
    CampaignPathMarkerPhase phase)

[Fact]
public async Task Compensation_mismatch_leaves_marker_untouched_and_requires_recovery()

[Fact]
public async Task Recovery_adopts_only_the_same_verified_temporary_handle_and_exact_bytes()

[Fact]
public async Task Recovery_compare_deletes_only_the_same_verified_temporary_before_parent_fsync()

[Fact]
public async Task Mutated_or_wrong_root_temporary_remains_untouched_and_keeps_admission_closed()

[Fact]
public async Task Marker_intent_key_loss_leaves_every_file_untouched_and_reports_manual_recovery()

[Fact]
public async Task Repair_moved_reopens_both_roots_and_never_trusts_the_recorded_display_path()

[Fact]
public async Task Campaign_creation_persists_unresolved_registration_before_marker_work()

[Fact]
public async Task Campaign_creation_uses_shared_codec_and_holds_exclusive_transition_through_response()

[Fact]
public async Task Campaign_deletion_commits_owner_journal_and_marker_cleanup_intent_before_compare_delete()

[Theory]
[InlineData("root-unavailable")]
[InlineData("marker-identity-mismatch")]
[InlineData("marker-bytes-mismatch")]
public async Task Campaign_deletion_retains_visible_orphan_but_reopens_after_composite_finalizer(
    string blocker)

[Fact]
public async Task Campaign_deletion_parent_reaches_marker_cleanup_terminal_before_disposition_and_completed_only_in_finalizer()

[Fact]
public async Task Campaign_deletion_parent_CAS_requires_owner_cleanup_authorization_on_the_caller_transaction()

[Fact]
public async Task Campaign_deletion_unauthorized_parent_update_aborts_and_retains_child_parent_and_owner()

[Fact]
public async Task Campaign_deletion_finalizer_borrows_owner_cleanup_authorization_only_after_successful_disposition()

[Fact]
public async Task Campaign_deletion_failed_disposition_never_authorizes_or_invokes_the_finalizer()

[Fact]
public async Task Campaign_deletion_rejects_rollback_compensation_and_orphan_reclassification_from_every_unlisted_phase()

[Fact]
public async Task Marker_effect_stops_at_reopen_pending_until_matching_disposition_succeeds()

[Fact]
public async Task Marker_failed_disposition_or_finalizer_retains_exact_owner_and_blocks_readiness()

[Fact]
public async Task Restore_cleanup_prepares_every_child_in_the_staged_transaction_before_swap()

[Fact]
public void Cleanup_root_observation_union_and_blocker_codes_are_closed_and_literal()

[Fact]
public async Task Restore_cleanup_nonempty_seed_requires_an_opened_matching_root_authority()

[Fact]
public async Task Restore_cleanup_accepts_authenticated_empty_seed_vector_and_returns_literal_zero_receipt()

[Fact]
public async Task Restore_zero_vector_reconcile_returns_commit_and_noop_finalizer_without_marker_io()

[Fact]
public async Task Restore_zero_vector_owner_count_or_digest_mismatch_keeps_admission_closed()

[Fact]
public async Task Staged_restore_preparation_borrows_connection_and_transaction_without_committing_or_disposing()

[Fact]
public void Root_authority_has_no_visible_constructor_or_create_bypass()

[Fact]
public void Root_authority_factory_accepts_only_the_sealed_concrete_opener_and_producer_capability()

[Fact]
public async Task Root_authority_forwards_only_typed_marker_operations_through_retained_handles()

[Fact]
public async Task Confirmed_orphan_takeover_uses_the_sealed_proof_and_already_opened_conflicting_marker()

[Fact]
public async Task Ordinary_marker_open_cannot_adopt_a_mismatched_orphan()

[Fact]
public async Task Confirmed_orphan_takeover_failure_disposes_marker_then_root_exactly_once()

[Fact]
public async Task Confirmed_orphan_takeover_success_transfers_one_aggregate_owner_and_disposes_in_finally()

[Fact]
public async Task Root_authority_rejects_echoed_identity_mismatch_and_disposes_the_capability_once()

[Fact]
public void Root_authority_exports_no_raw_handle_interface_downcast_or_path_reopen_surface()

[Fact]
public async Task Root_authority_double_dispose_releases_retained_handles_exactly_once()
```

- [ ] **Step 2: Run the red path tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathIdentityAdministrationTests|FullyQualifiedName~CampaignPathIdentityEndpointTests|FullyQualifiedName~CampaignEndpointTests|FullyQualifiedName~CampaignPathStartupRecoveryTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because the status, prepare, and apply routes are not mapped.

- [ ] **Step 3: Implement typed status and receipt-first apply**

Implement Task 1's exact `ICampaignPathIdentityAdministration` port and wire DTOs. Do not define another interface, request, response, enum, probe arm, continuation, or `ArcanumJsonContext` entry in this task. The Task 1 status selector supplies one Campaign or explicit `AllCampaigns` with keyset pagination. Its closed probe is `Terminal(CampaignPathIdentityResultDto)` or `Continue(CampaignPathApplyContinuation)`, and the continuation remains limited to `New` or `ActiveIntent` with only operation identity and authenticated digests.

Prepare opens the target through the Plan 03 no-follow root policy and shared marker codec, then computes Task 1's current and prospective physical-identity digests, typed marker effect, marker conflicts, active-turn blockers, old-marker cleanup, normalized display path, and effect digest. It exposes no marker bytes or raw marker payload. Before any Campaign-exclusive acquisition or token decode, the apply endpoint acquires one short Campaign-scoped `CovenantReadLease` and calls `ProbeApplyAsync`. The probe reads the completed receipt and active marker intent in one snapshot. The same operation ID and digest returns the terminal recorded result or a same-process active-intent continuation; a changed digest conflicts immediately. A terminal result transfers the short read lease through protected JSON completion and never closes or drains Campaign admission. For `New`, the endpoint disposes that read lease, constructs `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.CampaignPathMutation, effectDigest)`, and calls `AcquireCampaignExclusiveAsync`. For a same-process `ActiveIntent`, it disposes the probe lease, reconstructs the same owner from the intent, and calls `ResumeCampaignExclusiveAsync`. A restarted host never reaches this endpoint with an unreconciled intent because `ICampaignPathStartupRecovery` runs before readiness. `ApplyAfterProbeAsync` rereads and revalidates the receipt or intent under that exact exclusive lease before any effect. It resumes an authenticated active intent without old token authority, or authenticates the token only for still-absent new work, drains Campaign turns and MCP uses, reopens the same identities, and runs the crash-recoverable marker protocol using that same codec for every encode, parse, and exact-byte check. It never holds the probe read lease while waiting for exclusive drain, and a wrong operation ID or effect digest cannot adopt a kept-closed marker transition.

Expose one internal `ICampaignPathMarkerLifecycle` from this same implementation for Campaign creation/deletion, Task 15 restore cleanup, and Task 17 full-reset cleanup. It accepts typed Campaign/operation identities and producer-owned no-follow authority, never a caller-encoded marker payload. `CampaignPathRestoreCleanupSeed` is a nonserializable Infrastructure value containing the historical Campaign ID, positive prior path revision, exact indexed identity evidence, and one closed root observation. `Opened` carries the live `CampaignPathMarkerRootAuthority`. `Unavailable` and `Mismatch` carry only authenticated durable inventory evidence, an observation digest, and the closed blocker code. They carry no path or filesystem capability. `CampaignPathRestoreCleanupPreparation` contains the exact global `BackupRestore` owner and a bounded nondefault canonically ordered seed vector that may be empty only for the authenticated zero-marker inventory. `CampaignPathRestoreCleanupPreparationReceipt` contains only the owner, ordered random child intent IDs, count, and canonical intent-vector digest.

`CampaignPathMarkerGateReconcileRequest` contains the exact owner, a bounded ordered intent-ID vector, and its expected vector digest. `CampaignPathMarkerGateCompletion` is nonserializable and contains the content-free aggregate outcome, exact `CommitAndReopen` or `RollbackAndReopen` disposition, and one Plan 02 `ICovenantExclusivePostDispositionFinalizer`; it never owns or disposes the lease. `FullInstallationResetMarkerCleanupAuthority` is nonserializable and can be created only after Task 17 proves the caller-held installation lock plus the authenticated reset journal, signed operation identity, owner-effect digest, and Campaign inventory digest. `CampaignPathFullResetCleanupPreparation` contains that authority and a bounded ordered seed vector. `CampaignPathFullResetCleanupReceipt` contains the owner, ordered random child intent IDs, count, vector digest, and content-free blocker counts. Freeze the complete cross-task seam as:

```csharp
internal enum CampaignPathMarkerPhase : byte
{
    Prepared = 1,
    TempCreated = 2,
    TempWritten = 3,
    TempFsynced = 4,
    RenamedNoReplace = 5,
    ParentFsynced = 6,
    TargetReopenedOrAbsent = 7,
    CodecOrAbsenceVerified = 8,
    DatabaseStateCommitted = 9,
    SensitiveMaterialDestroyed = 10,
    ReopenPending = 11,
    Completed = 12,
    Compensated = 13,
    ManualBlocker = 14,
    OrphanReopenPending = 15,
    Orphaned = 16
}

public sealed partial class PhysicalCampaignRootOpener
{
    internal sealed class ConfirmedOrphanTakeoverOpen : IAsyncDisposable
    {
        private int ownershipState;
        private MarkerRootCapability? root;
        private MarkerHandleCapability? marker;

        private ConfirmedOrphanTakeoverOpen(
            MarkerRootCapability root,
            MarkerHandleCapability marker)
        {
            this.root = root;
            this.marker = marker;
        }

        internal Guid CampaignId => GetRoot().CampaignId;

        internal long PathRevision => GetRoot().PathRevision;

        internal CovenantDigest RootPhysicalIdentityDigest =>
            GetRoot().PhysicalIdentityDigest;

        internal CovenantDigest MarkerPhysicalIdentityDigest =>
            GetMarker().PhysicalIdentityDigest;

        internal MarkerRootCapability Transfer(
            out MarkerHandleCapability transferredMarker)
        {
            if (Interlocked.Exchange(ref ownershipState, 1) != 0)
            {
                throw new InvalidOperationException(
                    "Confirmed orphan takeover capability was already consumed.");
            }

            MarkerRootCapability transferredRoot = root ??
                throw new InvalidOperationException(
                    "Confirmed orphan takeover capability was already transferred.");

            transferredMarker = marker ??
                throw new InvalidOperationException(
                    "Confirmed orphan marker capability was already transferred.");

            root = null;
            marker = null;

            return transferredRoot;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref ownershipState, 1) != 0)
            {
                return;
            }

            MarkerHandleCapability? ownedMarker = marker;
            MarkerRootCapability? ownedRoot = root;
            marker = null;
            root = null;

            if (ownedMarker is not null)
            {
                await ownedMarker.DisposeAsync();
            }

            if (ownedRoot is not null)
            {
                await ownedRoot.DisposeAsync();
            }
        }

        private MarkerRootCapability GetRoot() =>
            Volatile.Read(ref root) ??
            throw new ObjectDisposedException(
                nameof(ConfirmedOrphanTakeoverOpen));

        private MarkerHandleCapability GetMarker() =>
            Volatile.Read(ref marker) ??
            throw new ObjectDisposedException(
                nameof(ConfirmedOrphanTakeoverOpen));
    }
}

internal sealed partial class CampaignPathIdentityAdministration
{
    internal sealed class ConfirmedOrphanTakeoverAuthority
    {
        private ConfirmedOrphanTakeoverAuthority(
            Guid operationId,
            Guid campaignId,
            long pathRevision,
            CovenantDigest applyRequestDigest,
            CovenantDigest confirmedEffectDigest,
            CovenantDigest expectedRootPhysicalIdentityDigest,
            CovenantDigest expectedMarkerPhysicalIdentityDigest,
            CovenantDigest expectedMarkerBytesDigest,
            string canonicalDisplayPath)
        {
            OperationId = operationId;
            CampaignId = campaignId;
            PathRevision = pathRevision;
            ApplyRequestDigest = applyRequestDigest;
            ConfirmedEffectDigest = confirmedEffectDigest;
            ExpectedRootPhysicalIdentityDigest = expectedRootPhysicalIdentityDigest;
            ExpectedMarkerPhysicalIdentityDigest = expectedMarkerPhysicalIdentityDigest;
            ExpectedMarkerBytesDigest = expectedMarkerBytesDigest;
            CanonicalDisplayPath = canonicalDisplayPath;
        }

        internal Guid OperationId { get; }

        internal Guid CampaignId { get; }

        internal long PathRevision { get; }

        internal CovenantDigest ApplyRequestDigest { get; }

        internal CovenantDigest ConfirmedEffectDigest { get; }

        internal CovenantDigest ExpectedRootPhysicalIdentityDigest { get; }

        internal CovenantDigest ExpectedMarkerPhysicalIdentityDigest { get; }

        internal CovenantDigest ExpectedMarkerBytesDigest { get; }

        internal string CanonicalDisplayPath { get; }
    }
}

internal sealed class CampaignPathMarkerRootAuthority : IAsyncDisposable
{
    private PhysicalCampaignRootOpener.MarkerRootCapability? retainedCapability;

    private CampaignPathMarkerRootAuthority(
        Guid campaignId,
        long pathRevision,
        CovenantDigest physicalIdentityDigest,
        PhysicalCampaignRootOpener.MarkerRootCapability retainedCapability)
    {
        CampaignId = campaignId;
        PathRevision = pathRevision;
        PhysicalIdentityDigest = physicalIdentityDigest;
        this.retainedCapability = retainedCapability;
    }

    public Guid CampaignId { get; }

    public long PathRevision { get; }

    public CovenantDigest PhysicalIdentityDigest { get; }

    internal static ICampaignPathMarkerRootAuthorityFactory Instance { get; } =
        new Factory();

    internal ValueTask<Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability>>
        CreateTemporaryExclusiveNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken) =>
        GetRetainedCapability().CreateTemporaryExclusiveNoFollowAsync(
            temporaryLeaf,
            cancellationToken);

    internal ValueTask<Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability>>
        OpenTemporaryNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken) =>
        GetRetainedCapability().OpenTemporaryNoFollowAsync(
            temporaryLeaf,
            cancellationToken);

    internal ValueTask<Result> RenameTemporaryToMarkerNoReplaceAsync(
        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary,
        CovenantDigest expectedTemporaryPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            expectedTemporaryPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    internal ValueTask<Result> CompareDeleteTemporaryAsync(
        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary,
        CovenantDigest expectedTemporaryPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().CompareDeleteTemporaryAsync(
            temporary,
            expectedTemporaryPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    internal ValueTask<Result<PhysicalCampaignMarkerOpenResult>>
        OpenMarkerOrProveAbsentNoFollowAsync(
            CancellationToken cancellationToken) =>
        GetRetainedCapability().OpenMarkerOrProveAbsentNoFollowAsync(
            cancellationToken);

    internal ValueTask<Result> CompareDeleteMarkerAsync(
        PhysicalCampaignRootOpener.MarkerHandleCapability marker,
        CovenantDigest expectedMarkerPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().CompareDeleteMarkerAsync(
            marker,
            expectedMarkerPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    internal ValueTask<Result> RenameMarkerToQuarantineNoReplaceAsync(
        PhysicalCampaignRootOpener.MarkerHandleCapability marker,
        string quarantineLeaf,
        CovenantDigest expectedMarkerPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().RenameMarkerToQuarantineNoReplaceAsync(
            marker,
            quarantineLeaf,
            expectedMarkerPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    internal ValueTask<Result> FlushMarkerDirectoryAsync(
        CancellationToken cancellationToken) =>
        GetRetainedCapability().FlushMarkerDirectoryAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        PhysicalCampaignRootOpener.MarkerRootCapability? capability =
            Interlocked.Exchange(
                ref retainedCapability,
                null);

        if (capability is not null)
        {
            await capability.DisposeAsync();
        }
    }

    private PhysicalCampaignRootOpener.MarkerRootCapability
        GetRetainedCapability() =>
        Volatile.Read(ref retainedCapability) ??
        throw new ObjectDisposedException(nameof(CampaignPathMarkerRootAuthority));

    private sealed class Factory : ICampaignPathMarkerRootAuthorityFactory
    {
        public async ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenAsync(
            PhysicalCampaignRootOpener opener,
            Guid campaignId,
            long pathRevision,
            CovenantDigest expectedPhysicalIdentityDigest,
            string canonicalDisplayPath,
            CancellationToken cancellationToken)
        {
            if (campaignId == Guid.Empty ||
                pathRevision <= 0 ||
                expectedPhysicalIdentityDigest == default ||
                string.IsNullOrWhiteSpace(canonicalDisplayPath))
            {
                return new Error(
                    ErrorCodes.Campaign.InvalidPath,
                    "Invalid Campaign root authority request.");
            }

            Result<PhysicalCampaignRootOpener.MarkerRootCapability> opened =
                await opener.OpenForMarkerLifecycleAsync(
                    campaignId,
                    pathRevision,
                    expectedPhysicalIdentityDigest,
                    canonicalDisplayPath,
                    cancellationToken);

            if (!opened.IsSuccess)
            {
                return opened.Error;
            }

            PhysicalCampaignRootOpener.MarkerRootCapability capability =
                opened.Value;

            if (capability.CampaignId != campaignId ||
                capability.PathRevision != pathRevision ||
                capability.PhysicalIdentityDigest != expectedPhysicalIdentityDigest)
            {
                await capability.DisposeAsync();

                return new Error(
                    ErrorCodes.Campaign.InvalidPath,
                    "Campaign root authority identity mismatch.");
            }

            return new CampaignPathMarkerRootAuthority(
                campaignId,
                pathRevision,
                expectedPhysicalIdentityDigest,
                capability);
        }

        public async ValueTask<Result<CampaignPathConfirmedOrphanTakeoverOpen>>
            OpenConfirmedOrphanTakeoverAsync(
                PhysicalCampaignRootOpener opener,
                CampaignPathIdentityAdministration.ConfirmedOrphanTakeoverAuthority authority,
                CancellationToken cancellationToken)
        {
            Result<PhysicalCampaignRootOpener.ConfirmedOrphanTakeoverOpen> opened =
                await opener.OpenForConfirmedOrphanTakeoverAsync(
                    authority,
                    cancellationToken);

            if (!opened.IsSuccess)
            {
                return opened.Error;
            }

            PhysicalCampaignRootOpener.ConfirmedOrphanTakeoverOpen capability =
                opened.Value;

            await using (capability)
            {
                if (capability.CampaignId != authority.CampaignId ||
                    capability.PathRevision != authority.PathRevision ||
                    capability.RootPhysicalIdentityDigest !=
                        authority.ExpectedRootPhysicalIdentityDigest ||
                    capability.MarkerPhysicalIdentityDigest !=
                        authority.ExpectedMarkerPhysicalIdentityDigest)
                {
                    return new Error(
                        ErrorCodes.Campaign.InvalidPath,
                        "Confirmed orphan takeover identity mismatch.");
                }

                PhysicalCampaignRootOpener.MarkerRootCapability root =
                    capability.Transfer(
                        out PhysicalCampaignRootOpener.MarkerHandleCapability marker);

                CampaignPathMarkerRootAuthority rootAuthority = new(
                    authority.CampaignId,
                    authority.PathRevision,
                    authority.ExpectedRootPhysicalIdentityDigest,
                    root);

                return new CampaignPathConfirmedOrphanTakeoverOpen(
                    rootAuthority,
                    marker);
            }
        }
    }
}

internal sealed class CampaignPathConfirmedOrphanTakeoverOpen : IAsyncDisposable
{
    private int ownershipState;
    private CampaignPathMarkerRootAuthority? rootAuthority;
    private PhysicalCampaignRootOpener.MarkerHandleCapability? marker;

    internal CampaignPathConfirmedOrphanTakeoverOpen(
        CampaignPathMarkerRootAuthority rootAuthority,
        PhysicalCampaignRootOpener.MarkerHandleCapability marker)
    {
        this.rootAuthority = rootAuthority;
        this.marker = marker;
    }

    internal CampaignPathMarkerRootAuthority RootAuthority =>
        Volatile.Read(ref rootAuthority) ??
        throw new ObjectDisposedException(
            nameof(CampaignPathConfirmedOrphanTakeoverOpen));

    internal PhysicalCampaignRootOpener.MarkerHandleCapability Marker =>
        Volatile.Read(ref marker) ??
        throw new ObjectDisposedException(
            nameof(CampaignPathConfirmedOrphanTakeoverOpen));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref ownershipState, 1) != 0)
        {
            return;
        }

        PhysicalCampaignRootOpener.MarkerHandleCapability? ownedMarker = marker;
        CampaignPathMarkerRootAuthority? ownedRoot = rootAuthority;
        marker = null;
        rootAuthority = null;

        if (ownedMarker is not null)
        {
            await ownedMarker.DisposeAsync();
        }

        if (ownedRoot is not null)
        {
            await ownedRoot.DisposeAsync();
        }
    }
}

internal interface ICampaignPathMarkerRootAuthorityFactory
{
    ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenAsync(
        PhysicalCampaignRootOpener opener,
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        CancellationToken cancellationToken);

    ValueTask<Result<CampaignPathConfirmedOrphanTakeoverOpen>>
        OpenConfirmedOrphanTakeoverAsync(
            PhysicalCampaignRootOpener opener,
            CampaignPathIdentityAdministration.ConfirmedOrphanTakeoverAuthority authority,
            CancellationToken cancellationToken);
}

internal sealed record CampaignPathRestoreCleanupSeed(
    Guid CampaignId,
    long PriorPathRevision,
    CovenantDigest IndexedIdentityDigest,
    CampaignPathCleanupRootObservation RootObservation);

internal enum CampaignPathCleanupRootBlocker : byte
{
    RootUnavailable = 1,
    PhysicalIdentityMismatch = 2,
    OwnershipMismatch = 3
}

internal sealed record CampaignPathCleanupRootBlockerEvidence(
    CampaignPathCleanupRootBlocker Blocker,
    CovenantDigest AuthenticatedInventoryEntryDigest,
    CovenantDigest ObservationDigest);

internal abstract record CampaignPathCleanupRootObservation
{
    private CampaignPathCleanupRootObservation()
    {
    }

    internal sealed record Opened(
        CampaignPathMarkerRootAuthority RootAuthority)
        : CampaignPathCleanupRootObservation;

    internal sealed record Unavailable(
        CampaignPathCleanupRootBlockerEvidence Evidence)
        : CampaignPathCleanupRootObservation;

    internal sealed record Mismatch(
        CampaignPathCleanupRootBlockerEvidence Evidence)
        : CampaignPathCleanupRootObservation;
}

internal sealed record CampaignPathRestoreCleanupPreparation(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<CampaignPathRestoreCleanupSeed> OrderedSeeds);

internal sealed record CampaignPathRestoreCleanupPreparationReceipt(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<Guid> OrderedIntentIds,
    ulong IntentCount,
    CovenantDigest IntentVectorDigest);

internal static class CampaignPathRestoreCleanupIntentVector
{
    internal const string Domain =
        "Arcanum.Covenant.BackupRestore.MarkerIntentVector.v1";

    internal const string EmptyDigestHex =
        "4da7d983be83d8a8f9c50e927319a6297f451d9c7366ce0df0506882fd1fee64";
}

internal sealed record CampaignPathMarkerGateReconcileRequest(
    CovenantExclusiveRecoveryOwner Owner,
    ImmutableArray<Guid> OrderedIntentIds,
    CovenantDigest IntentVectorDigest);

internal enum CampaignPathMarkerAggregateOutcome : byte
{
    Committed = 1,
    Compensated = 2,
    Orphaned = 3
}

internal sealed record CampaignPathMarkerGateCompletion(
    CampaignPathMarkerAggregateOutcome Outcome,
    CovenantExclusiveLeaseDisposition Disposition,
    ICovenantExclusivePostDispositionFinalizer Finalizer);

internal partial interface ICampaignPathMarkerLifecycle
{

    Task<Result<CampaignPathRestoreCleanupPreparationReceipt>> PrepareRestoreCleanupInStagedDatabaseAsync(
        CampaignPathRestoreCleanupPreparation preparation,
        CovenantInitializedConnectionLease stagedConnection,
        SqliteTransaction stagedTransaction,
        CancellationToken cancellationToken);

    Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
        CampaignPathMarkerGateReconcileRequest request,
        ICovenantExclusiveOperationLease exclusiveLease,
        CancellationToken cancellationToken);

}
```

All vectors are nondefault, bounded, immutable, canonically ordered, and deep-copied. Every count is checked and must equal the corresponding vector. Each digest is exactly 32 bytes. `CampaignPathMarkerRootAuthority.Factory` is the sole sealed `ICampaignPathMarkerRootAuthorityFactory` implementation and is private to the authority class, so only that nested type can call the private root-authority constructor. It validates the nonempty Campaign, positive revision, exact digest, canonical display path, and same-handle physical identity through Plan 03's no-follow opener before transferring the live root and `.arcanum` handles. An `Opened` seed's Campaign ID, prior revision, and indexed identity digest must equal its root authority. An `Unavailable` seed permits only `RootUnavailable`; a `Mismatch` seed permits only `PhysicalIdentityMismatch` or `OwnershipMismatch`. Both blocker arms require the exact authenticated inventory-entry digest and durable observation digest and contain no marker effect authority. Restore requires `Opened` for every nonempty destination seed. Full installation reset accepts every arm and journals exactly one child for every Campaign in its authenticated inventory, including unavailable and mismatched roots. Only `Opened` can reach a marker filesystem operation. `DisposeAsync` uses `Interlocked.Exchange` to detach and release the retained capability set exactly once. The implementation rejects null or already-transferred handle ownership. No visible constructor, `Create` method, alternate factory, parameterless construction, or DI registration exists, and none of these types is registered in a JSON context.

The restore intent-vector digest preimage is ASCII `Arcanum.Covenant.BackupRestore.MarkerIntentVector.v1`, one `0x00` separator, the checked item count as `UInt64BE`, then the existing canonical ordered child encoding. The canonical empty vector is exactly count zero with no child bytes and digest `4da7d983be83d8a8f9c50e927319a6297f451d9c7366ce0df0506882fd1fee64`. A zero-seed preparation receipt must contain the authenticated global `BackupRestore` owner, `ImmutableArray<Guid>.Empty`, `IntentCount=0`, and that literal digest. No other empty array, count, owner, digest, or default-vector combination is valid.

`CampaignPathIdentityAdministration.ConfirmedOrphanTakeoverAuthority` has its only constructor nested and private. The enclosing administration implementation creates it only in `ApplyAfterProbeAsync`, after it authenticates the apply token, revalidates the durable orphan row and exact marker observation under the matching Campaign-exclusive owner, proves no active Campaign owns the prepared root, and proves the confirmed operation, apply-request, and effect digests. `PhysicalCampaignRootOpener.CampaignPathTakeover.cs` adds the sole partial producer method named by Plan 03 and the closed `ConfirmedOrphanTakeoverOpen` result. It opens the root and conflicting marker once, returns retained capabilities, and never exports or later reopens the display path. The authority factory verifies the root and marker identity echoes and disposes the retained capability set exactly once on any mismatch. Architecture tests pin one constructor call site, one opener call site, no raw evidence overload, and no direct use of `CanonicalDisplayPath` outside that sealed partial opener.

The preparation method asserts that the live transaction belongs to the supplied initialized connection and borrows both without beginning, committing, rolling back, or disposing either. For a nonempty vector it borrows Plan 01's `CampaignPathMarkerIntentMutation` authorization only on that transaction connection, uses the shared codec and each `Opened` root authority to build the exact encrypted payload and evidence, and inserts every `RestoreCleanup` child into the staged core database atomically. The receipt digest covers the exact ordered rows. For the authenticated empty vector it returns the literal zero receipt without opening marker storage, borrowing marker-mutation authorization, calling the codec, or touching the filesystem.

`ReconcileGateOwnedAsync` accepts kinds 1 through 3 only, validates that the borrowed Campaign or global lease carries the same owner and scope, and processes every child through the shared codec and retained root authority. The exact authenticated zero request is global `BackupRestore`, empty ordered IDs, count zero, the literal empty digest, and its matching resumed exclusive lease. It returns `CampaignPathMarkerAggregateOutcome.Committed`, `CovenantExclusiveLeaseDisposition.CommitAndReopen`, and `CovenantNoOpPostDispositionFinalizer.Instance` without opening the marker store, calling the codec, borrowing mutation authorization, or touching the filesystem. Any zero-arm owner, scope, lease, count, vector-defaultness, or digest mismatch keeps admission closed. PathMutation and nonempty RestoreCleanup return their aggregate pending disposition plus finalizer only after all rows reach the matching `ReopenPending`. CampaignDelete normally does the same. An unavailable, mismatched, or no-longer-owned deletion target instead reaches `OrphanReopenPending(CommitAndReopen)` after sensitive intent material is destroyed. Before returning either CampaignDelete completion, the coordinator CASes the matching parent `OwnerDeleted -> MarkerCleanupTerminal`. Its composite finalizer advances the child to `Completed` or `Orphaned` and the parent `MarkerCleanupTerminal -> Completed` only after disposition succeeds. The caller invokes the disposition and finalizer. Failed disposition or finalizer retains the corresponding pending child and `MarkerCleanupTerminal` parent. Task 17 later extends this internal contract and its implementation with the exact shutdown-only kind-4 methods after its authenticated reset-journal types exist. No later task parses, encodes, compares, deletes, exports, or reimports a Campaign marker or intent row directly.

Every insert or CAS of `owner_deletion_operation_intents`, including `OwnerDeleted -> MarkerCleanupTerminal` and the composite-finalizer transition to `Completed`, uses the caller-owned transaction and borrows Plan 01's exact false-by-default `OwnerCleanup` authorization so `arcanum_owner_cleanup_authorized()` is true only on that transaction connection for the bounded statement. Direct SQL, a different connection, an expired authorization scope, and every unauthorized update abort. The response boundary invokes the composite finalizer only after successful `CommitAndReopen`. A failed disposition never borrows finalizer authorization and leaves both the pending child and `MarkerCleanupTerminal` parent unchanged. A finalizer transaction or authorization failure rolls back both child and parent transitions and retains the exact owner for recovery.

Implement the marker change with Plan 01's exact kind, phase, revision, and authorization contract. Phase one creates one random nonempty `IntentId` for the exact Campaign and commits it with the immutable `OwnerOperationId`, Campaign ID, encrypted exact bounded codec payload, marker digest, random same-directory temporary basename, target display path, prior path revision, exactly shaped optional `CovenantExclusiveOperation`, stable owner effect digest, and nullable apply-request digest before any filesystem effect. The apply-request digest is required only for `PathMutation`; it is null for `CampaignDelete`, `RestoreCleanup`, and `FullInstallationResetCleanup`. The store's unique owner-operation, Campaign, and kind tuple returns the existing intent on exact replay. It allows one restore or full-reset owner to hold multiple rows, one per Campaign, without operation-key collision. `PathMutation`, `CampaignDelete`, and `RestoreCleanup` carry the complete durable gate recovery owner and Campaign scope. `FullInstallationResetCleanup` carries no in-process gate operation and is owned only by Task 17's stopped-host lock and authenticated reset journal. Every insert and phase CAS borrows the exact Plan 01 marker-intent mutation authorization on the live transaction connection.

The filesystem phase follows the exact `Prepared`, `TempCreated`, `TempWritten`, `TempFsynced`, `RenamedNoReplace`, `ParentFsynced`, `TargetReopenedOrAbsent`, `CodecOrAbsenceVerified`, `DatabaseStateCommitted`, and `SensitiveMaterialDestroyed` sequence applicable to its kind. Each filesystem syscall is reached only through the seed's retained root authority and Plan 03's typed root, temporary, marker, and zeroizable-bytes capabilities. Recovery of `TempCreated`, `TempWritten`, or `TempFsynced` opens only the journaled temporary leaf relative to that retained root, reads its checked identity and exact bounded bytes into a `MarkerCodecBytesLease`, verifies both against the intent, and disposes the lease in `finally`. Rename receives the verified temporary capability plus the expected identity and exact lease bytes. Proven abort or compensation compare-deletes that same temporary capability with the same evidence, then flushes the parent before its phase CAS. Mutated, preexisting, wrong-root, or unverifiable temporary files remain untouched. No phase calls the opener again, reopens a display path, uses a raw handle, or deletes a temporary by leaf alone. The `SensitiveMaterialDestroyed` CAS securely clears the encrypted marker payload and temporary-name capability once while retaining immutable digests and the complete recovery owner. PathMutation and RestoreCleanup persist `ReopenPending` plus exact `CommitAndReopen` for a successful effect or `RollbackAndReopen` for a proven same-handle compensation. CampaignDelete persists only `ReopenPending(CommitAndReopen)` after exact cleanup or proven absence and can never reach `Compensated`. Uncertainty remains at the last proven pre-pending phase and calls `KeepClosed`; a genuinely nonfinalizable manual blocker advances to `ManualBlocker`. Only the one-shot marker-journal `ICovenantExclusivePostDispositionFinalizer` advances `ReopenPending -> Completed` after successful `CommitAndReopen` or, for the two eligible kinds only, `ReopenPending -> Compensated` after successful `RollbackAndReopen`. CampaignDelete alone may securely scrub only `Prepared` or `TargetReopenedOrAbsent` into `OrphanReopenPending(CommitAndReopen)` when the workspace marker is unavailable, mismatched, or no longer owned. ReopenPending and every other source cannot reclassify. Before disposition, either deletion arm advances the parent to `MarkerCleanupTerminal` under the exact owner-cleanup authorization above. Its composite finalizer advances the child to `Completed` or `Orphaned` and the parent to `Completed` after disposition succeeds under a new caller-owned authorized transaction. The orphan row remains visible and cannot authorize file deletion or block the deleted Campaign scope. Failed disposition, finalizer failure, uncertainty, and `ManualBlocker` retain the content-free row and owner for adoption. Kind 4 has no gate disposition. Under Task 17's exact shutdown-only authority it advances `SensitiveMaterialDestroyed -> Completed`, or records `ManualBlocker`, and copies its aggregate terminal outcome into the authenticated reset journal before database removal. Terminal retention may delete only the already-scrubbed row after the applicable parent receipt or journal is terminal; `Orphaned` is retained until an explicit authenticated takeover consumes the exact evidence. Only the single-Campaign `PathMutation` arm also writes `campaign_path_operation_receipts`. Campaign deletion records its replayable terminal outcome in `owner_deletion_operation_intents`; that parent remains at `MarkerCleanupTerminal` until marker disposition and composite finalization succeed. Restore and full-reset cleanup remain children of their owning recovery journals. Cleanup, takeover, and deregistration use the same codec and retained-handle identity rules.

Freeze the pre-readiness seam as `ICampaignPathStartupRecovery.RecoverBeforeReadinessAsync(ArcanumMaintenanceLock heldInstallationLock, CancellationToken cancellationToken) -> Task<Result<CampaignPathStartupRecoveryOutcome>>`, where the closed outcome is `NoActiveIntent=1`, `RecoveredReady=2`, or `KeptClosed=3`. The host and direct CLI bootstrap own the one installation lock; recovery asserts and borrows it without reacquisition or disposal. It runs after Plan 03 host-tools/catalog precheck and the Plan 01 core install, before shared pools, optional services, workers, endpoints, or readiness. It enumerates bounded active intents only of kind `PathMutation` or `CampaignDelete`, reconstructs each exact Campaign owner, and calls `ResumeCampaignExclusiveAsync`, including the historical-Campaign arm only when a committed deletion journal proves that scope. It delegates every filesystem action to `ICampaignPathMarkerLifecycle`. A proven result reaches matching `ReopenPending` or CampaignDelete-only `OrphanReopenPending`; recovery then invokes the matching disposition once and calls the ordinary or composite marker-journal finalizer only after success. Uncertainty calls `KeepClosed` from the last proven earlier phase. A disposition or finalizer failure leaves the owner row active and readiness closed. A `RestoreCleanup` intent is adopted only by Task 15 under the one resumed global `BackupRestore` lease. A `FullInstallationResetCleanup` intent is adopted only by Task 17 under its stopped-host lock and authenticated reset journal and never enters a gate disposition. Either cleanup kind encountered without its matching active parent journal returns `KeptClosed`; this service never calls `ResumeCampaignExclusiveAsync` for it. Recovery never calls initial acquisition for a persisted intent and never reconstructs an effect digest from token, path, or catalog state.

Compensation may compare-delete only the marker or temporary file owned by the same operation when both the retained opened-handle identity and exact codec bytes match the committed intent. A path, handle identity, digest, byte, owner, mode or ACL, link-count, or operation mismatch remains untouched and becomes a typed recoverable or manual-remediation state. CampaignDelete maps that condition to its durable visible-orphan arm; other kinds retain their approved blocker policy. Startup recovery is idempotent at every exact Plan 01 phase from `Prepared` through `Orphaned`. Separate fault cases crash after each filesystem syscall and before its corresponding phase CAS, plus before and after exclusive disposition and the post-disposition finalizer. Each case proves completion, safe retry, retained durable owner, unchanged manual blocker, or a finalized visible orphan. Loss of the key needed to decrypt an incomplete intent never falls back to a path, digest, or reconstructed payload and never mutates a marker. `RepairMoved` creates both old-root and proposed-root authorities through the private sealed factory before its first effect, verifies their Campaign ID, revision, and physical identity echoes, and retains both through completion. Every later compare, create, cleanup, and durability operation forwards through those two authorities without trusting or reopening either display path.

The endpoint validates the status selector before acquisition. `AllCampaigns` status uses exactly one `CovenantInstallationReadLease` from `AcquireInstallationReadAsync`; one-Campaign status and prepare use an exactly matching `CovenantReadLease` from `AcquireReadAsync`. Both pass the lease as `ICovenantSnapshotReadLease` and transfer it through response serialization. Apply follows the receipt/intent probe split above. New or active work uses its Campaign-exclusive lease for the effect and committed availability transition, persists `ReopenPending` plus the selected disposition, and returns Task 1's exclusive carrier with the exact marker-journal finalizer. Task 2's response boundary calls the disposition only after serialization, calls the finalizer only after disposition success, and then disposes once. A proven pre-marker abort selects `RollbackAndReopen`; an uncertain or postcommit failure selects `KeepClosed`. Failed disposition, finalizer failure, and `KeepClosed` retain the durable owner. The protected result retains no obsolete filesystem capability.

Map `POST /api/campaigns/path/status`, `POST /api/campaigns/{id}/path/prepare`, and `POST /api/campaigns/{id}/path/apply` as `Campaign_PathStatus`, `Campaign_PathPrepare`, and `Campaign_PathApply`. Require operator authority and the exact private no-cache headers. Status and prepare use lease-bound JSON. Apply returns its approved content-free result only after the Campaign-exclusive transition and health publication complete, so response serialization retains no marker payload, temp-name capability, or filesystem handle.

Change `RegisterCampaign` so it commits the Campaign and unresolved path-registration state first, then invokes the same intent protocol through the shared codec. A filesystem failure preserves the unresolved Campaign. The route requires operator metadata, creates a nonempty operation ID and stable marker effect digest, acquires with `CovenantExclusiveOperation.CampaignPathMutation`, owns that Campaign-exclusive lease through committed path-health publication, and transfers the lease, typed disposition, and exact marker-journal `ICovenantExclusivePostDispositionFinalizer` in `CovenantExclusiveLeasedServiceResult<CampaignDto>` through protected serialization after every filesystem capability has been disposed. The response boundary uses a recovery-owned token, invokes the finalizer only after successful disposition, then disposes once. Failed disposition or finalizer retains the marker intent and owner.

Change `DeleteCampaign` so its request carries one nonempty operation ID and Core-computed deletion effect digest and acquires through `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.CampaignDelete, effectDigest)`. Its Campaign-exclusive transition drains matching turns and MCP uses. In the Campaign deletion transaction it borrows Plan 01's exact `OwnerCleanup` authorization on that caller-owned transaction, inserts the core `owner_deletion_operation_intents` row with that exact owner before the DELETE, lets the core trigger observe `arcanum_owner_cleanup_authorized()` on the same connection, copy the operation ID and effect digest into the monotonic owner event, and advance the intent to `OwnerDeleted`, then commits a durable marker cleanup intent before any filesystem effect. The cleanup intent carries the same owner and historical Campaign scope. Recovery reconstructs the owner only from those immutable core rows and calls `ResumeCampaignExclusiveAsync`; it never infers a digest from the deleted Campaign, marker path, or request token. Optional Covenant-family damage cannot block the core owner journal. Exact marker cleanup advances the child to `ReopenPending`; an unavailable or mismatched marker advances it to `OrphanReopenPending` without touching the file. Before returning either pending completion, the coordinator uses a caller-owned transaction with the same bounded authorization to CAS the parent `OwnerDeleted -> MarkerCleanupTerminal`. The composite finalizer starts a new caller-owned transaction and borrows the authorization only after successful `CommitAndReopen`, then advances the child respectively to `Completed` or `Orphaned` and the parent `MarkerCleanupTerminal -> Completed` atomically. Failed disposition never invokes the finalizer. Failed disposition, authorization, CAS, or finalizer retains the pending child and `MarkerCleanupTerminal` parent. The `Orphaned` row stays visible for explicit takeover but no longer closes the historical Campaign scope. The route returns an approved content-free result only after Campaign availability publication and successful composite finalization.

- [ ] **Step 4: Run the green path tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathIdentityAdministrationTests|FullyQualifiedName~CampaignPathIdentityEndpointTests|FullyQualifiedName~CampaignEndpointTests|FullyQualifiedName~CampaignPathStartupRecoveryTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: PASS across register, update, repair, deregister, takeover, replay, and failure injection.

- [ ] **Step 5: Refactor and verify path secrecy**

Assert that status and errors contain no marker bytes or physical identity secret. Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: both commands exit zero.

### Task 8: Add one-time Session Campaign-binding status and resolution

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/SessionCampaignBindingAdministration.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionCampaignBindingResolutionStore.cs`
- Create: `src/RetroDownfall.Arcanum.Api/TheForge/SessionCampaignBindingEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SessionCampaignBindingAdministrationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionCampaignBindingEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionEndpointTests.cs`

**Interfaces:**

- Consumes: Task 1's exact Session-binding DTOs and `ISessionCampaignBindingAdministration` port; Task 2 lease-bound response and exact protected-header helper; Plan 03 immutable Session binding reader, Plan 01 binding and resolution-receipt tables, Campaign existence check, authority epoch, and Plan 03 Task 2 `ICovenantEnvelopeCodec` purpose `Arcanum.Session.CampaignBinding.v1`.
- Produces: the SQLCipher-backed resolution store, one implementation of Task 1's three-method administration port, and `MapSessionCampaignBindingEndpoints`.

- [ ] **Step 1: Write failing binding-resolution tests**

Add exact methods:

```csharp
[Fact]
public async Task Status_paginates_legacy_unresolved_bindings_without_guessing_scope()

[Fact]
public async Task Prepare_binds_session_prior_row_digest_authority_epoch_and_chosen_scope()

[Fact]
public async Task Apply_changes_only_legacy_unresolved_and_writes_receipt_atomically()

[Theory]
[InlineData(SessionCampaignBindingKind.GlobalOnly)]
[InlineData(SessionCampaignBindingKind.Campaign)]
public async Task Final_binding_cannot_be_changed(SessionCampaignBindingKind finalKind)

[Fact]
public async Task Campaign_deleted_before_install_cannot_be_resolved_as_global_implicitly()

[Fact]
public async Task Restart_replay_uses_same_operation_and_digest_without_old_token_authority()
```

- [ ] **Step 2: Run the red binding tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SessionCampaignBindingAdministrationTests|FullyQualifiedName~SessionCampaignBindingEndpointTests"
```

Expected: FAIL because `/api/sessions/campaign-binding/status`, `/prepare`, and `/apply` do not exist.

- [ ] **Step 3: Implement the one-time resolution contract**

Implement Task 1's exact `ISessionCampaignBindingAdministration` port and wire DTOs. Do not define another interface, request, response, enum, or `ArcanumJsonContext` entry in this task. Task 1's status contract accepts one Session or explicit `AllSessions`, then returns only binding state, historical Campaign identity, revision, and remediation. After validating that selector, `AllSessions` acquires exactly one `CovenantInstallationReadLease` through `AcquireInstallationReadAsync`; one Session and prepare use the exact Global or Campaign `CovenantReadLease` selected from the content-free immutable binding preflight. Both pass their concrete lease as `ICovenantSnapshotReadLease`, and the service rejects insufficient coverage before SQL. Prepare returns the Task 1 exact effect, stable apply-request digest, five-minute recovery token, and no mutable authority object.

Apply performs receipt-first lookup, rejects changed digest, then authenticates new work and writes the final binding plus `session_campaign_binding_resolution_receipts` in one immediate transaction. Accept only `LegacyUnresolved`, and recheck Session, Campaign, prior-row digest, authority epoch, and the caller-owned write lease. The endpoint retains status/prepare read leases and apply write leases through the lease-bound response.

Map the three POST routes with names `Session_CampaignBindingStatus`, `Session_CampaignBindingPrepare`, and `Session_CampaignBindingApply`. Require operator authority, the exact private no-cache headers, and lease-bound JSON for every result.

- [ ] **Step 4: Run the green binding tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SessionCampaignBindingAdministrationTests|FullyQualifiedName~SessionCampaignBindingEndpointTests|FullyQualifiedName~SessionEndpointTests"
```

Expected: PASS. Final rows remain immutable and every exact replay returns the recorded result.

- [ ] **Step 5: Refactor and verify missing-binding behavior**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignBinding|FullyQualifiedName~CanonicalCampaign"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. Missing and legacy rows never become implicit Global authority.

### Task 9: Integrate Covenant callers with the green requested-operation contract

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantRequestedOperationStarter.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantRequestedOperationStarterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRequestedIdentityTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationProgressContractsTests.cs`

**Interfaces:**

- Consumes without redefining: Plan 03's green `LongRunningOperationCreateRequest`, all-present or all-null requested-operation identity triplet, `LongRunningOperationPublicProgress` version-1 contract, `ILongRunningOperationCoordinator.StartAsync` result flow, store replay semantics, source-generated progress codec, and migrated existing callers.
- Produces: one internal caller adapter, `CovenantRequestedOperationStarter`, used by Task 10 to start or replay Covenant family reinitialize and by later Plan 04 callers that already possess an authenticated stable apply-request and effect digest. It owns no persistence, uniqueness handling, digest comparison, progress codec, DTO, or recovery policy.

- [ ] **Step 1: Write the failing caller-delegation tests**

Create `CovenantRequestedOperationStarterTests` with exact methods:

```csharp
[Fact]
public async Task Requested_start_delegates_the_exact_all_present_identity_triplet()

[Fact]
public async Task Same_identity_replay_returns_the_coordinator_result_unchanged()

[Fact]
public async Task Idempotency_conflict_returns_unchanged_without_local_digest_comparison()

[Fact]
public void Adapter_has_no_store_codec_or_public_dto_dependency()

[Fact]
public void Plan04_does_not_redeclare_the_requested_operation_contract()
```

- [ ] **Step 2: Run the red adapter tests and the green prerequisite tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRequestedOperationStarterTests|FullyQualifiedName~LongRunningOperationRequestedIdentityTests|FullyQualifiedName~LongRunningOperationProgressContractsTests"
```

Expected: the Plan 03 prerequisite suites PASS and the new adapter suite FAILS because `CovenantRequestedOperationStarter` does not exist. A failure in either prerequisite stops Plan 04. Do not modify the Plan 03 contract, store, coordinator, endpoint, callers, or fakes here.

- [ ] **Step 3: Add the smallest caller-only adapter**

Implement one internal adapter whose requested arm constructs the already-defined Plan 03 request and delegates once:

```csharp
internal sealed class CovenantRequestedOperationStarter(
    ILongRunningOperationCoordinator coordinator)
{
    public Task<Result<LongRunningOperationLeaseResult>> StartRequestedAsync(
        string kind,
        LongRunningOperationRecoveryPolicy recoveryPolicy,
        LongRunningOperationPublicProgress publicProgress,
        DateTimeOffset createdAt,
        Guid requestedOperationId,
        CovenantDigest applyRequestDigest,
        CovenantDigest effectDigest,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}
```

The implementation constructs exactly one `LongRunningOperationCreateRequest` with `requestedOperationId`, `applyRequestDigest`, and `effectDigest` present, passes every other optional correlation field as null unless an owning later task has exact evidence, and returns `coordinator.StartAsync` unchanged. It performs no replay lookup, transaction, fixed-time comparison, exception translation, progress encoding, or DTO projection. Plan 03 remains the only owner of those behaviors.

- [ ] **Step 4: Run the green caller-integration tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRequestedOperationStarterTests|FullyQualifiedName~LongRunningOperationRequestedIdentityTests|FullyQualifiedName~LongRunningOperationProgressContractsTests"
```

Expected: PASS. The adapter delegates the exact typed request and preserves Plan 03 replay and conflict results byte-for-byte.

- [ ] **Step 5: Verify ownership stays in Plan 03**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~LongRunningOperationRequestedIdentityTests|FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~LongRunningOperationEndpointTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. This task changes no Plan 03 contract, store, coordinator, endpoint, existing caller, fake, compatibility column, or JSON context.

### Task 9A: Build the shared protected-artifact erasure foundation

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantSensitiveArtifactPurgePolicy.cs`
- Create: `src/RetroDownfall.Arcanum.Core/DataLifecycle/ICovenantProtectedArtifactErasureKernel.cs`
- Create: `src/RetroDownfall.Arcanum.Core/DataLifecycle/ICovenantManagedFileErasureKernel.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantProtectedArtifactErasureKernel.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantManagedFileErasureKernel.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/LocalErasureWorkItemStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantLocalErasureStartupRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantProtectedArtifactErasureKernelTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantManagedFileErasureKernelTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/LocalErasureWorkItemStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantLocalErasureStartupRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Plan 03 artifact labels, finalization and erasure evidence, Session sensitivity projection, exact `OperatorAuthorityContext`, expanded Core `ManagedFileDurableLocationEvidence`, and the internal Infrastructure `IManagedFileCapabilityOpener` and `IManagedFileOwnershipVerifier`, Plan 02 `CovenantWriteLease`, `ICovenantExclusiveOperationLease`, and exact `CovenantExclusiveOperation`, plus Plan 01's exact `ManagedFileWriteIntentPhase`, `LocalErasureWorkItemState`, `LocalErasureDeletionEvidenceCode`, edge-specific managed-write guard, and connection-local `SensitivityRetentionPurge` and `CovenantFamilyMaintenance` SQL authorizations. Task 9A cannot consume Plan 03's internal created-child writer or recovery operation surfaces.
- Produces: the one exhaustive protected-artifact policy registry, `ICovenantProtectedArtifactErasureKernel`, exactly one `ICovenantManagedFileErasureKernel`, and one pre-readiness `ICovenantLocalErasureStartupRecovery`. Tasks 10, 16, and 17 must delegate to these kernels and cannot implement another database-artifact or managed-file identity/delete algorithm.

```csharp
public enum SensitiveArtifactKind : byte
{
    AssistantEntry = 1,
    TurnEvidence = 2,
    Summary = 3,
    ToolArtifact = 4,
    SessionTitle = 5,
    Saga = 6,
    Lexicon = 7,
    Embedding = 8,
    SearchProjection = 9,
    AuditProjection = 10,
    Notification = 11,
    ManagedWorkspaceFile = 12,
    IdempotencyClaim = 13
}

public enum CovenantArtifactErasureAuthorityKind : byte
{
    Ordinary = 1,
    Exclusive = 2
}

public sealed record CovenantProtectedArtifactErasureItem(
    Guid ArtifactId,
    SensitiveArtifactKind Kind,
    Guid? SessionId,
    Guid SensitivityLabelId,
    ArtifactSensitivityLabel ExpectedLabel,
    CovenantDigest ExpectedArtifactDigest,
    ulong ExpectedRevision);

public sealed record CovenantProtectedArtifactErasurePage(
    Guid ExpectedDatasetGeneration,
    IReadOnlyList<CovenantProtectedArtifactErasureItem> Items);

public sealed record CovenantManagedFileErasureRequest(
    Guid WorkItemId,
    Guid OperationId,
    Guid SourceManagedWriteOperationId,
    Guid ArtifactId,
    Guid SensitivityLabelId,
    ulong ExpectedSourceWriteRevision);

public enum CovenantErasureBlocker : byte
{
    None = 0,
    ManualOwnershipMismatch = 1,
    AuthorityStale = 2,
    IntegrityFailure = 3,
    StorageUnavailable = 4
}

public sealed record CovenantArtifactErasureProgress(
    ulong ExaminedCount,
    ulong ErasedCount,
    ulong PreservedEvidenceCount,
    CovenantErasureBlocker Blocker);

public interface ICovenantProtectedArtifactErasureKernel
{
    ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
        CovenantProtectedArtifactErasurePage page,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default);
}

public interface ICovenantManagedFileErasureKernel
{
    ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
        CovenantManagedFileErasureRequest request,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default);
}

internal enum CovenantLocalErasureStartupRecoveryOutcome : byte
{
    NoActiveWork = 1,
    ReconciledReady = 2,
    ManualEvidenceReady = 3,
    Blocked = 4
}

internal interface ICovenantLocalErasureStartupRecovery
{
    Task<Result<CovenantLocalErasureStartupRecoveryOutcome>>
        RecoverBeforeReadinessAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken);
}
```

The page constructor requires one nonempty canonical 128-bit `Guid` dataset generation, deep-copies at most 256 items, rejects duplicate artifact IDs and mismatched Session ownership, and exposes no mutable backing collection. `CovenantErasureBlocker` is a closed content-free code with `None=0`, `ManualOwnershipMismatch=1`, `AuthorityStale=2`, `IntegrityFailure=3`, and `StorageUnavailable=4`. Counts use checked `u64` arithmetic. Every consumer compiles against these methods and named records; no coordinator calls an implementation-only overload.

`CovenantSensitiveArtifactPurgePolicy` owns this one exhaustive code-to-policy table. No later task defines another `SensitiveArtifactKind` value, code, or policy table:

| Kind | Code | Required purge policy |
|---|---:|---|
| `AssistantEntry` | 1 | Delete dependent content and projections, the Entry, and label in one transaction; append the matching erasure receipt and preserve the finalization guard plus turn claim for typed 410 replay. |
| `TurnEvidence` | 2 | Delete only content-bearing turn projections after terminal guard or erasure evidence is durable; preserve terminal claim, finalization, disclosure, and replay-denial evidence. |
| `Summary` | 3 | Delete the summary artifact, dependent projections, current pointer and value, and label in one transaction, then repair Session sensitivity state. |
| `ToolArtifact` | 4 | Delete the tool artifact, every derived projection, and label in one transaction. |
| `SessionTitle` | 5 | Delete the title artifact, dependent projections, current pointer and value, and label in one transaction, then repair Session sensitivity state. |
| `Saga` | 6 | Delete the Saga row, embedding and FTS projections, provenance, and label in one transaction. |
| `Lexicon` | 7 | Delete the Lexicon row, facts and FTS projections, provenance, and label in one transaction. |
| `Embedding` | 8 | Delete the embedding, vector projection, and label in one transaction. |
| `SearchProjection` | 9 | Delete the protected search projection and label in one transaction. Generic search never contains this artifact. |
| `AuditProjection` | 10 | Delete the content-bearing audit projection and label in one transaction while retaining content-free disclosure and terminal audit evidence. |
| `Notification` | 11 | Delete the notification payload or projection and label in one transaction. |
| `ManagedWorkspaceFile` | 12 | Delegate once to `ICovenantManagedFileErasureKernel`; that kernel exclusively owns durable work-item persistence, no-follow open, same-handle verification, compare-delete, parent fsync, absence, mismatch, and label completion. |
| `IdempotencyClaim` | 13 | Purge any cached protected response body and label only after the claim is durably non-replayable or bound to erasure evidence; preserve claim identity and typed replay denial. |

The registry rejects unknown values and has literal coverage for every code. `external_disclosure_receipts`, folded disclosure aggregates, and joined disclosure state remain outside this artifact switch and cannot be selected by it.

- [ ] **Step 1: Write the failing shared-kernel tests**

Add exact methods:

```csharp
[Theory]
[InlineData(CovenantArtifactErasureAuthorityKind.Ordinary)]
[InlineData(CovenantArtifactErasureAuthorityKind.Exclusive)]
public async Task Kernel_borrows_and_revalidates_the_exact_caller_owned_authority(
    CovenantArtifactErasureAuthorityKind kind)

[Fact]
public async Task Kernel_never_acquires_completes_or_disposes_an_operation_lease()

[Fact]
public async Task Ordinary_authority_carries_operator_context_and_each_transaction_borrows_its_own_sql_authorization()

[Fact]
public void Ordinary_authority_compares_only_shared_epoch_identity_and_key_facts()

[Theory]
[InlineData(CovenantArtifactErasureAuthorityKind.Ordinary)]
[InlineData(CovenantArtifactErasureAuthorityKind.Exclusive)]
public async Task Database_artifact_owner_outside_lease_snapshot_scope_is_rejected_before_effect(
    CovenantArtifactErasureAuthorityKind kind)

[Theory]
[InlineData(CovenantArtifactErasureAuthorityKind.Ordinary)]
[InlineData(CovenantArtifactErasureAuthorityKind.Exclusive)]
public async Task Managed_file_owner_outside_lease_snapshot_scope_is_rejected_before_work_item_or_open(
    CovenantArtifactErasureAuthorityKind kind)

[Theory]
[InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
[InlineData(CovenantExclusiveOperation.CovenantReset)]
[InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
public void Exclusive_authority_accepts_only_the_three_erasure_operations(
    CovenantExclusiveOperation operation)

[Theory]
[InlineData(CovenantExclusiveOperation.CampaignPathMutation)]
[InlineData(CovenantExclusiveOperation.CampaignDelete)]
[InlineData(CovenantExclusiveOperation.ProtectedSessionTransfer)]
[InlineData(CovenantExclusiveOperation.SchemaRepair)]
[InlineData(CovenantExclusiveOperation.BackupRestore)]
public void Exclusive_authority_rejects_every_non_erasure_operation(
    CovenantExclusiveOperation operation)

[Fact]
public async Task Database_artifact_projection_label_and_session_state_commit_atomically()

[Fact]
public async Task Managed_file_persists_work_before_open_and_removes_label_only_after_delete_and_parent_fsync()

[Fact]
public async Task Managed_file_absent_is_idempotent_and_mismatch_remains_a_manual_blocker()

[Theory]
[InlineData(ManagedFileVerification.Match, 1)]
[InlineData(ManagedFileVerification.Mismatch, 2)]
public void Managed_file_verification_codes_are_literal_and_exhaustive(
    ManagedFileVerification result,
    byte code)

[Theory]
[InlineData(ManagedFileCompareDeleteResult.Deleted, 1)]
[InlineData(ManagedFileCompareDeleteResult.Mismatch, 2)]
public void Managed_file_compare_delete_codes_are_literal_and_exhaustive(
    ManagedFileCompareDeleteResult result,
    byte code)

[Fact]
public async Task Managed_file_kernel_disposes_opened_handle_once_on_every_terminal_and_failure_path()

[Fact]
public async Task Managed_file_request_rereads_producer_ownership_and_rejects_forged_evidence_before_open()

[Fact]
public async Task Managed_file_work_item_copies_root_revision_parent_segments_and_same_handle_parent_identity()

[Fact]
public async Task Managed_file_open_rejects_changed_root_revision_or_parent_identity_before_absence_is_authoritative()

[Fact]
public async Task Managed_file_completion_rejects_managed_mutation_authorization_alone()

[Theory]
[InlineData(CovenantArtifactErasureAuthorityKind.Ordinary)]
[InlineData(CovenantArtifactErasureAuthorityKind.Exclusive)]
public async Task Managed_file_completion_requires_matching_deletion_verified_item_and_exact_edge_authorization(
    CovenantArtifactErasureAuthorityKind kind)

[Fact]
public void Managed_file_erasure_cannot_resolve_or_call_created_child_recovery_primitive()

[Fact]
public async Task Local_erasure_recovery_reconciles_delete_to_label_crashes_under_the_held_installation_lock()

[Fact]
public async Task Local_erasure_recovery_never_reconstructs_an_ordinary_write_lease_or_operator_context()

[Theory]
[InlineData(CovenantLocalErasureStartupRecoveryOutcome.NoActiveWork, 1)]
[InlineData(CovenantLocalErasureStartupRecoveryOutcome.ReconciledReady, 2)]
[InlineData(CovenantLocalErasureStartupRecoveryOutcome.ManualEvidenceReady, 3)]
[InlineData(CovenantLocalErasureStartupRecoveryOutcome.Blocked, 4)]
public void Local_erasure_recovery_outcome_codes_are_literal_and_exhaustive(
    CovenantLocalErasureStartupRecoveryOutcome outcome,
    byte code)

[Fact]
public void Shared_erasure_kernels_have_exactly_one_registration()

[Theory]
[InlineData(CovenantArtifactErasureAuthorityKind.Ordinary, 1)]
[InlineData(CovenantArtifactErasureAuthorityKind.Exclusive, 2)]
public void Erasure_authority_kind_codes_are_literal_and_exhaustive(
    CovenantArtifactErasureAuthorityKind kind,
    byte code)

[Theory]
[InlineData(CovenantErasureBlocker.None, 0)]
[InlineData(CovenantErasureBlocker.ManualOwnershipMismatch, 1)]
[InlineData(CovenantErasureBlocker.AuthorityStale, 2)]
[InlineData(CovenantErasureBlocker.IntegrityFailure, 3)]
[InlineData(CovenantErasureBlocker.StorageUnavailable, 4)]
public void Erasure_blocker_codes_are_literal_and_exhaustive(
    CovenantErasureBlocker blocker,
    byte code)
```

- [ ] **Step 2: Run the red foundation tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~CovenantManagedFileErasureKernelTests|FullyQualifiedName~LocalErasureWorkItemStoreTests|FullyQualifiedName~CovenantLocalErasureStartupRecoveryTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests"
```

Expected: FAIL because the shared kernels and nonserializable authority union do not exist.

- [ ] **Step 3: Implement one borrower-only erasure authority and both kernels**

Define a closed, nonserializable Core `CovenantArtifactErasureAuthority` with `Ordinary` and `Exclusive` arms. `Ordinary` contains the exact live caller-owned `CovenantWriteLease` plus the exact Plan 03 `OperatorAuthorityContext`; construction requires its closed requirement to be `SensitivityRetentionPurge` and compares only facts actually present on both values. It requires equal authority epoch and installation identity. It also requires equal master-key version only when `CovenantOperationLeaseSnapshot` exposes that exact key-version fact. The constructor does not alias another generation or identity field to manufacture a comparison. `OperatorAuthorityContext` has no scope or availability-generation member, so construction performs neither comparison. After construction, each use independently calls the issuer's context revalidator and `CovenantWriteLease.RevalidateAsync` before reading scope or performing an effect. It never contains an Infrastructure SQL authorization scope. `Exclusive` contains the exact live caller-owned `ICovenantExclusiveOperationLease` plus Plan 02's exact `CovenantExclusiveOperation`. Construction accepts only `CovenantFamilyReinitialize`, `CovenantReset`, or `HealthyCatalogFactoryErasure` and requires that value plus the lease snapshot's operation ID and effect digest to equal the lease's `CovenantExclusiveRecoveryOwner`. It rejects `CampaignPathMutation`, `CampaignDelete`, `ProtectedSessionTransfer`, `SchemaRepair`, `BackupRestore`, unknown enum values, and any owner mismatch. Both arms expose revalidation and their exact `lease.Snapshot.CovenantOperationScope` only. Neither arm can acquire, complete, dispose, serialize, or persist a lease.

`ICovenantProtectedArtifactErasureKernel` accepts a bounded immutable artifact page plus that authority. Before any effect, it revalidates the authority, rereads each artifact and exact label in the immediate transaction, derives the owner scope only from those current rows, and requires that scope to be within `authority.Lease.Snapshot.CovenantOperationScope`. A cross-Campaign, Campaign-under-Global-only, historical-owner mismatch, missing owner, or caller-supplied scope fails before SQL authorization or deletion. The Infrastructure implementation then borrows Plan 01's connection-local `SensitivityRetentionPurge` authorization on that exact live transaction only when the authority arm is `Ordinary`; the exclusive arm borrows its exact family-maintenance authorization instead. It holds the SQL authorization only across that transaction's guarded statements and disposes it before commit or return. It applies the exhaustive 13-kind policy registry, deletes database-owned content and projections with their exact label atomically, repairs current title, summary, and `session_sensitivity_state`, appends assistant erasure evidence where required, and preserves finalization, claim, replay-denial, and disclosure evidence. It returns a content-free checked progress result.

`ICovenantManagedFileErasureKernel` accepts one immutable managed-file erasure request plus the same authority. The request identifies the source managed-write operation and expected revision but carries no path, durable location, root identity, path revision, parent segments, parent physical identity, ownership evidence, file physical identity, content hash, or owner scope. Before the first filesystem effect, one immediate transaction revalidates the authority, rereads that exact Plan 01 `managed_file_write_intents` row and `artifact_sensitivity`, derives their current owner scope, and requires it to be within `authority.Lease.Snapshot.CovenantOperationScope`. Only then does it borrow the arm's SQL authorization. It requires `AdoptedAndLabeled`, exact artifact and label identity, exact revision, null pending label projection, and nonnull final ownership, then copies the producer's complete target `ManagedFileDurableLocationEvidence` and `ManagedFileOwnershipEvidence` into a new `local_erasure_work_items` row. The copied location includes the canonical root identity digest, positive path revision, bounded normalized relative parent segments, same-handle parent physical-identity digest, and target child leaf. A cross-scope owner, forged source, caller value, same-leaf replacement, changed root or revision, parent swap, or producer/label race fails before work-item insertion, open, authoritative absence, or delete.

Every later work-item or label transaction separately borrows and disposes the exact connection-local SQL authorization selected by the live authority arm; no authorization object crosses a transaction or filesystem effect. On every initial or resumed attempt it consumes Plan 01's exact work-item state and calls the internal `IManagedFileCapabilityOpener.OpenNoFollowAsync`. The opener must first revalidate the copied canonical root identity and path revision, traverse only the copied normalized parent segments without following links, and compare the parent physical identity from the same retained parent handle. A mismatch is a manual blocker or readiness blocker as specified by the state, never `AlreadyAbsent`. Only after those checks may `Absent` record `DeletionVerified(AlreadyAbsent)` without invoking the verifier. Only `Opened(ManagedFileOpenHandle)` may reach internal `IManagedFileOwnershipVerifier.CompareDeleteAsync`; only `Deleted` after exact adopted identity, full hash, length, and parent fsync records `DeletionVerified(SameHandleDeletedAndParentFsynced)`. Every opened handle is disposed in `finally`, with repeat disposal remaining a no-op and every post-disposal call rejected. `Mismatch` leaves the file, producer ownership, and label untouched and records `ManualBlocker`. Task 9A cannot resolve, downcast to, or call `IManagedFileCreatedChildWriter` or `IManagedFileCreatedChildRecovery`; the created-child digest is writer-recovery evidence and never weakens adopted-file erasure.

The completion transaction selects its SQL authorization by the exact source-row edge. Ordinary authority borrows only `SensitivityRetentionPurge`; an exclusive authority borrows only `CovenantFamilyMaintenance`. It does not borrow `ManagedFileIntentMutation`. In one immediate transaction it revalidates the `DeletionVerified` work item, removes the exact label, advances the source write row `AdoptedAndLabeled -> Erased`, and advances that same work item to `Completed`. The source-row trigger rejects `ManagedFileIntentMutation` alone or in combination, the wrong erasure authorization, a missing or nonmatching `DeletionVerified` item, a still-present or mismatched label, and any source revision or identity change. The work-item completion guard requires that exact source row already be `Erased` and the exact label absent on the same transaction connection. Rollback leaves all three states unchanged. The kernel never persists a live handle or a capability for a nonexistent file. The write-only `ManagedFileWriteDurableLocationEvidence` and its temporary leaf cannot enter the public erasure request.

`CovenantLocalErasureStartupRecovery` is the sole pre-readiness adopter for nonterminal `local_erasure_work_items`. The hosted-service and direct CLI bootstrap pass the same already-held installation lock; the recovery asserts and borrows it without reacquiring or disposing it. It runs after Plan 01 core convergence and before optional Covenant initialization, workspace writers, reset or retention admission, workers, endpoints, and readiness. It authenticates each bounded row against its still-current source ownership row and label, including exact equality of the complete expanded durable location and final ownership copied from the producer, then calls the same internal managed-file erasure state-machine implementation used by `ICovenantManagedFileErasureKernel`. It never reconstructs the lost ordinary `CovenantWriteLease` or `OperatorAuthorityContext`. Instead, the durable authorized work item plus caller-held installation lock is the narrowly scoped restart authority, and each SQLite transition independently borrows Plan 01 `SensitivityRetentionPurge` on its live transaction connection. It can only finish `Prepared -> DeletionVerified -> Completed` or terminalize a proven mismatch as `ManualBlocker`; it cannot begin a new erasure, change a root, revision, parent segment, parent identity, artifact or label identity, substitute ownership, call the created-child cleanup primitive, or delete any other file. A malformed row, missing producer, source revision regression, storage uncertainty, or authorization failure returns `Blocked` and prevents readiness. A terminal manual row retains the file and label and returns `ManualEvidenceReady`; it does not block unrelated startup. Crash tests restart before and after root and parent revalidation, open, unlink, parent fsync, deletion-evidence CAS, label/source completion, and work-item terminalization.

Register each kernel exactly once. They receive caller-owned authority from Task 10's family coordinator, Task 16's ordinary retention coordinator, or Task 17's erasure coordinator. They never resolve `ICovenantOperationGate`, so an exclusive caller cannot self-deadlock by trying to acquire an ordinary lease during drain.

- [ ] **Step 4: Run the green foundation tests**

Run the Step 2 command.

Expected: PASS for both authority arms, atomic database purge, crash-safe managed-file work and startup adoption, absence, mismatch, and no lease ownership transfer.

- [ ] **Step 5: Verify the shared ownership boundary**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~CovenantManagedFileErasureKernelTests|FullyQualifiedName~CovenantLocalErasureStartupRecoveryTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~DiWiringSmokeTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. The foundation exposes exactly one registration for each shared erasure kernel and no second managed-file open, identity, compare-delete, or label-removal implementation. Task 18 owns the later consumer-parity assertion for Tasks 10, 16, and 17.

### Task 10: Add schema repair, index rebuild, and family reinitialize recovery surfaces

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICovenantSchemaRepairStartupRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantProtectedInventoryService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuildRecoveryHandler.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeRecoveryHandler.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantRecoveryContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantRecoveryJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CovenantMemoryEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantSchemaRepairTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantSchemaRepairRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantIndexRebuildRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantFamilyReinitializeRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantProtectedInventoryServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantMaintenanceEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationProgressContractsTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRecoveryRegistryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationCrashRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`

**Interfaces:**

- Consumes: Task 1's exact maintenance DTOs and `ICovenantMaintenanceService` port; Plan 01 capability installer, inspected closed manifests, always-present `covenant_schema_repair_intents`, central connection owner, pooled-handle drain, sidecar inventory, and canonical/accelerator repair rules.
- Consumes: Plan 02's exact `CovenantIndexRebuilder.AdvanceBatchAsync(CovenantIndexRebuildProgress?, CovenantAcceleratorLease, CancellationToken)` contract, `CovenantIndexRebuildProgress`, availability publisher, `CovenantExclusiveLease`, `CovenantInstallationReadLease`, and operation gate; Plan 03 Task 2 `ICovenantAuthorityTransitionPublisher`; Plan 03 Task 11 disclosure store and `ICovenantDisclosureWriterLifecycle`; Plan 03's requested-operation contract and coordinator, with Task 9's caller-only adapter used only for family reinitialize; Task 9A's shared database and managed-file erasure kernels; Plan 01 local-erasure tables; and recovery envelope purpose `Arcanum.Covenant.FamilyReinitialize.v1`.
- Produces: the bounded protected-state inventory, `LongRunningOperationKinds.CovenantIndexRebuild`, `LongRunningOperationKinds.CovenantFamilyReinitialize`, checkpoint V1 contracts, recovery handlers, `ICovenantSchemaRepairStartupRecovery`, and one implementation of Task 1's maintenance port.

Freeze the only schema-repair and family-reinitialize recovery phase codes in `CovenantRecoveryContracts.cs`:

```csharp
internal enum CovenantSchemaRepairPhase : byte
{
    Prepared = 1,
    CatalogCommitted = 2,
    HealthVerified = 3,
    ReopenPending = 4,
    Completed = 5,
    Abandoned = 6
}

internal enum CovenantFamilyReinitializePhase : byte
{
    Planned = 1,
    AdmissionClosed = 2,
    LocalArtifactsProcessed = 3,
    HandlesClosed = 4,
    FamilyDropped = 5,
    DatabaseCompacted = 6,
    CanonicalInstalled = 7,
    AcceleratorInstalled = 8,
    FinalWalTruncated = 9,
    SidecarsVerified = 10,
    ReopenedVerified = 11
}
```

Both enums use the literal byte codes above in their source-generated checkpoints. Unknown, zero, skipped, or out-of-order phases fail closed. No API DTO or later task defines a second phase model.

- [ ] **Step 1: Write failing maintenance and recovery tests**

Add exact methods:

```csharp
[Fact]
public async Task Repair_recreates_only_absent_family_or_safe_ordinary_index()

[Fact]
public async Task Repair_closes_and_drains_the_exclusive_gate_before_any_catalog_change()

[Fact]
public async Task Repair_selects_exactly_one_commit_rollback_or_keep_closed_disposition()

[Fact]
public async Task Repair_post_disposition_finalizer_completes_or_abandons_only_after_successful_reopen()

[Fact]
public async Task Repair_failed_disposition_skips_finalizer_and_leaves_reopen_pending()

[Fact]
public async Task Repair_finalizer_failure_leaves_reopen_pending_and_never_repeats_disposition()

[Fact]
public async Task Repair_persists_its_exact_owner_before_the_first_catalog_mutation()

[Theory]
[InlineData(CovenantSchemaRepairPhase.Prepared)]
[InlineData(CovenantSchemaRepairPhase.CatalogCommitted)]
[InlineData(CovenantSchemaRepairPhase.HealthVerified)]
[InlineData(CovenantSchemaRepairPhase.ReopenPending)]
public async Task Repair_startup_recovery_resumes_the_exact_journaled_owner_before_readiness(CovenantSchemaRepairPhase phase)

[Fact]
public async Task Repair_startup_recovery_rejects_wrong_operation_effect_or_catalog_digest_and_keeps_admission_closed()

[Fact]
public async Task Active_schema_repair_journal_blocks_pools_optional_services_workers_and_readiness_until_terminal()

[Theory]
[InlineData("missing-canonical-trigger")]
[InlineData("same-version-drift")]
[InlineData("newer-version")]
[InlineData("unknown-object")]
public async Task Repair_returns_manual_recovery_without_alteration(string defect)

[Fact]
public async Task Rebuild_resumes_in_256_head_batches_and_never_exposes_partial_fts()

[Fact]
public async Task Rebuild_identity_change_discards_partial_state_and_starts_clean()

[Fact]
public async Task Index_rebuild_uses_server_generated_operation_identity()

[Theory]
[InlineData("covenant-index-rebuild", LongRunningOperationProgressKind.CovenantIndexRebuild, 17)]
[InlineData("covenant-family-reinitialize", LongRunningOperationProgressKind.CovenantFamilyReinitialize, 18)]
public void Covenant_operation_kinds_have_exact_progress_catalog_mappings(
    string kind,
    LongRunningOperationProgressKind progressKind,
    ushort expectedCode)

[Fact]
public async Task Reinitialize_prepare_reports_all_loss_and_preservation_counts_without_content()

[Fact]
public async Task Reinitialize_apply_replays_requested_operation_before_token_decode()

[Fact]
public async Task Reinitialize_purges_database_owned_protected_artifacts_and_managed_files_before_family_drop()

[Fact]
public async Task Reinitialize_passes_its_exclusive_authority_to_the_shared_erasure_kernels_without_nested_acquisition()

[Theory]
[InlineData(CovenantFamilyReinitializePhase.Planned)]
[InlineData(CovenantFamilyReinitializePhase.AdmissionClosed)]
[InlineData(CovenantFamilyReinitializePhase.LocalArtifactsProcessed)]
[InlineData(CovenantFamilyReinitializePhase.HandlesClosed)]
[InlineData(CovenantFamilyReinitializePhase.FamilyDropped)]
[InlineData(CovenantFamilyReinitializePhase.DatabaseCompacted)]
[InlineData(CovenantFamilyReinitializePhase.CanonicalInstalled)]
[InlineData(CovenantFamilyReinitializePhase.AcceleratorInstalled)]
[InlineData(CovenantFamilyReinitializePhase.FinalWalTruncated)]
[InlineData(CovenantFamilyReinitializePhase.SidecarsVerified)]
[InlineData(CovenantFamilyReinitializePhase.ReopenedVerified)]
public async Task Reinitialize_recovers_idempotently_from_every_phase(CovenantFamilyReinitializePhase phase)

[Fact]
public void Recovery_registry_has_exactly_one_descriptor_and_handler_for_each_covenant_kind()

[Fact]
public void Schema_repair_and_family_reinitialize_phase_codes_are_literal_exhaustive_and_source_generated()
```

- [ ] **Step 2: Run the red maintenance tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSchemaRepairTests|FullyQualifiedName~CovenantSchemaRepairRecoveryTests|FullyQualifiedName~CovenantIndexRebuildRecoveryTests|FullyQualifiedName~CovenantFamilyReinitializeRecoveryTests|FullyQualifiedName~CovenantProtectedInventoryServiceTests|FullyQualifiedName~CovenantMaintenanceEndpointTests|FullyQualifiedName~LongRunningOperationProgressContractsTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because the new operation kinds, checkpoints, and routes are absent.

- [ ] **Step 3: Implement closed checkpoints and lifecycle coordinators**

Define `CovenantIndexRebuildCheckpointV1` as the bounded source-generated durable projection of Plan 02's exact `CovenantIndexRebuildProgress` fields: `Guid DatasetGeneration`, `ulong AcceleratorEpoch`, `long BaseTargetSearchSequence`, `long CapturedCoreCampaignDeletionSequence`, `CovenantIndexRebuildPhase Phase`, nullable `long BaseScanAfterSearchRowId`, `long LastContiguousAppliedSequence`, `long BaseHeadsProcessed`, nullable `long BaseHeadsTotal`, and `long DeltaRowsProcessed`. The checkpoint adds only its version discriminator. It cannot rename, reinterpret, or add a second rebuild cursor or phase model.

Define `CovenantSchemaRepairStartupRecoveryOutcome` as `NoActiveJournal=1`, `RecoveredReady=2`, or `KeptClosed=3`, and freeze this Infrastructure-only seam:

```csharp
internal interface ICovenantSchemaRepairStartupRecovery
{
    Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);
}
```

The host or CLI bootstrap owns the one live installation lock and passes it by reference. Recovery asserts the exact held lock, never reacquires or disposes it, and runs after Plan 03 host-tools and supported-catalog precheck plus the Plan 01 core-only install, but before shared pools, optional Covenant services, transfer or managed-file admission, workers, or readiness. An active Plan 01 repair intent reconstructs `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.SchemaRepair, effectDigest)` from immutable journal fields and calls Plan 02 `ResumeExclusiveAsync`. `Prepared` revalidates the inspected catalog digest and either performs the one safe transactional repair or proves a no-mutation rollback, then persists the selected disposition arm by advancing to `ReopenPending`. `CatalogCommitted` reruns the closed manifest and integrity checks. `HealthVerified` republishes availability and advances durably to `ReopenPending`. `ReopenPending` performs the exact selected gate disposition, then invokes the exact journal finalizer before lease disposal. The finalizer reaches `Completed` only after successful `CommitAndReopen` or `Abandoned` only after successful proven `RollbackAndReopen`. Failed disposition skips the finalizer and leaves `ReopenPending`; successful `KeepClosed` and finalizer failure also leave it nonterminal. Unknown phases, changed catalog or effect identity, failed health publication, or disposition uncertainty returns `KeptClosed`, leaves the journal active, and blocks bootstrap. The startup path never calls initial acquisition for an existing intent.

Define `CovenantFamilyReinitializeCheckpointV1` with operation ID, installation identity, authority epoch, database-file identity digest, inspected catalog digest, effect digest, old and optional new dataset generations, phase, managed-artifact cursor, drop/install flags, compact or replacement-file identity digest, retry count, and last durable error code. It stores no path, key, content, live handle, task, cancellation token, or service object.

Register both with `CovenantRecoveryJsonContext`, using exact version discriminators, payload size checks, unknown-field rejection, and no reflection fallback.

Add these registry descriptors:

```csharp
LongRunningOperationKinds.CovenantIndexRebuild
    => ResumeFromCheckpoint, MinCheckpointVersion: 1, MaxCheckpointVersion: 1

LongRunningOperationKinds.CovenantFamilyReinitialize
    => ResumeFromCheckpoint, MinCheckpointVersion: 1, MaxCheckpointVersion: 1,
       StartupPriority: BeforeStateWrites
```

Add these exact immutable kind constants to `LongRunningOperationKinds` and extend the sole `LongRunningOperationProgressKindCatalog` one-to-one map:

```csharp
public const string CovenantIndexRebuild = "covenant-index-rebuild";
public const string CovenantFamilyReinitialize = "covenant-family-reinitialize";

[LongRunningOperationKinds.CovenantIndexRebuild]
    = LongRunningOperationProgressKind.CovenantIndexRebuild; // ushort code 17
[LongRunningOperationKinds.CovenantFamilyReinitialize]
    = LongRunningOperationProgressKind.CovenantFamilyReinitialize; // ushort code 18
```

`LongRunningOperationProgressContractsTests` pins both literal strings, numeric codes 17 and 18, forward and reverse lookup, duplicate rejection, and exhaustiveness across the progress catalog, recovery registry, and registered handlers. Neither code remains a reservation after this task.

`CovenantIndexRebuildCoordinator` owns only long-running-operation identity, checkpoint serialization, recovery scheduling, and delegation of one idempotent batch step to Plan 02's single `CovenantIndexRebuilder`. Index rebuild has no authenticated preflight identity, so it calls the Plan 03 coordinator with a `LongRunningOperationCreateRequest` whose requested operation ID, apply-request digest, and effect digest are all null. It never calls Task 9 `CovenantRequestedOperationStarter`. For each step it acquires one exact `CovenantAcceleratorLease`, decodes the checkpoint to the exact nullable `CovenantIndexRebuildProgress`, calls `AdvanceBatchAsync`, copies the returned fields one-to-one into V1, then disposes the lease. It never reimplements base scanning, delta application, rank-1 integrity, or eligibility publication. A returned `RestartRequired` terminalizes the stale operation with a closed content-free code and starts one new all-null server-generated operation; it never mutates the old operation identity. Add architecture assertions that Plan 02 remains the only rebuild algorithm owner and index rebuild uses only the server-generated operation identity arm.

`CovenantFamilyReinitializeCoordinator` constructs `CovenantExclusiveRecoveryOwner(longRunningOperation.OperationId, CovenantExclusiveOperation.CovenantFamilyReinitialize, normalizedRequestIdentity.EffectDigest)` and calls Plan 02 `AcquireExclusiveAsync`. The owner always uses the durable server `LongRunningOperation.OperationId`; `RequestedOperationId` remains only the caller replay key in Plan 03's normalized identity row and is never a gate-owner identity. The recovery checkpoint stores the same server operation ID and effect digest, and the recovery handler reconstructs that exact owner before calling `ResumeExclusiveAsync`. The resulting global `CovenantExclusiveLease` closes admission and drains every affected operation lease. The coordinator wraps that exact caller-owned lease in Task 9A's exclusive `CovenantArtifactErasureAuthority` and passes it to both shared kernels. Before any family drop, the shared protected-artifact kernel securely purges every database-owned `CovenantDerived` Entry, tool artifact, summary, title, Saga, Lexicon, embedding, protected projection, notification, audit payload, and labeled cached body, repairs pointers and `session_sensitivity_state`, and preserves terminal, disclosure, claim, guard, and replay-denial evidence required by policy. The shared managed-file kernel then resolves every durable local work item through the single opener and same-handle verifier. A manual file blocker leaves the family intact and admission closed. Neither kernel acquires, completes, or disposes another lease.

Only after the comprehensive database and managed-file erasure reaches `LocalArtifactsProcessed` does the coordinator quiesce `ICovenantDisclosureWriterLifecycle`, obtain the central connection owner's exclusive drain, securely drop the closed-manifest and Covenant-prefixed family, compact or export-replace the database, install fresh canonical and accelerator tiers, truncate WAL, and prove sidecars. After the database transition commits, use the unpublished candidate key and initializer state for one read-only reopen that verifies canonical and accelerator health without creating WAL or SHM, then close that handle. Only after this `ReopenedVerified` proof, and while the exclusive gate remains held, call `ICovenantAuthorityTransitionPublisher.PublishCommittedAsync`; only a successful in-process key, issuer, availability, and gate publication permits disclosure-writer reopen. After the writer is healthy, `CompleteAsync(CommitAndReopen, lifecycleCt)` is the sole general-admission reopen. Preserve core Campaigns, Sessions, immutable bindings, authority taint, disclosure evidence, and unrelated memory. A reopen, publisher, writer-quiescence, owner-drain, erasure, or writer-reopen failure selects `KeepClosed`, keeps admission closed, and leaves the operation recoverable. A proven abort before durable mutation selects `RollbackAndReopen`.

`CovenantProtectedInventoryService` performs bounded indexed counts over labels, managed-file intents, local work items, canonical objects, derived artifacts, tainted Sessions, and nonrevocable disclosure state without returning content. The endpoint calls `AcquireInstallationReadAsync` exactly once for the all-installation preflight snapshot, passes that `CovenantInstallationReadLease` to the service, and retains it through plan serialization. The service only borrows and revalidates it and never acquires or disposes another lease.

Implement Task 1's exact four-method `ICovenantMaintenanceService` port. `RepairSchemaAsync` returns Task 1's `CovenantExclusiveLeasedServiceResult<CovenantSchemaRepairResultDto>` with the exact repair-journal post-disposition finalizer. Do not define another maintenance request, result, enum, interface, carrier, or `ArcanumJsonContext` entry in this task. The Infrastructure checkpoint types remain private durable recovery contracts and never replace the Task 1 wire DTOs.

Map `POST /api/memory/covenant/schema/repair`, `/schema/reinitialize/prepare`, `/schema/reinitialize`, and `/index/rebuild` as `Covenant_SchemaRepair`, `Covenant_FamilyReinitializePrepare`, `Covenant_FamilyReinitialize`, and `Covenant_IndexRebuild`. Schema repair creates one server operation ID and stable catalog-effect digest, constructs `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.SchemaRepair, effectDigest)`, and acquires and drains the global exclusive gate before service invocation. Before its first repair DDL, the service commits the Plan 01 core repair intent with that exact owner and inspected catalog digest. Every repairable DDL and catalog-metadata change then commits in one transaction before the intent advances to `CatalogCommitted`; closed manifest and integrity verification advances it to `HealthVerified`. A proven no-mutation rollback advances directly from `Prepared` to `ReopenPending`; the mutation path advances there after health verification. The service borrows the exact lease and returns Task 1's exclusive carrier with the result, lease, typed disposition, and exact repair-journal `ICovenantExclusivePostDispositionFinalizer`. The response uses `lifecycleCt`, never the HTTP-aborted token. After successful `CommitAndReopen`, the finalizer compare-and-swaps `ReopenPending -> Completed`; after successful proven `RollbackAndReopen`, it compare-and-swaps `ReopenPending -> Abandoned`; after successful `KeepClosed`, it verifies that `ReopenPending` remains unchanged. Failed disposition skips the finalizer and leaves `ReopenPending`. Finalizer failure also leaves `ReopenPending`, and neither failure permits a second disposition. The service never acquires or disposes a nested lease. Any crash or uncertainty leaves a journal for `ICovenantSchemaRepairStartupRecovery`. Reinitialize prepare uses `AcquireInstallationReadAsync`; reinitialize and rebuild use their short caller-owned write lease. Every lease remains owned by the response through JSON completion. Both return HTTP 202 with `LongRunningOperationDto`. Family reinitialize constructs the all-present requested-operation triplet only through Task 9's caller adapter. Index rebuild constructs the Plan 03 all-null server-generated request directly through the coordinator.

- [ ] **Step 4: Run the green maintenance tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSchemaRepairTests|FullyQualifiedName~CovenantSchemaRepairRecoveryTests|FullyQualifiedName~CovenantIndexRebuildRecoveryTests|FullyQualifiedName~CovenantFamilyReinitializeRecoveryTests|FullyQualifiedName~CovenantProtectedInventoryServiceTests|FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~CovenantManagedFileErasureKernelTests|FullyQualifiedName~CovenantMaintenanceEndpointTests|FullyQualifiedName~LongRunningOperationProgressContractsTests|FullyQualifiedName~LongRunningOperationRecoveryRegistryTests|FullyQualifiedName~LongRunningOperationCrashRecoveryTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: PASS, with one descriptor and one registered handler for each kind and no healthy availability during partial work.

- [ ] **Step 5: Refactor and verify recovery serialization**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRecovery|FullyQualifiedName~RecoveryRegistry"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. Recovery payloads are bounded, source-generated, and free of authority objects and sensitive content.

### Task 11: Add the thin HTTP Covenant CLI and stable mutation workflow

**Files:**

- Create: `src/RetroDownfall.Arcanum.Cli/Commands/TheForge/CovenantCommands.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.Covenant.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CovenantCliPayloads.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CliCommandShapeTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CliJsonContextCoverageTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CliHelpExampleTests.cs`

**Interfaces:**

- Consumes: Tasks 1, 4, 5, 6, and 10 HTTP contracts, Task 1 `CovenantExternalRetentionDisclosure`, plus existing `IConsoleDispatcher`, `IConfirmationPrompt`, `ICliInvocationContext`, Campaign resolver, and long-operation watch path.
- Produces: `CovenantCommands.Set`, `List`, `Show`, `Retire`, and `Doctor`, plus typed `ArcanumApiClient` methods and CLI-only payloads.

- [ ] **Step 1: Write failing CLI contract tests**

Add exact methods:

```csharp
[Fact]
public void Memory_covenant_tree_has_the_approved_verbs_and_options()

[Fact]
public async Task Set_reads_content_from_file_without_putting_it_in_argv_or_diagnostics()

[Fact]
public async Task Set_reads_redirected_or_interactive_stdin_when_file_is_absent()

[Theory]
[InlineData(true, false)]
[InlineData(false, true)]
public async Task Json_or_redirected_mutation_requires_explicit_yes(bool json, bool redirected)

[Fact]
public async Task Set_reuses_one_mutation_id_across_preflight_commit_and_transport_retry()

[Fact]
public async Task Retire_confirms_exact_revision_dependency_digest_and_scope_effects()

[Fact]
public async Task Doctor_reinitialize_confirms_every_loss_count_then_watches_the_returned_operation()

[Fact]
public async Task Doctor_reinitialize_uses_shared_external_retention_copy_and_provider_help_targets()

[Fact]
public async Task Doctor_reinitialize_writes_disclosure_counts_and_help_targets_before_requesting_confirmation()

[Fact]
public void Every_covenant_cli_payload_is_registered_with_cli_json_context()
```

- [ ] **Step 2: Run the red Covenant CLI tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~CliCommandShapeTests.Memory_covenant|FullyQualifiedName~CliJsonContextCoverageTests"
```

Expected: FAIL because `arcanum memory covenant` is not present.

- [ ] **Step 3: Implement the approved command tree and handlers**

Build this exact surface:

```text
memory covenant set <key> (--global | --campaign <id>) [--file <path>] --expected-revision <n> [--reactivate]
memory covenant list (--global | --campaign <id> | --all-scopes) [--lane <lane>] [--lifecycle <lifecycle>] [--query <text>]
memory covenant show <key> (--global | --campaign <id>) [--history]
memory covenant retire <key> (--global | --campaign <id>) --lane <lane> --expected-revision <n>
memory covenant doctor [--repair-schema | --rebuild-index | --reinitialize-family]
```

Use the repository-wide `-C` alias for `--campaign`. Resolve GUID, exact name, or unique prefix through the existing API client; ambiguous prefixes return candidates and exit 2 without mutation.

`CovenantCommands.Set` generates one mutation ID, reads content from `--file`, redirected stdin, or the existing console abstraction, calls prepare, prints compiled hash, framed bytes, and resolution effects, confirms, and commits with the same ID and returned token. `Retire` follows the same pattern and prints revision, dependent-head digest, Global and Proposed effects, exact affected count, and bounded examples.

`Doctor` permits exactly one mode. Repair is synchronous. Rebuild receives HTTP 202 and uses the existing operation watcher. Reinitialize prints catalog defects, lost rows, tainted local artifacts, managed files, nonrevocable disclosures, free space, and preserved core counts. Before invoking `IConfirmationPrompt`, it writes Task 1 `CovenantExternalRetentionDisclosure.DestructiveOperationText` byte-for-byte, then the receipt-backed possible-attempt count with exact or lower-bound semantics, then every resolved official provider-retention help target plus the operator-guide fallback. A shared ordered-event test proves all disclosure output precedes the prompt, and no operation starts on refusal. The CLI never describes a local provider-cache retention switch.

Implement typed API methods for every Covenant route in `ArcanumApiClient.Covenant.cs`. Keep all request serialization on `ArcanumJsonContext` and every CLI-only output on named `Covenant*Payload` records registered with `CliJsonContext`.

- [ ] **Step 4: Run the green Covenant CLI tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~ArcanumApiClientTests|FullyQualifiedName~CliCommandShapeTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~CliHelpExampleTests"
```

Expected: PASS. JSON mode writes one document to stdout, diagnostics stay on stderr, and transport retry preserves IDs.

- [ ] **Step 5: Refactor and verify CLI-only dependency direction**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CliContractTests|FullyQualifiedName~CliSurfaceTests|FullyQualifiedName~CliOperatorSurfaceTests|FullyQualifiedName~CliCompletionTests|FullyQualifiedName~CliSuggestionTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. `CovenantCommands` depends on `ArcanumApiClient` and never resolves Infrastructure.

### Task 12: Add Campaign path, Session binding, and offline host-tools CLI commands

**Files:**

- Create: `src/RetroDownfall.Arcanum.Cli/Commands/TheForge/CampaignPathCommands.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Commands/TheForge/SessionCampaignBindingCommands.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Commands/Security/HostProcessToolsSecurityCommand.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Security.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Spells.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.Covenant.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CovenantCliPayloads.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CampaignPathCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/SessionCampaignBindingCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/HostProcessToolsSecurityCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CliCommandShapeTests.cs`

**Interfaces:**

- Consumes: Tasks 7 and 8 typed HTTP clients, plus Plan 03 `IHostProcessToolsTransitionService.EnableAsync(HostProcessToolsTransitionRequest, CancellationToken)`, exact `HostProcessToolsTransitionRequest(Guid TransitionId)`, and its typed result/outcome carrying that same transition ID, for the sole offline command. Plan 03's service owns the installation lock, trusted security checks, database and OS-marker protocol, marker-pair join, compensation, and durable replay.
- Produces: `CampaignPathCommands`, `SessionCampaignBindingCommands`, and `HostProcessToolsSecurityCommand.Enable`.

- [ ] **Step 1: Write failing command and authority tests**

Add exact methods:

```csharp
[Fact]
public void Campaign_path_tree_has_status_register_update_repair_deregister_and_takeover()

[Fact]
public void Session_campaign_binding_tree_has_status_and_resolve()

[Fact]
public async Task Campaign_path_commands_never_probe_or_modify_the_local_filesystem()

[Fact]
public async Task Campaign_path_commands_display_opened_identity_digests_and_typed_marker_effect_before_confirmation()

[Fact]
public async Task Binding_resolve_confirms_immutable_global_or_campaign_choice()

[Fact]
public async Task Host_tools_enable_never_constructs_an_http_client_or_host()

[Fact]
public async Task Host_tools_enable_calls_transition_service_once_with_the_exact_transition_id()

[Fact]
public async Task Host_tools_enable_maps_completed_replay_blocked_and_refused_typed_outcomes()

[Fact]
public void Host_tools_enable_resolves_no_lock_environment_inventory_database_secret_or_marker_dependency()
```

- [ ] **Step 2: Run the red administration CLI tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathCommandTests|FullyQualifiedName~SessionCampaignBindingCommandTests|FullyQualifiedName~HostProcessToolsSecurityCommandTests"
```

Expected: FAIL because none of the three command families exists.

- [ ] **Step 3: Implement HTTP administration and the one offline exception**

Build the exact Campaign and Session surfaces from the approved CLI contract. Every path mutation calls status or prepare, prints server-opened path, old and new identity digests, marker effect, and drain impact, confirms, then calls apply. The CLI never opens a target path or reads marker bytes.

Binding status supports `--all` or `--session`. Resolve accepts one Session and exactly one of `--global` or `--campaign`, displays the immutable effect, confirms, and sends operation ID, stable digest, and token.

Build `security host-process-tools enable --yes` as the single local maintenance exception. The command generates or reuses one random transition ID, then invokes only `IHostProcessToolsTransitionService.EnableAsync(new HostProcessToolsTransitionRequest(transitionId), cancellationToken)`. `HostProcessToolsTransitionRequest(Guid TransitionId)` is the sole durable identity, and `HostProcessToolsTransitionResult.TransitionId` must equal it on every completed, replayed, blocked, or refused result. The command maps the result's closed `Completed`, `AlreadyCompleted`, `PendingManualRemediation`, or `Refused` outcome to the typed CLI payload, transition identity, restart indication, and existing exit codes. It does not acquire `ArcanumMaintenanceLock`, inspect edition or environment configuration, inventory protected state, open the Grimoire, read or write a secret or marker, call `HostProcessToolsMarkerPairJoiner`, perform compensation, or reproduce Plan 03's replay state machine. The Plan 03 service is the sole owner of those steps and delegates pair classification to the shared joiner, as does Plan 03 startup. Task 17 independently consumes that same joiner before attested compare-deletion.

Every mutating command requires `--yes` in JSON, plain automation, or redirected modes, follows existing exit codes, and writes one source-generated JSON document when requested.

- [ ] **Step 4: Run the green administration CLI tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathCommandTests|FullyQualifiedName~SessionCampaignBindingCommandTests|FullyQualifiedName~HostProcessToolsSecurityCommandTests|FullyQualifiedName~CliCommandShapeTests"
```

Expected: PASS. Path and binding mutations are HTTP-only, and the offline command never constructs a host or API client.

- [ ] **Step 5: Refactor and verify offline isolation**

Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: both commands exit zero. `HostProcessToolsSecurityCommand` has only the exact Plan 03 transition-service dependency for offline maintenance and contains no duplicated security transition logic.

### Task 13: Add the disabled-by-default configuration and Compendium disclosure

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Serialization/ConfigurationJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFeatureConfigurationPublisher.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptor.cs`
- Modify: `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`
- Modify: `src/RetroDownfall.Compendium.Ux/Views/GenericSettingsSectionView.axaml.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Configuration/ArcanumSettingsBindingTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Configuration/ConfigurationSurfaceContractTests.cs`
- Test: `tests/RetroDownfall.Compendium.Tests/Compendium/SettingDescriptorCoverageTests.cs`
- Test: `tests/RetroDownfall.Compendium.Tests/Compendium/SettingDescriptorParityTests.cs`
- Test: `tests/RetroDownfall.Compendium.Tests/Compendium/ConfigurationStoreSmokeTests.cs`
- Test: `tests/RetroDownfall.Compendium.Tests/Compendium/ConfigurationViewModelNotificationTests.cs`
- Test: `tests/RetroDownfall.Compendium.Tests/Compendium/CovenantDisclosureDescriptorTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantFeatureConfigurationPublisherTests.cs`

**Interfaces:**

- Consumes: Task 1 `CovenantExternalRetentionDisclosure` and its typed known-provider/fallback help targets, existing `FeatureSettings`, generated configuration binder, `ConfigurationJsonContext`, generic Compendium settings editor, `IOptionsMonitor<ArcanumSettings>`, and Plan 01's `CovenantAvailability` publisher.
- Produces: `FeatureSettings.Covenant`, editable `features.covenant` descriptor, Compendium rendering of the shared disclosure and real provider-retention help targets, and live availability feature publication without restart.

- [ ] **Step 1: Write failing binding and descriptor tests**

Add exact methods:

```csharp
[Fact]
public void Covenant_feature_defaults_false_and_binds_true_through_generated_configuration()
{
    Assert.False(new FeatureSettings().Covenant);
    Assert.True(Bind("Arcanum:Features:Covenant", "true").Features.Covenant);
}

[Fact]
public void Covenant_descriptor_warns_that_eligible_context_is_sent_on_every_provider_attempt()
{
    SettingDescriptor descriptor = Assert.Single(
        SettingDescriptors.All,
        item => item.Key == "features.covenant");

    Assert.Equal(SettingKind.Bool, descriptor.Kind);
    Assert.Contains("every provider attempt", descriptor.Description, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Covenant_descriptor_uses_shared_copy_and_links_known_provider_retention_documentation()

[Fact]
public void Unknown_or_self_hosted_provider_falls_back_to_providers_page_and_operator_guide()

[Fact]
public void Covenant_disclosure_and_help_actions_render_before_the_enable_toggle()

[Fact]
public async Task Configuration_change_publishes_feature_state_and_advances_availability_generation()

[Fact]
public async Task Disable_between_provider_attempts_is_visible_without_database_or_secret_store_io()
```

Update `SettingDescriptorCoverageTests.Editable_descriptor_count_matches_the_documented_total` to expect 162 for this implementation slice. Plan 05 updates the owning documentation to the same value.

- [ ] **Step 2: Run the red configuration tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumSettingsBindingTests.Covenant|FullyQualifiedName~ConfigurationSurfaceContractTests|FullyQualifiedName~CovenantFeatureConfigurationPublisherTests"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --filter "FullyQualifiedName~SettingDescriptorCoverageTests|FullyQualifiedName~SettingDescriptorParityTests.Covenant|FullyQualifiedName~CovenantDisclosureDescriptorTests"
```

Expected: FAIL because `FeatureSettings.Covenant` and `features.covenant` do not exist.

- [ ] **Step 3: Add the mutable property and descriptor**

Add exactly:

```csharp
public bool Covenant { get; set; }
```

Do not use `init`. `ConfigurationJsonContext` already owns `FeatureSettings`; add no duplicate type entry if the generated metadata includes the new property automatically.

Add one `SettingDescriptor` under Feature opt-ins. Extend the descriptor with an optional typed internal `HelpRoute`, rendered by the generic settings view as explicit help actions:

```csharp
new(
    "features.covenant",
    ConfigSection.Features,
    "Use The Covenant",
    CovenantExternalRetentionDisclosure.EnablementText,
    SettingKind.Bool,
    HelpRoute: SettingHelpRoute.ConfiguredProviderRetention),
```

Use Task 1 `CovenantExternalRetentionDisclosure.EnablementText` without a Compendium-owned paraphrase. For this descriptor, `GenericSettingsSectionView` renders the full disclosure text and every resolved help action before the enable toggle in logical and visual order. The toggle does not become interactive until those elements have been constructed. `SettingHelpRoute.ConfiguredProviderRetention` calls the shared resolver against the current configured providers. An exact recognized OpenAI API endpoint, Codex CLI Familiar, or Claude Code Familiar opens the corresponding official external retention/data-use page from Task 1 through the Avalonia top-level URI launcher. Unknown, proxy, and self-hosted providers render a typed action that navigates to Compendium's configured Providers page plus the visible Plan 05 operator-guide target. Verify exact shared copy, control order, every known URI, the unknown/self-hosted fallback, generic editor load, save, preservation, and view-model notification. No UI control or descriptor changes provider prompt-cache retention, and no typed provider capability is invented in issue #74.

Register one singleton `CovenantFeatureConfigurationPublisher`. At startup and on each `IOptionsMonitor<ArcanumSettings>.OnChange`, read the generated `Features.Covenant` Boolean and publish it through the same Plan 01 `CovenantAvailability` instance. Serialize callbacks so a stale callback cannot overwrite a later value, dispose the subscription at shutdown, and never read SQLCipher or the secret store. A disable publication becomes visible at the next in-memory gate read; affected live turns abort at their required pre-dispatch revalidation.

- [ ] **Step 4: Run the green configuration tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumSettingsBindingTests|FullyQualifiedName~ConfigurationSurfaceContractTests|FullyQualifiedName~CovenantFeatureConfigurationPublisherTests"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --filter "FullyQualifiedName~SettingDescriptorCoverageTests|FullyQualifiedName~SettingDescriptorParityTests|FullyQualifiedName~ConfigurationStoreSmokeTests|FullyQualifiedName~ConfigurationViewModelNotificationTests|FullyQualifiedName~CovenantDisclosureDescriptorTests"
```

Expected: PASS. Missing configuration remains false and save/load preserves an explicit true value.

- [ ] **Step 5: Refactor and verify source-generated binding**

Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: both commands exit zero. No manual JSON key alias or reflection binder is introduced.

### Task 14: Make physical backup disclosure-aware and include all protected state

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantBackupDisclosureBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCreateRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantBackupDisclosureTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupDatabaseSnapshotterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupCreateRecoveryHandlerTests.cs`

**Interfaces:**

- Consumes: Plan 03 `DisclosureGroupCommitter`, operation disclosure subjects, destination `EncryptedBackup`, nonrevocable receipts, and stable backup effect digest; Plan 02's all-scope `CovenantInstallationReadLease` returned by `AcquireInstallationReadAsync`.
- Produces: `CovenantBackupDisclosureBoundary.BeforeSnapshotReadAsync` and `BeforeArchiveWriteAsync`, keyed by backup operation ID and physical attempt ordinal.

- [ ] **Step 1: Write failing disclosure-order tests**

Add exact methods:

```csharp
[Fact]
public async Task Create_commits_snapshot_read_receipt_before_snapshot_reads_page_one()

[Fact]
public async Task Create_commits_archive_write_receipt_before_first_output_byte()

[Fact]
public async Task Failed_snapshot_or_archive_attempt_retains_its_committed_receipt()

[Fact]
public async Task Retry_allocates_a_new_physical_attempt_ordinal()

[Fact]
public async Task Full_backup_contains_canonical_covenant_tainted_artifacts_and_labels()

[Fact]
public async Task Backup_holds_the_operation_lease_through_snapshot_and_archive_completion()

[Fact]
public async Task Full_backup_uses_one_installation_read_lease_and_no_nested_scoped_lease()
```

- [ ] **Step 2: Run the red backup tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBackupDisclosureTests|FullyQualifiedName~BackupServiceTests|FullyQualifiedName~BackupDatabaseSnapshotterTests"
```

Expected: FAIL because backup can begin reading and writing without a durable disclosure acknowledgement.

- [ ] **Step 3: Add the two pre-effect durability barriers**

Implement:

```csharp
internal interface ICovenantBackupDisclosureBoundary
{
    Task<CovenantDisclosureReceipt> BeforeSnapshotReadAsync(Guid operationId, CovenantDigest backupIdentity, CovenantDigest destinationIdentity, CancellationToken cancellationToken);
    Task<CovenantDisclosureReceipt> BeforeArchiveWriteAsync(Guid operationId, CovenantDigest backupIdentity, CovenantDigest destinationIdentity, CancellationToken cancellationToken);
}
```

Each call queues frozen `Arcanum.Covenant.BackupDisclosureEffect.v1` fields to `DisclosureGroupCommitter`, waits for commit acknowledgement, and returns the assigned physical attempt. Commit failure prevents the corresponding read or write. Cancellation after queue sealing may retain a conservative receipt and suppresses the effect.

In `BackupService.CreateAsync`, call Plan 02 `AcquireInstallationReadAsync` before the full-backup inventory is frozen and retain that exact `CovenantInstallationReadLease` through snapshot completion and the last archive byte. Every full-scope inventory and snapshot read accepts it as `ICovenantSnapshotReadLease`; no backup layer acquires a nested Global or Campaign lease. Call `BeforeSnapshotReadAsync` immediately before `BackupDatabaseSnapshotter.CreateAsync`. Call `BeforeArchiveWriteAsync` immediately before the archive codec can write its first byte. Dispose the installation lease only after the archive writer completes or fails. Preserve the operation subject and acknowledged receipt identities in `BackupOperationCheckpoint` so crash recovery closes or resumes the same subject without duplicating a known-unattempted effect; a resumed process acquires a fresh `CovenantInstallationReadLease` through `AcquireInstallationReadAsync` and never persists a live lease.

Keep full physical Grimoire backup inclusive. `BackupInventoryPlanner` must not filter Covenant canonical tables, core sensitivity and disclosure tables, tainted Arcanum-owned artifacts, or required schema metadata from `BackupScope.Full`.

- [ ] **Step 4: Run the green backup tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBackupDisclosureTests|FullyQualifiedName~BackupServiceTests|FullyQualifiedName~BackupDatabaseSnapshotterTests|FullyQualifiedName~BackupCreateRecoveryHandlerTests"
```

Expected: PASS. No physical backup attempt crosses either effect boundary before receipt commit.

- [ ] **Step 5: Refactor and verify backup compatibility**

Keep `BackupJsonContext` as the only serializer for backup checkpoints. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~BackupArchiveCodecTests|FullyQualifiedName~BackupManifestValidationTests|FullyQualifiedName~BackupCreateRecoveryHandlerTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero and existing archive format validation remains deterministic.

### Task 15: Reconcile protected restore state and enforce selective-transfer policy

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreEffectDigestCalculator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreManagedAuthoritySanitizer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationCapability.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationSession.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantSqliteConnectionInitializer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionInitializer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantAuthorityStateJoiner.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantDisclosureStateJoiner.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournal.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalInstallationIdentityProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalKeyProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAnchorStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/IBackupRestoreStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/CampaignEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Backup.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantBackupRestoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupCovenantRestoreReconcilerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreManagedAuthoritySanitizerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantAuthorityStateJoinerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantDisclosureStateJoinerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantSelectiveTransferTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreJournalTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreJournalAuthenticationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreDatabaseWorkerTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupSessionImporterTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseHostedServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/CampaignEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/BackupRestoreCommandTests.cs`

**Interfaces:**

- Consumes: Plan 01 staged three-tier schema convergence, central connection initializer, exact restore-staging managed-authority authorization, and content-free restored-authority tombstones.
- Consumes: Plan 03 authority snapshots, Task 2 `ICovenantAuthorityTransitionPublisher`, sensitivity labels, turn-claim terminalization, disclosure store, `ICovenantDisclosureWriterLifecycle`, nonserializable `ImportedSessionSourceLease`, and `IProtectedArtifactTransferStore.CommitImportedSessionAsync(ImportedSessionTransferRequest, ImportedSessionSourceLease, CovenantProtectedTransferLease, CancellationToken) -> Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>`; Task 1 `CovenantExternalRetentionDisclosure`; Task 7 `ICampaignPathMarkerLifecycle`; Plan 02 operation gate, atomically acquired `CovenantProtectedTransferLease`, and `CovenantDisclosureStateAlgebra`; and Plan 01 authority/disclosure tables.
- Produces: the sole Core `IBackupRestoreEffectDigestCalculator`, destination-monotonic authority and disclosure semilattice joiners, explicit protected-state restore mode, Session Campaign mappings, staged Covenant reconciliation before swap, authenticated V2 restore-journal and anchor services, and exact authority-aware `IBackupRestoreStartupRecovery` integrated into pre-readiness host and CLI bootstrap.
- Produces: the sole internal static `BackupRestoreManagedAuthoritySanitizer`, sealed unpublished-candidate capability, sealed capability-owned transaction session, and checked content-free sanitation receipt.

```csharp
internal enum BackupRestorePhysicalRecoveryOutcome : byte
{
    NoActiveJournal = 1,
    TopologyReady = 2,
    KeptClosed = 3
}

internal enum BackupRestoreStartupRecoveryOutcome : byte
{
    NoActiveJournal = 1,
    RecoveredReady = 2,
    KeptClosed = 3
}

internal interface IBackupRestoreStartupRecovery
{
    Task<Result<BackupRestorePhysicalRecoveryOutcome>> RecoverPhysicalTopologyBeforeDatabaseAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);

    Task<Result<BackupRestoreStartupRecoveryOutcome>> RecoverAuthorityBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);
}

internal sealed record BackupRestoreManagedAuthoritySanitizationReceipt(
    Guid RestoreOperationId,
    ulong ManagedWriteIntentCount,
    ulong LocalErasureWorkItemCount,
    ulong RemovedLabelCount,
    CovenantDigest TombstoneVectorDigest);

public sealed record BackupRestoreEffectDigestInput(
    CovenantDigest ArchiveManifestDigest,
    CovenantDigest ArchivePhysicalIdentityDigest,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    CovenantDigest DestinationRootIdentityDigest,
    BackupRestoreConflictMode ConflictMode,
    BackupProtectedStateMode ProtectedStateMode,
    CovenantDigest PathMappingVectorDigest,
    bool RestoreMasterApiKey,
    bool CreateSafetyBackup);

public interface IBackupRestoreEffectDigestCalculator
{
    Result<CovenantDigest> Compute(BackupRestoreEffectDigestInput input);
}

internal enum BackupRestoreJournalAnchorState : byte
{
    Active = 1,
    Closed = 2
}

internal enum BackupRestoreNodeKind : byte
{
    Directory = 1,
    RegularFile = 2
}

internal enum BackupRestoreNodePresence : byte
{
    Absent = 1,
    Present = 2
}

internal sealed record BackupRestoreDurableNodeIdentityV1(
    string CanonicalParentPath,
    CovenantDigest ParentPhysicalIdentityDigest,
    string ChildLeaf,
    BackupRestoreNodeKind NodeKind,
    BackupRestoreNodePresence Presence,
    CovenantDigest? NodePhysicalIdentityDigest,
    CovenantDigest? ContentDigest);

internal sealed record BackupRestoreMarkerCleanupCheckpointV1(
    byte Version,
    Guid OwnerOperationId,
    CovenantExclusiveOperation OwnerOperation,
    CovenantDigest OwnerEffectDigest,
    ImmutableArray<Guid> OrderedIntentIds,
    ulong IntentCount,
    CovenantDigest IntentVectorDigest);

internal sealed record BackupRestoreJournalPayloadV2(
    CovenantExclusiveRecoveryOwner Owner,
    BackupRestoreConflictMode ConflictMode,
    BackupRestorePhase Phase,
    CovenantDigest RestoreRequestDigest,
    BackupRestoreDurableNodeIdentityV1 LiveRoot,
    BackupRestoreDurableNodeIdentityV1 StagedRoot,
    BackupRestoreDurableNodeIdentityV1 DisplacedRoot,
    BackupRestoreDurableNodeIdentityV1 ArchiveSource,
    BackupRestoreDurableNodeIdentityV1? SafetyBackup,
    BackupRestoreMarkerCleanupCheckpointV1? MarkerCleanup);

internal sealed record BackupRestoreJournalEnvelopeV2(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

internal sealed record BackupRestoreJournalAnchorV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest EnvelopeDigest,
    CovenantDigest JournalLocationDigest,
    BackupRestoreJournalAnchorState State);
```

The payload's `MarkerCleanup` is null only from the initial owner journal through `Validate`, before staged marker preparation commits. It becomes nonnull before advancing to `SafetyPoint` and is mandatory through `Commit`, `Reconcile`, `Cleanup`, and every displacement effect. This closed shape lets a nonzero restore persist its owner before child intent IDs exist. Once nonnull it never returns to null or changes owner identity. The checkpoint has `Version=1`, `OwnerOperation=BackupRestore`, a nonempty operation ID, exact 32-byte effect and vector digests, and a nondefault canonically ordered deep-copied vector bounded by the approved Campaign maximum. `IntentCount` uses checked `u64` arithmetic and equals the vector length. The vector may be empty only for the authenticated zero-marker arm. That arm has `IntentCount=0`, `OrderedIntentIds=ImmutableArray<Guid>.Empty`, and the literal `Arcanum.Covenant.BackupRestore.MarkerIntentVector.v1` digest of checked `u64` zero with no item bytes. Null is unprepared, while nonnull empty is proven zero. Any phase-shape violation, default vector, duplicate or empty item ID, wrong operation kind, malformed digest, count mismatch, or a checkpoint larger than the guarded-root journal bound fails closed.

`BackupRestoreEffectDigestInput`, `IBackupRestoreEffectDigestCalculator`, and its sole Core implementation `BackupRestoreEffectDigestCalculator` apply only to `ReplaceInstallation` or `NewProfileRoot`; selective Session import uses Plan 03's separate transfer-effect digest. `Compute` returns `Result<CovenantDigest>` and validates null input, empty installation identity, unsupported conflict or protected-state values, and every malformed digest before hashing. `DestinationRootIdentityDigest` is the canonical digest of the no-follow parent identity, child leaf, and present or absent root evidence, so a changed destination cannot replay the same owner. `PathMappingVectorDigest` is the domain-separated digest of the normalized, kind-then-source-then-destination ordered mapping vector with a checked `u64` count. The SHA-256 preimage domain is `Arcanum.Covenant.BackupRestore.Effect.v1`, followed by archive-manifest digest, archive physical-identity digest, profile-namespace digest, RFC-4122 installation UUID bytes, destination-root identity digest, one-byte conflict code, a length-prefixed ASCII protected-state wire value, path-mapping vector digest, and the `RestoreMasterApiKey` and `CreateSafetyBackup` boolean bytes in that exact order. Dry-run and confirmation transport flags are excluded because they authorize no effect. The resulting digest is the sole `restoreEffectDigest` producer used by initial acquisition, the authenticated journal, marker-child rows, and recovery comparison. Initial acquisition and every retry must receive a successful `Compute` result and compare it byte-for-byte with the authenticated journal before adoption; a failure performs no gate, journal, staging, or filesystem effect.

Upgrade the existing plain journal to the exact V2 authenticated envelope above. Before database open, the configured profile root's retained no-follow parent identity plus bounded child leaf produce `ProfileNamespaceDigest = SHA-256("Arcanum.BackupRestore.ProfileNamespace.v1" || ParentPhysicalIdentityDigest || UInt16BE(LeafUtf8Length) || LeafUtf8)`. The digest uses no path text and is recomputed from the same parent handle on every startup. `ArcanumCredentialIdentity` owns three account-prefix methods whose suffix is the 64-character lowercase hex profile-namespace digest: `backup-restore-journal-installation-`, `backup-restore-journal-key-`, and `backup-restore-journal-anchor-`. A malformed suffix or account from another profile is never consulted.

`BackupRestoreJournalInstallationIdentityProvider` is the sole owner of the first account. On clean normal bootstrap, after core authority becomes readable and only when no active restore evidence exists, it seeds a missing account from the exact database `covenant_authority_state.InstallationId` and verifies readback under the caller-held installation lock. An existing value must be one canonical uppercase `D` UUID and equal the database row before optional services or readiness. `NewProfileRoot` creates one random destination installation ID in this account before its first journal or staged effect and writes that same identity into the staged core authority row. `ReplaceInstallation` requires the existing external and database identities to agree before new work. Physical recovery reads the external identity before database open, binds it to the envelope and anchor, and the later core precheck compares it with the recovered live row. Ordinary restore, reset, family reinitialize, credential cleanup, and key rotation preserve it. Only attested full installation reset removes it after proving no active restore owner, and the next clean installation generates a new identity. This gives each profile an independent pre-database binding and makes cross-profile journal, key, anchor, or staged-root replay fail before topology mutation.

`BackupRestoreJournalKeyProvider` is the sole accessor for the namespaced key account. It stores one random installation-stable 256-bit AES-GCM key as canonical unpadded base64url of exactly 32 decoded bytes, generated only while the caller-held installation lock is proven and before new restore work. Recovery has a separate open-existing path and never creates or substitutes a missing key. The provider returns one nonserializable single-take key lease only to the authenticator, never a string or serializable byte array to a coordinator. Decode rejects padding, noncanonical alphabet, and every other byte length, and the lease zeroes decoded key bytes immediately after the bounded AES-GCM operation or disposal. The namespaced anchor account stores the bounded V1 anti-rollback anchor. Neither ordinary credential cleanup, Covenant reset, family reinitialize, nor restore removes these accounts. The attested full-installation-reset path may remove all three only after proving that no active restore owner remains.

`BackupRestoreJournalAuthenticator` registers `BackupRestoreJournalPayloadV2`, `BackupRestoreJournalEnvelopeV2`, `BackupRestoreJournalAnchorV1`, `BackupRestoreDurableNodeIdentityV1`, nullable `BackupRestoreMarkerCleanupCheckpointV1`, every nested enum, and each required immutable vector in `BackupJsonContext`. Payload, envelope, and OS-secret anchor decoding is bounded, source-generated, canonical, strict about unknown fields and versions, and has no reflection fallback. The encrypted payload contains the complete immutable `BackupRestore` recovery owner, restore-request digest, phase, optional pre-preparation or exact prepared marker checkpoint, and no-follow durable identity evidence for the live, staged, displaced, archive, and optional safety-backup nodes. Directory slots require `NodeKind=Directory`; archive slots require `RegularFile`. A present node requires an exact 32-byte same-handle physical-identity digest, an absent node requires null, and only a present regular file may carry a content digest. Canonical parent paths are bounded absolute paths, leaves are one bounded child name with no separators or traversal, and every parent physical identity is captured from the retained no-follow parent handle. Every phase freezes the expected present or absent shape for all three topology roots. Physical recovery reopens the recorded parent, verifies its physical identity, opens only the recorded child without following links, and compares the node identity before any rename or deletion. It never authorizes a filesystem effect from a path alone.

Envelope `Version` is exactly 2. `ProfileNamespaceDigest` is exactly 32 bytes and equals the account namespace and retained-parent recomputation. `InstallationId` and `OperationId` are nonempty and equal the namespaced OS-secret installation identity and the payload owner. `Revision` uses checked positive `u64` arithmetic. The nonce is exactly 12 random bytes and the authentication tag is exactly 16 bytes, both encoded as canonical unpadded base64url. AES-256-GCM additional authenticated data is the ASCII domain `Arcanum.BackupRestore.JournalEnvelope.v2`, followed in fixed binary order by version, profile-namespace digest, RFC-4122 installation and operation UUID bytes, revision, and the exact 32-byte previous-envelope digest. Ciphertext is the bounded source-generated UTF-8 payload. `EnvelopeDigest` is SHA-256 over the complete canonical envelope fields, including ciphertext and tag, under `Arcanum.BackupRestore.JournalEnvelopeDigest.v2`.

`BackupRestoreJournalAnchorStore` uses the namespaced OS-secret boundary and one installation-lock-serialized read-compare-write-readback transition. `JournalLocationDigest` is SHA-256 under `Arcanum.BackupRestore.JournalLocation.v1` over profile-namespace digest, RFC-4122 installation and operation UUID bytes, exact retained guarded-parent physical-identity digest, and the length-prefixed canonical journal child leaf. It contains no path text. Discovery opens each canonical candidate parent without following links, recomputes that digest from the same handle, and requires exactly one anchor match before reading the child. A new operation first writes `Active` revision zero with the zero previous digest and that journal-location digest. Every advance writes a same-directory owner-only temporary envelope, fsyncs it, atomically replaces the journal, fsyncs the parent, rereads and authenticates it, then rereads the anchor, requires revision `n` and the expected digest, writes revision `n+1` plus the new envelope digest, and verifies an exact readback while the same installation lock remains held. Recovery accepts only an exact anchor match or one envelope revision ahead whose previous digest equals the anchor digest; the latter is the single crash window after journal fsync and before anchor advancement, and recovery closes it through the same checked write and readback before any effect. An older revision, a jump larger than one, a changed profile namespace, location, operation, installation, previous digest, or envelope digest is an authenticated rollback or replay failure. Terminal cleanup first authenticates a terminal journal and advances the anchor to `Closed` through the same checked write and readback, then deletes and parent-fsyncs the journal. The closed anchor remains as the anti-replay tombstone and may be replaced only by a different new operation after journal absence is proved. Proven absence of an active anchor, canonical journal or lookalike, and staging-index entry returns `NoActiveJournal`; an existing external installation identity or unused key is allowed. A fresh or exact legacy profile with no external identity seeds it only after core convergence. A missing installation identity or key with any active anchor, journal, canonical lookalike, or staging-index evidence is a typed manual blocker. A missing active journal, active anchor without its unique canonical location, unauthenticated lookalike, unknown version, truncated encoding, tag failure, anchor mismatch, wrong installation or profile, or replay from another operation is likewise a blocker, never absence and never deletion authority.

- [ ] **Step 1: Write failing restore and selective-transfer tests**

Add exact methods:

```csharp
[Fact]
public async Task Restore_with_protected_state_requires_explicit_destructive_mode()

[Fact]
public async Task Restore_assigns_fresh_dataset_envelope_and_accelerator_identity_before_reopen()

[Fact]
public async Task Restore_preserves_destination_taint_and_monotonically_joins_disclosure_evidence()

[Fact]
public async Task Restore_strips_path_identities_marker_intents_and_file_deletion_authority()

[Theory]
[InlineData(ManagedFileWriteIntentPhase.Prepared)]
[InlineData(ManagedFileWriteIntentPhase.TempCreated)]
[InlineData(ManagedFileWriteIntentPhase.ParentFsynced)]
[InlineData(ManagedFileWriteIntentPhase.AdoptedAndLabeled)]
[InlineData(ManagedFileWriteIntentPhase.ManualNonrevocable)]
public async Task Restore_sanitizes_every_managed_write_phase_to_a_content_free_tombstone(
    ManagedFileWriteIntentPhase phase)

[Theory]
[InlineData(LocalErasureWorkItemState.Prepared)]
[InlineData(LocalErasureWorkItemState.DeletionVerified)]
[InlineData(LocalErasureWorkItemState.Completed)]
[InlineData(LocalErasureWorkItemState.ManualBlocker)]
public async Task Restore_sanitizes_every_local_erasure_state_without_a_filesystem_call(
    LocalErasureWorkItemState state)

[Fact]
public async Task Restore_sanitizes_adopted_ownership_and_removes_its_exact_label_atomically()

[Fact]
public async Task Restore_sanitation_rollback_retains_all_source_authority_and_no_tombstone()

[Fact]
public async Task Restore_sanitation_capability_rejects_live_wrong_candidate_and_reused_connections()

[Fact]
public async Task Restore_sanitizer_enables_only_code_11_on_the_capability_connection_and_disposes_before_commit()

[Fact]
public async Task Restore_sanitation_initializer_borrows_only_the_same_active_run_identity_without_connection_export()

[Fact]
public void Restore_sanitation_run_identity_is_minted_only_by_the_taken_capability_and_never_exported()

[Fact]
public async Task Restore_sanitation_capability_runs_only_the_exact_static_sanitizer_once()

[Fact]
public void Restore_sanitation_capability_exposes_no_connection_transaction_command_delegate_or_general_callback()

[Fact]
public void Restore_sanitation_session_is_sealed_nonresolvable_and_has_only_the_eight_typed_operations()

[Fact]
public async Task Restore_sanitation_session_rejects_foreign_reordered_repeated_and_post_transaction_use()

[Fact]
public async Task Restore_sanitation_inserts_all_source_tombstones_before_linked_local_tombstones()

[Fact]
public async Task Restore_sanitation_deletes_local_rows_then_labels_then_source_rows_before_verification()

[Fact]
public async Task Restore_sanitation_failure_rolls_back_before_capability_scope_and_session_can_escape()

[Fact]
public async Task Restore_tombstones_cannot_reconstruct_root_revision_segments_or_physical_identity()

[Fact]
public async Task Restore_validation_and_destination_openers_run_only_after_managed_authority_is_absent()

[Fact]
public async Task Restore_delegates_marker_cleanup_to_the_single_shared_codec_lifecycle()

[Fact]
public async Task Restore_one_global_owner_journals_and_recovers_distinct_marker_intents_for_multiple_Campaigns()

[Fact]
public async Task Restore_terminalizes_pending_and_begun_claims_as_restored_interrupted()

[Fact]
public async Task Source_tainted_archive_with_protected_state_is_refused_by_default()

[Fact]
public async Task Source_tainted_archive_can_only_continue_through_explicit_confirmed_purge()

[Fact]
public async Task Restore_marks_fts_dirty_and_never_trusts_archived_projection_state()

[Fact]
public async Task Plaintext_session_export_rejects_any_tainted_artifact_atomically()

[Fact]
public async Task Campaign_export_contains_no_covenant_or_tainted_artifact_fields()

[Fact]
public async Task Campaign_export_reports_typed_covenant_and_tainted_exclusion_counts()

[Fact]
public async Task Session_and_campaign_export_hold_conditional_read_lease_through_archive_serialization()

[Fact]
public async Task Campaign_bound_session_import_requires_explicit_existing_destination_mapping()

[Fact]
public async Task Imported_guards_are_committed_imported_and_no_turn_claim_is_copied()

[Fact]
public async Task Selective_import_uses_protected_transfer_store_without_fabricated_claim_receipt_or_replay_authority()

[Fact]
public async Task Selective_import_passes_source_snapshot_and_no_follow_attachment_lease_to_transfer_store()

[Fact]
public async Task Selective_import_acquires_and_passes_one_atomic_compound_destination_lease()

[Fact]
public async Task Selective_import_omitted_tainted_artifact_cannot_bypass_store_owned_manifest_scan()

[Fact]
public async Task Repeated_selective_import_is_idempotent_by_import_operation_identity()

[Fact]
public async Task Selective_import_racing_campaign_delete_has_one_serial_destination_outcome()

[Fact]
public async Task Selective_import_racing_reset_has_one_serial_destination_outcome()

[Fact]
public async Task Restore_confirmation_uses_shared_external_retention_copy_and_help_targets()

[Fact]
public async Task Restore_confirmation_writes_disclosure_counts_and_help_targets_before_prompt_or_staging()

[Fact]
public async Task Startup_restore_recovery_keeps_admission_closed_until_health_authority_and_writer_publication()

[Fact]
public async Task Startup_restore_recovery_borrows_the_existing_installation_lock_without_reacquisition()

[Theory]
[InlineData(BackupRestorePhysicalCrashPoint.BeforeLiveRootDisplacement)]
[InlineData(BackupRestorePhysicalCrashPoint.AfterLiveRootRenamedToRollback)]
[InlineData(BackupRestorePhysicalCrashPoint.AfterStagedRootRenamedToLive)]
[InlineData(BackupRestorePhysicalCrashPoint.AfterLiveParentFsync)]
public async Task Startup_restore_physical_recovery_converges_one_live_root_before_any_database_precheck(
    BackupRestorePhysicalCrashPoint crashPoint)

[Fact]
public async Task Startup_restore_authority_recovery_runs_after_catalog_precheck_and_before_other_database_recovery()

[Fact]
public async Task Startup_restore_recovery_rolls_back_only_a_proven_pre_swap_failure()

[Fact]
public async Task Startup_restore_recovery_keeps_closed_on_every_post_swap_uncertainty()

[Fact]
public async Task Selective_import_passes_source_evidence_digest_and_exact_bounded_manifest_counts()

[Fact]
public async Task Restore_cleanup_children_are_committed_to_staged_core_before_live_root_swap()

[Fact]
public async Task Restore_parent_journal_authenticates_exact_marker_child_vector_before_displacement()

[Fact]
public void Restore_effect_digest_has_one_domain_preimage_and_changes_with_every_destructive_input()

[Fact]
public void Restore_effect_digest_rejects_null_malformed_and_unsupported_inputs_before_hashing()

[Fact]
public async Task Restore_with_zero_active_markers_commits_an_authenticated_zero_child_checkpoint()

[Fact]
public async Task Restore_zero_marker_recovery_rejects_an_unexpected_child_and_reopens_through_the_no_op_finalizer()

[Theory]
[InlineData("ciphertext")]
[InlineData("tag")]
[InlineData("profile-namespace")]
[InlineData("installation")]
[InlineData("operation")]
[InlineData("revision")]
[InlineData("previous-digest")]
[InlineData("live-root")]
[InlineData("staged-root")]
[InlineData("displaced-root")]
[InlineData("marker-checkpoint")]
public async Task Restore_journal_tamper_blocks_before_topology_mutation(string field)

[Fact]
public async Task Restore_journal_rejects_unknown_version_wrong_key_cross_installation_replay_and_rollback()

[Fact]
public async Task Restore_journal_rejects_cross_profile_key_anchor_envelope_and_staged_root_replay()

[Fact]
public void Restore_journal_key_requires_canonical_unpadded_base64url_exactly_32_bytes_and_zeroizes_after_use()

[Fact]
public async Task Startup_restore_recovery_without_key_anchor_journal_or_staging_evidence_returns_no_active_journal()

[Fact]
public async Task Startup_restore_recovery_with_external_identity_but_no_active_evidence_returns_no_active_journal()

[Fact]
public async Task Startup_seeds_or_compares_the_namespaced_external_installation_identity_after_core_convergence()

[Fact]
public void Restore_profile_namespace_binds_retained_parent_identity_and_root_leaf_without_path_authority()

[Fact]
public async Task Restore_journal_location_digest_binds_retained_parent_identity_leaf_installation_and_operation()

[Fact]
public void Restore_journal_payload_envelope_anchor_and_nested_types_have_complete_AOT_context_coverage()

[Theory]
[InlineData(BackupRestorePhase.Authenticate)]
[InlineData(BackupRestorePhase.Inventory)]
[InlineData(BackupRestorePhase.Capacity)]
[InlineData(BackupRestorePhase.Stage)]
[InlineData(BackupRestorePhase.Migrate)]
[InlineData(BackupRestorePhase.RemapPaths)]
[InlineData(BackupRestorePhase.RewrapSecrets)]
[InlineData(BackupRestorePhase.Validate)]
public async Task Restore_initial_journal_allows_only_the_pre_preparation_null_marker_shape(
    BackupRestorePhase phase)

[Fact]
public async Task Restore_requires_nonnull_zero_or_nonzero_marker_checkpoint_before_safety_point_or_displacement()

[Theory]
[InlineData("missing-child")]
[InlineData("extra-child")]
[InlineData("reordered-child")]
[InlineData("wrong-owner")]
[InlineData("wrong-effect")]
public async Task Restore_recovery_rejects_marker_child_vector_corruption_before_cleanup(
    string corruption)

[Theory]
[InlineData(BackupRestoreMarkerCrashPoint.AfterStagedIntentCommitBeforeSwap)]
[InlineData(BackupRestoreMarkerCrashPoint.AfterSwapBeforeFirstCleanup)]
public async Task Restore_cleanup_children_survive_swap_and_recover_under_the_global_owner(
    BackupRestoreMarkerCrashPoint crashPoint)

[Fact]
public async Task Restore_marker_cleanup_reopen_pending_finalizes_only_after_global_disposition()

[Fact]
public async Task Selective_import_transfer_finalizer_runs_only_after_successful_disposition()
```

- [ ] **Step 2: Run the red restore tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBackupRestoreTests|FullyQualifiedName~BackupCovenantRestoreReconcilerTests|FullyQualifiedName~BackupRestoreManagedAuthoritySanitizerTests|FullyQualifiedName~CovenantAuthorityStateJoinerTests|FullyQualifiedName~CovenantDisclosureStateJoinerTests|FullyQualifiedName~CovenantSelectiveTransferTests|FullyQualifiedName~BackupRestoreServiceTests|FullyQualifiedName~BackupRestoreJournalAuthenticationTests|FullyQualifiedName~BackupRestoreRecoveryTests|FullyQualifiedName~BackupSessionImporterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireDatabaseHostedServiceTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~CampaignEndpointTests|FullyQualifiedName~BackupRestoreCommandTests"
```

Expected: FAIL because staged restore retains old Covenant identities and selective export has no taint refusal.

- [ ] **Step 3: Implement staged reconciliation and closed transfer choices**

Add string-only `BackupProtectedStateMode` with `Reject`, `RestoreProtectedState`, and `PurgeProtectedState`. Add it to `BackupRestoreRequest` with default `Reject`. Add:

```csharp
public sealed record BackupSessionCampaignMapping(
    Guid SourceCampaignId,
    Guid DestinationCampaignId);
```

For full restore, open the configured destination parent without following links and derive its exact profile namespace. `ReplaceInstallation` requires the namespaced external installation identity to match the destination core row. `NewProfileRoot` requires an absent destination, creates one external destination installation identity under the held lock, and binds the staged core row to it. Build the exact normalized `BackupRestoreEffectDigestInput` with that namespace and identity, require a successful result from the sole `IBackupRestoreEffectDigestCalculator`, construct `CovenantExclusiveRecoveryOwner(restoreOperationId, CovenantExclusiveOperation.BackupRestore, restoreEffectDigest)`, and call Plan 02 `AcquireExclusiveAsync`. Then close admission, drain affected leases, quiesce `ICovenantDisclosureWriterLifecycle`, and only then drain the central connection owner before swap. Persist that exact owner, namespace, identity, and null pre-preparation marker arm in the authenticated restore journal before any staged or replacement effect.

`BackupRestoreDatabaseWorker` mints one sealed, nonserializable, single-take `RestoreStagingManagedAuthoritySanitizationCapability` only after it has authenticated that journal and opened the exact initialized unpublished candidate connection. The capability privately retains that connection lease, the restore-exclusive owner, candidate dataset generation, candidate database-file identity, envelope revision and digest, the concrete central initializer, and a revocation bit owned by the staging worker. It has no public or parameterless constructor, raw connection or path export, serialization support, DI registration, callback, delegate, transaction transfer, or generic execution method. Its sole nested factory validates `BackupRestore`, exact operation and effect digests, the candidate generation, the still-held exclusive lease, authenticated envelope and anchor, and proof that the candidate has never been published as live. `RunImmediateAsync(CancellationToken cancellationToken) -> Task<Result<BackupRestoreManagedAuthoritySanitizationReceipt>>` is its only caller-facing lifecycle operation; the producer-only authorization handshake below is unreachable from the worker or sanitizer. It uses `Interlocked.CompareExchange` to take the capability exactly once, calls `AssertCurrent` before opening its immediate transaction and again before commit, and invokes only the compile-time-bound static `BackupRestoreManagedAuthoritySanitizer.ExecuteInSessionAsync` method. `AssertCurrent` rejects disposal, reuse, journal revision change, owner change, connection substitution, candidate publication, or lease revocation. Disposal uses `Interlocked.Exchange`, releases only the borrowed staged connection once, and never disposes the caller-owned exclusive lease.

`RestoreStagingManagedAuthoritySanitizationSession` is an internal sealed, nonserializable, nondefault transaction capability constructed only inside `RunImmediateAsync`. That method also mints one internal sealed nonserializable nested `RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity` with a private constructor for the taken invocation. The containing capability's `RunImmediateAsync` is its only production constructor call site. The session privately owns the exact unpublished candidate connection, its immediate transaction, that same run identity, restore owner, effect digest, staged generation, and a monotonically checked private operation ordinal. It exports no `SqliteConnection`, transaction, command, SQL text, path, service provider, repository, general callback, delegate, interface, or transfer method. Its complete internal operation inventory is `InventoryManagedWriteIntentsAsync`, `InsertAndValidateManagedSourceTombstonesAsync`, `InventoryLocalErasureWorkItemsAsync`, `InsertAndValidateLinkedLocalTombstonesAsync`, `GuardDeleteLocalErasureWorkItemsAsync`, `DeleteExactAdoptedLabelsAsync`, `GuardDeleteManagedWriteIntentsAsync`, and `VerifyCompleteAsync`. The inventory methods retain their canonically ordered rows inside the session and return only checked counts. The final method returns the exact sanitation receipt. Every method requires the same session object, transaction, run identity, restore owner, staged generation, and next operation ordinal; a skipped, repeated, reordered, escaped, completed, rolled-back, foreign, or closed session fails before SQL. The session and run identity invalidate before control returns to the caller and cannot be placed in DI or used by any other operation.

Task 15 adds exactly one method to the internal initializer: `AuthorizeRestoreStagingManagedAuthoritySanitization(RestoreStagingManagedAuthoritySanitizationCapability authority, RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) -> CovenantSqliteAuthorizationScope`. It accepts no raw `SqliteConnection` or general authorization kind. Only the capability calls this method from `RunImmediateAsync`. The initializer revalidates the unpublished candidate and owner, then invokes the capability's producer-only `BorrowCode11Scope(CovenantSqliteConnectionInitializer caller, RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity)` method. That method requires reference equality with the initializer retained at capability mint and with the active private run identity, enables code 11 through the capability's private connection-bound authorization kernel, and returns only the ordinary one-shot scope. It never returns or passes the connection, transaction, command, kernel, or connection identity. Architecture tests enforce the initializer as the sole `BorrowCode11Scope` call site. The capability begins the immediate transaction, constructs the session with the same run identity, and invokes the exact static sanitizer. It invalidates the session and run identity and disposes the authorization scope before commit; any failure invalidates both, rolls back, and restores the predicate before returning. A failed capability check never changes a counter.

`PrepareStagedGenerationAsync` uses the central initializer, converges core, canonical, and accelerator tiers, then calls the capability's `RunImmediateAsync` before any staged validation or destination opener is registered. The static sanitizer receives only the sealed session and invokes its exact methods in this order: inventory every `managed_file_write_intents` row; insert and validate every managed-source tombstone; inventory every `local_erasure_work_items` row; insert and validate every linked local tombstone; guard-delete the local rows; delete the exact adopted labels; guard-delete the managed source rows; and verify the complete receipt. Both inventories include every pending, adopted, erased, completed, and manual arm. Source tombstones use `ManagedWriteIntent=1`; local tombstones use `LocalErasureWorkItem=2` and copy label disposition only from the already-inserted exact linked source tombstone. The session rejects a missing, extra, duplicate, reordered, wrong-owner, wrong-effect, wrong-generation, wrong-label, or wrong-link row before deletion. `secure_delete=ON` clears every encrypted source location, created identity, final ownership, and pending-label projection with the removed rows.

It computes `TombstoneVectorDigest = SHA-256("Arcanum.BackupRestore.ManagedAuthorityTombstones.v1" || RestoreOperationId || RestoreEffectDigest || StagedDatasetGeneration || UInt64BE(Count) || each item)`. UUIDs and the 128-bit dataset generation use policy-v1 canonical bytes. Each item is one-byte source kind, source-row UUID, source-write-operation UUID, artifact UUID, sensitivity-label UUID, one-byte original phase or state, one-byte owner-scope code, policy-v1 optional Campaign UUID, one-byte label disposition, and the 32-byte stripped-authority digest in that exact order. Items are sorted by source-kind code and then RFC-4122 source-row UUID bytes. The receipt returns the exact checked write, work-item, and removed-label counts. Zero source rows use the same preimage with checked `UInt64BE(0)` and no item bytes. A count, digest, owner, label, or transaction mismatch aborts candidate validation.

The sanitizer never resolves `IManagedFileCapabilityOpener`, `IManagedFileOwnershipVerifier`, either created-child lifecycle surface, a workspace root, or a destination filesystem service, and it makes no filesystem call. The staged connection cannot reach the destination opener through DI. After sanitation, validation requires zero rows in both authority-bearing source tables, one exact immutable tombstone per enumerated row, no associated live label, and no decoded source root identity, path revision, normalized segment, parent identity, child identity, created identity, final ownership, or pending label anywhere outside the rolled-back or committed sanitation transaction. Ordinary live state graphs remain closed to this branch because the dedicated SQL authorization cannot be borrowed on a published connection.

The remainder of staged Covenant reconciliation creates a fresh dataset, advances envelope and accelerator epochs, clears outbox and rebuild progress, leaves applied FTS null, validates every remaining label, merges destination authority monotonically, joins disclosure state, and terminalizes source `PendingMaintenance` and `Begun` claims as `RestoredInterrupted`. `CovenantDisclosureStateJoiner` owns only staged/destination reads and writes and delegates every value merge to Plan 02's `CovenantDisclosureStateAlgebra.JoinRestore`. After replacement and sidecar proof, use the unpublished destination candidate key and initializer state for one read-only reopen that verifies core, canonical, and accelerator health without creating WAL or SHM, then close that handle. Only then call `ICovenantAuthorityTransitionPublisher.PublishCommittedAsync` while the exclusive gate remains held. Reopen the disclosure writer after that in-process key/issuer/availability transition succeeds, then call `CompleteAsync(CommitAndReopen, lifecycleCt)` as the sole general-admission reopen. Any reopen, publication, or writer failure selects `KeepClosed` and keeps restore recovery and admission closed; only a proven pre-swap abort may select `RollbackAndReopen`.

`BackupRestoreRecovery` implements both methods of the exact `IBackupRestoreStartupRecovery` contract and is registered once. The hosted-service or CLI bootstrap caller owns one live `ArcanumMaintenanceLock` for the complete startup, and both methods call `heldInstallationLock.AssertHeldFor` and borrow that same object without reacquisition, replacement, transfer, or disposal.

`GrimoireDatabaseBootstrapper` first calls `RecoverPhysicalTopologyBeforeDatabaseAsync` after the lock and guarded-root journal location plus OS-secret anchor are available, before opening or classifying any live database, reading the host-tools database row, installing core objects, creating a pool, or initializing the operation gate. This phase loads the dedicated key without creating it, authenticates the exact V2 envelope, closes the permitted journal-ahead anchor crash window, validates the complete `BackupRestore` owner and exact live, staged, and rollback durable node identities, and reconciles only physical rename topology plus required parent-directory durability. It converges to exactly one journal-selected live database root at every displacement boundary, leaves the restore journal and active anchor intact, and opens no SQLCipher handle. Only proven journal absence together with no active anchor returns `NoActiveJournal`; an active anchor, canonical journal lookalike, or staging-index entry that cannot authenticate returns `KeptClosed`. A proven topology returns `TopologyReady`; ambiguity, identity mismatch, rollback/replay evidence, or durability uncertainty returns `KeptClosed` and stops startup.

After physical topology is ready, bootstrap performs Plan 03's host-tools OS-marker join, supported-catalog classification, and Plan 01 core convergence against that exact live database. Only the normal clean initialization arm proceeds. Bootstrap then calls `RecoverAuthorityBeforeReadinessAsync` before Campaign-marker, schema-repair, protected-transfer, managed-file, or any other database-dependent recovery, shared pool, optional Covenant service, general admission, hosted worker, or ready state. Pending, mismatched, or permanently tainted host-tools outcomes do not enter this second phase. The split lets a crash between live-root and staged-root renames reach recovery without presuming a readable live database, while still requiring clean authority and current core validation before any key, gate, health, or readiness publication.

When a restore journal remains active after topology and catalog convergence, bootstrap publishes no ready state and opens no general admission. Authority recovery reconstructs the exact `BackupRestore` owner and calls `ResumeExclusiveAsync`, resolves a proven pre-swap journal through rollback and staged cleanup, or resumes post-swap sidecar proof, read-only health verification, committed authority publication, and disclosure-writer reopen. It never calls the initial acquisition method, and a mismatched operation ID or effect digest keeps admission closed. `NoActiveJournal` permits bootstrap to continue, and `RecoveredReady` is returned only after successful `CommitAndReopen`. `KeptClosed` prevents readiness and leaves the journal active. Any unknown phase, health failure, authority-publication failure, writer failure, or disposition uncertainty selects `KeepClosed`, leaves the journal active, and prevents host or CLI readiness. Physical topology recovery alone never authorizes admission or concludes that replacement is healthy.

`RestoreProtectedState` requires explicit confirmation and preserves Covenant data and labels only from a source whose authority state is clean. If the archive is source-tainted and contains any Covenant canonical row or protected artifact, default and `RestoreProtectedState` fail closed before replacement. The only supported continuation is a separately confirmed `PurgeProtectedState`, which securely removes the full Covenant family and every protected artifact in staging before replacement while preserving content-free destination taint and disclosure evidence. Joining or relabeling source taint can never promote archived protected data into a clean destination.

Before swap, enumerate each active destination marker ownership under the one global restore-exclusive lease and open every exact root through Task 7's authority adapter. While `PrepareStagedGenerationAsync` still owns the initialized staged connection and its core transaction, call `ICampaignPathMarkerLifecycle.PrepareRestoreCleanupInStagedDatabaseAsync` with the exact global owner and bounded ordered seed vector, including `ImmutableArray.Empty` when the observed active-marker inventory is exactly zero. The lifecycle borrows that connection and transaction. For a nonempty vector it creates one random per-Campaign `RestoreCleanup` intent inside the staged core database and returns the canonical vector receipt. Every row copies the shared server restore operation ID and `BackupRestore` owner effect digest, while its distinct intent ID and unique owner-operation, Campaign, and kind tuple prevent multi-Campaign collision. For the zero-marker arm it performs no marker-store or filesystem call and returns the canonical zero-child receipt whose digest is exactly `CampaignPathRestoreCleanupIntentVector.EmptyDigestHex`; Task 15 never recomputes or duplicates that literal. Commit the staged transaction first in either arm. Then copy the exact receipt fields into the mandatory `BackupRestoreMarkerCleanupCheckpointV1`, advance the authenticated V2 restore envelope and anti-rollback anchor through their exact fsync protocol, and reread both the journal checkpoint and staged rows before the first live-root displacement. The zero-child checkpoint distinguishes a proven empty inventory from omitted preparation. A crash after the staged commit but before parent-journal publication leaves the old live root in place; retry enumerates only the exact same-owner staged rows, reconstructs the same canonical receipt including zero, and publishes it before displacement. A normal live-database intent insert is forbidden because replacement would erase it.

After swap, the same restore coordinator or recovery owner authenticates the envelope and anchor, loads the child rows from the new live database while retaining the one resumed global lease, and requires exact owner, effect digest, ordered intent IDs, count, and canonical vector digest equality before any marker effect or global disposition. A missing, extra, reordered, wrong-owner, wrong-effect, malformed, or unexpected child beneath a zero checkpoint keeps admission closed. It then calls `ReconcileGateOwnedAsync` with the checkpoint's exact vector. It never calls `ResumeCampaignExclusiveAsync` for a restore child. For a nonempty vector the lifecycle uses only its retained no-follow capabilities and shared codec, then returns one aggregate `CampaignPathMarkerGateCompletion` after every child reaches the matching `ReopenPending`. For a verified zero vector it requires the checkpoint digest to equal `CampaignPathRestoreCleanupIntentVector.EmptyDigestHex`, performs no marker I/O, and consumes the returned `Committed`, `CommitAndReopen`, and `CovenantNoOpPostDispositionFinalizer.Instance` values without constructing substitutes. The restore coordinator invokes the global disposition once, invokes the aggregate finalizer only after disposition success, and then permits child retention and closes the journal anchor only at terminal restore cleanup. A failed disposition or finalizer retains every child, checkpoint, envelope, and active anchor; uncertainty remains before `ReopenPending` and selects `KeepClosed`. Crash tests before and after swap prove the exact authenticated child vector, including zero, exists in the staged and then new live core database. Task 15 never constructs, parses, compares, deletes, exports, or reimports marker bytes or intent rows itself. Never create a restored marker. After restore, every Campaign path is unresolved and requires Task 7 registration.

For selective Session export, reject a Session with any tainted Entry, tool artifact, summary, title, Saga, Lexicon, attachment-derived artifact, or projection before emitting bytes. For selective import, a clean Campaign-bound Session requires one exact `BackupSessionCampaignMapping` to an existing destination Campaign. `BackupSessionImporter` authenticates the archive manifest, opens a read-only source SQLCipher snapshot plus no-follow attachment authority, and transfers both in one nonserializable `ImportedSessionSourceLease`. Construct the exact Plan 03 `ImportedSessionTransferRequest` with the nonempty import operation ID, source Session ID, source-evidence digest, destination Session identity and explicit source-to-destination Campaign mapping, authenticated bounded source-manifest digest, its exact checked bounded counts, and the Core-computed transfer-effect digest. The count vector covers every manifest class used by the transfer store, rejects overflow, negative, missing, or over-limit values before destination acquisition, and is authenticated by the manifest digest. Construct `CovenantExclusiveRecoveryOwner(operationId, CovenantExclusiveOperation.ProtectedSessionTransfer, transferEffectDigest)` and pass it with the immutable Global or Campaign destination scope to `AcquireProtectedTransferAsync`. Its exclusive arm must match the destination mapping and its snapshot capability must remain part of the same capability. Pass the exact request, source lease, and compound destination lease to Plan 03 `IProtectedArtifactTransferStore.CommitImportedSessionAsync`.

The dedicated transfer store reads the complete source Session graph, attachment evidence, sensitivity labels, provenance, finalization-or-erasure evidence, and current Summary/Title state itself while the source lease pins the SQLCipher snapshot and attachment authority. It recomputes and compares the authenticated manifest before any destination write, rejects the entire import if any scanned artifact is Covenant-derived, and cannot be bypassed by an omitted caller artifact. Only then may it remap and atomically commit the complete untainted graph plus every `CommittedImported` guard. The compound destination lease fences every blob and database phase and is revalidated inside the immediate destination transaction. Neither importer nor store acquires a separate read lease or nested exclusive lease. It copies no `session_turn_claims`, fabricates no live turn claim or final Covenant receipt, grants no response-replay authority, and keys idempotency only to the import-operation identity. The store borrows both caller-owned leases.

For a verified commit or proven precommit cleanup, the store persists `ReopenPending` with exact `CommitAndReopen` or `RollbackAndReopen` and returns the matching Plan 03 transfer-journal finalizer. `BackupSessionImporter` holds both leases through that result, invokes the matching compound-lease disposition once, invokes the finalizer only after success, then disposes the compound lease and the independent source lease exactly once. Postcommit uncertainty remains at `DatabaseCommitted` or the last proven earlier phase, invokes `KeepClosed`, and retains the owner. Failed disposition or finalizer retains `ReopenPending` plus its children for recovery. A request never carries a caller-assembled graph, source attachment bytes, label decisions, a serializable source capability, or a live lease. `BackupSessionImporter` and this port own the selective import; do not add import methods to `ISessionRepository` or `SessionRepository`.

Import racing Campaign deletion or reset has one serial outcome under the compound lease. If deletion or reset wins acquisition or changes the captured generation, import exposes no destination Session row and no reachable destination blob. If import commits first, deletion or reset drains the same compound scope after commit and then applies its complete lifecycle transition. Deterministic barriers cover acquisition, blob staging, transaction revalidation, commit, and cleanup so no nested read lease can self-deadlock the exclusive arm.

Update `GET /api/sessions/{id}/export` to return the typed sensitivity-policy failure before content. Acquire its conditional read lease before the same-snapshot export graph and retain it through the last archive byte. Keep `/api/campaigns/{id}/export` and `.arcanum/campaign.json` free of Covenant content, versions, receipts, hashes, provenance, and tainted artifacts; `ExportCampaign` acquires the same conditional read policy before exclusion inventory and retains that lease through archive serialization. Add explicit CLI options for protected-state mode and Campaign mappings. Before a protected-state restore or staged purge calls `IConfirmationPrompt` or creates staging state, its owning command writes Task 1 `CovenantExternalRetentionDisclosure.DestructiveOperationText` byte-for-byte, then the receipt-backed possible-attempt count with exact or lower-bound semantics, then every resolved help target. An ordered-event test proves the prompt follows that output and refusal creates no staging or recovery state.

- [ ] **Step 4: Run the green restore and transfer tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBackupRestoreTests|FullyQualifiedName~BackupCovenantRestoreReconcilerTests|FullyQualifiedName~BackupRestoreManagedAuthoritySanitizerTests|FullyQualifiedName~CovenantAuthorityStateJoinerTests|FullyQualifiedName~CovenantDisclosureStateJoinerTests|FullyQualifiedName~CovenantSelectiveTransferTests|FullyQualifiedName~BackupRestoreServiceTests|FullyQualifiedName~BackupRestoreJournalAuthenticationTests|FullyQualifiedName~BackupRestoreRecoveryTests|FullyQualifiedName~BackupSessionImporterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireDatabaseHostedServiceTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~CampaignEndpointTests|FullyQualifiedName~BackupRestoreCommandTests"
```

Expected: PASS. Restore never imports source authority, and selective transfer never strips a sensitivity label to make content exportable.

- [ ] **Step 5: Refactor and verify crash boundaries**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~BackupRestoreJournalTests|FullyQualifiedName~BackupRestoreJournalAuthenticationTests|FullyQualifiedName~BackupRestoreRecoveryTests|FullyQualifiedName~BackupRestoreDatabaseWorkerTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero, including every old-marker cleanup and replacement crash point.

### Task 16: Make retention sensitivity-aware and add Covenant inventory

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Security/CovenantSensitivePurgeEndpointFilter.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SessionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/SagaEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/TheForge/MemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Data.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.DataLifecycle.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SessionEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/TheForge/SagaEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ContextCompressionServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/EmbeddingsResetScopeTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/EmbeddingsResetServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs`

**Interfaces:**

- Consumes: Task 1 `CovenantExternalRetentionDisclosure`; Task 2 prebinding authority feature and exact protected-header helper; Task 9A's one exhaustive policy registry, `ICovenantProtectedArtifactErasureKernel`, `ICovenantManagedFileErasureKernel`, and ordinary borrower-only erasure-authority arm; Plan 02 `CovenantQuotaGuard.ReleaseSessionCapacityAsync`; and Plan 03 sensitivity labels, `SensitivityRetentionPurge` authorization, assistant erasure receipts, finalization guards, Session sensitivity projection, `EmbeddingReset = GuardedPurge`, and Covenant operation lease.
- Produces: `RetentionDataClass.Covenant`, `MemoryResetScope.Covenant`, a narrow `ICovenantSensitiveArtifactPurger` coordinator, `RequireConditionalSensitivityRetentionPurge`, content-free inventory, and route integration that delegates every labeled database or managed-file purge to Task 9A.

- [ ] **Step 1: Write failing retention tests**

Add exact methods:

```csharp
[Fact]
public void Covenant_retention_class_has_no_configurable_time_rule()

[Fact]
public void Retention_and_reset_enum_codes_preserve_every_existing_value_and_append_covenant()

[Fact]
public async Task Ordinary_sweep_never_deletes_covenant_versions_heads_provenance_or_tombstones()

[Theory]
[InlineData(SensitiveArtifactKind.AssistantEntry)]
[InlineData(SensitiveArtifactKind.TurnEvidence)]
[InlineData(SensitiveArtifactKind.Summary)]
[InlineData(SensitiveArtifactKind.ToolArtifact)]
[InlineData(SensitiveArtifactKind.SessionTitle)]
[InlineData(SensitiveArtifactKind.Saga)]
[InlineData(SensitiveArtifactKind.Lexicon)]
[InlineData(SensitiveArtifactKind.Embedding)]
[InlineData(SensitiveArtifactKind.SearchProjection)]
[InlineData(SensitiveArtifactKind.AuditProjection)]
[InlineData(SensitiveArtifactKind.Notification)]
[InlineData(SensitiveArtifactKind.ManagedWorkspaceFile)]
[InlineData(SensitiveArtifactKind.IdempotencyClaim)]
public void Every_sensitive_artifact_kind_has_exactly_one_closed_purge_policy(SensitiveArtifactKind kind)

[Theory]
[InlineData(SensitiveArtifactKind.Summary)]
[InlineData(SensitiveArtifactKind.ToolArtifact)]
[InlineData(SensitiveArtifactKind.SessionTitle)]
[InlineData(SensitiveArtifactKind.Saga)]
[InlineData(SensitiveArtifactKind.Lexicon)]
[InlineData(SensitiveArtifactKind.Embedding)]
[InlineData(SensitiveArtifactKind.SearchProjection)]
[InlineData(SensitiveArtifactKind.AuditProjection)]
[InlineData(SensitiveArtifactKind.Notification)]
public async Task Database_owned_artifact_projection_and_label_purge_atomically(SensitiveArtifactKind kind)

[Fact]
public async Task Managed_workspace_file_delegates_once_to_the_shared_kernel_with_caller_owned_ordinary_authority()

[Fact]
public void Retention_coordinator_has_no_capability_opener_verifier_or_operation_gate_dependency()

[Theory]
[InlineData("DeleteSessionEntry")]
[InlineData("CompactSession")]
[InlineData("DeleteSagaMemory")]
[InlineData("DeleteAllSagaMemories")]
[InlineData("DeleteLexiconEntry")]
public async Task Every_direct_artifact_deletion_route_dispatches_potentially_labeled_artifacts_to_closed_purger(string endpointName)

[Fact]
public async Task Embeddings_reset_routes_every_labeled_base_or_projection_through_guarded_purge()

[Fact]
public async Task Compact_session_preserves_finalization_claim_and_erasure_evidence_for_each_sensitive_entry()

[Fact]
public async Task Bulk_saga_delete_purges_each_labeled_artifact_transactionally_before_ordinary_rows()

[Fact]
public async Task Legacy_raw_delete_methods_refuse_labeled_artifacts_outside_the_purger()

[Theory]
[InlineData(SensitiveArtifactKind.AssistantEntry)]
[InlineData(SensitiveArtifactKind.TurnEvidence)]
[InlineData(SensitiveArtifactKind.IdempotencyClaim)]
public async Task Finalization_turn_and_replay_evidence_survives_content_purge(SensitiveArtifactKind kind)

[Fact]
public async Task Sensitive_retention_never_deletes_external_disclosure_receipts()

[Fact]
public async Task Committed_assistant_purge_preserves_guard_and_claim_and_appends_erasure_receipt()

[Fact]
public async Task Replay_after_sensitive_entry_erasure_returns_410_without_recreation()

[Fact]
public async Task Session_and_campaign_deletion_continue_through_owner_journal_when_optional_family_is_damaged()

[Fact]
public async Task Whole_session_retention_releases_exact_claim_and_guard_capacity_in_parent_transaction()

[Fact]
public async Task Reset_and_factory_confirmations_use_shared_external_retention_copy_and_help_targets()

[Fact]
public async Task Reset_and_factory_write_disclosure_counts_and_help_targets_before_confirmation_or_lro_start()
```

- [ ] **Step 2: Run the red retention tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~DataRetentionApplyBoundaryTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~SagaEndpointTests|FullyQualifiedName~LexiconServiceTests|FullyQualifiedName~ContextCompressionServiceTests|FullyQualifiedName~EmbeddingsResetScopeTests|FullyQualifiedName~EmbeddingsResetServiceTests"
```

Expected: FAIL because `RetentionDataClass.Covenant` and sensitivity-aware purge do not exist.

- [ ] **Step 3: Implement inventory and consume the closed per-artifact purge registry**

Make every existing implicit numeric value in `RetentionDataClass` and `MemoryResetScope` explicit without renumbering it. Append `RetentionDataClass.Covenant=28` and `MemoryResetScope.Covenant=5`; exhaustive literal tests pin all old and new values, string-only API JSON, CLI parsing/help, policy-store behavior, and settings-catalog lookup. Leave `DataRetentionSettingsCatalog.ResolveRule(settings, RetentionDataClass.Covenant)` equal to null. Extend status and plan DTOs with content-free Covenant row, managed-file, local-artifact, affected-Session, and receipt-backed disclosure counts.

Consume Task 9A's one exhaustive `CovenantSensitiveArtifactPurgePolicy` registry and its frozen `SensitiveArtifactKind` codes 1 through 13. Task 16 creates no enum value, numeric mapping, policy switch, or second registry. It resolves every selected kind through that registry, executes the returned closed policy, and delegates `ManagedWorkspaceFile` once to `ICovenantManagedFileErasureKernel` with the caller-owned ordinary authority. `external_disclosure_receipts`, folded disclosure aggregates, and joined disclosure state remain outside the Task 9A artifact switch and are never deleted by ordinary sensitivity retention.

`CovenantSensitiveRetentionPurgeCoordinator` implements the narrow Core `ICovenantSensitiveArtifactPurger`. It accepts the prebinding-issued Plan 03 `OperatorAuthorityContext`, requires its closed requirement to be `SensitivityRetentionPurge`, acquires exactly one matching generation-bound `CovenantWriteLease`, wraps both Core values in Task 9A's ordinary `CovenantArtifactErasureAuthority`, and dispatches each registry row to the shared database or managed-file kernel. Managed-file inventory returns only the source managed-write operation ID and observed source revision with the artifact and label identities; the coordinator constructs Task 9A's evidence-free request and the kernel rereads authoritative location and ownership. It retains ownership of the lease through the whole bounded purge and disposes it once afterward. The coordinator never acquires or carries a connection authorization. Each kernel borrows and disposes Plan 01's SQL authorization separately on every exact live transaction connection. The kernels only borrow and revalidate the Core authority, never acquire, complete, or dispose a lease, and the coordinator contains no second database purge, capability-open, ownership-verification, compare-delete, fsync, or label-removal algorithm.

Route `DeleteSessionEntry`, every selected deletion in `CompactSession`, `DeleteSagaMemory`, `DeleteAllSagaMemories`, `DeleteLexiconEntry`, and every base or projection row selected by `EmbeddingsReset` through this narrow purger. `RequireConditionalSensitivityRetentionPurge` installs prebinding conditional metadata and the endpoint filter passes the already-issued authority feature into the purger; a request body or repository cannot manufacture it. The purger reads artifact identity plus label in its immediate transaction. An unlabeled ordinary artifact follows the existing delete semantics without touching optional Covenant tables when the family is absent. A labeled artifact requires clean `SensitivityRetentionPurge` authority, uses its exact policy row, and returns the exact protected headers on every success or error. `CompactSession` passes the complete tool-group-safe deletion set and preserves guard, claim, finalization, and erasure invariants per Entry. Bulk Saga and embedding-reset deletion process a bounded stable identity page at a time and cannot delete an unexamined labeled row through a set-based legacy call. This task consumes Plan 03's closed `EmbeddingReset = GuardedPurge` policy and owns the concrete `EmbeddingsResetService` plus endpoint migration so no Plan 03 task depends forward on the Plan 04 prebinding boundary. Guard legacy `IGrimoireRepository.DeleteEntryAsync`, `ISagaMemoryStore` deletion methods, and `ILexiconService.DeleteAsync` so a labeled artifact cannot be removed outside the purger. Architecture tests pin all six endpoint names and every production caller.

Enforce exactly one valid post-state for a committed sensitive assistant Entry: live artifact plus matching label, or both absent plus matching erasure receipt. A claim backed by the receipt returns `Covenant.ArtifactErased` and HTTP 410.

Keep Session and Campaign owner deletion on the core owner journal, including when Covenant canonical or accelerator tiers are absent or damaged. Whole-Session retention locks the exact Session quota row and calls Plan 02 `CovenantQuotaGuard.ReleaseSessionCapacityAsync` inside the same caller-owned immediate `CovenantMutationTransaction` as the parent Session delete. That method decrements the installation claim and guard totals from the locked per-Session counts before the authorized cascade removes claims, reservations, guards, and the Session quota row. A counter mismatch, underflow, failure, or crash rolls back the parent deletion and owner event together. Campaign deletion never releases a retained historical Session's capacity. Update the API enum handling, CLI `TryParseMemoryScope`, help text, and typed client for `covenant`.

Apply the Task 16 portion of the exact lifecycle policy to the existing route names. `PlanDataRetentionPrune` validates its selector, uses one exact scoped `CovenantReadLease` for Global or one Campaign and one `CovenantInstallationReadLease` from `AcquireInstallationReadAsync` when its inventory spans all Campaigns, and retains it through the lease-bound plan response. `PlanFactoryResetDataRetention` always inventories the installation and therefore uses one `CovenantInstallationReadLease` through response completion. Neither plan service acquires a nested lease. `ApplyDataRetentionPrune`, `DeleteDataRetentionSession`, and `DeleteDataRetentionAttachment` require operator metadata and recheck dataset generation inside their immediate owner transaction. `GetDataRetentionStatus` remains an explicitly inventoried content-free aggregate response and never reads optional protected content. All protected lifecycle success and failure paths use the exact three-header tuple from Task 2. Task 17 owns the final `ResetDataRetentionMemory` and `FactoryResetDataRetention` coordinator handoff, so Task 16 has no forward implementation dependency.

Before reset or healthy-catalog factory erasure invokes `IConfirmationPrompt` or starts an LRO, `DataRetentionCommands` writes Task 1 `CovenantExternalRetentionDisclosure.DestructiveOperationText` byte-for-byte, then the exact or lower-bound receipt-backed possible-attempt count, then every resolved provider-help target. An ordered-event test proves the prompt and operation start follow all disclosure output, and refusal starts nothing. The command never offers a local provider-cache control.

- [ ] **Step 4: Run the green retention tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~DataRetentionApplyBoundaryTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~SessionEndpointTests|FullyQualifiedName~SagaEndpointTests|FullyQualifiedName~LexiconServiceTests|FullyQualifiedName~ContextCompressionServiceTests|FullyQualifiedName~EmbeddingsResetScopeTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~DataRetentionCommandTests"
```

Expected: PASS. Time-based retention preserves Covenant canonical history, and individual tainted artifacts leave coherent evidence.

- [ ] **Step 5: Refactor and verify retention authorization**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetentionAgeBoundaryTests|FullyQualifiedName~DataRetentionDurabilityBoundaryTests|FullyQualifiedName~DataRetentionInventoryAccuracyTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. No retention path deletes a sensitivity label before its artifact.

### Task 17: Implement Covenant reset and healthy-catalog factory erasure recovery

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantReset.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantRecoveryContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantRecoveryJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionMutationRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionFactoryResetRecoveryHandler.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/InstallationResetContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/DataLifecycle/FullInstallationResetRemediationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetEffectDigest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetManagedFileErasureAuthority.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetManagedFileReconciler.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantManagedFileErasureKernel.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/LocalErasureWorkItemStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ManagedFileWriteIntentRecoveryService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICampaignPathMarkerLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycleContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathIdentityAdministration.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetRemediationAttestationVerifier.cs`
- Create: `src/RetroDownfall.Arcanum.Secrets/Security/FullInstallationResetRemediationTrustRootProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAnchorStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Data.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantResetTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantErasureRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantFactoryErasureTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantManagedFileErasureKernelTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRecoveryRegistryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationCrashRecoveryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetRemediationAttestationVerifierTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetRemediationTrustRootProviderTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetEffectDigestTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetManagedFileReconciliationTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CliJsonContextCoverageTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`

**Interfaces:**

- Consumes: Task 9's caller-only Plan 03 requested-operation adapter; Task 10 recovery JSON, optional installers, central connection owner, and availability publication; Task 9A's shared protected-artifact and managed-file erasure kernels; Task 7's single `ICampaignPathMarkerLifecycle`; Task 16 lifecycle route inventory; and Task 1 `CovenantExternalRetentionDisclosure` for the confirmations owned by Tasks 11, 15, and 16.
- Consumes: Plan 03 admission-close and lease-drain gate, Task 2 `ICovenantAuthorityTransitionPublisher`, `ICovenantDisclosureWriterLifecycle`, disclosure evidence, stale-collector generation checks, Plan 03's exact `HostProcessToolsDatabaseMarkerEvidence`, `HostProcessToolsOsMarkerEvidence`, dedicated OS-secret marker capability, and `IHostProcessToolsMarkerPairJoiner.Join(HostProcessToolsDatabaseMarkerEvidence, HostProcessToolsOsMarkerEvidence?) -> HostProcessToolsMarkerPairJoinResult` with `Clean`, `PendingBlocked`, `TaintedMatched`, and `MismatchBlocked` dispositions.
- Produces: `DataRetentionMutationCheckpointV3`, `DataRetentionFactoryResetCheckpointV1`, `CovenantErasureCoordinator`, same-process healthy reopen after reset, exact `FullInstallationResetRequest`, `FullInstallationResetExternalRemediationAttestation`, `IFullInstallationResetRemediationAttestationVerifier`, `IFullInstallationResetRemediationTrustRootProvider`, canonical full-reset Campaign-marker inventory and owner-effect digests, authenticated and anti-rollback `InstallationResetActiveEnvelopeV2`, `FullInstallationResetRestartProofV1`, `HostToolsMarkerPairResetCheckpointV1`, the two-arm backup-restore terminal proof, the sealed stopped-host managed-file authority and reconciler, and the shutdown-only `HostToolsMarkerPairResetCoordinator`.

Add the only Covenant-reset recovery phase declaration to `CovenantRecoveryContracts.cs` and its source-generated checkpoint context:

```csharp
internal enum CovenantResetPhase : byte
{
    InventoryPrepared = 1,
    CanonicalApplied = 2,
    ManagedArtifactsProcessed = 3,
    HandlesClosed = 4,
    WalTruncated = 5,
    DatabaseCompacted = 6,
    AcceleratorInitialized = 7,
    FinalWalTruncated = 8,
    SidecarsVerified = 9,
    ReopenedVerified = 10
}
```

The literal byte codes are exhaustive. Zero, unknown, skipped, and regressed phases fail closed, and no route DTO owns a second reset phase enum.

- [ ] **Step 1: Write failing reset and erasure tests**

Add exact methods:

```csharp
[Fact]
public async Task Reset_preview_reports_local_artifacts_affected_sessions_managed_files_and_possible_disclosures()

[Fact]
public async Task Reset_changes_nothing_when_any_lease_cannot_drain()

[Theory]
[InlineData(CovenantResetPhase.InventoryPrepared)]
[InlineData(CovenantResetPhase.CanonicalApplied)]
[InlineData(CovenantResetPhase.ManagedArtifactsProcessed)]
[InlineData(CovenantResetPhase.HandlesClosed)]
[InlineData(CovenantResetPhase.WalTruncated)]
[InlineData(CovenantResetPhase.DatabaseCompacted)]
[InlineData(CovenantResetPhase.AcceleratorInitialized)]
[InlineData(CovenantResetPhase.FinalWalTruncated)]
[InlineData(CovenantResetPhase.SidecarsVerified)]
[InlineData(CovenantResetPhase.ReopenedVerified)]
public async Task Reset_recovers_idempotently_from_every_v3_phase(CovenantResetPhase phase)

[Fact]
public void Covenant_reset_phase_codes_are_literal_exhaustive_and_source_generated()

[Fact]
public async Task V2_retention_checkpoint_decodes_and_resumes_without_second_dataset_replacement()

[Fact]
public void Data_retention_mutation_descriptor_runs_v3_recovery_before_state_writes()

[Fact]
public async Task Partial_Covenant_reset_recovery_runs_before_any_ordinary_durable_writer_or_optional_initializer()

[Fact]
public async Task Shared_managed_file_kernel_identity_change_returns_manual_artifact_erasure_required()

[Fact]
public async Task Managed_file_recovery_delegates_durable_evidence_to_shared_kernel_exactly_once()

[Fact]
public async Task Busy_wal_or_remaining_frame_keeps_erasure_incomplete_and_admission_closed()

[Fact]
public async Task Healthy_factory_erasure_preserves_schema_core_authority_and_disclosure_evidence()

[Fact]
public async Task Catalog_damage_refuses_factory_erasure_and_points_to_restore_or_reinitialize()

[Fact]
public async Task Successful_reset_reopens_status_crud_and_inference_with_fresh_dataset()

[Fact]
public async Task Authority_transition_failure_after_database_commit_keeps_admission_closed_and_old_tokens_invalid()

[Fact]
public async Task Reset_and_factory_hold_exclusive_authority_until_health_publication_and_reopen()

[Fact]
public async Task Reset_and_factory_routes_enter_coordinator_without_ordinary_lease_and_respond_only_after_release()

[Fact]
public async Task Reset_and_factory_server_generated_LROs_checkpoint_the_exact_effect_before_gate_acquisition()

[Fact]
public async Task Reset_and_factory_requested_and_server_generated_arms_recover_the_same_owner_identity_rules()

[Fact]
public async Task Reset_and_factory_pass_their_exclusive_authority_to_both_shared_erasure_kernels()

[Fact]
public void Reset_and_factory_implement_no_second_managed_file_erasure_algorithm()

[Fact]
public async Task Full_installation_reset_removes_grimoire_and_rotates_installation_path_and_authority_identity()

[Fact]
public async Task Full_installation_reset_journals_and_compare_deletes_every_exact_owned_Campaign_marker_before_identity_removal()

[Fact]
public async Task Full_installation_reset_leaves_only_unavailable_mismatched_or_blocked_Campaign_markers_as_typed_orphans()

[Fact]
public void Full_installation_reset_effect_digest_has_the_exact_v1_domain_field_order_and_scope_code()

[Fact]
public async Task Full_installation_reset_every_Campaign_child_copies_the_same_journaled_owner_effect_digest()

[Fact]
public async Task Full_installation_reset_changed_Campaign_inventory_conflicts_before_any_marker_effect()

[Fact]
public async Task Full_installation_reset_multiple_Campaigns_use_distinct_intents_with_one_owner_and_effect()

[Fact]
public async Task Full_installation_reset_recovery_uses_only_the_journaled_owner_effect_and_inventory_digests()

[Fact]
public void Full_reset_managed_file_checkpoint_phase_codes_are_literal_exhaustive_and_source_generated()

[Theory]
[InlineData(FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared)]
[InlineData(FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled)]
[InlineData(FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled)]
[InlineData(FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified)]
public async Task Full_reset_managed_file_reconciliation_recovers_from_every_authenticated_phase(
    FullInstallationResetManagedFileReconciliationPhase phase)

[Fact]
public async Task Full_reset_managed_file_authority_requires_held_lock_and_authenticated_current_reset_journal()

[Fact]
public async Task Full_reset_managed_file_authority_revalidates_before_every_transaction_and_filesystem_effect()

[Fact]
public async Task Full_reset_managed_file_authority_accepts_only_its_authenticated_successor_revision()

[Fact]
public async Task Full_reset_reconciles_every_write_phase_and_existing_work_item_state_before_Grimoire_deletion()

[Fact]
public async Task Full_reset_adopted_write_creates_or_reuses_one_shared_kernel_work_item()

[Fact]
public async Task Full_reset_uses_writer_recovery_and_the_shared_managed_erasure_state_machine_without_a_second_opener_or_delete()

[Fact]
public async Task Full_reset_manual_write_and_work_item_blockers_are_authenticated_as_external_orphans_without_unsafe_deletion()

[Fact]
public async Task Full_reset_refuses_Grimoire_deletion_for_nonterminal_missing_extra_or_changed_managed_inventory()

[Fact]
public async Task Full_reset_crash_between_managed_file_syscall_and_CAS_resumes_the_exact_existing_work_item()

[Fact]
public async Task Ordinary_reset_and_healthy_factory_retain_campaign_and_host_tools_markers_byte_for_byte()

[Fact]
public async Task Full_installation_reset_requires_verified_external_remediation_attestation_before_marker_effect()

[Fact]
public async Task Full_installation_reset_uses_the_operation_identity_signed_by_the_attestation()

[Fact]
public async Task Full_installation_reset_rejects_request_and_attestation_operation_identity_mismatch()

[Fact]
public async Task Full_installation_reset_compare_deletes_only_the_exact_database_and_os_host_marker_pair()

[Fact]
public async Task Full_installation_reset_marker_pair_mismatch_refuses_identity_rotation_and_preserves_surviving_evidence()

[Fact]
public async Task Full_installation_reset_crash_between_pair_deletions_never_publishes_a_clean_installation()

[Theory]
[InlineData(HostToolsMarkerPairResetPhase.PairJournaled)]
[InlineData(HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted)]
[InlineData(HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted)]
[InlineData(HostToolsMarkerPairResetPhase.PairAbsenceVerified)]
public async Task Full_installation_reset_resumes_the_same_exact_pair_from_every_marker_phase(HostToolsMarkerPairResetPhase phase)

[Fact]
public async Task Full_installation_reset_resumes_after_one_pair_member_is_absent_from_checkpoint_owned_evidence()

[Fact]
public async Task Full_installation_reset_journals_one_child_for_opened_unavailable_and_mismatched_inventory_entries()

[Fact]
public async Task Full_installation_reset_after_pair_deletion_records_unavailable_root_as_manual_blocker_without_filesystem_effect()

[Fact]
public void Full_reset_marker_authority_accepts_only_the_sealed_authenticated_journal_proof()

[Fact]
public async Task Full_reset_marker_authority_revalidates_the_held_lock_and_exact_journal_at_prepare_and_reconcile()

[Fact]
public async Task Full_reset_marker_authority_rejects_use_after_lock_release_or_journal_revision_change()

[Fact]
public void Ordinary_cleanup_catalog_retains_all_three_profile_namespaced_backup_restore_accounts()

[Theory]
[InlineData(BackupRestoreFullResetTerminalArm.NeverRestoredAbsence)]
[InlineData(BackupRestoreFullResetTerminalArm.ClosedAnchor)]
public async Task Full_reset_accepts_only_the_two_proven_restore_terminal_arms(
    BackupRestoreFullResetTerminalArm arm)

[Fact]
public async Task Full_reset_catalog_refuses_restore_secret_removal_for_active_indeterminate_or_lookalike_evidence()

[Fact]
public async Task Full_reset_catalog_removes_exactly_the_three_derived_profile_accounts_after_marker_and_database_cleanup()

[Fact]
public async Task Full_reset_catalog_recovers_idempotently_from_each_partial_three_account_removal()

[Fact]
public async Task Installation_reset_active_v2_envelope_rejects_wrong_key_tag_profile_operation_and_rollback()

[Fact]
public async Task Installation_reset_active_v2_closes_only_the_one_envelope_ahead_anchor_window()

[Fact]
public async Task Full_reset_restart_reverifies_the_persisted_signed_attestation_projection_and_exact_pair_evidence()

[Fact]
public async Task Full_reset_restart_after_attestation_expiry_uses_only_the_authenticated_original_acceptance_time()

[Fact]
public async Task Full_reset_restart_rejects_changed_signature_acceptance_time_pair_evidence_or_attestation_digest()

[Fact]
public async Task Plain_v1_active_record_can_resume_ordinary_reset_but_cannot_authorize_any_full_reset_effect()

[Fact]
public async Task Installation_reset_service_requires_the_exact_full_request_for_attested_marker_reset()

[Fact]
public async Task Full_reset_verifier_uses_only_the_independent_trust_root_and_exact_attested_fields()

[Fact]
public async Task Full_reset_attestation_taint_master_version_round_trips_the_full_positive_ulong_domain()

[Fact]
public async Task Full_reset_requires_shared_pair_join_outcome_tainted_matched_before_compare_delete()

[Fact]
public void Full_reset_request_verifier_trust_root_cli_and_di_have_one_source_generated_owner_and_registration()
```

- [ ] **Step 2: Run the red reset tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantResetTests|FullyQualifiedName~CovenantErasureRecoveryTests|FullyQualifiedName~CovenantFactoryErasureTests|FullyQualifiedName~CovenantManagedFileErasureKernelTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~FullInstallationResetRemediationAttestationVerifierTests|FullyQualifiedName~FullInstallationResetRemediationTrustRootProviderTests|FullyQualifiedName~FullInstallationResetEffectDigestTests|FullyQualifiedName~FullInstallationResetManagedFileReconciliationTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~InstallationResetContractTests|FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~InstallationResetApplyBoundaryTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~DiWiringSmokeTests"
```

Expected: FAIL because Covenant reset uses no V3 checkpoint or secure-erasure phase machine.

- [ ] **Step 3: Implement the bounded recovery phase machines**

Define `DataRetentionMutationCheckpointV3` with a bounded optional `Covenant` arm and exact phases `InventoryPrepared`, `CanonicalApplied`, `ManagedArtifactsProcessed`, `HandlesClosed`, `WalTruncated`, `DatabaseCompacted`, `AcceleratorInitialized`, `FinalWalTruncated`, `SidecarsVerified`, and `ReopenedVerified`. Its Covenant arm stores the immutable server `LongRunningOperation.OperationId`, canonical 32-byte reset effect digest, and exact operation code before gate acquisition. Set the data-retention mutation registry maximum to 3, retain explicit V2 decoding, and change the one existing `DataRetentionMutation` recovery descriptor from `StartupPriority.Readiness` to `StartupPriority.BeforeStateWrites`. The priority applies to every checkpoint version of the kind, so legacy arms remain compatible while a V3 Covenant reset interrupted during canonical erasure or replacement is reconciled before any optional initializer, ordinary durable writer, worker, or ready-state publication. Registry tests pin the single descriptor, maximum version, and priority, and a bootstrap barrier proves no state writer runs ahead of an active V3 checkpoint.

Define `DataRetentionFactoryResetCheckpointV1` with the same storage phases, immutable server operation ID, canonical 32-byte healthy-catalog factory-erasure effect digest, and exact operation code, and set its registry maximum to 1. Keep the documented legacy V0 abandon or recovery behavior. For both checkpoints, the owning planner derives the effect digest from the authenticated canonical plan under its pinned domain before saving `InventoryPrepared`. When the Plan 03 requested-operation triplet is present, this digest must equal the normalized identity row. When all three requested fields are null, no identity row exists and the checkpoint is the sole durable effect-digest source. Gate acquisition cannot occur until that initial checkpoint commit succeeds.

`CovenantErasureCoordinator` first commits `InventoryPrepared`, then constructs `CovenantExclusiveRecoveryOwner(checkpoint.OperationId, checkpoint.OperationKind, checkpoint.EffectDigest)` and calls Plan 02 `AcquireExclusiveAsync`. The operation kind is exactly `CovenantReset` or `HealthyCatalogFactoryErasure`. The gate identity is always the durable server `LongRunningOperation.OperationId`; an optional caller `RequestedOperationId` remains only the normalized replay key. The all-null requested arm never attempts to read a nonexistent request-identity row. Each matching recovery handler reconstructs the identical owner only from the checkpoint before calling `ResumeExclusiveAsync`. The resulting global `CovenantExclusiveLease` closes admission and drains every affected lease. The coordinator wraps that exact caller-owned lease in Task 9A's exclusive `CovenantArtifactErasureAuthority`, enumerates bounded comprehensive protected-artifact pages, and delegates database-owned artifacts to `ICovenantProtectedArtifactErasureKernel`. It delegates every managed file to the single `ICovenantManagedFileErasureKernel`, which persists the durable work item before its first external effect and owns all reopen, absence, same-handle compare-delete, parent-fsync, mismatch, and label-completion behavior. The coordinator and recovery handlers never call `IManagedFileCapabilityOpener` or `IManagedFileOwnershipVerifier` directly and implement no second deletion algorithm. Both kernels borrow and revalidate the exact exclusive authority; they never acquire, complete, or dispose it. A manual blocker keeps admission closed and the checkpoint active.

After the shared kernels prove all protected database and managed-file artifacts processed, quiesce `ICovenantDisclosureWriterLifecycle`, then use the central owner to clear pools and drain direct handles. Open one exclusive initialized connection with secure delete, delete reset-owned Covenant turn and mutation receipts while preserving core nonrevocable disclosure receipts and joined disclosure state, delete provenance, heads, versions, entries, key epochs, outbox, and search IDs, and create one new dataset. Reset canonical sequence, applied tuple, cleanup cursors, accelerator and envelope epochs, and search ID state in that transaction. Recovery checkpoints and work items persist no live lease, opened handle, or other live capability.

Checkpoint and verify `wal_checkpoint(TRUNCATE)`, close handles, inventory main database sidecars and staging artifacts, run `VACUUM`, and use verified SQLCipher export-and-atomic-replace when needed. Initialize the empty accelerator and run rank-1 integrity before the second checked `wal_checkpoint(TRUNCATE)`. Then close and clear every pool/handle again and prove final sidecar, journal, temp, staging, and replaced-file absence. Use the unpublished candidate dataset, master, authority, and capability state for one read-only verified reopen that cannot create WAL or SHM, then close that handle and durably publish `LocalSecureErasureComplete`. Only after that complete storage-health proof, and while the exclusive gate remains held, publish the committed dataset/master/authority/capability snapshot through `ICovenantAuthorityTransitionPublisher`; failure keeps old contexts unusable and admission closed. After successful publication, `ICovenantDisclosureWriterLifecycle` may acquire its new warm writer lease. A writer-restart failure occurs before disposition and therefore selects `KeepClosed`. When the writer is healthy, the coordinator calls `CompleteAsync(CommitAndReopen, lifecycleCt)` once as the sole general-admission reopen. If that one-shot call itself fails, the gate already remains closed; record the recoverable lifecycle failure and dispose once without attempting `KeepClosed` or any second disposition. Neither case can reverse the already-proven local erasure result. Only a proven pre-erasure abort may select `RollbackAndReopen`.

Status must expose `CanonicalResetApplied`, `LocalSecureErasureComplete`, and `ExternalDisclosuresNotRevocable` independently. A busy checkpoint, remaining WAL frame, surviving handle, or residual sidecar returns `Covenant.ErasureIncomplete`, keeps the checkpoint active, and keeps search and admission closed.

Healthy-catalog factory erasure preserves schema objects, `grimoire_feature_schemas`, authority taint, and nonrevocable disclosure evidence. It reseeds canonical and accelerator singletons and fresh identities after deleting the data selected by the existing factory contract. `CovenantErasureCoordinator` is the single authority-transition owner for both Covenant reset and healthy-catalog factory erasure; each path calls `ICovenantAuthorityTransitionPublisher.PublishCommittedAsync` only after its database and sidecar health proof and before disclosure-writer or general-admission reopen. Catalog damage refuses this path and names restore, family reinitialize, or full installation reset. Ordinary Covenant reset and healthy-catalog factory erasure retain every Campaign path marker, the exact database host-tools taint row, and the exact OS-secret host-tools marker byte-for-byte. Family reinitialize and ordinary credential cleanup have the same host-tools marker retention rule. `InstallationResetCredentialCatalog` also derives and classifies Task 15's exact three profile accounts from the current `ProfileNamespaceDigest`: `backup-restore-journal-installation-<64-lowercase-hex>`, `backup-restore-journal-key-<64-lowercase-hex>`, and `backup-restore-journal-anchor-<64-lowercase-hex>`. Every ordinary cleanup, Covenant reset, healthy-catalog factory erasure, family reinitialize, and nonattested installation-reset path retains all three byte-for-byte. The separate `installation-reset-active-key-<suffix>` and `installation-reset-active-anchor-<suffix>` identities are operation-control credentials retained by every catalog pass, including attested full reset, until `InstallationResetActiveStore` retires its authenticated record. A bare prefix, unnamespaced alias, another profile's suffix, or either reset-active identity is never substituted for the Task 15 trio.

Complete the Task 16 route inventory here. `ResetDataRetentionMemory` and `FactoryResetDataRetention` require operator metadata and enter `CovenantErasureCoordinator` directly, without an ordinary route lease that the coordinator would have to drain. Each route awaits the coordinator through health publication, disclosure-writer reopen, general-admission reopen, and exclusive-lease release, then emits the approved content-free `DataRetentionApplyResult` with Task 2's exact protected headers. Endpoint tests prove no response byte is written while the exclusive lease remains held and every failure from commit through reopen keeps admission closed.

Use Task 1's single `CovenantExternalRetentionDisclosure` at the actual confirmation owners: Task 11 family reinitialize, Task 15 protected restore or staged purge, and Task 16 Covenant reset plus healthy-catalog factory erasure. Each confirmation states that local work cannot revoke provider logs, automatic provider prompt caches, encrypted backup copies, unmanaged files, or other nonrevocable disclosures, shows the receipt-backed possible-attempt count with exact or lower-bound semantics, and renders the shared official-provider or fallback help targets. Tests at those owning command files assert exact shared copy. No Task 17 recovery handler prompts and no route-specific paraphrase becomes a second authority contract.

Keep full installation reset distinct. Add this exact owning request and service seam to `InstallationResetContracts.cs`:

```csharp
public sealed record FullInstallationResetExternalRemediationAttestation(
    byte Version,
    Guid OperationId,
    Guid InstallationId,
    Guid HostToolsTransitionId,
    ulong TaintMasterKeyVersion,
    CovenantDigest AuthorityFingerprint,
    CovenantDigest DatabaseMarkerDigest,
    CovenantDigest OsMarkerDigest,
    CovenantDigest RemediationActionDigest,
    string NonceBase64Url,
    string Issuer,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string SignatureBase64Url);

public sealed record FullInstallationResetRequest(
    Guid OperationId,
    [property: JsonRequired] InstallationResetApplyRequest Apply,
    [property: JsonRequired] FullInstallationResetExternalRemediationAttestation ExternalRemediation);

public interface IInstallationResetService
{
    Task<Result<InstallationResetPlan>> PlanAsync(InstallationResetPlanRequest request, CancellationToken cancellationToken = default);
    Task<Result<InstallationResetResult>> ApplyAsync(InstallationResetApplyRequest request, CancellationToken cancellationToken = default);
    Task<Result<InstallationResetResult>> ApplyFullAsync(FullInstallationResetRequest request, CancellationToken cancellationToken = default);
}
```

Freeze the guarded-root restart and restore-terminal projections before extending the marker lifecycle:

```csharp
internal enum HostToolsMarkerPairResetPhase : byte
{
    PairJournaled = 1,
    DatabaseMarkerCompareDeleted = 2,
    OsMarkerCompareDeleted = 3,
    PairAbsenceVerified = 4
}

internal enum InstallationResetActiveAnchorState : byte
{
    Active = 1,
    Closed = 2
}

internal enum BackupRestoreFullResetTerminalArm : byte
{
    NeverRestoredAbsence = 1,
    ClosedAnchor = 2
}

internal enum InstallationResetRestoreCredentialCleanupPhase : byte
{
    AnchorRemoved = 1,
    JournalKeyRemoved = 2,
    InstallationIdentityRemoved = 3,
    VerifiedAbsent = 4
}

internal enum FullInstallationResetManagedFileReconciliationPhase : byte
{
    InventoryPrepared = 1,
    WriteIntentsReconciled = 2,
    WorkItemsReconciled = 3,
    TerminalInventoryVerified = 4
}

internal sealed record FullInstallationResetSignedAttestationProjectionV1(
    byte Version,
    Guid OperationId,
    Guid InstallationId,
    Guid HostToolsTransitionId,
    ulong TaintMasterKeyVersion,
    CovenantDigest AuthorityFingerprint,
    CovenantDigest DatabaseMarkerDigest,
    CovenantDigest OsMarkerDigest,
    CovenantDigest RemediationActionDigest,
    string NonceBase64Url,
    string Issuer,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string SignatureBase64Url);

internal sealed record FullInstallationResetRestartProofV1(
    byte Version,
    DateTimeOffset AcceptedAtUtc,
    FullInstallationResetSignedAttestationProjectionV1 SignedAttestation,
    CovenantDigest SignedAttestationDigest,
    HostProcessToolsDatabaseMarkerEvidence DatabaseEvidence,
    HostProcessToolsOsMarkerEvidence OsMarkerEvidence,
    CovenantDigest PairEvidenceDigest);

internal sealed record BackupRestoreFullResetTerminalProjectionV1(
    byte Version,
    BackupRestoreFullResetTerminalArm Arm,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid? ClosedRestoreOperationId,
    ulong? ClosedRevision,
    CovenantDigest? ClosedEnvelopeDigest,
    CovenantDigest? ClosedJournalLocationDigest,
    CovenantDigest? InstallationAccountValueDigest,
    CovenantDigest? JournalKeyAccountValueDigest,
    CovenantDigest? AnchorAccountValueDigest,
    CovenantDigest TerminalEvidenceDigest);

internal sealed record FullInstallationResetManagedFileCheckpointV1(
    byte Version,
    FullInstallationResetManagedFileReconciliationPhase Phase,
    ulong SourceWriteIntentCount,
    CovenantDigest SourceWriteIntentVectorDigest,
    Guid? LastSourceWriteOperationId,
    ulong? LocalErasureWorkItemCount,
    CovenantDigest? LocalErasureWorkItemVectorDigest,
    Guid? LastLocalErasureWorkItemId,
    ulong? SafeTerminalWriteIntentCount,
    ulong? ManualWriteOrphanCount,
    ulong? CompletedWorkItemCount,
    ulong? ManualWorkItemOrphanCount,
    CovenantDigest? TerminalClassificationDigest);

internal sealed record HostToolsMarkerPairResetCheckpointV1(
    byte Version,
    HostToolsMarkerPairResetPhase Phase,
    FullInstallationResetRestartProofV1 RestartProof,
    CovenantDigest CampaignMarkerInventoryDigest,
    CovenantDigest OwnerEffectDigest,
    ulong? MarkerIntentCount,
    CovenantDigest? MarkerIntentVectorDigest,
    ulong? MarkersDeletedCount,
    ulong? MarkerOrphanCount,
    FullInstallationResetManagedFileCheckpointV1? ManagedFileReconciliation,
    BackupRestoreFullResetTerminalProjectionV1? RestoreTerminal,
    InstallationResetRestoreCredentialCleanupPhase? RestoreCredentialCleanupPhase);

internal sealed record InstallationResetActiveEnvelopeV2(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    CovenantDigest ActiveLocationDigest,
    InstallationResetScope Scope,
    string PlanId,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

internal sealed record InstallationResetActiveAnchorV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest EnvelopeDigest,
    CovenantDigest ActiveLocationDigest,
    InstallationResetActiveAnchorState State);
```

`InstallationResetActiveRecord` version 2 retains its existing reset plan, accepted binding, phase, point-of-no-return, counts, credential results, and error fields, and adds nullable `HostToolsMarkerPairResetCheckpointV1 HostToolsMarkerPairReset`. The checkpoint is null for ordinary reset and mandatory before the first attested full-reset marker effect. `FullInstallationResetSignedAttestationProjectionV1.Version`, `FullInstallationResetRestartProofV1.Version`, `BackupRestoreFullResetTerminalProjectionV1.Version`, `FullInstallationResetManagedFileCheckpointV1.Version`, and `HostToolsMarkerPairResetCheckpointV1.Version` are exactly 1. After bounded CLI decoding and successful verification, the coordinator copies every signed input field byte-for-byte into the internal projection and proves canonical equality before publication. The restart proof contains that complete projection including its signature, the exact timestamp at which the verifier accepted it, both members of the sole `TaintedMatched` pair, and their canonical digest. It contains no private trust root, signing key, marker plaintext, opened handle, or database connection.

`SignedAttestationDigest` is SHA-256 under ASCII domain `Arcanum.FullInstallationReset.ExternalRemediationDigest.v1` over the exact bounded canonical signed-attestation fields, including `SignatureBase64Url`. `PairEvidenceDigest` is SHA-256 under `Arcanum.FullInstallationReset.MarkerPairEvidence.v1` over the policy-v1 encodings of every `DatabaseEvidence` field followed by every `OsMarkerEvidence` field. Initial acceptance requires `IssuedAtUtc <= AcceptedAtUtc < ExpiresAtUtc`, a successful independent-trust-root signature verification at that exact time, exact request and evidence equality, and `IHostProcessToolsMarkerPairJoiner.Join` returning `TaintedMatched` with those same two records. Restart repeats the signature, digest, shape, and join verification from the authenticated projection. Later wall-clock expiry does not cancel that one durably accepted operation. A changed acceptance timestamp, signature, issuer, nonce, operation, pair field, digest, or a proof accepted outside the signed interval fails closed.

`InstallationResetActiveRecordAuthenticator` is the sole V2 payload codec. `ArcanumCredentialIdentity` owns exact profile-namespaced account-prefix methods `installation-reset-active-key-` and `installation-reset-active-anchor-`, each suffixed by the same 64-character lowercase `ProfileNamespaceDigest` used by Task 15. Under the caller-held installation lock, `InstallationResetActiveRecordKeyProvider` generates the missing random 256-bit AES-GCM key before the first V2 record and verifies readback. Recovery opens an existing key and never creates or substitutes one. The key lease is nonserializable, single-take, and zeroes its exact decoded 32-byte buffer after authentication or disposal.

Envelope version is exactly 2. The complete envelope remains within the existing 64 KiB bound. The nonce is exactly 12 random bytes and the tag is exactly 16 bytes, both canonical unpadded base64url. AES-256-GCM additional authenticated data is ASCII `Arcanum.InstallationReset.ActiveEnvelope.v2`, then version, profile-namespace digest, RFC-4122 installation and operation UUID bytes, checked positive `UInt64BE` revision, previous-envelope digest, active-location digest, one-byte scope, and bounded length-prefixed UTF-8 plan ID. Ciphertext is the canonical source-generated V2 active record. Its version, operation ID, scope, and plan ID must equal the outer authenticated header, and its full-reset restart proof installation ID must equal the header installation ID when that arm is present. `ActiveLocationDigest` is SHA-256 under `Arcanum.InstallationReset.ActiveLocation.v1` over profile namespace, the retained guarded-parent physical-identity digest, and the bounded active-record child leaf. `EnvelopeDigest` is SHA-256 under `Arcanum.InstallationReset.ActiveEnvelopeDigest.v2` over every canonical envelope field including ciphertext and tag.

`InstallationResetActiveStore` uses the namespaced `InstallationResetActiveAnchorV1` in the OS-secret store and the same installation-lock-serialized write, file fsync, atomic replace, parent fsync, reread, authentication, anchor compare-write, and readback protocol as Task 15. A new operation first writes `Active` revision zero with the zero envelope digest and exact active-location digest, then publishes envelope revision one with the zero previous digest. Recovery accepts an exact anchor match or exactly one envelope revision ahead whose previous digest equals the anchor digest, closes only that crash window, and rejects older, skipped, cross-profile, wrong-location, wrong-installation, wrong-operation, wrong-key, or tag-invalid state. An active file without its key and anchor, active anchor without the unique canonical file, unauthenticated lookalike, or anchor rollback keeps startup closed. The outer scope and plan ID are display-only until the payload is authenticated and never authorize an effect.

Version 1 plain records retain explicit bounded decoding only for in-place recovery of a preexisting ordinary reset. Under the held lock, the store wraps a valid V1 ordinary record in a V2 envelope before its next external effect. V1 has no host-tools checkpoint, restart proof, or full-reset authority and can never enter `ApplyFullAsync`, delete either host-tools marker, prepare a Campaign-marker child, remove a restore credential, or rotate an installation identity. Every new record is V2. `InstallationResetActiveJsonContext` registers the envelope, anchor, V2 active record, checkpoint, restart proof, internal signed-attestation projection, both exact pair evidence records, restore-terminal projection, managed-file checkpoint, `FullInstallationResetManagedFileReconciliationPhase`, every other nested enum, credential-result arrays, and required immutable arrays. `CliJsonContext` remains the sole source-generated wire owner of `FullInstallationResetExternalRemediationAttestation`; the active-record context owns only its internal persistence projection. No reflection fallback exists.

After the authenticated record reaches its final reset state, the store advances its active anchor to `Closed`, deletes and parent-fsyncs the active file, removes and verifies absence of the reset-active anchor account, then removes the reset-active key account last. A crash after file retirement may leave only these two operation-control credentials; startup under the installation lock recognizes the closed-or-absent file state and completes their bounded idempotent removal before publishing a fresh installation. Neither credential is removed while an active envelope could still be needed for recovery.

In this task, extend Task 7's internal marker lifecycle with the shutdown-only authority and methods whose authenticated reset-journal types now exist:

```csharp
internal sealed partial class BackupRestoreJournalAnchorStore
{
    internal sealed class FullResetTerminalProof
    {
        private FullResetTerminalProof(
            BackupRestoreFullResetTerminalProjectionV1 projection)
        {
            Projection = projection;
        }

        internal BackupRestoreFullResetTerminalProjectionV1 Projection { get; }
    }
}

internal sealed partial class HostToolsMarkerPairResetCoordinator
{
    internal sealed class AuthenticatedFullInstallationResetJournalProof
    {
        private readonly Func<
            ArcanumMaintenanceLock,
            ulong,
            Guid,
            CovenantDigest,
            CovenantDigest,
            CovenantDigest,
            CancellationToken,
            ValueTask<Result>> revalidate;

        private AuthenticatedFullInstallationResetJournalProof(
            ulong envelopeRevision,
            Guid operationId,
            CovenantDigest ownerEffectDigest,
            CovenantDigest campaignInventoryDigest,
            CovenantDigest authenticatedResetEnvelopeDigest,
            Func<
                ArcanumMaintenanceLock,
                ulong,
                Guid,
                CovenantDigest,
                CovenantDigest,
                CovenantDigest,
                CancellationToken,
                ValueTask<Result>> revalidate)
        {
            EnvelopeRevision = envelopeRevision;
            OperationId = operationId;
            OwnerEffectDigest = ownerEffectDigest;
            CampaignInventoryDigest = campaignInventoryDigest;
            AuthenticatedResetEnvelopeDigest = authenticatedResetEnvelopeDigest;
            this.revalidate = revalidate;
        }

        internal ulong EnvelopeRevision { get; }

        internal Guid OperationId { get; }

        internal CovenantDigest OwnerEffectDigest { get; }

        internal CovenantDigest CampaignInventoryDigest { get; }

        internal CovenantDigest AuthenticatedResetEnvelopeDigest { get; }

        internal ValueTask<Result> RevalidateAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            ulong envelopeRevision,
            Guid operationId,
            CovenantDigest ownerEffectDigest,
            CovenantDigest campaignInventoryDigest,
            CovenantDigest authenticatedResetEnvelopeDigest,
            CancellationToken cancellationToken) =>
            revalidate(
                heldInstallationLock,
                envelopeRevision,
                operationId,
                ownerEffectDigest,
                campaignInventoryDigest,
                authenticatedResetEnvelopeDigest,
                cancellationToken);
    }
}

internal sealed class FullInstallationResetMarkerCleanupAuthority
{
    private readonly ArcanumMaintenanceLock heldInstallationLock;
    private readonly HostToolsMarkerPairResetCoordinator
        .AuthenticatedFullInstallationResetJournalProof authenticatedProof;

    private FullInstallationResetMarkerCleanupAuthority(
        ArcanumMaintenanceLock heldInstallationLock,
        HostToolsMarkerPairResetCoordinator
            .AuthenticatedFullInstallationResetJournalProof authenticatedProof)
    {
        this.heldInstallationLock = heldInstallationLock;
        this.authenticatedProof = authenticatedProof;
        OperationId = authenticatedProof.OperationId;
        OwnerEffectDigest = authenticatedProof.OwnerEffectDigest;
        CampaignInventoryDigest = authenticatedProof.CampaignInventoryDigest;
        AuthenticatedResetEnvelopeDigest =
            authenticatedProof.AuthenticatedResetEnvelopeDigest;
    }

    public Guid OperationId { get; }

    public CovenantDigest OwnerEffectDigest { get; }

    public CovenantDigest CampaignInventoryDigest { get; }

    public CovenantDigest AuthenticatedResetEnvelopeDigest { get; }

    internal static async ValueTask<Result<FullInstallationResetMarkerCleanupAuthority>>
        CreateAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        HostToolsMarkerPairResetCoordinator
            .AuthenticatedFullInstallationResetJournalProof authenticatedProof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);
        ArgumentNullException.ThrowIfNull(authenticatedProof);

        Result current = await authenticatedProof.RevalidateAsync(
            heldInstallationLock,
            authenticatedProof.EnvelopeRevision,
            authenticatedProof.OperationId,
            authenticatedProof.OwnerEffectDigest,
            authenticatedProof.CampaignInventoryDigest,
            authenticatedProof.AuthenticatedResetEnvelopeDigest,
            cancellationToken);

        if (!current.IsSuccess)
        {
            return current.Error;
        }

        return new FullInstallationResetMarkerCleanupAuthority(
            heldInstallationLock,
            authenticatedProof);
    }

    internal ValueTask<Result> AssertCurrentAsync(
        CancellationToken cancellationToken) =>
        authenticatedProof.RevalidateAsync(
            heldInstallationLock,
            authenticatedProof.EnvelopeRevision,
            OperationId,
            OwnerEffectDigest,
            CampaignInventoryDigest,
            AuthenticatedResetEnvelopeDigest,
            cancellationToken);
}

internal sealed record CampaignPathFullResetCleanupPreparation(
    FullInstallationResetMarkerCleanupAuthority Authority,
    ImmutableArray<CampaignPathRestoreCleanupSeed> OrderedSeeds);

internal enum CampaignPathFullResetCleanupState : byte
{
    Prepared = 1,
    Terminal = 2
}

internal sealed record CampaignPathFullResetCleanupReceipt(
    Guid OwnerOperationId,
    CovenantDigest OwnerEffectDigest,
    ImmutableArray<Guid> OrderedIntentIds,
    ulong IntentCount,
    CovenantDigest IntentVectorDigest,
    CampaignPathFullResetCleanupState State,
    ulong DeletedCount,
    ulong OrphanCount);

internal partial interface ICampaignPathMarkerLifecycle
{
    Task<Result<CampaignPathFullResetCleanupReceipt>> PrepareFullInstallationResetCleanupAsync(
        CampaignPathFullResetCleanupPreparation preparation,
        CovenantInitializedConnectionLease liveCoreConnection,
        SqliteTransaction liveCoreTransaction,
        CancellationToken cancellationToken);

    Task<Result<CampaignPathFullResetCleanupReceipt>> ReconcileFullInstallationResetCleanupAsync(
        CampaignPathFullResetCleanupReceipt prepared,
        FullInstallationResetMarkerCleanupAuthority authority,
        CancellationToken cancellationToken);
}
```

`BackupRestoreJournalAnchorStore` becomes `sealed partial` only for the Task 17 terminal-proof method `ProveFullResetTerminalAsync(ArcanumMaintenanceLock heldInstallationLock, CovenantDigest profileNamespaceDigest, Guid installationId, CancellationToken cancellationToken) -> ValueTask<Result<BackupRestoreJournalAnchorStore.FullResetTerminalProof>>`. Its nested proof constructor is private. `NeverRestoredAbsence` requires proven absence of the profile's anchor account, canonical journal, canonical lookalike, and staging-index evidence. The namespaced installation identity and unused journal key may be present, and their exact value digests are inventoried. `ClosedAnchor` requires an authenticated Task 15 V1 anchor with `State=Closed`, matching profile and installation, positive revision, exact envelope and journal-location digests, plus absence of the canonical journal, every lookalike, and staging-index evidence. Active, missing-key-with-active-evidence, malformed, indeterminate, wrong-profile, wrong-installation, or partially cleaned restore state produces no proof.

The proof inventories the exact presence and SHA-256 value digest of Task 15's three derived credential accounts and returns the immutable `BackupRestoreFullResetTerminalProjectionV1`. Arm-specific shape is closed. `NeverRestoredAbsence` requires all four closed-anchor fields and `AnchorAccountValueDigest` null. `ClosedAnchor` requires all four and the anchor value digest nonnull. The installation and key value digests are independently nullable in either arm. `TerminalEvidenceDigest` is SHA-256 under `Arcanum.FullInstallationReset.BackupRestoreTerminal.v1` over the arm, profile namespace, installation ID, nullable closed-anchor fields with policy-v1 presence bytes, and the three nullable account-value digests in listed order. Before removing any restore account, Task 17 writes this projection into the authenticated reset checkpoint, advances the active envelope and anchor, and verifies readback. Recovery after a partial account removal uses only that authenticated projection and exact current-account comparison. Raw account names, another profile's evidence, or a caller-created projection grants no deletion authority.

The full-reset journal-proof constructor is private to its containing sealed coordinator. The coordinator creates it only after the caller-held lock passes `AssertHeldFor`, `InstallationResetActiveStore` authenticates the exact V2 envelope and anti-rollback anchor, the persisted signed projection re-passes the independent signature verifier and pair join, and the checkpoint is at `PairAbsenceVerified` with the exact operation, attestation, effect, inventory, envelope revision, and envelope digest. Its private revalidator captures only that producer and rereads and reauthenticates the envelope, anchor, restart proof, and checkpoint under the same held lock. A raw `InstallationResetActiveRecord`, decrypted payload without its envelope proof, matching caller-supplied scalar values, serialized authority proof, delegate supplied by another caller, or reconstructed digest cannot create either proof or authority.

The authority constructor is private. `CreateAsync` accepts only that producer-owned proof and retains the caller's exact lock object and proof without owning or disposing either. It rejects a missing, disposed, or wrong-installation lock; a stale or changed envelope or anchor revision; a non-V2 or non-`PairAbsenceVerified` checkpoint; failed restart-signature or pair-evidence verification; any operation, attestation, effect, inventory, or authenticated-envelope digest mismatch; and every empty or malformed field. `PrepareFullInstallationResetCleanupAsync` and `ReconcileFullInstallationResetCleanupAsync` call `AssertCurrentAsync` before reading or mutating a child. Revalidation failure has no marker, database, credential, or identity effect. The receipt vectors are nondefault, bounded, immutable, canonically ordered, and deep-copied. Counts are checked, `IntentCount` equals vector length, and the terminal deleted plus orphan counts equal `IntentCount`. No proof, authority, preparation, or receipt has a public parameterless/default construction path or an HTTP or CLI JSON registration.

Version is exactly one. Every identity is nonempty, every digest is exactly 32 bytes, `TaintMasterKeyVersion` is positive and uses the exact `ulong` domain from both Plan 03 marker-evidence records, nonce is canonical unpadded base64url of 16 through 32 bytes, issuer is a closed configured identifier capped at 128 UTF-8 bytes, signature is canonical unpadded base64url within the verifier's algorithm-specific bound, and expiry is later than issue time within the configured maximum lifetime. Source-generated decoding accepts the full positive `ulong` domain and rejects zero, overflow, negative JSON, and a value that differs from either joined evidence record. The signature preimage includes every field through `ExpiresAtUtc` in the listed order under `Arcanum.FullInstallationReset.ExternalRemediation.v1`; it never includes `SignatureBase64Url` itself. `FullInstallationResetRequest.OperationId` must equal the signed `ExternalRemediation.OperationId` before any lookup, journal write, marker read, or reset effect.

`ApplyFullAsync` accepts only `Apply.Request.Scope=All`, requires the host to be stopped while `InstallationResetApplyBoundary` retains `ArcanumMaintenanceLock`, and is the only service method that can authorize host-tools marker compare-deletion. Ordinary `ApplyAsync` carries no attestation and must refuse a joined tainted, pending, or mismatched marker state before any reset effect. `IFullInstallationResetRemediationAttestationVerifier` validates an externally signed, bounded, unexpired, single-operation statement against the current installation identity, request operation ID, host-tools transition ID, taint-time master version, authority fingerprint, exact database-marker digest, exact OS-marker digest, remediation-action digest, nonce, issuer, and issued/expiry times. It resolves the issuer only through `IFullInstallationResetRemediationTrustRootProvider`. The provider's public trust root is independent of the Grimoire, master keys, API key, installation credentials, and credential catalog being erased. A local confirmation, command-line flag, unsigned document, missing field, nonce already bound to a different or completed operation, stale identity, stale digest, or unknown issuer cannot substitute for that attestation.

Register the verifier and independent trust-root provider exactly once through `AddArcanumInstallationReset`. `CliJsonContext` is the sole source-generated wire owner of `FullInstallationResetRequest`, the external attestation input, and every nested signature field; none is registered in `ArcanumJsonContext` or exposed through HTTP. The closed verifier result and trust-root material are nonserializable. `FullInstallationResetExternalRemediationAttestation` contains the one nonempty reset `OperationId` covered by its signature. `InstallationFactoryResetCommand --all --apply` accepts an optional `--external-remediation-attestation <file>` only in that exact mode. It opens the file as data after argv preflight, performs bounded source-generated decoding, copies that exact signed operation ID into `FullInstallationResetRequest`, and passes the request through `InstallationResetApplyBoundary.ApplyFullAsync`. It never generates or substitutes a second identity after decoding. The request, signed payload, verifier result, authenticated active checkpoint, and every recovery call must agree on that identity before any effect. The internal persistence projection appears only inside authenticated ciphertext. The attestation plaintext or signature never appears in argv-derived diagnostics, logs, confirmation text, JSON output, or the outer active-record header. Without the option, the existing request path remains incapable of clearing a tainted pair. `InstallationResetServiceTests`, verifier tests, trust-root tests, coordinator tests, contract/source-generation tests, CLI command and apply-boundary tests, DI smoke tests, and CLI JSON coverage all belong to Task 17. Task 12 owns none of this wiring or verification.

The checkpoint phase codes are exhaustive and monotonic. `RestartProof`, Campaign inventory digest, and owner-effect digest are mandatory and immutable at every phase. Marker intent count, vector digest, deleted count, and orphan count are all null before Campaign-marker preparation is durably published and all nonnull afterward; the counts use checked `u64`, deleted plus orphan equals intent count at terminal, and the vector digest cannot change. `RestoreTerminal` is null until one of the two exact restore-terminal arms has been proven, then immutable and mandatory before `RestoreCredentialCleanupPhase` becomes nonnull. Credential cleanup advances only `AnchorRemoved -> JournalKeyRemoved -> InstallationIdentityRemoved -> VerifiedAbsent`. Each advance authenticates the current V2 envelope and anchor, compare-removes only the exact derived account whose current value digest equals the projection, verifies absence, then publishes the next envelope revision. An account already absent is idempotent only when the prior authenticated phase or the original projection proves that absence. Unknown, zero, skipped, regressed, partially populated, or cross-profile shapes fail closed.

`ManagedFileReconciliation` is null until the Campaign-marker receipt is terminal and becomes mandatory before the first Grimoire, database-sidecar, or source-identity deletion. Its version is one and its four phase codes are literal and monotonic. `SourceWriteIntentVectorDigest` is SHA-256 under `Arcanum.FullInstallationReset.ManagedWriteIntentVector.v1` over the checked `u64` count followed by every source write operation UUID in RFC-4122 byte order. `LocalErasureWorkItemVectorDigest` uses the parallel `Arcanum.FullInstallationReset.LocalErasureWorkItemVector.v1` domain and ordered work-item UUIDs. The source count and digest are immutable from `InventoryPrepared`. The work-item count and digest are null at `InventoryPrepared`, become nonnull only after every write intent has been reconciled and every required shared-kernel work item exists, and remain immutable afterward. A cursor is null exactly for a zero vector or before its first item, otherwise it is an exact member and advances only in canonical order.

`TerminalClassificationDigest` is null before `WorkItemsReconciled`. From that phase onward it is SHA-256 under `Arcanum.FullInstallationReset.ManagedFileTerminalClassification.v1` over the ordered source and work-item identities, their exact terminal phase or state codes, deletion-evidence code where applicable, and a domain-separated content-free blocker-evidence digest for each manual arm. It contains no durable location, root, revision, segment, leaf, file identity, hash, pending label, final ownership, or serialized opener input. `SafeTerminalWriteIntentCount + ManualWriteOrphanCount` equals the source count. `CompletedWorkItemCount + ManualWorkItemOrphanCount` equals the work-item count. A safe source is exactly `Cleaned` or `Erased`. A manual source is exactly `ManualNonrevocable`, or `AdoptedAndLabeled` with one exact linked `ManualBlocker` work item. A safe work item is exactly `Completed`; a manual work item is exactly `ManualBlocker`. Every other phase, state, unlinked adopted source, duplicate work item, count overflow, vector change, or classification mismatch blocks `TerminalInventoryVerified` and Grimoire deletion.

Before the first host-tools marker effect, while the caller holds the installation lock and the matched database plus OS evidence and retained core Campaign snapshot are readable, compute `CampaignMarkerInventoryDigest` under `Arcanum.FullInstallationReset.CampaignMarkerInventory.v1`. Its preimage is the checked `u64` item count followed by entries sorted by RFC-4122 Campaign UUID bytes. Each entry is Campaign UUID, positive prior path revision as `u64`, 32-byte marker digest, 32-byte indexed physical-identity digest, 32-byte canonical display-path digest, and 32-byte same-handle ownership-evidence digest. Duplicate Campaigns, unsorted input, zero revision, missing digest, count overflow, or inventory beyond the approved bounded Campaign maximum fails before a journal or marker effect.

Compute the one `FullInstallationResetOwnerEffectDigest` under `Arcanum.FullInstallationReset.Effect.v1` over these exact ordered fields: reset OperationId; installation ID; host-tools transition ID; positive `ulong` taint-time master-key version; authority fingerprint; database-marker digest; OS marker-bytes digest; signed remediation-action digest; Campaign-marker inventory digest; and reset-scope code `All=1`. Use the policy-v1 canonical UUID, digest, `u64`, and one-byte code encodings without optional fields or text. Persist both digests in `HostToolsMarkerPairResetCheckpointV1` inside the authenticated, anchor-advanced active envelope before the first compare-delete. Every `FullInstallationResetCleanup` child copies that exact owner operation ID and owner-effect digest; its marker digest remains per-Campaign and its apply-request digest is null. Recovery reads the two digests only from the authenticated checkpoint, recomputes the inventory when the retained core snapshot still exists, and conflicts on any change. It never substitutes the attestation digest, remediation-action digest alone, an effect digest from another lifecycle, or a reconstructed child digest.

Before any marker or identity effect, `HostToolsMarkerPairResetCoordinator` reads the OS-secret marker first through its dedicated retained capability, then opens one non-pooled core-only SQLCipher connection and reads the exact `covenant_authority_state` taint row. It constructs the exact Plan 03 evidence records and calls `IHostProcessToolsMarkerPairJoiner.Join(databaseEvidence, osMarkerEvidence)`. New work with no reset checkpoint proceeds only from `HostProcessToolsMarkerPairDisposition.TaintedMatched` with a nonnull `HostProcessToolsMatchedPair`. `Clean`, `PendingBlocked`, and `MismatchBlocked` have no marker or identity effect on that new-work path. The coordinator contains no second pair-classification implementation. After `TaintedMatched`, it verifies the external attestation against that exact joined pair, captures `AcceptedAtUtc`, builds the complete `FullInstallationResetRestartProofV1`, and publishes `PairJournaled` inside the authenticated, anchor-advanced `InstallationResetActiveEnvelopeV2` before the first compare-delete. The checkpoint binds the full signed projection, its digest, both exact evidence records, their digest, Campaign inventory, owner effect, operation, and revision. It persists no live handle or private trust-root material. The coordinator rereads and authenticates the envelope and anchor before compare-deleting the database marker only with a transaction predicate over every previously read field, and compare-deletes the OS marker only through the retained opened identity when its exact bytes still match, followed by the platform-required secure-store or parent durability barrier. It never reconstructs either marker from a path, digest, transition ID, or caller-resupplied attestation.

A mismatch before the first effect leaves both markers untouched. Once `PairJournaled` is durable, recovery authenticates the active envelope and anti-rollback anchor, revalidates the stored signature at its authenticated original acceptance time, replays the sole pair join over the stored exact evidence, and proves each checkpoint-owned exact deletion or absence from its prior pair digests and durability evidence. It does not require a fresh attestation file, a currently unexpired statement, or the now-impossible live `TaintedMatched` pair after either member has been removed. A crash or durability failure after one exact compare-delete leaves the authenticated checkpoint active, keeps startup and identity rotation blocked, and permits only that same signed reset operation to resume the remaining exact phase. A different operation or signed statement cannot take over. A missing marker is idempotent only when the authenticated checkpoint proves that this operation durably deleted that exact member. No new clean identity can publish until both exact compare-deletes and their durability barriers are proven. Any unproven absence, changed surviving marker, signature failure, pair-evidence mismatch, envelope rollback, or missing key or anchor remains a manual-remediation blocker.

After the authenticated marker-pair checkpoint proves both exact members durably absent, and before deleting the database or active path identity, full installation reset reopens the retained core Campaign snapshot and proves its inventory digest still equals the checkpoint. Under that same held installation lock, `HostToolsMarkerPairResetCoordinator` rereads and authenticates the live V2 envelope and anchor, revalidates its restart proof, proves `PairAbsenceVerified`, and creates its sealed `AuthenticatedFullInstallationResetJournalProof` bound to the exact signed operation, attestation, envelope revision, envelope digest, full-reset owner-effect digest, and Campaign inventory digest. Task 7 creates `FullInstallationResetMarkerCleanupAuthority` only from that proof. It never accepts the raw decrypted active record or caller-supplied matching digests. Through one initialized non-pooled core connection and caller-owned immediate transaction, it calls `ICampaignPathMarkerLifecycle.PrepareFullInstallationResetCleanupAsync` with one ordered seed per authenticated Campaign inventory entry. An `Opened` seed carries the producer-owned root authority. An unavailable root carries `Unavailable` durable blocker evidence; a physical or ownership mismatch carries `Mismatch` durable blocker evidence. That method first revalidates the proof and held lock, then commits one exact random `FullInstallationResetCleanup` child per Campaign with the shared operation ID and owner-effect digest, per-Campaign inventory and observation evidence, null gate-operation and apply-request fields, then returns the vector receipt. Task 17 commits the transaction before any Campaign marker effect. A crash after host-tools pair deletion cannot erase an unavailable Campaign entry from the child inventory.

Call `ReconcileFullInstallationResetCleanupAsync` with that receipt and the same authority. It first revalidates the proof and held lock. For `Opened`, it uses only the retained no-follow root authority and either durably proves deletion of the exact owned marker and advances that child directly to `Completed`, or records `ManualBlocker` for a later durability or same-handle mismatch. For `Unavailable` and `Mismatch`, it performs no marker-store, opener, codec, or filesystem call and terminalizes that exact child as `ManualBlocker` from its authenticated durable evidence. It uses no Covenant gate disposition or post-disposition finalizer. A path or reconstructed digest never authorizes deletion. The lifecycle persists terminal counts and the vector digest into the checkpoint, advances the authenticated active envelope and anchor, and verifies readback before managed-file reconciliation begins. Exactly deleted markers are gone before identity rotation. Marker blocker outcomes remain as untrusted orphans for Task 7's explicit no-follow takeover protocol.

After marker cleanup is terminal, `HostToolsMarkerPairResetCoordinator` rereads the retained database, authenticates the current V2 envelope and anchor, revalidates the persisted signed restart proof and marker receipt, and creates one sealed nonserializable `AuthenticatedFullInstallationResetManagedFileJournalProof`. Its constructor is private to the coordinator. It binds the exact held `ArcanumMaintenanceLock`, operation, installation, current envelope revision and digest, owner-effect digest, Campaign-marker terminal digest, and still-readable database-file identity. A raw active record, decrypted checkpoint, caller-supplied digest, ordinary operation lease, or reconstructed connection cannot create it. `FullInstallationResetManagedFileErasureAuthority` has a private constructor and one internal factory that accepts only this proof. It retains but never owns or disposes the lock or proof, exposes no lease, SQL scope, path, connection, handle, or serializable field, and uses `Interlocked.Exchange` for one-shot disposal. Before every transaction, open, verify, compare-delete, fsync, checkpoint publication, and final inventory read, `AssertCurrentAsync` reauthenticates the envelope and anchor, requires the same stopped-host lock and database identity, and proves that no unowned reset revision or publication has superseded it. A successful checkpoint publication returns an authenticated successor envelope and anchor to the producer, which atomically advances its private expected revision and digest before another effect. Only this exact operation-owned successor may keep the proof current; a caller-supplied revision, skipped revision, concurrent writer, replay, or failed publication makes the authority stale.

`FullInstallationResetManagedFileReconciler.ReconcileAsync(FullInstallationResetManagedFileErasureAuthority authority, CancellationToken cancellationToken) -> Task<Result<FullInstallationResetManagedFileCheckpointV1>>` is the sole stopped-host entry point. It first publishes `InventoryPrepared` with the exact source vector before the first managed-file reconciliation effect. For each nonterminal write intent it invokes Plan 03's sole `ManagedFileWriteIntentRecoveryService.RecoverForFullInstallationResetAsync(Guid sourceWriteOperationId, FullInstallationResetManagedFileErasureAuthority authority, CancellationToken cancellationToken) -> Task<Result<ManagedFileWriteIntentPhase>>`, so partial create, rename, parent-fsync, adoption, safe cleanup, and manual classification use the existing created-child lifecycle and phase graph. It never deletes or adopts from a leaf, digest, or copied caller evidence.

For each resulting `AdoptedAndLabeled` source the reconciler calls Task 9A's internal `CovenantManagedFileErasureKernel.ReconcileSourceForFullInstallationResetAsync(Guid sourceWriteOperationId, FullInstallationResetManagedFileErasureAuthority authority, CancellationToken cancellationToken) -> Task<Result<LocalErasureWorkItemState>>`. That method either reuses the exact existing work item or inserts one work item from the current producer and label under `CovenantFamilyMaintenance`, then uses the same opener, same-handle ownership verifier, compare-delete, parent fsync, deletion evidence, label removal, and source completion as ordinary and exclusive erasure. Every preexisting nonterminal work item resumes through `ResumeWorkItemForFullInstallationResetAsync(Guid workItemId, FullInstallationResetManagedFileErasureAuthority authority, CancellationToken cancellationToken) -> Task<Result<LocalErasureWorkItemState>>` on that same implementation. These methods are internal Infrastructure-only overloads and are callable only from the full-reset reconciler, enforced by architecture tests. No Core authority enum or ordinary lease gains a stopped-host arm.

The full-reset authority is installation-wide only because its proof binds the authenticated full-installation-reset record and stopped host. Before each database or filesystem effect the shared implementation rereads the current source owner, verifies it belongs to this retained installation and source vector, and borrows `CovenantFamilyMaintenance` only on that caller-owned transaction. A current `ManualNonrevocable` source receives no filesystem effect and becomes one manual write orphan. An existing or newly reached `ManualBlocker` work item leaves the external file and live label untouched and contributes one manual work-item orphan plus its linked source classification. These manual outcomes do not become deletion authority. They are included in the authenticated terminal classification so full reset can report external remnants after the Grimoire is gone. A malformed, missing, extra, duplicated, cross-installation, nonterminal, or changed row keeps the active reset journal and database intact.

The reconciler publishes `WriteIntentsReconciled` only after every source is safe-terminal or has one exact work item, freezes the complete work-item vector, then publishes `WorkItemsReconciled` only after every work item is `Completed` or `ManualBlocker`. A filesystem syscall always returns before its corresponding row CAS, that row CAS commits before the next active-envelope revision, and crash recovery adopts the same operation-owned row rather than issuing a second effect. `TerminalInventoryVerified` requires one final bounded reread that reproduces both vectors, all counts, and `TerminalClassificationDigest`. Only that authenticated phase permits full reset to remove the Grimoire and application-owned protected state, remove joined nonrevocable disclosure evidence, and rotate installation, path-identity, recovery-envelope, and authority identities. The coordinator, installation reset service, and recovery handler cannot resolve the managed-file opener or verifier directly and contain no second file-deletion algorithm.

The attested full-reset credential terminalizer derives Task 15's exact installation, journal-key, and anchor account names only from the authenticated `ProfileNamespaceDigest`. After it revalidates the same full-reset authority, persists terminal Campaign-marker cleanup counts, and durably completes Grimoire and database cleanup, it calls `ProveFullResetTerminalAsync`. Only `NeverRestoredAbsence` or `ClosedAnchor` succeeds. It publishes the returned projection in the active V2 envelope and anchor before the first credential effect. The cleanup then verifies or compare-removes the exact namespaced anchor, journal key, and installation identity in that order, advancing the four literal credential-cleanup phases after each verified effect. A projection-null account is proven absent rather than deleted. A projection-nonnull account must still match its exact value digest. After `VerifiedAbsent`, it inventories all three derived identities and requires all absent before installation, path-identity, recovery-envelope, or authority identity rotation.

A live or indeterminate restore anchor, active restore journal, canonical lookalike, staging-index evidence, wrong profile suffix, changed account value, missing authenticated projection, Campaign-marker child not terminal, database cleanup failure, skipped credential phase, partial credential removal without its prior envelope revision, or durability failure keeps reset active and identity publication blocked. Recovery authenticates the reset envelope using its separate reset-active key, so deletion of the Task 15 journal key cannot strand the full-reset owner. Ordinary reset and every nonattested cleanup retain all three Task 15 accounts byte-for-byte. The reset-active key and anchor follow their separate post-record-retirement cleanup and are never counted as one of the Task 15 trio.

- [ ] **Step 4: Run the green reset and erasure tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantResetTests|FullyQualifiedName~CovenantErasureRecoveryTests|FullyQualifiedName~CovenantFactoryErasureTests|FullyQualifiedName~CovenantManagedFileErasureKernelTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~FullInstallationResetRemediationAttestationVerifierTests|FullyQualifiedName~FullInstallationResetRemediationTrustRootProviderTests|FullyQualifiedName~FullInstallationResetEffectDigestTests|FullyQualifiedName~FullInstallationResetManagedFileReconciliationTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~InstallationResetContractTests|FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~InstallationResetApplyBoundaryTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~LongRunningOperationRecoveryRegistryTests|FullyQualifiedName~LongRunningOperationCrashRecoveryTests"
```

Expected: PASS across every crash phase, V2 compatibility, blocked file, busy WAL, compaction fallback, and same-process reopen.

- [ ] **Step 5: Refactor and verify lifecycle races**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantReset|FullyQualifiedName~CovenantErasure|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~DataRetentionQuarantineRecoveryTests|FullyQualifiedName~DataRetentionDurabilityBoundaryTests"
dotnet build RetroDownfall.Arcanum.slnx
git diff --check
```

Expected: all commands exit zero. A stale collector, reader, rebuild, or turn cannot publish through a changed dataset or operation generation.

### Task 18: Close the Plan 04 surface and lifecycle integration gate

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantSurfaceInventoryTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantProtectedResponseTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantRecoveryCoverageTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CliJsonContextCoverageTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationRecoveryRegistryTests.cs`

**Interfaces:**

- Consumes: every production surface and lifecycle handler from Tasks 1 through 17.
- Produces: an executable inventory proving route metadata, serializer ownership, recovery parity, exact protected headers, same-snapshot reads, lease-bound response ownership, lifecycle transition ordering, and dependency direction before Plan 05 begins.

- [ ] **Step 1: Write the failing cross-surface inventory tests**

Add exact methods:

```csharp
[Fact]
public async Task Every_covenant_route_has_unique_name_typed_body_authority_rate_limit_and_exact_header_policy()

[Fact]
public void Every_new_covenant_route_matches_the_exact_lease_disposition_inventory()

[Fact]
public void Existing_conditional_read_and_lifecycle_routes_match_the_closed_inventory()

[Fact]
public void Existing_direct_artifact_purge_routes_match_the_closed_inventory()

[Fact]
public async Task Every_context_eligible_route_declares_prebinding_context_policy_metadata()

[Fact]
public void Every_api_cli_and_recovery_payload_has_exactly_one_source_generated_owner()

[Fact]
public void Every_covenant_operation_kind_has_exactly_one_registry_descriptor_and_one_handler()

[Fact]
public async Task Protected_success_error_replay_and_stream_paths_revalidate_before_first_byte()

[Fact]
public async Task Protected_success_error_replay_and_stream_paths_set_the_exact_three_header_tuple_before_first_byte()

[Fact]
public async Task Explicit_none_untainted_cacheable_response_omits_protected_private_headers()

[Fact]
public void Every_lease_required_route_returns_only_lease_bound_result_or_an_approved_content_free_result()

[Fact]
public void Every_conditional_derived_read_or_transfer_uses_its_exact_same_snapshot_port()

[Fact]
public async Task Reset_racing_serialization_waits_for_the_protected_response_lease()

[Fact]
public void Every_lifecycle_operation_kind_has_one_descriptor_handler_and_transition_owner()

[Fact]
public void Every_non_http_protected_operation_matches_the_exact_lease_and_transition_inventory()

[Fact]
public void Every_all_scope_snapshot_and_full_backup_uses_exactly_one_installation_read_lease()

[Fact]
public void Every_fork_or_import_transfer_has_one_atomic_compound_lease_owner()

[Fact]
public void Every_durable_exclusive_journal_finalizes_only_after_successful_matching_disposition()

[Fact]
public void Every_exclusive_owner_selects_exactly_one_success_rollback_or_keep_closed_disposition()

[Fact]
public void Every_campaign_marker_consumer_delegates_to_the_single_shared_codec_lifecycle()

[Fact]
public void Campaign_path_apply_probes_receipt_and_intent_before_exclusive_acquisition()

[Fact]
public void Tasks_10_16_and_17_delegate_to_the_same_erasure_kernel_registrations()

[Fact]
public void Restore_recovery_blocks_bootstrap_readiness_until_health_authority_and_writer_publication()

[Fact]
public void Full_installation_reset_requires_external_attestation_and_exact_marker_pair_compare_delete()

[Fact]
public void Full_installation_reset_uses_the_signed_operation_identity_and_shared_Campaign_marker_lifecycle_before_identity_removal()

[Fact]
public void Full_installation_reset_recovery_after_pair_deletion_uses_only_authenticated_checkpoint_owned_evidence()

[Fact]
public void Covenant_requested_operation_adapter_redeclares_no_plan03_contract_or_store()

[Fact]
public void Every_enablement_and_destructive_confirmation_uses_shared_disclosure_and_help_targets_before_control_or_prompt()

[Fact]
public void Cli_covenant_handlers_depend_on_http_client_except_the_named_offline_security_command()

[Fact]
public async Task Disabled_stateless_path_calls_no_covenant_optional_service_or_store()
```

- [ ] **Step 2: Run the red integration inventory**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSurfaceInventoryTests|FullyQualifiedName~CovenantProtectedResponseTests|FullyQualifiedName~CovenantRecoveryCoverageTests"
```

Expected: FAIL on the first missing or incorrectly attributed endpoint, payload, or recovery owner found by the inventory.

- [ ] **Step 3: Make only inventory-driven corrections**

For each failure, correct the owning Task 1 through 17 implementation. The final expected endpoint-name set is:

```text
Covenant_List
Covenant_Query
Covenant_Detail
Covenant_Versions
Covenant_Sources
Covenant_Explain
Covenant_SetPrepare
Covenant_RetirePrepare
Covenant_Set
Covenant_Retire
Covenant_SchemaRepair
Covenant_FamilyReinitializePrepare
Covenant_FamilyReinitialize
Covenant_IndexRebuild
Campaign_PathStatus
Campaign_PathPrepare
Campaign_PathApply
Session_CampaignBindingStatus
Session_CampaignBindingPrepare
Session_CampaignBindingApply
```

Pin this exact authority, response, and lease-disposition table for that set. Every row uses the exact protected-header tuple on success and mapped failure.

| Endpoint name | Required response ownership |
|---|---|
| `Covenant_List` | Operator read authority; `AllScopes` uses one `CovenantInstallationReadLease`, otherwise one exact scoped `CovenantReadLease`; it spans the canonical snapshot and JSON completion. |
| `Covenant_Query` | Operator read authority; `AllScopes` uses one `CovenantInstallationReadLease`, otherwise one exact scoped `CovenantReadLease`; it spans the FTS or fallback snapshot and JSON completion. |
| `Covenant_Detail` | Operator read authority; one exact scoped `CovenantReadLease` spans the scoped-key snapshot and JSON completion. |
| `Covenant_Versions` | Operator read authority; one exact scoped `CovenantReadLease` spans the version snapshot and JSON completion. |
| `Covenant_Sources` | Operator read authority; one exact scoped `CovenantReadLease` spans the source snapshot and JSON completion. |
| `Covenant_Explain` | Operator read authority; the management-explain loader creates one lease, transfers it exactly once, and the response owns it through JSON completion. |
| `Covenant_SetPrepare` | Operator authority; a Global effect spanning Campaign shadows or all-scope effect uses one `CovenantInstallationReadLease`, otherwise one exact scoped read lease; it spans effect snapshot, token creation, and preflight JSON completion. |
| `Covenant_RetirePrepare` | Operator authority; a Global effect spanning Campaign shadows or all-scope effect uses one `CovenantInstallationReadLease`, otherwise one exact scoped read lease; it spans effect snapshot, token creation, and preflight JSON completion. |
| `Covenant_Set` | Operator authority; caller-owned write lease is rechecked in the immediate transaction and retained through mutation-result JSON completion. |
| `Covenant_Retire` | Operator authority; caller-owned write lease is rechecked in the immediate transaction and retained through mutation-result JSON completion. |
| `Covenant_SchemaRepair` | Operator authority; one drained global `CovenantExclusiveLease` spans repair, health publication, result JSON completion, one typed disposition with `lifecycleCt`, and the mandatory repair-journal finalizer after successful disposition; the service borrows it and acquires no nested lease. |
| `Covenant_FamilyReinitializePrepare` | Operator authority; one `CovenantInstallationReadLease` spans the all-installation protected inventory and plan JSON completion. |
| `Covenant_FamilyReinitialize` | Operator authority; the short route write lease covers receipt-first LRO creation and the content-free 202 response, then the recovery worker owns the exclusive transition. |
| `Covenant_IndexRebuild` | Operator authority; the short route write lease covers LRO creation and content-free 202 serialization, and each worker batch acquires and rechecks its own accelerator lease. |
| `Campaign_PathStatus` | Operator authority; `AllCampaigns` uses one `CovenantInstallationReadLease`, otherwise one exact Campaign read lease; it spans status snapshot and JSON completion. |
| `Campaign_PathPrepare` | Operator authority; one exact Campaign read lease spans no-follow inspection, plan creation, and JSON completion. |
| `Campaign_PathApply` | Operator authority; one short Campaign read lease probes receipt and intent before token decode or exclusive acquisition. Terminal replay retains that lease through JSON. New or active work releases it before one Campaign-exclusive lease spans the filesystem and availability transition, persists `ReopenPending`, selects its typed disposition, and transfers the mandatory marker-journal finalizer for invocation only after successful disposition. |
| `Session_CampaignBindingStatus` | Operator authority; `AllSessions` uses one `CovenantInstallationReadLease`, otherwise one exact scoped read lease; it spans binding snapshot and JSON completion. |
| `Session_CampaignBindingPrepare` | Operator authority; one exact scoped read lease spans binding/effect snapshot, token creation, and JSON completion. |
| `Session_CampaignBindingApply` | Operator authority; caller-owned write lease is rechecked in the immediate receipt-plus-binding transaction and retained through JSON completion. |

Keep a second exact registry for existing conditional derived-read routes:

```text
QuerySessions
GetCampaignSessions
GetSession
UpdateSession
GetSessionEntries
GetSessionAttachments
DownloadSessionAttachment
StreamSession
ExportSession
ForkSession
PostSessionRest
SessionDivination
ListSagaMemories
SagaDivination
ListLexiconEntries
GetLexiconEntry
SearchMemory
GetSessionMemoryStatus
GetSessionMemorySources
ExplainSessionMemory
ExportCampaign
```

`StreamSession` covers both bounded replay and live frames. `SearchMemory` covers generic memory results plus its closed `Saga` and `Lexicon` scopes; a test invokes all three selector paths. Every name in this registry uses `RequireConditionalCovenantReadAuthority`. Every route except `ForkSession` obtains its read through Task 1 `IProtectedDerivedReadStore` and retains the caller-owned read lease through the last JSON, archive, attachment, NDJSON, or SSE byte. A request whose snapshot can span Campaigns uses exactly one `CovenantInstallationReadLease` from `AcquireInstallationReadAsync`; a request bounded to Global or one Campaign uses one matching `CovenantReadLease`. Both are passed as `ICovenantSnapshotReadLease`, and no port supplements coverage through nested acquisition. `ForkSession` atomically acquires one Plan 02 `CovenantProtectedTransferLease`, passes it to Plan 03 `IProtectedArtifactTransferStore.ForkSessionAsync`, uses its single snapshot/exclusive capability instead of separate read and exclusive leases, and transfers the lease, required disposition, and mandatory transfer-journal finalizer through the last success or mapped-error JSON byte. The response invokes that finalizer only after successful matching disposition and retains `ReopenPending` on disposition or finalizer failure. Feature-disabled reads still enforce taint. `GetMemoryStatus`, `GetMemorySources`, `ExplainMemory`, and `GetDataRetentionStatus` are separately registered content-free aggregate exceptions and may not read an optional protected table.

Keep this exact context-eligible inference-wrapper registry:

```text
PostIntelligencePing
PostIntelligencePingStream
PostIntelligenceContextInspect
PostOpenAiChatCompletions
TestPrompt
Prompt_Execute
Prompt_ExecuteStream
Spell_Execute
Spell_ExecuteStream
Spell_Cast
PostSessionRest
```

Each wrapper declares pre-binding context-policy metadata. Buffered output, `InferenceExecuteWriter`, `SseStreamWriter`, OpenAI SSE, and idempotency replay all use the shared protected-header helper when the frozen request or persisted response sensitivity is protected. An explicit `none`, stateless, untainted response remains eligible for the generic response cache and does not receive the protected private-header tuple.

Keep this exact direct artifact-purge registry:

| Endpoint name | Authority and purge policy |
|---|---|
| `DeleteSessionEntry` | Conditional purge metadata; labeled Entry requires `SensitivityRetentionPurge`, one caller-owned ordinary authority, one shared-kernel transaction, and a protected content-free result. |
| `CompactSession` | Conditional purge metadata; the complete tool-group-safe deletion set is dispatched per artifact through the shared kernels with the same caller-owned ordinary authority, generation recheck, and a protected content-free count result. |
| `DeleteSagaMemory` | Conditional purge metadata; labeled Saga deletion uses the shared database kernel and a protected content-free result. |
| `DeleteAllSagaMemories` | Conditional purge metadata; bounded stable pages dispatch every labeled Saga through the shared database kernel before ordinary rows, with no set-based labeled bypass. |
| `DeleteLexiconEntry` | Conditional purge metadata; labeled Lexicon deletion uses the shared database kernel and a protected content-free result. |
| `EmbeddingsReset` | Conditional purge metadata; each labeled base or projection row uses the shared guarded-purge kernel before the existing content-free deletion-count result. |

The unprotected arm of each route preserves its existing contract and touches no optional Covenant service when availability proves no label can exist. The protected arm returns `ApiResponse<T>` with explicit source-generated type information and the exact three-header tuple on success and failure. A test rejects direct calls from these endpoints or `ContextCompressionService` to legacy raw deletion methods for a potentially labeled artifact, and rejects any second managed-file opener, verifier, compare-delete, fsync, or label-removal algorithm outside Task 9A.

Keep an exact lifecycle-policy registry with these existing and new route names:

| Endpoint name | Authority and lease policy |
|---|---|
| `PlanDataRetentionPrune` | Operator metadata; all-Campaign inventory uses one `CovenantInstallationReadLease`, otherwise one exact scoped read lease, retained through plan serialization. |
| `ApplyDataRetentionPrune` | Operator metadata, dataset recheck inside each immediate purge transaction, protected result boundary. |
| `DeleteDataRetentionSession` | Operator metadata, owner-journal writer generation recheck, protected result boundary. |
| `DeleteDataRetentionAttachment` | Operator metadata, owner writer generation recheck, protected result boundary. |
| `ResetDataRetentionMemory` | Operator metadata, no ordinary route lease that can self-block drain; Task 17 coordinator owns exclusive authority through health publication and reopen, then returns an approved content-free result. |
| `PlanFactoryResetDataRetention` | Operator metadata; one `CovenantInstallationReadLease` spans the all-installation inventory and plan serialization. |
| `FactoryResetDataRetention` | Operator metadata, no ordinary route lease that can self-block drain; Task 17 factory coordinator owns the exclusive transition, then returns an approved content-free result. |
| `EmbeddingsReset` | Conditional sensitivity-retention metadata, bounded stable-page processing, generation recheck in every guarded purge transaction, and exact protected headers when any labeled artifact is selected. |
| `RegisterCampaign` | Operator metadata; the Campaign-exclusive lease spans unresolved creation, marker protocol, path-health publication, and protected `CampaignDto` serialization, then selects the exact success, rollback, or keep-closed disposition; only successful matching disposition invokes the marker-journal finalizer. |
| `DeleteCampaign` | Operator metadata; Campaign-exclusive owner-journal and marker-cleanup transition, followed by `CommitAndReopen` and composite finalization. Exact cleanup reaches `Completed`; unavailable or mismatched workspace evidence reaches visible `Orphaned` without touching the file or retaining the historical Campaign close. Then return the approved content-free result after availability publication. |
| `ExportSession` | Conditional read metadata and read lease through the last archive byte; taint rejects before output. |
| `ExportCampaign` | Conditional read metadata and read lease through the last archive byte; Covenant and tainted artifacts are excluded with typed counts. |
| `Covenant_SchemaRepair` | Operator metadata; close and drain one global exclusive lease, borrow and revalidate it in repair, publish health, serialize through the exclusive response arm, select one typed disposition with `lifecycleCt`, and invoke the repair-journal finalizer only after that disposition succeeds. |
| `Covenant_FamilyReinitializePrepare` | Operator metadata and one `CovenantInstallationReadLease` through all-installation inventory and plan serialization. |
| `Covenant_FamilyReinitialize` | Operator metadata and approved content-free 202 result; the recovery worker, rather than an ordinary route lease, owns the exclusive transition. |
| `Covenant_IndexRebuild` | Operator metadata and short route lease through 202 serialization; every batch has its own generation and accelerator recheck. |

Keep this exact non-HTTP protected-operation inventory:

| Production owner | Required lease or transition policy |
|---|---|
| `BackupService.CreateAsync` | Acquire one `CovenantInstallationReadLease` through `AcquireInstallationReadAsync` before full inventory and retain it through snapshot completion and the last archive byte. |
| `BackupCreateRecoveryHandler` | Resume the same disclosure subject under a fresh `CovenantInstallationReadLease`; persist no live lease. |
| `BackupSessionImporter.ImportAsync` | Authenticate the source manifest; construct the exact Plan 03 request with import identity, source Session, source-evidence digest, destination Session and explicit Campaign mapping, authenticated bounded manifest digest and checked counts; acquire one `ImportedSessionSourceLease` plus one destination `CovenantProtectedTransferLease`; retain both through commit and cleanup. |
| `BackupRestoreService.RestoreAsync` | Compute the sole Core restore-effect digest, commit the mandatory authenticated marker checkpoint including its zero-child arm, advance the V2 journal envelope and OS-secret anti-rollback anchor, then own the exclusive restore transition through staged validation, swap, storage-health proof, authority publication, and disclosure-writer reopen. Select `CommitAndReopen`; use `KeepClosed` for any postcommit uncertainty. |
| `BackupRestoreRecovery` | Under the caller-held installation lock and before any database-dependent precheck, require an exact V2 envelope plus anchor match, verify retained-parent and live/staged/rollback node identities, close only the one journal-ahead crash window, and reject every tamper, rollback, replay, or unauthenticated lookalike before topology mutation. After the exact live database passes host-tools, catalog, and core convergence, reconstruct and resume the `BackupRestore` exclusive owner before every other database recovery, verify the checkpoint's exact nonempty or zero child set, keep admission closed through health proof, authority publication, and disclosure-writer reopen, and publish readiness only after `CommitAndReopen`; any uncertainty uses `KeepClosed`. |
| `CampaignPathStartupRecovery` | Before readiness, reconstruct each exact path or deletion owner from the immutable marker and owner-deletion intents, call `ResumeCampaignExclusiveAsync`, delegate filesystem work to the shared lifecycle, and keep the historical Campaign scope closed until matching disposition plus ordinary or CampaignDelete composite finalization succeeds. A finalized visible orphan no longer closes that deleted scope. |
| `CovenantLocalErasureStartupRecovery` | After core convergence and before optional initialization or any writer, reset, retention, worker, endpoint, or ready state, borrow the caller-held installation lock and reconcile each nonterminal local-erasure row only from its exact `AdoptedAndLabeled` producer ownership and label. It never recreates an ordinary route lease or trusts caller location evidence. Completed deletion removes the label and terminalizes producer ownership atomically; a mismatch becomes terminal manual evidence without touching either. |
| `CovenantSchemaRepairStartupRecovery` | Before readiness, reconstruct the exact `SchemaRepair` owner from the core repair intent, call `ResumeExclusiveAsync`, revalidate catalog and health phases, and keep global admission closed until the matching `CommitAndReopen` or proven `RollbackAndReopen` disposition and its repair-journal finalizer succeed. |
| `DataRetentionRecoveryHandler` | Each prune batch rechecks its captured dataset generation in the immediate transaction. |
| `DataRetentionMutationRecoveryHandler` | The kind's sole descriptor runs at `BeforeStateWrites` for every supported checkpoint version. Its V3 Covenant reset arm reconstructs the single Task 17 exclusive transition owner and passes its exclusive authority to both Task 9A kernels before any ordinary writer or optional initializer; other mutation arms retain their existing scoped recovery policy under the same earlier descriptor. |
| `DataRetentionFactoryResetRecoveryHandler` | Healthy-catalog Covenant erasure delegates the entire exclusive transition and all protected-artifact deletion to the single Task 17 owner and Task 9A kernels. |
| `CovenantIndexRebuildRecoveryHandler` | Each bounded batch owns an accelerator lease and rechecks dataset, accelerator epoch, and Campaign-deletion sequence before commit. |
| `CovenantFamilyReinitializeRecoveryHandler` | Own the exclusive reinitialize transition, pass that exact authority to both Task 9A kernels before family drop, then prove health, publish authority, reopen the writer, and select `CommitAndReopen`; use `KeepClosed` for any uncertainty. |
| `CovenantRequestedOperationStarter` | Construct one already-defined Plan 03 request and delegate once. It owns no requested-operation contract, store, replay, digest comparison, codec, DTO, or recovery behavior. |
| `HostProcessToolsSecurityCommand.Enable` | Generate or reuse one random transition ID, invoke only Plan 03 `IHostProcessToolsTransitionService.EnableAsync(new HostProcessToolsTransitionRequest(transitionId), cancellationToken)`, require the typed result to echo that same ID, and map it. It owns no lock, security check, marker pair, or transition logic. |
| `InstallationResetService.ApplyFullAsync` | Require exact `FullInstallationResetRequest` whose operation ID is copied from the signed attestation; under the stopped-host lock, consume Plan 03's joiner and require `TaintedMatched` before journaling new work, resume later phases only from the authenticated same-operation journal, compare-delete both exact host-tools members, delegate every owned Campaign marker to Task 7's intent-first exact cleanup, then perform shutdown-only full identity rotation. It never advertises an in-process healthy reopen. |

Restore, family reinitialize, Covenant reset, and healthy-catalog factory erasure must all prove this ordering: close admission, cancel and drain affected leases, commit and verify storage health, publish the committed key/issuer/availability transition through `ICovenantAuthorityTransitionPublisher` while the exclusive lease is still held, reopen the disclosure writer, then call `CompleteAsync(CommitAndReopen, lifecycleCt)` as the only gate and general-admission reopen. A proven precommit abort calls `CompleteAsync(RollbackAndReopen, lifecycleCt)`. Any postcommit uncertainty or failure discovered before disposition calls `CompleteAsync(KeepClosed, lifecycleCt)` and leaves recovery active. A failed one-shot disposition already leaves the scope closed and is recorded without a second call. The bounded lifecycle token is independent of an HTTP request after durable mutation. Disposal without one successful disposition never reopens. Full installation reset retains its documented shutdown-only authority rotation.

Fork and selective import must prove a separate closed transfer ordering. Construct the exact `ProtectedSessionTransfer` recovery owner from the request's immutable operation ID and Core-computed transfer-effect digest, then atomically call `AcquireProtectedTransferAsync` for the immutable Global or Campaign scope. Read through that lease's snapshot capability, stage blobs under its exclusive arm, revalidate it in the destination immediate transaction, commit, and retain it through any required protected response or import cleanup. Recovery uses only `ResumeProtectedTransferAsync` with the persisted owner and scope. A verified commit or proven precommit cleanup first persists `ReopenPending` with exact `CommitAndReopen` or `RollbackAndReopen`, calls that disposition exactly once, invokes the mandatory transfer-journal finalizer only after success, and then disposes. Postcommit uncertainty remains at `DatabaseCommitted` or the last proven earlier phase, selects `KeepClosed`, uses the sealed no-op finalizer, and leaves the owner recoverable. Failed disposition or finalizer retains `ReopenPending`. Selective import additionally holds its independent `ImportedSessionSourceLease` through destination commit. No transfer path acquires a separate Covenant read lease, nests an exclusive lease, or persists either live capability. Deterministic races with Campaign deletion and reset have one serial outcome and no partially visible destination graph.

The closed approved content-free protected-response set is `Campaign_PathApply`, `DeleteCampaign`, `ApplyDataRetentionPrune`, `DeleteDataRetentionSession`, `DeleteDataRetentionAttachment`, `ResetDataRetentionMemory`, `FactoryResetDataRetention`, `Covenant_FamilyReinitialize`, `Covenant_IndexRebuild`, `DeleteSessionEntry`, `CompactSession`, `DeleteSagaMemory`, `DeleteAllSagaMemories`, `DeleteLexiconEntry`, and `EmbeddingsReset`. These results may contain only operation identity, status, bounded counts, revision, closed phase, blocker, and remediation codes, plus optional domain-separated digests. They contain no free-form public summary, path, title, summary, Entry, attachment metadata, snippet, Covenant key, provenance, marker identity, or protected receipt detail. Content-free is a payload constraint, not a lease waiver. A row whose policy retains a read, write, or exclusive lease through response completion still uses `CovenantLeaseBoundJsonResult<T>` or its exclusive arm. Only a coordinator result emitted after its exclusive lease has been successfully disposed may use the approved ordinary content-free response. Every result still uses `ApiResponse<T>`, explicit source-generated type information, mapped status, and the exact protected-header tuple. Every other protected route is lease-bound through response completion.

The inventory must prove:

1. Every route belongs to the authenticated `/api` group and host rate limiter.
2. Every protected success, mapped error, idempotency replay, buffered body, archive, attachment download, NDJSON stream, and SSE stream sets exactly `Cache-Control: no-store, private`, `Pragma: no-cache`, and `Expires: 0` before its first byte.
3. Every lease-required HTTP route returns only `CovenantLeaseBoundJsonResult<T>` or `CovenantLeaseBoundStreamResult`, including content-free routes whose inventory row retains a live route lease. A coordinator may use the approved ordinary content-free result only after successful exclusive disposition and disposal. No lease-required route returns plain `Results.Json`, a plain `TypedResults` result, `IdempotencyReplayResult`, or a raw stream directly.
4. Every conditional derived read except `ForkSession` uses the single same-snapshot protected read port, retains its lease through serialization, and fails closed on missing or mismatched label evidence even while Covenant is disabled. `ForkSession` uses the one atomic compound transfer lease and protected transfer port with the same response-ownership guarantee.
5. Aggregate memory and retention status have no Covenant authority requirement, use nullable unavailable counts rather than invented zero, and contain no protected detail.
6. Every eligible inference wrapper has context-policy metadata before binding. Explicit `none` plus an untainted cacheable response omits the protected private headers.
7. Every API payload resolves only through `ArcanumJsonContext`, every CLI-only payload through `CliJsonContext`, and every checkpoint through its Infrastructure context. `FullInstallationResetRequest` and its attestation are CLI-only and resolve only through `CliJsonContext`; the verifier result and trust-root material are nonserializable.
8. `LongRunningOperationKinds.BackupCreate`, `DataRetentionPrune`, `DataRetentionMutation`, `DataRetentionFactoryReset`, `CovenantIndexRebuild`, and `CovenantFamilyReinitialize` each have one descriptor and one DI-registered idempotent handler, with checkpoint-version parity and exactly one transition owner. Plan 03 remains the sole requested-operation contract, store, replay, conflict, and progress-codec owner; Task 9 is caller integration only.
9. A reset race cannot pass lease drain until a delayed protected JSON or stream serialization releases its lease; a lifecycle writer cannot commit after its captured dataset changes. Campaign path apply probes receipt and intent under a short read lease before token decode or exclusive acquisition. Fork and import cannot self-deadlock through nested read and exclusive leases, and Campaign deletion or reset cannot observe a partially committed transfer.
10. Compendium enablement renders the exact Task 1 disclosure and typed provider-help actions before its enable control. CLI family reinitialize, protected restore or purge, Covenant reset, and healthy-catalog factory erasure write the exact disclosure, receipt-backed count, and help targets before invoking confirmation or starting work. No surface offers a local cache control.
11. Generic memory `All`, plaintext exports, logs, metrics, URLs, and exception envelopes expose no Covenant data.
12. Ordinary Covenant reset, healthy-catalog factory erasure, family reinitialize, and credential cleanup retain the database and OS host-tools taint markers. Full installation reset requires the typed external attestation with the exact request operation identity, independent trust root, Plan 03 marker-pair join result `TaintedMatched` before the first journaled effect, authenticated same-operation recovery after either member is absent, and the exact pair compare-delete journal before any clean identity can publish. It then journals and compare-deletes every exact owned Campaign marker through Task 7 before identity removal; only typed unavailable, mismatch, ownership, or durability blockers remain as takeover-only orphans.
13. Every snapshot that can span Campaigns and every full backup owns exactly one `CovenantInstallationReadLease` from `AcquireInstallationReadAsync`; scoped snapshots use `CovenantReadLease`, and no consumer supplements coverage with a nested acquisition.
14. Tasks 10, 16, and 17 resolve the same protected-artifact and managed-file erasure kernels. Each passes caller-owned ordinary or exclusive authority, and no kernel or caller duplicates acquisition, no-follow open, same-handle verification, compare-delete, parent fsync, or label completion.
15. The guarded-root restore phase runs before every database-dependent precheck and must authenticate the V2 envelope, OS-secret anti-rollback anchor, complete recovery owner, and exact retained-parent node identities before converging exactly one live database topology. A proven zero-marker checkpoint is mandatory and distinct from omitted preparation. After core convergence, local-erasure recovery reconciles its exact producer-owned rows before optional initialization or any ordinary writer. Any active Campaign marker, schema-repair, protected-transfer, nonterminal managed-file, or restore authority-recovery journal then blocks host and CLI readiness. Each registered database recovery owner reconstructs only its exact persisted owner and scope, calls the matching `Resume...Async` method where applicable, and keeps admission closed until its terminal health proof and successful exclusive disposition are complete.

- [ ] **Step 4: Run the green Plan 04 suites**

Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "Category!=Perf"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
```

Expected: PASS with no unexpected skip or failure.

- [ ] **Step 5: Refactor, verify the diff, and hand off to Plan 05**

Run:

```bash
git diff --check
git status --short
```

Expected: `git diff --check` exits zero. Status contains only the approved issue #74 implementation and coordinated plan files.

Stop here. Plan 05 owns benchmark gates, native and AOT shipping-RID verification, coverage thresholds, documentation updates, independent reviews, final full-suite evidence, and branch integration.
