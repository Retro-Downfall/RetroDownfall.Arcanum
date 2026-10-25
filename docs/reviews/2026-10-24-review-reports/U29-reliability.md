# U29 (Arcanum.Infrastructure, Data (5)) - RELIABILITY review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only, left clean). Probes ran in a scratch worktree
(`wt/U29-reliability`, since removed). Probe source: `reports/C/U29-reliability-probes/U29ProbeTests.cs`.

## Unit identification

`scratchpad/review/units.json` did not exist. I rebuilt the unit the way the sibling reviewers did (U25, U27): the
top-level `src/RetroDownfall.Arcanum.Infrastructure/Data/*.cs` files in ordinal order, packed greedily to a 12,000-line
cap. Chunk 5 is `SessionAttachmentStore.cs` .. `UtcInstantTypeMappings.cs` and sums to exactly 5,447 lines, the size the
task gives.

Files, all read in full (every line):

| File | Lines |
|---|---|
| SessionAttachmentStore.cs | 2610 |
| SessionContextPinStore.cs | 143 |
| SqlLikePatterns.cs | 37 |
| SqliteBusyRetry.cs | 259 |
| SqliteConnectionPragmas.cs | 49 |
| SqliteNativeRuntime.cs | 86 |
| SqliteNativeRuntimeManifest.cs | 169 |
| SqliteNativeRuntimeUnavailableException.cs | 42 |
| SqliteNativeRuntimeValidationResult.cs | 54 |
| SqliteNativeRuntimeValidator.cs | 477 |
| SqlitePragmaConnectionInterceptor.cs | 32 |
| SqlitePragmaStatementFactory.cs | 56 |
| SqliteScratchDatabase.cs | 104 |
| SqliteUniqueConstraintViolation.cs | 24 |
| TurnRunWriter.cs | 165 |
| UnseenServantWatermarkStore.cs | 174 |
| UploadedFileRepository.cs | 693 |
| UtcInstantSql.cs | 70 |
| UtcInstantText.cs | 96 |
| UtcInstantTypeMappings.cs | 107 |

Context read (not in the unit, read to trace callers and contracts): `SessionAttachmentStore.Lifecycle.cs` lines 180-420
and 680-760 (fork copy/insert, `ReconcileCoreAsync`, `SweepMissingFileRowsAsync`), `EncryptedBlobStore.cs` (`ReplaceOrMove`,
`CompleteAsync`, `CreateWriterAsync`), `SessionAttachmentPathSanitizer.cs` (Core), `SecureFilePermissions` temp-file
creation, `GrimoireEntitySql` format/read helpers, `UtcInstantEntityTypeCustomizations.cs`, `SessionAttachmentPendingGcHostedService.cs`,
`SessionAttachmentTurnService.PromotePendingAsync`, `WizardIntelligenceProvider` pending-turn creation, `SessionEndpoints`
upload paths, `UploadedFileMimeValidator`, `SessionRepository` fork transaction, `GrimoireRepository` delete-entry transaction,
`CovenantSqliteConnectionInitializer` pragmas, and the schema files `SessionAttachments.sql`, `SessionContextPins.sql`,
`InferenceRuns.sql`, `BillableOperations.sql`, `UploadedFiles.sql`, `UnseenServantWatermarks.sql`.

Owning docs read: DESIGN §10 attachment lifecycle and consistency (lines 1370-1445, including the sweep concurrency
paragraph), DESIGN `/v1/files` delete (line 3972), API.md `/v1/files` row (line 281), DESIGN §5.4 runtime proof context,
the `GrimoireEntitySql.Format(Guid)` spelling contract.

Baseline: the existing tests that cover this unit's classes all pass in the review tree (205 passed, 0 failed:
`SessionAttachmentStoreTests`, `UploadedFileRepositoryTests`, `SqliteBusyRetry*`, `SessionContextPinStoreTests`,
`TurnRunWriter*`, `UnseenServantWatermarkStoreTests*`, `UtcInstant*`, `SqlLikePatternsTests`). Every defect below therefore
passes the existing suite.

## Findings

### U29-R1 - Critical - Attachment paths collide on case- and normalization-insensitive volumes: a later upload overwrites an earlier one, or the orphan sweep deletes it

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2393-2405` (`BuildRelativePath`), with
  the sweep comparison at `:2161-2171`, the per-key gate at `:1554`, the read at `:1327-1364`, and
  `Storage/EncryptedBlobStore.cs:86-96` (`ReplaceOrMove`).
- Claim: logical keys are distinct under SQLite's BINARY comparison (`"LogicalKey" = @logicalKey`, the unique index
  `UX_SessionAttachments_Bound`, the Ordinal gate key), but the blob path is built directly from the logical key and the
  file name: `{session:N}/{logicalKey}/v{n}/{originalFileName}`. The default macOS volume (APFS) and Windows (NTFS) are
  case-insensitive, and APFS is also Unicode-normalization-insensitive. Two logical keys that differ only by case or by
  NFC/NFD form therefore name the same directory, and if the file names also match, the same file:
  1. `notes.txt` then `Notes.txt` in one session: both are version 1 of different keys. The second write's
     `EncryptedBlobStore.ReplaceOrMove` sees the destination "exists" and `File.Replace`s it, so the first row now points at
     the second upload's ciphertext. The read does not compare the blob's authenticated metadata (the row id passed to
     `CreateWriterAsync`) with the row, and `ReadBytesAsync` does not re-hash against `ContentSha256`, so the first
     attachment silently returns the second one's content when the lengths match, and throws `InvalidDataException`
     ("plaintext length mismatch") when they do not.
  2. `Report`/`a.txt` then `report`/`b.txt`: `b.txt` lands in the existing `Report/v1/` directory. The orphan sweep
     enumerates `Report/v1/b.txt`, which is neither in the Ordinal `knownPaths` set (the row says `report/v1/b.txt`) nor
     claimed by the BINARY `RelativePath = @relativePath` re-check, so it unlinks the bytes; `SweepMissingFileRowsAsync`
     then deletes the row. The attachment disappears at the next host start.
  3. The same happens for `café` typed in a browser (NFC) and `café` read from a macOS workspace (NFD).
  The Ordinal `BoundGateKey` also lets writers of `notes.txt` and `Notes.txt` run concurrently against one physical file, and
  `PromotionsInFlight` uses an Ordinal comparer on macOS (`:51-52`).
- Failure scenario: an operator attaches `README.md`, and the model later attaches `readme.md` from the workspace (or the
  operator re-uploads `Report.pdf` as `report.pdf`) in the same session on a default macOS or Windows install. Either the
  first attachment now serves the second's content to every later turn and pin, or the second attachment and its row are
  deleted at the next restart. The operator gets no error in either case.
- Evidence (probes, all passed, so the defect is present):
  - `U29Probe_case_only_duplicate_name_overwrites_the_earlier_attachment_bytes`:
    `first row '.../notes.txt/v1/notes.txt' reads: SECOND-note`; `second row '.../Notes.txt/v1/Notes.txt' reads: SECOND-note`;
    `files on disk: 1`.
  - `U29Probe_case_only_logical_keys_share_a_directory_and_the_sweep_unlinks_one`: rows `.../Report/v1/a.txt` and
    `.../report/v1/b.txt`; on disk `.../Report/v1/b.txt` and `.../Report/v1/a.txt`; after `ReconcileAsync`:
    `Report/a.txt row=True; report/b.txt row=False`.
  - `U29Probe_normalization_only_logical_keys_share_a_directory_on_apfs`: `NFC key row=True; NFD key row=False; distinct keys=True`.
- Suggested fix: stop deriving on-disk path components from display names. Store new blobs at a collision-free path such as
  `{session:N}/{attachmentId:N}` (or a lowercase hex digest of the logical key plus `v{n}/{attachmentId:N}`), keep
  `LogicalKey`/`OriginalFileName` only in the row, and leave existing rows on their stored `RelativePath`. Make the attachment
  publish refuse an existing destination (create-new) instead of `ReplaceOrMove`. In the orphan sweep, never unlink a file
  that `File.Exists` maps to a claimed row path (compare with the volume's semantics, not Ordinal). Bind reads to the row:
  pass the expected id to the reader and compare it with the header's authenticated metadata. Add the three probes as
  regression tests. Effort M.

### U29-R2 - Critical - The orphan sweep deletes every bound attachment whose file name ends in `.tmp` or contains `.tmp.`

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2149-2159`.
- Claim: `SweepOrphanAttachmentFilesAsync` unlinks any file older than its snapshot whose name ends with `.tmp` or
  contains `.tmp.` (OrdinalIgnoreCase), and it does this before it consults `knownPaths` or the live-row re-check. The
  heuristic is meant for staging temporaries, but a bound attachment's blob file name is the sanitized original file
  name, and `SessionAttachmentPathSanitizer` accepts `notes.tmp`, `results.tmp.csv` and `Report.TMP.md`. The upload path
  does not refuse them either (`UploadedFileMimeValidator.IsExtensionMimeMismatch` returns false for an unknown extension).
  `ReconcileCoreAsync` runs the orphan sweep (inside `DeleteStalePendingAsync`) before `SweepMissingFileRowsAsync`, so a
  single startup reconcile deletes the bytes and then the row. DESIGN says "Bound files are not background-pruned".
- Failure scenario: an operator attaches `export.tmp.csv` (or the model attaches `build.tmp.log` from the workspace). It
  works for the rest of the session. At the next `arcanum serve` start, `SessionAttachmentPendingGcHostedService` reconciles,
  and the attachment disappears from the session with only a "row whose file is missing" warning in the log.
- Evidence: probe `U29Probe_reconcile_deletes_bound_attachments_whose_file_name_contains_tmp` (passed):
  `notes.txt: row=True file=True`, `notes.tmp: row=False file=False`, `data.tmp.csv: row=False file=False`.
- Suggested fix: classify staging files by the exact shapes their producers create, all of which can be made to start with
  a `.` (a sanitized attachment name never does, because the sanitizer strips leading dots): EncryptedBlobStore's
  `.{name}.tmp.{32 hex}`, the clock probe `.sweep-clock.tmp.{32 hex}`, and the promotion/fork copy staging changed from
  `destination + ".tmp"` to the same dot-prefixed shape. Check `knownPaths` and the live re-check before any name rule. Add
  the probe as a regression test. Effort S.

### U29-R3 - Minor - The orphan sweep unlinks a new attachment whose bytes landed before the sweep but whose row has not committed

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2103-2175` (sweep) and `:461-535`
  (`PersistNewCoreAsync`); the remark at `:2085-2096` and DESIGN line 1436.
- Claim: the sweep's first guard spares only files modified at or after its clock probe. A new attachment publishes its
  blob, then waits in `InsertRowAsync` for `BEGIN IMMEDIATE` (busy_timeout 5 s, then Microsoft.Data.Sqlite's 30 s
  command loop, then `SqliteBusyRetry` for up to 2 min) before it inserts the row. During that wait the blob is older than
  the probe, is not in the snapshot, is not claimed by the re-check, and, unlike a promotion's copies, is not registered
  in `PromotionsInFlight`. The sweep unlinks it. Commit-time revalidation then refuses the insert, so the upload fails
  with `IOException` ("refused to publish metadata for missing or replaced bytes"), which surfaces as a 500. The same
  applies to an `EncryptedBlobStore` staging temporary during `VerifyEnvelopeAsync` (no writes, so its mtime ages), which
  makes the publish rename fail. A worse interleaving was traced but not executed: the sweep's re-check `SELECT` runs
  before the writer commits and its unlink runs after the writer's `ValidateOwnedBlob`. That commits a row for missing
  bytes and reports the upload as successful. DESIGN says that "nothing modified at or after the moment of the sweep's own
  path snapshot is touched, because an attachment write lands its ciphertext before it inserts its row". That only covers
  writes whose bytes land after the snapshot. The fork's exposure is documented; the new-attachment path's is not.
- Failure scenario: at host start (DESIGN: Kestrel is already accepting requests while the startup reconcile runs), an
  upload's insert waits behind another writer's transaction while the orphan sweep runs. The operator's upload fails with
  a 500 and has to be repeated.
- Evidence: probe `U29Probe_orphan_sweep_unlinks_a_new_attachment_waiting_for_the_writer_lock` (passed). A second
  connection holds `BEGIN IMMEDIATE`, a real `PersistNewAsync` publishes its blob and blocks, and the sweep
  (`DeleteStalePendingAsync`) runs on another scope. Output: `blob exists after sweep: False`; after the lock is released,
  `persist outcome: IOException: Attachment persistence refused to publish metadata for missing or replaced bytes.`
- Suggested fix: register every publish destination, plus its staging directory or prefix, in the in-flight set (rename it
  from `PromotionsInFlight`) from before `CreateWriterAsync` until the row commits or the write is abandoned. Do the same
  for fork copies. Correct the DESIGN paragraph. Effort S.

### U29-R4 - Minor - Promotion deletes the fsynced pending blob after publishing an unsynced copy

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2319-2364` (`AtomicCopyFileAsync`,
  `FlushAsync` at `:2351`) and `:725-728`.
- Claim: `EncryptedBlobStore` publishes a pending blob only after `Flush(flushToDisk: true)` (`EncryptedBlobStore.cs:1141`).
  Promotion copies it with `CopyToAsync` + `FlushAsync`, which does not reach the disk. It renames the copy into place
  (`File.Delete(destination)` then `File.Move`, which is not atomic), commits the row update, and then deletes the
  original. It flushes neither the copy nor its directory. On APFS a rename can be durable before the renamed file's
  data. After the WAL commit is checkpointed, a power loss or kernel panic before the copy's dirty pages are written leaves
  the Bound row pointing at a zero-length or partial ciphertext, and the only durable copy is already unlinked. Fork copies
  share the helper. The fork keeps its source, but the forked session's rows can point at an unsynced copy in the same way.
- Failure scenario: a first turn with attachments creates a session. The promotion commits, a checkpoint follows, and the
  machine loses power within the page-cache flush interval. After reboot the attachment fails authentication on every read
  and is reported by encrypted-file validation, and its bytes cannot be recovered.
- Evidence: code trace (power loss cannot be executed). `destStream.FlushAsync` is the only flush, and there is no
  `Flush(true)`, `FileOptions.WriteThrough` or `DurableDirectoryFlush` in `AtomicCopyFileAsync`. Compare
  `EncryptedBlobStore.CompleteAsync:1141` and `AtomicFile.cs:147/207/315`, which do flush.
- Suggested fix: `destStream.Flush(flushToDisk: true)` before closing, `File.Move(temp, destination, overwrite: false)`,
  and `DurableDirectoryFlush` on the destination directory before the row commit. Effort S.

### U29-R5 - Minor - An uploaded-file delete interrupted after quarantine strands the file permanently

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs:324-331` (and `:356-359`, `:430-434`).
- Claim: `TryDeleteUnreferencedOnceAsync` moves the bytes into `.arcanum-file-delete-{id:N}-*` before the metadata delete
  commits. If the process dies in that window, the transaction rolls back: the row is live, `{files}/{id:N}` is missing,
  and the bytes are in quarantine. Nothing in `src` restores or reclaims a delete quarantine. The only references to the
  prefix are in this file. The first statement of every later delete returns `RecoveryRequired` when such a quarantine
  exists, so the file can never be deleted (500 `file_delete_recovery_required`) and can never be read, because its bytes
  are not at their path. The same permanent state follows a post-commit `TryDeleteQuarantined` failure. In that case the row is
  gone, every later delete answers `RecoveryRequired` instead of `NotFound`, and the quarantined bytes are never reclaimed.
  DESIGN/API document the 500 but give no recovery path.
- Failure scenario: `DELETE /v1/files/{id}` is in flight when the host is killed (crash, `kill -9`, OS shutdown). From then
  on, every delete of that id returns 500, its content cannot be read, and a batch that names it fails for a missing input.
  No API, CLI command or startup step clears the state.
- Evidence: probe `U29Probe_uploaded_file_delete_interrupted_after_quarantine_is_stranded` (passed):
  `delete1=RecoveryRequired delete2=RecoveryRequired rowLive=True bytesAtPath=False`. `rg 'arcanum-file-delete' src` finds
  only `UploadedFileRepository.cs`.
- Suggested fix: add an idempotent startup step (or run it at the start of `TryDeleteUnreferencedAsync`) that, for each
  recognizable quarantine, restores the bytes when the row is live and finishes the unlink when the row is gone, with the
  same identity checks. Document the recovery in DESIGN §/v1/files. Effort S-M.

### U29-R6 - Minor - A stored logical key with leading whitespace cannot be looked up by its own key

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:779` (`GetByLogicalAsync` re-sanitizes),
  root cause in `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs:62-106`.
- Claim: the sanitizer trims whitespace first and strips leading dots afterwards (`TrimStart('.')`). Its trailing trim
  removes only `.` and ASCII space. It is therefore not idempotent: `". notes"` is stored as `" notes"`, and
  `"x ."` is stored as `"x "`. `GetByLogicalAsync` sanitizes its argument again, so the stored key (`" notes"`,
  which is what the index shows the model) becomes `"notes"` and matches nothing. `SessionAttachmentToolInjection` also
  `Trim()`s first.
- Failure scenario: an attachment named `. notes` (or a name ending in a no-break space and a dot) is listed in the
  attachment index as ` notes`. `read_session_file`, tool injection and logical-name pins all answer "not found" for it.
- Evidence: probe `U29Probe_logical_key_with_leading_whitespace_is_unreachable_by_its_stored_key` (passed):
  `stored key=' notes' index key=' notes' byStoredKey=False byTrimmed=False`.
- Suggested fix: make the sanitizer idempotent by repeating `Trim()` after `TrimStart('.')`/`TrimEnd` until stable, or by
  trimming all whitespace in the end trims. Add a property test that `Sanitize(Sanitize(x)) == Sanitize(x)`. Effort S.

## Checked and sound

- `SqliteBusyRetry`: it retries only codes 5/6 found in the inner-exception chain and never cancellation, the doubling
  backoff caps at 2 s, and the deadline is checked before each wait, so cancellation propagates through `Task.Delay`.
  The cursor's struct-mutation pattern was checked: `WaitAsync` increments before the first await. Every transactional
  lambda in this unit begins its own transaction inside the lambda, so a retry never reuses a doomed transaction. A
  nested `GrimoireBusyTimeoutException` is classified as busy by an outer loop, but the outer deadline has already elapsed,
  so it throws at once and does not double the bound.
- Ambient transactions: EF `BeginTransactionAsync` maps to Microsoft.Data.Sqlite `BEGIN IMMEDIATE` (Unspecified to
  Serializable, not deferred), so the fork's `InsertRowAsync` writer-lock probe (`UPDATE ... WHERE 0`) cannot hit
  `SQLITE_BUSY_SNAPSHOT`. `SqliteConnection.CreateCommand` auto-enlists the connection's transaction, so commands without an
  explicit `Transaction` are safe.
- `SqliteConnectionPragmas.Apply` sync-over-async: the initializer awaits only Microsoft.Data.Sqlite calls, which
  complete synchronously, with `ConfigureAwait(false)`. Neither UI project references `ArcanumDbContext`, so there is no
  synchronization context to deadlock.
- Guid spellings: `SessionContextPinStore` binds a `Guid` through `AddWithValue`, which Microsoft.Data.Sqlite renders as
  uppercase dashed text, identical to `GrimoireEntitySql.Format`. Probe `U29Probe_pin_store_binds_guid_as_canonical_text`
  showed `type=text`, value equal to `Format(sessionId)` and to `Sessions.Id`, which the FK requires. The `InferenceRuns`
  dash-free spelling is the documented exception, and every retention join normalizes it with a matching expression index.
- `UtcInstantText`: the fixed-width 7-digit pattern preserves ticks and makes ordinal text order chronological. The parser
  accepts every historical EF/SQLite form with AssumeUniversal/AdjustToUniversal, and literal `'Z'` parses as UTC.
  `ParseDateTime` returns `Kind=Utc`. The compiled-model mappings set `Value` before `ConfigureParameter` (EF order), so
  every EF write is canonical, and `Local` is refused. All `DateTime` assignments into the mapped properties
  (`Loremaster` `.UtcDateTime`, readers via `ParseDateTime`) are UTC.
- `PersistNewCoreAsync`: gate order is session then bound or pending; `PromotePendingAsync` takes session then pending;
  `DeleteStalePendingAsync` and the pending-directory sweep take pending only. No inversion. Cancellation before or during
  insert deletes the identity-captured blob. `CommitAsync` checks the token only before committing, so a cancelled request
  cannot orphan a committed row's bytes. Version overflow and the byte budget are measured before the write.
- `PromotePendingAsync`: pending turns exist only for session-less requests (`WizardIntelligenceProvider` creates them only
  when `request.SessionId is null`), so promotion targets a fresh session and cannot collide with existing bound rows except
  through R1. Copies are registered before the first byte, revalidated inside the commit transaction, and unregistered in
  `finally`. On failure the destinations are removed and the pending rows remain for GC.
- `UploadedFileRepository`: classification and conditional delete run in one IMMEDIATE transaction, every rollback path
  restores the quarantine, and BUSY at `BEGIN` happens outside the try (no rollback needed). `CreateForOwnedFileAsync`
  revalidates the owned file's identity inside the transaction and cleans up identity-safely.
- `TurnRunWriter`: single-statement autocommit writes are safe to replay after BUSY, and `TryAbandonRunAsync`'s Running
  guard makes recovery idempotent.
- `UnseenServantWatermarkStore`, `SessionContextPinStore`: single-statement upserts (`ON CONFLICT ... DO UPDATE`,
  `RETURNING`) are replay-safe, and the conflict targets match unique indexes.
- `SqliteUniqueConstraintViolation`: extended codes 1555/2067 are reported for TEXT primary keys and unique indexes.
  Callers catch `DbException`, and raw ADO throws `SqliteException` directly.
- `SqlitePragmaStatementFactory`: the grammar is closed (non-negative int; canonical 44-character Base64 of 32 bytes) and
  stack buffers are cleared.
- `SqliteNativeRuntime`: `Lazy` with ExecutionAndPublication caches a load failure (fail closed), and `FreezeProvider`
  prevents a later swap. The validator and manifest are test-time only (no `src` caller besides the runtime itself). Their
  narrow catches (`KeyNotFoundException` from a malformed manifest escapes) and the process-wide `ClearAllPools` therefore
  cannot affect a host.
- `SqlLikePatterns`: escapes `%`, `_` and `\`, and every caller passes `ESCAPE '\'`.

## Observations not raised as findings

- `UpdateSourceAsync` (`SessionAttachmentStore.cs:1757`) is the only write in the file outside `SqliteBusyRetry`. It still
  waits up to Microsoft.Data.Sqlite's 30 s command loop, so it matters only under a writer hold longer than 30 s.
- `ReconcileCoreAsync` runs the full orphan-file sweep twice (once inside `DeleteStalePendingAsync`, once at the end). This
  is a performance-lens item.
- HFS+ volumes (normalizing to NFD) would expose more of the R1 mismatch class, but the R1 fix covers it.

## Probes run

All in `reports/C/U29-reliability-probes/U29ProbeTests.cs`, built in the scratch worktree and run with `--no-build </dev/null`:

| Probe | Result |
|---|---|
| U29Probe_reconcile_deletes_bound_attachments_whose_file_name_contains_tmp | passed (defect present) |
| U29Probe_orphan_sweep_unlinks_a_new_attachment_waiting_for_the_writer_lock | passed (defect present) |
| U29Probe_logical_key_with_leading_whitespace_is_unreachable_by_its_stored_key | passed (defect present) |
| U29Probe_uploaded_file_delete_interrupted_after_quarantine_is_stranded | passed (stranded state confirmed) |
| U29Probe_pin_store_binds_guid_as_canonical_text | passed (sound) |
| U29Probe_case_only_logical_keys_share_a_directory_and_the_sweep_unlinks_one | passed (defect present) |
| U29Probe_case_only_duplicate_name_overwrites_the_earlier_attachment_bytes | passed (defect present) |
| U29Probe_normalization_only_logical_keys_share_a_directory_on_apfs | passed (defect present) |

The baseline run of 205 existing tests over the unit's classes in the review tree passed, so none of them catches
R1-R6.
