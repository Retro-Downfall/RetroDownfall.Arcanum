# U01 Arcanum.Api, Intelligence (1), reliability lens

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Reviewer: reliability.

## Unit file list (reconstructed)

`scratchpad/review/units.json` was missing when this reviewer ran (the whole `scratchpad/review` directory was absent). The unit was rebuilt from the run arguments recorded in `~/.claude/projects/.../workflows/wf_4f3c2f94-71f.json` (`U01`, `Arcanum.Api`, `Intelligence (1)`, 11,879 lines): the root-level files of `src/RetroDownfall.Arcanum.Api/Intelligence`, in ordinal order, up to a cumulative total of exactly 11,879 lines. Familiars and Guardrails belong to U57, the other subdirectories to U58, and `WizardIntelligenceProvider.cs` is U04. All 28 files were read in full:

ArcanumInvocationContexts.cs (254), AttachmentSourceSanctum.cs (137), AuditEndpoints.cs (118), BatchJsonlRecordReader.cs (602), BatchProcessingService.cs (1907), BatchRecoveryService.cs (351), BudgetMonitor.cs (172), CampaignMaintenancePayloadFingerprint.cs (17), CampaignMaintenanceProviderCallFreezer.cs (61), CampaignRollupMaintenance.cs (774), CampaignRollupProviderClientFactory.cs (129), CampaignRollupTurnPreparer.cs (845), ChatClientFactory.cs (277), ChronicleFrameWire.cs (17), ClientToolCapabilityValidator.cs (283), ContextCompressionService.cs (439), ContextMaterializationLedger.cs (772), CovenantDispatchGate.cs (716), CovenantProviderCallFreezer.cs (561), DailySpendAuthority.cs (46), EmbeddingGeneratorFactory.cs (207), EmbeddingsResetEndpoints.cs (131), GitIgnoreRules.cs (485), GrimoireTurnWriter.cs (1485), HumanPromptRegistry.cs (185), InferenceContextBuilder.cs (764), InferenceProviderFailureMessage.cs (50), InferenceTokenizerResolver.cs (94).

Owning docs read: DESIGN §5.4.2 (Campaign continuity, turn binding and maintenance authority), §11.20 and §11.21 (files, batches, startup recovery, unexpected page failure, page and publication frontiers, shutdown ownership), §10.8.2 extract (one connection per send), Compendium `features.campaignRollups`.

Callers and callees traced outside the unit where a finding depends on them: `Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs` and `CampaignRollupStore.cs`, `Infrastructure/Hosting/Loremaster.cs`, `Api/Intelligence/WizardIntelligenceProvider.cs` (Campaign preparation at 1785-1870, scope ownership at 115-123/698/1082, tool persistence at 5250), `ModelCallExecutor.CampaignMaintenance.cs`, `Infrastructure/Storage/EncryptedBlobStore.cs` (streaming writer 1000-1200), `Infrastructure/Security/FileEncryptionKeyProvider.cs` and `Hosting/FileEncryptionKeyBootstrapHostedService.cs`, `Hosting/InstallationResetRecoveryAwareHostedService.cs`, `Api/Middleware/ArcanumExceptionHandler.cs`, `Infrastructure/Weave/EmbeddingsResetService.cs`, `Core/Covenant/ProviderCallEnvelope.cs`, `Infrastructure/Covenant/CovenantOperationGate.cs` (drain), `TurnAccountingHandle.cs` (begin), `Repositories/GrimoireRepository.TurnCommit.cs` (claim updates), `CommLink/CommLinkMultiplexer.cs`.

## Probes run

One scratch worktree, `scratchpad/review/wt/U01-reliability` at `ac0d2d8e` (removed afterwards). Three probe files were added to `tests/RetroDownfall.Arcanum.Tests/Intelligence` and the test project was built there (build succeeded, 0 warnings, 0 errors, 21 min 51 s under a load average near 150). The probe sources are kept at `scratchpad/review/reports/A/U01-reliability-probes/`. Command:

```
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build \
  --filter "FullyQualifiedName~ReviewProbeU01|FullyQualifiedName~ReviewProbe_U01" \
  --logger "console;verbosity=detailed" --results-directory scratchpad/review/results/A </dev/null
```

Result: 9 tests, 9 passed. Each test asserts the defective behaviour, so a pass confirms the defect.

| Probe | What it asserts | Output |
|---|---|---|
| P1 `ReviewProbeU01Tests.P1_...` | Real `CampaignRollupMaintenance.ProcessCampaignAsync` over two pending Sessions, the first refused by the store with `campaign_rollup.input_too_large`: the call fails with that code, the second Session is never prepared and `PrepareRollupAsync` is never called | passed |
| P2 `WizardIntelligenceProviderTests.ReviewProbe_U01_P2_...` (both rows) | Real `CampaignRollupTurnPreparer` + `WizardIntelligenceProvider` in the existing Campaign harness, with a maintenance double returning `campaign_rollup.input_too_large` from `ProcessSessionAsync` (row false) or `ProcessCampaignAsync` (row true): `ExecutePromptAsync` fails with that code and the chat client receives no call | passed (2 rows) |
| P3 `BatchProcessingServiceTests.ReviewProbe_U01_P3_...` | Real `BatchRecoveryService.ReconcileStrandedAsync` over one stranded `in_progress` batch whose input read throws `IOException` (existing `FailingInputBlobStore`): the reconcile throws `IOException` and the batch stays `in_progress` | `P3: thrown=System.IO.IOException` |
| P4 `ReviewProbeU01Tests.P4_...` | `CovenantTurnScope.DisposeAsync` disposes a completed Campaign preparation (control: claim completed once) but never disposes a preparation that completes after the scope was disposed (claim completion count stays 1) | passed |
| P5 `BatchProcessingServiceTests.ReviewProbe_U01_P5_...` | Real `BatchProcessingService` over a 100-line input, concurrency 1, provider throws on its 64th call (the last line of the first page): batch `failed`, provider called 64 times, `TotalRequestCount` 64, no error file | `P5: status=failed total=64 completed=63 failed=1 calls=64 errorFile=none` |
| P6 `ReviewProbeU01Tests.P6_...` | `CovenantProviderCallFreezer.TryFreeze` throws an `ArgumentException` (rather than returning a failed `Result`) when a message part cannot be enveloped (unpaired surrogate in a tool result and in a text part) | passed |
| P6b `ReviewProbeU01Tests.P6b_...` | Records whether System.Text.Json yields an unpaired surrogate from `"\ud83d"` | `thrown=System.InvalidOperationException` (STJ refuses it; see finding 8) |
| P7 `ReviewProbeU01Tests.P7_...` | `GrimoireMaintenanceUnavailableException` satisfies the embeddings-reset route's catch filter `InvalidOperationException and not LabeledArtifactRefusalException` | passed |

No published-binary runs were needed.

## Findings

### 1. Important. One Session whose contribution cannot be prepared fails every Campaign-bound operator turn and stalls the Campaign fold

- Files: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs:138` (also 216-221), `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:628` and `:643`; related `Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs:60-65`, `WizardIntelligenceProvider.cs:1834-1843`.
- Category: crash-consistency / poison input (head-of-line blocking).
- Claim. `ProcessCampaignAsync` iterates every pending Session of the Campaign (`ORDER BY s.Id`) and returns on the first failed contribution (`if (contribution.IsFailure) return contribution.Error;`), so later Sessions and the fold are never processed. `ProcessPagesAsync` passes on every store failure except `ForbiddenAuthority` (lines 216-221). The live turn preparer turns any maintenance failure into a turn failure (`if (contributed.IsFailure) return contributed.Error;`, `if (folded.IsFailure) return folded.Error;`), and `WizardIntelligenceProvider` ends the turn with that error before dispatch. A live turn calls `ProcessCampaignAsync` with a capability, which sets the idle cutoff to `DateTimeOffset.MaxValue`, so the turn waits on, and can be failed by, every other Session of the Campaign.
- A deterministic trigger exists. `CampaignRollupStore.PrepareContributionAsync` throws `campaign_rollup.input_too_large` whenever the first unconsumed native Entry is larger than `32768 - bytes(previous contribution)` UTF-8 bytes (`CampaignRollupStore.Read.cs:60-65`; pinned by `CampaignRollupStoreTests.An_oversized_entry_fails_before_materialization_and_does_not_pay_a_cursor` with a 34,000-byte Entry). The watermark never moves, so the error recurs forever. A user prompt with a pasted 30 KB log, or one long code-heavy assistant reply, is enough.
- Other triggers: `CampaignSummaryPolicy` rejecting both paid attempts (`ProcessPageAsync` 485-495; 2,048 output tokens can exceed the 8 KiB summary bound, and that costs two paid calls on every retry); context admission failing for the FastModel (the default `ProviderSettings.ContextWindowLimit` is 8192, while a 32 KiB page plus the 2,048-token reservation needs more); a concurrent turn in another Session of the same Campaign that starts between this turn's prepare and send for that Session (`EnsureCurrentAsync` throws "sources changed", and the executor maps it to a generic `Hub.Error`).
- Failure scenario. `features.campaignRollups` is on. Session A in Campaign C receives a 30 KB prompt. From the next turn on, every operator turn in A fails with `campaign_rollup.input_too_large` before dispatch (`ProcessSessionAsync`). Every turn in every other Session of C fails as well, at `ProcessCampaignAsync`, because A is pending in C. Loremaster's background pass also stops at A (Debug log only), so C's continuity summary never folds again. The only remedies are turning rollups off (a host restart) or deleting A's Entry.
- Contradicts DESIGN §5.4.2: "These allocation bounds do not cap Campaign lifetime or the number of contributors", and `CampaignRollupLimits`' own doc, "pages bound work, never Campaign lifetime progress". Long Rest handles the same case gracefully ("a memory too large for any summary request is carried and the sweep completes without sending its text", DESIGN §21).
- Evidence: probes P1 and P2 (above), store test cited, code trace.
- Suggested fix. (a) In the store, carry an oversized Entry rather than refuse it: render a fixed `[omitted: N bytes]` marker, or a rune-safe prefix, and advance the watermark, as Long Rest does. (b) In `ProcessCampaignAsync`, isolate per-Session failures: log the error and the Session, skip it, keep going, and still run the fold over the surviving contributions. (c) In `CampaignRollupTurnPreparer`, treat maintenance failures other than authority, claim or configuration staleness as best-effort, like the clean Long Rest schedule refusal at lines 475-494: proceed with the current artifact and record a metric. (d) Add a test of `ProcessCampaignAsync` with more than one pending Session. Today the `StoreDouble` in `CampaignRollupMaintenanceTests` returns no pending Sessions, so the per-Session loop is untested.
- Effort: M.

### 2. Minor. An unexpected failure after every line of a page has started leaves no resume note, and every later page is silently dropped

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:874` (`TryStopAfterUnexpectedFailureAsync` 860-894).
- Category: data-integrity / error reporting.
- Claim. After an unexpected page failure, the note telling the operator where to resume is written only for the first page line that never started (lines 872-882), or for `lastFinishedLine + 1` when no page was prepared (884-894). When every line of the failing page has a checkpoint, `firstUnstarted` is null and nothing is written. The batch is then published as `failed`, its counters show only the claimed lines, and no error file exists. The rest of the input is never read and nothing says so.
- Triggers. A host fault (for example a SQLite disk-full or I/O error from `TryBeginLineAsync`/`CompleteLineAsync`) on the last line of a 64-line page, or on the last lines scheduled under concurrency > 1. Also any fault of the cancellation watcher (`WatchForCancellationAsync`, `GetByIdAsync` at 1320, which catches only `OperationCanceledException`): the watcher's exception is raised only after `Parallel.ForEachAsync` has finished every line (`StopAndJoinCancellationWatcherAsync`), so every line has always started.
- Failure scenario (measured, P5). Input of 100 lines, failure on line 64. Result: `status=failed total=64 completed=63 failed=1`, provider called 64 times, no error file. Lines 65-100 were never attempted. To the operator this is indistinguishable from a 64-line batch whose last line failed. DESIGN §11.21 promises a note "for the first page line that never started — or ... for the line after the last page that finished ... with the operator resubmitting from the noted line".
- Suggested fix. When `firstUnstarted` is null, write the note on `page[^1].Line + 1`, worded "at or after line N", since the input may have more lines. Better still, record a resume line on the batch row. Add a test with a failure on the last line of a page.
- Effort: S.

### 3. Minor. A non-cryptographic read failure of one stranded batch input aborts host startup

- Files: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchRecoveryService.cs:305` (`InputExistsAsync`), `:69-86` (`ReconcileStrandedAsync`), `BatchProcessingService.cs:70-78` (`StartAsync`).
- Category: startup ordering / exception handling.
- Claim. `InputExistsAsync` authenticates the whole input with `CopyToAsync(Stream.Null)` but catches only `CryptographicException` and `InvalidDataException`. An `IOException` or `UnauthorizedAccessException` (a file locked by an antivirus or backup scanner on Windows, an EIO, a permissions change) escapes `ReconcileStrandedAsync`. Nothing catches it on the way up: `BatchProcessingService.StartAsync`, then `InstallationResetRecoveryAwareHostedService.StartAsync`, then Generic Host start. One bad stranded batch prevents `arcanum serve` from starting at all. The same exception from `/v1/batches/{id}/reset` becomes a 500.
- Failure scenario (P3). One `in_progress` batch, input read throws `IOException`: `ReconcileStrandedAsync` throws `System.IO.IOException` and the batch stays `in_progress`. In the host, the same exception fails `StartAsync`, and the next start fails the same way until the file is fixed by hand.
- Suggested fix. Catch per batch inside the `foreach` in `ReconcileStrandedAsync`: log, leave the batch `in_progress` for a later `/reset`, or mark it failed with an "input unreadable" reason, and continue. In `InputExistsAsync`, treat `IOException`/`UnauthorizedAccessException` as unavailable, so `/reset` answers `InputMissing` rather than 500.
- Effort: S.

### 4. Minor. A Campaign preparation that completes after its turn scope is disposed is never disposed

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs:207` (`CovenantTurnScope.DisposeAsync`).
- Category: leak (claim lease and `CovenantTurnLease`).
- Claim. `DisposeAsync` disposes the `CampaignRollupTurnSnapshot` only if `CampaignPreparation.IsCompletedSuccessfully`. The streaming path deliberately yields ("Finishing protected memory maintenance...", `WizardIntelligenceProvider.cs:1819-1832`) while preparation is still running. If the consumer disposes the enumerator at that yield and the preparation then completes successfully (its token was never cancelled, or the cancellation arrived after its last observed await), `PrepareLiveAsync` has already handed ownership to the snapshot (`transferred = true`). Nobody disposes it after that.
- Consequence. The executable claim is never completed (the Session reports `Hub.SessionTurnBusy` until the lease deadline). The `CovenantTurnLease` is never released, so a Covenant operation that drains leases over that Campaign scope (reset, Campaign delete, path mutation) times out after 30 s with `Covenant.MaintenanceFailed` (`CovenantOperationGate.DrainAsync`, `DefaultDrainTimeout`), until the host restarts.
- Evidence (P4). With the control scope (preparation already complete), the claim completion count is 1. With the abandoned scope (preparation completed after `DisposeAsync`), the count stays 1: the second snapshot is never disposed. The race window itself is traced, not executed end to end.
- Suggested fix. When `CampaignPreparation` is not complete at disposal, attach a continuation that disposes the snapshot if the task later succeeds, for example `_ = CampaignPreparation.ContinueWith(t => t.Result.Value?.DisposeAsync(), TaskScheduler.Default)`, observed and logged. Alternatively await the preparation under a bounded wait.
- Effort: S.

### 5. Minor. The embeddings-reset route reports Grimoire maintenance, an unavailable database, or any other `InvalidOperationException` as "manual artifact erasure required"

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs:68`.
- Category: error-message / over-catch.
- Claim. `catch (InvalidOperationException blocked) when (blocked is not LabeledArtifactRefusalException)` is meant to catch the service's "A protected artifact ... could not be erased" signal (`EmbeddingsResetService.cs:438`, a plain `InvalidOperationException`). It also catches `GrimoireMaintenanceUnavailableException` and `GrimoireDatabaseUnavailableException` (both derive from `InvalidOperationException`; P7), the service's own "The Grimoire requires a SQLCipher connection", and any EF Core or Sqlite `InvalidOperationException`. All of these are answered as `Covenant.ManualArtifactErasureRequired`, with the raw exception message in the body. Without the filter, `ArcanumExceptionHandler` would have answered the maintenance case with the maintenance refusal.
- Failure scenario. An operator runs `arcanum` embeddings reset while a backup or rekey holds Grimoire admission closed (the connection-enrolment interceptor throws `GrimoireMaintenanceUnavailableException` on open). The operator is told a protected artifact must be erased by hand, not that the Grimoire is in maintenance and to retry.
- Suggested fix. Throw a dedicated exception type for the blocked-purge case from `EmbeddingsResetService` and catch only that type in the route.
- Effort: S.

### 6. Minor. A committed claimed reply is reported as not saved when settling its claim fails, and the retry is then refused

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:1288`.
- Category: crash-consistency / wrong outcome.
- Claim. For a claimed turn, `TryCommitProtectedAsync` commits the reply, and only afterwards `CompleteClaimAsync(committed: true)` settles the claim, in a separate write (`claims.CompleteAsync`). The interruption path settles inside the commit transaction (`GrimoireRepository.TurnCommit.cs:715-745`); the committed path does not. If that second write fails (busy, I/O, admission), the method returns `false` after `IsFinalized = true`. The caller then emits `PublicFinalizeFailureMessage` ("The conversation reply could not be saved. Please try again.") although the reply is durably in the Session. The claim stays Begun, so a retry with the same idempotency key is refused by `CampaignRollupTurnPreparer.cs:385-388`: "This turn already began and has no replayable finalization".
- Evidence: code trace. Not executed, because it needs a fault injected between two repository writes.
- Suggested fix. Settle the Committed claim outcome inside `CommitTurnAsync`'s transaction, as the interruption path already does. Otherwise return success and leave the claim to restart recovery.
- Effort: M.

### 7. Minor. On the protected tool-interaction path, an event-hub publication failure after a successful commit fails the turn

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:415` (and 417).
- Category: exception handling.
- Claim. After `AppendProtectedToolInteractionAsync` commits the call/result pair, the two `PublishSavedEntryByIdAsync` calls run with no try/catch. Any exception from `GetEntryByIdAsync` or the hub propagates through `PersistToolInteractionAsync` and ends the turn, although the pair is committed. The unprotected path (lines 435-455) and the begin path (lines 984-994: "Publication is best-effort on purpose: the rows are already committed") both treat publication as best-effort.
- Failure scenario. A transient read failure right after the commit on a Covenant-derived turn fails that turn with a generic error. The Session keeps the committed tool pair with no assistant continuation.
- Suggested fix. Wrap both publications in the same best-effort try/catch, rethrowing only `OperationCanceledException`.
- Effort: S.

### 8. Minor. `CovenantProviderCallFreezer.TryFreeze` throws instead of returning a refusal when a message part cannot be enveloped

- File: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantProviderCallFreezer.cs:73` (also `FreezeToolResult` 313).
- Category: api-contract / exception handling.
- Claim. The type's contract is "a message part or an option this build does not know how to freeze produces a refusal". But `FreezeMessages` (and `FreezeTools`' envelope construction) run outside the method's `ArgumentException` catch, which only wraps the final `ProviderCallEnvelope` constructor. `ProviderContentPartEnvelope` validates through `RequireText`/`StrictUtf8` and throws `ArgumentException` for an empty tool-call id or name, an empty media type, an empty message id, or invalid UTF-16. That exception escapes `TryFreeze`, so a Covenant-derived dispatch fails as an unhandled exception (a generic inference error, exception type logged) rather than the typed `Covenant.InvalidContent` refusal.
- Realistic trigger: a stateless `/v1/chat/completions` transcript or a provider that supplies an assistant tool call with `"id": ""` on a turn that admits Covenant content. P6b shows that System.Text.Json refuses escaped unpaired surrogates, so the surrogate case in P6 is not reachable from JSON input; P6 is used only to show that the escape path exists.
- Evidence: P6 (exception escapes), code trace for the empty-id case.
- Suggested fix. Move `FreezeMessages` and `FreezeTools` inside the existing try, or catch `ArgumentException` in `FreezePart`/`FreezeToolCall`/`FreezeToolResult` and return `Covenant.InvalidContent`.
- Effort: S.

## Checked and found sound

- **`BatchJsonlRecordReader`** (traced line by line):
  - Physical line numbering is correct for blank lines, CRLF, a missing final newline and leading-whitespace records.
  - Exact leading-whitespace bytes count toward the record limit.
  - Oversized records are consumed without allocation and reported with the next line.
  - The spill is created only for records over 256 KiB, and its cleanup runs in `finally` on every path, cancellation included.
  - BOM handling is the same on the in-memory and spill paths.
  - `Dispose`'s unsettled-spill guard cannot fire, because `ReadRecordAsync` always resets in `finally`.
- **`BatchProcessingService` registration and shutdown.**
  - `_stopping` and `_inFlight.TryAdd` share `_dispatchSync`, and each registration's completion TCS is published before `Task.Run`.
  - `StopAsync` sets `_stopping` before taking its drain snapshot, and the drain is bounded.
  - `ProcessBatchWithCleanupAsync` always removes the registration.
- **Mid-batch cancellation.** It is classified only when the linked token fired while the host token is live, so a provider-timeout OCE is a failure (`ProcessBatchAsync_ProviderTimeoutOce_DoesNotMarkBatchCancelled` exists).
- **Accounting completion** runs on `CancellationToken.None`, and a failed settlement is excluded from the stop path.
- **Artifact publication.**
  - `newlyPublished` registers the owned identity before metadata.
  - Compensation goes through `TryDeleteUnreferencedAsync`, then an identity-bound delete.
  - `StreamingEncryptedBlobWriter.CompleteAsync` closes the stage file before `File.Move`, so the move works on Windows (`EncryptedBlobStore.cs:1123-1159`).
- **Startup key unavailability** cannot misclassify stranded batches as failed: when encrypted blobs exist and the key cannot load, `FileEncryptionKeyBootstrapHostedService` throws before batch reconciliation.
- **`BatchRecoveryService.ResetStuckBatchAsync`** excludes concurrent local owners (`_operatorRecoveries`, `IsBatchInFlight`) and relies on the durable claim.
- **`BudgetMonitor`.**
  - The alert is deduplicated through `RecordAlertAsync`.
  - The discarded `DispatchAsync` result is logged by `CommLinkMultiplexer` itself.
  - Cancellation is rethrown.
- **`AuditEndpoints`.** `QueryPageAsync` fails only on an invalid cursor, so 400 is correct.
- **`CampaignRollupProviderClientFactory` and `ChatClientFactory`.**
  - Owned `HttpClient` with zero pooled lifetime and idle timeout.
  - Idempotent `Dispose`.
  - Leases never own the named factory client.
- **`CampaignRollupMaintenance` paid suffix.**
  - Usage recording and publication use `CancellationToken.None` after paid output.
  - The heartbeat is stopped and joined in `finally`.
  - `sent` is guarded by `Interlocked`.
  - The accounting handle completes in `finally`.
- **`ClientToolCapabilityValidator`.** Choice parsing and required/specific availability are correct, null-safe and ordered.
- **`ContextCompressionService`.**
  - The tool-group expansion runs before the purge.
  - A refused purge stops compaction rather than falling back to the ordinary delete.
  - Cancellation is honoured only before the first committed delete.
  - The removed count is reconciled after a stop.
  - History mapping renders tool entries as text, so an orphaned half cannot produce an invalid provider transcript.
- **`ContextMaterializationLedger`.** It is not thread-safe, but it is used from the sequential tool loop only (no `Parallel` or `WhenAll` in `ToolExecutionPipeline`). Duplicate, range, version and semantic-limit checks were traced.
- **`CovenantDispatchGate`.**
  - Disclosure is durable before the admission is recorded.
  - An unreadable label fails toward taint.
  - `ResolveSensitivity` cannot report clean when history, artifact or derived sensitivity is tainted.
- **`ArcanumInvocationContexts`.** `Create` can fail only for undefined or non-operator surfaces, so the `None` fallback is unreachable from these operator builders.
- **`AttachmentSourceSanctum`.** An unreadable or unloaded governing Sanctum refuses, and no claim means unrestricted, as documented.
- **`GitIgnoreRules`.** Traced for anchoring, a trailing `/`, the `**` forms (leading, middle, trailing), negation, escapes, trailing spaces and classes. Two divergences from git were noted and not reported, because they only list more files: matching is case-sensitive even where git uses `core.ignorecase=true` on macOS, and POSIX `[[:class:]]` is not supported.
- **`GrimoireTurnWriter` session-turn gate.** The writer is scoped (`ApiBootstrapper.cs:613`), so `Dispose` releases any lease whose handle the caller never received. Interrupted resolution on stream exit uses `CancellationToken.None`.
- **`HumanPromptRegistry`.** Each reservation releases its admission slot exactly once (`Interlocked` dispose guard), a `TryAdd` failure releases the slot, and cancellation completes only the matching TCS.
- **`InferenceContextBuilder`.**
  - Trailing empty assistant placeholders are trimmed.
  - Data URIs that fail to decode are dropped, not thrown.
  - Unparseable tool arguments fall back to a `raw` value.
  - The filtered path drops orphan tool halves.
- **`InferenceTokenizerResolver`.** The `GetOrAdd` race is benign, and an unknown encoding is cached as a fallback.
- **`EmbeddingGeneratorFactory`.** The cache key includes the endpoint and a credential fingerprint. The dictionary grows by one entry per credential rotation, which is small and not reported.

## Documented limitations seen and not reported

A `GrimoireMaintenanceUnavailableException` inside a batch page propagates and leaves the batch `in_progress` until restart or `/reset` (DESIGN §11.21, pinned by `ProcessBatchAsync_MaintenanceUnavailableDuringAPage_PropagatesAndLeavesTheBatchInProgress`). An exception opening the input before the first lookahead (`BatchProcessingService.cs:385`) likewise strands the batch until startup reconciliation, consistent with the §11.21 "unhandled exception before safe publication" sentence.
