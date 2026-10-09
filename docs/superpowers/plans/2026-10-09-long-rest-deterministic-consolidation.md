# The Long Rest implementation plan

> **For agentic workers:** Use the TDD and verification skills for each task; independent file ownership permits parallel work without conflicting edits.

**Goal:** Deliver #93's deterministic Saga consolidation, immutable receipts and exact-version recall projection.

**Architecture:** Pure Core policy over bounded exact snapshots; admitted raw-SQL application kernel; declarative schema and common lifecycle cleanup; API orchestration and source-generated contracts.

**Tech stack:** .NET 10 Native AOT, SQLCipher, source-generated System.Text.Json, xUnit.

**Spec:** ../specs/2026-10-09-long-rest-deterministic-consolidation.md

## Global constraints

- Preserve all immutable source versions, origins, Campaign binding, sensitivity, pin/retirement and provenance.
- Maximum sixteen exact targets; no floating-point, storage Sequence or platform normalization in policy identity.
- Parameterized direct SQLite inside one admitted transaction; no EF schema/model changes.
- Typed Result flow, exact /api payload registration, attached canonical documentation.
- Retain unrelated primary-checkout work. Deliver to existing memory-enhancement after exact-candidate green.

## Review focus

- Protected artifact label despite an untainted Annals version.
- Overlapping groups forming chains or hiding the only survivor.
- Pin/correction changing source eligibility after an applied receipt.
- Erasure deleting an entire receipt rather than leaving a partial manifest.
- Filtering after top-K starving unrelated eligible results.
- Consolidation and prior-output context changing canonical identity after erasure removes a blocking projection.
- Verified historical applied replay after an input head advances.
- Protected retired inputs proving their retained predecessor digest without plaintext or caller-supplied authority.

## Task 1: Pure policy and contracts

- [x] Write behavioral tests for deterministic selection, literal canonical bindings and typed no-change boundaries.
- [x] Observe RED; implement Core/LongRest contracts and policy; observe GREEN.

## Task 2: Schema and lifecycle

- [x] Freeze Core v16 fingerprint and fixture before edits; write missing-object/upgrade/immutability/erasure tests; observe RED.
- [x] Declare Core v17 receipt/input/suppression objects, transitions and guards; integrate Annals erasure and inventories.
- [x] Observe GREEN for fresh/evolved schema and cleanup.

## Task 3: Canonical application and recall

- [x] Write real SQLCipher integration tests for convergence/replay/pins/dependencies/rollback/recall; observe RED.
- [x] Implement application and receipt inspection with exact transactional revalidation.
- [x] Apply shared suppression predicate before semantic top-K and in hydration/eligibility/detail/search.
- [x] Observe GREEN including curation and lifecycle neighbors.

## Task 4: API and docs

- [x] Write endpoint and serialization behavior tests; observe RED.
- [x] Register authenticated Saga Long Rest POST and receipt GET, DI and source-generated wire types.
- [x] Update DESIGN/API and forward OATH contract to describe #93 and remaining #91/#95 boundaries.
- [x] Observe GREEN.

## Task 5: Qualification and delivery

- [x] Review full change and repair validated defects with regressions.
- [x] Complete local build, client suites, coverage thresholds, mandatory source analysis, formatting, Native AOT warning/shipping smoke and SQLCipher provenance checks. The full runtime invocation's sole archived-baseline fixture failure was reproduced and repaired; the affected test then passed with all tamper checks intact. Full source analysis passed all 2,019 cases.

Delivery protocol after local qualification:

1. Commit and push the candidate; await exact-SHA terminal CI, including its complete runtime rerun and source-analysis aggregate.
2. Fast-forward existing memory-enhancement; prove unchanged tree and remote alignment.
3. Mark #93 complete and update parent #75's child checkbox; leave #73/#75 open.
