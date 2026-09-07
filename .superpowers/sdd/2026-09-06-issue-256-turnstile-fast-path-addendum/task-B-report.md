# Task B implementation and qualification report

Implementation complete on 2026-09-07; independent review is pending.

- Exact clean task base: `8544401f27928ed8bff9ba6fcde0639bb1bc8f63`.
- Immutable instrument H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`.
- Proposed exact monitor baseline B: `18c7a2b2f779dd4a87a1d45c1a1e61fa924200ff`.
- Branch: `codex/issue-256-hosted-producer-admission`.
- Worktree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission`.
- Normal implementation commit: `fix: characterize and preserve Grimoire gate lifecycle invariants`.
- This report and the ledger update belong to a subsequent bookkeeping commit, not B.

No benchmark claim is made. H calibration is not B/C acceptance evidence. Independent review must
approve the monitor implementation before B is used for the optional Task C comparison.

## Scope and implementation

The approved Task B brief, reconnaissance, and addendum were read before implementation. The TDD
and verification-before-completion skills guided the failing-test sequence and final evidence.
No subagents were spawned.

Three existing defects were proven before their corresponding production changes:

1. Closing skipped cancellation for active effect winners and never delivered it after group
   release. Closing now records the cancellation obligation on each live work lease. Group release
   captures its source under `_sync`, completes the exact scope/group drain decision, and calls
   cancellation only after leaving `_sync`. The obligation survives a proven stage-one abort.
2. Proven abort mutated owner, phase, and closure before its checked generation increment. It now
   computes the checked next generation first. Overflow therefore leaves the exact closing owner,
   generation, closed-to-new-work admission, and dormant signal intact, matching the existing safe
   stage-two path.
3. Read-only Task C reconnaissance reported a possible ambient retention defect during Task B.
   Tests proved that four repeated non-LIFO pairs retained four released ancestors, and new
   admission in a flowed released context retained the dead predecessor. Request/work acquisition
   and current-head disposal now skip released predecessors. Shared lifetime liveness and live
   ancestor links remain intact; no epoch, census, scheduler, or signal redesign was introduced.

Only `GrimoireConnectionAdmissionGate.cs` changed in production. The existing test class became
partial, with new `Characterization`, `Lifetimes`, `Races`, and `Terminalization` parts. The owning
architecture/testing discussion in `docs/Arcanum.DESIGN.md` travels with the change. Public
interfaces, all sixteen work kinds and validation, the three phases, production five-second
timeouts, the dormant generation signal, and request/work terminal TCS behavior are unchanged.

## TDD evidence, in brief order

All tests exercise the real gate. Expectations are literal states, generations, cancellation
counts, exception types, exact object identity, or the two legal outcomes of a linearization race.
No source-text oracle, sleeps, scheduler-luck assertion, production test hook, or mutation API was
added. The generation test alone mutates `_generation`, through test-only `UnsafeAccessor`.

The successful focused commands used this common prefix and suffix:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-restore --disable-build-servers -m:1
  --filter '<filter below>' --logger 'console;verbosity=minimal'
```

Each production correction followed a compile-clean, inspected assertion failure, then the
smallest production edit, then the same focused filter passing. The final helper/formatting
cleanup was verified again by the complete slice.

### Cluster 1: deferred effect revocation

Filter:

```text
FullyQualifiedName~Effect_winner_is_revoked_after_group_release_even_after_abort|FullyQualifiedName~Deferred_revocation_callback_reenters
```

RED: 5 failed, 0 passed, exit 1. All four combinations of group-first/scope-first and
closing/proven-abort failed at the post-group cancellation assertion: expected true, actual false.
The callback test failed with expected callback count 1, actual 0. No production changes preceded
this RED.

GREEN after recording deferred cancellation and delivering it outside the monitor: 5 passed,
0 failed, exit 0. Repeated disposal keeps cancellation count exactly 1; an active group receives
no maintenance cancellation before release. A throwing callback asks a separate, prestarted
dedicated thread to read the gate and waits for that read, proving another thread can acquire the
gate monitor during the callback. The callback runs on a dedicated release participant; neither
it nor the other synchronous waits blocks an xUnit worker. Its exception cannot prevent drain.

### Cluster 2: generation exhaustion

Filter:

```text
FullyQualifiedName~Generation_exhaustion_preserves
```

RED: abort case failed, unchanged stage-two control passed (1 failed, 1 passed, exit 1). Both
operations threw the expected `OverflowException`, but after abort overflow a new request was
admitted: expected false, actual true. This proves the half-reopen independently of source order.

GREEN after moving checked arithmetic before abort mutations: 2 passed, exit 0. Assertions also
pin `long.MaxValue`, no next-generation signal, refusal of request/work/open, exact same owner on
resume, valid stage-one drain after release, and repeated stage-two exhaustion without reopening.

### Cluster 3: ambient identity, retention, and stale generations

Retention regression filter:

```text
FullyQualifiedName~Non_lifo_disposal_does_not_retain|FullyQualifiedName~New_admission_does_not_retain
```

RED: 4 failed, 0 passed, exit 1. Request and work non-LIFO churn each had ambient retained depth
4 when the literal required depth was 0. New request and work admission after the flowed original
was released each retained depth 2 instead of the one current lifetime. The retention census uses
read-only test reflection over the actual ambient chain, avoiding nondeterministic garbage
collection or adding a production diagnostic surface.

GREEN after prefix pruning at acquisition and current-head restoration: 4 passed, exit 0.

Expanded cluster filter:

```text
FullyQualifiedName~Non_lifo_disposal_does_not_retain|FullyQualifiedName~New_admission_does_not_retain|FullyQualifiedName~Nested_and_non_lifo|FullyQualifiedName~Flowed_finisher|FullyQualifiedName~Promoted_request_cannot_replay|FullyQualifiedName~Revoked_old_work|FullyQualifiedName~Repeated_old_generation
```

20 passed, exit 0. The other 16 cases characterize already-correct behavior and were not
manufactured REDs: eight mixed request/work ancestor and disposal-order cases, two flowed shared
release cases, promotion replay after abort, revoked work in the next close census, and four
old/new request/work disposal-isolation combinations. Generations 1, 2, and 3, exact promoted
identity, drain incompletion while the final real owner lives, and closing finisher refusal are
asserted directly.

### Cluster 4: admission/close and effect-frontier linearization

Filter:

```text
FullyQualifiedName~Request_admission_and_close_have|FullyQualifiedName~Every_work_kind_admission_and_close_have|FullyQualifiedName~Open_admission_and_stage_two_have|FullyQualifiedName~Effect_start_and_close_select
```

28 passed, exit 0. These are passing characterization tests. Both request kinds run admission
first, simultaneous release, and closure first. Each of all sixteen work-kind cases runs those
same three schedules. Physical-open acquisition versus stage two and effect start versus closure
also each run all three schedules.

`RaceDedicated` starts two `TaskCreationOptions.LongRunning` participants. A two-party barrier
ensures both exist before either gate call runs. Completion events force each ordered control;
the simultaneous case accepts only a coherent legal winner and tests its census/cancellation
consequences. The test coordinator awaits tasks rather than synchronously waiting on the barrier.

An admitted request/work lease must keep stage one pending until release. An admitted open must
keep stage two and the physical drain pending until its explicit terminal result. Lost generation
refuses post-native-open revalidation; disposal cannot manufacture the required terminal report.
An effect winner remains uncancelled through its active group, and the loser starts no group.

### Cluster 5: missed-zero races and exactly-once disposal

Filter:

```text
FullyQualifiedName~Final_lifetime_release|FullyQualifiedName~Final_open_terminalization|FullyQualifiedName~Direct_lifetime_disposal|FullyQualifiedName~Disposed_effect_group|FullyQualifiedName~Scope_and_group_disposal|FullyQualifiedName~Terminal_open_disposal
```

Final result: 24 passed, exit 0, using `--no-build --no-restore` after the fresh nonincremental
solution build. These are characterization cases, not new production corrections.

Twelve cases cover final finite-request, quiesceable-request, work-scope, and held-effect release
before, concurrent with, and after drain waiter acquisition. Three do the same for final native
open terminalization versus stage-two wait publication. The monitor's existing per-lifetime
terminal semantics remain intact; these observable no-missed-zero contracts also constrain any
later candidate publish/recheck implementation.

The remaining matrix proves direct concurrent/repeated request and work disposal cannot release
a sibling; an old disposed group cannot clear a successor group on the same work lease; scope
and group disposal revoke and drain exactly once in all three schedules; and opened, failed, and
refused-after-open tickets cannot replay terminal transitions or release a new-generation ticket.
The native-open matrix distinguishes terminal replay (`InvalidOperationException`) from replay
after disposal (`ObjectDisposedException`) and verifies that a new unresolved ticket still blocks
the next close.

## Test-authoring and environment corrections

- The initial sandboxed MSBuild launch failed local named-pipe binding with `SocketException
  (13): Permission denied`. It was stopped, and tests were run with authorized escalation,
  disabled build servers, and `-m:1`. These launch failures are not TDD evidence.
- The first terminal replay matrix incorrectly expected the exact `InvalidOperationException`
  type after disposal. xUnit requires an exact type, while this boundary correctly throws
  `ObjectDisposedException`; cleanup of the deliberately unresolved current ticket masked that
  assertion. The test now checks both terminal and disposed states separately and terminalizes
  the held current ticket in `finally`. No production code changed for this authoring error.
- An edit overlapped a compiling test run and the incremental output retained the earlier test
  body. Intermediate stale-binary failures were discarded. Source was frozen, the entire solution
  was built with `--no-incremental`, and both the terminal cluster and full requested slice then
  passed against the fresh binaries. No success claim relies on those intermediate runs.

## Final verification

```text
dotnet build RetroDownfall.Arcanum.slnx
  --no-restore --no-incremental --disable-build-servers -m:1
Build succeeded. 0 Warning(s), 0 Error(s). Elapsed 00:01:00.45. Exit 0.

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-build --no-restore
  --filter 'FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireRequestAdmissionScopeTests'
  --logger 'console;verbosity=minimal'
Passed: 195, Failed: 0, Skipped: 0, Total: 195. Exit 0.

dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes --no-restore
  --include
    src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs
    tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
    tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.Characterization.cs
    tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.Lifetimes.cs
    tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.Races.cs
    tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.Terminalization.cs
  --verbosity minimal
Exit 0, no changes or diagnostics.

python3 scripts/align_csharp_blanklines.py --repo . --check
Would change 0 file(s). Exit 0.

git diff --check
git diff --cached --check
Exit 0, no diagnostics.
```

The complete regression slice includes existing real interceptor/provider/physical-close tests,
exact finisher/promotion checks, owner authority, native revalidation/final-publication loss,
timeouts, abort/retry, callback exception isolation, dormant generation signaling, `KeepClosed`,
and request-scope disposal. The 79 added cases extend those contracts. No broad producer test
suite or Native AOT benchmark qualification is claimed for Task B.

## Immutable-H and scope audit

`git diff --exit-code H --` returned 0 for the entire admission benchmark project, linked benchmark
tests, benchmark packaging tests, benchmark script, workflows, NativeSqlCipher project/assets,
project/build/solution/lock files, `global.json`, and `NuGet.Config`. The only changed `src/` path
relative to H is `GrimoireConnectionAdmissionGate.cs`. H is an ancestor of B. The optional
`GrimoireConnectionAdmissionEpoch.cs` does not exist. No benchmark, catalog, source allowlist,
native/dependency/build input, queue, scheduler, TCS-lazification, or hot-path candidate was added.

## Self-review and concerns

- Lock/callback ordering: closing records the work obligation while holding `_sync`; exact group
  release captures the source while holding `_sync`; cancellation and consumer callbacks occur
  afterward. The work terminal signal is settled before a callback can fail. The cross-thread
  re-entry case would fail if cancellation moved inside the monitor.
- Generation/ABA: checked abort arithmetic precedes any authority mutation; stage two remains
  unchanged. Old leases keep their original generation and exact object identities. No identity,
  closure, epoch, or signal reuse was introduced.
- Exactly-once ownership: existing atomic disposal guards and exact group identity comparisons
  remain. Duplicate disposal cannot remove sibling, successor-group, or new-generation entries.
- Ambient retention: only released leading ancestors are skipped; live predecessors and shared
  liveness are preserved. The read-only ancestry test is intentionally coupled to the current
  ambient representation, so a future representation change must preserve its retention contract.
- Flakiness: synchronous blocked participants are prestarted dedicated threads; coordinator waits
  are asynchronous. Manual timers create the timeout preconditions. Real-time ceilings only bound
  failed handshakes; they do not decide the legal race winner. No sleeps or ThreadPool blocking
  participant tasks were added.
- Scope: the three proven corrections are baseline behavior, not optimization gains. Eager
  request/work terminal tasks are intentionally unchanged for the later candidate's independent
  structural/performance decision.

No unresolved implementation defect is known. Independent review remains required before treating
`18c7a2b2f779dd4a87a1d45c1a1e61fa924200ff` as the reviewed baseline B.
