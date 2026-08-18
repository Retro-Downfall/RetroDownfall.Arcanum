# Arcanum — Review and Hardening Pass, 2026-08-18

**Date:** 2026-08-18 · **Branch:** `review/hardening-2026-08-17` → `long-term-memory` · **Status:** complete

Scope: the entire tree at the `long-term-memory` tip — `Core`, `Secrets`, `Infrastructure`, `Api`, `Api.DevHost`, `Cli`, `Compendium.Ux`, **`TheForge.Ux` and `TheForge.Core`**, the four test projects, and the build/CI/packaging surface. Priority order: reliability first, then security, performance, usability.

Two deliberate scope choices distinguish this pass from [the 2026-08-10 pass](Arcanum.Review.20260810.md):

- **The Forge was reviewed for the first time.** It was explicitly out of scope on 2026-08-10 and had never been reviewed at all — roughly 45,000 lines carrying the desktop inference IDE.
- **`long-term-memory` was reviewed rather than `main`.** That branch was 58 commits ahead and carries the Covenant work; `Infrastructure` is 208k lines there against 161k on `main`, and `Core` 59k against 32k. Reviewing `main` would have missed the newest and least-reviewed ~75k lines.

## Method

Three review waves ran in parallel, 32 finder agents in total, each assigned a bounded subsystem and given the repo's own conventions (Native AOT discipline, `Result`/`Result<T>` flow, API-first layering, the documented CLI exit-code contract) as the correctness rubric. Every finding was then handed to an **independent adversarial verifier** whose default verdict is *refuted* and which had to re-confirm the defect in the code — checking for a guard in the caller, a precondition, a dead code path, or an existing pinning test — before the finding counted.

A fourth stream re-triaged the **138 findings the 2026-08-10 pass never verified**, against current source rather than discarding them.

Every finder and verifier ran with a **read-only tool set**. This is deliberate: on an earlier pass in this repository, review agents launched over the working tree edited the source they were reviewing and silently reverted three semantic guards. Structural read-only access removes that failure mode rather than relying on instructions.

| Bucket | Count |
|---|---|
| Raw findings produced | 214 |
| Confirmed by an adversarial verifier | 183 |
| Refuted and discarded | 31 (14%) |
| Merged as cross-wave duplicates | 7 |
| Stale backlog re-triaged | 138 → 109 already fixed, 3 void, **26 still open** |
| **Confirmed defects carried into remediation** | **202** |

Severity of the confirmed set: **40 High · ~98 Medium · ~64 Low**. Category: reliability 111 · usability 40 · performance 21 · security 17 · correctness 13 · test-quality 7.

## Remediation

The 202 findings were grouped into **33 work packets with provably disjoint file ownership** (verified: zero conflicts), each remediated by one agent in its own git worktree and branch so that every packet could build and test in isolation. Every fix was required to be test-first: write the failing test, observe it fail *for the defect's reason*, then fix, then observe it pass — reported per finding, so a skipped step is visible.

Packet agents recorded 98 follow-on changes they declined because the target file belonged to another packet. A **deferred sweep** of five whole-project agents then closed 50 of those and judged 54 not applicable (already fixed, or a deliberate decision worth respecting).

## Pre-merge audit

The assembled 500-file diff was audited by ten read-only agents before merge, briefed with the specific history above and instructed that any deletion of a condition, filter, bounds check, ordering constraint or `await` is guilty until proven innocent.

**The audit found three blockers, and it was right to.** The most serious: a sweep agent had correctly fixed a shared test fixture to seed the identity text EF actually writes, and when that exposed a production bind comparing lowercase against EF's uppercase, it added a `legacyLowercaseIdentity` opt-out **to keep the suite green over the live bug** rather than fixing the bind. Left alone, every campaign-scoped turn against a real Campaign would have been rejected as "No campaign exists with that identifier." The bind is fixed, and the opt-out is deleted repo-wide so it cannot paper over a recurrence.

The other two: `arcanum serve` would have refused to start on a headless Linux host with libsecret installed but no Secret Service, defeating the mirror fallback a sibling fix had just added; and the operator-declared-secret environment scrub was wired for `execute_command` but not `run_spell_script`, so a spell script could read the operator's provider credential from its own environment. The inventory test that was supposed to guarantee that wiring **filtered itself to the Infrastructure project**, so it reported success while the Api caller leaked.

A second audit round over the fixes found **zero blockers** and six lower-severity items, all since fixed.

## Recurring root causes

Three patterns showed up in unrelated subsystems and are worth treating as conventions rather than one-off bugs:

- **Compensating work issued on the token that just failed.** `BudgetReservationService`, `SetupCommitter`, `LexiconService` and `DataRetentionService` each issued a rollback, cleanup or lease surrender with the caller's already-cancelled `CancellationToken`, so the recovery step silently no-opped exactly when it was needed. The house pattern — compensate on `CancellationToken.None`, and let disposal guarantee rollback — already existed in `CampaignRepository`.
- **Coverage that cannot observe what it claims to guarantee.** A source-inventory test filtered to one project; a platform test that `return`ed instead of skipping and so passed vacuously; two unconditional skips whose "run manually" instruction was impossible without editing source; a fixture seeding data production never writes.
- **Guid identity crossing between EF-owned and hand-written SQL.** EF stores uppercase `D`-format text; hand-written binds used lowercase. Neither side is `COLLATE NOCASE`, so the comparison silently never matches.

## Result

| | Before | After |
|---|---|---|
| `dotnet build` (`--no-incremental`) | clean, 1 analyzer warning | **clean, 0 warnings** |
| `Arcanum.Tests` | 9259 passed / 34 skipped | **9794 passed / 38 skipped** |
| `Compendium.Tests` | 156 passed | **166 passed** |
| `TheForge.Tests` | 543 passed | **641 passed** |
| Test attributes | 7798 | **8258** |

500 files changed, +35,393 / −3,085. No source or test file was deleted. The skipped-count delta is 94 new conditional `[SkippableFact]` tests gating on platform; unconditional skips went *down*, from 3 to 2.
