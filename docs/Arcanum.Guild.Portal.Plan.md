# Guild Portal Implementation Plan

> For agentic workers: use the TDD execution workflow for each task. This plan implements the already resolved contract; it does not activate Guild.

**Goal:** Implement issue #148's durable addressed Portal on the current `guild-implementation` integration base.

**Architecture:** The Portal uses the existing installation admission, Signet root gate, and Guild SQLite transaction. Host-derived tagged producers, immutable envelopes, ordered recipient claims and durable input/application receipts share that transaction. The existing Quest ledger remains the execution authority. Source transactions persist content-free projection events and delivery intentions; retries consume those intentions without repeating child execution.

**Tech stack:** .NET 10, parameterized SQLite over the admitted SQLCipher connection, source-generated JSON, Native AOT.

**Spec:** `Arcanum.DESIGN.md` §23.7, §23.15, §23.16 and GitHub issue #148.

## Constraints and boundaries

- Bodies are at most 65,536 UTF-8 bytes; envelope metadata is at most 16,384 bytes. Other variable metadata is at most 65,536 bytes. Complete metadata DTOs are at most 262,144 encoded bytes.
- Pages are at most 100 records and 1 MiB. Work batches are at most 100 rows; these bounds do not limit lifetime work or inbox depth.
- Tagged Run, native Operator and HostReceipt identities never borrow authority from an attribution field. Host-only kinds require their actual source receipts.
- Ordering is per recipient, tagged producer and Quest. The exact current LRO holder/revision and inbox revision fence durable effects.
- Persist neither raw Signets nor hidden reasoning. Ward remains informational.
- Cohort membership is owned by #155; unimplemented Cohort addressing fails closed. HTTP/CLI/SSE activation is owned by #158. Provider execution and accounting composition remain with their existing owning slices.
- Preserve published schema fingerprints and persisted enum codes. New names use Portal; historical Circle wire identifiers remain compatible until their owning public-surface delivery decides migration.
- No PR or hosted CI run. Complete local qualification, then integrate and push `guild-implementation`.

## Review focus

1. A retry after erasure or receipt loss must retain its accepted identity without rereading erased bodies or reexecuting child work.
2. A stale/revoked holder, sibling Quest, forged receipt source or restored credential must not disclose content or acknowledge an effect.
3. Storage failure at any admission/effect statement must leave either the whole transaction or none of it, with pending source evidence retained.
4. A page that filters or withholds records must advance its durable scoped cursor; cross-principal, root or reset-generation replay must fail closed.
5. Cleanup and protected erasure must preserve immutable sequence/dedupe evidence and visibly mark unavailable history.

## Task 1: Schema and immutable evidence

- [x] Capture the published Guild v3 schema fixture and write failing fresh-install, v3-upgrade, atomic constraint and immutable evidence tests.
- [x] Add Guild v4 streams, envelopes, inboxes, application/input receipts, source outbox, event sequence/index and scoped cursors, with declared transitions and unchanged Core fingerprint.
- [x] Run schema tests to green, including earlier Guild upgrade paths and timestamp inventory.

## Task 2: Verified atomic admission

- [x] Write failing real-store tests for producer tagging, same-key concurrency/conflict, relationship checks, host-receipt proof, body/metadata limits, all-recipient rollback and no-subscriber durability.
- [x] Implement `GuildPortalStore.PublishAsync(GuildCallerContext, Guid, GuildPublishRequest, CancellationToken)` using host-derived identity and the existing Signet transaction wrapper.
- [x] Prove current body sensitivity and owner checks at disclosure, including erasure and storage deferral.

## Task 3: Ordered durable effects

- [x] Write failing tests for interleaved producers, earliest unresolved selection, concurrent consumers, replaced/expired leases, lost acknowledgements and permanent withholding.
- [x] Implement claims bound to a live `GuildQuestDriver`, and typed durable-input/result effects with receipt and inbox acknowledgement in one transaction.
- [x] Preserve separate nondestructive Operator projection and visibility receipts.

## Task 4: Projection replay and source integration

- [x] Write failing tests for source-transaction event sequencing, outbox restart/retry, scoped cursors, byte/row page boundaries, erased history and explicit resync.
- [x] Add root-monotonic content-free events at source receipts, durable outbox drainage, and bounded authorized replay with live-per-page state.
- [x] Preserve Chronicle's no-subscriber/no-hub allocation behavior; tool frames remain live only.

## Task 5: Lifecycle and qualification

- [x] Write failing real-store retention, erasure, shared cleanup cursor, reset and schema-isolation tests, then integrate new objects into their existing lifecycle owners.
- [x] Update generated JSON, AOT graph, dormancy, connection, timestamp, constraint and hosted-producer inventories as applicable.
- [x] Update DESIGN, API, Command Reference, configuration and Guild implementation documentation with the exact implemented/dormant boundary.
- [x] Complete an independent whole-change review and correct discovered implementation defects with regression tests.

Local delivery qualification requires the full runtime coverage and hosted-producer proof gates, including reviewed manifest changes and worker-parity measurements. Focused runtime/schema/boundary tests, desktop suites and the Guild native AOT smoke provide additional scoped evidence. The delivery report records the final outcomes and their limits.

## Delivery procedure

After local qualification, commit the candidate and merge that qualified tree into `guild-implementation`, push, and verify the remote head/tree. Record the commit and local qualification evidence in the delivery report. Do not create a PR or dispatch a hosted CI run.
