# Real RAPTOR Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` for inline implementation, or the user's expressly authorized agent workflow. Do not spawn additional agents merely because this document exists. Complete each behavioral RED/GREEN/review checkpoint before integrating the next stage.

**Goal:** Replace Tapestry's hard spherical clustering with actual managed UMAP + full-covariance soft GMM RAPTOR, with safe DAG persistence and retrieval.

**Architecture:** Core contains deterministic numeric kernels and graph/retrieval policies. Infrastructure owns admitted SQLite generation/edge storage and existing paid summary/provider integration. Wizard consumes a typed retrieval service under invocation authority.

**Tech stack:** .NET 10, pure-managed double arithmetic, existing Microsoft.Data.Sqlite/SQLCipher and MEAI provider boundaries, source-generated JSON; no Python/native runtime dependency. Python exists only in a reproducibly locked dev fixture generator.

**Spec:** `docs/superpowers/specs/2026-10-25-real-raptor.md`.

## Baseline and global constraints

- Initial inspected runtime: `cd02c2223bb24066e9222d084ae07f1c7e8a7a65`; controller subsequently verified at `8e10e09a35dd61b0aa2dbca0904d3dc45af73e11`. Core schema is 24. Parent reports 67 integrated original-review fixes and 537 acquisition sites; neither count is a target to blindly rewrite.
- Recommended isolated checkout for execution: existing clean `review-command-center`, after verifying it is idle, clean, attached and current; do not edit the active controller or workers' worktrees.
- Feature has its own plan/register, separate from original 272 review findings; discoveries receive failing tests and fixes, not fabricated original-review closure counts.
- Existing feature toggle stays false by default; existing providers, model selection, paid summaries and privacy/authority boundaries remain.
- Full covariance; reduction dimension 10; `p > 0.1`; BIC candidates `1 <= K < min(50,n)`; no hard clustering fallback.
- Same-runtime deterministic numeric identity; no Python coordinate or cross-architecture bit-identity promise.
- All eligible corpus leaves accounted for; existing explicit 50,000-row ceiling preserved, never silently take a prefix.
- All source-schema changes through per-object schema files and version coordinator; no EF migrations, no closed-catalog weakenings.
- Exact-SHA verification at integration and final delivery. No installation/config/keychain/Ollama changes.

## Review focus

1. Duplicate source vectors/text are distinct identities, including a leaf shared by two summaries. Tasks 2, 5, 6 and 8 test this.
2. A source changes after read or before send/publish; no stale ancestor is admitted. Tasks 7, 8 and 10 own race tests.
3. Soft memberships create noncontracting layers or a cluster too large for the configured model. Tasks 5 and 8 prove finite termination and full coverage.
4. Old backup/schema/algorithm data is read or erased during upgrade. Tasks 6, 7 and 11 prove atomicity and compatibility.
5. A delegated call has SessionId/path but no granted Tapestry source. Task 10 proves zero ambient lookup and no capsule expansion.

## File and interface map

Paths below are repository-relative and exact proposed names; every task also lists existing files it changes. Preserve current public wire records unless a task explicitly extends and documents them.

New Core folder `src/RetroDownfall.Arcanum.Core/Weave/Tapestry/Raptor/`:

- `RaptorNumericContracts.cs`: `RaptorPoint(string StableId, ReadOnlyMemory<float> Vector)`; `RaptorNumericOptions` immutable code-owned manifest; `RaptorReducedPoint(string StableId, double[] Coordinates)`; CSR `RaptorNeighborGraph` and `RaptorFuzzyGraph`; `RaptorMembership(int PointIndex, int GlobalComponent, int LocalComponent, double GlobalPosterior, double LocalPosterior, RaptorRepairReason Repair)`; `RaptorCluster(string StableId, IReadOnlyList<RaptorMembership> Members)`; `RaptorClusterResult(IReadOnlyList<RaptorCluster> Clusters, IReadOnlyList<int> Unassigned, RaptorNumericDiagnostics Diagnostics)`.
- `RaptorNeighborGraphBuilder.cs`, `RaptorFuzzyGraphBuilder.cs`, `RaptorUmapOptimizer.cs`, `RaptorGaussianMixture.cs`, `RaptorClusterer.cs`: small, separate numeric responsibilities.
- `RaptorSmallMatrix.cs`: Cholesky, triangular solve, symmetric covariance/logdet helpers only.
- `RaptorAlgorithmManifest.cs`: algorithm version `raptor-umap-gmm-v1`, all numeric constants, seed derivation and fingerprint serialization.

New Core Tapestry files:

- `ITapestryClusterer.cs`: `Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken cancellationToken)`; identity contains exact scope, layer, stage and sorted-member digest.
- `TapestryGraphContracts.cs`: `TapestryEdgeWrite(string GenerationId,string ParentNodeId,string ChildNodeId,double? GlobalPosterior,double? LocalPosterior,TapestryMembershipReason Reason)`; `TapestryBuildSnapshot(TapestryScope Scope,long CorpusRevision,long InvalidationRevision,string CorpusFingerprint,int LeafCount)`; `TapestryGraphCheckpoint(IReadOnlyList<TapestryNodeWrite> Nodes,IReadOnlyList<TapestryEdgeWrite> Edges)`; `TapestryPublishRequest` containing snapshot, algorithm/model fingerprints and validated node/root/layer counts.
- `ITapestryRetriever.cs`: `Task<Result<IReadOnlyList<TapestryRetrievedNode>>> RetrieveAsync(TapestryRetrievalRequest request,CancellationToken cancellationToken)`; request contains authorized scopes, one query embedding/model identity, mode and existing node/byte/token bounds. Authorized scopes are created only after invocation policy, never reconstructed inside Infrastructure from untrusted raw request fields.
- `TapestrySelectionPolicy.cs`: deterministic rank ordering, node dedupe, all-path ancestor suppression and distinct-leaf coverage policy.

Infrastructure additions: `Weave/TapestryRetriever.cs`, `Weave/TapestryStore.Graph.cs`, `Weave/TapestryStore.Revisions.cs`, `Weave/TapestryBuildPlanner.cs`. Convert existing TapestryStore to partial with these cohesive slices, avoiding unrelated rewrites. Existing TapestryWeaver remains build orchestration.

All new numeric tests go under `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/Raptor/`; all named tests below are new unless stated otherwise. New fixture files go under `tests/RetroDownfall.Arcanum.Tests/Fixtures/Raptor/`, read through an explicit generated test JSON context.

## Task 1: Pin reproducible reference fixtures and identity contract

Files: create numeric contracts/manifest above; `scripts/raptor-reference/{requirements.lock,generate.py,README.md}`; `Fixtures/Raptor/{manifest.json,gaussian.json,fuzzy-graph.json,optimizer-step.json}`; `Raptor/RaptorReferenceFixtureTests.cs`; `Raptor/RaptorIdentityTests.cs`.

Interfaces: produces the types in the file map, `RaptorAlgorithmManifest.Version`, `Fingerprint()`, and stage-specific `DeriveSeed(TapestryClusterIdentity identity)` consumed by all kernels.

- [ ] Write `ReferenceManifest_RejectsMissingCommitOrArtifactHash`, `Seed_IsStableAcrossInputPermutationAndChangesAcrossStage`, and `AlgorithmFingerprint_ChangesForEveryBehavioralConstant`; exact official commit is `7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767`.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~RaptorReferenceFixtureTests|FullyQualifiedName~RaptorIdentityTests'`; require real RED on missing behavior.
- [ ] Lock proposed Python 3.11, numpy 1.26.3, umap-learn 0.5.5, sklearn 1.4.2, scipy 1.12.0, numba 0.59.1 and pynndescent 0.5.12 with complete transitive hashes in an isolated dev environment; resolve conflicts explicitly before generating fixtures. Pin source/seed/float dtype/initial parameters in manifest. No dependencies installed into user/global environments. The generator uses actual official kernels and independent analytic expected values, not C#-generated expectations.
- [ ] Implement manifest and type contracts; generate small synthetic reference artifacts. Treat official upstream's unpinned sklearn and unseeded UMAP as limitations, not reproducibility evidence.
- [ ] Run filter GREEN; read fixture diffs for provenance and finite bounded arrays; commit only this task's files.

## Task 2: Bounded cosine neighbor graph

Files: `RaptorNeighborGraphBuilder.cs`; `Raptor/RaptorNeighborGraphTests.cs`; `Raptor/RaptorNeighborQualityTests.cs`.

Interfaces: `Result<RaptorNeighborGraph> Build(IReadOnlyList<RaptorPoint> points,int neighbors,ulong seed,CancellationToken cancellationToken)`; graph returns source indices/distances in sorted CSR rows, with documented self-neighbor treatment matching fixture.

- [ ] RED tests: `ExactGraph_MatchesOracleWithTiesAndDuplicates`; `DuplicateVectors_RetainDistinctIndices`; `ApproximateGraph_RecallAtLeastPoint95`; `LargeGraph_DoesNotAllocateSquareDistanceMatrix`; `Cancellation_StopsInsideCandidateLoop`.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~RaptorNeighbor`; inspect failure.
- [ ] Implement scalar cosine normalization; exact blockwise bounded heaps at n<=2048; seeded random-projection-tree neighbor initialization and deterministic NN-descent above threshold, bounded candidate/reverse-neighbor arrays. Stable-id tie-break; reject invalid vectors with typed Result. No per-sample drop or duplicated identity.
- [ ] Run GREEN plus deterministic fixed-seed shuffled-input tests and allocation instrumentation. Record measured recall on fixed synthetic clustered, manifold, duplicate and disconnected fixtures; do not lower 0.95 to accommodate a bug.
- [ ] Commit this independently reviewable numerical kernel.

## Task 3: Fuzzy simplicial set and real UMAP optimizer

Files: `RaptorFuzzyGraphBuilder.cs`, `RaptorUmapOptimizer.cs`; `Raptor/RaptorFuzzyGraphTests.cs`, `Raptor/RaptorUmapTests.cs`.

Interfaces: `Result<RaptorFuzzyGraph> Build(RaptorNeighborGraph graph,CancellationToken cancellationToken)`; `Result<IReadOnlyList<RaptorReducedPoint>> Optimize(RaptorFuzzyGraph graph,int outputDimensions,ulong seed,CancellationToken cancellationToken)`; internal fixed-initial-coordinate test seam.

- [ ] RED: `SmoothDistances_MatchPinnedRhoSigma`, `FuzzyUnion_UsesAPlusBMinusAB`, `DuplicateDistances_StayFinite`, `FixedInitialEpoch_MatchesGoldenAtOneEMinus6`, `NegativeSamples_ActuallyRepelNonNeighbors`, `Optimization_PreservesNeighborhoodsBetterThanInitialization`, `Cancellation_StopsInsideEpoch`.
- [ ] Run numeric test filter and inspect RED before code.
- [ ] Port pinned 0.5.5 smooth-kNN binary-search semantics and sigma floors, symmetric fuzzy union, curve fitting and epoch/negative-sample schedule with license attribution. Implement seeded random initial coordinates, explicit Euclidean output cross-entropy gradients, clipping and learning-rate schedule. Store CSR edges, never dense n² probabilities. Constants are the manifest's fixed min_dist .1/spread1/repulsion1/negative samples5/learning rate1; copy exact small/large epoch threshold from pinned source into manifest and fixtures.
- [ ] Run GREEN against supplied graph/initial-coordinate fixtures at 1e-6 absolute and end-to-end topology tests. Verify a projection-only implementation fails these tests.
- [ ] Commit and review numerical correctness before GMM integration.

## Task 4: Full-covariance EM, responsibilities and BIC

Files: `RaptorSmallMatrix.cs`, `RaptorGaussianMixture.cs`; `Raptor/RaptorGaussianMixtureTests.cs`, `Raptor/RaptorSmallMatrixTests.cs`.

Interfaces: `Result<RaptorGmmFit> Fit(double[][] points,int components,ulong seed,CancellationToken cancellationToken)`; `Result<RaptorGmmFit> SelectByBic(double[][] points,ulong seed,CancellationToken cancellationToken)`; fit exposes weights, means, full covariances, total likelihood, BIC, convergence, `Posterior(ReadOnlySpan<double> point,Span<double> destination)`.

- [ ] RED: `LogDensity_MatchesCorrelatedGaussian`, `Responsibilities_SumToOne`, `Bic_CountsFullCovarianceParameters`, `ModelSelection_FindsSeparatedMixtures`, `Selection_UsesExclusiveReferenceUpperBound`, `IdenticalPoints_ReturnFiniteOneComponent`, `SingularCandidate_IsRejectedWithoutHardFallback`, `NonconvergedCandidates_AreNotSelected`, `Cancellation_InterruptsEmBatch`.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~RaptorGaussianMixture|FullyQualifiedName~RaptorSmallMatrix'`; inspect RED.
- [ ] Implement Cholesky/logdet/triangular solve; blockwise log-sum-exp E step and full covariance M step, diagonal regularization1e-6, tolerance1e-3, max100 iterations. Euclidean K-Means initializes EM only; derive its deterministic seed per K. Select the best converged fit without differently seeded refit. Canonicalize component ordering.
- [ ] Run analytic 1e-8 and supplied-initial-fit likelihood/BIC 1e-6 relative goldens GREEN; ensure an off-diagonal covariance fixture distinguishes diagonal-only implementations.
- [ ] Commit; numeric Core checkpoint may be independently built, tested and reviewed while all production behavior still uses old Tapestry.

## Task 5: Global/local soft clustering and bounded plan policy

Files: `ITapestryClusterer.cs`, `RaptorClusterer.cs`, `TapestryGraphContracts.cs`, `Infrastructure/Weave/TapestryBuildPlanner.cs`; `Raptor/RaptorClustererTests.cs`, `Weave/Tapestry/TapestryBuildPlannerTests.cs`.

Interfaces: `ITapestryClusterer.Cluster` from map; planner `Result<TapestryLayerPlan> Plan(TapestryScope scope,IReadOnlyList<TapestryWorkingNode> nodes,int layer,ITapestryClusterer clusterer,ITapestrySummarizer summarizer,CancellationToken cancellationToken)`. Define WorkingNode/LayerPlan records in GraphContracts; plans contain cluster child IDs, carry IDs, memberships/reasons and proposed terminal reason, not provider calls.

- [ ] RED: `LocalReduction_UsesOriginalEmbeddingSubset`, `BridgePoint_BelongsToTwoSummaries`, `Threshold_IsStrictlyGreaterThanPointOne`, `NoMembership_RepairsWithoutDroppingSource`, `DuplicateVectors_MapByIndex`, `SameMemberSets_Coalesce`, `RecursiveSplit_StopsOnRepeatedMemberSet`, `AllSources_RemainInClustersOrCarries`, `SmallLayer_TerminatesAtEleven`, `NoncontractingFrontier_DoesNotSpend`.
- [ ] Run both filters; verify behavioral RED with fake reducers/fits that expose the required stage inputs.
- [ ] Compose real UMAP/GMM global/local stages; track point indices and both posterior stages; no top-one collapse. Split oversized requests by recursive real clustering, then explicit stable partitions only on nonprogress; refine partitions by FitsOneRequest. Preserve single oversized carry. Do not reuse current undersized-neighbor merge to overwrite soft memberships.
- [ ] Run GREEN with duplicate, all-identical, near-duplicate, overlap and 0..12-size corpora; prove finite work and exact source-set union.
- [ ] Commit with algorithm/default docs in DESIGN draft, still no production switch.

### Approved stage 5 bounded-work clarification

Global repeated-member-set detection cannot itself bound exponentially many distinct overlapping subsets. Stage 5 fingerprints a deterministic per-layer envelope of at most n clustering calls and 81*n member visits (submitted members plus inspected returned membership/unassigned records), using checked arithmetic and cancellation. Exhaustion returns a zero-summary diagnostic plan with all original current nodes, terminal reason `WorkLimit`, and typed disposition `Incomplete`. **Task 8 must refuse this build, preserve the prior valid generation, back off, and never publish this plan.** WorkLimit is neither successful completion nor `NonContracting`; it must never trigger a stable-partition fallback. Repetition/nonshrinking semantic splits retain the approved stable-id/request-fit partition behavior. Per-split evidence preserves prior posterior paths without recursively copying shared ancestry.

## Task 6: DAG schema and legacy atomic evolution

Files: current schema table files for tapestry_nodes/generations; create `Tables/tapestry_edges.sql`, `Tables/tapestry_scope_revisions.sql`; graph validation/write guards as individually named `Triggers/*.sql`; new transition resources; `GrimoireSchemaVersionChains.cs`; new `Schema/TapestryDagBackfill.cs` only if bounded managed backfill required; `tests/.../Data/Schema/TapestryDagSchemaEvolutionTests.cs`; existing `TapestrySchemaTests.cs`.

Interfaces: SQL relation and revision fields from design; no EF entities. Reserve Core25 only if still free at integration, otherwise next version; freeze Core24 source/catalog fixture before edits.

- [ ] RED fixtures cover fresh install, genuine Core24 upgrade, valid legacy parent backfill, corrupt dangling/cross-generation parent rejection, duplicate edges, cross-scope edges, cycle/layer inversion, published mutation, failure/cancellation after each transition stage, and newer-schema refusal.
- [ ] Run schema evolution filter before DDL implementation.
- [ ] Implement composite node uniqueness/FKs, strict parent>child layer and summary-parent guards, Building-only mutation, reverse edge index. Backfill valid legacy edges without changing old algorithm ids/node ids. Mark legacy source validation pending. Update canonical source and frozen transition definitions through the existing coordinator.
- [ ] Run GREEN and catalog/foreign-key checks against fresh/upgraded/rollback databases; prove version metadata only publishes after validated head.
- [ ] Commit this storage feature, leaving old runtime reader operational through compatibility.

## Task 7: Revision-safe source invalidation and graph store

Files: `ITapestryStore.cs`, `TapestryStore.cs`, new `TapestryStore.Graph.cs`, `TapestryStore.Revisions.cs`; source lifecycle trigger files under schema; tests `TapestryGraphStoreTests.cs`, `TapestryRevisionTests.cs`, existing Store/LeafHash tests.

Interfaces: add `Task<TapestryBuildSnapshot> ReadBuildSnapshotAsync(TapestryScope,CancellationToken)`; `Task AppendGraphCheckpointAsync(TapestryGraphCheckpoint,CancellationToken)`; `Task<Result> PublishGenerationAsync(TapestryPublishRequest,CancellationToken)`; `Task<IReadOnlyList<string>> GetRootIdsAsync(string generationId,CancellationToken)`; `Task<IReadOnlyList<string>> GetChildIdsAsync(string generationId,IReadOnlyList<string> parentIds,CancellationToken)`; batched graph closure/coverage reads with bounded SQL parameters. Replace SetParentAsync production writes.

- [ ] RED: `Diamond_CountsThreeDistinctLeavesNotFour`, `AncestorClosure_FollowsBothParentsOnce`, `Checkpoint_IsAtomic`, `SourceEdit_InvalidatesBothAncestorBranches`, `AttachmentScopeReplacement_InvalidatesSummary`, `InsertOrReplace_Invalidates`, `SourceMove_InvalidatesOldAndNewScope`, `PureAppend_KeepsOldSummaryUsableButRequiresNewPublicationSnapshot`, `Publish_RejectsChangedRevision`, `LegacyLookup_ValidatesFingerprintOnce`, `OldValidGeneration_RemainsDuringBuild`.
- [ ] Run graph/revision/legacy filters; inspect RED.
- [ ] Implement snapshot/revision comparison in admitted transactions; source change triggers cover source identity/content/scope/eligibility and old/new scopes. Every mutation bumps CorpusRevision; only destructive/meaning-changing mutations bump InvalidationRevision. Pure append keeps the current safe generation retrievable while requiring a new complete build snapshot. Legacy fingerprint check is revision-stable and Complete-only. Publication validates coverage/edges/vectors, then uses existing one-Complete atomic switch. An erased/stale generation never survives merely because its summary hash is unchanged.
- [ ] Run GREEN, EXPLAIN query-plan assertions for reverse lookup/root anti-join and bounded diamond expansion; preserve busy retry and cancellation.
- [ ] Commit; review change-trigger inventory against every source writer and closed-world catalogs.

## Task 8: Integrate paid recursive weave with deterministic reuse

Files: `TapestryWeaver.cs`, `TapestrySummarizer.cs`, `TapestryBuildBackoff.cs`, `TapestryEmbeddingSettings.cs`, DI registration; existing Weaver/Summarizer/Seed/Backoff tests plus `TapestryRaptorWeaverTests.cs`.

Interfaces: consumes planner, clusterer and graph checkpoint/publish APIs. Keep existing `WeaveAsync(TapestryScope,EmbeddingSettings,CancellationToken)` and summarizer public contract; extend typed status/terminal reasons with source-generated serialization where exposed.

- [ ] RED: `SummaryEmbeddings_AreNewProviderEmbeddings`, `SoftChild_WritesTwoEdgesWithoutOverwrite`, `InvalidLeafVector_RefusesPartialPublication`, `MissingEmbeddingModelProvenance_Reembeds`, `SummaryReuse_RequiresExactCanonicalInputsAndResolvedModels`, `SameTextDifferentSources_HaveDifferentNodeIds`, `Cancellation_KeepsPublishedGeneration`, `FailedPublish_DoesNotRepeatPaidPrefixEverySweep`, `FeatureOff_MakesNoCalls`.
- [ ] Run focused filters and inspect RED.
- [ ] Switch builder to `raptor-umap-gmm-v1`, original-dimensional summary embeddings, strict-contraction plans and atomic graph checkpoints. Hash ordered actual summary input bytes plus recipe, resolved summary provider/model and embedding provider/model identity; hash child stable IDs separately for unique node identity. Never claim same nominal model implies immutable provider weights; preserved identity contract is configuration identity.
- [ ] Add provider-effect revision/authority check under existing lease/barrier and preserve existing billing/reservation/audit path. Numeric errors and cancellation are explicit, cleanup cannot undo an already-published generation. Backoff identity includes new algorithm manifest. No durable operation kind or paid-prefix resume added.
- [ ] Run GREEN and existing regression suite; commit with DESIGN/Compendium algorithm and mechanic corrections.

## Task 9: Typed collapsed/traversal retrieval and overlap selection

Files: `ITapestryRetriever.cs`, `TapestrySelectionPolicy.cs`, new `Infrastructure/Weave/TapestryRetriever.cs`, graph read slices; `TapestryDagRetrievalTests.cs`, existing `TapestryRetrievalTests.cs`.

Interfaces: request/result from map; add coverage metadata to internal retrieved-node record, or a separate internal selection projection, without leaking source IDs in public diagnostics. Use existing actual token estimator via DI.

- [ ] RED: `Collapsed_RanksLeavesAndSummariesTogether`, `Traversal_RanksDistinctUnionOfSelectedChildren`, `SharedDiamondChild_IsScoredAndReturnedOnce`, `MixedLayerCarry_IsAReachableRoot`, `AllPathAncestor_IsSuppressed`, `FullyCoveredSibling_IsSuppressed`, `PartlyOverlappingSibling_RemainsEligible`, `RejectedTopCandidate_BackfillsNextEligible`, `RenderedTokens_RespectExactBudget`.
- [ ] Run retrieval filters and inspect RED.
- [ ] Implement bounded BLOB scan/top-K and graph adjacency reads under captured generation/revision; ranked cursor or increasing windows continue after rejected candidates with explicit diagnostic bounds. Do not add unconstrained dynamic join identifiers to Divination. Keep original embeddings for cosine and stable deterministic ranking.
- [ ] Run GREEN and source-set/source-order assertions. Include many shared paths without exponentially repeated SQL rows.
- [ ] Commit with mode/overlap docs.

## Task 10: Wizard invocation/capsule authority and ledger integration

Files: `Api/Intelligence/WizardIntelligenceProvider.cs`, relevant DI extension; tests `TapestryInvocationAuthorityTests.cs`, existing MemoryDelegation integration/lifecycle/source tests, materialization ledger and context preview tests. Read exact delegated execution branches before moving checks.

Interfaces: change private retrieval method to accept `ArcanumInvocationContext` and construct authorized scope request only after established eligibility; injected `ITapestryRetriever` does not mint scope authority. Existing capsules remain Covenant/Saga/Lexicon-only.

- [ ] RED: `NoCapsule_DelegatedCallDoesNotQueryTapestry`, `GrantedOtherMemory_DoesNotGrantTapestry`, `RevokedCapsule_DeniesFinalEffect`, `CrossCampaignSessionId_DoesNotAuthorizeLookup`, `PreviewAndLiveUseSameEligibleScopes`, `RawAndExplicitMaterialWin`, `DocumentAttachmentsNeverContribute`, `SourceRevisionChangesBeforeSend_PreventStaleInjection`.
- [ ] Run authority/ledger filters, inspecting real seam call counters rather than source-text assertions.
- [ ] Integrate typed retriever with actual invocation gates, source revision revalidation and existing provider effect admission; preserve once-only ledger injection, adaptive fences, scope-safe labels and graceful expected empty result. Do not change capsule wire schema or promote summaries.
- [ ] Run GREEN including streamed/buffered/preview/delegated paths and feature-disabled equivalence; commit.

## Task 11: Complete retention, erasure, reset and restore ownership

Files: `DataRetentionService.cs`, `.FactoryReset.cs`, `.WorkspaceReset.cs`; `EmbeddingsResetService.cs`; `BackupRestoreDatabaseWorker.cs`, `BackupRestoreProtectedStatePurger.cs`; TapestryStore delete helper; tests `TapestryDagRetentionTests.cs`, `TapestryDagRestoreTests.cs`, existing erasure/retention/restore suites.

Interfaces: existing lifecycle methods remain; extend exact table inventories/count/fingerprint models for edges/revisions. Version-gate DAG table access when inspecting old staged archives. No wildcard untrusted table deletion.

- [ ] RED: shared source erasure removes all affected generations including Building; other scope survives; reset inventories/counts equal actual derived rows; optional legacy vector mirror sanitized; pre-DAG backup restores; new DAG backup purged before activation; post-purge replay cannot restore old source; disabled corpus is not mistaken for removed scope.
- [ ] Run focused lifecycle filters and inspect RED.
- [ ] Update table ownership, inventory counts, plan fingerprints, delete order and old-schema checks. Cascades are tested, not assumed to fix plan counts. Purge summary text, embeddings, edges and reusable cache together; revision rows survive only where required for correct live-source invalidation and are included in reset semantics.
- [ ] Run GREEN plus owner-closure and staged restore catalogs. Update owning DESIGN/API documentation as applicable; commit.

## Task 12: Qualification, independent review and exact-candidate delivery

Files: existing test/gate scripts only when a genuine new path needs explicit enumeration; numeric benchmark harness `scripts/raptor-reference/benchmark.md` records commands/results, not unproven claims; final owning docs.

- [ ] Freeze candidate SHA/tree, run full Arcanum and Compendium suites and required qualification lanes; preserve baseline failures separately from regressions. No widening allowlists to suppress unknown new producers.
- [ ] Run `./scripts/verify-aot-il-warnings.sh`, `./scripts/verify-shipping-publish.sh --rid osx-arm64`, `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`, and coverage policy required by delivery scope. Include direct SQL/EF boundary, source-gen serialization, closed-world producer and archive/schema inventories.
- [ ] Measure deterministic synthetic 1k/10k/50k corpora, dimensions768 and edge k≈sqrt(n), recording memory/runtime/cancellation/call counts. Fail explicit operational envelopes established by benchmark fixture; do not imply model cost/performance based only on tiny unit tests.
- [ ] Run normal configured local-model end-to-end proof if available/required; no provider installation/config changes. Verify recursive summaries/re-embeddings, bridge-source overlap, both retrieval modes and erasure. Offline/fake tests are not substituted for a required live gate.
- [ ] Independent whole-change review focuses on numerical fidelity, source closure, revocation race, schema rollback and legacy compatibility. Each validated defect gets RED/GREEN, then affected gates rerun at the new exact SHA.
- [ ] Integrate only after all required evidence binds to candidate. Verify controller ref/tree equality after integration. Report complete/partial/unavailable lanes honestly; do not mark cancelled CI green.

## Self-review and execution notes

All design requirements map to tasks: numeric fidelity1–5; DAG/upgrade6–7; recursive paid builds8; retrieval9; authority10; lifecycle11; final evidence12. Numeric kernels can land behind existing behavior and be independently reviewed before storage or production integration. Storage and source revision changes must integrate together before enabling the new builder; do not deploy a half-upgraded topology reader. The precise next schema number and current checkout head are integration facts to re-read, not missing algorithm decisions.

No runtime implementation or tests were run for this design deliverable. Fixture environment resolution and 50k performance remain explicit implementation gates. The parent may carry the already-authorized execution forward; this document does not require another permission exchange for routine engineering choices.
