# Issue 223 timestamp comparison audit

Audit baseline: `8f2dfeed2f906f2227ac2acc2cd0d42579c7aead` (`memory-enhancement` lineage), 2026-10-21. Source line numbers below refer to that baseline. Paths in the tables are relative to `src/RetroDownfall.Arcanum.Infrastructure/` unless otherwise stated.

## Finding and scope

The baseline already contains the canonical UTC persistence repair. No remaining production comparison examined passes a typed `DateTimeOffset` or `DateTime` directly to the SQLite driver against a persisted timestamp TEXT column. The remaining issue-223 work is to establish production-entry-point regression evidence, prove that reverting each protected binding makes its regression fail, and close the source guard's comparison-discovery gap.

This audit covers authored C# SQL in all `src/` projects, including SELECT, UPDATE/DELETE predicates, dynamic filter fragments, nullable equality, timestamp cursors, aggregate comparisons, and `julianday` predicates. The search used `rg --no-config` for timestamp properties, every SQL comparison followed by a parameter, `AddParameter`/`AddWithValue`/`DbParameter.Value`, EF raw-SQL/interpolated call sites, and SQLite date functions. Column-name searches were cross-checked against both `UtcInstantColumnInventory.Core` and `.CovenantCanonical`, then SQL builders and binders were read together. Generated EF sources were inspected for their installed UTC type mappings rather than treated as authored query sites. In-memory date comparisons and numeric counters were separated from SQL timestamp text comparisons.

The inventory is source evidence, not a claim that every legacy branch was separately reproduced. In the tables, **C** means the writer and bound comparison value use the same canonical UTC encoding; **S** means the parameter is exact timestamp text read from storage rather than a driver-rendered instant; **J** means SQLite parses both sides through `julianday`, so separator spelling cannot cause the reported lexical mismatch. Every row is unaffected at the audit baseline. Historical exposure is discussed separately below.

## Writer and parameter contract

`Data/UtcInstantText.cs` defines `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`, normalizes offsets to UTC, preserves seven fractional digits, treats historical suffix-free values as UTC, and rejects local `DateTime` at the shared formatting boundary. This supersedes the earlier mixture of driver-rendered space-separated timestamps and round-trip strings. Merely requiring an `O`/`o` string is insufficient for mixed offsets; the repository now has one UTC suffix and one width.

`Data/UtcInstantSql.cs` binds typed instants only after formatting and normalizes imported stored text. `Data/GrimoireEntitySql.cs` supplies the same codec for repository writes and comparisons. `Data/UtcInstantTypeMappings.cs` and `Generated/UtcInstantEntityTypeCustomizations.cs` install the same encoding for EF tracked writes in the compiled model. Direct writers in the stores below use `UtcInstantText.Format`, `UtcInstantSql`, or local `Format`/`Iso` helpers that resolve to the codec. `Data/Schema/UtcInstantCanonicalizationBackfill.cs` and `UtcInstantColumnInventory.cs` normalize historical stored rows before ordinary application work uses them. Thus readers are not relying on comparison-side formatting alone while leaving historical row encodings mixed.

## Lexical comparison inventory

Each occurrence is enumerated below, including identical predicates in distinct production paths. Multiple line numbers in a row identify separate occurrences, not a sample.

| Source and baseline lines | Production method or route into helper | Persisted timestamp predicate(s) | Binder and writer evidence; status |
| --- | --- | --- | --- |
| `Repositories/EntryTemporalQueries.cs:311-312` | `LoadBeforeDeletedKeysetAsync` → `CreateLoadBeforeDeletedKeysetCommandAsync` | `CreatedAt < $beforeCreatedAt`; `CreatedAt = $beforeCreatedAt` | `:319` binds `GrimoireEntitySql.Format(beforeCreatedAt.ToUniversalTime())`; Entry writes use EF UTC mapping or `SessionEntryPersistence.AddEntryParameters`; C. |
| `Repositories/EntryTemporalQueries.cs:372` | `CountAfterAsync` → `CreateCountAfterCommandAsync` | `CreatedAt > $afterExclusive` | The command binds formatted UTC; Entry writer as above; C. |
| `Repositories/EntryTemporalQueries.cs:399,408` | `LoadAfterWatermarkThroughTimestampGroupAsync` → its command builder | Both boundary selection and returned-row selection: `CreatedAt > $afterExclusive` | Shared `BindWatermarkParameters` at `:580-591` formats the instant; C. The additional `CreatedAt <= Boundary.CreatedAt` at `:412` compares stored values to stored values. |
| `Repositories/EntryTemporalQueries.cs:443,450` | `CountAfterWatermarkThroughTimestampGroupAsync` → its command builder | Both boundary selection and count: `CreatedAt > $afterExclusive` | Same shared binder; C. The `CreatedAt <= Boundary.CreatedAt` at `:454` is storage-to-storage. |
| `Repositories/SessionRepository.cs:139,144,191` | `ListAsync` | `UpdatedAt >= from`; `UpdatedAt <= to`; `UpdatedAt < before` | Each dynamic `$pN` value is `GrimoireEntitySql.Format(value.ToUniversalTime())`; Session inserts/updates use that helper and compiled EF UTC mapping; C. |
| `Repositories/SessionRepository.cs:310,329` | `ListAsync` → `LoadTieGroupAsync` | `UpdatedAt = $pN`; older-row existence `UpdatedAt < $pN` | The boundary parameter at `:313-315` is formatted; C. |
| `Repositories/ApprenticeRepository.cs:84,140,166` | `ListAsync` main page, complete tie group, and older-row existence | `UpdatedAt < $before`; `UpdatedAt = $boundary`; `UpdatedAt < $boundary` | `:86,137,149,175` use formatted UTC text; Apprentice tracked writes have the compiled UTC mapping; C. |
| `Repositories/GrimoireRepository.cs:1193` | `GetSessionsNeedingSummarizationAsync` | `UpdatedAt < $idleCutoff` | `:1173-1180` explicitly interprets UTC/local/unspecified caller `DateTime`; `:1200` formats resulting UTC offset. Session writes canonical; C. |
| `Repositories/GrimoireRepository.cs:1378` | `GetTodaySpendAsync` | `CreatedAt >= $dayStart`; `CreatedAt < $dayEnd` | `:1385,1389` use `UtcInstantText.Format` on UTC day boundaries. Session `CreatedAt` writer canonical; C. |
| `A2A/A2AExternalSpendLedger.cs:66-67` | `GetTodayAsync` | `LongRunningOperations.CompletedAt >= @dayStart`; `< @dayEnd` | `:76,78` format UTC midnight/day end. Operation terminal writes use canonical `Format`; C. |
| `Data/BudgetReservationService.cs:413` | `ExtendExpiryAsync` | `ExpiresAt < @expires` | `:417` formats new expiry, as does reservation creation `:120`; C. |
| `Data/BudgetReservationService.cs:618` | `SweepExpiredAsync` | `ExpiresAt < @now` | `:623` formats now; reservation creation `:120` formats expiry; C. |
| `Data/BudgetReservationService.cs:723` | `GetTodayCommittedSpendAsync`, `ReserveAsync`, `AdjustAsync`, `RecheckDailyLimitAsync` → `SumCommittedAsync` | `BillableOperations.CompletedAt >= @dayStart`; `< @dayEnd` | `:713,715,726-727` create/bind canonical strings. BillableOperation writes use the shared codec; C. `BudgetPeriod` equality elsewhere compares a calendar-day key, not an instant. |
| `Data/IdempotencyStore.cs:34` | `TryGetAsync` | `CreatedAt >= @notOlderThan` | `:40` formats cutoff; `SaveAsync:88` formats stored creation time; C. |
| `Data/IdempotencyStore.cs:107` | `DeleteExpiredAsync` | `CreatedAt < @olderThan` | `:110` formats cutoff; same writer; C. |
| `Data/IdempotencyClaimStore.cs:324` | `TryReclaimAsync` | `LeaseExpiresAt < @now` | `:332` formats now; `TryCreateAsync`/`HeartbeatAsync` format lease and timestamps; C. |
| `Data/IdempotencyClaimStore.cs:383` | `DeleteExpiredAsync` | `CreatedAt < @olderThan` | `:387` formats cutoff; creation `:167` formats creation time; C. |
| `Data/BatchRepository.cs:240-241,257-258` | `ListPageAsync` unfiltered and filtered cursor branches | Each branch has `CreatedAt < @afterCreatedAt` and `CreatedAt = @afterCreatedAt` | `:278-284` formats parsed cursor instant; `CreateAsync:77` formats stored time; C. |
| `Data/SanctumBreachRepository.cs:142` | `QueryAsync` | `OccurredAt < @before` | `:144` formats cursor; `RecordAsync` stores formatted occurrence; C. |
| `Data/SessionAttachmentStore.cs:1952` | `DeleteStalePendingAsync` → `ListStalePendingAsync` | `CreatedAt < @threshold` | `:1958` formats threshold; `InsertRowAsync:1660` formats creation time; C. |
| `Data/SessionAttachmentStore.cs:1993` | orphan pending-directory sweep → `ListStalePendingForTurnAsync` | `CreatedAt < @threshold` | `:2000` formats threshold; same writer; C. |
| `Data/LongRunningOperationStore.cs:624,626` | `FindExpiredAsync`, `FindExpiredForGenericRecoveryAsync` → private `FindExpiredAsync` | `CreatedAt <= @initialStartCutoff`; `LeaseExpiresAt <= @now` | `:659-660` bind `Format` → `UtcInstantText.Format`; creation and lease writes use same codec; C. |
| `Data/LongRunningOperationStore.cs:924` | `TryAcquireLeaseAsync`, `TryAcquireClassifiedRecoveryLeaseAsync` → `AcquireLeaseAsync` | `LeaseExpiresAt <= @now` | `:961` formatted now; same lease writer; C. |
| `Data/LongRunningOperationStore.cs:1054` | `HeartbeatAsync` → `RenewLeaseAsync` | `LeaseExpiresAt > @now` | `:1061` formatted now; same lease writer; C. |
| `Data/LongRunningOperationStore.cs:1237` | `ResetForRetryAsync` | `LeaseExpiresAt <= @now` | `:1248` formatted now; same lease writer; C. |
| `Diagnostics/OperationDiagnostics.cs:93` | `ReadAsync`, consumed by operation doctor diagnostics | `LeaseExpiresAt < $now` | `:96` formats now; operation lease writer canonical; C. |
| `Data/DataRetentionService.Pruning.cs:190` | prune planning → `ReadActiveIdempotencyConflictsAsync` | `LeaseExpiresAt > @now` | `:198` binds `FormatTimestamp(PrunePlanningTimestamp)` → shared codec; claim lease writer canonical; C. |
| `Data/DataRetentionService.FactoryReset.cs:937` | factory-reset conflict collection | `LeaseExpiresAt > @now` | `:945` formats current time; claim lease writer canonical; C. |
| `Data/Covenant/CampaignRollupStore.cs:242` | `FindPendingContributionsAsync`, `FindPendingContributionsForCampaignAsync` → `FindPendingContributionsWithinAsync` | `Sessions.UpdatedAt <= $cutoff` | `:257` formats UTC idle cutoff; Session writer canonical; C. |
| `Data/Covenant/SessionSummaryMaintenanceStore.cs:88` | `PublishAsync` | `Entries.CreatedAt > $through` | `:92` formats last summarized Entry instant; Entry writer canonical; C. |
| `Data/Covenant/SessionSummaryMaintenanceStore.cs:198` | `PrepareAsync` → `PrepareWithinAsync` | `Entries.CreatedAt > $watermark` when watermark exists | `:202` formats parsed prior watermark; summary writer `SessionDerivedArtifactStore` and Entry writer use codec; C. |
| `Memory/MemoryErasureExposure.cs:259,291` | `ReadSagaAsync`, `ReadLexiconAsync`, `ReadCovenantAsync` → `BackupAsync` and `ProviderContextAsync` | `MAX(DisclosedAtUtc) >= $windowStartUtc`; `DisclosedAtUtc >= $windowStartUtc` | `BindWindowStart:314-319` floors start to its millisecond then formats it. Disclosure journal writes canonical time reconstructed from receipt Unix milliseconds; C. Flooring is intentional conservative evidence inclusion. |
| `Data/LongRest/SagaRankingEvidenceReader.cs:58` | `ReadAsync` | `memory_evaluation_reports.CompletedAtUtc <= $asOf` | `:64` uses `UtcInstantSql.AddParameter`; report writer in `MemoryEvaluationEvidenceStore` uses codec; C. |
| `Data/LongRest/SagaRankingEvidenceReader.cs:265-267` | `ReadAsync` → `SuccessfulUsesAsync` | `FinalizedAtUtc <= $asOf`; `>= $validFrom`; `>= $recorded`; `< $validTo` when nonnull | `:277-285` use `UtcInstantSql` for every nonnull bound. Finalization and Annal version writers canonical; C. |
| `Repositories/GrimoireRepository.TurnCommit.SagaUses.cs:42-43` | `CommitTurnAsync` → `PublishSagaUsesAsync` | `ValidFromUtc <= $now`; `RecordedAtUtc <= $now`; nullable `ValidToUtc > $now` | `:69` binds through `UtcInstantSql`; Annal writers canonical; C. |
| `Data/LongRest/LongRestSweepStore.Pending.cs:126,172` | `FindNextRunnableCleanAsync` → `ReadNextCooldownDueAsync`; runnable/readmission query through `ParkedClauseSql` | `ParkedAt > CASE ReadmitCount ... $cooldown0 ... $cooldown3 END`; `ParkedAt <= CASE ReadmitCount ... $cooldown0 ... $cooldown3 END` | `BindParkedClause:209` formats each computed deadline; ParkedAt writes canonical; C. Four thresholds share each predicate. |
| `Data/LongRest/LongRestDiscoveryStore.CompletedCheckpoint.cs:119` | `ReconcileCompletedCheckpointAsync` | `LongRunningOperations.LeaseExpiresAt > $now` | `:120` formats now; operation lease writer canonical; C. |
| `Data/LongRest/LongRestAttemptStore.Claims.cs:23,56` | `RenewClaimAsync`; `ValidatePendingClaimWithinTransactionAsync` | Each statement: `LeaseDeadlineUtc > $now` | `:25,71` format now; `SessionTurnClaimStore` writes deadline via `Iso` → codec; C. |
| `Data/Covenant/CampaignMaintenanceCheckpointStore.cs:41,286,290,341` | `RenewClaimAsync`; `CommitPublicationAsync` (two updates); `ValidateClaimWithinAsync` | Each occurrence: `LeaseDeadlineUtc > $now` | `:46,297,357` bind `Iso` → codec at `:479`; claim writer uses same codec; C. |
| `Repositories/GrimoireRepository.ProtectedTools.cs:71` | `AppendProtectedToolInteractionAsync` | `LeaseDeadlineUtc > $now` | `:80` formats now; claim writer canonical; C. |
| `Repositories/GrimoireRepository.SessionTurnClaimBegin.cs:183,225` | `BeginClaimedAssistantReplyAsync` → `BeginClaimedWithinAsync`; `ReadLiveClaimExpectationAsync` | Each occurrence: `LeaseDeadlineUtc > $now` | `:196,262` format now; claim writer canonical; C. |
| `Repositories/GrimoireRepository.SessionTurnClaimBegin.cs:223` | `ReadLiveClaimExpectationAsync` | `PreRequestHistoryWatermarkUtc IS $watermark` | Nullable bound watermark is formatted; `SessionTurnClaimStore` insert formats it at `:804-805`; C. |
| `Repositories/GrimoireRepository.SessionTurnClaimReplay.cs:83` | `ReadCleanCommittedReplyAsync` → `ReadCleanClaimedReplyWithinAsync` | `PreRequestHistoryWatermarkUtc IS $watermark` | `BindClaimedReplayProof:168` formats nonnull watermark; same claim writer; C. |
| `Repositories/GrimoireRepository.TurnCommit.cs:733` | `RecordClaimInterruptionAsync` | `PreRequestHistoryWatermarkUtc IS $watermark` | Nullable watermark bound through canonical formatting; same claim writer; C. |
| `Data/Covenant/ArtifactSensitivityLedger.LexiconReplacement.cs:56` | `ReplaceLexiconWithinAsync` | `CreatedAtUtc = $at` | `:95` formats `expected.CreatedAt`; label creation/replacement uses same codec; C. |
| `Data/SagaMemoryStore.cs:481-482` | `ListPositionsAfterAsync` | `CreatedAt <= @afterCreatedText`; `CreatedAt < @afterCreatedText` | `:490` binds `SagaMemoryPosition.CreatedAt`, the exact `reader.GetString` returned by previous page. This is a stored ordering cursor, never driver DateTime binding; S. Memory creation writes canonical text. |
| `Data/Covenant/CovenantEntryErasurePlan.cs:1135` | receipt selection/deletion using `MutationReceiptPredicate` | `CommittedAtUtc >= $created` | `:1195` binds `CovenantEntryErasureSubject.CreatedAtUtc` (`string` at `:53`), read directly from `covenant_entries` at `:421`. Both canonical tables are backfilled; receipt/entry writers canonical; S. |

The Entry sequence paths `LoadRecentDescendingAsync`, `LoadAfterSequenceAsync`, `LoadBeforeSequenceAsync`, `LoadDescendingPagedAsync`, `LoadSagaExtractionPageAsync`, and `CountSagaExtractionPageAsync` compare INTEGER `Sequence` positions, not a driver timestamp, after the retained-entry cursor is resolved. The deleted-keyset fallback remains in the timestamp inventory above. `CreatedAt` boundary subqueries compare stored values to stored values and do not introduce a driver encoding boundary.

## Parsed SQLite comparisons

The complete occurrence list follows. All are **J**: both the stored timestamp expression and bound instant pass through `julianday`. The writer is covered by the UTC column inventory; retention cutoff binders use `FormatTimestamp`/shared codec and provenance bounds use the codec. These predicates would still parse a supported historical space-separated value, unlike an ordinal TEXT comparison. This finding concerns encoding only; it does not assert tick-level precision for SQLite floating-point Julian days.

| Source and baseline line | Method | Parsed comparison |
| --- | --- | --- |
| `Data/AttachmentMemoryProvenanceStore.cs:106` | `ListConsultationsAsync` | `julianday(e."CreatedAt") > julianday(@afterExclusive)` |
| `Data/AttachmentMemoryProvenanceStore.cs:107` | `ListConsultationsAsync` | `julianday(e."CreatedAt") <= julianday(@throughInclusive)` |
| `Data/DataRetentionService.cs:4372` | `SessionCandidateOldEnoughInTransactionAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.cs:4396` | `AttachmentCandidateOldEnoughInTransactionAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.cs:5327` | `ReadUploadedFilesByReferenceEligibilityAsync` | `julianday(file.CreatedAt) < julianday(@cutoff)` |
| `Data/DataRetentionService.cs:5471` | `ReadSessionIdsBeforeAsync` | `julianday(UpdatedAt) < julianday(@cutoff)` |
| `Data/DataRetentionService.cs:5516` | `ReadEligibleSessionIdsBeforeAsync` | `julianday(session.UpdatedAt) < julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:638` | `AddCompletedBatchCandidatesAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:684` | `AddCompletedBatchCandidatesAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:914` | `AddEntryProtectionDiagnosticsAsync` | `julianday(entry.CreatedAt) <= julianday(@entryDiagnosticCutoff)` |
| `Data/DataRetentionService.Pruning.cs:1070` | `AddAttachmentProtectionDiagnosticsAsync` | `julianday(attachment.CreatedAt) <= julianday(@attachmentDiagnosticCutoff)` |
| `Data/DataRetentionService.Pruning.cs:1208` | `AddEntryEmbeddingProtectionDiagnosticsAsync` | `julianday(entry.CreatedAt) <= julianday(@embeddingDiagnosticCutoff)` |
| `Data/DataRetentionService.Pruning.cs:1447` | `AddEntryCandidatesAsync` | `julianday(entry.CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:1693` | `AddAttachmentCandidatesAsync` | `julianday(attachment.CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:1868` | `AddEntryEmbeddingCandidatesAsync` | `julianday(entry.CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2055` | `AddWorkspaceCandidatesAsync` | `julianday(chunk.IndexedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2125` | `AddSagaCandidatesAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2132` | `AddSagaCandidatesAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2274` | `AddLexiconCandidatesCoreAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2281` | `AddLexiconCandidatesCoreAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2376` | `AddIdempotencyCandidatesAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2422` | `AddIdempotencyCandidatesAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2496` | `AddIdempotencyCandidatesAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2560` | `AddAccountingCandidatesAsync` | `julianday(COALESCE(run.CompletedAt, run.StartedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2650` | `AddAccountingCandidatesAsync` | `julianday(COALESCE(run.CompletedAt, run.StartedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2782` | `AddStandaloneAccountingCandidatesAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2786` | `AddStandaloneAccountingCandidatesAsync` | `julianday(AlertedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2844` | `AddOperationHistoryCandidatesAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:2889` | `AddSanctumCandidatesAsync` | `julianday(OccurredAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4438` | `ApplyCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4446` | `ApplyCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4494` | `ApplyCandidateAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4511` | `ApplyCandidateAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4526` | `ApplyCandidateAsync` | `julianday(OccurredAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4534` | `ApplyCandidateAsync` | `julianday(OccurredAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4593` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4604` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4634` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4645` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4659` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4677` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4689` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(IndexedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4700` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4711` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4719` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAtUtc) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4726` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAtUtc) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4757` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4768` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4779` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(COALESCE(CompletedAt, StartedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4793` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4804` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(AlertedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4815` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:4826` | `CandidateStillMeetsFrozenAgeAsync` | `julianday(OccurredAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5180` | `DeleteBatchCandidateAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5192` | `DeleteBatchCandidateAsync` | `julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5246` | `DeleteUploadedFileCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5529` | `DeleteEntryCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5696` | `DeleteEntryEmbeddingCandidateAsync` | `julianday(entry.CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5771` | `DeleteEntryEmbeddingCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5845` | `DeleteWorkspaceCandidateAsync` | `julianday(IndexedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:5885` | `DeleteWorkspaceCandidateAsync` | `julianday(IndexedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6058` | `DeleteSagaCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6212` | `DeleteLexiconCandidateAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6412` | `DeleteIdempotencyClaimCandidateAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6426` | `DeleteIdempotencyClaimCandidateAsync` | `julianday(UpdatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6449` | `DeleteStandaloneCostAdjustmentCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6461` | `DeleteStandaloneCostAdjustmentCandidateAsync` | `julianday(CreatedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6479` | `DeleteBudgetAlertCandidateAsync` | `julianday(AlertedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6489` | `DeleteBudgetAlertCandidateAsync` | `julianday(AlertedAt) <= julianday(@cutoff)` |
| `Data/DataRetentionService.Pruning.cs:6587` | `DeleteAccountingCandidateAsync` | `julianday(COALESCE(CompletedAt, StartedAt)) <= julianday(@cutoff)` |
| `Data/DataRetentionService.CampaignSummaries.cs:45` | `AddCampaignSummaryCandidatesAsync` | `julianday(CreatedAtUtc) <= julianday(@cutoff)` |
| `Data/DataRetentionService.CampaignSummaries.cs:158` | `DeleteCampaignSummaryCandidateAsync` | `julianday(CreatedAtUtc) <= julianday(@cutoff)` |

## Numeric and in-memory boundaries

`UtcInstantColumnInventory.cs:7-10` explicitly excludes the INTEGER disclosure aggregate tick watermarks and `BudgetReservations.BudgetPeriod`. Tick watermark comparisons remain numeric; budget period compares UTC calendar labels. Identity, sequence, revision, work cursor, and ordering offsets are numeric/identity bounds even where their methods also materialize timestamp columns. `LogQueryService` filters `LogEntry.Timestamp` as a parsed .NET instant; provider-card TTLs, filesystem last-write tests, parsed operation lease checks, and Long Rest in-memory evidence checks likewise compare `DateTimeOffset`/`DateTime` values after parsing. They have no SQLite driver-bound TEXT comparison and cannot manifest the space-versus-T defect.

## Verified history and historical exposure

`git show bb5b07ce` identifies commit `bb5b07ceea2d4bd9d4cb0d7ce8b3e2253ed5384f`, dated 2026-09-09, titled `fix: complete issue 256 qualification blockers`. Its actual diff adds `UtcInstantText`, `UtcInstantSql`, UTC type mappings, historical normalization, and the persisted-instant source guard. The Grimoire idle query changes from EF-interpolated `UpdatedAt < {idleCutoffOffset}` to direct SQL with a formatted `$idleCutoff`. This is a verified historical driver-bound timestamp comparison repaired before this audit baseline. The diff also changes budget, idempotency, operation and other writers/binders from mixed round-trip spellings to the common canonical codec. Historical exposure in those mixed-format paths follows from their writer/binder encoding contract; this audit does not claim a separate failing execution for each old commit.

The same verified diff replaces Entry temporal EF `FromSql`/`SqlQuery` interpolation, including `CreatedAt > {afterExclusive}`, `CreatedAt > {afterUtc}`, and the deleted-keyset `beforeCreatedAt` path, with direct commands that format their comparison bounds. These are the historical production comparison repairs to which the issue-223 returned-row and mutation regressions apply. The current file's last modifying commit at the audit baseline is `bb5b07ce` (`git log -1 --format=%H -- .../EntryTemporalQueries.cs`).

`git show eb9fc763` identifies commit `eb9fc763544b18306471c6f0d2daaa1b57b7fc59`, dated 2026-09-18, titled `fix: close production collection provenance`. Its timestamp-related changes materialize the backfill table collection into a `List`, change canonical row values to an array, and update the inventory record's collection type. It preserves the timestamp codec and comparison semantics; it is not the original separator repair. Both commits are available in local Git and were inspected rather than inferred from their titles.

## Comparison guard gap and bounded repair

`tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs` already resolves SQL text, associates each binding with its command and owning scope, follows local canonical aliases, and fails closed for ambiguous command provenance. However, `AddSqlWriteContract:1465-1488` returns for any SQL without INSERT or UPDATE, and `DiscoverSqlMappedInstantParameters:1864-1894` repeats that write-only filter and discovers only assignment `column = parameter` and INSERT column/value pairs. A SELECT such as `WHERE CreatedAt < $value` with raw `$value` is invisible when the generic parameter name avoids `IsSemanticInstantParameter:1849-1863`. DELETE age predicates have the same gap. Existing semantic parameter recognition also does not recognize all names used above (`$idleCutoff`, `$afterExclusive`, `$pN`, `$asOf`, `$cutoff`, `$cooldownN`).

A proportionate extension is to reuse the existing SQL contract/provenance machinery for SELECT/DELETE comparison contracts as well as INSERT/UPDATE, and map known inventory columns on either side of `<`, `<=`, `>`, `>=`, `=`, `!=`, `<>`, and nullable `IS`/`IS NOT` when the other operand is a parameter. Match optional alias/quoted identifiers, keep the INSERT mapping, and avoid claiming a raw unparsed timestamp comparison for `julianday(column)` numeric expressions. The parked CASE expression, aggregate `MAX(DisclosedAtUtc)`, COALESCE timestamp expressions, interpolated `$pN` fragments, and exact stored-text cursors need explicit handling or narrowly documented proof rather than an assertion that a simple regex covers every SQL shape. Preserve the existing per-command and per-method provenance checks.

Focused guard RED fixtures should cover: SELECT `CreatedAt < $value`; reversed `$value >= alias."CreatedAt"`; DELETE `CreatedAt <= :value`; equality/nullable watermark `IS`; a generic parameter beside a legitimate unrelated codec call; two commands in one method; canonical counterpart acceptance; storage cursor exception proof; and a numeric `julianday` comparator that must not be misclassified as raw timestamp text. These are source guard tests. They supplement production returned-row tests and cannot replace them.

The issue-223 implementation extends the existing guard for SELECT/DELETE, both operand orders, relational/equality/nullable operators, and BETWEEN. SQL keyword matching uses whole tokens so an `UpdatedAt` fragment cannot masquerade as an UPDATE statement. The semantic `before` parameter classification covers the audited Apprentice and Sanctum bounds; this does not claim generic reconstruction of arbitrary dynamic SQL fragments. Mutually exclusive Batch query assignments share a binding only when construction completes before binding and no execution or command-text reassignment intervenes. The Saga exception is restricted to its exact owner, method, parameter, member expression, and string type. Existing `ListPositionsAfterAsync_ResumesFromTheStoredCreatedAtTextWhateverItsSpelling` coverage proves why normalizing that ordering cursor would change its contract.

## Acceptance evidence and delivery gate

`EntryTimestampComparisonTests` adds twelve production-entry-point cases. They create Sessions and Entries through `SessionRepository`, delete the cursor through `GrimoireRepository`, and assert the returned row identities/order or persisted unsummarized count. Cases cover zero and seven-digit fractions, midnight/noon, one-tick neighbors, previous/next UTC dates, equal-time lower/higher identities, and equivalent non-UTC cursor inputs. The writer's actual stored timestamp bytes are asserted independently of the codec.

The three independently reverted bindings each failed all four corresponding cases on behavioral assertions, then were restored:

| Isolated mutation | Observed failure |
| --- | --- |
| Deleted-keyset `$beforeCreatedAt` bound as a raw `DateTimeOffset` | Four returned-row failures: same-day older and equal-time lower-ID rows disappeared. |
| `CountAfter` `$afterExclusive` bound as a raw `DateTimeOffset` | Four exact-count failures: expected two, observed three or four. |
| Shared timestamp-group `$afterExclusive` bound as a raw `DateTimeOffset` | Four returned-row failures: an earlier/equal row replaced the expected next-tick tied pair. |

After restoration, all twelve cases passed without skips. `EntryTemporalQueries.cs` was restored byte-for-byte to the baseline (SHA-256 `78560ccac57f0d5412fec4a810d9f74a076af5c5418c3ebcfa816108b834d2cd`). The comparison-discovery fixtures first produced thirteen failures under the previous write-only guard. Correct shared-query and exact stored-string cursor controls separately produced two expected failures before their proof was added. These guard fixtures supplement the production results; they are not substituted for them.

The real-model qualification now includes a provider-free HTTP timestamp probe within its existing four-turn Ollama schedule. It deletes a cursor Entry, checks exact pages for equivalent `Z`, `+05:30`, and `-07:00` spellings, reads actual timestamp TEXT from the isolated SQLCipher installation, repeats the checks across the real host restart, and removes the probe Session before the original single-session assertions resume. The success receipt requires this probe and a final persisted-encoding check after the recall turn.

The focused injection review traced the audit's thirty-four referenced Infrastructure files, including dynamic Session/Apprentice filters, generated ID placeholder lists, fixed cooldown CASE expressions, and erasure predicates. Timestamp, identity, and filter data remain in parameters; structural SQL comes from internal constants or numeric enum values. The new inspection query is static SQL on a read-only isolated connection. `Timestamp_filtered_session_queries_keep_injection_payloads_as_literal_data` additionally drives production Session queries with timestamp bounds and hostile status/title values. It passed with parameterization, then failed on returned rows when only the status bind was deliberately replaced with interpolation. That source mutation was restored before final verification.

At the operator's explicit request, this change uses focused local verification rather than the full CI gate. Delivery requires the timestamp/parameterization and affected harness regressions, a clean solution build, the already-run companion suites, and the candidate's local Native AOT publish checks, followed by the multi-step Ollama qualification and fast-forward integration into `memory-enhancement` and `main`. The issue completion record carries the candidate SHA, local verification and live qualification results, and post-integration ref equality; this audit does not claim those later actions occurred merely because the implementation tests passed.

No new schema or timestamp representation change is required by the baseline survey. The canonical owning contract remains `docs/Arcanum.DESIGN.md`; this review records issue-specific evidence.
