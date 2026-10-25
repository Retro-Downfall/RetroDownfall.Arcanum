# U24 usability review: Arcanum.Infrastructure / Covenant (2)

Tree: worktree `codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e`. Lens: usability (operator, model as tool caller, next engineer).

Note on the unit list: `scratchpad/review/units.json` was not present when this reviewer started (the scratchpad `review/` directory held only empty `results/`, `wt/`, `reports/` folders). The file list was reconstructed from the workflow record (`U23` = "Covenant (1)", 9,964 lines; `U24` = "Covenant (2)", 7,491 lines) and from the line counts of `src/RetroDownfall.Arcanum.Infrastructure/Covenant/` in path order: the first 20 files sum to exactly 9,964 lines (through `CovenantManagementService.cs`), and the remaining 15 files sum to exactly 7,491. U24 is therefore the 15 files listed under Coverage below.

## Coverage

Read in full (every line), all in `src/RetroDownfall.Arcanum.Infrastructure/Covenant/`:

| File | Lines |
|---|---|
| CovenantMemoryReviewService.cs | 2540 |
| CovenantOperationGate.cs | 1501 |
| CovenantMutationService.cs | 793 |
| CovenantMutationService.Curation.cs | 480 |
| CovenantMutationService.Correction.cs | 212 |
| CovenantSchemaRepairExecutor.cs | 430 |
| CovenantSchemaRepairStartupRecovery.cs | 422 |
| CovenantSchemaRepairJournal.cs | 342 |
| GrimoireSchemaTransitionCoordinator.cs | 227 |
| CovenantTurnHeadProbe.cs | 159 |
| ICampaignPathMarkerLifecycle.cs | 128 |
| GrimoireSchemaTransitionHostedService.cs | 114 |
| CovenantRequestedOperationStarter.cs | 75 |
| ICampaignPathMarkerLifecycle.FullInstallationReset.cs | 40 |
| DeferredCampaignPathMarkerConnectionSource.cs | 28 |

Read to judge callers and documents (not part of the unit): `Api/Primitives/ArcanumErrorMapper.cs`, `Api/Tower/MemoryReviewEndpoints.cs`, `Infrastructure/Memory/MemoryReviewTokenCodec.cs`, `Infrastructure/Security/CovenantEnvelopeCodec.cs` + `Core/Covenant/CovenantEnvelopeContracts.cs` (decode errors), `Core/Covenant/CovenantCompiler.cs` / `CovenantWireValidation.cs` / `CovenantLimits.cs` / `CovenantSectionCapacity.cs`, `Infrastructure/Data/Covenant/CovenantMutationKernel.cs` / `CovenantQuotaGuard.cs` / `CovenantAgentErasureGate.cs` / `CovenantStore.ReadRetirementTargetAsync`, `Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs` / `GrimoireSchemaBackfillRunner.cs` / `GrimoireSchemaTransitionJournal.cs` / `GrimoireSchemaVersionChains.cs`, `Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs` (repair recovery consumer), `Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs` + `.CovenantSchemas.cs` (the model-facing sibling), `Cli/Commands/Tower/MemoryCommands.Review.cs`, `Cli/Commands/Tower/AuthoredContentReader.cs`. Docs: `Arcanum.API.md` §8.23, §8.28, §8.34; `Arcanum.Command.Reference.md` Covenant and review sections; `Arcanum.DESIGN.md` §10.17, §10.18, §10.22, §21.14, schema-transition paragraphs; `Arcanum.DEBUGGING.Human.md` item 31.

## Findings

(See the structured result for the machine-readable copy; the numbering here is the order of severity.)

### 1. Important - Covenant review refusals name neither the failing decision nor the next step, and the 5-minute life of observation and cursor tokens is never disclosed
`Infrastructure/Covenant/CovenantMemoryReviewService.cs:56` (also 60-74, 329-332, 549-552, 888-965, 958-961, 1520-1523). Category: error-message / docs-mismatch. Effort M.

- The refusals an operator sees from `memory covenant review list|apply` are three shared static `Error` instances (`InvalidToken` "invalid, expired, stale, or bound to another queue"; `StaleObservation` "A reviewed Covenant version or its review position changed..."; `UnseenObservation`) plus the codec's own text (`MemoryReviewTokenCodec.cs:48`, "invalid, expired, or has the wrong purpose"). None tells the operator to run `review list` again.
- `DecodeObservations` (888-924) and `ValidateObservedTarget` (926-965) return the shared instance without the decision ordinal or event sequence, so a 50-decision batch with one stale, duplicated or non-compiling decision names none of them. The compile catch (958-961, 1520-1523) forwards `ArgumentException.Message` verbatim (see finding 4).
- Lines 329-332 and 549-552 say "Proposed content must be retired or confirmed separately". In this tool `Confirm` is acknowledgment only (API §8.34) and never promotes a proposal; the sentence names no verb that does (`memory covenant set --campaign <id> <key>`).
- `MemoryReviewLimits.TokenLifetime` is 5 minutes (`Core/Memory/MemoryReviewTokenFacts.cs:15`) and `Issue`/`Read` (`MemoryReviewTokenCodec.cs:411-490`) apply it to observation, cursor and plan tokens alike; the key is per process, so a host restart also kills them. `CovenantReviewItemDto` has no expiry, `WriteCovenantReviewPage` (Cli `MemoryCommands.Review.cs:405-421`) prints only the token, the Command Reference example is `"observationToken": "token from review list"` (line 745) and API §8.34 / DESIGN §21.14 state the five minutes only for the plan token. The sibling operator write path is clear: `CovenantEnvelopeErrors.Expired` = "This token has expired. Prepare the operation again." (`Core/Covenant/CovenantEnvelopeContracts.cs:488-490`), and the CLI's erasure verbs add their own expiry hint (`MemoryCommands.Erasure.cs:623`); the review verbs add nothing.
- Every successful apply bumps the lane's marker `Revision` (`AdvanceMarkerAsync`, 1891-1919), and every observation, cursor and plan token binds that revision (`MatchesCommon`, 1042-1070). A request carries exactly one action, so a review that retires 3 proposals and confirms 47 needs two applies, and the second apply's tokens (all taken from the same list) answer `MemoryReview.StaleObservation` with the sentence above. The Command Reference only says "Refresh rather than reusing a token after the version changes" (line 734). The probe hit this itself: after one `Confirm` apply, preparing a different item from the same list failed with `StaleObservation`.
- Scenario: operator lists a page of proposals, reads them, edits the decision file and runs `review apply` 8 minutes after the list: 400 `MemoryReview.InvalidToken` with no hint that age is the cause or that a fresh `review list` is the fix; a cursor printed on page 1 dies the same way.
- Fix: put "decision #n (event e)" in every per-item refusal; say that any apply in the lane invalidates the other tokens (or stop binding `MarkerRevision` into observation tokens when the observed event itself is still unreviewed and current); end each token/stale refusal with the recovery ("Run 'arcanum memory covenant review list' again"); add an `ExpiresAtUtc` to `CovenantReviewPageDto` and print it in the CLI footer; document the 5-minute lifetime of observation and cursor tokens in API §8.34 and the Command Reference; consider a longer lifetime for observation/cursor tokens than for the plan token.

### 2. Important - The operator set/correct preflight approves writes the commit will refuse for Section capacity or a retired head
`Infrastructure/Covenant/CovenantMutationService.cs:194` (`PrepareAsync`; commit-side `Data/Covenant/CovenantMutationKernel.cs:145,525-529`, `CovenantQuotaGuard.cs:236,283`). Category: api-contract. Effort M.

- `PrepareAsync` measures the effect and the epochs only; `CovenantMutationPreflightDto` carries no capacity or lifecycle fact. The commit enforces Section capacity for operator batches too (`CheckCanonicalCapacityAsync` -> `CovenantSectionCapacity.Refusal`: 4,096 rendered bytes and 64 entries (32 for the Campaign Proposed Section) per Section, `Core/Covenant/CovenantLimits.cs:9-19`, while one authored entry may be 2,048 bytes) and refuses a set of a retired head without `Reactivate` ("This Covenant head is retired and reactivation was not requested.").
- API §8.28 ("Both revisions on the preflight") states the principle ("the refusal arriving after the operator had approved it"); `CovenantQuotaGuard.cs:279-283` says a second copy of the comparison "is how a proposal comes to be accepted at the tool and refused at the commit, and a refused commit discards the operator's answer"; the agent path pre-checks exactly this (`ArcanumInternalToolServer.CovenantTools.cs:436-500`, `RefuseWhatTheCommitWouldRefuseAsync`), and `correct` pre-checks its target (`ResolveTargetAsync`). `set`/`retire` do neither.
- Scenario: an operator with a handful of 1 KB preferences runs `memory covenant set`, reads the preflight (shows byte cost and effect), confirms, then gets 409 `Covenant.CapacityExceeded` (executed probe: prepare succeeded with `localDecision=HeadCreated`, commit answered "This mutation would render the GlobalConfirmed Section at 4196 bytes, past its 4096-byte bound.") with no hint to retire or shorten another entry; or re-sets a retired key without `--reactivate` (executed probe: prepare succeeded at revision 2 = expected 2, commit answered `Covenant.LifecycleConflict` "This Covenant head is retired and reactivation was not requested.") and is refused after confirming.
- Fix: in `PrepareAsync` read the scope/section occupancy through the existing `ICovenantStore.ReadQuotaSnapshotAsync` / `ReadSectionOccupancyAsync` and apply `CovenantScopeCapacity.Refusal` / `CovenantSectionCapacity.Refusal` (return the same error at prepare, with a remediation sentence); refuse `Lifecycle == Retired && !reactivate` at prepare with the commit's text plus "pass --reactivate".

### 3. Important - A draining schema transition is invisible to the operator
`Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:103` and `GrimoireSchemaTransitionCoordinator.cs:146-170`. Category: diagnostics. Effort M.

- The only progress signal is `"A Grimoire schema transition pass advanced {Runs} run(s) through {Batches} batch(es) and {Rows} row(s)."` where `{Runs}` is `RunsSeen` (every run in flight, including ones that failed or did nothing), with no tier, sweep name, from/target version, cursor or cumulative `BackfillRowsProcessed`. The failure line (coordinator 149-152) names the tier but not the sweep, and the durable `LastDurableErrorCode` is the single constant `Grimoire.Schema.TransitionPassFailed` for every cause (line 60). `GrimoireSchemaBackfillRunner` and the journal have no logger.
- Nothing reads `grimoire_schema_transitions` for an operator: `grep` shows readers only in the coordinator, installer, planner, runner and `BackupRestoreSchemaDrain`; `arcanum doctor` has no schema-transition check (Command Reference diagnostic table); `memory status` shows only the Covenant block's diagnostic code (`CovenantManagementService.cs:570`, "Grimoire.Schema.TransitionIncomplete"). DEBUGGING.Human.md item 31 tells an engineer to read the encrypted journal and "break in `RunOnceAsync`".
- Cost: Core has eight chained sweeps (`GrimoireSchemaVersionChains.cs` Backfills for steps 2,3,5,7,9,12,16,18), each at 128-256 rows per batch, 16 batches per pass, one pass per 15 s: at most about 200-270 rows per second, so an installation with a million rows in the swept tables needs more than an hour at that bound (an estimate from the constants, not a measurement). The tier reports `TransitionIncomplete` for all of it and DESIGN says a Core tier mid-run stands its dependents down (concretely, for an installation coming from Core below v11, Lexicon curation answers 503 "temporarily unavailable while the Core schema transition completes", `LexiconService.cs:65`, API §8.33). The operator cannot tell progress from a stall, and DEBUGGING.Human.md says the batches are "a few seconds apart" where the code waits 15 s (5 min after any pass that did not advance).
- Fix: log tier, sweep name, `CompletedThroughVersion -> TargetVersion` and cumulative rows per run (and the sweep name on failure); count runs that actually advanced; add a read-only doctor check (`grimoire.schema_transition`, Warn with sweep name and rows processed) and a line in `memory status`; correct the cadence statement.

### 4. Minor - `Covenant.InvalidContent` from the operator write and review paths names an internal parameter, not the offending character, and the rule is undocumented
`Infrastructure/Covenant/CovenantMutationService.cs:731-743` (`TryCompile`; same pattern in `CovenantMemoryReviewService.cs:954-961` and `1516-1523`). Category: error-message / docs-mismatch. Effort S.

- `TryCompile` returns `exception.Message` of the compiler's `ArgumentException`. The BCL appends "(Parameter 'authoredContent')", which is not a wire field (the body field is `content`/`replacementContent`), and the compiler (`Core/Covenant/CovenantCompiler.cs:156-165`) says only "contains a Unicode 17 Format scalar" or "a prohibited control scalar", with no scalar value or offset. Format scalars include U+200B, U+200D (emoji joiner), U+00AD, U+200E/F and U+FEFF; they arrive with web and PDF pastes and emoji sequences.
- API §8.28 "Frozen request bounds" lists only 1-2,048 UTF-8 bytes; neither it, the Command Reference nor DESIGN says Format scalars, control characters and whitespace-only text are refused.
- The reserved typed defaults (`defaults.inference.temperature`, `top-p`) refuse any whitespace, including the trailing newline `echo 0.7 | arcanum memory covenant set ...` adds, with "A Covenant operational default requires a finite bounded unsigned decimal scalar." (`CovenantOperationalDefaultCatalog.cs:181`), which does not mention whitespace; only a Command Reference example (`printf '0.25' |`, line 809) tells the operator.
- Probe (console probe over the built `Core` DLL, `scratchpad/review/probe-u24-usability`): every one of ZWJ family emoji, zero-width space, soft hyphen, BOM, LRM returns "Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')"; NUL returns "...prohibited control scalar. (Parameter 'authoredContent')"; whitespace-only returns "...cannot be empty or whitespace-only. (Parameter 'authoredContent')".
- Fix: map the compiler failure to a typed reason without the BCL suffix, name the wire field, the code point (`U+200B`) and the 1-based scalar offset (neither is content a log would mind), and add the rule to API §8.28 and the `set`/`correct` Command Reference rows.

### 5. Minor - Review confirmation screen identifies bulk targets by GUID only; the key is omitted though the server has it
`Infrastructure/Covenant/CovenantMemoryReviewService.cs:967-975` (`PlanItem`); rendering `Cli/Commands/Tower/MemoryCommands.Review.cs:366-373`. Category: cli-ergonomics. Effort S.

- `PlanItem` builds `(Sequence, Canonical(EntryId), Canonical(VersionId), IsCurrent, Origin, "Turn:<guid>/Tool:<id>", "Campaign:<guid>/<lane>")` from a `CovenantReviewEvent` that carries `Key`, and the CLI prints exactly those fields. The operator approving a bulk `Retire`/`Correct`/`Pin` sees "event 12: 5b8c... @ 9f2e... (Campaign:.../Proposed; AgentProposed; Turn:.../Tool:...)" and must trust that the opaque observation tokens in a hand-built decision file map to the keys they meant.
- Fix: put the key into the plan item (the `Source`/`Scope` string or a trailing optional `Label` field on the shared DTO) and print it.

### 6. Minor - `propose_covenant` refuses uncompilable input with the wrong code and a sentence the model cannot act on (outside the unit, found while tracing TryCompile)
`Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs:104-119` with schema text `ArcanumInternalToolServer.CovenantSchemas.cs:38-49`. Category: error-message. Effort S.

- Any `ArgumentException` from the compiler (bad key grammar, over 2,048 bytes, whitespace-only, Format scalar) becomes `Covenant.InvalidScope` "propose_covenant could not compile that key and content under the Covenant text policy." The code names a different problem (scope), the text does not say which of key/content failed or what the policy is, the tool schema gives the model neither the key grammar (`[a-z0-9][a-z0-9._-]{0,127}`) nor the size cap, and the log records only `ArgumentException`.
- Fix: return `Covenant.InvalidKey` / `Covenant.InvalidContent` with a fixed reason per cause (these are not content), state the grammar and cap in the schema descriptions, log the reason category.

### 7. Minor - Hash casing differs between the review list and every other Covenant read of the same DTO
`Infrastructure/Covenant/CovenantMemoryReviewService.cs:1133,1419` versus `CovenantManagementService.cs:1091` and `CovenantMutationService.cs:792`. Category: api-contract. Effort S.

- `CovenantHeadDto.AuthoredHash/RenderedHash/ProvenanceDigest` and `CovenantSourceDto.ContentHash` are lower-case hex from `/list`, `/detail`, `/sources` and the mutation DTOs (`Convert.ToHexStringLower`) but UPPER-CASE in `review list` (`Convert.ToHexString`). A script comparing `show --json` `renderedHash` with `review list --json` `current.renderedHash` as strings sees them differ; the parse side is case-insensitive so `correct --target-hash` hides it. No test or doc pins the case.
- Fix: use the lower-case helper in the review service and pin it in `CovenantMemoryReviewServiceTests`.

### 8. Minor - Review apply diagnostics drop the cause, log nothing on replay integrity failures, and log a retryable busy at Error
`Infrastructure/Covenant/CovenantMemoryReviewService.cs:499-528` and `2097-2297`. Category: diagnostics. Effort S.

- `MapApplyFailure` logs `failure.GetType()` only. For `InvalidDataException` the messages are app-authored constants ("Covenant canonical state is absent.", "The Covenant review marker could not be created.", "A Covenant head update did not produce its review event.") and are the only thing that says which state is damaged; they are dropped. Of the 14 places that return `IntegrityFailure` (245, 519, 1550, 1712, 2128-2278) only 519 logs. The ten in `ReadReplayAsync` return a 500 `MemoryReview.IntegrityFailure` with no log line and no request id, so a tampered or generation-mismatched receipt cannot be diagnosed from outside, and the provenance-count check in `ListAsync` (242-246) fails the whole page of up to 50 items for one damaged version without saying which, so the lane cannot be reviewed at all. The busy case, which the API documents as a retryable 503, is logged at Error.
- Fix: log the app-authored message for `InvalidDataException`, add a fixed reason code per replay failure site plus `RequestId` at Warning, and log the busy give-up at Warning.

### 9. Minor - The gate's refusal says "closing" for a closure that may be kept closed indefinitely, and never names the operation
`Infrastructure/Covenant/CovenantOperationGate.cs:633-638` (also 703-706, 786-789, 965-967). Category: error-message. Effort S.

- `AcquireOrdinary` answers every blocked lease with `Covenant.Unavailable` "A Covenant operation is closing this scope, so no new lease may be taken over it." A closure whose exclusive lease completed with `KeepClosed` (`CompleteExclusive`, 1235-1241) stays installed for the life of the process, so the operator sees "closing" for a state that needs recovery. The closure knows `Owner.Operation` and whether `LiveRegistration`/`AcquisitionInProgress` is set, so it could say "an erasure is in progress, retry in a moment" versus "a reset was kept closed pending recovery". `ResumeOrAcquireInstallationAsync` ("Another Covenant recovery owner already holds this scope closed.") has the same gap.
- Fix: include the operation name and in-progress/kept-closed in the message (the gate logs nothing by design, so the message is the only carrier).

### 10. Minor - Gate class remarks say acquisition touches no database; the Campaign-exclusive paths read it and can throw
`Infrastructure/Covenant/CovenantOperationGate.cs:14-17` versus 553-581 and `CovenantCampaignScopeProbe.cs:46,101,109`. Category: docs-mismatch. Effort S.

- The remark says "Acquiring and revalidating touch memory only: no secret store, no database". `AcquireCampaignExclusiveAsync`, `AcquireProtectedTransferAsync` (Campaign scope) and `AcquireEntryErasureAsync` (Campaign entry) call `RequireLiveCampaignAsync`, which opens a scope and runs two SELECTs (`Campaigns`, `owner_deletion_events`) and throws `GrimoireMaintenanceUnavailableException` (probe line 101) when admission is closed, instead of returning a `Result`. The probe's own remarks call it "the cold exclusive-acquisition path".
- Fix: restate the remark (ordinary leases are memory only; the Campaign-bound exclusive acquisitions do one live-Campaign read and can throw the maintenance exception).

### 11. Minor - Startup refusal "See logs for recovery steps" has no log behind it when the repair journal cannot be read (dormant today)
`Infrastructure/Covenant/CovenantSchemaRepairJournal.cs:105-112,182-192`, `CovenantSchemaRepairStartupRecovery.cs:126-137`, consumer `Hosting/GrimoireDatabaseBootstrapper.cs:1227-1230,1340-1343`. Category: diagnostics. Effort S.

- `TryReadActiveAsync` turns any exception, two live intents and any malformed row into the same `ReadFailure()` ("could not be read safely") with no log; `PrepareBeforeEffectsAsync` forwards it, and the bootstrapper discards the `Error` and throws `GrimoireDatabaseUnavailableException` "Protected Covenant startup recovery could not prove one durable owner. See logs for recovery steps." The host does not start and no log line carries the reason. The `InvalidOperationException` arm of `PreparationFailure` also drops its message although its doc comment says the operator needs it.
- `KeptClosed` warnings (`Kept`, 397-406) name the stage and code but not the intent's operation id, action or phase, and say the journal "stays active for the next start" even for deterministic stages (`catalog-moved`, `canonical-invalid`, `evidence-selected-keep-closed`) whose next start repeats; with `ICovenantMaintenanceService` unregistered there is no operator verb to clear it. `CovenantSchemaRepairExecutor.RepairAsync` returns raw `SqliteException.Message` (195, 265) where `Kept` says driver messages are not logged.
- Dormant: the only creator of intents (`CovenantMaintenanceService.CommitPreparedAsync`) is registered nowhere, so no production database holds a repair journal. Worth fixing when the port is wired.
- Fix: log the `Error` (code + stage) before throwing at the bootstrapper, include operation id/action/phase in `Kept`, and make the keep-closed warning say whether a restart can change the outcome.

### 12. Minor - Documentation attached to the wrong member in `CovenantTurnHeadProbe`
`Infrastructure/Covenant/CovenantTurnHeadProbe.cs:118-133`. Category: docs-mismatch. Effort S.

- Two stacked `<summary>`/`<remarks>` blocks precede `ProbeScopeAsync`; the first ("Measures the turn's own Campaign Section") describes `ProbeSectionAsync`, which has no documentation, so the scope-quota probe is documented as a Section probe and the Section probe not at all.
- Fix: move the first block onto `ProbeSectionAsync`.

### 13. Minor - `GrimoireSchemaTransitionPassOutcome.Advanced` does not mean what its remarks say, and can spin a stuck run every 15 seconds while logging "advanced"
`Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs:12-24,116-124,169`. Category: docs-mismatch. Effort S.

- The remarks say `Advanced` means "there was work and some of it was done". The coordinator sets it whenever a run exists with a null `BackfillName` (line 120) before any work, and whenever the runner counts a batch that it then rolled back (`GrimoireSchemaBackfillRunner` finalization-unhealthy arm returns `StepComplete: false` with `BatchesRun > 0`). A non-Core run with a null `BackfillName` that the build cannot resume (`TransitionUnresumable`: `GrimoireSchemaEvolutionPlanner.Resume` refuses a journal whose target fingerprint or version differs from this build's head, and `GrimoireSchemaInstaller.Refuse` returns a failed result instead of throwing for Covenant tiers) therefore reports `Advanced = true` on every pass, the hosted service waits 15 s instead of 5 min, re-runs the installer for all tiers each time, republishes availability, and logs "advanced 1 run(s) through 0 batch(es)". Trace, not executed.
- Fix: set `Advanced` only from `progress.BatchesRun > 0 && StepComplete or cursor moved`, or from a converge result that changed a tier's health or version.

### 14. Minor - Human-mode `review list` omits the proposed content (outside the unit; confirm intent)
`Cli/Commands/Tower/MemoryCommands.Review.cs:405-421`. Category: cli-ergonomics. Effort S.

- `WriteCovenantReviewPage` prints key, origin, scope/lane, source, version and token but not `item.CompiledContent`, which the page DTO carries and `--json` emits. `memory covenant show` and `list` are content-free by rule, so for the operator in human mode there is no CLI view of what a proposal says. Saga and Lexicon human lists omit their text the same way, so this may be deliberate (terminal scrollback); if it is, the Command Reference should say that `--json` is the way to read the content under review.


## Checked and found sound

- Error-code to HTTP-status mapping for every code this unit emits (`Covenant.Unavailable`, `MaintenanceFailed`, `ManualRecoveryRequired` -> 503; `StaleSnapshot`, `LifecycleConflict`, `CapacityExceeded` -> 409; `InvalidContent`, `InvalidScope` -> 400; `ForbiddenAuthority` -> 403; `MemoryReview.InvalidToken` -> 400, `StaleObservation`/`UnseenObservation`/`RequestReuse` -> 409, `IntegrityFailure` -> 500, `Covenant.WriteFailed` -> 500): read `ArcanumErrorMapper.cs` and compared with API.md §8.23 and §8.34 text; they agree.
- Apply failure mapping in `CovenantMemoryReviewService.MapApplyFailure` (busy -> 503 `Covenant.Unavailable` "retry the same request", missing state -> 500 `MemoryReview.IntegrityFailure`, anything else -> 500 `Covenant.WriteFailed` "Nothing was written"): matches API.md §8.34 paragraph "A Saga or Covenant apply whose transaction could not commit...".
- Receipt-first replay on `set`, `retire`, `correct` and `curate` (replay resolved before token decode, idempotency conflict on a changed request, literal `Security.IdempotencyConflict` equals `ErrorCodes.Security.IdempotencyConflict`): matches DESIGN §10.22.3 and API §8.28 "Idempotency".
- Both revisions on the preflight (`currentLaneRevision` read from the head, `expectedLaneRevision` echoed): matches API §8.28 "Both revisions on the preflight".
- Correction target refusals (`ResolveTargetAsync`: absence, lifecycle, identity, content, in that order, all `StaleSnapshot`, malformed hash `Validation.InvalidBody`): matches API §8.28 "A malformed target hash is a malformed body" and the order stated in the code comment.
- Covenant envelope (preflight) token expiry text is actionable ("This token has expired. Prepare the operation again.", `CovenantEnvelopeContracts.cs:488-490`), so the preflight token path is not part of finding 1.
- `CovenantTurnHeadProbe.ResolveRetirementPreflightAsync`: a pinned lane and an erased key deliberately share one sentence ("managed by the operator in this scope") so an agent cannot tell them apart; key-trouble uses the standard `MemoryErasure.KeyLost`/`KeyUnavailable` errors; store's absent-head text ("has no live head in that lane, so there is nothing to retire") is actionable for the model.
- Gate drain refusal (`MaintenanceFailed`, 503) states that in-flight work was cancelled and the operation did not start; the gate's single fixed warning log is documented and intentional.
- Maintenance port absence: `ICovenantMaintenanceService` is registered nowhere and `CovenantSchemaRepairJournal.CommitPreparedAsync` has exactly one caller (`CovenantMaintenanceService`), so DESIGN §10.17 / API §8.28 / Command Reference statements that repair, rebuild and reinitialize cannot be started are true. Consequently the schema-repair startup recovery is dormant in production.
- `GrimoireSchemaTransitionHostedService` is gated on the journal and not on availability, as DESIGN §21 says; cadence constants are 15 s while a run advances and 5 min otherwise.
- `CovenantRequestedOperationStarter` only forwards the identity triple and passes every optional correlation field as null, as its remarks say.
- `ICampaignPathMarkerLifecycle` partials and `DeferredCampaignPathMarkerConnectionSource` are contract/adapters with no operator-visible text; their DESIGN cross-references (§10.12, §10.19.7) resolve to real sections.


## Probes

### Probe 1: compiler messages as the operator and review paths return them (executed)
Console project `scratchpad/review/probe-u24-usability/Program.cs` referencing the built `RetroDownfall.Arcanum.Core.dll` of `ac0d2d8e`; output saved in `scratchpad/review/probe-u24-usability/compile-probe.txt`.

```
zero-width joiner (family emoji): ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')
zero-width space (web paste):     ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')
soft hyphen (PDF paste):          ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')
BOM at start:                     ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')
left-to-right mark:               ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')
NUL control:                      ArgumentException: Covenant authored content contains a prohibited control scalar. (Parameter 'authoredContent')
whitespace only:                  ArgumentException: Covenant authored content cannot be empty or whitespace-only. (Parameter 'authoredContent')
lone surrogate:                   ArgumentException: Covenant authored content must contain valid Unicode scalar values. (Parameter 'authoredContent')
2049 bytes:                       ArgumentException: Covenant authored content cannot exceed 2048 strict UTF-8 bytes. (Parameter 'authoredContent')
key "Response Style":             ArgumentException: A Covenant key must match [a-z0-9][a-z0-9._-]{0,127}. (Parameter 'value')
typed default "0.7\n":            ArgumentException: A Covenant operational default requires a finite bounded unsigned decimal scalar. (Parameter 'content')
```
`CovenantMutationService.TryCompile` (731-743) and the two review-service catch blocks return `exception.Message` unchanged. This is the evidence for finding 4 and, for the model-facing sibling, finding 6 (the sibling replaces every one of these with one fixed sentence).

### Probes 2 and 3: in-tree integration probes (executed)
Two probe tests were written in a scratch worktree (`scratchpad/review/wt/U24-usability`, detached at `ac0d2d8e`), inside the existing `CovenantMemoryReviewServiceTests` and `CovenantMutationServiceTests` classes so they reuse their real SQLCipher fixtures: `U24_usability_probe` (review list/prepare/apply: plan item contents, one-of-three stale, six-minute-old tokens, bulk Correct with one bad decision, tampered receipt replay log count, deleted state row log lines) and `U24_operator_preflight_probe` (Section overflow: prepare vs commit; retired key without reactivate: prepare vs commit; content errors through `PrepareSetAsync`). Results, if the build finished, are in `Executed in the scratch worktree `scratchpad/review/wt/U24-usability` (detached at `ac0d2d8e`; probe test methods copied to `scratchpad/review/probe-method.cs.txt` and `probe-method2.cs.txt`) with `dotnet test --no-build --filter "FullyQualifiedName~U24_usability_probe|FullyQualifiedName~U24_operator_preflight_probe" </dev/null`: both passed. Raw output: `scratchpad/review/probe-u24-usability/review-probe-final.txt` and `mutation-probe-run1.txt`.

Review service (`CovenantMemoryReviewServiceTests` harness, real SQLCipher fixture, fake clock):
```
== LIST (what the operator sees per item) ==
event 3 key=units.metric content="- units.metric: "Use metric units."
" tokenLen=218 headHashes: rendered=3CA948FFC0E18D4A23C141EA099249523BFFFE94996DF1F6B7309B19EB1F230B authored=FA556F0BC7C2A7AE95C9CB8275F69DBCFB85C28BABE828786FB60EC677C4FAB0 prov=A2A5A22666DAC973F5B4E3679360A7A2CA5018E5E77E525782B13DEB1850E86B
event 2 key=tone.formal content="- tone.formal: "Use a formal tone."
" tokenLen=218 headHashes: rendered=D9B25C8F1EB968E8F16BC541803FC8AEA34724A5391F80DCCAE139C22CDE6548 authored=5ACD25BEA9338D210399072AC5728AAD9A91007B602897E004A0AD97D033BD4C prov=A2A5A22666DAC973F5B4E3679360A7A2CA5018E5E77E525782B13DEB1850E86B
event 1 key=response.style content="- response.style: "Prefers concise answers."
" tokenLen=218 headHashes: rendered=C3252B6E404D0983B0336674CCB3B3116078BF55657954F04CBCA4D2EAD4D300 authored=5428A2C645C8DC6966A064F970E5D605EDE8CC13D9EED7E1B4F2128D603F3B25 prov=A2A5A22666DAC973F5B4E3679360A7A2CA5018E5E77E525782B13DEB1850E86B
== PLAN ITEMS (the confirmation screen's data) ==
event 3: subject=08e3aba1-409c-4fda-85af-5e78e1779e6e version=23aedab1-0de3-4718-80c2-ad3f3bd53c19 scope=Campaign:247a1767-7fc1-4c93-bc17-d021e52ff665/Proposed origin=AgentProposed source=Turn:1706f286-4ca3-4d51-ae53-1804678f56df/Tool:call-1 :: contains key? False
event 2: subject=bf47dcee-6a38-443c-9092-bece9754537c version=af806249-1306-494a-9ae8-f9ac4337f97a scope=Campaign:247a1767-7fc1-4c93-bc17-d021e52ff665/Proposed origin=AgentProposed source=Turn:9a9bd03c-6453-4f30-b733-2e33377890f3/Tool:call-1 :: contains key? False
event 1: subject=5f197655-9321-40d2-8cbb-2edc6bc35c9b version=c67ec4c5-55c9-4738-9077-ec28be57bcd6 scope=Campaign:247a1767-7fc1-4c93-bc17-d021e52ff665/Proposed origin=AgentProposed source=Turn:8acce84c-9273-43fc-84c7-71fe65c906ee/Tool:call-1 :: contains key? False
== ONE OF THREE DECISIONS SUPERSEDED ==
FAILED code=MemoryReview.StaleObservation message="A reviewed Covenant version or its review position changed before this operation could commit."
== OBSERVATION TOKENS 6 MINUTES OLD ==
FAILED code=MemoryReview.InvalidToken message="The memory-review token is invalid, expired, or has the wrong purpose."
cursor garbage: FAILED code=MemoryReview.InvalidToken message="The memory-review token is invalid, expired, or has the wrong purpose."
== BULK CORRECT, DECISION #2 OF 3 HAS A ZERO-WIDTH SPACE ==
FAILED code=Validation.InvalidBody message="Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')"
== APPLY ==
success=True
== REPLAY OF A TAMPERED RECEIPT ==
FAILED code=MemoryReview.IntegrityFailure message="Durable Covenant review state failed its integrity contract." logEntriesAdded=0
== TOKEN OF A DIFFERENT ITEM FROM THE SAME LIST, AFTER ONE APPLY IN THAT LANE ==
FAILED code=MemoryReview.StaleObservation message="A reviewed Covenant version or its review position changed before this operation could commit."
== FRESH LIST + PLAN FOR THE DAMAGED-STATE STEP ==
success
== COVENANT STATE ROW DELETED, THEN APPLY ==
FAILED code=MemoryReview.IntegrityFailure message="Durable Covenant review state failed its integrity contract."
LOG [Error] Covenant memory review apply found damaged review state: System.IO.InvalidDataException. | exception= message=
```
Reading it against the findings:
- LIST prints `rendered=3CA948FF...` upper-case; `/detail`, `/list`, `/sources` return the same field lower-case (finding 7).
- PLAN ITEMS: subject/version GUIDs, `Campaign:<guid>/Proposed`, `Turn:<guid>/Tool:call-1`; the key (`response.style`) appears nowhere (finding 5).
- ONE OF THREE DECISIONS SUPERSEDED, SIX-MINUTE-OLD TOKENS, BULK CORRECT: the refusals name no decision and no next step (finding 1); the bulk Correct failure is the BCL text with `(Parameter 'authoredContent')` and no decision number (findings 1 and 4).
- TOKEN OF A DIFFERENT ITEM FROM THE SAME LIST, AFTER ONE APPLY IN THAT LANE: `StaleObservation` although that item was untouched (finding 1; an earlier run of the probe tripped over this by itself).
- REPLAY OF A TAMPERED RECEIPT: `MemoryReview.IntegrityFailure` with `logEntriesAdded=0` (finding 8).
- COVENANT STATE ROW DELETED, THEN APPLY: the only log line is `[Error] Covenant memory review apply found damaged review state: System.IO.InvalidDataException.`; the app-authored message ("Covenant canonical state is absent.") is not in it (finding 8).

Operator write path (`CovenantMutationServiceTests` harness):
```
filler.1: prepare=success commit=success bytes=1049
filler.2: prepare=success commit=success bytes=1049
filler.3: prepare=success commit=success bytes=1049
== OVERFLOWING SET ==
prepare: success byteCost=1049 effect.localDecision=HeadCreated
commit (after the operator confirmed): FAILED code=Covenant.CapacityExceeded message="This mutation would render the GlobalConfirmed Section at 4196 bytes, past its 4096-byte bound."
retire filler.1: success=True
== SET OF A RETIRED KEY WITHOUT --reactivate ==
prepare: success currentLaneRevision=2 expected=2
commit: FAILED code=Covenant.LifecycleConflict message="This Covenant head is retired and reactivation was not requested."
== CONTENT ERRORS AS RETURNED BY PrepareSetAsync ==
zero-width space: FAILED code=Covenant.InvalidContent message="Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')"
soft hyphen: FAILED code=Covenant.InvalidContent message="Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')"
whitespace only: FAILED code=Covenant.InvalidContent message="Covenant authored content cannot be empty or whitespace-only. (Parameter 'authoredContent')"
3000 bytes: FAILED code=Covenant.InvalidContent message="Authored Covenant content must be between 1 and 2048 UTF-8 bytes."
```
Reading it against the findings: the overflowing `set` and the `set` of a retired key both pass `PrepareSetAsync` (a token is issued, `localDecision=HeadCreated`) and are refused only by `SetAsync`, after the operator would have confirmed (finding 2). The 3,000-byte content is refused by wire validation with a clear sentence ("must be between 1 and 2048 UTF-8 bytes"); the Format scalar and whitespace-only cases fall through to the compiler text with `(Parameter 'authoredContent')` (finding 4).

### Trace-only evidence (execution not feasible or not needed)
- Finding 1 (token lifetime): `MemoryReviewTokenCodec.Issue` stamps `expiresAt = issuedAt + 5 min` for every purpose and `Read` rejects with the one `InvalidToken` text when `now >= expiresAt`; the key is `ProcessKey = RandomNumberGenerator.GetBytes(32)` per process.
- Finding 3 (transition visibility): `grep` for `grimoire_schema_transitions|GrimoireSchemaTransitionJournal` in `src` (readers: coordinator, installer, planner, runner, `BackupRestoreSchemaDrain`); the doctor check table in the Command Reference has no schema-transition row.
- Finding 11 (startup refusal with no log): `GrimoireDatabaseBootstrapper.cs:1227-1230` `if (prepared.IsFailure) { throw ProtectedRecoveryUnavailable(); }`; no `Log.*` between the failing call and the throw; the exception text is the constant at 1342-1343.
- Finding 13 (`Advanced`): line 120 `advanced = true; continue;` for a null `BackfillName`; `GrimoireSchemaBackfillRunner.AdvanceAsync` returns `StepComplete: false` with `BatchesRun >= 1` after rolling the batch back when finalization is unhealthy.

### Cross-lens observation (not counted as a finding; documented limitation)
`GrimoireSchemaEvolutionPlanner.Resume` (`Data/Schema/GrimoireSchemaEvolutionPlanner.cs:142-163`) refuses a journal whose `TargetVersion` or head fingerprint differs from the running build's head as `TransitionUnresumable`, and DEBUGGING.Human.md #31 says the repair is "restore a .arcbackup this build took, or start fresh". Combined with finding 3 (a draining Core sweep can last an hour or more on a large database with no visible progress), installing a newer build before a run has drained leaves the Core tier refusing startup. This is documented, so it is recorded here only for the synthesis to weigh alongside finding 3.

### Probe status
Both integration probes and the console probe ran to completion. The scratch worktree is removed at the end of the review; the probe test sources are kept in the scratchpad. Nothing was changed in `codebase-review`.

