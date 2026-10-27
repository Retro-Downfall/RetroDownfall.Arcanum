# Covenant Review Queue Reliability Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans and test-driven-development inline, with no child agents. Track each behavioral RED and exact candidate gate separately.

**Goal:** Repair RV197/198/199/202/207/208/209 while preserving exact-version Review authority and atomicity.

**Architecture:** List and Prepare borrow deferred read snapshots and synthesize initial marker0/revision1 only from a proven current canonical generation. Apply creates a missing marker inside its existing immediate, replay-first, all-or-nothing transaction. Exact fixed SQL variants use existing partial indexes; closed diagnostics and optional target labels complete the operator contract.

**Tech Stack:** .NET10, native pinned SQLCipher initialized by the existing fixture, xUnit, source-generated API/CLI JSON, existing fake HTTP command harness.

**Spec:** Dated RV register/U24 reports; root-approved `/private/tmp/arcanum-covenant-review-queue-preflight.md` (2026-10-26).

## Global Constraints

- Start only from root's committed post-storage controller base; preserve storage9c9f and all prior branches/artifacts.
- No schema/version, authority interface, provider, installer, operator configuration, main or push changes.
- No raw exceptions/messages/type names, authored content/keys/source/token/hash values in diagnostics.
- No automatic reopening/retry after uncertain cleanup; preserve cancellation and real maintenance/disposal refusals.
- No INDEXED BY or relaxed source/performance guard; exact SQL executed in production is the SQL explained in tests.
- Existing token lifetimes, scopes, decisions, confirmation, replay-before-expiry, curation and filtered-frontier CAS stay strict.

## Review Focus

- Missing marker on a valid queue and on arbitrary Campaign IDs must produce no durable row until successful apply.
- First apply and a concurrent sibling plan must create/advance once and then refuse stale revision1 tokens.
- Late kernel refusal rolls back marker establishment and every earlier decision/receipt.
- Empty queue with many unrelated events must seek its own partial index and avoid installation-wide native VM work.
- Corrupt replay and storage errors must give content-free cause/stage guidance, with null exception and no structured-value leak.

### Task 1: Read snapshots and marker establishment (RV197/198/202)

Files: existing CovenantMemoryReviewService.cs; partial CovenantMemoryReviewServiceTests.Reads.cs; minimal original test fixture corrections.

- [x] Add first behavioral tests using existing native fixture: arbitrary Campaign lists leave marker count0; List fresh/existing queue and Prepare succeed while second connection holds BEGIN IMMEDIATE; a second writer reservation can succeed during final read-lease revalidation.
- [x] Run narrow owning filter and retain genuine RED on unchanged production, native errors and exact test hashes. Exclude compile/tooling corrections.
- [x] Make List/Prepare read-only deferred snapshots, synthesize marker only after current canonical state validation, and establish it inside existing Apply transaction after replay/target validation. Keep absence distinct from corrupt state.
- [x] Prove GREEN plus first apply revision2, failed apply marker rollback, stale sibling refusal, legacy marker and replay/expiry controls.

### Task 2: Exact indexed query contract (RV199)

Files: new narrow CovenantReviewSql.cs and direct service callsites; new CovenantReviewQueryPlanTests and native work tests; exact source/SQL catalog entries.

- [x] Add RED for actual queue/marker/curation SQL plans for both scopes, plus native VM work with fixed target queue and increasing unrelated events. Preserve row/frontier/receipt semantic oracles. Point ReadEvent requires efficient primary-key seeking.
- [x] Extract only existing statement shapes into shared internal fixed-predicate builders; select literal IS NULL or equality; parameterize all values and preserve all filters/order/CAS/epochs.
- [x] Verify native named-index plans and stable unrelated-row work envelopes. Do not claim strict wall/CPU bounds or O(page) for many filtered rows inside the same queue.

### Task 3: Closed actionable diagnosis (RV207)

Files: review service, a focused diagnostic partial only if useful, owning tests; reuse approved neutral storage classifier.

- [x] Add RED from real source-count and replay corruption, canonical-state absence, bounded native busy and faulting acquisition; assert exact public codes/guidance, closed operation/reason/stage fields, severity, one log, null exception and privacy across rendered/structured values.
- [x] Introduce minimal closed reason/stage taxonomy and typed known integrity failures; map read storage failures contextually while retaining existing apply categories/authority exceptions. Busy Warning; real structural faults remain faults. Do not copy InvalidDataException.Message.
- [x] Verify every actual integrity return family is diagnosed without changing RequestReuse/stale semantics or rollback/receipt behavior.

### Task 4: Keys before confirmation and consistent DTO hashes (RV208/209)

Files: shared MemoryReviewBulkPlanItemDto, review PlanItem/DTO hash helpers, CLI WriteReviewPlan; service/CLI/contracts/serialization tests.

- [x] Add RED that real service serialized plan has label equal to normalized Covenant key, human CLI displays it before confirmation with no apply on decline, and review Head/Source hash strings equal lower-case Management/native bytes.
- [x] Append optional Label=null to the existing source-generated shared DTO and render nonnull Covenant labels beside exact GUIDs. Saga/Lexicon preserve null and existing data/authority behavior. Lower-case DTO formatting only; hash bytes/tokens/receipts untouched.
- [x] Run complete owning CLI/contract/JSON/store controls and document API §8.34/8.28, DESIGN §21.14 and CLI surface.

### Task 5: Exact candidate closure

- [ ] Run selected meaningful causal mutants for reads/marker write, partial-index predicate, privacy/closed cause, busy severity, label/CLI visibility, hash case and marker atomicity. Restore every owned path exactly after each.
- [ ] Refresh only actual owning source paths/read ordinals in the closed benchmark catalog; preserve schema and authority/source scanner checks.
- [ ] Run complete affected classes, normal zero-warning solution build and owned house/compact/Roslyn styles. Keep already open Hosted global gate separate; map candidate-owned diagnostics precisely for closure worker without repeating its unchanged whole-class run.
- [ ] Freeze clean explicit-path commit/tree/parent, full-context diff, source snapshots, terminal TRXs, source hashes, restoration receipts and immutable review package. Root independently reviews/integrates. No full-CI/live credit.
