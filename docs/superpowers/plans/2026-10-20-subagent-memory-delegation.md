# Subagent Memory Delegation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship issue #107's authenticated, exact-version, one-child read-only memory capsule through one buffered native inference wrapper.

**Architecture:** The wrapper prepares one invocation-local grant under genuine parent authority. A sealed one-use capability supplies only exact selected fragments to an otherwise isolated child; a shared source-mutation/effect admission barrier and inherited generation guards revoke stale grants, while typed sensitivity survives child-to-parent publication.

**Tech Stack:** .NET 10/C#, Native AOT source-generated JSON, existing ASP.NET Core native API, SQLCipher parameterized direct SQLite, xUnit and deterministic fake provider transports.

**Spec:** `docs/superpowers/specs/2026-10-20-subagent-memory-delegation.md`

**Status:** User approved design and authorized implementation/review/qualification/integration/closure. Execute without an additional design approval gate. Baseline `0845aa08`, branch `codex/issue-107-memory-capsules`. Existing subagent/invocation baseline: 37 passed; source/evaluation baseline recorded by the coordinating agent: 46 passed.

## Global Constraints

- One new buffered route: `POST /api/intelligence/memory-delegation`; parent accepts ordinary stateless or Session-backed `PingRequest`.
- Child ordinary invocation is `None`, no tools, no ambient retrieval; capsule-only disclosure scope has no mutation collector.
- 1–16 selected current versions; 128 total selected/dependency coordinates; 32 KiB rendered memory; 64 KiB proof metadata.
- Five-minute absolute maximum clipped by valid-to/parent lifetime; one grant per parent; 32 active grants per process; no renewal, forwarding or durable executable authority.
- Selector `memory`, optional root tool argument `memory_grant`; absence preserves byte-for-byte ordinary child input construction.
- Purpose `DelegatedTask` binds actual canonical child task digest at atomic consumption; no semantic natural-language policing.
- Relevant source mutations may revoke every live grant in v1. Drain active capsule effects, not the entire parent request; never hold SQLite transactions across provider awaits.
- Protected labels survive child, parent tool result, Session persistence, later parent sends and protected no-store response serialization.
- Preserve evaluation authority checks and regression behavior; no dummy evaluation request to bypass them.
- Preserve unrelated work; modify only task-owned files, stage explicit paths, no broad add/reset/stash/clean. All implementation and integration evidence is exact-SHA.

## Review Focus

- A parent mutates memory after its child returns: revocation must suppress later publication without waiting on that parent's own request (Tasks 2/4).
- A source is suppressed by Long Rest without changing its Annals head: preparation and dispatch must refuse its new ineligibility (Tasks 1/2).
- An async iterator rolls back AsyncLocal state: the root tool must retain its exact explicit binding and siblings must receive none (Task 3).
- A Session-backed parent persists a protected tool result before the next model send: text and label must commit atomically before entry events (Task 4).
- Expiry or revocation lands after disclosure acknowledgement but before provider admission: zero sends, truthful prior disclosure, drained slot ownership (Tasks 2/4).

## File and interface map

Core owns validated selection, bounds/digests, nonserializable capabilities and coordination policy under `src/RetroDownfall.Arcanum.Core/Intelligence/MemoryDelegation/`. Infrastructure owns exact source adapters and mutation hooks. API owns wrapper, bound tool selection, isolated materialization, typed provider/parent taint and protected response lifetime. Tests follow those boundaries.

Use these initial interface names consistently across tasks; coordinated signature refinements are permitted when required by existing typed conventions, but update this plan and both consumers together:

- `MemoryDelegationPurpose.DelegatedTask`; `MemoryDelegationGrant(MemoryEvaluationScope Scope, MemoryEvaluationMemory Memory, MemoryDelegationPurpose Purpose)`. Reuse existing exact coordinate DTO shapes; their evaluation names do not confer evaluation authority.
- `IMemoryDelegationSourceReader.ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct) -> Task<Result<MemoryEvaluationSourceSnapshot>>`; `ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) -> Task<Result>`. These reuse transient evidence shapes, not evaluation workload/provider/authority.
- `MemoryDelegationCoordinator.PrepareAsync(Guid rootId, MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, IMemoryDelegationSourceReader reader, CancellationToken ct) -> Task<Result<RootMemoryDelegation>>`.
- `MemoryDelegationCoordinator.ConsumeAsync(RootMemoryDelegation root, Guid childId, CovenantDigest taskDigest, CancellationToken ct) -> Task<Result<ChildMemoryCapsule>>`.
- `MemoryDelegationCoordinator.BeginEffectAsync(ChildMemoryCapsule child, MemoryDelegationEffectKind kind, CancellationToken ct) -> ValueTask<Result<MemoryDelegationEffect>>`, where kind distinguishes ProviderDispatch and OutputPublication. The effect owns cancellation and drain registration; it is asynchronously disposable.
- `MemoryDelegationCoordinator.ValidateRootAsync(RootMemoryDelegation root, CancellationToken ct) -> Task<Result>` and `CompleteAsync(RootMemoryDelegation root) -> ValueTask`; terminal state stays visible on the root object even after registry removal.
- `IMemoryDelegationMutationBarrier.BeginMutationAsync(CancellationToken ct) -> ValueTask<MemoryDelegationMutationLease>`. An explicit lease/owned-core overload covers nested store calls without recursive acquisition; the lease closes preparation/dispatch until disposed after commit/rollback.
- `MemoryDelegationRequest(PingRequest Parent, MemoryDelegationGrant Grant)` belongs to API wire contracts; success reuses `PromptResponseDto`. Root/child/effect/mutation capability classes are never wire DTOs.

### Task 1: Exact selection and bounded source proof

**Ownership/files:** Create Core `Intelligence/MemoryDelegation/{MemoryDelegationContracts,MemoryDelegationLimits,MemoryDelegationDigests,IMemoryDelegationSourceReader}.cs`; create Infrastructure `Data/MemoryDelegation/MemoryDelegationSourceReader.cs`; carefully factor existing `Data/MemoryEvaluation/MemoryEvaluationSourceReader.*.cs` proof helpers. Tests: new `tests/RetroDownfall.Arcanum.Tests/Intelligence/MemoryDelegationSourceTests.cs`, existing `ProvingGrounds/MemoryEvaluationSourceTests.cs` and `Fixtures/MemoryEvaluationSourceHarness.cs`.

**Consumes:** Existing `ICovenantOperationGate`, `CovenantTurnLease`, owning-store SQL/linker/Annals/sensitivity primitives. **Produces:** Grant/limits/digests and the exact source reader interface above. No registry, route or child execution.

- [ ] Write failing tests `Selection_bounds_fail_before_content_read` asserting 0/17 selected, 129 manifest coordinates, 32769 rendered bytes and 65537 metadata bytes refuse; corresponding maximums pass. `Current_head_required_but_retained_dependency_allowed` rejects superseded intervention while retaining lawful historical metadata-only dependency. `Unselected_shadow_and_suppression_still_refuse` covers Covenant/Lexicon Campaign shadow and Long Rest suppression without an Annals head change. `Owner_and_author_are_distinct`, `Label_mismatch_refuses`, `Caller_array_mutation_cannot_change_snapshot` cover the rest of the proof contract.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~MemoryDelegationSourceTests'`; observe relevant behavioral failure, not only missing-type compilation.
- [ ] Implement the declared reader and bounds using parameterized source-owned reads and unchanged placement/eligibility logic. Extract shared private/internal proof helpers behind separate authority facades; preserve #106 predicates. Hydrate only selected sources; reject partial proofs. Validate UTF-8 lengths before text allocation where SQL length projections permit.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~MemoryDelegationSourceTests|FullyQualifiedName~MemoryEvaluationSourceTests'`; require all pass and inspect no regression in protected reads.
- [ ] Review diff and commit only this task's explicit paths: `feat: prove exact memory delegation sources`.

### Task 2: One-use grant lifecycle and mutation/effect linearization

**Ownership/files:** Create Core `Intelligence/MemoryDelegation/{MemoryDelegationCoordinator,MemoryDelegationCapabilities,IMemoryDelegationMutationBarrier}.cs`; register singleton coordination in existing Infrastructure DI composition. Modify owning mutation seams below and existing global admission integration. Tests: `Intelligence/MemoryDelegationLifecycleTests.cs`, `Data/MemoryDelegationMutationTests.cs`, existing Covenant/Grimoire concurrency tests.

**Consumes:** Task 1 reader/snapshot and existing genuine lease/generation validation. **Produces:** Root/child/effect capabilities, coordinator signatures and explicit mutation ownership above. API consumers must not issue authority themselves. The singleton coordinator owns registry/admission state, not scoped SQLite services: preparation borrows the request-owned reader explicitly and the root retains it only through that protected request lifetime.

- [ ] Write RED tests `Only_one_parallel_consumer_wins`, `Wrong_root_or_child_does_not_consume`, `Forged_or_disposed_parent_lease_refuses`, `Thirty_third_live_grant_refuses`, `Five_minute_expiry_cancels_without_renewal`, `Revocation_after_child_result_survives_root_continuation`, `Parent_mutation_does_not_wait_for_parent_lifetime`, and `Mutation_wins_before_dispatch_then_zero_sends`. Use `TimeProvider` and TCS barriers, not sleep-based scheduling. Verify no active slot releases before its registered effect drains.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~MemoryDelegationLifecycleTests|FullyQualifiedName~MemoryDelegationMutationTests'`; observe behavioral RED.
- [ ] Implement immutable registry ownership, atomic consume, absolute expiry, linked cancellation and asynchronous admission semaphore/protocol (never await inside a CLR monitor lock). Mutation closes admission/increments generation/revokes under lock, drains active effects outside lock/transaction, and holds exclusive mutation ownership through commit/rollback. Never drain the parent request itself. A failed/rolled-back mutation may leave old grants revoked. Every live host writer must resolve this same singleton; add a composition test against omission/substitution. Legacy direct tests/offline construction may use an optional no-runtime dependency without requiring unrelated rewrites, provided composition proves no active-grant host bypass.
- [ ] Instrument outer transaction owners, not already-transactional kernels: `Covenant/CovenantMutationService.CommitAsync`, `CovenantMemoryReviewService` before its in-transaction apply, and `Repositories/GrimoireRepository.TurnCommit.cs` when mutations exist; Saga `Data/SagaMemoryStore` insert/delete/delete-all and curation retire/reinstate/correct; `Memory/SagaMemoryReviewService.ApplyCoreAsync` after embedding preparation and before transaction/retry; Lexicon `LexiconService` upsert/delete, lifecycle, correction, memory review and erasure outer methods. Use explicit owned-core overloads for nested calls so a reset/purger does not reacquire its own barrier.
- [ ] Cover raw and eligibility bypasses: `Data/LongRest/SagaLongRestService.ApplyAsync`, non-deferred `LongRestSweepStore.RunAsync`, Campaign owner deletion, session deletion, prune/factory paths, `Weave/EmbeddingsResetService.ResetAsync`, Saga/Covenant erasure and `CovenantSensitiveRetentionPurgeCoordinator.PurgeAsync`. Add an inventory test tying all relevant write families to outer coordination. Existing `AnnalsClaimWriter`, Covenant mutation kernels, and cleanup workers inside transactions may assert ownership but cannot wait/drain there.
- [ ] Join existing `GrimoireConnectionAdmissionGate` stage-one work admission for reset/restore/factory operations: invalidate when closing starts and drain registered capsule effects before existing stage-two connection drain. Reuse this spine; do not create an independent maintenance protocol.
- [ ] Run the new tests plus affected owning-store mutation and `CovenantOperationGateConcurrencyTests`/Grimoire admission tests. Assert both race orders, rollback/no reopen, concurrent close, nested mutation no deadlock, and no source change behind an admitted unfenced send. Commit explicit paths: `feat: fence memory capsule lifecycle and source mutations`.

### Task 3: Authenticated wrapper and explicit one-child binding

**Ownership/files:** Create API `Intelligence/MemoryDelegation/{MemoryDelegationEndpoints,MemoryDelegationRequestScope,MemoryDelegationRequest}.cs`; modify `IntelligenceEndpoints.cs`, `Serialization/ArcanumJsonContext.cs`, protected route/auth inventories and error mapper; modify `Intelligence/Tools/ArcanumDelegateTaskTool.cs`, `Subagents/{SubagentContracts,SubagentRunner,SubagentExecutionAmbient}.cs`, and `WizardIntelligenceProvider` root capture/tool factory seams. Tests: `Api/MemoryDelegationEndpointTests.cs`, `Intelligence/MemoryDelegationToolTests.cs`, existing subagent/invocation suites.

**Consumes:** Tasks 1–2 grant, coordinator, exact parent lease and child capability. **Produces:** Buffered authenticated route; per-tool root binding; one-use consumed child passed explicitly to runner; unchanged ordinary invocation predicates.

- [ ] Write RED endpoint tests for authenticated canonical scope, missing/tainted epoch, context None/no-tools refusal, malformed/unknown grant members, unavailable capability, quota, Session-backed parent and stateless parent. Assert one lease survives through protected serialization and missing auth still produces protected headers.
- [ ] Write RED tool tests `Only_bound_root_advertises_selector`, `Missing_selector_is_ordinary_child`, `Sibling_and_nested_child_have_no_grant`, `Exact_executed_task_digest_is_bound`, `Iterator_yield_cannot_swap_root_binding`, and `Ordinary_child_prompt_is_unchanged`; reusing consumed selector invokes no second child/provider.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~MemoryDelegationEndpointTests|FullyQualifiedName~MemoryDelegationToolTests'`; observe behavioral RED.
- [ ] Implement source-generated wrapper plus canonical/authenticated scope factory and protected result lifetime. Capture root capability once at `WizardIntelligenceProvider.ExecutePromptCoreAsync` entry and explicitly pass it through pipeline/fallback to `BuildToolSetWithMcpAsync` and tool constructor. Never look up a root grant only inside InvokeCoreAsync after an iterator yield. Validate ordinary child arguments first, mint child operation identity, compute actual immutable task digest, consume, then invoke runner.
- [ ] Extend child state with only `ChildMemoryCapsule`; `EnterChild` masks root grant. Keep `ArcanumInvocationContext.None`, ContextPolicy None, disabled tools/retrieval and ordinary budget enforcement. Do not use a serializable request property to carry capability.
- [ ] Run new tests and `--filter 'FullyQualifiedName~Subagent|FullyQualifiedName~ArcanumInvocationContext|FullyQualifiedName~CampaignInvocationBoundary'`; all pass. Commit explicit paths: `feat: expose authenticated one-child memory delegation`.

### Task 4: Protected materialization, disclosure and parent taint

**Ownership/files:** API `CovenantDispatchGate.cs`, `WizardIntelligenceProvider.cs`, capsule materializer/provider-fence helper, `ToolExecutionPipeline.cs`, typed processed-tool result seam, `SubagentContracts.cs` result sensitivity fields, `SubagentRunner.cs`; API `Intelligence/GrimoireTurnWriter.cs` and protected tool interaction persistence; Core `Covenant/CovenantDerivedOutputInventory.cs`. Tests: `Intelligence/MemoryDelegationDispatchTests.cs`, `Api/MemoryDelegationSensitivityTests.cs`, affected protected turn/tool persistence and inventory suites.

**Consumes:** Source fragments/labels, bound child capability and effect admission from Tasks 1–3. **Produces:** Narrow capsule-only read/disclosure scope with no mutation collector; correct typed sensitivity and provenance through all output sinks.

- [ ] Write RED tests `Capsule_child_has_selected_fragments_only`, `Proposed_stays_data_with_origin_and_token_attribution`, `Disclosure_failure_sends_nothing`, `Revoke_after_disclosure_sends_nothing`, `Expiry_after_provider_completion_blocks_publication`, `Child_label_reaches_parent_next_send`, `Protected_tool_pair_commits_text_and_label_atomically`, and `Interrupted_or_empty_output_does_not_downgrade`. Assert no tools, retrieval or mutation capability despite protected admission.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter 'FullyQualifiedName~MemoryDelegationDispatchTests|FullyQualifiedName~MemoryDelegationSensitivityTests'`; observe behavioral RED.
- [ ] Materialize only selected frozen fragments into the child with existing CONTEXT/DATA fencing, placement and token source attribution. Add a dedicated capsule read-only admission/disclosure scope: ordinary `BeginTurnAsync(None)` remains ineligible. Include capsule and accumulated tool sensitivity in `ResolveSensitivity`; acknowledge disclosure before fenced physical send. Merge trusted labels before any stringification/persistence/emission of the child summary.
- [ ] Extend typed result/tool pipeline and atomic Session tool-interaction persistence. `SubagentRunResult` carries sensitivity/provenance even on partial/failure paths; the delegate tool conveys a trusted typed result for the pipeline to consume before exposing text. Extend `GrimoireTurnWriter.TryAppendToolInteractionAsync` so labels commit with text before entry events. Retain an output-publication effect through root HTTP serialization so invalidation cannot race the first/last response bytes. Final and interrupted outputs retain the maximum label and provenance; logs/progress stay content-free.
- [ ] Test precise race barriers at hydrate, disclosure acknowledgement, reservation/admission, provider completion and parent response start. Mutation admitted first yields zero subsequent provider bodies or plaintext publication; prior admitted sends remain truthfully accounted. Run all affected protected-output inventory/persistence/evaluation suites and new tests; commit explicit paths: `feat: preserve capsule disclosure and output sensitivity`.

### Task 5: Contract docs, full qualification and exact delivery

**Ownership/files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.OATH.md`, focused architecture/native smoke inventory files required by the new route, and this plan's completion checkboxes. Do not change README, CLI, Forge or configuration surface.

**Consumes:** Complete reviewed implementation. **Produces:** Documented native contract and terminal exact-candidate evidence for integration/closure.

- [ ] Add end-to-end regression proving authenticated wrapper → bound delegate selector → exact source child → protected parent response, including Session-backed persistence. Add route/source-generation/capability-not-serialized/derived-output/native smoke inventory coverage; observe RED before any necessary inventory implementation.
- [ ] Update exact API body/result/errors, child isolation exception, conservative relevant-mutation revocation, limits/expiry/no recovery, and OATH #107 status in owning docs. Reconcile any reviewed interface refinements into this spec/plan.
- [ ] Run `dotnet build RetroDownfall.Arcanum.slnx`, main and Compendium test projects, `./scripts/coverage.sh --threshold`, `./scripts/verify-aot-il-warnings.sh`, `./scripts/verify-shipping-publish.sh --rid osx-arm64`, and `./scripts/verify-native-sqlcipher.sh --rid osx-arm64` as required by repository qualification. Use CI for all required shipping/platform/source-analysis lanes; do not claim focused passes qualify delivery.
- [ ] Perform whole-change correctness/security review, fix validated issues with RED/GREEN regressions, then requalify changed candidate. Require terminal green logs/artifacts for every required job at the exact candidate; unchanged qualified trees may use equality proof under the repository delivery contract.
- [ ] Commit explicit final paths, push candidate, prove `memory-enhancement` fast-forward eligibility and exact SHA/tree/ref equality, integrate only the qualified candidate, verify remote target and issue state, then close #107 as completed. Attach any created PR with the app tool. Preserve unrelated checkout state and report exact delivery evidence.

## Self-review and handoff

Spec coverage maps to Tasks 1 (proof/bounds), 2 (lifecycle/races), 3 (wrapper/binding), 4 (materialization/sensitivity), 5 (docs/qualification/delivery). All five Review Focus cases have named tests. Shared interfaces are defined once above; no task can issue a new generic operator context to a child. Mutation waits are outside transactions and bounded by active effect cancellation/drain, not parent lifetime. This plan is ready for the already-authorized implementation; the coordinating agent owns integration and exact delivery.
