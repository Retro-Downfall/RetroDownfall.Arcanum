# Budget writer snapshot implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline. Root owns independent review.

**Goal:** Move only validated normal-day committed folding outside the existing budget writer.

**Architecture:** One invocation-local successful exact subtotal is bracketed by native change stamps and validated again after BEGIN IMMEDIATE. An uninterrupted open lifetime, native handle and exact period are required. Every absent/changed hint takes the existing writer fold; outstanding/current-row/decision remain writer-owned. Rare second-day Adjust folds stay under writer.

**Tech Stack:** .NET10, Microsoft.Data.Sqlite, hermetic SQLCipher, existing exact decimal codec.

**Spec:** Owning persistent contract: docs/Arcanum.DESIGN.md §22.2. Approved read-only preflight /private/tmp/arcanum-rv110-preflight.md, checkpoint8fde256119dde00a1dfd4166f8818ca3f432a302; parent approval requires close/reopen invalidation even when pooling reuses a handle object.

## Constraints and review focus

No schema/cache/config/provider/authority changes; no float sum, reordered fold or weakened guard. Preserve cancellation, exact decimal prefix rejection, UTC periods, no-op Adjust and writer decision exclusion. Native source/row/VM evidence distinguishes validated versus fallback work; elapsed times are diagnostics only. Bound event subscription to one call and dispose it on every exit. Source/capsule deltas require exact discovery/review, never broad regeneration.

### Task1: Native RED and smallest GREEN

Files: BudgetReservationService.cs; existing BudgetReservationServiceTests new WriterSnapshot partial; owning DESIGN and this plan.

- [x] Prove normal Reserve/Adjust32/10k/30k committed rows are currently read under writer with real native statement/row/VM trace; add held native read plus actual independent writer control.
- [x] Watch genuine behavior RED on unchanged production; retain full source and raw TRX.
- [x] Implement private candidate/lifetime/stamp helpers, Reserve read-ahead, Adjust period hint and authoritative matching; preserve rare second-day fallback.
- [x] Run native GREEN and complete BudgetReservationServiceTests.

### Task2: Fence/cancellation/semantics controls and qualification

- [ ] Prove external changes before/during/after read, own writes/rollback, schema, pooled close/reopen, period/status/no-op/midnight, unsupported proof, money/cancellation and actual concurrent decisions; causal reversals must fail and restore exact bytes.
- [ ] Record actual inside/outside statement/row/VM counts, including deliberately invalidated fallbacks; retain no constant-time/worst-case speed claim.
- [ ] Update DESIGN with exact proof/limitations; discover exact scoped producer/source-pin delta without guard relaxation, request root review before catalog merge.
- [ ] Complete affected accounting/authority/source/boundary classes, normal zero-warning build and owned styles; freeze/commit/seal immutable candidate for independent review.


Evidence so far: unchanged-production native RED8/3controls (six exact row/VM writer-work cases, two real held-read/independent-writer cases), firstGREEN11. Native own/external/rollback/schema/lifetime/day/money/cancellation controls70GREEN; pooled close/reopen actually reuses native object and scalar stamps but invalidates lifetime. Added no-raise work RED4/2controls, then exact amount hint optimization and complete76GREEN. One speculative extreme-day no-op RED1/56 fixed by discarding that unusable hint; baseline/current native reader cancellation characterization excludes two over-specific token-identity assertions, preserving caller cancellation/no effects/cleanup/reuse. Actual startup scoped discovery's3diagnostics and whole manifest match exactbase; no model/guard relaxation is proposed. Final discovery, causal reversals and immutable qualification remain pending.

Final evidence:78 complete owning tests pass; ten literal causal profiles fail8/2/9/4/2/1/1/1/6/4 with exact restoration, plus stale post-only stamp CRUD supplement4fail10controls. Final production-entry discovery retains exact baseline diagnostics and actual row views for all4existing roots. Root independently approved only23added/15removed BRS capsule rows:15 changed generated-lambda hashes retain their physical effects and8 new DB reads are explicit. Exact application preserves unrelated rows and validates the full BRS projection against final discovery. Global Hosted/fullCI remain open; final owning/consumer/guard qualification and immutable review still pending.
