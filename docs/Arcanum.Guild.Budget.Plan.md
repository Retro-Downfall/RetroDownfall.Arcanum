# Guild budget implementation plan

**Goal:** Deliver issue #153's dormant durable hierarchical budget authority and once-only physical-spend evidence.

**Architecture:** Add Guild V7 accounts, Quest bindings and effect evidence. One admitted immediate transaction partitions each account between own calls and child allocations; exact checked arithmetic derives remaining authority. The existing daily gate and physical billing records remain authoritative for host spend. Public Guild execution remains owned by #150 and remote transport qualification by #152.

**Spec:** Arcanum.DESIGN.md §23.10, §§23.4–23.6 and §23.15; GitHub issue #153.

## Constraints and decisions

- Use existing Quest, Cohort, grant and accounting terminology; no new public domain concept or endpoint.
- Exact invariant decimal TEXT and checked signed-64-bit token arithmetic; no SQLite floating-point USD aggregation.
- A typed account has one funding parent; allocation is a transfer, never a host bill.
- Conservative per-call bounds precede effects; unknown and unresolved usage remain distinct from measured zero.
- Existing immediate transactions own admission, reservation linkage, settlement and replay. No production no-op allocation participant.
- Retain live accounting and replay evidence through owner cleanup; reset keeps its existing active-operation conflicts.
- Keep changes in the isolated worktree. Deliver to guild-implementation as directed, with targeted CI; do not create a PR or update main.

## Review focus

Concurrent sibling/own-call admissions must never use the same remainder. Partial known evidence must reduce its hold without double counting. Replayed or cross-Quest evidence cannot duplicate a bill. Overruns must preserve actual spend and prevent further authority. Daily-off synthetic reservations must never become durable foreign links.

## Task 1: Durable hierarchy and schema

- [x] Write real-store tests for root/child/Cohort allocation, finite/unlimited dimensions, hints, strict excess, precision and overflow; observe failures.
- [x] Add Guild V7 canonical tables and declared transitions with a frozen V6 fixture; prove fresh/upgraded parity and preserve unrelated family fingerprints.
- [x] Implement GuildBudgetLedger allocation, exact remaining projections, closure transfer and replay under existing transactions.
- [x] Pass concurrency, rollback/restart, cohort single-funding and malformed arithmetic tests.

## Task 2: Physical authority and daily gate

- [x] Write failing tests for same-transaction daily reservation admission, disabled nullable linking, owned renewal, rollover and rollback.
- [x] Extract internal transaction-aware BudgetReservationService primitives without changing public daily-gate semantics.
- [x] Write failing tests for conservative bound admission, physical evidence uniqueness, no-dispatch, priced terminal, unpriced terminal, uncertain/partial outcomes and honest overruns.
- [x] Implement durable preparation/exposure, local BillableOperations settlement and the external evidence contract, never child/ancestor aggregate bills; #152 owns remote receipt validation and transport integration.
- [x] Verify exact replay and cross-Quest conflict behavior after reopening the store.

## Task 3: Quest integration, maintenance and read contract

- [x] Replace the fake budget participant in real Quest integration tests; verify allocation is atomic with Quest/LRO/Signet and replay adopts without another allocation.
- [x] Connect durable prepared physical effects to dispatch and result settlement; missing authoritative preparation fails closed.
- [x] Implement authorized IGuildTreeSpendLedger reads and update retention/cleanup pins for unresolved evidence.
- [x] Verify deletion, retention and reset conflicts cannot erase live accounting, and generated JSON/AOT contracts remain valid.

## Task 4: Documentation, review and qualification

- [x] Update DESIGN and each affected API/CLI/configuration implemented-versus-proposed note.
- [x] Add an explicit targeted Guild budget CI profile with selection contract tests; preserve full CI authority.
- [x] Run local targeted regression, schema, contract and AOT verification; resolve all discovered defects with regressions.
- [x] Obtain independent code review and fix actionable findings.

## Delivery procedure

After local qualification, commit and push the candidate and dispatch CI with `qualification=guild-budget`. Integrate only that passing candidate directly into `guild-implementation`, verify the remote revision, and mark #153 completed with the exact commit and run evidence. Keep `main` unchanged and create no PR. The focused run does not replace full coverage, the hosted producer partitions or native Windows qualification.
