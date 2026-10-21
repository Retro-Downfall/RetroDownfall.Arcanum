# Repository review remediation implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox syntax; each repair requires observed behavioral RED and GREEN and an independent review.

**Goal:** Repair every validated finding from the broad review, close the discovered #73 restore gaps, and deliver only an exact candidate that passes full CI and fresh-install Ollama qualification.

**Architecture:** Reuse existing authority, quota, path, Unicode, Result and persistence mechanisms. Preserve atomic import graphs and truthful postcommit evidence, bind spell I/O to existing permitted roots, and fix validation/editor state at their owning boundaries. The frozen #229 candidate remains isolated while its CI runs.

**Tech Stack:** .NET 10, source-generated JSON, raw SQLite/SQLCipher, Native AOT, Avalonia, xUnit, GitHub Actions.

**Spec:** `docs/reviews/2026-10-21-repository-code-review.md`, with current owning DESIGN/API/Command Reference/Compendium/OATH contracts.

## Global Constraints

- Preserve unrelated checkouts and never reset, stash, clean or broadly stage their work.
- Native AOT throughout; no reflection JSON, anonymous wire DTOs or new unverified vector/native dependency.
- All application reads and set-based writes use parameterized raw SQLite; do not weaken schema/authority/capacity guards.
- Keep imported immutable history, sensitivity and source evidence exact; do not label committed work rejected.
- Use existing thematic concepts and owning documentation; root README is unchanged unless public content intentionally changes.
- Observe RED before new production edits. Existing-fix mutation evidence is labeled honestly.
- Each task owns its files; serialize tasks sharing importer, spell or schema/catalog surfaces. Coordinator owns shared docs and source-analysis catalog integration.
- Full CI is required for the final changed tree: all 14 jobs, exact source authority, coverage, all shipping RIDs, native/provider gates. User authorized merge/push/closure; do not infer green from partial jobs.

## Review Focus

- Failures after a transaction commits must preserve receipt and recovery ownership.
- Read/write parent-link replacement must fail without external bytes read or written, including cache hits.
- Invariant-globalization behavior must be exercised through the actual restricted runtime, not normal unit-test assumptions.
- Pagination must remain bounded while filters cannot hide eligible later work.
- Validation and async editing races must retain the user-visible contract and cancellation behavior.

## Phase 1 — Restore integrity and #73 completion

### Task 1: Protected import finalization, commit evidence, and attachment identity (R1–R3, R11)

**Files:** `Infrastructure/Repositories/ProtectedArtifactTransferStore.cs`, `Infrastructure/Backup/BackupSessionImporter.cs`, associated Core transfer receipt/completion contracts only if needed, `Infrastructure/Backup/BackupRestoreService.cs`; tests in `Repositories/ProtectedArtifactTransferStoreTests.cs`, `Backup/BackupSessionImporterTests.cs`, relevant restore tests.

**Interfaces:** Keep `CommitImportedSessionAsync` and `ImportProtectedAsync` as owning boundaries; use existing `CovenantQuotaGuard.AllocateDirectFinalizationAsync` with imported origin inside the graph transaction. Preserve committed receipt independently of later lease-disposition success. Plain entry copy produces a source-to-destination identity map consumed by attachment copy.

- [x] Reproduce an ordinary protected archive containing a valid finalized assistant; assert import succeeds, exact imported consumed capacity exists, guard is non-replayable and scope reopens. Observe RED before allocation repair.
- [x] Inject failure advancing the journal to ReopenPending after graph commit; assert committed destination Session evidence and reconciliation-required outcome. Cover disposition failure and earlier successful imports followed by later failure/cancellation. Observe RED, then preserve truthful receipts through all boundaries.
- [x] Import a bound attachment, assert destination EntryId equals the corresponding newly copied Entry, and fork through that Entry to prove attachment/payload survives. Cover null source association. Observe RED, then implement map.
- [x] Reproduce a genuine encrypted protected attachment import; journal raw ciphertext identity and authenticate the bounded plaintext against original metadata before graph publication. Preserve envelopes/key access and refuse tampered payloads.
- [x] Run focused importer/transfer/restore tests, verify rollback/quota refusal behavior and inspect diff. Independent review before completion.

## Phase 2 — Filesystem and archive security

### Task 2: Spell authoring and hydration containment (R4–R5)

**Files:** `Infrastructure/Intelligence/Spells/SpellRepository.cs`, `Infrastructure/Workspaces/SpellScanner.cs`, existing spell metadata/root/read abstractions and their API/Wizard callers as required; tests `Repositories/SpellRepositoryTests.cs`, `Workspace/SpellScannerTests.cs` and routed/explicit spell tests.

**Interfaces:** Existing admitted workspace/global spell roots remain the authority. Carry root and appropriate discovery identity to hydration; use existing secure read/containment facilities and identity-owned staging. Do not introduce ambient authority.

- [x] Parameterize create/clone/import under an escaping `spells` directory link; assert typed refusal and no external output. Add deterministic parent replacement at the existing before-first-write seam. Observe RED then enforce containment through publication/cleanup.
- [x] Discover metadata, pivot parent to external regular body/sidecar, hydrate cold and warm cache; assert refusal and no provider disclosure. Observe RED, then propagate/revalidate source root and identity before reads/cache returns.
- [x] Include export source-root/handle admission, nested workspace metadata, absent sidecar tags and actual rename-boundary replacement regressions.
- [x] Check all affected authoring and hydration entry points, allowed legitimate roots, cancellation and cleanup. Run focused suites; independent review.

### Task 3: Invariant archive identity (R6)

**Files:** `Infrastructure/Backup/BackupInventoryPlanner.cs`, `BackupArchiveCodec.cs`, `BackupArchivePathFolding.cs` if needed; tests for backup inventory/codec and `Build/PathKeyGlobalizationTests.cs`.

**Interfaces:** Replace runtime-dependent protocol NFC with existing `UnicodeNfc`; authenticate exact archived names and reject invalid/colliding identities before materializing output.

- [x] Run invariant-runtime/native reproduction for decomposed `cafe\u0301` and a composed/decomposed pair holding different payloads. Assert canonical creation and collision refusal with no overwritten output. Observe RED.
- [x] Apply invariant-independent normalization at canonical creation/validation boundaries, retain strict authenticated-name semantics and add source guard coverage.
- [x] Run focused backup tests plus real invariant/native probe; independent review.

## Phase 3 — Recovery, request validation and editor reliability

### Task 4: Bounded recovery phase discovery (R7)

**Files:** `Infrastructure/Operations/LongRunningOperationReconciler.cs`, classified discovery/store contracts and SQL implementation, `Operations/LongRunningOperationReconcilerTests.cs` and store tests.

- [x] Seed older ordinary operation and later BackupCreate with page size one; assert both recovered with documented phase precedence. Observe RED.
- [x] Apply phase/kind predicate before LIMIT through classified discovery; retain bounded pages and exact authority. Add progress/concurrency/empty-phase cases that exercise real store behavior.
- [x] Run focused operation suites; independent review.

### Task 5: Null OpenAI chat messages (R8)

**Files:** `Api/OpenAiChatCompletionRequestValidator.cs`; tests `Api/OpenAiV1EndpointTests.cs` and `Api/OpenAiV1BatchesEndpointTests.cs`.

- [x] Raw JSON `[null]` and mixed valid/null message arrays must produce HTTP 400 OpenAI invalid-request envelopes and zero provider calls. Batch execution produces ordinary per-line failure. Observe RED.
- [x] Guard the shared message loop before dereference, return typed error naming the message index, run focused live/batch validation suites; independent review.

### Task 6: Concurrent editor saves (R9)

**Files:** `Compendium.Ux/ViewModels/ConfigurationViewModel.cs`; tests `Compendium/FailClosedEditorTests.cs`, `SaveCommandCanExecuteTests.cs` or a focused new file.

- [x] Hold WriteAsync on a TaskCompletionSource; save revision A, edit revision B, complete A, assert B stays dirty/saveable and close still prompts. Verify written snapshot is A. Observe RED.
- [x] Track edit revision through the save snapshot and clear dirty only for matching revision; failed/canceled save remains dirty. Run focused Compendium suite; independent review.

### Task 7: Forge long-operation deadlines (R10)

**Files:** `TheForge.Ux/ServiceCollectionConfigurator.cs`, `Services/ArcanumApiClient.cs`, `Services/OpenAiCompatApiClient.cs`, long-operation route services, and corresponding Forge client/DI tests.

- [x] Reproduce delayed compaction headers or multipart upload cancellation caused solely by the production finite client timeout; caller cancellation must remain distinguishable. Observe behavioral RED, using a controlled deadline clock/seam if needed rather than a permanent 100-second test.
- [x] Separate short-call response-headers deadlines from operations with no product-owned expected duration. Explicitly classify compaction, inference/streams, file transfer, tool work and other exposed long operations; use caller cancellation for them. Preserve bounded ordinary calls and error mapping.
- [x] Run Forge transport/route/DI suites and independent review; document the actual timeout policy.

### Task 8: Restore key-cache coherence (R12, found during qualification)

**Files:** `Infrastructure/Security/FileEncryptionKeyProvider.cs`, `Infrastructure/Backup/BackupSecretRewrapper.cs`, restore options/composition, encrypted import and key-provider tests.

- [x] Warm the real destination provider, merge a new archive key through the real rewrapper, and observe protected import fail to authenticate.
- [x] Invalidate the live provider cache after attempted restore key mutations, including rollback, while retaining already-borrowed material until provider disposal. Preserve the existing active write key during selective merge.
- [x] Prove immediate import and subsequent same-provider reads, replacement/rollback visibility, production DI propagation and borrowed-key safety; independent review.

## Phase 4 — Integration and delivery

- [x] Integrate task commits, reconcile shared docs/source-analysis catalogs without weakening any gate, and update the #73 acceptance record to include the repaired restore regressions.
- [x] Run independent whole-branch review and fix all validated findings using regressions.
- [ ] Freeze final SHA/tree, run full CI, inspect complete partition authority, coverage, native/provider artifacts and all terminal jobs.
- [ ] Fast-forward memory-enhancement and main, push and prove live ref/tree equality. Close #229 and #73 only when their evidence is complete.
- [ ] Run a newly published exact-candidate Native AOT binary against a fresh isolated local Arcanum install with Ollama gemma4:e4b; retain success evidence.
