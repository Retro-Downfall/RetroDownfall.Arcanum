# Task 7 slice 3 report

## Scope and base

- Reviewed base: `511a17498224c1c634b204022b751d6317da2ba1`.
- Scope: startup-only completion of the authenticated terminal operation-row suffix, the real V4/V2 two-host crash matrix, retained nonterminal-finalizer restart, strict refusal/nonfallback coverage, and Closed-anchor physical recovery.
- Explicitly out of scope: Task 8, Ollama, Git delivery, issue closure, push, merge, and primary-checkout cleanup.

## Rulings applied

1. Canonical, Retiring, and absent `Closed` anchor states stay owned by `RecoverClosedAsync` before terminal classification. They neither enter the terminal finisher nor request disclosure-writer restoration.
2. A real terminal suffix reports `Resumed`, defers writer restoration to the existing post-schema/pre-readiness bootstrap boundary, and does not burn a process-local Grimoire admission generation.
3. Terminal proof requires rollback to remain at `First` with no in-flight phase, canonical raw operation storage, byte-canonical V4/V2 checkpoint payload, exact catalog tuple, and a fresh-process lane/disposition postcondition.
4. Marker capture remains before recovery-only database open, but a captured marker failure is consulted only after the terminal classifier returns `Nonterminal`; an exact terminal suffix does not depend on provisional host-tools authority.
5. A parent receipt already recorded as satisfied is verify-only. It must reread the exact completed parent winner before any later suffix mutation and may not republish the receipt.
6. The retained-finalizer barrier, not observations after readiness, owns the zero-replay claim. At the barrier verifier/publisher/effects/mutations remain absent. After release, the required deferred writer restoration legitimately verifies and publishes exactly once; the test separately pins dispatch, terminal transition, retirement, generation, and readiness.
7. Current V4/V2 owner-bound offline-transition checkpoints cannot be rewritten to `Cancelling`. Their authenticated journal/coordinator is their only lifecycle owner. Ordinary operations and legacy retention checkpoint versions remain cancellable.
8. Defense-in-depth predicates entailed by the exact SQL operation-id lookup, canonical decoder, and `ClosingOwner` are tested/mutated as semantic categories rather than by deleting every duplicate predicate independently.

## TDD chronology

1. The first real two-host V4/V2 terminal-boundary theory was RED at startup: the existing recovery path rejected terminal rows and could not complete the suffix. The production finisher was added as a tri-state branch after recovery-only SQLite open and before host-tools classification, authority Load/Consume, adoption, dispatch, coordinator, or effect work.
2. The production finisher initially admitted only the exact authenticated lifecycle publication and a strict 24-column terminal row. The matrix then grew one durable boundary at a time: database terminalized, winner recorded, parent/no-parent recorded, lane closed, disposition in-flight, disposition verified, RetirementPending, and retired Closed anchor.
3. V4 direct reset and V2 standalone factory reset now each restart from all eight boundaries. At the pre-retirement barrier readiness and the final hosted sentinel remain dark; authority Load/Consume, adoption, dispatch, operation CAS, provider, worker, ordinary mutation, and effect counts remain zero. Release advances only the missing suffix, preserves the complete terminal row, retires the journal, restores the writer after schema, and admits request/work/open on the fresh host's initial generation.
4. A separate V4/V2 theory proved the terminal rollback arm: exact `Failed`, `grimoire.offline_transition_not_applied`, `First`, null in-flight, source catalog, and unchanged row. `Abandoned`, crossed state/code, non-final phase, and in-flight terminal shapes refuse.
5. The initial negative matrix caught valid JSON payload whitespace reaching readiness. Canonical V4/V2 decode plus byte-for-byte re-encode comparison fixed it. Removing that comparison makes the payload-whitespace row RED with `unexpectedly reached readiness`; restoration is green.
6. Raw catalog tests caught permissive epoch coercion and a false rejection of a legitimate target epoch at `long.MaxValue`. The terminal observed-state reader now requires raw SQLite INTEGER epochs, permits a saturated already-written target, and retains the launch-time source reader's successor requirement.
7. Parent audit caught later states trusting only `BindingDigest`. The finisher now calls `VerifyCompletedAsync` for every already-satisfied bound-parent state before any journal mutation. Missing, claimed, foreign-winner, and wrong-binding proofs refuse with zero `PublishAndRereadAsync` calls. Skipping the proof made all five focused parent tests RED; restoration is green. A real `FinishAsync` test over a bound authenticated publication, strict terminal row/catalog, fresh gates, scoped resolver, and real journal proves one verify-only winner read, zero publication, remaining suffix advancement, and typed retirement/absence.
8. Fresh-gate proof initially occurred only after some winner/parent writes and was skipped from already-verified/RetirementPending states. It now runs before every terminal publication and is repeated at the lane/disposition edges. No stale process-local owner, readiness, registration, or generation can be presented as a fresh-process postcondition.
9. Strict row equality initially normalized GUID spelling. Raw canonical `N`/`D` text comparison plus exact UTC text parsing now prevents case-only or timestamp spelling drift. The terminal row is reread immediately before retirement and compared with byte-wise checkpoint payload equality.
10. Attempt-count and chronology tests were RED because attempt zero and invalid completed chronology reached retirement. Exact terminal/nonterminal candidates now require attempt count at least one, required started/heartbeat instants, and monotonic created/started/heartbeat/completed ordering. The controlled happy path separately pins `AttemptCount == 1`.
11. A `ReconciliationRequired` row with non-null `CompletedAt` initially fell through to slice-2 authority, as did a row with null `CompletedAt` and terminal-only error code. Focused REDs observed one authority Load. The explicit nonterminal contract now admits only Pending/Running/Waiting/Cancelling with a cleared code, or lease-free ReconciliationRequired with one of the three exact recoverable attention codes. Active-state terminal-code and attention-state terminal-code mutations both refuse with zero fallback.
12. That invariant exposed a real adjacent operation-store gap: generic `RequestCancellationAsync` could move an owner-bound attention row to `Cancelling` while preserving its attention code. The focused current V4 row returned true and rewrote the row. Request cancellation now excludes only the current owner-bound V4/V2 checkpoint windows. Removing the exclusion makes both rows RED; restored V4/V2 rows are byte/field stable, while legacy same-kind and ordinary operations remain cancellable.
13. The startup seam test that returns a synthetic terminal outcome was renamed and documented as tri-state routing only. `Exact_terminal_candidate_takes_the_terminal_route_before_authority_load` is the real encrypted-catalog production-finisher proof: it uses a real exact terminal row/journal and proves terminal completion before authority Load or dispatch.
14. Closed-anchor failure coverage now distinguishes canonical (`anchor:closed-written`, `anchor:closed-readback`) from `.retiring` (`file:retiring-moved`) topology. The second host fails before readiness with one terminal-finisher invocation; the third host invokes the terminal finisher zero times, performs no authority/adoption/dispatch/writer restore, removes both paths, and reaches readiness. An already-retired/absent startup also invokes the finisher zero times, and repeated startup over the persistent Closed tombstone does not report `Resumed` again.
15. Retained nonterminal finalizer attention still follows slice-2 authenticated Load/Consume/adoption/dispatch. Before release it has no verifier/publisher/effect/ordinary mutation. After release it records exactly one terminal transition, one writer restoration with exact candidate publication, and journal retirement before readiness.

## Exact evidence and mutation record

- The terminal winner is recomputed in the full-host test from an independent test-only SHA-256 preimage: domain, separator, launch-binding digest, big-endian operation UUID, state byte, big-endian code length plus UTF-8 code, and big-endian revision. Every winner-bearing first-host and recovered publication must contain those independent bytes. A separate fixed protocol vector expects `b6580a4f8e2adaaa630769b4dd12c846208e25ee4a093f12b45f7fa611a7afdf`.
- Every first-host terminal publication is checked for exact state/step, operation and installation identity, contiguous envelope revision, winner presence, standalone no-parent fields, lane bit, and disposition intent. Every second-host publication is checked against the exact missing suffix and next envelope revision.
- Removing checkpoint canonical re-encode makes the whitespace payload negative RED.
- Broadening the nonterminal fallback makes the terminal-code contradiction RED at authority Load count 1.
- Skipping completed-parent reproof makes the four negative rows plus positive verify-call row RED.
- Removing the V4/V2 cancellation exclusion makes both current-owner rows RED while legacy controls define the narrow scope.
- The original implementation absence made the real terminal theory RED before readiness; restored production completes all boundary rows with no fallback calls.
- Mid-suffix injected failures retain exactly the successful prefix and no later edge. Retirement-boundary row/catalog drift is seeded at RetirementPending and leaves the row/journal unchanged rather than demanding an impossible rewind of earlier durable prefix steps.

## Verification snapshot

- Restored focused terminal cluster: 48/48 passed in 1m28s (real 16-boundary matrix, V4/V2 rollback, refusal matrix, exact-route, retained-finalizer, Closed topology, parent proof, fixed vector).
- Terminal refusal matrix plus existing V4/V2 nonterminal two-host recovery: 20/20 passed in 39s, confirming the explicit nonterminal chronology remains compatible with slice 2.
- Exact-route, bound-parent real finisher/phase suffix, and current/legacy cancellation selection: 8/8 passed.
- Focused exact-route, retained-finalizer, Closed topology, and independent vector: 6/6 passed.

- Required exact V4/V2 terminal-boundary theory with single-node/no-build flags: 16/16 passed in 36s.
- Restored negative/finalizer/Closed/parent/evidence cluster: 34/34 passed in 50s.
- Full Task 7 startup/authentication authority filter plus new suffix, phase-session, and cancellation suites: 370/370 passed in 1m33s.
- Fresh single-node Release test-project build: succeeded, 0 warnings and 0 errors in 0.70s.
- Final single-node Release solution build: succeeded, 0 warnings and 0 errors in 11.33s.
- Final `git diff --check`: clean.

## Task 8 documentation carry-forward

The architecture rule is documented here, but Task 8 must also update the owning user-facing API/CLI
operation-cancellation text: `operation cancel` intentionally returns the existing compare-and-swap
refusal for current owner-bound V4/V2 offline-transition checkpoints because only their authenticated
journal/coordinator may move them. This is not a new verb or response shape, but it is a newly explicit
restriction on an existing command and should not remain architecture-only.

## Self-review

- Terminal classification cannot fall through on malformed terminal-looking evidence.
- The finisher owns no operation store, adopter, handler, coordinator, old Covenant lease, or ordinary connection factory.
- The full operation row remains unchanged; the only writes are typed monotonic lifecycle publications and typed retirement.
- Parent lookup occurs only for a non-null committed binding, and later parent states are verify-only.
- Every pre-retirement terminal state proves a fresh process before mutation; no process-local generation is synthesized.
- Closed physical cleanup remains separate and idempotent.
- No unrelated recovery kind, generic settlement, or legacy cancellation behavior was broadened.
