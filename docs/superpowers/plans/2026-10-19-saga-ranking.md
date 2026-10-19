# Saga Ranking Implementation Plan

> **For agentic workers:** Use TDD for each deliverable and independent review before integration.

**Goal:** Complete #95 on memory-enhancement with bounded explainable Saga ranking and reversible read-time dormancy.

**Architecture:** A pure Core policy consumes exact-version metadata and bounded derived evidence. The existing finalizer atomically publishes compact use receipts; existing validated evaluation reports remain canonical. The managed Saga selection path and inspection share the same reader and policy.

**Tech Stack:** .NET 10, raw parameterized SQLite, source-generated JSON, xUnit.

**Spec:** ../specs/2026-10-19-saga-ranking.md

## Global constraints

Preserve default-off parity, Native AOT, existing immutable history, protection partitions and owner cleanup. No public score tuning, cross-store mutation, heuristic deletion or plaintext evidence.

## Review focus

Exact current content/version and owner changes must revoke evidence. Report deletion must revoke credit immediately. Interrupted/forked/imported/retried branches must never create false success. Claimless legacy stores and historical schema fixtures must remain readable. Protected explanation must expose no text or protected identity.

### Task 1: Pure policy

Files: Core/LongRest/SagaRankingPolicy.cs; tests/LongRest/SagaRankingPolicyTests.cs.

- [x] Write behavioral RED tests for exposure-only zero credit, bounds, origin and pin exemptions, dormancy/validity boundaries, future and malformed evidence, default-off parity.
- [x] Implement SagaRankingPolicy.Evaluate(similarity,snapshot,asOfUtc,enabled), SagaRankingEvidence, SagaRankingSnapshot and SagaRankingComponents with the exact v1 constants in the spec.
- [x] Verify focused GREEN.

### Task 2: Committed-use evidence

Files: Core/Storage/IGrimoireTurnCommitter.cs; Core/Weave/SagaUseCoordinate.cs; Api/Intelligence/GrimoireTurnWriter.cs; Infrastructure/Repositories/GrimoireRepository.TurnCommit*; declarative schema v20; lifecycle owners; real-transaction tests.

- [x] Write RED tests for native success, rollback, replay, stale/erased source, interruption and transferred turns.
- [x] Add SagaUseCoordinate(MemoryId,ClaimId,VersionId,HashFormat,ContentDigest) and optional TurnCommitRequest.SagaUses. Writer replaces one handle's final admitted set. Persist saga_committed_uses only in the existing successful native finalization transaction.
- [x] Add declarative evolution and exact lifecycle cleanup; verify GREEN including fresh/evolved catalog equality.

### Task 3: Shared retrieval and inspection

Files: Infrastructure/Data/LongRest/SagaRankingReader.cs; Infrastructure/Weave/DivinationService.cs; Infrastructure/Data/SagaMemoryStore*; Core/Weave/SagaMemory*.cs; Api/Intelligence/WizardIntelligenceProvider.cs; Memory endpoints/DTOs/CLI; tests and owning docs.

- [x] Write RED integration tests for age/dormancy, pin/operator exemptions, score before K, raw threshold, gate-off parity, exact report attribution, content-safe explanation and successful final ledger-coordinate plumbing.
- [x] Read bounded validated evidence on the same snapshot; preserve raw cosine and stream scored candidates into the top-K heap. Share metadata evaluation with hydration and inspection.
- [x] Expose content-free policy/components/evidence via memory explain and detail/search/status as appropriate, with JSON registrations and owning docs.
- [x] Verify GREEN; review all changed code independently and fix confirmed defects with regression tests.

### Task 4: Delivery

- [ ] Commit the reviewed candidate after local verification, then run exact-SHA CI gates, including coverage/source analysis/Windows/macOS AOT. Fix failures and requalify changed candidate.
- [ ] Fast-forward memory-enhancement to the qualified candidate, prove remote SHA/tree equality and close #95 completed. Keep #75 open until inherited epic obligations are independently satisfied.
