# Retention plan authority and expired-claim diagnostics implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline with TDD; no child agents.

**Goal:** Resolve RV240's internal Covenant-decoration metadata loss and RV242's remaining expired-idempotency diagnostic scenario without changing retention or replay authority.

**Base:** Qualified controller 7b18ca645bf82787eded8e0b79fe88edc074fcb1, explicitly approved by root after dependency comparison. Preserve sealed RV260 owner-proof03250a and its parent354bfd branches/packets. The current controller's later four-line explicit UTC capture fix is separately reviewed and pending integration; do not import unrelated runtime changes.

**Spec:** Approved /private/tmp/arcanum-retention-plan-authority-preflight.md, reconciled with this base. BindCovenantErasurePlanIdentity still clones ordinary prune plans while both ConditionalWeakTable authorities remain on the generated source object. Aged unfinished claims still report Data.IdempotencyLeaseActive even after expiry; RV258 cleanup already exists and must retain its strict creation-plus-expiry cutoff.

## Constraints

No schema, public DTO, global PlanId cache, new reconciliation/replay/deletion mechanism, TTL/cadence change, or cleanup-store change. Transfer both exact internally registered weak metadata objects only through the owned Covenant decoration. Preserve protected reset inventory identity, read-capability disposal, all currentness/CAS/checkpoint/recovery/rollback and RV249 bounded-acquisition/known-result rules. Keep live leases' existing diagnostics. New expired code: Data.IdempotencyLeaseExpired, with factual advice about admitted existing aged-claim cleanup and re-planning. No manual row deletion or unrelated operation-replay guidance.

## Task 1: RV240 native RED

- [ ] Extend only test composition for a successful recording Covenant installation read gate plus a real operation store's AfterStart callback.
- [ ] Native request and admitted hosted interleavings happen after their own fresh plan/start; archive with ArchivedSessions disabled or remove one selected Session. Assert changed candidate preserved/idempotently gone, unrelated eligible candidate completes, operation/checkpoint remains truthful, and the read capability closes exactly once.
- [ ] Include healthy, stricter current boundary/policy, unrelated inventory change and caller-clone controls. Supplement behavior with exact weak metadata/cache presence and persisted snapshot/digest/cutoff checks; reflection is supporting evidence only.
- [ ] Run and retain genuine RED with unchanged production before implementing the private owned transfer of both metadata objects.

## Task 2: RV242 native/API/CLI RED

- [ ] Seed old Claimed/Running rows with expired leases and observe actual plan diagnostics, distinguishing a live lease and recent/exact-cutoff expiry.
- [ ] Prove real existing native TTL cleanup removes only rows whose creation AND expiry are strictly older than its cutoff, without replaying request keys. Diagnostics then disappear on re-plan; recent creation/expiry, exact boundary, live/unknown state, terminal policy and independent heartbeat/reclaim remain protected.
- [ ] Exercise real API plan DTO and CLI human/JSON output using native computed plans and existing fake HTTP transport; no provider/service calls.
- [ ] RED before the narrow diagnostic classification/copy change; update owning DESIGN/API/Command Reference/catalog rows as applicable.

## Task 3: Qualification and review

- [ ] Complete actual affected Covenant/retention/age/checkpoint/recovery/hosted/admission, claim-store/Unseen Servant, API/CLI/sourcegen/docs/SQL/acquisition/schema classes. Preserve the known separately owned four UTC capture source-boundary failures against their exact baseline until root's fix integrates; no guard weakening or false all-green credit.
- [ ] Normal zero-warning solution build and owned house/compact/Roslyn whitespace/style/analyzers/diff checks.
- [ ] Meaningful causal removals with executable bodies/raw TRX and exact full tracked+untracked source restoration; rebuilt post-restore checks.
- [ ] Explicit owned-path commit and immutable source/Git/snapshot/RED/causal/qualification package for independent review. Do not rerun or claim closure of the known-open global Hosted gate.

## Dated execution refinements

- 2026-10-27: corrected unchanged-base native RED is six genuine failures/three controls; request and real admitted hosted after-start archive/disappearance plus expired Claimed/Running diagnostics. Initial compile failure and four invalid fixture refusals are preserved and excluded. Minimal transfer/classification repair passed all nine cases.
- The selection-to-freeze native probe independently proved archive/disappearance can abort planning even after the admitted identity selection completed (two failures/one metadata control). Root approved carrying each accepted identity's exact selection cutoff and retaining live apply checks. No MinValue/new later-status cutoff is allowed. A second native collision probe proved one identity measured twice after active-to-archived transition (one failure/fifteen controls); exact identity deduplication now retains the stricter cutoff without double-counting or double-consuming the candidate limit. These are adjacent defects, zero extra original finding credit.
- Exact weak-authority/snapshot reference identity, unowned caller clones, stricter current policy, native checkpoint dates, caller cancellation, API plans and CLI human/JSON through actual native host output are covered. Boundary fixtures use the actual claim store's compact GUID spelling; the prior expanded fixture's canonical Session-style spelling made GetById return null and is explicitly excluded from behavioral RED.
- The selection helper's private return now carries selected cutoffs. Freezing accepted Sessions removes a later per-Session status read; the fixed SQL and its acquisition site remain necessary for the unchanged live apply boundary/fallback. This is an explicit runtime read-flow change, not a source-count equivalence claim. No new production path, SQL literal, acquisition root or schema change is intended. Final exact inventories and source/member mapping must verify it.
