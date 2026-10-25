# U28 (Arcanum.Infrastructure, Data (4)) - USABILITY review

Tree: worktree `codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e` (read-only). Probes ran in a scratch worktree
(`scratchpad/review/wt/U28-usability`, removed afterwards). Probe source and raw outputs are in `reports/C/U28-usability-probes/`.

## Unit identification (units.json was absent)

`scratchpad/review/units.json` did not exist. The unit was reconstructed the same way the U27 review did: top-level
`Infrastructure/Data/*.cs` in ordinal path order, packed greedily to a 12,000-line cap. Chunk 3 (U27) ends at
`GrimoireInstallationMaintenancePaths.cs`; chunk 4 is `GrimoireMaintenanceConnectionFactory.cs` .. `SessionAttachmentStore.Lifecycle.cs`,
which sums to exactly 10,500 lines (the task says "about 10500"; adding `SessionAttachmentStore.cs`, 2,610 lines, would pass the cap).

## Files read in full (every line), 31 files

GrimoireMaintenanceConnectionFactory (374), GrimoireOrdinaryConnectionFactory (581), GrimoireOrdinaryConnectionLifecycle (435),
GrimoireRequestAdmissionScope (127), GrimoireSqlCommandFactory (56), GrimoireWalCheckpoint (59),
IAuthenticatedCovenantErasureRecoveryHandler (17), ICovenantLabeledArtifactTransactionGuard (84), ISqliteNativeRuntime (23),
IdempotencyClaimStore (470), IdempotencyStore (154), LongRunningOperationStore.RequestAdmission (116), LongRunningOperationStore (1444),
MemoryErasureEvidence (1095), MemoryErasureFingerprintRelease (166), MemoryErasureGuard (344), MemoryErasureKeyWarmup (76),
MemoryErasureLabels (120), MemoryScopeResolver (112), SagaErasureWriteGate (132), SagaExtractionCursorResolver (157),
SagaMemoryScopeClassifier (153), SagaMemoryStore.Curation (806), SagaMemoryStore.Ranking (179), SagaMemoryStore.Recall (47),
SagaMemoryStore (1305), SagaRetirementSuppression (180), SagaSuppressionKeyStore (91), SagaVectorMirror (321),
SanctumBreachRepository (312), SessionAttachmentStore.Lifecycle (964).

Read for context (not part of the unit): `GrimoireLivenessProbe`, `ArcanumExceptionHandler`, `GrimoireMaintenanceRefusal`, `ArcanumErrorMapper`,
`LongRunningOperationEndpoints`, `OperationCommands`, `CliCommandTree.Operations`, `ArcanumApiClient` (operations region),
`LongRunningOperationReconciler`, `LongRunningOperationRecoveryAdmission`, `LongRunningOperationRecoveryRegistry`,
`DurableOperationDiagnostics`, `SagaCurationService`, `MemoryEndpoints` (explain/search), `SagaEndpoints`, `SanctumEndpoints`,
`AttachmentSourceResolver.RevalidateAsync`, `SqliteNativeRuntime*`, `MemoryErasureProtocol`, `MemoryRecallCandidateReader`,
`SagaExtractionService` (erasure deferral), `IdempotencyEndpointFilters` (messages).

Owning docs read: `Arcanum.API.md` (operations 254-258, memory search 98, sanctum 191, explain 343-349, error table 881-909),
`Arcanum.Command.Reference.md` (operation 1105-1165, saga/memory 668-730, `--json` envelope 95), `Arcanum.DESIGN.md` (397, 1093, 1840-1846).

## Probes and runs

| Id | What | Result |
|---|---|---|
| P1 | Production `GrimoireOrdinaryConnectionFactory` (fakes for lifecycle/drain/initializer, real native runtime, isolated `ARCANUM_TEST_HOME`), capturing logger. A: Grimoire file absent. B: 4 KiB zero-filled file opened with a passphrase (stands for a corrupt file or key mismatch). | Both return `Covenant.Unavailable` / "The ordinary Grimoire connection could not be opened." Zero log entries at any level. |
| P1c | Real `GrimoireLivenessProbe` with a factory that refuses with that error. | `ok=False detail=Grimoire live probe failed: GrimoireMaintenanceUnavailableException.` |
| P2 | Real `LongRunningOperationStore.ListAsync` over 520 stored operations. | default (what the endpoint/CLI ask) 100; Limit=0 -> 1; Limit=-5 -> 1; Limit=1000 -> 500; Offset=500 -> 20. |
| P3 | Real `SagaMemoryStore.ListAsync(query)` over "Eric ... eclair" and "Eric aime les eclairs" (accented). | `ECLAIR` finds the ASCII row; `ECLAIRS`-accented-upper finds nothing; `eric`-accented-lower finds nothing. (`sqlite3` confirms `'e-acute-clair' LIKE '%E-ACUTE-CLAIR%'` = 0.) |
| P4 | Real `SessionAttachmentStore.RevalidateBoundSourcesAsync` with a resolver that throws `InvalidOperationException`. | Row returned and persisted as `CorruptMetadata` / "Source metadata could not be safely revalidated."; zero log entries. |
| P5 | Spectre.Console `Table` built exactly as `OperationCommands.List` builds it (5 default columns), one realistic row, rendered at 80/120/160/200 columns. | The 36-character operation ID is split across 3 lines at 80, 2 lines at 120; intact only at 160+. |
| existing | `LongRunningOperationEndpointTests` owner-bound cancel/retry theories (4 cases), run `--no-build` in the review tree. | Pass, i.e. they pin the 409 texts quoted in finding 2. |

Not run: no published-binary run (it would need a host and touches the Keychain); findings 2 and 3 were traced through the real callers and the
existing endpoint tests instead, and finding 1's HTTP mapping was traced rather than driven end to end (P1c covers the health path).

---

## Findings

### 1. [Important] The connection factories discard why a Grimoire open failed, log nothing, and every caller then reports it as a maintenance window

- **Where:** `Data/GrimoireOrdinaryConnectionFactory.cs:107-110, 139-144, 215-218, 257-264, 324-333`; `Data/GrimoireMaintenanceConnectionFactory.cs:178-183, 217-227, 258-265`.
  Callers that turn any `Result` failure into `GrimoireMaintenanceUnavailableException`: `Api/Health/GrimoireLivenessProbe.cs:107-110`,
  `Api/Workspaces/WorkspaceDivinationEndpoints.cs:255-258`, `Api/Tower/SessionDivinationEndpoints.cs:236-239`, `Api/Tower/MemoryEndpoints.cs:1752-1755`,
  `Api/Intelligence/WizardIntelligenceProvider.cs:6870-6872`, `Infrastructure/Covenant/CovenantCampaignScopeProbe.cs:99-101`,
  `Data/Covenant/ICovenantConnectionSource.cs:157-160`, and this unit's `LongRunningOperationStore.cs:1074-1077` (lease heartbeat).
- **Defect:** The factories catch `Exception` without binding or logging it (the ordinary factory even has an `ILogger`, used only for an unrelated warning) and
  return one fixed sentence: "The ordinary Grimoire connection could not be opened." / "... could not be initialized." / "... native runtime is unavailable." The journal factory
  has no logger at all. `SqliteNativeRuntimeUnavailableException` was written so its message (runtime id, expected asset filename) and stable code
  `Grimoire.NativeRuntimeUnavailable` are pasteable "for logs and support"; the factories throw that away too. The `Result` carries `Covenant.Unavailable` for a real failure and
  `Grimoire.MaintenanceUnavailable` only for a closed gate, precisely so a caller can tell them apart, but the callers listed above ignore the code and throw the maintenance exception for every failure.
- **Scenario:** The Grimoire file is moved or loses permissions, the disk is full, or the key no longer matches while the host runs. `GET /api/health` reports component
  Grimoire Unhealthy with detail `Grimoire live probe failed: GrimoireMaintenanceUnavailableException.` (P1c). Endpoints that open through the factory answer 503
  "The Grimoire is temporarily unavailable while maintenance owns connection admission." (`GrimoireMaintenanceRefusal.cs:36-37`), and the only log line is Debug
  "A request was refused because Grimoire maintenance owns connection admission." (`ArcanumExceptionHandler.cs:71`), with no exception and no cause. `arcanum doctor` and the logs
  both send the operator to wait out a maintenance window that does not exist.
- **Evidence:** P1 outputs A and B above (identical message, `log entries: 0`); P1c output. Code quotes: `catch (Exception) { await TerminalizeOpenFailureAsync(...); return Refused("The ordinary Grimoire connection could not be opened."); }` (lines 139-144).
- **Fix:** Bind the exception in each catch and log it at Warning with the kind/mode/purpose (never the connection string); put the SQLite error code/type in the `Error` message
  (and `SqliteNativeRuntimeUnavailableException.Message` + `ErrorCode` for the runtime branch). Give `GrimoireMaintenanceConnectionFactory` an `ILogger`. At the call sites, throw the maintenance
  exception only when `acquired.Error.Code == ErrorCodes.Grimoire.MaintenanceUnavailable`; otherwise surface `acquired.Error` (503 `Covenant.Unavailable`, probe detail = the error message).
- **Effort:** M.

### 2. [Important] Retry and cancel of an owner-bound retention operation are refused with text that contradicts the row's state, and no surface names the real remedy

- **Where:** `Data/LongRunningOperationStore.cs:1215-1245` (`RequestCancellationAsync` excludes owner-bound rows inside the SQL and returns a bare `bool`), `:1254-1287`; endpoint
  texts in `Api/Operations/LongRunningOperationEndpoints.cs:94-103, 133-157`; docs `Arcanum.Command.Reference.md:1116, 1144`, `Arcanum.API.md:256-257`;
  registry guidance `Core/Operations/LongRunningOperationRecoveryRegistry.cs:150, 171, 192`.
- **Defect:** For a current `data-retention-mutation` V4 or `data-retention-factory-reset` V2 row in `ReconciliationRequired` (the rows `arcanum operation list --state ReconciliationRequired` is
  documented to surface), `POST /retry` answers 409 `Operation.StateConflict` "Only Failed, Abandoned, or ReconciliationRequired operations can be retried." The row IS ReconciliationRequired. `POST /cancel` answers
  "The operation cannot be cancelled in its current state." The existing tests pin both strings (`LongRunningOperationEndpointTests.cs:171, 240`) with a row in exactly that state. Meanwhile
  - the Command Reference row for `Covenant.ErasureIncomplete` / `Covenant.MaintenanceFailed` (the codes these rows carry) tells the operator to "retry or reconcile the same operation";
  - `arcanum operation reconcile` skips these rows (`LongRunningOperationReconciler.cs:204-210`, metric label `awaiting_exact_owner`) and prints only a `skipped` count;
  - `arcanum doctor`'s per-kind guidance says "Re-run the factory reset" / "Inspect 'arcanum data retention show'", but a new start is refused while the ReconciliationRequired row exists
    (single flight counts it as active: `LongRunningOperationStore.cs:346-358`, reported by `DescribeRetentionConflictAsync` as "Another data-retention operation is already active: ... is ReconciliationRequired");
  - the only path that adopts them is the authenticated owner at a host start (`AdoptUnderInstallationLockAsync`, `LongRunningOperationStore.cs:793-817`; `StartupRecoveredAttention`, `:715-722`),
    and nothing an operator can read says "restart the host".
  - The terminal-code table in the Command Reference has no row for `Data.ReconciliationFailed`, the code three of the five `StartupRecoveredAttention` pairs carry.
- **Scenario:** A Covenant reset ends `ReconciliationRequired` / `Covenant.MaintenanceFailed`. The operator follows the documented runbook: `operation list --state ReconciliationRequired`, then `operation retry <id>`
  (409, "can be retried" for a state the row is in), `operation cancel <id>` (409, no reason), `operation reconcile` (exit 0/2 with a "skipped" count), `data ... --apply` (conflict naming the same row). Every route
  looks broken and none says that the host adopts the row at its next start.
- **Evidence:** Test text at `LongRunningOperationEndpointTests.cs:171, 240`; the `NOT (("Kind" = @retentionMutation AND "CheckpointVersion" = @mutationOwnerVersion) OR ...)` predicate at `LongRunningOperationStore.cs:1226-1228`;
  `grep ReconciliationFailed docs/Arcanum.Command.Reference.md` returns nothing. I did not start a host to drive the restart path, so the "restart the host" remedy is read from the adoption code, not executed.
- **Fix:** Classify before the compare-and-swap (the retry endpoint already does for retry) and give the owner-bound case its own message, for both verbs, naming the reason and the action (for example: "This operation is owned by an
  authenticated offline transition and is resumed by the host when it next starts; it cannot be retried or cancelled here. Restart the host and re-check `arcanum operation show <id>`.") and have `reconcile` report
  owner-bound rows as "awaiting host restart" instead of folding them into `skipped`. Add the `Data.ReconciliationFailed` row and the owner-bound exception to the Command Reference table, and make the `Covenant.MaintenanceFailed`
  row and the registry guidance say the same thing.
- **Effort:** S-M.

### 3. [Important] `arcanum operation list` splits operation IDs across lines on any terminal narrower than about 140 columns, so the ID that `show`/`retry`/`cancel` need cannot be copied

- **Where:** `src/RetroDownfall.Arcanum.Cli/Commands/OperationCommands.cs:22-39` (outside this unit's file list; reported because it is the operator face of this unit's ledger and the entry point of the repair flow `doctor` and the Command Reference point to).
- **Defect:** The command renders a default Spectre `Table` with five unconstrained columns (ID, Kind, State, Attempts, PublicSummary). Summaries written by the store's callers are long ("Applying automatic data-retention plan {guid}." is 83 characters), so
  the table exceeds the console width and Spectre shrinks the widest columns and character-wraps the GUID. The Command Reference promises "never wrapped or shortened" IDs for `saga list` and `apprentice list` (`:668`, `:890`, via `ListingTable`); `operation list` does not use it.
  Captured `--json` output (`{ "output": ..., "exitCode": ... }`, a documented text-envelope family) is rendered at the default width as well.
- **Scenario:** `arcanum doctor` -> `arcanum operation list --state ReconciliationRequired` on an 80- or 120-column terminal -> the ID prints as `3f2c9a4e-7b1d-` / `4c58-9e0a-5d6f` / `1a2b3c4d`; the operator cannot paste it into `arcanum operation show`.
- **Evidence:** P5 (`probe-u28-usability/P5.txt`): width 80 -> ID on 3 lines; width 120 -> `3f2c9a4e-7b1d-4c58-9e0a-5d6` / `f1a2b3c4d`; width 160 and 200 intact.
- **Fix:** Reuse `ListingTable` (drop Kind/Summary columns first, name them on stderr) or mark the ID column no-wrap.
- **Effort:** S.

### 4. [Minor] `operation list` silently shows only the newest 100 operations; the store silently clamps `limit`, the response has no continuation, and the CLI cannot page

- **Where:** `Data/LongRunningOperationStore.cs:546-576` (`Math.Clamp(query.Limit, 1, 500)`, `Math.Max(0, query.Offset)`), `Api/Operations/LongRunningOperationEndpoints.cs:36-40` (default 100, bare array),
  `Cli/Services/ArcanumApiClient.cs:465-480` and `Cli/Infrastructure/CliCommandTree.Operations.cs:16-19` (no `--limit`/`--offset`).
- **Defect:** `GET /api/operations` has `limit` and `offset` but returns a bare array: no `hasMore`, no total, no cursor. `limit=0` or a negative limit returns one row, `limit=1000` returns 500, silently. `Arcanum.API.md:254` says
  "optional `kind`, `state`, `limit`, and `offset`" and states no default, maximum or clamp. The CLI never sends a limit and prints no notice, so with more than 100 operations the older ones are invisible. Terminal history is retained 90 days
  (`DESIGN.md:699`), so an active installation can exceed 100. This contradicts the Command Reference's own rule (`:72`) that a local bound "must expose or automatically follow its continuation", and the house rule the memory-search route states in a comment ("Refused rather than clamped ... a full page is never mistaken for a complete one").
- **Scenario:** 150 operations exist; `arcanum operation list` shows 100 with no hint; the operator cannot find a Failed operation from last week without knowing to add `--state`/`--kind`.
- **Evidence:** P2 output: 100 / 1 / 1 / 500 / 20 as listed above.
- **Fix:** Return `hasMore` (read `limit+1`) or a cursor, document default/maximum in `Arcanum.API.md`, refuse out-of-range `limit`/`offset` with 400 (as memory search does), and give the CLI `--limit/--offset` plus a stderr continuation notice (as `session list` does) or follow pages.
- **Effort:** M.

### 5. [Minor] Saga memory text search is case-insensitive for ASCII only, but the API documents it as "Case-insensitive"

- **Where:** `Data/SagaMemoryStore.cs:640-645` (`m."Content" LIKE @query ESCAPE '\'`), used by `saga list --query`, `memory saga` listings and `POST /api/memory/search` (scope `saga`); doc `Arcanum.API.md:98`.
- **Defect:** SQLite `LIKE` folds case only for ASCII (the hermetic SQLCipher build has no ICU). Non-ASCII memories are matched case-sensitively and the docs do not say so.
- **Scenario:** A memory "Eric aime les eclairs" with accents is stored; `arcanum memory search` for the accented-lowercase "eric" or an accented-uppercase "ECLAIRS" returns nothing, with no hint that case mattered.
- **Evidence:** P3 output; `sqlite3 :memory: "select 'e-acute-clair' like '%E-ACUTE-CLAIR%'"` -> 0. No doc mentions ASCII-only folding (`rg -i "ASCII.*(case|LIKE)" docs/Arcanum.DESIGN.md` finds nothing relevant).
- **Fix:** Say "ASCII case-insensitive" in `Arcanum.API.md:98` and the CLI help, or fold on a normalized column / registered Unicode collation / the FTS index the Archive search already uses.
- **Effort:** S (doc) / M (behaviour).

### 6. [Minor] Attachment source revalidation turns any non-I/O exception into a persisted `CorruptMetadata` with a generic reason and no log; the reconcile variant also swallows cancellation

- **Where:** `Data/SessionAttachmentStore.Lifecycle.cs:476-488` (`RevalidateAttachmentSourcesAsync`, startup reconcile) and `:535-559` (`RevalidateBoundSourcesAsync`, driven by the debounced workspace watcher). The first uses `catch (Exception)`
  (also catching `OperationCanceledException`); the second rethrows cancellation but then does the same for everything else.
- **Defect:** `AttachmentSourceResolver.RevalidateAsync` already maps missing/inaccessible/unsafe/changed cases to precise statuses and reasons. What reaches the store's catch is everything else, notably a failure reading the workspace registry,
  which is Campaign-backed and so a Grimoire read (`CampaignBackedWorkspaceRegistry`). The store records `CorruptMetadata` / "Source metadata could not be safely revalidated." and writes it to the row via `UpdateSourceAsync`, although the stored metadata is fine,
  and logs nothing. The status then appears in attachment listings and refusals of `refresh_session_file`, with no log line that connects it to a cause.
- **Scenario:** A transient registry read fails during a watcher-driven revalidate; the attachment is marked corrupt until the next successful pass; the operator has nothing to search the log for.
- **Evidence:** P4: returned and persisted `Status=CorruptMetadata Reason=Source metadata could not be safely revalidated.`, `log entries: 0`.
- **Fix:** Log the exception at Warning with the attachment id; leave the persisted row unchanged for exceptions that are not about its metadata; rethrow `OperationCanceledException` in the reconcile variant as the sibling does.
- **Effort:** S.

### 7. [Minor] `MemoryErasureGuard.KeyUnavailableError` omits the remedy its sibling `KeyLostError` carries

- **Where:** `Data/MemoryErasureGuard.cs:106-112` (copied for erase flows in `Memory/MemoryErasureProtocol.cs:54-56`).
- **Defect:** `KeyLostError` ends "Run 'arcanum memory erasure status'." `KeyUnavailableError` is "The erasure key could not be read, so automatic writes to this store are withheld until it can be." with no pointer. These strings are the final text for automatic writers
  (Lexicon scribe and agent Covenant proposals return it to the model via `LexiconService.cs:285-288` / `CovenantAgentErasureGate.cs:67`), and the Saga extraction deferral log (`SagaExtractionService.cs:756-758`) carries neither code nor remedy. The unlock-the-credential-store guidance lives only in `status` output (`Command.Reference.md:728`).
  The startup warning (`MemoryErasureKeyWarmup.cs:71-73`) does name the command, so the log and the error disagree about what to do.
- **Fix:** Append "Run 'arcanum memory erasure status'." to `KeyUnavailableError` (the erase-flow copy can stay because the CLI substitutes its own explanation, `Command.Reference.md:730`), and add the code and the command to the extraction deferral warning.
- **Effort:** S.

### 8. [Minor] Docs name an `arcanum operations` verb that does not exist and show a typed-looking `operation list --json | jq` example

- **Where:** `docs/Arcanum.DESIGN.md:1093` ("`arcanum operations`") and the same words in source comments `Infrastructure/A2A/A2ASendingRecoveryHandlers.cs:17`, `Infrastructure/A2A/ArcanumA2ATaskStore.cs:236`; `docs/Arcanum.DESIGN.md:397` (example `arcanum operation list --json | jq`).
- **Defect:** The command is `operation` (`CliCommandTree.Operations.cs:12`, no alias; `DESIGN.md:1842` and the Command Reference both say `operation`). And `operation` is in the Command Reference's text-envelope list (`CliJsonEnvelopeContractTests` pins it), so `operation list --json` yields `{ "output": "<table text>", "exitCode": 0 }`, not rows `jq` can address (and the table text has finding 3's wrapped IDs).
- **Fix:** Correct the verb at the three places; replace the `jq` example with a verb that has a typed payload.
- **Effort:** S.

---

## Checked and found sound

- Sanctum breach history (`SanctumBreachRepository`, `SanctumEndpoints.cs:107-150`): `hasMore` read via limit+1, `before` cursor, documented default 100 / clamp 1-1000 equal `ArcanumSettingClamps.SanctumBreachQueryLimit`; retention trim keeps newest.
- Saga curation errors (`SagaCurationService.MapOutcome`): each outcome maps to an actionable message; a wrong-width embedding is refused with `Saga.EmbeddingUnavailable` before the store's dimension guard (whose message names `Arcanum:Integrations:Embeddings:Dimensions`) can be reached.
- `GET /api/memory/explain` validates `similarity`/`sagaId` and answers 400 before `ExplainRankingAsync`'s argument check; the hard-coded modifier bounds -0.09/+0.13 equal the extremes of `SagaRankingPolicy` (0.04+0.09; -0.04-0.05).
- `KeyLostError` and the warm-up warning name `arcanum memory erasure status`; the Command Reference text for `status`, `reset-key` and `release` matches the strings (including "`MemoryErasure.KeyLost` message already ends with Run ...").
- Saga listing order (`CreatedAt DESC, Id DESC`), `saga stats` fields and the Command Reference `saga list` description agree with `BuildListCommand`/`GetStatsAsync`.
- `DescribeRetentionConflictAsync` reads `ListAsync(Kind: ...)` with the default 100-row window; it cannot miss its blocker because single-flight stops newer rows of the kind while the blocker is active.
- `A2ASendingLedger.FindOpenAsync` and `InstallationResetExistingGrimoire` page `ListAsync` within the 500 clamp, so the clamp does not affect them.
- Native-runtime load failure is reported with the stable code `Grimoire.NativeRuntimeUnavailable` by `SqliteNativeRuntimeValidator`; only the per-open factory paths lose it (finding 1).
- `StartupRecoveredAttention` and `AdoptUnderInstallationLockAsync`: the SQL arms match their doc comments and the Command Reference list of pairs.
- Attachment lifecycle logs (`SessionAttachmentStore.Lifecycle.cs`) carry attachment id and relative path; reconcile is a one-shot at startup (`SessionAttachmentPendingGcHostedService`), so Error lines do not repeat per cycle.
- Idempotency claim store: conflict / in-progress outcomes map to the documented 409 codes with actionable messages; the `<=` vs `<` lease comparison between `TryAcquireAsync` and `TryReclaimAsync` only matters at an exact-equality instant and fails safe.
