# U27 (Arcanum.Infrastructure, Data (3)) - USABILITY review

Tree: worktree `codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e`. Read-only; probes ran in a scratch worktree
(`scratchpad/review/wt/U27-usability`, removed afterwards). Probe sources are saved in `reports/C/U27-probes/`.

## Unit identification (units.json was absent)

`scratchpad/review/units.json` did not exist when this review started (only the empty `reports/`, `results/`, `wt/` directories
did; there was no `publish/` directory either). The unit was reconstructed from the figures in the task: the top-level
`Infrastructure/Data/*.cs` files in ordinal path order, packed greedily to a 12,000-line cap, give chunk 3 =
`DataRetentionService.cs` .. `GrimoireInstallationMaintenancePaths.cs`, which sums to exactly 11,953 lines (8099 + 182 + 31 +
128 + 95 + 6 + 589 + 2604 + 187 + 32). That matches the task's "about 11953 lines", so it is almost certainly the intended unit.

Files, all read in full (every line):

| File | Lines |
|---|---|
| Data/DataRetentionService.cs | 8099 |
| Data/DataRetentionSweepHostedService.cs | 182 |
| Data/EfSaveChangesRetry.cs | 31 |
| Data/ExactUsdText.cs | 128 |
| Data/FtsMatchQuerySanitizer.cs | 95 |
| Data/GrimoireConnectionAcquisitionRouteAttribute.cs | 6 |
| Data/GrimoireConnectionAdmissionContracts.cs | 589 |
| Data/GrimoireConnectionAdmissionGate.cs | 2604 |
| Data/GrimoireEntitySql.cs | 187 |
| Data/GrimoireInstallationMaintenancePaths.cs | 32 |

Owning docs read: DESIGN section on retention (lines 711-766, 1811), API.md retention routes and error table (lines 757-789,
905-909), Command Reference `data` and `operation` sections (lines 1105-1160, 1211-1231), Compendium retention keys. Callers read to
judge the operator surface: `DataRetentionEndpoints.cs`, `DataRetentionCommands.cs`, `CliCommandTree.Data.cs`,
`LongRunningOperationReconciler.cs`, `LongRunningOperationStore.cs` (single-flight, `StartupRecoveredAttention`),
`CovenantErasureCoordinator.cs` (drain call and abort), `ArcanumErrorMapper.cs`, `ArcanumInternalToolServer*.cs`,
`GrimoireRepository.SearchArchivesAsync`, `ServiceCollectionExtensions.cs` (gate composition).

## Findings

### U27-1 (Important) A retention mutation that recovery cannot reconcile is parked with a bare `Data.ReconciliationFailed` and no log line

`DataRetentionService.RecoverMutationAsync` (DataRetentionService.cs:7189-7328) returns `RequiresAttention(Data.ReconciliationFailed)`
from nine different branches (bad checkpoint reference, bad version, unparseable journal, unreadable or malformed quarantine,
path escaping its root, a quarantine that cannot be restored or deleted, an original whose identity no longer matches, and so on).
It contains no logger call at all (24 `logger.Log*` calls exist in the file; none between lines 7189 and 7328). The two
`catch (InvalidDataException)` blocks (lines 7249 and 7269) throw away the exceptions whose messages say exactly which check
failed ("A retention mutation quarantine has an unexpected shape.", "...is not strictly owner-only.", "...could not be reconciled
safely.", "The retention mutation journal digest does not match."). The reconciler (`LongRunningOperationReconciler.cs:689-721`)
only logs when a handler *throws*; a returned `RequiresAttention` just bumps a metric (`RecordOutcome(..., "attention")`).

Consequences: the row lands in `ReconciliationRequired` with `TerminalErrorCode = Data.ReconciliationFailed`. That pair is in
`LongRunningOperationStore.StartupRecoveredAttention` (LongRunningOperationStore.cs:717-718), so every host start re-runs the
same handler and gets the same silent answer, and `TryStartSingleFlightAsync` (LongRunningOperationStore.cs:337-355) refuses every
new `data-retention-*` operation (prune, delete-session, delete-attachment, reset-memory, factory reset) while such a row exists.
The operator's only text is `DescribeRetentionConflictAsync`: "Another data-retention operation is already active:
DataRetentionMutation <id> is ReconciliationRequired." Nothing says why, nor what to do. The Command Reference table of terminal
error codes that follows "Its terminal error code says why" (Command.Reference.md:1121-1148) has no `Data.ReconciliationFailed`
row, and `operation retry` simply re-drives the same handler. Even the benign outcomes are invisible: a recovery that returns
`Completed` (the delete did commit) or `Failed(Data.ReconciliationFailed)` (the mutation never committed, nothing was deleted;
line 7325) also logs nothing, and the Failed row carries the same code as the "needs a human" row.

Probes (both executed, `U27Probe_MutationRecovery_*`): an interrupted delete-attachment journal plus a quarantine directory
holding two files, and a journal whose body is garbage, each return `state=ReconciliationRequired code=Data.ReconciliationFailed`
with a capturing `ILogger<DataRetentionService>` that recorded `logLines=0`.

Suggested fix: log (Warning) at every `RequiresAttention`/`Failed` return with the OperationId, journal subtype, and the specific
reason (and the swallowed `InvalidDataException.Message`); log one Information line for `Completed`; add a
`Data.ReconciliationFailed` row to the operation error-code table that distinguishes "never committed (Failed)" from "parked".
Effort S.

### U27-2 (Important) `search_archives` tells the model it accepts FTS5 query text, but `FtsMatchQuerySanitizer` strips every operator and silently turns queries into an AND of exact tokens

The tool schema says `query`: "Keywords or FTS5 query text to match against archived chat message content"
(Mcp/InternalTools/ArcanumInternalToolServer.Schemas.cs:579; description at ArcanumInternalToolServer.cs:1042-1046 says "keyword
matching"). `GrimoireRepository.SearchArchivesAsync` passes the text through `FtsMatchQuerySanitizer.Sanitize`
(FtsMatchQuerySanitizer.cs:25), which keeps only letters/digits/underscore/combining marks, joins tokens with spaces (implicit AND
in FTS5) and double-quotes `AND/OR/NOT/NEAR`. `Entries_fts` uses the default unicode61 tokenizer (no stemming, no prefix index).
Results of the real class run through the real sqlite 3.54 FTS5:

| Model sends | Sanitized form | Hits vs. raw FTS5 |
|---|---|---|
| `schema OR migration` | `schema "OR" migration` | 0 (raw FTS5: 2) |
| `migrat*` | `migrat` | 0 (raw: 1) |
| `"schema migration"` | `schema migration` | phrase lost |
| `what did we decide about the schema migration` | unchanged | 0 when any word (here `decide` vs `decided`) is absent |
| `???`, `!!!` | empty | tool answers "No matching archives found." (GrimoireRepository.cs:993-996) without running a search |

The model cannot tell "nothing was discussed" from "my query was syntactically transformed": the empty-result text is identical,
names no word that failed to match and does not say that all words must be present. Natural-language queries, the model's usual
form, therefore fail silently. The operator who types `session list --search` meets the same AND-of-exact-tokens behaviour,
undocumented.

Suggested fix: change the schema/description to "plain keywords; every word must appear; operators, quotes and * are not
supported", return a distinct message when the sanitized query is empty ("Query contains no searchable words"), and say in the
no-hit message which words were searched; optionally OR-join or support quoted phrases and a trailing `*` safely in the
sanitizer. Effort S.

### U27-3 (Important) A Covenant reset that rolls back before touching anything reports only "did not reach a committed reopen disposition"

`DataRetentionService.CovenantResetFailure` (DataRetentionService.cs:2308-2313) builds the error for the `RollbackAndReopen`
(lines 2004-2011) and `KeepClosed` (2013-2017) dispositions. It keeps the specific *code* (for a drain timeout,
`Grimoire.WorkDrainTimeout`, HTTP 503) but replaces the *message* with the fixed "The Covenant reset did not reach a committed
reopen disposition." The API contract for that code is the opposite of that sentence: "nothing was erased, ordinary admission is
open again, and asking once the database is quieter is the action" (API.md:905; `ErrorCodes.cs` remarks). Traced, not executed
end to end: `CloseGrimoireAsync` -> `DrainRequestAndWorkAsync` timeout -> `AbortBeforeErasureAsync` ->
`CloseAsync(blockingErrorCode: error.Code)` -> `RollbackAndReopen` -> `CovenantResetFailure`. The CLI prints
`{Code}: {Message}` (`DataRetentionCommands.WriteError`, line 516) and adds no per-code hint. The wording is also pinned by
`GrimoireMaintenanceAdmissionTests.Outcomes.cs:61,569`, so a change must update that test (it only requires "no path, no owner"
in the body).

For a destructive, irreversible erase the single most important fact (did anything get erased?) is absent, and a busy host (an
Apprentice or a chat turn longer than the gate's fixed 5 s) makes the timeout easy to hit.

Suggested fix: per-code, content-free messages ("The reset did not start: ordinary work did not finish within the drain window.
Nothing was erased; retry when the host is idle."), keeping the generic sentence only for unknown codes. Effort S.

### U27-4 (Minor) Gate drain/open timeouts identify nothing about what held the database, and three gate codes are undocumented and map to HTTP 500

`GrimoireConnectionAdmissionGate` has no logger. `DrainRequestAndWorkAsync` (GrimoireConnectionAdmissionGate.cs:569-573)
fails with "Ordinary Grimoire request or background work did not drain before maintenance closing timed out." The gate holds the
exact set (`_requestLeases` with `Kind`, `_workLeases` with `GrimoireWorkKind`), but neither the message nor any log says which
lease kinds (for example `ApprenticeExecution`, `BatchProcessing`, a finite request) were still open, how many, or the window
(`ProductionOpeningAttemptTimeout` = 5 s, fixed; `ServiceCollectionExtensions.cs:2102` never passes another value, so the
class comment about "a knob of its own to turn" (lines 44-49) has no operator-reachable knob). Probe
`U27Probe_GateDrainTimeout_*`: with a `BatchProcessing` work lease and a finite request lease open, the message is exactly the
text above and contains neither.

`OpeningTimeoutCode` ("Grimoire.OpeningTimeout", line 19; message at 686-687), `LifecycleConflictCode`
("Grimoire.AdmissionLifecycleConflict", line 17) and `StaleOpenCode` ("Grimoire.StaleOpenGeneration", line 24) are private
literals, are absent from `ErrorCodes`, from `ArcanumErrorMapper` (so they answer **500**, "Arcanum broke"), and from API.md
(only `MaintenanceUnavailable` and `WorkDrainTimeout` are listed). `OpeningTimeout` fires after stage two has moved the gate to
Closed on a burned generation (lines 649-672, comment 613-621), i.e. the host keeps answering 503 "temporarily unavailable"
while the client was told a 500 with no instruction.

Suggested fix: add the outstanding lease kinds and counts to the timeout message and a Warning log, move the three codes into
`ErrorCodes`, map them (503 for the timeouts) and document the retry-through-the-same-owner path. Effort S.

### U27-5 (Minor) `reset-memory --campaign` refusal names the wrong set of stores, and the refusal is not what the Command Reference says

`BuildResetMemoryPlanAsync` (DataRetentionService.cs:2684-2695) answers "Only Saga and Lexicon memories record an owning Campaign,
so only those two can be reset for one Campaign." while `CampaignTargetedResetIsSupported` (line 2740) also admits
`CampaignSummary`; the Command Reference (line 1230) and the CLI help (`DataRetentionCommands.cs:185-188`) list all three.
Executed probe: scope `CampaignSummary` + campaign -> no blocker and the apply succeeds; scopes `Entry` and `Workspace` +
campaign -> plan with blocker `Data.InvalidRequest`, and `ApplyAsync` fails with **`Data.Blocked`** (HTTP 409, line 1412) carrying
the "only those two" text. The Command Reference says the flag "is refused with `Data.InvalidRequest`". The preview route
returns HTTP 200 for it and `ResetMemory` in the CLI never shows `preview.Blockers` (DataRetentionCommands.cs:218-244): the
operator is asked "Reset the entry memory scope for campaign ...?", answers yes (or passes `--yes`) and only then gets the
refusal.

Suggested fix: derive the message from `CampaignTargetedResetIsSupported`'s set, return `Data.InvalidRequest` (400) for this
case in `ApplyOrdinaryAsync`, and have the CLI stop on a non-empty `Blockers` before prompting. Effort S.

### U27-6 (Minor) Delete-session / delete-attachment errors tell the operator to "request a new dry-run" that does not exist; "Data was pruned" is used for non-prune operations

`DeleteSessionAsync`/`DeleteAttachmentAsync`/`RevalidateAttachmentDeletionBoundaryAsync` raise "...changed after preview; request a
new dry-run before retrying." (DataRetentionService.cs:3107, 3138, 3477, 3498, 4536), and `ApplyOrdinaryAsync` says "request a new
dry-run before applying" for any operation when the managed-file inventory throws (line 1381). `arcanum data delete-session`
and `delete-attachment` have no `--dry-run` and no preview (`CliCommandTree.Data.cs:162-205`; API has `DELETE /data/sessions/{id}`
and `/data/attachments/{id}` only, `DataRetentionEndpoints.cs:181,199`, with `ExpectedPlanId` always null), so the advice cannot
be followed; the right instruction is "run the command again". Separately, the finalization failure for every operation reads
"Data was pruned, but the durable operation could not be finalized; retry is safe." (lines 1193, 1617), including delete-session,
delete-attachment, reset-memory and reset-workspace.

Suggested fix: pick the advice by `request.Operation` (dry-run only for prune/reset/factory reset) and say "The data change was
applied" generically. Effort S.

### U27-7 (Minor) Plan blocker and conflict reason codes are string literals scattered across five files and appear in no document

Wire-visible `reasonCode`/`code` values `Data.PinnedEntry` (DataRetentionService.cs:2463), `Data.SessionHold` (2475, 2545),
`Data.AttachmentInFlight` (2527), `Data.ContextPin` (4858), `Data.PinnedAttachment` (4901), `Data.DaemonExecutionActive` (5158),
plus `Data.BatchInProgress`, `Data.IdempotencyLeaseActive`, `Data.BatchReference`, `Data.SessionAccountingAuthority` in the
Pruning/FactoryReset partials, are not in `ErrorCodes.cs` and occur 0 times in API.md, the Command Reference and DESIGN (counted
with grep). API.md:765 says blockers carry a `reasonCode` but never lists the values, so a client or a model cannot enumerate or
branch on them, and a rename is a silent wire break.

Suggested fix: constants in `ErrorCodes.Data` and a table in API.md. Effort S.

### U27-8 (Minor) Retention failure results carry no operation id, in the API error or in the hosted-sweep warning

`ApplyStartedOrdinaryAsync` logs the OperationId and PlanId (DataRetentionService.cs:1744-1746) but returns
`Data.ReconciliationFailed` (HTTP 500) with "The retention operation failed; its durable history requires operator review."
(line 1765): the row was marked terminal `Failed`, so there is nothing to reconcile, the message names no operation to review,
and `DataRetentionApplyResult.OperationId` exists only on success. In the hosted path every settle helper returns a
`HostedPruneError(...)` built on the same code ("could not claim", "could not verify its durable owner", "cannot resume its
exact durable lease", lines 945-994) and the scheduler logs only
"Automatic retention sweep did not complete: {ErrorCode} {ErrorMessage}" (DataRetentionSweepHostedService.cs:154-158) with no
operation id, while the success line includes one. `data status` and `doctor` have no "last automatic sweep" outcome, so an
operator who enabled `retention.automaticSweepsEnabled` cannot correlate a warning with `arcanum operation list`.

Suggested fix: include the operation id in the error message (it is already a stable, non-sensitive GUID) and in the sweep
warning; use a distinct code for "could not claim/verify" (nothing to reconcile). Effort S.

### U27-9 (Minor) Global-reset conflict messages state the problem but not the remedy, which needs a started host

`GlobalOperationConflict` (DataRetentionService.cs:5016-5033), the budget-reservation text (5112) and the daemon text (5160) say
only "An active durable operation conflicts with this global reset." / "A durable operation is waiting for an operator and
conflicts with this global reset." The remedy exists only in API.md:773: `Data.ActiveOperation` clears by starting the host once;
`Data.OperationNeedsAttention` needs `arcanum operation cancel <id>` or `retry` *against a started host*. But
`data factory-reset` requires the host to be stopped (Command Reference 1215, 1231), so the real sequence is start, cancel,
stop, plan again, and nothing in the CLI output (`WritePlanSummary` prints `{Code} - {Message}`) says so.

Suggested fix: carry the remedy in the message ("start the host once to settle it" / "start the host, run `arcanum operation cancel
<id>`, stop it, and plan again"). Effort S.

## Checked and found sound (with how)

- Hosted-sweep skip while the admission gate is closed: probe `U27Probe_ClosedGateAtSweepTime_*` shows the next attempt is a full
  `sweepIntervalHours` later with zero log entries, but DESIGN.md:711 states this deliberately ("returns to the ordinary cadence
  without a warning") and `ClosedGateSkipsAutomaticSweepBeforeScope` pins it, so it is not reported.
- `IsExactHostedPruneOwner` (line 1096): `operation.State is A or B or C` is a pattern combinator, not a `||` precedence bug.
- `Data.OperationNotFinalized`/`QuarantineRecoveryRequired` HTTP mapping equals API.md:789 and `ArcanumErrorMapper` (read both).
- `DescribeRetentionConflictAsync` does name the blocking operation (matches DESIGN line 754); its catch is narrow and logged.
- Audit-log retention root: `HostSettings.cs:55` says the audit file path is code-owned, so retention scanning
  `ArcanumPaths.GrimoireDirectory` does not drift from the writer; not reported.
- `ExactUsdText` decimal formatting/parse/`CheckedAdd` overflow guards read; no operator-visible defect (its FormatException has no
  column name, judged too rare to report).
- `GrimoireEntitySql` column lists and reader ordinals compared pairwise for all five entities: consistent.
- Gate lock discipline, abort path ignoring the cancelled ambient token, one-shot authority accounting, interlock release: read in
  full; the state machine is fail-closed by design and I found no usability defect beyond U27-4.
- `FtsMatchQuerySanitizer` does prevent FTS5 syntax errors and handles supplementary-plane letters (ran the real class on
  twelve inputs); the defect is the contract around it (U27-2), not its safety.
- `PlanAsync` falling back to a plan without Covenant binding when admission fails is the documented non-request path; request
  routes use `PlanAdmissionAsync`, which surfaces the failure (`DataRetentionEndpoints.cs`).

## Probes run

1. `U27Probe_MutationRecovery_UnexpectedQuarantineShape_ParksSilently` and `..._CorruptJournal_ParksSilently`
   (scratch worktree, `ZzU27ProbeRecoveryLoggingTests.cs.txt`): `state=ReconciliationRequired code=Data.ReconciliationFailed logLines=0`.
2. `U27Probe_ClosedGateAtSweepTime_SleepsTheWholeIntervalAndLogsNothing`: `next attempt scheduled in 1.00:00:00; applyCalls=0; logEntries=0`.
3. `U27Probe_GateDrainTimeout_NamesNothingAboutWhatHeldTheDatabase`: `code=Grimoire.WorkDrainTimeout message=Ordinary Grimoire request or background work did not drain before maintenance closing timed out.`
4. `U27Probe_ResetMemory_WithCampaign_PlanAndApply` for `Entry`, `Workspace`, `CampaignSummary` (outputs quoted under U27-5).
5. Stand-alone compile of the real `FtsMatchQuerySanitizer.cs` (`probes/sanit`) on twelve queries, compared with the system
   sqlite3 3.54 FTS5 for `schema OR migration`, `migrat*`, natural language and `C++` (outputs quoted under U27-2).

Execution commands used the form
`dotnet test <scratch>/tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --filter "FullyQualifiedName~U27Probe" --results-directory .../results/C </dev/null`.
No operator state, Keychain item, Ollama process or published binary was touched.
