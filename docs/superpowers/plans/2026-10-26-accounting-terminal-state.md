# Accounting terminal state implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. The controller explicitly assigned inline implementation without children and independent review afterward.

**Goal:** Complete RV113 by attempting run disposition after reservation failure, preserving independent retries, frozen accounting and cancellation, and making standalone embedding cleanup failures safely visible.

**Architecture:** Keep TurnAccountingHandle's completion semaphore, first frozen snapshot, ambient cutoff and per-disposition success flags. Capture failures independently; rethrow a lone original unchanged, aggregate two ordinary failures in reservation/run order, and retain canceled-task semantics for two faults only when the supplied token is canceled and a disposition actually threw OperationCanceledException. WeaveService logs a fixed Warning with actual run/reservation IDs and exception type while retaining its produced result and scope disposal.

**Tech Stack:** .NET10, C#, xUnit, SQLCipher, existing Microsoft logging.

**Spec:** Controller assignment for RV113; original register T5.1 RV113 in `2026-10-24-codebase-review-remediation.md`, original A/U03 reliability finding5. RV118 provider diagnostics remain separately open.

## Global constraints

- Exact baseline `8bce182263255238450dd17b31f91b4bbbc77d30`; branch `codex/review-accounting-terminal-state` in the assigned campaign-rollups worktree.
- Preserve prior candidate branches and sealed review packages. No main, push, children, install, live actions, Core28, schema, authority or new background repair mechanism.
- `IsSettled` means snapshot frozen, including failed completion; it must continue cutting off new ambient billed work.
- Keep the supplied token for both disposition attempts. Cancellation while waiting for completion ownership must have no snapshot or storage side effects.
- Outstanding Reserved/UncertainPaid reservations independently protect retention even after run terminalization. No change to pruning or orphan recovery.
- No raw exception object/message or private source/model/argument data in the new completion Warning. Owning DESIGN changes travel with code.

## Review focus

- Reconcile and Release failures must each still attempt run completion; retry repeats only the failed reservation step.
- Both disposition failures must retain both original exceptions, in order; interleaved retry cannot rewrite frozen status/cost or repeat successful work.
- Requested cancellation plus an actual OCE preserves supplied token and canceled task; unrelated OCE or canceled token with only ordinary faults must remain combined faults.
- A frozen but incompletely persisted handle must disappear from ambient lookup immediately; existing late incurred usage semantics remain unchanged.
- Standalone embedding returns already-produced vectors and disposes its scope after accounting failures, with safe correlated Warning rather than Debug/raw exception retention.

### Task1: Independent accounting dispositions

**Files:** Modify `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`; test `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnAccountingHandleTests.cs` and `tests/RetroDownfall.Arcanum.Tests/Data/InferenceAccountingStoreTests.cs`.

**Interface:** Existing `CompleteAsync(ITurnRunWriter?, IBudgetReservationService?, InferenceRunStatus, CancellationToken)` remains unchanged.

- [x] Add behavioral tests for reconcile/release failure with successful run, dual failures and staged retries, frozen status/cost/ambient behavior, gate cancellation and exact requested/unrequested cancellation distinctions.
- [x] Add SQLCipher integration controls with real reservation-update failure and real run writer, proving persisted run terminal status, outstanding reservation protection and successful later reservation retry.
- [x] Run focused tests and retain terminal behavioral RED with exact commands/logs/TRX before production edits.
- [x] Attempt both independent dispositions under the existing completion gate; mark only successful steps complete and retain all original failures as specified.
- [ ] Run complete handle/store classes and relevant accounting/retention controls.

### Task2: Safe embedding cleanup diagnosis

**Files:** Modify `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs`; test `tests/RetroDownfall.Arcanum.Tests/Intelligence/WeaveServiceTests.cs`; document in `docs/Arcanum.DESIGN.md`.

- [x] Add genuine embedding consumer tests with reservation/run/dual completion failures, asserting vectors retained, actual IDs, Warning, safe exception type, no retained exception/private data and disposal.
- [x] Observe RED, then change only the accounting completion catch to fixed structured safe Warning.
- [ ] Run complete Weave classes including provenance and ambient/cancellation/usage controls; preserve provider diagnostics outside this catch for separately open RV118.
- [x] Document frozen snapshot vs durable dispositions, retry/failure/cancellation contract, conservative reservation retention and safe cleanup diagnostics.

### Task3: Exact-candidate qualification and review package

- [ ] Apply and check both house-style modes and Roslyn formatting on all owned C# paths.
- [x] Kill distinct meaningful behavior mutants and prove exact source restoration after each.
- [ ] Freeze a clean commit, tree and source hashes; normal solution build must have zero warnings/errors.
- [ ] Run complete affected classes and named accounting/retention/recovery/constructor/AOT guards with terminal TRX and full class census; no global hosted discovery.
- [ ] Seal full-context diff, committed source archive, terminal evidence, exact commands, hashes and report for independent review. No integrated credit.

### Authorized adjacent correction: BeginAsync exception preservation

Controller explicitly included the discovered reservation-open/run-cleanup masking seam. Add a separate RED for normal, requested cancellation, foreign OCE and canceled-token-with-ordinary-fault combinations. Preserve the already-open run cleanup on CancellationToken.None, its Failed status and original reservation-open provenance. Reuse the ordered failure/cancellation rule without adding recovery, startup or authority behavior. Count this separately from RV113 and give no RV118 credit.

Tracked implementation report: `docs/superpowers/reports/2026-10-26-accounting-terminal-state.md`. Final-stage boxes intentionally remain pending at source freeze; the separate immutable terminal report binds actual final outcomes to that exact commit.
