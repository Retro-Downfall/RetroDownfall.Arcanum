# U25 (Arcanum.Infrastructure, Data (1)) - usability lens

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only). Probes ran in a scratch worktree of my own (`scratchpad/review/wt/U25-usability`, patched copies of three existing test files plus two new probe files; source of every probe is in `scratchpad/review/probes/U25/`).

## Unit file list (reconstructed)

`scratchpad/review/units.json` did not exist when this run started (the scratchpad had just been recreated empty). I identified the unit from the size in the task text: the non-recursive `.cs` files of `src/RetroDownfall.Arcanum.Infrastructure/Data/`, sorted by path with the root files before the sub-folders, add up to exactly 10,549 lines at `DataRetentionService.FactoryActivation.cs`. The reliability reviewer reconstructed the same list independently. The 22 files, all read in full:

ArcanumDbContext.cs (184), ArcanumDbContextFactory.cs (87), ArcanumDbContextOptionsConfigurator.cs (84), AttachmentMemoryProvenanceStore.cs (175), BatchAccountingRecoveryStore.cs (364), BatchRepository.cs (1148), BlobEncryptionMetadataStore.cs (193), BudgetAlertRepository.cs (114), BudgetReservationService.cs (806), CovenantErasureCoordinator.cs (4291), CovenantLabeledArtifactGuard.cs (327), CovenantLaunchGapRecoveryHandlers.cs (123), CovenantSensitiveRetentionPurgeCoordinator.cs (649), DataRetentionFactoryResetRecoveryHandler.cs (24), DataRetentionHostedSweepOutcome.cs (32), DataRetentionLeaseMaintainer.cs (187), DataRetentionMutationRecoveryHandler.cs (24), DataRetentionPolicyStore.cs (411), DataRetentionRecoveryHandler.cs (16), DataRetentionService.CampaignSummaries.cs (186), DataRetentionService.CovenantInventory.cs (376), DataRetentionService.FactoryActivation.cs (748).

For an internal data unit the operator surface is the text of the errors and log lines it produces and the CLI/API verbs that reach it (`data status`, `data retention set`, `data reset-memory`, `data factory-reset`, `data encryption`, the budget refusal). I read the docs that own that behaviour (DESIGN 5.4.7, 10.20, 22, API.md 8.20 and the error table at 917-919, Command Reference 1143-1144 and 1215-1237, Compendium retention keys) and followed each message to the surface that prints it.

## Findings

### F1 - Important - Three failure paths in the Covenant erasure coordinator leave admission closed and write no log line at all

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`: 2484-2541 (`catch (OperationCanceledException)` / `catch (Exception)` in `RunClosedAsync`), 2652-2664 (writer restart failure), 4250-4262 (`RunLifecycleAsync`), also 996-1008, 1485-1488, 2591-2605 and the four `Parked(...)` returns at 3908, 3940, 3983 and 3996.
- Claim: the coordinator is the only place an erasure failure can be logged. `rg -c "ILogger|\.Log(Warning|Error|...)"` finds no logging in `CovenantLocalErasureStorageHealth`, `CovenantCanonicalErasureTransaction`, either erasure kernel, `CovenantErasureInventorySource` or `CovenantResetCheckpointInitiator`. In three paths the coordinator logs nothing either:
  1. Any exception that is not a `CovenantErasureStepFailedException` (an `IOException` from the file replace, a `SqliteException` from the kernel, a cancellation) is replaced by `MaintenanceFailure()` and closed with `KeepClosed`. Neither the exception type nor the phase is logged. The sibling `CovenantSensitiveRetentionPurgeCoordinator.cs:358-368` already sets the pattern: it logs `exception.GetType().Name` and says why the message is withheld (it can name a managed file).
  2. After the erasure is proven and published, a failing `_disclosureWriter.ReopenAsync` goes straight to `CloseAsync(KeepClosed, writer.Error.Code)` (2652-2664). The `published.IsFailure` branch just above it logs; this one does not, and not even `writer.Error.Message` is recorded.
  3. `ReconcileBeforeDispositionAsync` parks the transition in four places: terminal intent could not be prepared (3908), no terminal winner could be named (3940), the winner evidence is missing (3983), the parent receipt could not be published or re-read (3996). The comment at 3879-3881 lists four different causes of the missing winner alone (row missing, another launch's row, row moved, row terminalized by someone else). All of them park without a log line and end as the same `Covenant.MaintenanceFailed`.
- Failure scenario: a reset or factory erasure hits a disk-full or permission error between `SidecarsVerified` and the proof. The installation stays closed. `arcanum doctor` shows `DataRetentionMutation x1 (Covenant.MaintenanceFailed)`. The rolling log has nothing about this operation, so the operator cannot tell a transient I/O problem from corruption and cannot know whether the erasure had already happened.
- Evidence (probes run in the scratch copy of `CovenantErasureCoordinatorTests`, with the harness logger replaced by a capturing logger):
  - `PROBE_unexpected_exception_mid_erasure_is_logged_nowhere` (fault seam throws `IOException("disk full /secret/path")` before phase `WalTruncated`): `PROBE1 success=True disposition=KeepClosed blocking=Covenant.MaintenanceFailed`, `PROBE1 log line count=0`.
  - `PROBE_writer_restart_failure_after_proven_erasure_is_logged_nowhere` (`DisclosureWriter.ReopenFails = true`): `PROBE2 success=True disposition=KeepClosed localSecure=True blocking=Covenant.MaintenanceFailed`, `PROBE2 log line count=0`. The erasure was complete and proven, and the log says nothing.
  - The existing suite cannot see this: every coordinator test passes `NullLogger`.
- Fix: in each of those catch blocks log operation id, `state.Phase` and `exception.GetType().Name` (type only, as the purge coordinator does); log `writer.Error.Code`/`Message` (they are fixed strings); log the park cause at the four `Parked` sites. Add a capturing-logger assertion to the existing crash-matrix cases. Effort S.

### F2 - Minor - The coordinator's own error lines omit the operation id and the phase, and the pre-erasure rollback line drops the reason

- File: `CovenantErasureCoordinator.cs`: the eight `LogError` calls in `CloseAsync` (3605, 3615, 3658, 3668, 3704, 3721, 3739, 3769), plus 1296 and 2919.
- Claim: these are the lines that say "admission stays closed". Every one has `operation.Id` and `checkpoint.Phase` in scope and none logs them; only the error code is logged (for most of them that code is always `Covenant.MaintenanceFailed`, because the failing helper returned `MaintenanceFailure()`). The `RollbackAndReopen` branch at 2919 logs only the code, while the `KeepClosed` branch six lines below logs phase, operation id, code and message.
- Evidence: probe `PROBE_preflight_refusal_reason_is_dropped_from_the_log_and_the_completion` (preflight stub returns `Covenant.IntegrityFailure` / "The erasure inventory could not be enumerated."): `PROBE3 disposition=RollbackAndReopen blocking=Covenant.IntegrityFailure`, one log line: `Warning: A Covenant erasure aborted before any artifact was touched with Covenant.IntegrityFailure; admission reopens.` The message that says why is in no log, and the completion record has no message field. The service then answers with the fixed text of F3.
- Failure scenario: `Data.Conflict` from `CovenantErasureInventorySource.cs:141` ("An active inference run still protects its inputs and accounting chain after maintenance drain.") is the likeliest real instance. The operator is refused, the reopen is logged without the sentence that explains it.
- Fix: add `{OperationId}`, `{ResetPhase}` and (for the rollback line) `{ErrorMessage}` to those templates. The messages are fixed strings, so this stays inside the content-free policy. Effort S.

### F3 - Important - A failed Covenant reset or factory erasure tells the operator nothing about what happened or what to do, although the docs promise both

- Files: `DataRetentionService.cs:2308-2313` (`CovenantResetFailure`, outside the unit but called from `DataRetentionService.FactoryActivation.cs:485,492,501`), `CovenantErasureCoordinator.cs:4264-4267` (`MaintenanceFailure`), `DataRetentionService.FactoryActivation.cs:102-107,362-389`, type docs at `CovenantErasureCoordinator.cs:435-455`.
- Claim: every failed or parked erasure reaches the operator as a code plus a fixed sentence: "The Covenant reset did not reach a committed reopen disposition.", "The Covenant erasure lifecycle could not be completed safely.", "Healthy-catalog factory erasure requires one current installation inventory and its exclusive lifecycle.", "The committed factory-erasure checkpoint could not be reloaded." None names the phase, the operation id, whether the data was erased, or a next step. `CovenantErasureCompletion` computes `CanonicalResetApplied`, `LocalSecureErasureComplete` and `ExternalDisclosuresNotRevocable`, and its doc comment says "an operator needs all three. Collapsing them would let a half-proven erasure read as a finished one". `rg` finds no reader of those properties outside the coordinator and `CovenantErasureProgress`: the service discards them on `KeepClosed` and `RollbackAndReopen`.
- Docs that this contradicts: `docs/Arcanum.API.md:919` ("Each names an operator action rather than a retry"), `docs/Arcanum.Command.Reference.md:1143` ("follow the reported restore, Covenant-family reinitialize, or full-installation-reset remedy"; no remedy is reported anywhere), `docs/Arcanum.Command.Reference.md:1144` ("Keep admission closed and inspect the operation").
- Failure scenario: `arcanum data reset-memory --scope covenant --apply` stops after the canonical erasure. The CLI prints `Covenant.MaintenanceFailed: The Covenant reset did not reach a committed reopen disposition.` (`DataRetentionCommands.cs:518`) and exits 1 (503 over HTTP). Probe 2 above is exactly this state with `localSecure=True`. The operator cannot tell "nothing was erased" from "everything was erased and only the writer restart failed", and is not told to run `arcanum operation show <id>` or not to start a second reset. The same applies to `CovenantLabeledArtifactGuard.cs:305-325`, whose 503/403 refusals name "the sensitivity purge boundary" but no command, although DESIGN 10.20.2 says the operator's action is to repair the Grimoire.
- Fix: make `CovenantResetFailure` take the completion, and say (content-free) whether the canonical erasure was applied and whether local erasure was proven, plus "Admission stays closed. Run 'arcanum operation show <id>'; do not start a second reset." Carry the operation id. Effort M.

### F4 - Important - `arcanum doctor` gives a stuck Covenant reset the repair guidance for an ordinary retention policy change

- File: `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs:171` (outside the unit; it is where the failures of F1-F3 end up), consumed by `src/RetroDownfall.Arcanum.Infrastructure/Operations/DurableOperationDiagnostics.cs:54`.
- Claim: a parked Covenant reset is a `DataRetentionMutation` row at checkpoint version 4 (`DataRetentionService.cs:1857`, state `ReconciliationRequired`, terminal code `Covenant.MaintenanceFailed`). The doctor panel groups by (kind, terminal code) but looks guidance up by kind only, so it prints `DataRetentionMutation x1 (Covenant.MaintenanceFailed): Inspect 'arcanum data retention show' before re-applying the policy change.` The registry's own `RecoveryIntent` three lines above says a version-4 row "is an interrupted Covenant reset ... whose admission stays closed until the reset is resumed or an operator resolves it", and Command Reference 1143 says "Do not start a second erasure."
- Failure scenario: an operator follows the doctor line, runs `data retention show` (useless here), and re-applies the reset, which is the one thing the Command Reference says not to do.
- Evidence: read of the three call sites above (no execution needed; `Find(kind)?.ManualRepairGuidance` has no overload for terminal code or checkpoint version).
- Fix: when the terminal code is a `Covenant.*` code (or the version is 4), return guidance that matches Command Reference 1143: `arcanum operation show <id>`, keep admission closed, do not start a second erasure. Effort S.

### F5 - Minor - Budget refusals show numbers that look under the limit, omit the request, the UTC day and the setting that governs it

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:145`, `:283`, `:396`; `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:72` has the same omissions (its wording, "has been reached (current spend ...)", is clearer about the numbers).
- Claim: `ReserveAsync` and `AdjustAsync` report `committed+reserved` *before* the request, so the message can read "limit $5.00 would be exceeded (committed+reserved: $4.99)". `DailyLimitRecheckFailure` (396) reports the total *including* the delegated amount, a different meaning under the same wording. Two-decimal formatting makes the numbers equal when the real figures are not. None of the four messages says the day is a UTC day, when it resets, or that `Arcanum:Cost:Budget:DailyLimitUsd` is the setting to raise.
- Docs that this contradicts: `docs/Arcanum.DESIGN.md:1707` ("reports the operator-owned token/cost policy and measured or reserved usage needed to change or resume it") and `docs/Arcanum.CHAT-LOOP.md:33` ("how to change or resume the policy"). `docs/Arcanum.API.md:918` mentions the UTC day, the message does not.
- Failure scenario: an operator in UTC-5 hits the limit at 16:00 local. The message looks like an arithmetic error ($4.99 of $5.00) and gives no hint that the budget resets at 19:00 local.
- Evidence: probe `PROBE_budget_message_shapes` (scratch copy of `BudgetReservationServiceTests`): limit 5 / committed 4.99 / request 0.12 gives `Daily budget limit of $5.00 USD would be exceeded (committed+reserved: $4.99 USD).`; limit 4.9899 / request 0.0001 gives `Daily budget limit of $4.99 USD would be exceeded (committed+reserved: $4.99 USD).` The existing test at `BudgetReservationServiceTests.cs:278` pins the same equal-numbers sentence.
- Fix: one shared formatter: "Daily budget of $X (UTC day YYYY-MM-DD) would be exceeded: $A committed + $B reserved + $C requested. It resets at 00:00 UTC; raise Arcanum:Cost:Budget:DailyLimitUsd or wait." Use up to 4 decimals when a figure is under 1 cent. Effort S.

### F6 - Minor - Every turn after the alert threshold logs a Warning for an expected duplicate, and `HasAlertedTodayAsync` has no caller

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs:56-63`; caller `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:133`.
- Claim: `BudgetMonitor.CheckAsync` calls `TryDispatchAlertAsync` on every check at or above the threshold; that calls `RecordAlertAsync` unconditionally, so after the first insert each later check runs a write transaction that fails on the unique index and logs `Warning: Budget alert for threshold {Threshold} already recorded today; skipping duplicate.` (the test at `BudgetAlertRepositoryTests.cs:118-135` pins Warning). The monitor's own comment calls this "another concurrent turn" and logs it at Debug. DESIGN `5922` says `HasAlertedTodayAsync` "is retained as a cheap pre-check"; `rg HasAlertedTodayAsync src` finds only the definition and the interface.
- Failure scenario: from the first turn that crosses 80% until UTC midnight, every turn adds one Warning and one failed write. At 100% every refused request adds one.
- Evidence: probe `BudgetMonitorTests.PROBE_every_check_above_threshold_attempts_the_alert_insert` (the monitor with a recording alert repository that behaves like the unique index: first insert accepted, later ones refused). Five `CheckAsync` calls at 80% of the limit gave `PROBE-ALERT checks=5 RecordAlertAsync calls=5 (accepted=1) HasAlertedTodayAsync calls=0 dispatched=1`. With the real repository each of the four refused calls is one failed write plus one Warning (the Warning is pinned by `BudgetAlertRepositoryTests.cs:118-135`).
- Fix: pre-check `HasAlertedTodayAsync` in `TryDispatchAlertAsync` (and keep the constraint as the race guard), or log the duplicate at Debug. Correct DESIGN 5922. Effort S.

### F7 - Minor - `data retention set`: the refusals name neither the value nor the valid classes, one rule is shared by several class names, and the effective floor replaces the request silently

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionPolicyStore.cs:177,204,312` (messages), `:241-253,292-297` (shared rules), `:338-340` (floor); CLI `DataRetentionCommands.cs:59-66,73`.
- Claim: "The retention data class is not recognized." and "<data-class> must name a supported retention data class." do not echo the input or list the classes (the sibling `--scope` message lists its values). Command Reference 1225 says "Set one named typed rule", but `batch-input-files`, `batch-output-files`, `batch-error-files` and `uploaded-files` all write the single `UploadedFiles` rule, `attachment-*` write `Attachments`, and `inference-runs|billable-operations|budget-reservations|cost-adjustments` write `Accounting`; the prompt says only "Update the batch-input-files retention rule?". For `accounting`, `Normalize` raises `Days` to `accountingMinimumDays` (default 365) before the value is saved, so `set accounting 30` stores 365; Compendium 307 calls the stored value the "requested age threshold".
- Failure scenario: an operator who wants shorter retention for batch inputs runs `set batch-input-files 2`; the next sweep applies 2 days to every uploaded file and batch output. Or runs `set accounting 30` and sees 365 with no explanation.
- Evidence: probe `ProbeU25RetentionPolicy.PROBE_retention_policy_store_messages_and_shared_rules` (real `DataRetentionPolicyStore` and `ConfigurationWriter` over an isolated test home). Output:
  ```
  PROBE-POLICY unknown class => code=Data.InvalidRequest msg=[The retention data class is not recognized.]
  PROBE-POLICY tapestry => code=Data.InvalidRequest msg=[The retention data class has no configurable rule.]
  PROBE-POLICY batch-input-files 2 => ok=True UploadedFiles=True/2 CompletedBatches=True/30
  PROBE-POLICY accounting 30 => ok=True Accounting=True/365 AccountingMinimumDays=365
  PROBE-POLICY persisted accounting fragment: "accounting": { "enabled": true, "days": 365 }
  ```
  `sessions` (the natural guess) and `tapestry` get no list of valid names. The batch-input-files request turned on a 2-day rule for every uploaded file. The accounting request of 30 days was written to arcanum.json as 365.
- Fix: list the valid class names in both messages; make the prompt and response name the rule that changes and the classes it covers; when a floor applies, say so. Effort S.

### F8 - Minor - The CLI help example for `data retention set` does not work

- File: `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs:310` (outside the unit; found while following F7).
- Claim: `arcanum data retention set --help` prints `Examples: arcanum data retention set --sessions 90`. The command takes two positional arguments (`<class> <days|disabled>`) and has no `--sessions` option.
- Evidence: probe `PROBE_retention_set_example_runs`: `data retention set --sessions 90` exits 2 with `<data-class> must name a supported retention data class.`; the help output ends with the broken example. The parse itself reports 0 errors, so `--sessions` is consumed as the class name. `CliHelpExampleTests` only asserts that the examples are printed, never that they parse.
- Fix: `arcanum data retention set archived-sessions 90`; add a test that parses every example and checks that it reaches a handler. Effort S.

### F9 - Minor - `data status` and the factory path drop the cause when the Covenant inventory cannot be measured

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs:87,209,246,352-356`; message at `DataRetentionService.FactoryActivation.cs:102-107`.
- Claim: `TryAcquireCovenantInstallationReadAsync` returns null on any failure of the gate; `InventoryCovenantAsync` returns null when the lease no longer revalidates or the disclosure read fails; `ReadNonrevocableDisclosureExposureAsync` throws away `exposure.Error` (the specific "The nonrevocable Covenant disclosure exposure could not be folded safely."). `DataRetentionService.cs:496-510` then simply omits the Covenant row. The state in which an operator most needs the row (a closed gate after a stopped erasure) shows exactly what a Covenant-off install shows. On the apply path the three reasons become one `Covenant.IntegrityFailure`/`MaintenanceFailed` with the sentence from F3.
- Evidence: trace through the five lines above; not executed.
- Fix: return a typed "unmeasured: <code>" and print it in `data status`; log the dropped `Error.Code` at the discard site. Effort S.

### F10 - Minor - Factory-erasure error logs have no operation id

- File: `DataRetentionService.FactoryActivation.cs:336-338, 464-466, 537-539` (`operation` is non-null at each).
- Claim: "Healthy-catalog factory erasure lost its exact durable owner; recovery must reconcile it." (twice) and "Healthy-catalog factory erasure failed unexpectedly after durable operation admission." carry no `{OperationId}`, while `DataRetentionService.cs:7466` and the nested `ContinueFactoryResetAsync` lines do. For an unnamed apply the caller never learns the id, so the log is the only place to find it.
- Fix: add `operation.Id`. Effort S.

### F11 - Minor - A connection failure is logged as one fixed line with no exception type or SQLite result code, and EF's own event is suppressed

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs:50` (`warnings.Ignore(RelationalEventId.ConnectionError)`), replacement in `Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs:205-217`.
- Claim: with EF's `ConnectionError` ignored, the only signal for a failed open (wrong key, locked, unreadable, disk full) is `Error: An ordinary Grimoire connection failed to open safely.` with no exception. `GrimoireConnectionAdmissionInterceptorTests.cs:1092-1094` pins that wording and `Assert.Null(entry.Exception)`. `CovenantLabeledArtifactGuard.cs:308-312` shows the content-free alternative that is already accepted here: log `SqliteErrorCode` and `SqliteExtendedErrorCode`.
- Fix: log the exception type and, for `SqliteException`, the primary and extended result codes. Effort S.

### F12 - Minor - One bad row aborts every blob-encryption command and the error names no row

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs:119,152,170-181` (and `:76-77`).
- Claim: `ListAsync` throws on the first attachment row whose path escapes the root (`InvalidDataException("Session attachment metadata escapes the attachment root.")`) or whose upload Id is not a Guid (`Guid.Parse` has no row context). `status`, `migrate`, `verify` and `rotate-key` all list first, and `DataEncryptionCommands` has no catch, so the CLI prints `An unexpected CLI error occurred.` (`CliContracts.cs:857-866`) with exit 1 and no row id. The row-vanished message at 76 has no id either.
- Evidence: trace through `BlobEncryptionLifecycleService.GetStatusAsync`, `DataEncryptionCommands.Status` and `CliFailureMapper`; not executed (needs a corrupted catalog row).
- Fix: skip and count the offending row as invalid with its id in the exception or log, as `GetStatusAsync` already does for a missing file. Effort S.

### F13 - Minor (docs) - Stale and stranded documentation comments

- `BatchRepository.cs:12` says the table is "created by the embedded `InitialCreate.sql` schema baseline under `Data/Schema/Tables/`". No such file exists (`find -iname '*InitialCreate*'` under `Data/Schema` is empty; the object is `Data/Schema/Tables/Batches.sql`). The same sentence is in `UploadedFileRepository.cs:14`, `SanctumBreachRepository.cs:17`, `IdempotencyStore.cs:12`, `UnseenServantWatermarkStore.cs:12`. AGENTS.md says to add or edit the object's own file.
- `CovenantErasureCoordinator.cs:3065-3074` is the summary of `AdvanceAsync` ("Performs one phase's step exactly once...") but sits above the `_reconciler` field, and `:4269-4276` documents the progress type at the end of the class with no member beneath it. IDE hover on `_reconciler` shows the wrong text.
- Effort S.

## Checked and found sound

- `BatchRepository` and `BatchAccountingRecoveryStore` messages: `Batch '<id>' lost its durable accounting recovery claim...` and `Batch line checkpoint '<id>/<line>' was missing or already completed with different content.` name the object. `BatchFileReferenceException` is mapped by `OpenAiV1BatchesEndpoints.cs:205-212` to 404 `No such file: '<input_file_id>'` with `param: input_file_id`. The list endpoint refuses a bad `limit` naming the field and bounds (`OpenAiV1BatchesEndpoints.cs:255-262`).
- `DataRetentionPolicyStore` Covenant and erasure-evidence refusals explain why the class has no rule and what removes its rows, matching DESIGN 10.20.1 and 678.
- `CovenantLabeledArtifactGuard` withholds the artifact id by design (DESIGN 2630) and logs kind and SQLite codes only. `CovenantSensitiveRetentionPurgeCoordinator` logs exception type and code only, never the message, and keeps the dispositions of already-erased items on an interruption.
- `DailyLimitRecheckFailure` formats with `CultureInfo.InvariantCulture`; the other two budget interpolations are culture-safe in the shipped binary because `Cli.csproj` sets `InvariantGlobalization`.
- `ArcanumErrorMapper.cs:206-224` maps `Covenant.MaintenanceFailed|ManualRecoveryRequired|ErasureIncomplete|IntegrityFailure|Unavailable` to 503 as API.md 919 says; the status codes are right, the messages are the gap (F3).
- `MapFactoryErasureReplay` returns `IdempotencyConflict` / `IdempotencyInProgress` / the operation's terminal code, and the plan-changed message ("request a new dry-run before applying") says what to do.
- `ArcanumDbContext`, `ArcanumDbContextFactory` (design-time only, `[RequiresAssemblyFiles]`), `AttachmentMemoryProvenanceStore`, `BudgetAlertRepository.HasAlertedTodayAsync`, `DataRetentionLeaseMaintainer`, the three recovery-handler adapters and `DataRetentionHostedSweepOutcome` have no operator-facing text beyond what is listed above.

## Probes

All in `scratchpad/review/wt/U25-usability` (scratch worktree at `ac0d2d8e`); sources copied to `scratchpad/review/probes/U25/`.

1. `CovenantErasureCoordinatorTests.PROBE_unexpected_exception_mid_erasure_is_logged_nowhere` - F1. Output: `PROBE1 success=True disposition=KeepClosed blocking=Covenant.MaintenanceFailed` / `PROBE1 log line count=0`.
2. `CovenantErasureCoordinatorTests.PROBE_writer_restart_failure_after_proven_erasure_is_logged_nowhere` - F1/F3. Output: `PROBE2 ... disposition=KeepClosed localSecure=True blocking=Covenant.MaintenanceFailed` / `PROBE2 log line count=0`.
3. `CovenantErasureCoordinatorTests.PROBE_preflight_refusal_reason_is_dropped_from_the_log_and_the_completion` - F2. Output: `PROBE3 disposition=RollbackAndReopen blocking=Covenant.IntegrityFailure`, one Warning line with the code only.
4. `BudgetReservationServiceTests.PROBE_budget_message_shapes` - F5. Output as quoted.
5. `Cli/ProbeU25HelpExample` - F8. Output: exit 2 with `<data-class> must name a supported retention data class.`; help text ends with the broken example.
6. `BudgetMonitorTests.PROBE_every_check_above_threshold_attempts_the_alert_insert` - F6. Output: `PROBE-ALERT checks=5 RecordAlertAsync calls=5 (accepted=1) HasAlertedTodayAsync calls=0 dispatched=1`.
7. `Configuration/ProbeU25RetentionPolicy` - F7. Output as quoted in F7.
