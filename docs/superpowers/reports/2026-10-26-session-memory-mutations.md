# RV153 / RV154 Session memory mutations — implementation report

Base: `3f78b208aeda091a86bc313681d94f0fd7f92604`, tree `311f5e763c6f9de07a37b6f95481b3adf6338d14`. Branch: `codex/review-session-memory-mutations`. This is an independent review candidate, with no controller integration or original-finding closure credit. Exact committed SHA/tree and final terminal evidence are bound by the sealed manifest and final receipt, avoiding a self-referential tracked report. RV177/PDF/Task11 branches and their sealed packages remain preserved.

## RV153: atomic pin policy and truthful missing-row result

The original authenticated routes counted pinned rows before the repository's lock, then ignored SetEntryPinnedAsync's bool. Real SQLCipher HTTP tests demonstrated two scopes both committing the last available slot and pin/unpin answering200 after the row disappeared. Three ordinary behavioral RED cases preceded production edits.

Both public routes now use SetEntryPinnedWithinLimitAsync. One admitted scoped SQLite transaction and per-Session write lock own a parameterized conditional UPDATE and its missing-versus-cap classification. Already-pinned rows succeed at the cap; repeated unpin succeeds; missing/wrong-Session/concurrently deleted rows return404 EntryNotFound; a new pin over the cap returns 409 TooManyPinned. Independent SQLite writers participate in that database decision rather than relying on a process-local semaphore. Existing unsupported implementations fail closed with 503 PinUnavailable. The old bit primitive is explicitly documented as low-level and has no production caller: an archived git-grep audit finds only its interface and implementation declarations. No unrelated repository fake or root-owned BudgetMonitor source was edited.

Controls use real authenticated Host/API, real SQLCipher connections in independent scopes and a direct competing SQL writer that bypasses the local lock (not an actual second-process launch). Post-update cancellation is held by a test-only native SQL trigger/function; the request is cancelled, mutation settles, and an independent read proves rollback. A separate native RAISE(ROLLBACK) control verifies original SQLite exception type and fixed canary survive cleanup; it passed and prompted no speculative rollback change. These triggers exist only inside disposable test databases, with no production schema/Core28 changes.

## RV154: persisted assistant model owns compaction sizing

The route formerly selected DefaultModel's provider window; the service separately selected a default token profile. Eight real authenticated SQLCipher route RED cases preceded the new target behavior, with an absence/default positive control. Latest saved assistant model and reciprocal small/large defaults now demonstrate both deletion decisions and exact estimator attribution.

One captured scoped settings object is read before awaits. A metadata-only repository query selects the newest assistant answer by authoritative Sequence, excludes persisted tool-call representations, distinguishes genuine absence from missing newest attribution, and returns content-presence rather than reply text. It preserves the full saved model ID, including slashes. Empty/Unicode-whitespace newest replies, unresolved IDs, missing newest attribution despite older saved models, and conflicting applicable tokenizer/window policies refuse with 409 CompactionUnavailable and fixed recovery guidance before any token estimation or deletion. Identical policies across duplicate providers are permitted. Only genuine absence retains documented default/unconfigured8192 behavior.

Verified semantics and controller rulings are archived separately: Wizard stores lease.ResolvedModel without a provider-name prefix; placeholders and tool calls also use Assistant role, and finalization updates Content without an entry-level finalized flag. Empty-reply refusal is deliberately conservative; it does not assert an empty reply can never have completed. Current configuration policy is not proof of historical provider provenance or paid dispatch. No new provenance/finalization schema or public model override was added.

The controller further required explicit declarations for saved Familiar models: ordinary wildcard inference routing cannot establish destructive compaction applicability. A direct real-repository service test uses an isolated Familiar settings snapshot and local estimator; that snapshot never enters a chat-client factory, discovery service or process launcher. Unlisted saved models refuse; explicit declarations succeed with their current window/profile. Ordinary Familiar inference/pass-through implementation is unchanged.

A separately proved clamp seam was repaired: the duplicate-policy comparison already used the provider's 256-minimum read-time clamp, while raw nonpositive windows invoked the old explicit-window primitive's 8192 fallback. Real durable-row oracles demonstrated7 removals instead of12, depending on equivalent provider ordering. The new manual target passes the existing clamped provider window into the preserved algorithm. Positive256 and nonpositive-equivalent controls now agree. This is an adjacent consistency repair, not an extra original-finding count.

The existing destructive compression/purge/tool-group/cancellation/outcome body, from its min-message check through final result, remains byte-identical to the base. Only admission/target selection and shared method extraction changed. An archived preservation hash and both final qualification receipts verify that fact. Conditional sensitivity-purge route metadata, late-label ordinary-delete guards, partial-effect counts, protected response marking and stop semantics remain intact.

## Qualification repairs, kept distinct

The first broad run was585 passed / five failed / zero skips, retained as failed evidence. Four protected-compaction fixtures used unconfigured saved ModelUsed='test-model'; only that setup literal changed to their actually configured 'mistral:latest'. All original authorization, purge, no-displacement and partial-effect assertions are retained and pass.

The fifth failure was a stale closed Lexicon call inventory already present identically on the base: HandleSearchAsync calls SearchInspectionPageAsync. Base/current source and stale guard bytes, Git blob hashes and the failing test are archived. Exactly that expected call was added. Lexicon runtime/policy bytes remain identical to base. A source-only unknown-call probe (never compiled or executed) still fails the closed inventory and is removed exactly afterward. This is an adjacent guard correction, with no extra original-finding credit.

## TDD and excluded attempts

| Stage | Failed / passed / total |
| --- | --- |
| Pin primary RED | 3 / 0 / 3; `pin-primary-red/pin-primary-red.trx` |
| Pin first GREEN | 0 / 3 / 3; `pin-first-green/pin-first-green.trx` |
| Model primary RED | 8 / 1 / 9; `model-primary-red/model-primary-red.trx` |
| First owning GREEN | 0 / 12 / 12; `memory-first-green/memory-first-green.trx` |
| Lifetime/default/purge controls | 0 / 40 / 40; `lifetime-green/lifetime-green.trx` |
| Repaired broad gate | 0 / 590 / 590; `repaired-complete/repaired-complete.trx` |
| Final seams proved RED | 3 / 21 / 24; `final-seams-proved-red/final-seams-proved-red.trx` |
| Final seams GREEN | 0 / 24 / 24; `final-seams-green/final-seams-green.trx` |

The 14 distinct behavioral RED cases are the three pin cases, eight primary model cases and three final seams. Do not add the preliminary or repeated causal failures as additional cases.

Two test-decorator composition mistakes (internal-constructor activation and overly exact delegate type assertion) are retained/excluded. The first pin GREEN attempt hung only in its postcondition read, which incorrectly re-entered the retired count barrier; its identified process tree was terminated, that run is excluded, and the postcondition now reads independent durable state. No missing/aborted/partial TRX is called green.

The preliminary range oracle assumed the synthetic estimator's empty-user framing could fit256 tokens; even the positive control retained its deliberately synthetic 1000-token framing. That attempt is retained/excluded, not credited as product RED. The corrected oracle measures actual durable deletions: each stored message costs 1000, so all 12 stored rows must leave under a 256-token ceiling; zero/negative first-provider windows incorrectly left five rows while the positive ordering removed all 12. The original late SQLite-fault control is independently labelled and passed; it was not a failed product case.

## Causal mutation evidence

| Probe | Ordinary failed assertions |
| --- | --- |
| 01-pin-cap | 3 |
| 02-zero-row-refusal | 5 |
| 03-rollback-on-cancel | 1 |
| 04-saved-model | 7 |
| 05-empty-newest | 2 |
| 06-policy-ambiguity | 2 |
| 07-config-snapshot | 1 |
| 08-canonical-sequence | 3 |
| 09-tokenizer-target | 4 |
| 10-metadata-default | 1 |
| 11-pin-default | 1 |
| 12-unknown-lexicon-call | 1 |
| 13-raw-window-fallback | 2 |
| 14-wildcard-applicability | 1 |

All 14 probes were killed with terminal TRXs and no compiler/error/timeout substitution. Each restores all 13 owned C# files exactly. The first 12 causal-source snapshots/hashes and later two final-source snapshots/hashes are separately preserved: final clamp/Familiar changes are not retroactively attributed to the earlier bytes. The first 12 protect unchanged policy/transaction/algorithm/default contracts; final two directly prove the changed seams. The unknown Lexicon call is explicitly a source-scanner probe with --no-build, not runtime code. The synthetic file is absent at final qualification. Unaffected probes were not needlessly repeated.

## Final exact-source qualification boundary

Precommit source qualification: **596 passed, 0 failed, 0 skips, 24 actual complete classes**; full normal solution build 0 warnings / 0 errors; both house checks, Roslyn whitespace verification and git diff --check exit 0. The 13 source hashes exactly match the restored final causal baseline. Final committed-candidate build/test/style receipts are sealed after freezing this report; those bind the actual SHA/tree and terminal TRX rather than relying on a different checkout or mutable files.

The executed precommit filter had 23 planned selectors and also fully selected MandatoryGrimoireRepositoryTests through the repository-name suffix, yielding 24 actual complete classes. All intended guards were present. A receipt-only expected-count assertion caught the difference after tests/styles had returned0; its failed assertion, original selector list, original exact command/filter and actual 596-result TRX are retained unchanged. The receipt was corrected to actual 24; no run was repeated solely for that bookkeeping. Mandatory class discovery is archived, and the next committed-candidate filter explicitly names it. Do not relabel the earlier run as using that newer filter.

Actual class-by-class census and all exact command arrays, cwd/timestamps, exit receipts, source hashes and terminal TRXs are in the package. Selection covers every affected real Session/repository/compression class, mandatory receipt controls, source/compiled EF NativeAOT/direct-SQL guards, sensitive purge/authority and admission controls, strict constructors, complete error mapping/catalog, API domain and existing API client consumers.

No shipping NativeAOT publish, all-repository/full campaign CI, coverage gate, Windows runtime, live Ollama/model/embedding/provider traffic or installation/configuration/service/operator action is claimed. Runtime probes are macOS synthetic/offline with disposable encrypted SQLCipher profiles. Core28/schema, native assets, public wire DTOs, ordinary Familiar routing, Lexicon runtime, controller/main and root-owned Budget files are unchanged. Owning API, DESIGN, CLI and Compendium docs travel with the code. Independent review and root-owned final campaign gates remain required.
