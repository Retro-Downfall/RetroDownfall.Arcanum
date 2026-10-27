# RV185/RV186 Cache Publication and Maintenance Catch-up Implementation Plan

> For agentic workers: use `superpowers:executing-plans` after root approves the contract and supplies the exact implementation base. No child agents. This document is a read-only preflight; no implementation is authorized by its existence.

**Goal:** Stop redundant idle Covenant publications from invalidating DCI v2 cache evidence, and drain real owner-deletion/outbox progress promptly without weakening any authority or batch boundary.

**Architecture:** Coalesce only equivalent persisted-state/feature publications atomically in the existing runtime holder. Keep forced publication semantics and all cache authority generations. Retain one existing bounded batch of each maintenance lane per pass; pace another pass at 500 ms after committed progress, preserving current unavailable/no-progress delays.

**Tech stack:** Existing .NET 10, pinned native SQLCipher, immutable Covenant runtime snapshots, `BackgroundService`, injected `TimeProvider`, existing operation/admission leases and native fixtures. No package/schema/API change proposed.

**Spec and exact baseline:** Clean committed controller `c925036083c967a0f28c3ec511ba0d5a4e7c6d6e`, observed 2026-10-27. Register `docs/reviews/2026-10-24-repository-code-review.md` RV185/RV186 at 2568/2580; U23 performance report sections 2/3. Canonical DESIGN §5.4.5b (631–635), maintenance/delegation contract (2024–2026), feature configuration (§10.18), Long Rest capability distinction (6054), DCI v2 scope/currentness (6215 onward). Git-object source snapshot and blob/SHA256 manifest: `/private/tmp/arcanum-rv185-186-preflight-source/manifest.json`.

## Current evidence and dated distinctions

- **RV185 is still present.** Idle cleanup's read-probe path republishes; outbox republishes after each commit; `CovenantRuntimeGenerationProvider.PublishAvailability` (203–235) increments the counter unconditionally. `PublishPersistedState` always changes `LastHealthTransition`, so ordinary record equality without normalizing that diagnostic field cannot stop alternating idle publications. The cache marker includes `Availability.Generation` (validator 213), and final physical-dispatch validation compares the complete captured authority record (306).
- **That counter is intentional authority evidence.** Existing availability tests explicitly require forced schema publications and 512 concurrent forced publications to yield distinct monotonic generations. A committed reset/restore additionally binds the exact composite object and expected generation. Do not rewrite these contracts to make a global “ignore unchanged publications” implementation pass.
- **RV186 is still present.** The driver calls cleanup once with 64, outbox once with 512, compaction once with 16, then waits five minutes after any admitted pass. The cleanup reader still orders the combined coalesced Session/Campaign queue. This preflight traces current code; it does not claim new timing measurements.
- **Two relevant Important defects were already repaired.** Commit `6b93f460e939711ca37172a53156a0050e2b3459` (2026-10-25 17:32 -04:00), “drain oversized outbox sequences and preserve idle grants,” added the idle cleanup read probe and whole-sequence outbox fallback. Preserve both; do not claim them again.
- **A full-batch equality test is insufficient for the outbox.** `ChooseContiguousTarget` may consume fewer than 512 rows because the next entire sequence will not fit; its existing first-sequence fallback can also consume more than 512 under the canonical pending ceiling. `RowsConsumed == 512` misses both. Use positive committed consumption as a scheduling hint, not as authority or a completeness claim.

## Global constraints

- Preserve runtime authority generation, availability generation for actual/forced publications, authority/key object identity, dataset generation, canonical/deletion/applied sequences, accelerator epoch, Campaign/path generations, configuration and credential ABA stamps.
- Leave DCI marker inputs and complete physical-dispatch authority equality intact. No new cache eligibility, dispatch or protected-read authority.
- Read persisted facts on the committing connection after COMMIT and under the existing gate lease. Keep idle corrective reads even when a batch applies zero rows.
- Keep native transaction, cursor, replay, whole-outbox-sequence, full-sweep-debt, revocation, memory-delegation barrier-before-mutation and ordinary-work-lifetime boundaries.
- Preserve optional-tier failures, per-lane isolation, safe warning deduplication, cancellation and reset-aware hosted registration.
- No SQL/index/schema/version change is needed. Core29 and all predecessors stay frozen. No new production path or acquisition root is expected.
- Only proposal/docs in `/private/tmp` now. No worktree switch, source edit, tests, build or live service call has occurred for this preflight.

## Approaches and recommendation

**Recommended:** narrow atomic coalescing plus progress-paced single-batch passes. This preserves the current small transaction/lease units and releases the ordinary work lease and scope before each scheduling delay. One extra short pass after the final nonempty batch is acceptable; it establishes no further progress before restoring the five-minute cadence.

**Alternative:** repeated full rounds inside a two-second/16-round pass, followed by short catch-up delay when capped. This can reduce scope/setup overhead but holds ordinary admission longer, needs both a monotonic budget and a hard round cap, and complicates lane fairness. There is no evidence yet that this extra machinery is required.

**Rejected:** remove availability generation from marker/validation, globally coalesce every schema/authority publication, skip idle publication reads solely because zero rows were applied, increase native batch sizes, or loop until an unbounded backlog is empty.

## Review focus

1. Same facts with alternating transition names must be a true no-op; changed applied/deletion/health facts must still invalidate stale cache evidence (Task 1).
2. Feature/health A→B→A and forced equal schema publication must remain observable through generations; no authority or key swap may be lost (Task 1).
3. A zero-row pass must still correct a stale published tuple after a missed publication, including a Campaign deletion with no heads (Task 1).
4. Positive progress below/above 512 and Session-only cleanup progress must receive prompt catch-up without changing atomic sequence/cursor semantics (Task 2).
5. Sustained work, refusal, cancellation and exclusive admission must release all scopes/leases and avoid a fast retry loop without durable progress (Task 2).

## Task 1 — narrow no-op publication suppression (RV185)

**Production ownership proposal:** `Infrastructure/Data/Covenant/CovenantAvailability.cs`, `CovenantPersistedAvailabilityPublisher.cs` (contract wording only if sufficient), `Infrastructure/Security/CovenantRuntimeGenerationProvider.cs`. `CovenantFeatureConfigurationPublisher.cs` only for its now-accurate publication comments; retain its serialized monitor-current-value behavior. No API production edit.

**Interfaces:** retain `PublishAvailability(Func<CovenantAvailabilitySnapshot,CovenantAvailabilitySnapshot>)` as forced. Add an internal `PublishAvailabilityIfChanged` with the same signature and return type, sharing the same holder lock and dataset-authority validation. `PublishPersistedState` and a new `PublishFeatureEnabledIfChanged` select this conditional route. Direct `PublishFeatureEnabled` remains forced; the configuration observer uses the conditional entry, per root’s approved refinement. Normalize candidate `Generation` and `LastHealthTransition` to the current record for the equality test, so every other field participates. An equivalent candidate returns the exact current snapshot/composite reference; a changed candidate uses the existing monotonic increment/swap. Schema publication and committed transition remain forced.

No-op publication still means a successful persisted-state read, so `PublishAsync` must continue returning true rather than generate an “unreadable” warning. Document `LastHealthTransition` as the last publication that actually moved this conditional state. Do not do a racy check before entering the holder lock or only in the options callback.

**Tests:** existing `CovenantAvailabilityRepublicationTests`, `Data/Covenant/CovenantAvailabilityTests`, `Security/CovenantRuntimeGenerationProviderTests`, `CovenantFeatureConfigurationPublisherTests`, `Intelligence/PromptCacheDispatchValidatorTests` (a focused partial is acceptable if source census is recorded).

- [ ] Add native `Idle_passes_preserve_the_exact_availability_snapshot_and_cache_scope`: warm a real isolated host to synchronized, remove its timer through the existing `Host(...)` fixture, run ten actual maintenance passes, assert every persisted fact and exact snapshot reference/generation remain stable. Feed that real publication into cache preparation and require the same DCI marker/cache key and Current dispatch with other proofs unchanged. Watch genuine RED before production.
- [ ] Add same-feature callback/no-op and alternating-transition tests; assert no generation advance or false unreadable warning. Real flag changes must still advance and remain visible without storage I/O.
- [ ] Add/retain each actual persisted-coordinate change, missed-publication correction, zero-head Campaign delete, dirty→synchronized, degraded accelerator, rollback, forced equal schema publication, initialized dataset-change refusal, composite key/authority identity, concurrent publication and A→B→A controls. Prepared cache evidence must replan after real/forced changes, even when final feature/health values equal the starting values.
- [ ] Implement the conditional publication route only after RED; keep all current forced-publication assertions. Unrelated real configuration edits may independently advance the configuration epoch, so do not claim the whole cache proof stays current through those edits.
- [ ] Run complete owning classes. Mutate conditional suppression to always publish (idle REDs return), then mutate real-change/ABA publication to suppress changes (authority controls fail); restore exact bytes. Update DESIGN §5.4.5b, maintenance discussion and the historical “every idle pass publishes” wording in the Long Rest section without changing Long Rest's capability algorithm.

## Task 2 — progress-paced bounded maintenance (RV186)

**Production ownership proposal:** `CovenantMaintenanceHostedService.cs`; coordinators only if the necessary typed progress cannot be carried without altering their existing commit boundary. Leave cleanup reader/worker SQL and outbox worker algorithm unchanged.

**Interfaces:** add internal `CovenantMaintenancePassOutcome(bool Admitted, bool Advanced)` and `Task<CovenantMaintenancePassOutcome> RunPassAsync(CancellationToken)`. Keep `RunOnceAsync(CancellationToken): Task<bool>` as a thin compatibility wrapper returning `Admitted`; both paths use the same actual pass. `ExecuteAsync` selects 500 ms after `Advanced`, existing five minutes after admitted/no-progress, existing one minute after unavailable/refused admission. Progress is a local per-pass value, never a shared pending flag.

Set `Advanced` only after successful committed coordinator outcomes: `CampaignsCleaned + SessionsCleaned > 0`, or outbox `RowsConsumed > 0`. A refused/thrown attempt, `RebuildRequired` with zero consumed rows, or a zero-row read is not progress. All three lanes still run once and independently in every admitted pass; compaction does not gain an independent rapid-retry policy in this bounded batch. An advancing lane can justify a prompt next pass even if another lane refused, but persistent refusal alone cannot manufacture a fast loop.

**Tests:** `CovenantMaintenanceHostedServiceTests`, `CovenantMaintenanceWiringTests`, `Data/Covenant/CovenantMaintenanceCoordinatorTests`, `CovenantOwnerCleanupTests`, `CovenantSearchOutboxWorkerTests`, `CovenantAvailabilityRepublicationTests`, and admission/delegation lifetime controls.

- [ ] Native RED: seed more than two cleanup batches of Session deletions followed by a Campaign deletion carrying real Covenant rows. Drive the actual hosted loop with `ManualWallClockTimeProvider`: the first pass is bounded; another begins after 500 ms instead of five minutes; subsequent bounded passes reach the Campaign, advance both exact cursors and remove its owned rows. Require initialized native SQLCipher, real gate/transactions, no timer sleeps or fake empty outcome.
- [ ] Native outbox RED/controls: an initial complete range below 512 with another sequence pending, a previously repaired atomic sequence above 512, repeated deltas coalesced to fewer projections than consumed rows, and an empty terminal pass. Assert prompt retries follow positive consumed rows, no partial sequence is published, and cadence returns to five minutes after no progress.
- [ ] Test real scheduled delays through the existing injected time provider, not only a helper that mirrors a switch. Cover unavailable/disabled/admission refusal, failure-only/no-progress, rebuild-required/no-progress, cancellation, no post-stop pass, and no starvation of outbox/compaction by cleanup progress. Prove the ordinary work lease lasts through async scope disposal and is released before the next delay; an exclusive owner can acquire between passes. Keep idle memory-delegation grants alive; nonempty/full-sweep work still uses the existing barrier and gate order.
- [ ] Implement one-batch progress pacing after RED; preserve reset-aware registration, warning deduplication and lane isolation. No query rewrite or elapsed-CPU promise.
- [ ] Run complete owning classes; causally force five-minute progress delay and false fast-progress on idle/refusal, prove the native/timer tests fail, and restore exact bytes. Document cadence as inter-pass scheduling after completion, not a wall-clock deadline for native work.

## Optional adjacent finding — decision, not added scope

**RV188** remains independently visible at current `CovenantEntryErasureService.ApplyAsync` line 350: `memoryDelegation.BeginOptionalAsync` precedes null/token validation, replay and absent-target handling. It is a small related *no-op drain/revocation* defect and naturally shares the already-preserved idle-cleanup principle. It is not RV185/RV186 and would touch a separate irreversible-erasure path. Include only after root explicitly approves its extra ownership/acceptance; otherwise keep it as the next cohesive erasure-lifecycle item with RV187/RV193.

If approved, first prove genuine malformed, replayed, absent and stale requests leave a real live delegation root/effect usable, paired with a real mutating erase that still drains effects before acquiring the erasure gate after its probe lease is released. Preserve receipt-first replay and all authority generations. No changes are proposed now. RV187's scan optimization and latent RV189–194 are not part of this cache/driver plan. The already-fixed oversized outbox/idle cleanup findings receive no new credit.

## Dependencies, worktree and verification proposal

1. Wait for root's review/integration of the frozen RV229 packet. Receive the exact new committed controller SHA; do not base execution on mutable controller files or silently keep the now-stale c925 base.
2. Reuse the clean assigned provider-pins worktree. Preserve `codex/review-covenant-lifecycle-operator` and its sealed d6cc commit, plus prior harness branches. After authorization, create a new `codex/review-covenant-cache-drain` branch at the root-supplied committed base. No rebase/reset/stash/main edits or additional worktree are needed. Verify clean status, branch and full SHA first.
3. Record a dated tracked plan only when implementation is authorized. Root owns integration/review; no overlap with its current RV195/RV196/RV206 inspection work, which uses management/status/explain paths. No shared DI edit is expected. Coordinate any unexpected schema need before editing; none is proposed.
4. Minimum complete-class gate: the publication/config/runtime/cache validator classes above; maintenance driver/wiring/coordinator/owner-cleanup/outbox worker classes; Covenant operation gate and concurrency classes; MemoryDelegationLifecycle; existing ordinary admission/physical-drain lifetime controls. Also normal zero-warning solution build, owned styles, SQL/AOT/acquisition/source/documentation guards and exact catalog checks. Derive the final actual class list from edits rather than claiming a precomputed pass count.
5. Preserve every genuine RED/control and exact source binding; record fixture/tool failures separately. After causal mutations, restore exact source hashes and rebuild/requalify focused controls before freezing SHA/tree, full-context diff, source/member/SQL/acquisition delta and immutable review packet. No new production path or SQL/acquisition root is anticipated; report actual changes precisely. Coordinate the existing hosted roots and changed member map with the Hosted closure owner; no unchanged global Hosted rerun, exemption, zero-delta inference or full-CI credit.

**Self-review:** both original findings have native behavioral REDs and causal tests; every authority generation and forced-publication contract is retained; the scheduler uses actual committed progress rather than a false completeness heuristic; outage/cancellation/disposal/fairness controls are explicit; optional RV188 is separated pending a ruling. The read-only preflight ended before implementation; execution now follows the approved base and the private evidence ledger.

## Execution checkpoint (2026-10-27)

Implementation base is exactly `e596143f1f74869f529290ec99c19a7f13210a5c`; all31 preflight paths were compared before edits. Only DESIGN and the register differed from preflight because RV229 was integrated. Root approved preserving the direct forced `PublishFeatureEnabled` contract and its512-publication test, with a separately named conditional entry for the actual configuration observer.

The publication cycle produced4 genuine RED failures and2 forced-publication controls. Two later diagnostic-transition expectations were intentionally superseded by exact captured-reference stability plus the unchanged restart-coordinate oracle. The outbox scheduler had2 genuine native500ms-versus5minute REDs. The first owner fixture failed an unchanged retention authorization guard; it was corrected to use the existing narrow retention scope, then replayed on the exact base scheduler to produce1 genuine cadence RED. Those earlier fixture/analyzer/namespace failures are retained and excluded as behavioral evidence. A native catch-up case then proved duplicate rebuild warnings; root approved a fixed-cause/dataset/accelerator-epoch key, rearming after recovery. The complete initial owning gate passed213 cases with no skips. Final source-bound gates, causal checks and exact restoration are recorded in the sealed review packet; no global Hosted completion is claimed.
