# Session compaction outcomes — RV-092 (2026-10-26)

Review source: docs/reviews/2026-10-24-repository-code-review.md, RV-092. Base50822628834715d7d01b1b81419cbb3ff116d58f, runtime1365e859978a889fe13db99caec091db7eccc30c. Isolated codex/review-maintenance-request-outcomes branch. RV090 and RV101 have separate preflight notes and are not implemented in this change.

## Accepted behavior

Preserve actual token measurements when a loaded context has too few messages to compact. Add nullable source-generated CompactionOutcome to CompactResult: Compacted, NotNeeded, TooFewMessages, OnlyPinnedRemain, Stopped. Outcome distinguishes ordinary no-ops from a protected erasure refusal; StoppedBy remains the typed refusal code. OnlyPinnedRemain can follow actual removals. Counts describe the existing repository-loaded context window, not a new whole-history census. Missing session route behavior stays404; direct missing repository result remains compatible.

The CLI and Forge share fixed guidance from Core. CLI normal/plain output explains no-op reasons; JSON retains the typed outcome and measured counts. Older hosts may omit the field, preserving their counts-only display. Keep zero success exit code for partial compaction. Preserve pinned entries, complete tool groups, cancellation behavior, purge dispositions and actual removed-entry accounting.

The review's disabled-compression premise is unreachable: both original45d12bca and current ResolveIntelligence explicitly set EnableContextCompression=true. Remove the dead service check; do not invent a setting or fake a reflection-only supported path. Existing Features.MemoryManagement route admission remains unchanged.

## Task 1 — Behavioral RED

Add service tests for short context, below threshold, all pinned, and removals leaving pinned context; CLI normal/plain/JSON coverage and Forge display coverage. Original production produced16behavioral failures with4passing controls. A preceding CLI test raw-string compile error is fixture setup only and is excluded. Preserve logs/TRXs under /private/tmp/arcanum-rv092-red-*.

## Task 2 — Minimal implementation and consumers

Measure tokens before the minimum-message return, return typed observed outcomes, add the generic string enum converter and source-generation entries, and use shared fixed guidance in both clients. Add actual authenticated API response and protected-stop/ordinary-removal controls. Preserve all lifecycle/authority behavior. Owning docs: API, Command Reference, DESIGN; no schema/config/catalog/source-path additions.

## Task 3 — Causal verification

Restoring the original service must fail the new behavior while existing controls remain. Returning0tokens for the short-context outcome must fail both service and HTTP tests. Suppressing shared guidance must fail both CLI and Forge while legacy and JSON controls pass. Restore exact candidate bytes after each. Preserve receipts; no re-running causal mutations for subsequent formatting-only changes.

## Task 4 — Qualification and review

Normal solution build with analyzers, complete affected service/API/CLI/Forge/serialization classes and strict boundary/doc guards; require real nonempty TRX with all results passed, zero skips, zero build warnings. Both house modes, Roslyn whitespace, diff checks, exact source hashes and committed tree. Independent review before local integration. No main merge, push, install or global delivery claim.
