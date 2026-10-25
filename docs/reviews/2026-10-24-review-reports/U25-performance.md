# U25 (Arcanum.Infrastructure, Data (1)) - PERFORMANCE review

Tree: `.claude/worktrees/codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e`. Read-only. Probes ran in a scratch worktree
(`scratchpad/review/wt/U25-perf`, removed afterwards); probe sources are kept in `reports/C/U25-probes/`.

## Coverage notes (read this first)

* `units.json` did not exist at the path the task named (`scratchpad/review/` had not been created), so the unit's file list was
  **inferred**: the first ~10.5k lines, in byte-sorted order, of `src/RetroDownfall.Arcanum.Infrastructure/Data/**/*.cs` (excluding `Migrations`).
  That is exactly 28 files / 10,501 lines, ending at `Covenant/CovenantCleanupWorker.cs` (the task says "about 10549"):
  `Annals/{AnnalsClaimWriter,AnnalsErasurePlan,AnnalsStore}`, `ArcanumDbContext`, `ArcanumDbContextFactory`, `ArcanumDbContextOptionsConfigurator`,
  `AttachmentMemoryProvenanceStore`, `BatchAccountingRecoveryStore`, `BatchRepository`, `BlobEncryptionMetadataStore`, `BudgetAlertRepository`,
  `BudgetReservationService`, `Covenant/{ArtifactSensitivityLedger, ArtifactSensitivityLedger.LexiconReplacement, CampaignMaintenanceCheckpointStore,
  CampaignPathFullResetCleanupEvidenceStore, CampaignPathMarkerIntentStore, CampaignRollupStore (+ .Manifests/.Read/.Write), CampaignSummaryLifecycle,
  CovenantAgentErasureGate, CovenantArtifactPlanRunner, CovenantAvailability, CovenantCanonicalContentTables, CovenantCanonicalErasureTransaction,
  CovenantCleanupWorker}`. All 28 were read in full.
* As a hedge against a boundary a few hundred lines either way I also read in full: `Covenant/CovenantConnectionEnrolmentInterceptor`,
  `CovenantConnectionDrain`, `CovenantClosedPeriodAuthority`, `CovenantSqliteConnectionInitializer`, `ICovenantConnectionSource` (head),
  `CovenantIdentitySql`, `GrimoireOrdinaryConnectionLifecycle`, `SqliteBusyRetry`, `EfSaveChangesRetry`, `ExactUsdText`,
  `SqliteConnectionPragmas`, `SqlitePragmaStatementFactory`, `SqliteScratchDatabase`, `GrimoireCoreSchemaVersion`, and the schema `.sql`
  files for every table those files touch.
* Machine state: the shared box had a load average of 100-166 for the whole session (many parallel reviewers building). Wall-clock numbers
  below are therefore inflated and noisy; I report **process CPU time** (`Process.TotalProcessorTime`, or sqlite3 `user`) as the primary figure
  and give wall only where it is informative. All timings use the product's real SQLCipher build via `GrimoireFixture`, unless marked "plain
  sqlite3" (the macOS sqlite3 CLI against the repository's own `Schema/Tables/*.sql`, used for query plans).

## Findings (ranked)

### 1. [Important] `SumOutstandingAsync` scans every `BudgetReservations` row ever written, inside the budget write transaction and the per-turn gate
`BudgetReservationService.cs:741-772` (predicate at 753):
`WHERE ("BudgetPeriod" = @period AND "Status" = @reserved) OR "Status" = @uncertain`.
The only index is `IX_BudgetReservations_BudgetPeriod_Status ("BudgetPeriod","Status")`; the second OR branch has `Status` as its
trailing column and the database never gathers statistics (no `ANALYZE`/`PRAGMA optimize` anywhere in `src`), so there is no skip-scan.
`EXPLAIN QUERY PLAN` (real DB): `SCAN BudgetReservations`.

Who pays: `ReserveAsync` (line 82-88, inside `BEGIN IMMEDIATE`), `AdjustAsync` (210-221), `RecheckDailyLimitAsync` (335-351; DESIGN 22.2 says it
runs before every main/tool-continuation call), `GetTodayOutstandingReservationsAsync` (every turn through `BudgetMonitor.CheckAsync` ->
`DailySpendAuthority`, `WizardIntelligenceProvider.cs:675/1053`, and `/api/budget`). So a turn with R provider rounds does about 4+2R full scans, and
`Reserve`/`Adjust` hold the single SQLite write lock for the duration.

Growth: rows are never removed by default. `RetentionSettings.AutomaticSweepsEnabled = false` and `Accounting` retention is 365 days even when on
(`AccountingMinimumDays = 365`); one row per accounted operation. Applies whenever `cost.budget.enabled` is true (reservations are a no-op otherwise).

Measured (SQLCipher, `P1`, 20 iterations, CPU ms per call; "bills" = BillableOperations completed today, "resv" = historical Reconciled reservations):

| bills / resv | ReserveAsync | GetTodayOutstanding | GetTodayCommitted |
|---|---|---|---|
| 0 / 0 | 0.54 | 0.06 | 0.03 |
| 2,000 / 5,000 | 7.0 | 0.51 | 5.35 |
| 10,000 / 30,000 | 21.9 | 12.5 | 12.0 |
| 30,000 / 100,000 | 79.5 | 45.2 | 33.2 |

The indexable half of the predicate alone (`BudgetPeriod=? AND Status=?`) costs 0.075 ms at 100k rows, so the OR is the whole cost (about 600x).
Plain sqlite3 at 730k rows (one year at 2k operations/day): 27 ms per scan vs 0.064 ms.
Verified fix (plain sqlite3, 730k rows): add `CREATE INDEX IX_BudgetReservations_Status_Period ON BudgetReservations(Status, BudgetPeriod)` and run the
two branches as `UNION ALL`: plan becomes two `SEARCH ... USING INDEX`, 27 ms -> 0.05 ms.

### 2. [Minor] `SumCommittedAsync` re-reads and re-sums every bill completed today, per call, with a BigInteger add per row, inside the write transaction
`BudgetReservationService.cs:699-739` (loop 733-736) with `ExactUsdText.CheckedAdd` (`ExactUsdText.cs:51-89`). The class summary promises "tiny BEGIN IMMEDIATE
transactions"; the transaction does O(bills completed today) work at every reserve/adjust, so a day's total work is quadratic in the number of
provider calls. `CheckedAdd` builds two `int[4]`, two `BigInteger`s, two `BigInteger.Pow(10,k)` and a divide loop per row.
Measured (`P7`): 1.2-1.4 us and 421 B allocated per row (12 MiB of garbage per 30k-row sum) vs 0.04-0.2 us for a plain `decimal +=` of the same values
(equal totals). `P1`: 5.4 ms CPU at 2k bills, 12 ms at 10k, 33 ms at 30k, per call. Only noticeable beyond roughly 5k bills/day, hence Minor.
Fix: accumulate once into an `Int128`/scaled-integer (fall back to BigInteger only on overflow), add a covering index
`(CompletedAt, ActualCostUsd)`, or keep a per-day running total written in the same transaction as the BillableOperation row.

### 3. [Important] Each Campaign rollup revision copies every prior source, so `campaign_rollup_sources` grows quadratically and each publish does several O(N) passes under the write lock
`CampaignRollupStore.cs:131-156` (`INSERT ... SELECT ... FROM campaign_rollup_sources WHERE RollupArtifactId = $previous`, then one INSERT per addition),
`FullSourceManifestAsync` (`CampaignRollupStore.Manifests.cs:86-150`, one `ReadMetadataAsync` per prior source) and `PrepareRollupWithinAsync` re-run inside
`PublishRollupAsync` (`CampaignRollupStore.cs:109`) which in turn runs `VerifySourcesAsync` over the previous revision. No revision is ever deleted by the
store; `CampaignSummaries` retention is disabled by default (`Enabled=false`, no automatic sweeps).
Measured (`P8`, SQLCipher, one fold per added Session, `features.campaignRollups` on): rows after 10/50/100/200/400/600 Sessions =
55 / 1,275 / 5,050 / 20,100 / 80,200 / 180,300 (exactly N(N+1)/2). `PublishRollupAsync` write-transaction CPU: 1.1 / 3.2 / 7.0 / 11.9 / 29.5 / 48.9 ms
(about 0.08 ms per source, linear). A 2,000-Session Campaign would hold about 2M rows and hold the write lock about 160 ms per fold (extrapolated).
Fix: delta-encode (a revision stores only its additions plus a base-revision pointer), or delete revisions older than the newest one no live turn has
bound; keep the manifest digest incremental.

### 4. [Important] Every lookup of `campaign_rollup_sources` by `SessionId` is a full scan, and `ReadSessionClosureAsync` materialises the whole closure into multi-megabyte strings
Statements: `CampaignSummaryLifecycle.cs:40` (`EnsureSessionUnlabeledAsync`, reached from `GrimoireRepository.DeleteEntryAsync` at `GrimoireRepository.cs:747`
inside `BEGIN IMMEDIATE`, from `CampaignRepository.cs:505` and from every retention Session candidate at `DataRetentionService.Pruning.cs:5475`), `:141`
(`ReadDependencyOwnersAsync`), `:200-210` (`ReadSessionClosureAsync`). The table's only indexes are the primary key `(RollupArtifactId, SessionId)` and
`ContributionArtifactId`; `EXPLAIN QUERY PLAN` shows `SCAN campaign_rollup_sources USING COVERING INDEX sqlite_autoindex_...` for each. With finding 3
the table is O(N^2), and a Session present in all R revisions yields R*N-ish closure rows.
`ReadSessionClosureAsync` (`CampaignSummaryLifecycle.cs:179-250`) additionally appends every closure row to two `StringBuilder`s (`authority`, `rowKeys`) and
hashes `authority.ToString()`; the planner (`DataRetentionService.cs:3882`) and the applier (`:4145`) each call it per Session candidate.
Measured (`P8`, SQLCipher): at 20,100 rows the SessionId lookup costs 3.5 ms CPU; at 180,300 rows **28.7 ms per Entry delete / Session candidate**
(`DISTINCT RollupArtifactId WHERE SessionId=?`). `ReadSessionClosureAsync` for the oldest Session at 180,300 rows: **534 ms CPU, 180,902 rows, 17.7 M chars
of `RowKeys`, 418 MiB allocated by one call** (20,302 rows / 56 ms / 2.0 M chars at 20,100 rows). At 500k rows that is about 1.1 GiB allocated per
closure read (extrapolated).
Fix: `CREATE INDEX idx_campaign_rollup_sources_session ON campaign_rollup_sources(SessionId, RollupArtifactId)` (and `campaign_maintenance_checkpoints(SessionId)`,
`(SourceSessionId)`, which the `OR` at `:207` scans today); feed the closure rows into an `IncrementalHash` and keep only the key set that is actually consumed,
instead of two unbounded `StringBuilder`s.

### 5. [Minor] Validating a bound Campaign rollup re-verifies every source with about five queries each, twice per provider dispatch
`CampaignRollupStore.Read.cs:196-239` -> `VerifySourcesAsync` (`CampaignRollupStore.Manifests.cs:16-84`): per source `ReadMetadataAsync`, `ReadStateAsync`,
`ReadCanonicalCampaignAsync` (3-way join), `VerifyArtifactLabelAsync` (presence probe + point read), all in one pass per 128-source page. It is used by
`ValidateAsync` and `ReadCurrentAsync`. `WizardIntelligenceProvider.AcknowledgeCovenantDispatchAsync` (`WizardIntelligenceProvider.cs:7737` and `:7810`)
calls `campaign.ValidateAsync` before **and** after the dispatch acknowledgement, i.e. twice per provider round for any turn that carries a rollup.
Measured (`P5`, SQLCipher, unlabelled sources): `ValidateAsync` 3.7 ms CPU at 100 sources, 11.3 ms at 300 (about 37 us per source, linear).
At 600 sources that is about 44 ms per round for validation alone. Fix: validate the rollup row plus its `SourceCount`/manifest digest and rely on a per-source
generation counter (`campaign_contribution_state.SourceGeneration`) rather than re-reading each source.

### 6. [Minor] Subject-scoped Annals erasure appends `OR NOT EXISTS (orphan)` clauses that turn two DELETEs/counts into full-table scans of the receipt tables
`AnnalsErasurePlan.cs:99-107` (`long_rest_suppressions`, `long_rest_receipt_inputs`): `... ReceiptId IN (...) OR NOT EXISTS (SELECT 1 FROM long_rest_receipts ...)`.
An OR with a non-indexable branch forces `SCAN long_rest_receipt_inputs` (plan verified). These statements run for every erased Saga memory
(`SagaMemoryStore.DeleteAsync`, `SagaMemoryStore.cs:827`), every Lexicon entry delete (`LexiconService.cs:452`) and, via `SagaLongRestPruneSteps`
(`DataRetentionService.Pruning.cs:2229`), as count + delete (+ planning preview) **per retention candidate** (`:6018`). `long_rest_receipt_inputs.SnapshotJson` is
up to 1 MiB per row, so the scan is over the widest table in the schema.
Measured (`P4`, SQLCipher, 20k receipts / 80k inputs / 138 MB): with the clause 20 ms CPU (receipt_inputs) + 4-5 ms (suppressions) per statement;
without it 0.1-0.3 ms. A retention sweep over K candidates therefore costs K x 2-3 x ~25 ms of write-locked scanning at this size and grows with the receipt count.
Fix: keep the orphan sweep for the store-wide `ForStore` plan only (FKs are enforced and read back on every connection), or run it once per sweep.
The per-step `GrimoireCoreSchemaVersion.ReadAsync` in `AnnalsClaimWriter.DeleteInOrderAsync` (about 25 one-row queries per erase) is negligible next to this.

### 7. [Minor] Past the alert threshold every turn attempts a duplicate `BudgetAlerts` insert, throws, and logs a warning; the cheap pre-check is unused
`BudgetMonitor.CheckAsync` (Api) calls `TryDispatchAlertAsync` -> `BudgetAlertRepository.RecordAlertAsync` (`BudgetAlertRepository.cs:19-60`) on every turn once spend
crosses the threshold (and again at 100%). The unique index `(Threshold, date(AlertedAt))` rejects it; the catch logs `LogWarning` each time.
`HasAlertedTodayAsync` (`:66`) exists but has no production caller. Measured (`P9`, SQLCipher): 47 us CPU, 7 first-chance exceptions and 1 Warning log line **per turn**
for the rest of the UTC day, plus a write-lock attempt, vs 7 us for the unused read. CPU is small; the cost is log noise and an avoidable write-lock acquisition on every turn.
Fix: `HasAlertedTodayAsync` first (or cache the last-alerted day per threshold in the singleton `BudgetMonitor`).

### 8. [Minor] `MergedProvenanceDigestAsync` reads every label of the Session on each tainted write; the comment says the indexed lookup keeps it bounded
`ArtifactSensitivityLedger.cs:651-685` (`SELECT DISTINCT ... FROM artifact_sensitivity WHERE SessionId = $sessionId`, called from `AdvanceProjectionAsync` at `:624` and
`ReplaceLexiconWithinAsync` at `.LexiconReplacement.cs:126`). `idx_artifact_sensitivity_session` is on `SessionId` alone, so every label row is fetched
(table lookup for the two blobs) and de-duplicated in a temp B-tree; DISTINCT bounds the merge, not the read. Measured (`P3`, SQLCipher): 0.04 ms at 100 labels,
0.095 ms at 1,000, **6.6 ms at 10,000, 37 ms at 50,000**, per labelled write, inside the writer's transaction. Only very long Covenant Sessions reach it.
Fix: keep the merged provenance (at most 128 B) on `session_sensitivity_state` and fold the new label into it, or at least add a covering index.

### 9. [Minor] `ReadPageWithinAsync` keyset pagination uses `($after IS NULL OR LabelId > $after)`, so every page re-scans the index from the start
`ArtifactSensitivityLedger.cs:340`. A parameter-guarded OR is not a range constraint: the plan is `SCAN artifact_sensitivity USING COVERING INDEX` with a residual filter, so
page k reads k pages of entries and an inventory walk is O(n^2 / pageSize). Measured on the repository schema (plain sqlite3/python, 50,000 labels, 256 per page, 196 pages):
169 ms CPU for the OR form vs 25 ms for `WHERE LabelId > ?` with `''` as the first cursor. At 500k labels the quadratic term is about 100x (extrapolated: ~17 s vs ~0.25 s).
Only the erasure inventory walks this. Fix: bind `''` for the first page and drop the OR. The same shape in `CampaignRollupStore.Manifests.cs:158` is bounded by one rollup's sources.

## Checked and found sound (with how)

* Per-connection policy cost on the request path is not a finding. `P2` (SQLCipher, pooled): bare open+close 0.001 ms CPU, plus 15 `CreateFunction` registrations 0.033 ms, full
  `CovenantSqliteConnectionInitializer.InitializeAsync` (functions + 5 pragmas + 5 read-backs) **0.044 ms** per open. `P6`: an EF context open+close through the real
  `CovenantConnectionEnrolmentInterceptor` costs 0.35 ms CPU vs 0.42 ms with no interceptor (noise). The cold unpooled open is 61 ms CPU (PBKDF2), which pooling avoids; `GrimoireOrdinaryConnectionLifecycle`
  takes one short in-memory lock per open and does no I/O under it.
* `AnnalsStore` reads: `idx_annal_versions_predecessor` serves the correlated successor subquery; claim/head/version lookups are PK or unique-index probes (`annal_claims`, `annal_heads`, `annal_versions` DDL read).
* `AnnalsClaimWriter` appends: 5-6 indexed statements per claim, all on the caller's transaction; `lexicon_annal_fact_provenance` snapshot joins on `(EntryId, FactHash)` PK prefix, bounded by one entry's facts.
* `BatchRepository`: `ListPageAsync` unfiltered plan is `SEARCH Batches USING COVERING INDEX IX_Batches_CreatedAt_Id`; status-filtered and pending plans use `IX_Batches_Status` (tiny table); line checkpoints use PK `(BatchId, LineNumber)` and `(BatchId, State, LineNumber)`. (Plans from `EXPLAIN QUERY PLAN` on the repo DDL.)
* `BatchAccountingRecoveryStore`, `BlobEncryptionMetadataStore`, `AttachmentMemoryProvenanceStore`, `CampaignMaintenanceCheckpointStore`, `CampaignPathMarkerIntentStore`, `CampaignPathFullResetCleanupEvidenceStore`, `CovenantCleanupWorker`,
  `CovenantCanonicalErasureTransaction`, `CovenantArtifactPlanRunner`: recovery/maintenance/migration paths; statements use PK/unique indexes; per-row commands are bounded by a page or a batch (64 owners).
  `ListConsultationsAsync` uses `IX_attachment_memory_consultations_Session_Time` and joins `Entries` by PK (O(consultations of one Session), once per summarisation).
* `CampaignRollupStore.FindPendingContributionsAsync`: plan scans the Campaign-bound Sessions (`SCAN b`) with indexed probes per row, once per Loremaster sweep interval (minutes); acceptable.
* `ArtifactSensitivityLedger` point reads and `ReadProjectionWithinAsync` (the per-turn dispatch gate read) are unique-index/PK seeks.
* `SqliteBusyRetry`/`EfSaveChangesRetry`: closure + delegate per call, no polling; backoff capped at 2 s and bounded by the 2-minute deadline.
* Considered and not reported: `CampaignMaintenanceCheckpointStore.WithinAsync` opens `BEGIN IMMEDIATE` for read-only `ValidateClaim/ValidateAttempt` (a few calls per maintenance provider call; contention-only, no
  measurable cost); `length(CAST(Content AS BLOB))` in rollup metadata reads whole entry bodies (measured 14 ms per 64 MB of entries vs 0.2 ms for `octet_length`, only for very large entries); the global lack of `cache_size`
  tuning showed no effect in `P4` (scan CPU 20 ms at the default vs 18 ms with a 256 MB cache).

## Probes run

| id | what | result |
|---|---|---|
| P1 | `BudgetReservationService` Reserve/Committed/Outstanding vs ledger size, real SQLCipher DB | table in finding 1; plans: `SCAN BudgetReservations`, `SEARCH ... CompletedAt` |
| P2 | pooled open cost: bare / select 1 / +CreateFunction x15 / +`InitializeAsync` | 0.001 / 0.004 / 0.033 / 0.044 ms CPU; cold unpooled 61 ms CPU |
| P3 | `SELECT DISTINCT` provenance query vs labels per Session | 0.04 / 0.095 / 6.6 / 37 ms at 100 / 1k / 10k / 50k |
| P4 | `AnnalsErasurePlan` orphan clause on a SQLCipher copy of the receipt tables (80k inputs) | 20 ms vs 0.1 ms; cache_size 256 MB 18 ms |
| P5 | `CampaignRollupStore.ValidateAsync`/`ReadCurrentAsync` vs sources | 3.7 ms @100, 11.3 ms @300 |
| P6 | EF context open+close through the real enrolment interceptor | 0.345 ms vs 0.422 ms plain |
| P7 | `ExactUsdText` parse + `CheckedAdd` per row | 1.2-1.4 us, 421 B/row vs 0.04-0.2 us plain decimal |
| P8 | rollup folds, one per added Session, to 600 Sessions | rows N(N+1)/2 = 180,300; publish 49 ms; SessionId lookup 28.7 ms; closure 534 ms / 418 MiB |
| P9 | duplicate budget-alert insert after threshold | 47 us, 7 exceptions, 1 warning per call; pre-check 7 us |
| plain sqlite3 | outstanding scan at 730k rows 27 ms; UNION ALL + `(Status,BudgetPeriod)` index 0.05 ms; keyset walk 169 vs 25 ms; plans for 10+ statements | see findings |

Raw test output lines are quoted in the findings. Sources: `reports/C/U25-probes/`.
