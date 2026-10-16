# The Long Rest Carried Admission implementation plan (#91)

> **For agentic workers:** This plan records the work as delivered, workstream by workstream, with the evidence each one produced. A checked box is delivered and merged into the delivery branch; an unchecked box is still to run. Do not re-execute a checked workstream; read its outcome and evidence. The 2026-10-09 plan keeps its history ([2026-10-09-long-rest-hybrid-sweeps.md](2026-10-09-long-rest-hybrid-sweeps.md)); its Tasks 1, 2 and 4 stand (with the schema 18 additions, the capture changes and the model-window-derived bounds recorded below), its Task 3 stands except that the lexical lane and hierarchy recall are re-specified (Scoped Lexical Ranking, Ancestor-Excluded Cross-Cluster Recall, a generation-scoped index, no summary indexing), its Task 7 stands with two exceptions, the dispatch internals of its Task 5 change (bounds, one connection per send, unknown-usage settlement, release of unsent attempts), the hosting and recovery internals of its Task 6 are replaced, and the lifecycle closure of its Task 8 stands while its qualification and delivery list is replaced by WS14 below.

**Goal:** Deliver #91's hybrid Saga discovery and resumable authority-scoped consolidation with one authority architecture (Carried Admission) that the repository's hosted-producer proof can verify, and a recovery model that never runs paid work (Park-and-Readmit), then qualify the exact final commit and merge it into `memory-enhancement`.

**Architecture:** One kind-17 work lease is minted at a root and passed by value to every use. Durable recovery parks Long Rest sweeps; the hosted worker readmits clean ones atomically under its own lease and a fresh Session capability readmits protected ones. Paid doors settle unknown usage truthfully and send once per connection. Verification is deep at seams and cheap at steps. The static proof gained only the machinery Carried Admission needs, plus the repairs the first implementation's checker hardening required.

**Tech Stack:** .NET 10 Native AOT, hermetic SQLCipher/FTS5, source-generated System.Text.Json, Roslyn-based hosted-producer analysis, xUnit, Python/shell gates, GitHub shipping macOS/Windows gates.

**Spec:** ../specs/2026-10-13-long-rest-carried-admission.md

## Global Constraints

- Deliver only to `memory-enhancement`, never to `main`. Work on `claude/issue-91-delivery` and one branch per workstream (`claude/91-<id>`), each created from the exact delivery SHA as its first command and merged `--no-ff`.
- Preserve the 2026-10-09 domain and public surface: `Arcanum:Features:LongRest` default off, `POST /api/memory/saga/long-rest/sweeps`, exact Global and canonical Campaign partitions, generations, protected authority, disclosure before dispatch.
- No weakening of the six traversal budgets (256, 256, 8,192, 4,096, 64 and 128), any storage scan, any fail-closed path or any assertion. A size problem is fixed by a production reshape or a sound identity rule, never by a larger limit.
- TDD: a behavioral RED before each behavior, then GREEN. A change to the checker is first a fixture of the exact production text.
- Run each test and review once, at the right point. A review whose verdict carries findings goes to a fixer; a Minor-only fix round is checked by reading the diff. Reviewer minors and neighbours noted along the way are fixed before the merge, not deferred.
- Opus for judgment-heavy checker and security work and for the final review; Sonnet for well-defined waves. Every security-relevant workstream gets an independent review before its merge.
- Docs travel with code (DESIGN, API, Command Reference, Compendium); the root README changes only for an intentional front-page correction.
- Schema 18 is unpublished: identity-bearing changes land in the V18 head and transition files together. It has no downgrade, so installing the delivered build needs an owner-taken backup.

## Method

- **Delivery branch and baseline.** `claude/issue-91-delivery` starts at dcc2a12a, the first implementation's retained state (tree 326f85e5, equal to the tree it was copied from; base b15d6960). Release build of the test project at the baseline: 0 warnings, 0 errors.
- **Loops.** L0 is an incremental Release build plus a filtered `dotnet test` on fixtures (about a minute). L1 is a root-scoped discovery run through the Production Discovery Entry with the real limits (three to five minutes). A full-graph run covers the primary and additional partitions. A fixture partition run (`fx*`) and a production-lane run (`pl*`) validate each integration.
- **Build-then-Swap.** The checker rebuilds the production compilations from `src/` at run time, so the candidate test assembly can be built at HEAD and run with `src/` swapped to the base commit. That is how the attribution runs and the Old-Source Parity checks were produced. The harness scripts (`tools/graph.sh`, `tools/scoped.sh`) are scratch in the delivery worktree and are not committed.
- **Process note.** The orchestration scripts treated a review verdict of the form "approve with fixes" as an approval, so those findings did not reach the fixer. Three reviews were affected (the WS11 performance review, the WS8c review and the closing-wave Sanctum security review). Each was recovered from its saved output and fixed in WS11c, WS8d and the Sanctum stream. Later scripts run the fixer whenever a review has any finding.

## Workstream index

| Id | Branch head | Merged into delivery | Outcome in one line |
| --- | --- | --- | --- |
| WS0 | scratch only | none | Baselines and attribution; nothing shipped. |
| WS1 | 85308cde | f8b3be7d | Presented-Lease Attestation replaces the drained-maintenance proof object. |
| WS2 | 4fdecf5e | cd6d8654 | Unkeyed Registry Restoration. |
| WS6a | c7e7a18a | 4bdf3aec | Production Discovery Entry, SqliteSealedByUse, Owned Client Construction. |
| WS6b | 26ccedfa | d52f2351 | Closed-Value Equality, Structural Operand Rule, Runtime Type Identity Comparison. |
| WS0F | 16530418 | 63f6eed8 | Checker integrity: 13 false grants closed, stale fixtures, extern audit. |
| WS6c | 40acf73a | f1e3fc50 | Awaited recovery helpers; `ProviderSettings.Models` as an array. |
| WS0G | fe379f80 | 45e466b8 | Implicit enumeration modeled. |
| WS3 | 38d64921 | c1c8ae01 | Lease Spine and Park-and-Readmit Recovery. |
| WS8a | 54c3d0b6 | 5b1704ea | Handle-incapable values; one-read worker wake. |
| WS4 | b2544fe6 | 1417c8ab | Lease Receiver Proof with the Must-Alias Lease Demand. |
| WS5 | f9c66ecb | f677b01f | Lease-Handoff Root. |
| WS7 | ebd47f6e | b2280721 | Authority Carrier Ban, Admitted Entry Census, Ambient Holder pin. |
| WS9 | ee5c97ca | 9bb7bc92 | Robustness wave: fast model, one connection per send, unknown usage, turn policy. |
| WS10r | 211aea6b | bbf93f04 | Retry Cursor and the first zero-diagnostic graph. |
| WS10 | eb613df7 | c51d6664 | Discovery-quality wave. |
| WS0H | c8123c17 | cbc1d80b (into int0hi) | EF and ADO.NET completeness. |
| WS0I | 993f36ff | 56c8e217 (into int0hi) | Implicit Call Census. |
| INT | 942bcd52 | 14a34164 | Integration fixes after WS0H and WS0I. |
| WS8b | bbbf4f6c | e4c289a0 | State containment and the Headroom Tripwire. |
| WS11 | c555a6d9 | 906106cd | Verification-cost wave: Seams-and-Steps. |
| WS8c | a3934a5a | f5c95c95 | Verification speed. |
| WS11c | 08f510f0 | aef0bc63 | Idle-turn cost independent of corpus size. |
| WS8d | 096dae85 | 7b37f7eb | Review fixes and CI partitions. |
| CW | 5576e2d0 and e384011d | 49bdba58 and 763e012a | Closing wave: checker (cwc) and product (cwp). |
| SX | 1f926446 | aa3c83c6 | Sanctum Campaign resolution. |
| WS12 | not merged (branch claude/91-ws12 at aa3c83c6, work paused) | none | Docs, contract and compliance. |
| WS13 | not merged (branch claude/91-ws13 at aa3c83c6, work paused) | none | Final convergence. |
| WS14 | not started | none | Qualification and delivery. |

The delivery head with every row above merged except WS12 to WS14 is aa3c83c6.

## WS0: Evidence harness and baselines

- [x] **Goal.** Measure before building, so every later claim is judged against a recorded number. Nothing in WS0 changes a verdict.
- **Outcome.** All measurement ran in scratch worktrees and left no production or committed test change. The two committed pieces the plan had assigned to WS0 landed inside later workstreams: the Production Discovery Entry in WS6a and the frozen-limits pin in WS8b. The parity checks the scratch harness ran through Build-then-Swap were the residue and state-limit parts of the Old-Source Parity Gate (the residue and the overflow roots); its manifest-delta part waits for the Diff Row Classifier (`scripts/classify-manifest-diff.py`), which does not exist yet and arrives in WS13. No committed parity script exists.
- **Evidence.**
  - Full production graph on dcc2a12a: failed in 12 minutes 57 seconds with 8,538 diagnostics (7,602 `WORK_FRONTIER_MISSING`, 889 `UNCLASSIFIED`, 28 `RECOVERY_HANDLER_UNPROVEN`, 7 recovery-owner, 7 disposal-target, 2 call-target, 1 recovery-dispatch, 2 state limits).
  - Attribution: new checker on old source 665 primary and 287 additional; old checker on new source 64 and 1,661; new on new 8,538 and 534 (total 9,072), reproduced exactly. The new checker ran about 75 percent slower.
  - Runtime baseline (excluding the hosted-analysis categories): 22,695 passed, 7 failed, 117 skipped in 38 minutes 58 seconds. The seven are stale benchmark input catalogs (2), keyed-registration failures (3), a stale production-lane list (1) and a console-scan failure that also occurs on the base commit in Release (1).
  - Fixture Baseline Multiset on dcc2a12a: 3,003 passed, 47 failed in 15 minutes 44 seconds. Eight are deliberate (the scoped-lease discovery fixtures, 7, and the keyed known-selected row, 1); 39 were unknown and went to triage.
  - Triage: 13 confirmed false grants (a widened `DbDataReader` indexer certified as Sqlite database access), environment metrics (a record `with` clone defeated the exact-token cache), 11 stale cleanup and factory fixtures, 4 Serve fixtures that were red by design because the Long Rest closure was 16,453 states, and a pre-existing `DbConnection.CreateCommand` grant by name.

## WS1: Presented-Lease Attestation

- [x] **Goal.** Replace the drained-maintenance proof object with an abstract gate member that attests the presented live lease at each use, with the old predicate term for term and the Long Rest kind check in the fence reader.
- **Outcome.** `AttestDrainedMaintenance(lease, previous, current)` on the gate; `DrainedMaintenanceProof` and `ProveDrainedMaintenance` deleted; `LongRestLiveFenceReader.AttestCleanAdmission` checks the kind. Clean readmission and the completed checkpoint take the lease. Merged f8b3be7d.
- **Evidence.** `GrimoireConnectionAdmissionGateDrainedAttestationTests` (a positive case and a false case for each of a foreign gate with equal counters, a wrapper, a disposed lease, scope disposed with an active group, revocation pending, a cancelled token, closing and closed gates, a wrong generation, a previous generation below the floor, previous at or above current); fixture `LeaseArgumentAttestation` in an ordinary and a recovery root. Opus review: fixed. Transitional kind-14 acceptance was left in the attestation for WS3 to tighten.

## WS2: Unkeyed Registry Restoration

- [x] **Goal.** Return recovery-handler registration to the base shapes and remove the validation pass the keyed shape needed.
- **Outcome.** Thirteen registrations, the expression-bodied reconciler factory and `GetServices(...).ToDictionary` lookup restored; `ValidateRecoveryHandlerAliases` and the selected-kind tests deleted; the known-selected RED row removed with its six refusal rows kept as pins. Merged cd6d8654.
- **Evidence.** `Recovery_registry_has_no_keyed_handler_descriptors`, `Every_serving_handler_has_exactly_one_unkeyed_registration`, and a real serving composition resolved with validate-on-build. After review: approved with minors fixed (4fdecf5e).

## WS6: Checker parity and production reshapes (WS6a, WS6b, WS6c)

- [x] **Goal.** Remove the checker regressions the first implementation's hardening caused on code #91 never touched, developed against unchanging base source through Build-then-Swap.
- **WS6a outcome (c7e7a18a).** The Production Discovery Entry with an optional root subset; the Premise Ledger with `SqliteSealedByUse`, which scans every resolved reference and refuses an unbound image; exact CLI native imports; Owned Client Construction for Campaign Rollup and MCP HTTP; a Sqlite-typed schema read. Four major review findings fixed, among them the premise scanning all references, the `ProviderResolver` reshape reverted, and the owned MCP transport's connect-time egress policy pinned.
- **WS6b outcome (26ccedfa).** Closed-Value Equality with Canonical Equals Traversal and Full-Environment Identity, the Structural Operand Rule, Runtime Type Identity Comparison, and the triage of the scalar tail by reshape (a reviewed contract for the three token-count histograms). Three major findings fixed.
- **WS6c outcome (40acf73a).** Recovery handlers await the helper they delegate to; `ProviderSettings.Models` is declared as `ModelEntry[]` so the model indexers classify; the wire shape is unchanged (`ProviderSettingsModelsTests`). Approved with no code defect.
- **Evidence.** Unclassified sites fell from 1,405 to 7 on the wave-1 delivery graph; old-source overflow roots sat at 7,562 and 7,625 of 8,192. Fixture partition on the wave-1 delivery: 46 failures, the baseline's 47 minus the keyed known-selected row, with zero new failures.

## WS0F: Checker integrity

- [x] **Goal.** Close the false grants and stale-fixture families the baseline fixture run exposed, before any soundness claim is made on top of the checker.
- **Outcome (16530418).** The 13 Sqlite indexer false grants and their hardening (never-written constructor parameters, an operation-based write detector, base-constructor options forwarding, a connection-replacement symbol scan, a first-use hook not admitted); the exact-token fast path hit restored for static calls; 11 stale fixtures updated; the extern vacuity audit (about 97 static extern values; one vacuous fixture fixed); contracts documented. Five Opus HIGH findings fixed. Merged 63f6eed8.
- **Evidence.** Fixture partition on the branch: 12 deliberate failures only (7 scoped-lease, 1 keyed, 4 Serve). The production graph was identical to the attribution run. After the merge the fixture partition had 11 failures (7 scoped-lease and 4 Serve, all deliberate).

## WS0G: Implicit enumeration

- [x] **Goal.** Model the calls a `foreach`, `await foreach` and collection spread make without writing them.
- **Outcome (fe379f80).** `GetEnumerator`, `MoveNext` and `Current` (async too), extension `GetEnumerator`, `ICollection` members and lazy iterators modeled; the premise `AuthoredEnumerablesAbsentByUse` added, the second premise of the Premise Ledger (the panel had one; the growth is a decision listed in the spec). Eleven Opus major findings fixed. Merged 45e466b8.
- **Evidence.** Graph byte-identical to the wave-1 graph; fixture partition: exactly the 11 deliberate failures.

## WS3: Lease Spine and Park-and-Readmit Recovery

- [x] **Goal.** One compiling production change set that carries the lease by value and makes recovery park instead of run, then measure the new graph.
- **Outcome (38d64921).** WS3a (cabed5a0): `LongRestWorkAdmission` deleted, `ILongRestAdmittedSweep` and `ILongRestAdmittedMaintenance`, the Session shell, the Admission Kind Guard. WS3b (efaa633f): the Pure Park Handler, Atomic Readmission, Sweep Failure Budget (`ReadmitCount`, `ParkedAt` in the head table and the V18 transition), `LongRestFaultPolicy`, Parked Rescan, Parked-Is-Active, the per-lease token and holder `Bind` removed from settlement while the inline acquisition (Finisher Ambient Fix) stays. `AttestCleanAdmission` is tightened to kind 17 only, which removes WS1's transitional kind-14 acceptance. Merged c1c8ae01.
- **Review.** Opus: one major finding (a stale budget count) and eight minor ones, all fixed. One deviation: `LongRunningOperationStore.AcquireLeaseAsync` stamps `long_rest.readmit_requested` when it adopts a Pending Long Rest row, because the pure handler cannot see Pending. The restart-generation parks that still answered 409 `SweepParked` were handed to WS9.
- **Evidence.** Kill-Switch Run at the real 8,192 cap, with the delivery checker as of WS0G (before WS4 and WS5), on the compiled tree: primary 2,228 diagnostics (1,494 handle-escape and 729 work-frontier under the worker root, 3 callback-ownership, 1 state limit and 1 `Histogram.Record`), additional 0; recovery roots clean (the background recovery root at 3,444 states). State Census at a 65,536 scratch cap: `ExecuteAsync` 14,536 states (over 8,192, which triggered WS5), `RecoverBeforeBootstrap` 7,602, `InstallationReset` 7,562, `WorkspaceIndexing` 6,564.

## WS8a: Handle-incapable values and a one-read wake

- [x] **Goal.** Remove the 2,228 diagnostics at the root cause: the may-alias origin walk made closed data derived from lease calls look like handle carriers.
- **Outcome (54c3d0b6).** Closed pure data contributes no origin (`HandleIncapableValues`); an instance event refuses the closed-data claim; static storage stash refused at the store; the Parked Rescan reshaped to one awaited read with a `TimeProvider`-backed token source. Merged 5b1704ea.
- **Evidence.** Full graph primary 2,228 to 2 (a state limit and a `Histogram.Record`), additional 0; the sites outside Long Rest byte-identical. Known limits pinned (instance-storage stash through a closed key, static alias through a local or ref, delegates not carriers) and covered at production level by the Authority Carrier Ban.

## WS4: Lease Receiver Proof

- [x] **Goal.** Close the gap that let a group begun on any lease receiver satisfy a send site, in both the old and new checker.
- **Outcome (b2544fe6).** The Must-Alias Lease Demand and the receiver proof at the effect site, the retained-admission scan and the recovery effect frontier; code `HOSTED_ADMISSION_RECEIVER_MISMATCH`; the attachment-batch flush takes its lease as an argument (a production reshape). Merged 1417c8ab.
- **Review.** Opus: two HIGH findings fixed (unsound local null-test pruning, now `LocalWrites`; cache poisoning, now a declaration-qualified binding identity). Open items taken into later work: the holder-era exemption (to be removed in WS13) and a forged-gate case (closed in the closing wave by the Single Gate Census).
- **Evidence.** Validation on the integrated tree: graph primary 2, additional 0; fixture partition only the 11 deliberate failures.

## WS5: Lease-Handoff Root

- [x] **Goal.** Split the Long Rest closure so it is judged once as its own root, adopted only on census evidence.
- **Outcome (f9c66ecb).** One handoff at `LongRestMaintenance.ProcessAdmittedAsync` with the lease formal `admission`: Root Lease Formal, Formal Preservation, the Handoff Caller Census, a manifest binding row; a handoff root may hand its lease to another handoff root. New codes `HOSTED_LEASE_HANDOFF_{UNPROVEN,CALLER_UNANALYSED,ROOT_UNBOUND}` and `HOSTED_SITE_LEASE_{FORMAL,HANDOFF}_UNDECLARED`. Merged f677b01f.
- **Review.** Opus: three major findings (a second alias through other arguments, the receiver or a closure; a `ConfigureAwait` window; a chain pin bypass by concrete type) and five minor ones, all fixed test-first.
- **Evidence.** Scoped state counts: `ProcessAdmittedAsync` 5,385 and `ExecuteAsync` 3,714 (the cap untouched). Full graph at 7bdf9d9f: primary 1 (`Histogram.Record`), additional 0; the Serve fixtures green.

## WS7: Authority pins and checker-test topology

- [x] **Goal.** Make the authority discipline a failing test rather than a review comment, and retire the holder-era discovery fixture.
- **Outcome (ebd47f6e).** `AuthorityCarrierBanTests` (a ten-entry census), `AdmittedEntryCensusTests`, the ambient-holder fixtures, the generated-record fixtures ported to the lease-parameter shape, `ScopedLeaseDiscovery` deleted, `ActualReadinessCapsules` replaced by a test over the shared hosted-root discovery, a DESIGN tracker reference removed. Merged b2280721 (one conflict resolved by keeping both the shared discovery structure and the formal assertions).
- **Review.** Opus: 12 findings, all fixed except two decided separately: the two work-lease closure captures (removed in WS10r by the Retry Cursor) and the ambient-holder refusal being keyed on the `Bind` name (taken by WS13).
- **Evidence.** Fixture partition on the merged tree: 4,112 of 4,112 passed, the first fully green partition. Production lane, build and documentation tests: 728 of 743, the 15 failures being 13 copies of the single `Histogram.Record` diagnostic and 2 manifest and catalog regeneration tests.

## WS9: Robustness wave

- [x] **Goal.** Close the audit gaps G9, G10, G2, G1, G1c, G6 and G11 and make the optional ports required.
- **Outcome (ee5c97ca).** `LongRestRecipeResolver` (`FastModel` else `DefaultModel`, fail closed); one connection per send on the Long Rest and Campaign Rollup handlers; in-frame unknown-usage settlement (state 6, no completion, `LongRest.TransportUnknown`), not-sent settlement for connect failures, the Unknown-Usage Cap in `PrepareAttemptAsync`, failures typed from the exception chain alone; the clean-turn policy and restart-generation retirement for clean sweeps; Clean-Only Busy Skip with a status event before protected maintenance; a validator warning, a doctor row and a health row; the four Long Rest ports on the campaign preparer made required. Merged 9bb7bc92.
- **Review.** Opus: three major findings (the not-sent retained-effect invariant; a not-sent connect failure lost inside the egress guard, fixed so the guard's own connect failure types as unreachable; documentation that claimed no turn is refused) and three minor ones, all fixed.
- **Evidence.** Fast pins 333 of 333 on the merged tree (carrier ban, entry census, handoff chain, documentation, nullable-interface, spine, Pure Park, boundaries); release build 0 warnings.

## WS10r: Retry Cursor and the first zero graph

- [x] **Goal.** Remove the two pinned work-lease closure captures and the last `Histogram.Record` diagnostic.
- **Outcome (211aea6b).** `SqliteBusyRetryCursor` (a struct; `WaitAsync` is the old policy exactly, `ExecuteAsync<T>` re-implemented on it); `ReadmitCleanAsync` and `ReconcileCompletedCheckpointAsync` loop inline around the lease parameter with no closure. The carrier census fell from 10 to 8 entries, none a work lease. The `Histogram.Record` cause was a traversal frontier recorded over children read from storage; a new exact contract for storage GUID sequences with ten negative twins replaced the allowance. Merged bbf93f04.
- **Evidence.** Opus verified 109 scripted retry scenarios identical to the old policy. On 211aea6b: full graph primary 0 and additional 0, the first zero graph; fixture partition 4,119 of 4,119.

## WS10: Discovery-quality wave

- [x] **Goal.** Fix identity-bearing quality gaps while V18 is unpublished: G3 and N1, G4 and G7c-1, hierarchy self-match, Eligible-Source Capture, G5a, G8.
- **Outcome (eb613df7, 86 files).** 10a: `LongRestPromptLayout`, the window-derived input bound, `LongRestGroupPacker`, Admit-then-Prepare with `ReleaseUnsentAttemptAsync`, `LongRest.ModelContextTooSmall`. 10b: the integer `LongRestLexicalScorer`, letters-and-digits match expressions, generation-scoped full-text, lazy verification, Ancestor-Excluded Cross-Cluster Recall with rank interleave, chain verification, a differential oracle, the flaky hierarchy test root-caused. 10c: Eligible-Source Capture, Set-Level Decision Reuse with an adjudication recipe version, the Supersede-on-Completion trigger (V18/058) with index purge, the reuse index dropped. Merged c51d6664.
- **Review.** Three parallel Opus reviews: 10a three medium and five low, 10b one important and five minor, 10c two important and two minor, all fixed except one embedding-minimum item that was handled by documentation and then by the closing wave's Embedding-Bounded Summary.
- **Evidence.** Scoped roots `ProcessAdmittedAsync` 5,763 and `ExecuteAsync` 3,734; scoped run on the merge with WS10r: 0 diagnostics (the storage-sequence contract survives the hierarchy rewrite).

## WS0H: EF and ADO.NET completeness

- [x] **Goal.** Close the checker's Entity Framework and ADO.NET holes by fixing them, not by accepting a documented boundary.
- **Outcome (c8123c17).** Exact operator verdicts and a census for query execution sites; context hooks fail closed; the Interceptor and Logger Lane (callbacks entered at the raising operations, with a disposal lane); the Context Universe; the Provider Dispatch Closure and the premise `DbProviderTypesClosedByUse` (the third premise of the Premise Ledger); Connection State Handlers; once-per-discovery Inventory Facts. A core rule changed: `Task` is no longer a lease carrier merely because it is disposable.
- **Review.** Opus: eight CRITICAL, one HIGH, three MEDIUM and two LOW findings, all fixed.

## WS0I: Implicit Call Census

- [x] **Goal.** Account for every implicit user-code call in C# with a fail-closed operation census.
- **Outcome (993f36ff).** Deconstruct, collection `Add`, awaiters, interpolation handlers, `with` and records, patterns, operators and conversions, construction entry, accessors and written virtual dispatch on the runtime type, framework callbacks into `ToString`/`Equals`, static initialisation and async builders; `ReviewedClosedOverrideSets` for the credential mirror; a behavior-preserving production reshape (`OsKeychainSecretStore` builds each mirror in a member of its own). Merged into the integration branch as 56c8e217.
- **Review.** Opus: five HIGH, seven MEDIUM and four LOW findings fixed; three reviewer expectations rebutted with pins.

## INT: Integration fixes after WS0H and WS0I

- [x] **Goal.** Reconcile the two checker branches and the delivery tree.
- **Outcome (942bcd52).** Conflicts resolved in design text, collection lists and catalog rows. A: the Serve production-text pin retargeted to `TimeSpan.Zero` with a refusal twin. B: a conditional-access member read judged like a plain read. C: the handoff dedupe expectation pinned by five facts. D: finalizer sites before a manually owned lease's disposal run under that lease, and closure and member-group disposal are recognised. Merged 14a34164.
- **Evidence.** Before the fixes the integrated tree had a full graph of 1 primary and 0 additional (1,557 s on a loaded machine) and a fixture partition of 6,129 tests with 47 failures (44 stale Serve production-text pins, 2 accessor fixtures, 1 closure-traversed-once expectation). After: fixture partition 6,152 of 6,152 passed; graph 0 and 0.

## WS8b: State containment and the Headroom Tripwire

- [x] **Goal.** Bring every root back under the traversal limit with sound identity rules and a ratchet, without changing any verdict.
- **Outcome (bbbf4f6c).** Five identity rules (Constant Value Identity, Foreign Parameter Projection, cancellation tokens never bound to their chain, non-owning parameter creation under `DbProviderTypesClosedByUse`, Inert Payload); the Headroom Tripwire (`Every_production_root_stays_within_its_ratchet_ceiling` with 66 rows, `Every_additional_root_stays_within_its_ratchet_ceiling` with 29); `LimitsAreFrozen` pins `MaxCallableWork` at 256. Merged e4c289a0.
- **Evidence.** Site and diagnostic dumps byte-identical to the pre-change tree at one, two and three root workers. Roots: `RecoverBeforeBootstrap` 8,164 to 6,836, `GrimoireDatabase` 6,446 to 5,600, `InstallationReset` 8,020 to 5,555, `ProcessAdmittedAsync` 5,927 to 4,452, `WorkspaceIndexing` 7,482 to 2,926; the additional maximum 3,080. Fixture partition 6,355 of 6,355 on the merged tree; production lane, build and documentation 745 of 747 (only the manifest and catalog regeneration tests). Opus: one moderate, three low and two informational findings fixed.

## WS11: Verification-cost wave

- [x] **Goal.** Make verification cost proportional to what each step acts on, without making any gate cheaper by omission: G7b, G7d, G7f, then G7a Seams-and-Steps.
- **Outcome (c555a6d9).** Expression indexes on the normalized identity joins with plan pins; deferred read transactions and a newest-first keyset completed lookup; `LongRestVerificationCounters` and an opt-in scale probe; Seams-and-Steps with a depth parameter that has no default, the rotating scrub window of 32, and `LongRestMutationMatrixTests` (114 and 114 cases plus 45 hierarchy rows). Merged 906106cd.
- **Review.** An independent security Opus found one major issue (an ordinary deletion of a consolidated source was admitted by Steps; fixed by a reverse attribution check in the metadata anti-join) and three minor ones (every leaf under a fold re-proved at the gates in front of its sends, a hierarchy-phase oracle, no defaults on `EnsureCurrentAsync`), all fixed.

## WS8c: Verification speed

- [x] **Goal.** Make the analysis cheap enough to run in CI, soundly and with identical verdicts.
- **Outcome (a3934a5a).** Root cause: the sites-only callback judgement asked about 2,030 authored formatters per key (343 million formatter iterations, 79.5 million repeated formatting steps per Serve case). Fixes: a Formatter Index, once-per-resolution formatting steps, an implementation-answer memo with a no-cycle-cut rule, shared semantic models per compilation and tree, and Program Indexes. Merged f5c95c95.
- **Evidence.** Fixture partition 6,379 of 6,379; summed test time 6,393 s to 1,206 s (wall time 1 hour 46 minutes to 20 minutes, loaded). Production lane with both partitions at three workers 1,626 s to 673 s, at one worker 2,298 s to 1,006 s. Sites, diagnostics and roots byte-identical at one and three workers (12 comparisons); an Opus review re-proved over 8,801 discoveries. Its minor findings were dropped by the process bug above and fixed in WS8d.

## WS11c: Idle-turn cost

- [x] **Goal.** Fix the review findings WS11's own review had dropped: an idle turn cost proportional to the corpus.
- **Outcome (08f510f0).** The completed-replay seam is one set-based statement plus a per-row comparison (SHA-256 cannot run in SQLite) and one proof per distinct applied receipt, with a per-leaf fallback only for over 16 causes or non-canonical label spellings; the `node_label_delete` trigger uses the norm index; a partial index for the sensitive early-out (V18/059); every Long Rest transaction is counted by a census test. Merged aef0bc63.
- **Evidence.** An idle turn runs 24 to 29 statements independent of the corpus size (it ran 5,037 at 1,000 sources and 15,053 at 3,000). A differential test (`LongRestMetadataCorpusEquivalenceTests`) proves equal verdicts. Opus: one BLOCKER (a stored manifest decoded from a BLOB was admitted by the set read), two medium and two low findings fixed. Decision: accept a seam cost proportional to the distinct applied receipts, with pinned statement counts. Headroom rows raised for `ProcessAdmittedAsync` (5,712) and the worker's `ExecuteAsync` (3,792) with the reason stated.

## WS8d: Review fixes and CI partitions

- [x] **Goal.** Fix WS8c's dropped findings and make the analysis fit CI.
- **Outcome (096dae85, plus doc fix 3309e973).** Whole-anchor keys for both callback memos, a work pin, the Unresolved-as-set fixture, shared-state documentation. The runner has four partitions (primary, additional, fixtures, serve-host), each its own CI job with one test process per fixture job; receipts are schema 2 and an aggregate job reconciles the four so every method and case appears exactly once; `ci.yml` has 12 jobs. Worker jobs build with build servers disabled and shut them down. A per-graph string pool fixed an out-of-memory under the memory proxy (cleanup-binding identity strings of up to 96,000 characters rebuilt per key). Merged 7b37f7eb.
- **Evidence.** Under a 5.5 GB heap limit and three processors: peaks of 5.9 GB (primary), 5.4 to 5.8 GB (fixtures), 4.1 GB (serve-host) and 4.9 GB (additional); the longest lane took 35 percent of the 1,740 s cap two at a time and 51 percent four at a time. Re-measured after the review fixes, the lanes took 172 to 669 s, with the longest at 38 percent. No branch protection exists on `main` or `memory-enhancement`, so renaming jobs is safe. Doc, CI and collection tests: 158 of 158.

## CW: Closing wave

- [x] **Goal.** Land the product and checker items the earlier waves routed forward, each with an independent review.
- **cwc outcome (5576e2d0).** Single Gate Census (`HOSTED_ADMISSION_GATE_UNREVIEWED`), the Departed-Lease Group rule (`HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH`), unread-tree lookups fail closed, Limit Pins (`CheckerLimitPinTests`), one call-site matcher, a Release-capable console scan, a whitespace-only style commit and a blank-line aligner that skips raw and verbatim literals. Opus: one important, four moderate and two minor findings fixed. Graph 0 and 0; the `ContinueInBackground` ceiling lowered to 3,640.
- **cwp outcome (e384011d).** Fresh-Authority Retire (six restart-generation codes, two proofs, a rollback twin); the recipe headline on a protected-turn failure; Embedding-Bounded Summary (the derived output cap was removed after review); Long Rest-scoped enrichment of the Campaign context; the Sanctum AuditOnly change kept, with tests on all three paths. Product review: one HIGH (an inspection regression), two medium and two low findings fixed.
- Merged 49bdba58 and 763e012a. Fixture partition on the merge: 6,484 of 6,485 (one Saga blob fixture, fixed in 849d7fcd); production lane at two workers: three expected failures (two manifest rows, one benchmark input catalog).

## SX: Sanctum Campaign resolution

- [x] **Goal.** Fix the Sanctum defect the closing-wave security review found: the Sanctum was activated only by an exact working-directory path lookup, so a subdirectory, trailing separator, case variant, symlink or Session-bound turn ran unsandboxed under a Strict Campaign. The defect also exists on `main`.
- **Outcome (1f926446).** `SanctumCampaignResolution`: the governing Campaign is the strictest claim among the canonical binding, the per-source asks (Session, explicit id, workspace identity) and the registered root that contains the working directory (`CampaignRootContainment`: NFC text, case folding on macOS and Windows, symlink-resolved spellings, physical identity walk, no depth bound); an unreadable policy refuses every tool call; denial kinds `Policy`, `PolicyUnavailable` and `AuditRecordFailed`; AuditOnly overrides only policy refusals and a failed breach write refuses; the research ward comes from the same resolution; Campaign registration refuses equivalent spellings (`Campaign.DuplicatePath`). Merged aa3c83c6.
- **Review.** The closing-wave security review of the Sanctum AuditOnly change found one HIGH and five LOW, which is what SX fixes. SX then had two independent Opus security reviews of its own; the second found two medium and four low findings, all fixed.
- **Open.** Lookup reads every `(Id, Path)` row per call, linear in Campaign count and uncached; Windows spellings are unverified on a Windows host; the owner has not said whether the fix should also land on `main` separately.

## WS12: Docs, contract and compliance

- [ ] **Goal.** Write the owning documents from the final code in the same change set (rule 6): DESIGN (§10.8, §10.8.2, §21.16.3 to §21.16.5, §21.9, §21.5, §4.3, §21.3, §21.4, §10.12, the Windows lanes, the Sanctum sentence), API (§8.36 and the error rows), the Command Reference (the Long Rest notes, with reconcile parking and retry resetting the budget), Compendium, OATH delivery rows, the registry repair guidance and the README link to the human OATH document.
- **Method.** Five read-only auditors (DESIGN Long Rest; DESIGN other, with a scrub of time-bound statements; API; Command Reference and Compendium; OATH, README, registry and miscellaneous) feed one editor, then an Opus documentation review and a fixer. A new `LongRestDocumentationTests` pins the doc claims. WS12 does not edit the hosted-producer checker sections; WS13 owns them. Branch `claude/91-ws12` from aa3c83c6.
- **Outcome.** To be recorded at merge.

## WS13: Final convergence

- [ ] **Goal.** With production frozen, run the full graph and settle the manifest, the benchmark input catalog and the holder-era retirement.
- **Method.** (A) Holder-era retirement (the per-unit and readiness acceptance lanes, the ordinary-holder witness acceptance, the scoped-lease instance hook and its fixtures, the Lease Receiver Proof's holder-era exemption), the ambient-holder refusal widened from the `Bind` name to any holder read, with Site Digest equality at two root workers (CI runs two) as the neutrality proof; if the digest differs, the retirement commit is dropped and a follow-up filed. (B) A one-time manifest regeneration reviewed by a diff-row classifier (`scripts/classify-manifest-diff.py`) with zero unexplained rows, the benchmark input catalog regenerated, the headroom ceilings re-measured as the maximum over one, two and three workers, the four runner partitions and the aggregate green, and a graph of 0 and 0. Two Opus reviews and a fixer. Branch `claude/91-ws13` from aa3c83c6.
- **Outcome.** To be recorded at merge.

## WS14: Qualification and delivery

- [ ] **Goal.** Qualify the exact final commit once, then deliver it to `memory-enhancement` only.
- Gates still to run, each once, on the exact committed candidate (they follow WS12 and WS13 and the merge of both into the delivery branch, followed by one validation run):
  - [ ] Release build of the solution: `dotnet build RetroDownfall.Arcanum.slnx -c Release`, no first-party warnings.
  - [ ] Compendium tests and Forge tests: `dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj` and the Forge test project.
  - [ ] Helper tests: the coverage-threshold, coverage-workflow, hosted-producer runner and AOT warning-script tests.
  - [ ] Coverage with the tiered gates and the mandatory uninstrumented source analysis: `./scripts/coverage.sh --threshold`, which supplies the authoritative full-runtime pass; do not run a duplicate full suite beside it.
  - [ ] The four runner partitions with SHA-bound receipts and the aggregate reconciliation (primary, additional, fixtures, serve-host).
  - [ ] Formatter and lint: `dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes`, `./scripts/align-csharp-blanklines.sh --check`, and the commands of CI's formatting and shell lint job (the formatter's own tests, shellcheck over `scripts`, actionlint over the workflows).
  - [ ] `./scripts/verify-aot-il-warnings.sh`: the publish closure free of first-party IL and AOT warnings.
  - [ ] `./scripts/verify-shipping-publish.sh --rid osx-arm64`: the exact Native AOT apphost and the in-process provider-contract smoke.
  - [ ] `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`: native provenance, hashes, symbols and compile options.
  - [ ] Live qualification with `./scripts/verify-local-ollama-aot.sh`: Ollama `gemma4:e4b` and `qwen3.8:27b` (both with vision and tools), the stop-sign vision turn, a Perplexity web-search turn, and a forced mid-sweep restart that exercises park, readmission and unknown-usage settlement against a real provider.
  - [ ] Calibration on live data: `LexicalMinimumScore` (12), the scorer constants and the 0.92 minimum cosine (`MinimumVectorCosine`, applied to the vector and hierarchy lanes); the live embedding model (qwen3-embedding:8b, 8-bit, 40,960-token window) against the 1,194-token minimum; the effect of the default 8,192-token chat window (memories of about 2.4 KiB or more are not paired). If a calibration changes a recipe digest, rerun the focused suites and one root-scoped run, and requalify only the gates the change touched.
  - [ ] A final independent Opus review of the whole branch against this plan and the spec.
  - [ ] History cleanup, then proof that the committed tree equals the tested bytes.
  - [ ] Exact-SHA CI, all 12 jobs, including both native Windows lanes (x64 and arm64) with their focused Long Rest selection and the complete producer-analysis aggregate. Measure the four hosted-producer lanes against the 1,740 s cap; add a third fixture lane if the first run exceeds 85 percent of it.
- Delivery steps, each after its gates are green:
  - [ ] `--no-ff` merge into `memory-enhancement` only (through the worktree that has it checked out), push, and prove remote ref and tree equality without redundant post-merge suites.
  - [ ] Install the retained candidate only after the owner approves the keychain prompt and takes a backup (schema 18 has no downgrade).
  - [ ] Mark #91's criteria complete and close it; tick only #91's row in #75 and leave #75 and #73 open.
  - [ ] File the deferred work as child issues of #75: G5b, G5c, G6 tier 2, G3-F6, G7c-2 and G7e (the spec lists them with one line each).
  - [ ] Ask the owner whether the Sanctum correction should also land on `main` separately.
  - [ ] Remove the managed worktrees once the delivered changes and the evidence under `$HOME/arcanum-evidence/issue91-final` are safely retained.

## Review focus carried into the final review

1. Anything that stores, captures, returns or lends a work lease or an external-effect group (the carrier ban and the entry census are the mechanical half; the review is the semantic half).
2. The Atomic Readmission SQL: parked state, partition, code class, budget and cooldown, and the absence of any `Pending` window.
3. Fresh-Authority Retire: the lease proof before the write and before the commit, and that a park a capability could readmit is never retired.
4. The Seams-and-Steps trigger coverage: every path that bypasses a trigger (foreign keys off, restore sanitation, factory reset, the erasure kernel, schema rebuild) has a refusing mechanism in the mutation matrix.
5. Unknown-usage settlement never writes an estimate or a zero, and the cap bounds repeated unknown billing.
6. The Sanctum resolution under hostile path spellings, and that AuditOnly never overrides an unreadable policy or an unrecorded breach.
7. The MCP transport's handler lifecycle under Owned Client Construction (pooling, DNS refresh, egress policy), which is outside Long Rest.
