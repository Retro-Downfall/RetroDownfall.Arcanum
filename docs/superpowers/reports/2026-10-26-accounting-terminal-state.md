# RV113 accounting terminal state implementation report

Source baseline: `8bce182263255238450dd17b31f91b4bbbc77d30`; implementation branch: `codex/review-accounting-terminal-state`. The plan is `docs/superpowers/plans/2026-10-26-accounting-terminal-state.md`. This tracked report describes the implementation and causal proof before candidate freeze. The separate read-only terminal report/manifest binds final qualification to the exact committed SHA/tree; no integrated credit is claimed here.

RV113 now attempts reservation and run dispositions independently under the original completion semaphore and caller token. Only successful steps are remembered. The first frozen status/cost and immediate IsSettled ambient cutoff remain intact. A lone original exception is rethrown unchanged. Two faults retain both original objects, reservation first/run second. Canceled-task semantics apply to two faults only when the supplied token is canceled and at least one step actually threw OperationCanceledException; the supplied token and both original faults remain available. No exception-text parsing establishes cancellation.

Standalone Weave accounting cleanup emits a fixed Warning containing actual RunId, ReservationId and exception type. It retains no exception object or private message and preserves already-produced vectors and scope disposal. Provider diagnosis and other RV118 work remain separately open; this change gives no RV118 completion credit.

The separately authorized adjacent correction is BeginAsync failure masking: a failed run cleanup no longer replaces the original reservation-open exception. Cleanup still attempts Failed with CancellationToken.None. Its own four behavioral RED cases and unchanged-cleanup control are distinct from RV113.

Existing orphan recovery was inspected: SubagentRecoveryHandler and MemoryEvaluationRecoveryHandler terminalize known operation-owned runs through TryAbandonRunAsync; there is no generic production caller of reservation SweepExpiredAsync and no broad standalone-run expiry sweep. No new sweep, schema, recovery authority, paid replay or startup behavior was added. DataRetentionService.Pruning independently checks outstanding Reserved/UncertainPaid rows; a terminal run does not make that chain prunable.

| Terminal receipt | Passed | Failed | Meaning |
|---|---:|---:|---|
| behavioral-red.trx | 108 | 13 | Real run-skipping, dual-fault/cancellation, SQLCipher reconcile/release and embedding cleanup regressions. Five scoped controls plus 103 incidental matches from the initial broad method substring passed. |
| focused-green.trx | 18 | 0 | Class-qualified original regressions and controls after minimal implementation. |
| complete-direct-green.trx | 422 | 0 | Four complete directly affected classes before final symmetric/adjacent additions. |
| symmetric-cancellation-green.trx | 7 | 0 | OCE from either step, foreign token, lone OCE and canceled gate controls. |
| adjacent-begin-red.trx | 1 | 4 | Separate BeginAsync masking seam. |
| adjacent-and-completion-green.trx | 25 | 0 | Combined RV113 and adjacent behavior. |
| formatted-focused-green.trx | 25 | 0 | Passing formatted mutation baseline. |

Seven distinct behavioral mutants were killed, restoring all eight then-owned source hashes exactly after every run:

| Mutation | Passed | Failed |
|---|---:|---:|
| skip-run-after-reservation-fault | 1 | 4 |
| repeat-successful-dispositions | 1 | 4 |
| second-fault-replaces-first | 3 | 8 |
| ignore-caller-cancellation | 8 | 3 |
| token-alone-invents-cancellation | 9 | 2 |
| completion-warning-hidden-at-debug | 0 | 3 |
| retain-private-completion-exception | 0 | 3 |

An initial xUnit2013 assertion error and an initial compile-only retry mutant (CS0649) were corrected with normal analyzers enabled and retained as non-credit evidence. They are not behavioral RED or killed-mutant receipts. Exact commands, terminal TRX, complete failures and before/mutated/restored SHA256 values are retained in the immutable evidence package.

Final gate boundary: normal solution build with zero warnings/errors; complete affected accounting, Weave, retention, reservation, batch, subagent and genuine Wizard-wrapper consumer classes; recovery and strict constructor/AOT/acquisition/documentation guards; both owned house-style modes, Roslyn whitespace and committed diff checks. These run after the commit freezes. Final-stage plan boxes remain pending in frozen source; the immutable terminal report records actual outcomes without changing qualified bytes. No global hosted discovery, whole-repository test suite, live provider/Ollama, installs, persistent configuration, Core28/schema changes, main mutation or push is authorized or claimed.
