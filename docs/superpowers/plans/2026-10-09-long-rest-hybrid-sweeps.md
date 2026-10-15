# The Long Rest hybrid sweeps implementation plan (#91)

> **Superseded in part by [2026-10-13-long-rest-carried-admission.md](2026-10-13-long-rest-carried-admission.md), the plan of what was delivered.** Tasks 1, 2 and 4 (contracts, storage, protected policy) stand, with additions to the unpublished schema 18 (the sweep failure budget columns, the supersede-on-completion trigger and verification indexes); Task 1's provider input and summary bounds became derived from the configured model windows, and Task 2's source capture now excludes retired, pinned and suppressed sources. Task 3 (discovery) stands except that its lexical lane and hierarchy recall are re-specified (Scoped Lexical Ranking, Ancestor-Excluded Cross-Cluster Recall, a generation-scoped full-text index, summaries no longer indexed) and its derived reuse index was dropped. Task 7 (the endpoint and turn seam) stands, except that the registered-root enrichment moved out of the shared Campaign resolver into a Long Rest-scoped enricher and that a refused clean schedule no longer fails a Session turn. Within Task 5, the closed provider dispatch and disclosure ordering stand, but the input and summary bounds became model-window-derived, the owned clients send one request per connection, an unknown-usage send is settled on its attempt and an unsent attempt is released. Task 6's hosting and recovery internals are replaced: the scoped lease holder, the coordinator-backed recovery handler, the per-unit external-effect admission kind and `ILongRestMaintenance.ProcessAsync` on the Core contract no longer exist; the work lease is carried by value, recovery only parks, and the hosted worker or a fresh Session capability readmits. Task 8's lifecycle closure stands, but its qualification and delivery list is replaced by the gate list in the new plan. The text below is kept as it was approved.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking. Design and plan approved; the user selected subagent-driven execution on 2026-10-10.

**Goal:** Deliver #91's deterministic hybrid Saga discovery and resumable authority-scoped consolidation, then merge its qualified commit into memory-enhancement and complete #91.

**Architecture:** Exact generation-owned Saga snapshots feed bounded FTS5, managed vector and recursive hierarchy discovery. A dedicated closed provider lane records every protected physical disclosure before send; the owning SQLite transaction applies immutable decisions and advances item state together. A durable operation coordinator resumes clean work and parks protected work until a fresh authenticated request supplies authority.

**Tech Stack:** .NET 10 Native AOT, hermetic SQLCipher/FTS5, source-generated System.Text.Json, existing managed SphericalKMeans, xUnit, GitHub shipping macOS/Windows gates.

**Spec:** ../specs/2026-10-09-long-rest-hybrid-sweeps.md (approved 2026-10-09).

## Global Constraints

- Implement the approved #91 contract under #75; #95 retains counterfactual credit, usefulness, decay and ranking. No cross-store consolidation or new CLI verb.
- One default-off gate: Arcanum:Features:LongRest. Configuration properties use get/set. Preserve gate-off prompt/retrieval behavior and preview/inspection no-dispatch behavior.
- Source pages: 128. Provider input: 32,768 UTF-8 bytes. Summary: 8,192 UTF-8 bytes. Exact transformation: 16 targets. Hierarchy: 16 children per fold, six levels, explicit maximum-depth forest.
- Lexical queries: 16 tokens, at most 128 UTF-8 bytes each. Lexical/vector/summary top-K: eight each. Minimum vector cosine: 0.92. Expanded leaves: 32 per selected node. Traversal frontier: 128. Merged neighbors: 32 per anchor.
- Bounds govern pages, heaps, groups and checkpoints, never total productive lifetime or corpus size. Invalid/oversized items cannot disappear behind an advanced cursor.
- Exact Global and exact canonical Campaign partitions stay separate. Protected read/disclosure/application requires matching live request scope and identical nonempty sensitivity provenance; preserve original labels and immutable source history.
- Ordinary #93 policy v1, public ApplyAsync, receipt identities and digest corpus remain compatible. Protected decisions use a separate version-2 grammar and evidence-backed SQL trigger arm.
- Parameterized direct SQLite for reads and set mutations; one admitted transaction for canonical receipt, input manifest, suppressions and item completion/cursor. No EF model or numbered production migration.
- Capture the published Core v17 fingerprint and freeze every changed predecessor object before head edits. Add Core v18 declarative objects, bounded source backfill, exact UTC inventory and fresh/evolved parity.
- No authority, plaintext, live task/token/process/DI object or growing per-attempt array in operation checkpoints. Append artifact kind16, maintenance step7 and admission kind17; preserve existing codes.
- Zero tools and no implicit physical retries. Protected chat and embedding each require durable disclosure, attempt association and a second live fence before send. Paid usage remains billed even when output/publication is invalid.
- Preserve unrelated primary-checkout work. Work only in the attached issue-91 worktree. Do not commit implementation until the required local qualification is green; then qualify the committed exact SHA remotely before integration.
- Owning docs travel with behavior: DESIGN, API, Compendium, Command Reference recovery/status notes and OATH delivery status. README changes only for intentional public-front-page content.

## Review Focus

1. A protected Saga label paired with historically clean Annals data or alias/duplicate label identities must never enter a clean lane (Tasks 2, 4, 5).
2. A randomly ordered new MemoryId behind a capture cursor, or source deletion/reinsertion, must neither skip debt nor invalidate an unrelated completed input silently (Tasks 2, 3, 6).
3. Strict-schema/transient SDK retries, malformed paid output and embeddings must each have truthful physical-send/disclosure/accounting evidence (Task 5).
4. Gate/configuration ABA, expired worker/claim ownership and cancellation during provider I/O must prevent stale publication and the next send (Tasks 5, 6, 7).
5. Erasure of any participant, protection transition and staged restore must remove complete dependent discovery state while preserving unrelated scopes (Tasks 2, 4, 8).

## File and interface map

Core contracts live together under `src/RetroDownfall.Arcanum.Core/LongRest/`. Use positional immutable records for data, private constructors for live capabilities, closed persisted enums and length-prefixed/domain-separated hashes. Only request/response DTOs enter ArcanumJsonContext; live capability/context types never enter a serializer.

| Contract | Required data/role |
| --- | --- |
| LongRestSweepRequest | LongRestSweepScopeKind Scope; Guid? CampaignId. Only Global/null or Campaign/nonempty is valid. |
| LongRestSweepScope | Closed exact Global/Campaign factory value plus optional protected partition digest; validated server-side canonical context is separate. |
| LongRestConfigurationStamp | Guid BootId; long Epoch; CovenantDigest ValueDigest. |
| LongRestSweepFence | Canonical scope/owner/path facts; insertion high-watermark; source mutation evidence; dataset/restore/admission/runtime-authority/capability generations; boot/configuration stamp. No paths or plaintext. |
| LongRestRecipeIdentity | Summary/clustering recipe versions, exact provider/model/embedding identities, dimensions and fixed bound values. |
| LongRestDiscoveryGeneration | Guid generation/operation IDs; exact scope/partition, source fence and recipe; closed phase, revision, committed capture/fold coordinates and publication state. |
| LongRestSweep / LongRestSweepProgress | Operation/generation IDs, phase, exact scope/partition, committed item cursor/revision; progress is content-free. |
| LongRestSourcePage / LongRestSourceLeaf | At most 128 leaves; source insertion sequence/mutation epoch; exact target, content/embedding/lifecycle/dependency/label bindings. Plaintext exists only in authorized transient bounded work. |
| LongRestHierarchyWork | Noncyclic Fold plus optional immutable accepted summary staging for embedding-only restart. |
| LongRestSourceEmbeddingBinding | ObservedSaga=1, stored dimension and authoritative raw vector digest; source producer explicitly unknown. Derived LongRestEmbeddingBinding remains exact provider/model evidence. |
| LongRestDispatchCompletion | Accepted/Invalid/Failed outcome, bounded usage/cost and safe stable failure code; current CAS and accounting enforced in Task4/5. |
| LongRestAcceptedHierarchySummary | Exact generation/fold/membership/source/recipe/payload hashes, bounded accepted summary, retained sensitivity labels, paid attempt/usage/disclosure evidence; immutable staging that does not advance the fold cursor. |
| LongRestHierarchyFold / LongRestHierarchyPublication | Exact membership/work hash, layer/group, at most 16 child coordinates; validated summary/vector, complete sensitivity and paid attempt evidence. |
| LongRestCandidateGroup | At most 16 exact targets plus typed discovery-lane evidence; model scores never enter LongRestSnapshot. |
| LongRestWorkIdentity / LongRestDispatchAttempt | Exact generation/item/source/recipe identity; checked physical ordinal, current attempt revision/state, payload/destination digests and optional durable disclosure receipt. |
| LongRestSweepWork | One bounded next source/fold/anchor work identity, expected sweep revision, exact inputs and optional immutable accepted adjudication. Already accepted evidence resumes without another provider call. |
| LongRestAdjudication | Closed equivalent/supersession/no-change decision bound to frozen inputs, lifecycle/dependencies/labels, provider recipe and accepted attempt evidence. |
| LongRestProtectedEvidence | Exact retained label digests and identical nonempty partition provenance; v2 receipt verification evidence. |
| LongRestProviderPayload / LongRestProviderResult | Immutable closed summary/adjudication/embedding request; exact bounded response projection and usage. Successful paid responses carry a closed Valid/Invalid disposition and usage even when parsing/vector validation fails; the executor records usage before returning that disposition. |
| LongRestProviderAdmission | Closed Chat(ModelCallContext) or Embedding(ProviderSettings Provider, string Model, int Dimensions, int ReservedInputTokens, CovenantDigest InputFingerprint) arm. Exact payload kind must match the arm; token/pricing reservation uses its own provider/model and input contract. |
| LongRestMaintenanceCapability / LongRestDispatchCapability | Single-use root operation and single-use exact physical-step capability; borrowed live pending claim/lease, scope/configuration and source vector. Never durable/wire data. |
| LongRestSweepCheckpoint | Version1 scope/generation/phase/cursor/committed work coordinates only; version0 is pre-work creation gap. |

Shared port signatures (CancellationToken is required on every operation):

```csharp
ILongRestDiscoveryStore.StartOrResumeAsync(Guid operationId, LongRestSweepScope scope,
    LongRestSweepFence fence, LongRestRecipeIdentity recipe, CancellationToken ct)
    -> Task<Result<LongRestDiscoveryGeneration>>;
ILongRestDiscoveryStore.CaptureNextSourcePageAsync(Guid generationId,
    long expectedRevision, LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestSourcePage>>;
ILongRestDiscoveryStore.ReadNextFoldAsync(Guid generationId,
    LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestHierarchyWork?>>;
ILongRestDiscoveryStore.CommitAcceptedHierarchySummaryAsync(
    LongRestAcceptedHierarchySummary summary, LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestAcceptedHierarchySummary>>;
ILongRestDiscoveryStore.CommitFoldAsync(LongRestHierarchyPublication publication,
    LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestDiscoveryGeneration>>;
ILongRestDiscoveryStore.PublishAsync(Guid generationId, long expectedRevision,
    LongRestSweepFence fence, LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestDiscoveryGeneration>>;
ILongRestDiscoveryStore.DiscoverAsync(Guid generationId, LongRestTarget anchor,
    LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestCandidateGroup[]>>;

ILongRestAttemptStore.RenewClaimAsync(SessionTurnClaimLease claim,
    CanonicalCampaignContext scope, CancellationToken ct) -> Task<Result<SessionTurnClaimLease>>;
ILongRestAttemptStore.PrepareAttemptAsync(LongRestWorkIdentity work,
    LongRestProviderPayloadKind payloadKind, CovenantDigest payloadDigest,
    CovenantDigest destinationDigest, CancellationToken ct) -> Task<Result<LongRestDispatchAttempt>>;
ILongRestAttemptStore.RecordDisclosureAsync(LongRestDispatchAttempt attempt,
    CovenantDigest receiptDigest, CancellationToken ct) -> Task<Result<LongRestDispatchAttempt>>;
ILongRestAttemptStore.ValidateAttemptAsync(LongRestDispatchAttempt attempt,
    CancellationToken ct) -> Task<Result>;
ILongRestAttemptStore.MarkSentAsync(LongRestDispatchAttempt attempt,
    CancellationToken ct) -> Task<Result<LongRestDispatchAttempt>>;
ILongRestAttemptStore.CompleteAttemptAsync(LongRestDispatchAttempt attempt,
    LongRestDispatchCompletion completion, CancellationToken ct) -> Task<Result<LongRestDispatchAttempt>>;

ILongRestSweepStore.BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope,
    LongRestConfigurationStamp configuration, CancellationToken ct) -> Task<Result<LongRestSweep>>;
ILongRestSweepStore.FindCompletedReplayAsync(CanonicalCampaignContext scope,
    LongRestRecipeIdentity recipe, CancellationToken ct) -> Task<Result<Guid?>>;
ILongRestSweepStore.ReadNextWorkAsync(LongRestSweep expected,
    LongRestMaintenanceCapability? capability, CancellationToken ct) -> Task<Result<LongRestSweepWork?>>;
ILongRestSweepStore.CommitAcceptedAdjudicationAsync(LongRestSweep expected,
    LongRestAdjudication accepted, LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestAdjudication>>;
ILongRestSweepStore.CommitDecisionAsync(LongRestSweep expected,
    LongRestAdjudication decision, LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestSweep>>;

ILongRestProvider.ExecuteAsync(LongRestProviderPayload payload,
    ITurnBudget budget, Func<CancellationToken, Task> beforeSend,
    LongRestProviderAdmission admission, CancellationToken ct) -> Task<Result<LongRestProviderResult>>;
ILongRestMaintenance.CreateCapabilityAsync(Guid operationId,
    ArcanumInvocationContext invocation, SessionTurnClaimLease claim,
    CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct)
    -> Task<Result<LongRestMaintenanceCapability>>;
ILongRestMaintenance.ProcessAsync(Guid operationId,
    LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestSweepProgress>>;
ILongRestSweepCoordinator.RequestAsync(CanonicalCampaignContext scope,
    CancellationToken ct) -> Task<Result<LongRunningOperationDto>>;
ILongRestSweepCoordinator.ResumeAsync(Guid operationId,
    LongRestMaintenanceCapability? capability, CancellationToken ct)
    -> Task<Result<LongRestSweepProgress>>;
```

`ModelCallContext` remains the existing fixed chat admission context inside the Chat admission arm. The Embedding arm has its own exact provider/model/dimensions/input fingerprint and input-token reservation. Task5 implements this explicit arm and its digest/token contract; mismatched payload/admission arms fail before accounting or dispatch.

`SagaLongRestService.ApplyInTransactionAsync(SqliteConnection, SqliteTransaction, LongRestRequest, LongRestProtectedEvidence?, CancellationToken)` is an internal composition seam; its caller owns commit. The public v1 ApplyAsync retains retry/transaction ownership and passes no protected evidence.

## Task 1: Configuration, closed contracts and deterministic work bounds

**Files:** Create Core/LongRest `LongRestSweepContracts.cs`, `LongRestDiscoveryContracts.cs`, `LongRestMaintenanceCapability.cs`, `LongRestDiscoveryPolicy.cs`, `LongRestDiscoveryHash.cs`, `LongRestProviderPayload.cs` and the ports above. Modify `Core/Configuration/PublicConfigurationSettings.cs`, `ArcanumRuntimeDefaults.cs` only for effective configured embedding availability, `Core/Covenant/CovenantEnums.cs`, enum validators and `Compendium.Ux/Models/SettingDescriptors.cs`. Create tests `Configuration/LongRestConfigurationTests.cs`, `LongRest/LongRestDiscoveryPolicyTests.cs`, `LongRest/LongRestMaintenanceCapabilityTests.cs`; extend Compendium `GenericSettingsPreservationTests.cs`.

**Produces:** The exact shared names/ports/bounds above; Features.LongRest=false; LongRestDiscovery=16 and LongRest maintenance step=7; strict source/work hash grammar and root/child consumption behavior. Preserve existing digest literals.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestConfigurationTests|FullyQualifiedName~LongRestDiscoveryPolicyTests|FullyQualifiedName~LongRestMaintenanceCapabilityTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write tests `LongRest_defaults_off_and_round_trips_without_enabling_rollups`, `Unsupported_scope_and_numeric_codes_are_refused`, `Permutations_and_opaque_identity_delimiters_have_distinct_literal_hashes`, `Candidate_merge_is_bounded_and_stable`, and `Root_and_child_capabilities_are_single_use_and_exact_scope`.
- [ ] Use behavior assertions such as `Assert.False(settings.Features.LongRest)`, exact literal hash equality, candidate length <=32, group length <=16, and second Consume returning failure. Include matching Global success, Campaign/Global cross-use, forged owner facts and disposed lease refusal.
- [ ] Run the named classes and observe behavioral RED. For brand-new types introduce only compileable signature stubs before running; a compile error is not the recorded behavioral RED.
- [ ] Implement the smallest code satisfying each test; never serialize live capability types or widen stateless/preview authority. Add a model-free configuration test proving only the configured integration is derived, with no hard-coded provider/model.
- [ ] Run those classes plus existing configuration surface, Campaign rollup and digest corpus tests; observe GREEN. Record RED/GREEN commands/results in the task log. Keep the work uncommitted until final local qualification.

## Task 2: V18 storage, immutable source freeze and projection ownership

**Files:** Create `Fixtures/CoreSchemaVersionSeventeenFixture.cs`; extend `LongRest/LongRestSchemaEvolutionTests.cs`; create `LongRest/LongRestSourceTrackingTests.cs`. Create Infrastructure schema `LongRestSourceIdentityBackfill.cs`, new Tables/FullTextSearch/Triggers and matching Transitions/V18 resources. Modify `GrimoireSchemaVersionChains.cs`, `UtcInstantColumnInventory.cs`, affected artifact-kind CHECK/validator definitions and the existing UTC diagnostic view. Create `Data/LongRest/LongRestDiscoveryStore.cs`, `.Sources.cs`, `LongRestLifecycle.cs`. Expose only the existing Core LongRestMaintenanceCapability.ValidateWorkAsync verifier publicly for exact consumed-root/store fencing; retain internal issuance/refresh access.

**Objects:** `long_rest_sources` (AUTOINCREMENT subject insertion sequence, unique MemoryId, positive MutationEpoch); discovery generations/nodes/embeddings/edges; clean/protected FTS; sweep state/work items; current provider effects; immutable adjudications and protected receipt input evidence. Names all retain the `long_rest_` prefix. Provider effects retain one current ordinal/state and bounded aggregates per work unit, not a growing local attempt array. Immutable accepted evidence remains separately addressable.

**Consumes/produces:** Task1 contracts; implement StartOrResumeAsync and CaptureNextSourcePageAsync. Every leaf binds exact source insertion sequence/epoch, version/hash, embedding/dependency/lifecycle/label evidence. Schema backfill processes <=128 identities per committed batch.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestSchemaEvolutionTests|FullyQualifiedName~LongRestSourceTrackingTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Capture the currently published v17 fingerprint before editing any head object. Freeze all replaced predecessor definitions, including its UTC view; verify the fixture reproduces that fingerprint and historical predecessor fixtures remain stable.
- [ ] Write RED tests `Version_seventeen_evolves_to_the_same_discovery_catalog_as_fresh`, `Backfill_cursor_and_source_rows_commit_together`, `New_source_above_highwater_is_future_debt`, `Random_identity_behind_cursor_is_not_skipped`, `Delete_reinsert_has_a_new_sequence`, and `Mutation_of_included_source_invalidates_generation`.
- [ ] Add a real SQLCipher projection test: index a clean leaf, label it protected, then assert zero matching clean FTS tokens and no reusable dependent clean summary in that same committed mutation. With foreign keys disabled, deletion must still remove companions. Do not settle for checking an invalidation flag.
- [ ] Observe RED via sqlite_master/catalog and behavior assertions; implement one-object resources, v18 chain/pin/backfill, source tracking and protection-transition closure. Cover Saga content/scope/lifecycle, current head/dependencies, embedding replacement, attachment provenance and normalized Saga labels; projection-only suppression is excluded.
- [ ] Install FTS secure-delete and required segment cleanup; give artifact kind16 concrete node/embedding/edge/FTS ownership and digest validation. Add exact schema/UTC/erasure catalogs, without changing CoreVersionNine's frozen inventory.
- [ ] Run schema, source tracking, sensitivity ledger and existing Long Rest evolution tests; observe GREEN and unchanged v1 receipts/source history.

## Task 3: Bounded lexical, vector and recursive hierarchy discovery

**Files:** Create `Core/LongRest/LongRestHierarchy.cs`; create Infrastructure `LongRestDiscoveryStore.Candidates.cs`, `.Hierarchy.cs` and `Weave/LongRestHierarchyBuilder.cs`. Create `LongRest/LongRestDiscoveryStoreTests.cs`, `LongRest/LongRestHierarchyTests.cs`, and `Fixtures/LongRestSweepHarness.cs` over real SQLCipher plus controlled provider fixtures.

**Consumes/produces:** Tasks1–2 generation/source bindings; implement ReadNextFoldAsync, CommitAcceptedHierarchySummaryAsync, CommitFoldAsync, PublishAsync and DiscoverAsync. ReadNextFoldAsync returns any exact reusable accepted-summary stage, so restart performs only its missing embedding. CommitAcceptedHierarchySummaryAsync validates current source/recipe/authority facts and persists immutable accepted text, labels and paid evidence without advancing the fold cursor; final CommitFoldAsync still owns node/vector/edges/cursor atomically. Reuse SphericalKMeans.ClusterLayer/NormalizedDirection/DirectionCosine and length-prefixed Long Rest membership/recipe seeds. The harness stores exact source/label facts and exposes fault boundaries, not test behavior in production.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestDiscoveryStoreTests|FullyQualifiedName~LongRestHierarchyTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED tests for real FTS token/BM25 matches, deterministic score/ID ties, malformed vectors and filter-before-top-K in each exact scope/protection partition. Seed tempting cross-Campaign, Global, unresolved, pinned, retired, consolidated and stale-owner matches before an eligible result; assert they cannot displace it.
- [ ] Implement FTS escaping and bounded ranking heaps; use managed exact-eligibility vector scans in both accelerator states. Reject nonfinite/dimension-mismatched vectors and never route protected discovery through generic Divination.
- [ ] Write `Hierarchy_only_recall_expands_an_upper_node_across_source_pages` with at least 257 eligible leaves and lexical/vector lanes disabled. Assert an actual recursively summarized upper node connects leaves on different pages, every frozen leaf is represented once, and child/level/frontier/expansion bounds hold.
- [ ] Observe RED, then implement stable page partitions and recursive cross-page summary clustering, exact child edges, atomic node/vector/edge/cursor publication and complete-generation CAS. Record maximum-depth forest explicitly; singleton pass-through must not issue gratuitous model calls.
- [ ] Inject failure after node insertion but before cursor write and assert the entire fold rolls back. Resume after a committed fold and assert it is reused without another provider call. Write a RED restart test that fails the embedding after CommitAcceptedHierarchySummaryAsync, opens a fresh store/process fixture, and asserts one summary send total plus a fresh embedding ordinal/receipt. Persist the accepted paid summary before its embedding attempt, without advancing the fold cursor; resume validates its exact source/recipe/sensitivity before using it.
- [ ] Run discovery/hierarchy tests plus existing Tapestry and Saga scoped retrieval tests; observe GREEN. Demonstrate permutation-independent membership/candidate/receipt identities on controlled persisted vectors.

## Task 4: Protected v2 evidence and atomic canonical item application

**Files:** Create `Core/LongRest/ProtectedLongRestPolicy.cs` and `ProtectedLongRestCanonicalHash.cs`; add Infrastructure `SagaLongRestService.Sweeps.cs`, `.Protected.cs`, `LongRestSweepStore.cs`, `LongRestAttemptStore.cs`. Modify existing service/receipt partials for transaction extraction and version-dispatched replay, and existing Core `LongRestPolicy.cs` only for a shared internal conservative-policy seam that preserves the public v1 behavior and hashes; update `long_rest_suppressions_validate_insert.sql` and its v18 replacement. Create tests `LongRest/ProtectedLongRestServiceTests.cs`, `LongRest/LongRestSweepStoreTests.cs` and extend v1 policy/receipt tests.

**Consumes/produces:** Task1 exact work/attempt/adjudication/protected types and Task2 evidence tables. Implement the internal ApplyInTransactionAsync, all attempt-store methods, ReadNextWorkAsync, CommitAcceptedAdjudicationAsync and CommitDecisionAsync. Persist accepted paid adjudication independently without canonical/item-cursor changes; ReadNextWorkAsync returns it after current source/recipe/sensitivity proof, and CommitDecisionAsync requires its exact stored immutable identity. Canonical application plus item completion/cursor owns one immediate transaction.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~ProtectedLongRestServiceTests|FullyQualifiedName~LongRestSweepStoreTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED tests proving v1 Protected/NoChange remains exact, while authorized matching Global and Campaign groups can apply distinct v2 receipts without rewriting rows/versions/labels/provenance. Include historically clean Annals paired with a protected Saga label.
- [ ] Assert ambiguous/alias duplicate labels, wrong artifact revision/content/owner digests, mixed clean/protected or different nonempty provenance, wrong scope/claim and stale lease refuse with zero canonical changes. Read metadata/labels before protected content, then prove both Saga Annals and derived-artifact content digests.
- [ ] Write the protected v2 conservative-policy parity matrix with valid identical provenance but pinned/retired inputs, missing survivor embedding, incompatible bitemporal validity, dependency mismatch/current incoming DerivedFrom, transformation output/no-chain and stale head. Assert typed refusal/NoChange, no suppressions or effective projection/source-history changes, and retained labels/content. Preserve deterministic operator-origin priority for duplicates/equivalent and the existing positive supersession case where the declared exact winner overrides that priority. Initial immutable NoChange evidence is permitted; unchanged replay has zero total writes. Direct SQL insertion without exact v2 evidence must fail the protected trigger arm.
- [ ] Implement v2 domain-separated identity with retained exact label/partition evidence. Replay verification chooses recorded policy version and revalidates retained manifests. Never substitute clean sensitivity into protected snapshots.
- [ ] Write RED tests `Canonical_receipt_and_item_cursor_commit_or_rollback_together` and `Head_pin_dependency_or_label_change_after_adjudication_refuses_publication`. Inject the failure between suppression and cursor write. Separately crash after CommitAcceptedAdjudicationAsync but before CommitDecisionAsync, open a fresh store/process fixture, and prove the accepted decision applies with no second provider send or billing. Repeated accepted exact evidence must not invoke the provider again.
- [ ] Extract the existing v1 transaction body while preserving public ApplyAsync retry/transaction behavior; extend the SQL guard's protected evidence arm, maintaining all clean current-head/scope/pin/retirement/embedding/no-chain invariants.
- [ ] Run v1/v2 policy/store/receipt and curation regressions; observe GREEN. Compare total_changes around unchanged applied/no-change replay.

## Task 5: Closed physical provider dispatch, disclosure and accounting

**Files:** Create Api/Intelligence `LongRestProviderClientFactory.cs`, `LongRestProvider.cs`, `LongRestProviderCallFreezer.cs`, `LongRestConfigurationEpoch.cs`, `ModelCallExecutor.LongRest.cs`, and `ModelTokenEstimator.LongRest.cs`; add the exact executor/estimator port arms in Core/Intelligence. Extend ApiBootstrapper's named-client registrations. Create tests `Intelligence/LongRestProviderCallTests.cs`, `LongRest/LongRestProviderDispatchTests.cs`, `LongRest/LongRestConfigurationEpochTests.cs`.

**Consumes/produces:** Task1 immutable payload/root/child capabilities and Task4 attempt store; implement ILongRestProvider. The configuration singleton supplies coherent boot/epoch/value snapshots and exact provider/model/pricing selection. Match LongRestProviderAdmission.Chat/Embedding against the closed payload arm before resolving a client; use the embedding input-token admission and frozen pricing rather than a synthetic chat envelope. Physical callbacks allocate/check one child, persist disclosure/attempt association, revalidate again and send once.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestProviderCallTests|FullyQualifiedName~LongRestProviderDispatchTests|FullyQualifiedName~LongRestConfigurationEpochTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED tests using a real counting fake HTTP handler for summary, adjudication and embedding. A strict-related HTTP400 or transient failure must produce exactly one physical send for that attempt. Explicit retry/correction has a new ordinal and a distinct durable disclosure receipt visible inside the handler.
- [ ] Construct dedicated pinned clients using normal ProviderResolver/IProviderApiKeyResolver and ClientRetryPolicy(0), omitting OpenAiRequestAugmentingHandler. Do not weaken ordinary client behavior. Use a closed SDK embedding batch with exact inputs/model/dimensions and no generic Weave sanitization/truncation.
- [ ] Write RED executor tests for exact admitted messages/schema/fingerprint, Tools=[], zero routing/injection, receipt failure/no send, child reuse/no second send and source/claim/lease/config fences both before and after journal commit. Freeze summary/adjudication and embedding-call digests separately; SDK object construction happens after the send fence.
- [ ] Implement token admission and normal TurnAccountingHandle reservation/usage/completion plus inference audit purposes for summary/adjudication/embedding. Capture usage before parsing response or rejecting vector count/dimensions/finiteness; successful paid suffix accounting uses CancellationToken.None while publication remains guarded.
- [ ] Write RED tests for paid malformed output, invalid vectors, cancellation after paid success, budget refusal, unsupported provider, oversized input/output and feature off-on/config A-B-A during I/O. Assert truthful spend but no invalid publication/cursor advance. Configuration epoch covers LongRest/Covenant gates and provider/model changes even when values return to their prior digest.
- [ ] Run the new provider tests and existing model admission/accounting, Campaign maintenance and HTTP handler tests; observe GREEN. Logs/audits/progress must contain no protected content or provider exception body.

## Task 6: Durable sweep orchestration, clean hosting and request-only recovery

**Files:** Create Api/Intelligence `LongRestMaintenance.cs`; create Infrastructure/Operations `LongRestSweepCoordinator.cs`, `LongRestSweepRecoveryHandler.cs`; create Infrastructure/Hosting `LongRestSweepHostedService.cs` and source-generated checkpoint context. Modify Core/Operations kinds/registry, GrimoireConnectionAdmissionContracts.cs, Infrastructure DI, the closed LongRunningOperationRecoveryAdmission classifier and the narrow LongRest cancellation-preservation arm in LongRunningOperationStore lease acquisition. Add exact live borrowed-turn validation to ICovenantOperationGate/CovenantOperationGate with focused gate tests. Create tests `LongRest/LongRestSweepCoordinatorTests.cs`, `LongRest/LongRestSweepRecoveryTests.cs`, `Hosting/LongRestSweepHostedServiceTests.cs`; extend recovery registry/coverage/crash tests.

**Consumes/produces:** Tasks2–5 ports; implement ILongRestMaintenance and ILongRestSweepCoordinator. Operation kind `long-rest-sweep`, ResumeFromCheckpoint, descriptor version0–1, Readiness priority, owner LongRestSweepCoordinator; admission kind17. Persist minimal checkpoint1 only after durable work. State code `long_rest.authorized_request_required` parks protected recovery as ReconciliationRequired. BeginOrResumeAsync persists initial scope/configuration; FindCompletedReplayAsync validates current source/recipe facts without writes before RequestAsync creates a new operation. Infrastructure resolves Core maintenance ports without referencing Api.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestSweepCoordinatorTests|FullyQualifiedName~LongRestSweepRecoveryTests|FullyQualifiedName~LongRestSweepHostedServiceTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED tests `Version_zero_recovery_abandons_without_effects`, `Protected_recovery_never_reconstructs_authority`, `Committed_adjudication_restarts_without_another_send`, `Canonical_commit_before_ledger_checkpoint_replays_without_writes`, `Heartbeat_refusal_and_public_cancellation_stop_dispatch` and `Unchanged_completed_sweep_returns_existing_operation_without_any_writes`.
- [ ] Implement RequestAsync to perform a metadata-only completed replay check before creating a new operation. Start/checkpoint initial work before any provider/canonical effect. Process bounded source/fold/anchor units without a whole-sweep deadline or retained attempt array; operation checkpoint follows committed store work.
- [ ] Renew worker and protected claim ownership through independent paths, serialize checkpoints for one owner and use latest revision for terminal CAS. Protected recovery preserves committed debt, releases ownership and returns RequiresAttention; a fresh eligible request resets/acquires the same operation after current-fact checks. Ordinary retry/background never supplies authority.
- [ ] Implement the hosted shell with installation-reset recovery awareness and Grimoire work admission before scope creation. Wrap each billable unit in an external-effect group. Gate-off or denied admission means no connection/scope, provider attempt, billing/failure entry or cursor move.
- [ ] Prove Global/Campaign protected debt remains unread in background. Change binding/path, owner deletion, dataset/restore, source, feature/config or authority generations at successive boundaries and assert no next dispatch/publication. Insertion above frozen high-watermark allows current progress and appears in the next generation.
- [ ] Register exactly one producer/descriptor/handler and update the closed hosted-producer roots/frontiers, derived-output policy, capsule manifest and benchmark input catalog. Run recovery, admission, hosted inventory and existing crash/lifecycle suites; observe GREEN.

## Task 7: Authenticated API, live turn seam and configuration documentation

**Files:** Modify Api/Tower/LongRestEndpoints.cs, Api/Serialization/ArcanumJsonContext.cs, ApiBootstrapper.cs, CampaignRollupTurnPreparer.cs, WizardIntelligenceProvider.cs, context-preview neighbor and Api/Intelligence/ArcanumInvocationContexts.cs; narrowly enrich CanonicalCampaignContextResolver/CanonicalCampaignResolutionPolicy with registered root facts after successful scope/conflict resolution. Create tests Api/LongRestSweepEndpointTests.cs and Intelligence/LongRestTurnPreparationTests.cs; extend wire completeness, invocation and fallback/preview tests. Update docs/Arcanum.API.md, Arcanum.DESIGN.md, Compendium.README.md and Arcanum.Command.Reference.md operation notes.

**Consumes/produces:** All previous ports. POST /api/memory/saga/long-rest/sweeps validates the typed scope and resolves canonical ownership server-side, then returns the operation DTO envelope. Register request/response/error payloads, WithName and explicit JsonTypeInfo; malformed JSON/media type retain ApiRequestJson contracts.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestSweepEndpointTests|FullyQualifiedName~LongRestTurnPreparationTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED endpoint tests for authenticated Global and exact Campaign requests, unauthenticated refusal, missing/wrong/numeric scope, contradictory CampaignId, malformed body/media type, disabled gate and generation conflict. Assert zero caller-supplied authority can enter the request shape and error envelopes are content-free.
- [ ] Write RED live-turn tests with LongRest on and Rollups off: one memoized accepted claim/preparation across retry/fallback; exact Global/Campaign protected debt processes before assistant begin; rollup prompt bytes remain absent. Stateless requests/previews/inspection dispatch nothing and create no maintenance claim.
- [ ] Generalize existing preparation eligibility to Rollups OR LongRest while retaining each feature's independent work/injection gate. Preserve exact accepted-request identity and scope-neutral pending-claim fencing; do not allocate competing claims for the same request.
- [ ] Capture and compare gate-off prompt/retrieval bytes, plus replay after feature disable/context ineligibility. Existing owned accepted keys cannot turn into unclaimed paid calls.
- [ ] Implement routes/DI and update exact owning docs, status/disclosure text and source-generated serialization completeness. Run endpoint, turn, invocation, preview/fallback and Compendium tests; observe GREEN.

## Task 8: Complete lifecycle closure, adversarial review and exact-candidate delivery

**Files:** Extend Data/Annals/AnnalsErasurePlan.cs; LongRestLifecycle; CampaignRepository.cs and GrimoireRepository.cs owner deletion; DataRetentionService.cs/.Pruning.cs/.FactoryReset.cs; Core/Memory/MemoryErasureEffectFacts.cs; CovenantSensitiveArtifactPurgePolicy.cs; CovenantProtectedArtifactErasureKernel.cs; BackupRestoreProtectedStatePurger.cs and BackupRestoreErasureEvidenceApplier.cs. Extend LongRest/LongRestLifecycleTests.cs, Data/DataRetentionServiceTests.LongRest.cs, protected purge/restore tests, Packaging/ContinuousIntegrationWorkflowTests.cs and .github/workflows/ci.yml. Update OATH delivery/recovery status.

**Consumes/produces:** Complete generation, node, label, FTS, work, effect/adjudication and receipt ownership. Append erasure target codes after31. Final branch satisfies every #91 criterion and preserves #75/#95 boundaries.

**TDD command:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~LongRestLifecycleTests|FullyQualifiedName~DataRetentionServiceTests'`. For each behavior, record a failing assertion before its implementation, then terminal Passed with zero Failed/Skipped after implementation. A task's regression run also includes its named existing neighbors.

- [ ] Write RED real-store tests erasing any leaf/participant with foreign keys on and off. Assert complete dependent nodes/vectors/edges/FTS/labels/work/evidence disappear, no partial receipt survives and unrelated scope content/history remains identical. Count and execute through the same common plan.
- [ ] Cover Campaign/Session deletion, retention/pins, Saga/store/factory reset, protection transition and staged protected restore/import. Restored attempt state is ineligible without fresh identity/authority; no executable lease/claim capability is imported. Preserve disclosure reporting and external copies' truthful revocability.
- [ ] Implement shared closure and exact table inventories; run all lifecycle/retention/backup/restore neighbors and observe GREEN.
- [ ] Have a fresh reviewer check the full branch against the approved spec/issue contracts and implementation, focusing on real behavior and missing cases. Fix validated defects with a reproducing RED test, then GREEN. Perform targeted temporary mutations that remove pre-top-K scope filtering, disclosure-before-send, atomic cursor ownership, ABA fence and taint purge; each claimed regression must fail, then restore production and rerun affected tests.
- [ ] Add focused Long Rest tests/TRX evidence to both existing native Windows CI lanes, covering policy/hash, discovery/store, protected provider, cursor/recovery and schema classes. Pin the selection in workflow tests; retain all existing native/AOT/provider gates and complete producer-analysis aggregation.
- [ ] Implement the user-added live Native AOT qualification requirement: enhance `scripts/verify-local-ollama-aot.sh` and `PublishedArcanumOllamaQualificationTests` with a real Perplexity `web_search` turn and parsed nonempty results, retaining corrected-file, restart persistence and stop-sign vision checks. Use a marker-only first CLI turn, one same-Session native streamed search turn, independent no-tool CLI citation/file recall, then the original restart/vision turn: four enhanced turns and ten entries. Prove native toolCall/toolResult opaque-ID correlation, exact captured-to-durable argument/result equality and retained Entry identities, plus assistant consumption of returned results. Persisted reply placeholders precede appended tool rows; ordinary wrappers do not carry a transport CallId. Enable only required web/tool settings and explicitly pass the configured credential without exposing it; preserve the user-owned Ollama service. Write a meaningful failing regression before changing the harness, then run the enhanced real published-binary scenario on the final candidate.
- [ ] Complete local qualification from repo root: `dotnet build RetroDownfall.Arcanum.slnx`; Compendium and Forge suites; `./scripts/coverage.sh --threshold` (full Arcanum runtime plus mandatory uninstrumented analysis); `dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes`; repository shell/packaging gates; `./scripts/verify-aot-il-warnings.sh`; `./scripts/verify-shipping-publish.sh --rid osx-arm64`; `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`. Observe terminal success, no first-party warnings and required coverage thresholds. Do not run duplicate full Arcanum suites when coverage already supplies the authoritative green runtime run.
- [ ] Refresh remote branch lineage. If memory-enhancement advanced, integrate it safely in the isolated branch and requalify changed candidate bytes. Stage only named #91 files, commit locally green work as `feat(memory): add authority-scoped Long Rest sweeps (#91)`, and push the candidate branch.
- [ ] Dispatch and await exact-SHA terminal CI, including both shipping Windows architectures, focused selected test identities, native SDK/host evidence, AOT/provider smoke, runtime/coverage and complete same-SHA producer-analysis aggregate. Repair any failure and repeat affected qualification on the new final SHA; partial logs/counters never authorize integration.
- [ ] Fast-forward/merge the qualified candidate into memory-enhancement without overwriting unrelated work, push and prove the remote candidate/tree identity. Avoid redundant post-merge suites when tree equivalence is exact.
- [ ] Mark all #91 criteria checked and close #91 only after remote integration is verified. Check #75's #91 child while leaving #75/#73 open. Report final SHA, terminal verification evidence and material limitations. Archive the managed worktree only once delivered changes and needed artifacts are safely retained.

## Execution and review handoff

Recommended: **subagent-driven**, because discovery storage, provider authority, recovery and lifecycle each need independent review before their contracts are consumed by the next task. Tasks1–2 establish shared interfaces/catalog state; dependent tasks remain ordered. Independent read-only review and bounded test investigations may run in parallel with explicit file ownership. A reviewer rejects incomplete/unsafe behavior without widening #91 into #95.

Alternative: **native** implementation in this session, with independent whole-branch review at the end. Both approaches retain TDD, the same acceptance criteria and exact-candidate delivery gates.

Human review of this plan and execution-method selection are the last planning gate before implementation. The user has already authorized implementation, fixes, commit, merge into memory-enhancement and #91 completion after green qualification.

Controller contract corrections (2026-10-10): explicit operation identity, noncyclic accepted-stage return, exact physical payload-kind/destination and truthful effect completion ports, and a source-only observed-Saga vector binding. The file/interface map and shared port signatures above define these corrections. Observed source vectors bind their authoritative raw float32 bytes and stored dimension without claiming a historical producer; derived publications retain exact provider/model/destination bindings. Unknown source vectors cannot serve as known-model summary queries; hierarchy lookup uses a proved compatible generated anchor node. Current effects remain bounded while accepted stages retain immutable paid evidence. The approved design remains authoritative; no historical provider/model attribution or paid leaf migration is introduced.

Accepted-stage restart clarification (2026-10-10): store work-unit identity is stable across newly admitted current boot/fence identities; historical accepted stage and actual paid Work remain immutable. Current wrappers carry separately fenced executable work alongside the unchanged stage. Exact store-owned association/revalidation permits genuine restart reuse without restoring old authority or resending accepted work. SummaryDigest uses DerivedArtifactContentDigest.ForText, matching the kind16 canonical artifact digest.


Task3 integration clarifications (2026-10-10): permit strictly descending actual same-generation child layers in the Core fold validator and both head/V18 edge triggers, preserving singleton identity, adjacent-layer goldens and fresh/evolved parity. Add Core/LongRest/LongRestEmbeddingPayloadHash.cs and focused independent golden/refusal tests for the complete embedding-only grammar in the spec. Storage uses the exact retained accepted summary and authoritative recipe; Task5 consumes the same helper for admission/physical digest/final SDK recomputation. Chat rendering remains Task5-owned.

Current completion evidence clarification (2026-10-10): Task3 adds nullable 4096-byte CompletionJson to the current provider-effects head/V18 definitions plus source-generated LongRestDispatchCompletion and exact consumer/fixture tests. Task4 implements real attempt CAS, clearing on new physical attempts, exact completion/no-write replay, and aggregate increments exactly once. Task5 consumes the actual current completion for accepted paid evidence. Cumulative counters cannot prove individual paid counts/cost. Immutable historical accepted evidence survives replacement and is validated through the current exact semantic association. Published v17 remains frozen; unpublished v18 resources/pin/parity evolve together.

Additional final delivery request (2026-10-10): after every required local/live/remote test passes, install the retained qualified final candidate and its complete native dependency closure locally. Verify both installed Ollama models, tools/vision and Perplexity through supported configuration and safe credential storage. Preserve unrelated settings, encrypted data and operator services; no model pull or implicit LongRest enablement. Verify retained/install binary hashes and effective configuration. The existing live harness work is already component-reviewed; Task8/controller performs its actual exact-candidate run and final cross-task obligation discharge.
