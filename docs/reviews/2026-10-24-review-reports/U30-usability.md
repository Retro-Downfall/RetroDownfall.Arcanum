# U30 (Arcanum.Infrastructure, "Data/Covenant (1)") - usability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Lens: usability (operator, model as tool caller, next engineer, and,
for an internal unit with no operator surface, whether a failure the unit produces can be diagnosed from the outside).

## Unit list

`scratchpad/review/units.json` did not exist when this review started, so the unit was reconstructed from its stated size
(about 11,940 lines, area "Data/Covenant (1)"). The alphabetical listing of
`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/*.cs` reaches exactly 11,940 cumulative lines at
`CovenantErasureStartupRecoveryOwnerAdopter.cs`, so U30 is the first 29 files of that directory (the same reconstruction
method the U23 reviewer used and `u23-files.json` later confirmed):

`ArtifactSensitivityLedger.LexiconReplacement.cs`, `ArtifactSensitivityLedger.cs`, `CampaignMaintenanceCheckpointStore.cs`,
`CampaignPathFullResetCleanupEvidenceStore.cs`, `CampaignPathMarkerIntentStore.cs`, `CampaignRollupStore.Manifests.cs`,
`CampaignRollupStore.Read.cs`, `CampaignRollupStore.Write.cs`, `CampaignRollupStore.cs`, `CampaignSummaryLifecycle.cs`,
`CovenantAgentErasureGate.cs`, `CovenantArtifactPlanRunner.cs`, `CovenantAvailability.cs`,
`CovenantCanonicalContentTables.cs`, `CovenantCanonicalErasureTransaction.cs`, `CovenantCleanupWorker.cs`,
`CovenantClosedPeriodAuthority.cs`, `CovenantClosedPeriodLedgerConnection.cs`, `CovenantConnectionDrain.cs`,
`CovenantConnectionEnrolmentInterceptor.cs`, `CovenantCurationKernel.cs`, `CovenantDisclosureExposureReader.cs`,
`CovenantDisclosureJournal.cs`, `CovenantDisclosureWriter.cs`, `CovenantEntryErasurePlan.cs`,
`CovenantEnvelopeStateStore.cs`, `CovenantErasureInventorySource.cs`, `CovenantErasureProgress.cs`,
`CovenantErasureStartupRecoveryOwnerAdopter.cs`.

## What this unit is, for the operator

No verb, route or tool lives here. The unit is the Grimoire storage layer under the Covenant (standing preferences), the
sensitivity-label ledger, Campaign continuity summaries ("rollups") and the protected-erasure machinery. Its surface to a
human is therefore (a) the `Error` text and code it returns, which `ArcanumErrorMapper` and the CLI show verbatim, (b) the
very few log lines it writes (two files in 29 have a logger), and (c) what the docs promise about those errors. The
Command Reference (`Covenant.*` table, line 1143-1144) and API.md (line 919) say each 503 Covenant code "names an operator
action rather than a retry" and tell the operator to "repair the named content-free blocker". Most findings below are
places where this unit returns the opposite: one fixed sentence for many causes, no identifier, no next step, and no log.

## Findings (most severe first)

### 1. Important - one oversized Entry wedges Campaign continuity for the whole Campaign, with an unmapped code and no identifier

`CampaignRollupStore.PrepareContributionWithinAsync` (`CampaignRollupStore.Read.cs:50-65`) budgets a contribution page as
`InputPageUtf8Bytes (32768) - bytes(previous summary)`. When the first unsummarized Entry does not fit it throws
`new Error("campaign_rollup.input_too_large", "A native Entry exceeds the bounded Campaign contribution page.")`. There is
no skip, split or truncate path and the cursor cannot move past the Entry, so every later attempt fails identically.

- Threshold, measured (probe P1): with no prior summary an Entry of 32,768 bytes is accepted and 32,769 fails; with a
  full 8,192-byte prior summary an Entry of 24,576 bytes is accepted and 24,577 fails. The default
  `Sessions.MaxEntryContentBytes` is 1,048,576 (`SessionSettings.cs:14`), so a pasted log or a long code answer is a
  normal Entry that the host accepts and this store then refuses for ever. A second attempt fails the same way (P1).
- The code is a bare string literal, absent from `ErrorCodes` and from `ArcanumErrorMapper`; `ResolveStatusCode` returns
  500 for it (P1 first line). The message names no Entry, Session, Campaign, size or limit, so the operator cannot find
  the Entry (the API has `DELETE /api/sessions/{id}/entries/{entryId}`) or even know which Session holds it.
- Reach, traced from the callers (not run end to end): with `features.campaignRollups` on, a Campaign-bound live turn
  runs `ProcessSessionAsync` then `ProcessCampaignAsync` (`CampaignRollupTurnPreparer.cs:624-646`) and returns the error
  as the turn's failure (`:628-631`, `:643-646`). `ProcessPagesAsync` returns a prepare failure unchanged
  (`CampaignRollupMaintenance.cs:216-220`), and `ProcessCampaignAsync` drains the pending contributions of every Session of
  the Campaign and aborts on the first failure (`:138-140`) before the fold. So one oversized Entry in any Session makes
  every later turn in every Session of that Campaign fail, until the feature is switched off or the Entry is deleted.
- In the background lane (`Loremaster.cs:266-286`) the same failure is logged as `LogDebug("... remains pending for
  Session {SessionId}")` with no error code, below the Information floor (`LoggingBootstrapper.cs:45`, quoted in the U23
  report), so nothing visible says why continuity stopped advancing.
- The documentation promises the opposite: DESIGN.md:466-467 "These allocation bounds do not cap Campaign lifetime or the
  number of contributors" and "A failed or interrupted fold advances no pointer or watermark" (true, which is the
  problem); Compendium.README.md:181 states the 32 KiB bound without saying a single Entry larger than the remaining page
  stops the Campaign. The existing test (`CampaignRollupStoreTests.cs:229-243`) pins the refusal only, with a 34,000-byte
  Entry and no prior summary.

Failure scenario: a user pastes a 30 KB build log into a Campaign-bound Session; the Campaign already has a full summary.
The reply is fine. On the next turn (and on every turn of every other Session in that Campaign) the host answers 500
`campaign_rollup.input_too_large: A native Entry exceeds the bounded Campaign contribution page.` Nothing in the log at
Information or above names the Session; `context inspect`, `memory status` and `doctor` do not show it.

Fix: (a) make an unsummarizable Entry non-fatal: record it as consumed-but-skipped (or split it across pages) so the
cursor advances, and make `ProcessCampaignAsync` skip a failing Session instead of aborting the fold; (b) add a typed
`ErrorCodes.CampaignRollup.*` constant mapped by `ArcanumErrorMapper`, with the Session id, Entry id/sequence, size and
limit in the message (all content-free); (c) log the failure at Warning, once per Session and code, from `Loremaster`;
(d) document the limit beside the 32 KiB sentence. Effort M (S for b-d alone).

### 2. Important - a refused startup adoption of the Covenant erasure owner reports one sentence for every cause, logs nothing, and points at logs

`CovenantErasureStartupRecoveryOwnerAdopter` has nine `return Refusal()` sites (lines 174, 192, 196, 208, 227, 251, 265,
285, 320) and a catch-all (`:206-209`, any `Exception`, including `SqliteException`). `Refusal()` (`:333-337`) is one fixed
`Covenant.ManualRecoveryRequired` / "Durable Covenant erasure ownership could not be reconstructed safely."

Probe P3 (existing fixture, one durable reset launch seeded, each corruption applied inside a savepoint): payload NULL,
`State = 99`, `RecoveryPolicy = 99`, `CheckpointVersion = 99`, an upper-cased checkpoint reference, a renamed
`LongRunningOperations` table (a storage fault) and a second valid owner all produce the identical code and message.

The caller, `GrimoireDatabaseBootstrapper.cs:1197-1204`, discards `erasureOwner.Error` and throws
`ProtectedRecoveryUnavailable()` (`:1340-1343`): "Protected Covenant startup recovery could not prove one durable owner.
See logs for recovery steps." Neither the adopter (no logger) nor that call site logs anything, so there are no "recovery
steps" in any log. The operation id is parsed in most branches and is a canonical GUID by then, but is not reported.

The documented remedy cannot be followed: Command Reference line 1143 says to "inspect `arcanum operation show <id>`",
which needs a running host (the host just refused to start) and an id the message never gave. No doc mentions this startup
refusal (`rg "durable owner|could not prove" docs` finds nothing about it). The class's own test comment (adopter tests,
"A launch whose own identity fields disagree ... is refused, and says nothing") explains the content-free intent: do not
echo rejected bytes. A closed reason code and the operation id do not echo anything a corrupt row chose.

Failure scenario: a power cut during `arcanum memory covenant reset` leaves a half-written checkpoint, or a storage fault
makes the read fail. `arcanum serve` exits at startup with the sentence above. The operator cannot tell a damaged row from
two owners from a locked file, cannot tell which operation, and has no log to read.

Fix: give `Refusal` a closed reason enum (`MalformedRow`, `PolicyMismatch`, `UnsupportedVersion`, `UnreadableCheckpoint`,
`WrongOperation`, `SecondOwner`, `GateRefused`, `StorageFault`), carry the operation id when the row id parsed, log one
Warning from the bootstrapper before throwing (`{OperationId} {Reason}`), and add the startup remedy to the Command
Reference row (what to inspect without a host, and what is safe to do). Effort M.

### 3. Important - erasure inventory/preflight refusals name no label, kind or cause, and cannot be told apart from a transient fault

`CovenantErasureInventorySource` returns three static errors for everything: `UnsafeInventory`
(`Covenant.IntegrityFailure`, "The Covenant erasure inventory could not be proved from one bounded storage snapshot.",
used at 17 sites: lines 93, 110, 193, 229, 298, 355, 390, 429, 464, 677, 684, 711, 740, 778, 794, 824, 837),
`UnadvanceableCanonicalState`, and `ManualManagedErasure` (`Covenant.ManualArtifactErasureRequired`, "A managed artifact
does not have one exact adopted ownership record and requires manual erasure.", lines 217, 316, 483). The snapshot helpers
end in bare `catch { return UnsafeInventory; }` (`:737-741`, `:821-825`), so a locked file, a full disk, a missing table,
an unknown artifact kind (`CovenantSensitiveArtifactPurgePolicy.Resolve` failing) and a failed lease dispose are the same
sentence. `ArtifactSensitivityLedger.Materialize` (`:400-408`) adds "This artifact's sensitivity label is malformed and
cannot authorize any read." with no label id; `ReadPageWithinAsync` hands it straight up the preflight.

This is the path of a privacy-critical operator action: `arcanum memory covenant reset` and the factory erasure run the
inventory as `PreflightBeforeCanonicalAsync` and abort with this error (`CovenantErasureCoordinator.cs:2228-2247`). The
abort records only the error's code as the operation's terminal code, and `AbortBeforeErasureAsync` logs a Warning with
`{ErrorCode}` alone when it reopens admission (`:2918-2922`; the `{ErrorMessage}` variant at `:2924-2932` is only for the
admission-stays-closed branch), so the sentence itself reaches neither log nor record. Messages and docs disagree: API.md:919 "Each names an operator action
rather than a retry"; Command Reference 1144 "Repair the named content-free blocker, then retry". There is no named
blocker. "Requires manual erasure" does not say which artifact (the label already carries kind and artifact id, both
content-free GUIDs) or how to erase it by hand.

Failure scenario: a reset is refused with "requires manual erasure" because one Campaign-summary file lost its ownership
record. The operator has the code and nothing else; there is no list of affected labels in any surface this unit feeds, and
the only log line is the coordinator's code-only Warning. Equally, a transient lock gives "could not be proved from one bounded storage
snapshot", which reads as corruption and offers no retry hint.

Fix: make `UnsafeInventory` a small closed set (`StorageBusy`, `StorageFault`, `UnknownArtifactKind`, `SnapshotMismatch`) and
put `LabelId`, `ArtifactKind` and (for managed files) the blocker class in `ManualManagedErasure`; classify
`SqliteException` with the existing `StorageBlocker` rule (finding 5) so a busy file says "retry"; log one Warning per
refusal with the operation id and the label count. Effort M.

### 4. Minor - the disclosure writer closes for good after one failed open, says "maintenance", and never logs why

`CovenantDisclosureWriter.AcknowledgeAsync` (`:110-129`) calls `FailClosed` on any `OpenVerifiedAsync` failure other than
`Grimoire.MaintenanceUnavailable`: a thrown exception (`:466-470`), a failed `OpenFreshAsync`, a dataset mismatch, a
`StaleSnapshot`. `FailClosed` (`:573-594`) sets `_accepting = false; _pendingClose = true` and only `ReopenAsync` clears
it; `ReopenAsync` is called by the erasure coordinator and the bootstrapper's authenticated-transition restore, nowhere else
(`rg ReopenAsync src`). Every later call returns `AdmissionClosed()` (`:616-619`): `Covenant.LifecycleConflict`, "The
Covenant disclosure writer is closed for maintenance."

Probe P2 (existing `WriterHarness`, one-shot open refusal): the first call fails with the refusal's own code; the second
call, with a healthy open available, fails with `LifecycleConflict` "closed for maintenance" and opens nothing; after a
healthy availability republication the third call fails the same way; `ReopenAsync` then makes the fourth succeed. The
class has no logger, and `CovenantDispatchGate.cs:600-606` returns the error without logging. Fail-closed is the right
privacy default and `An_ordinary_open_failure_keeps_the_writer_closed_without_a_lease` pins it, but no maintenance is
running, the first cause is lost, and 409 invites a client retry loop that cannot succeed until restart.

Fix: log the first failure at Warning with its code (and the exception type) when `FailClosed` fires; give the closed state
its own message ("closed after an earlier failure to open its connection; restart the host or run ..."), or allow a bounded
re-attempt of `OpenVerifiedAsync` once the failure was environmental. Effort S (message and log) / M (retry).

### 5. Minor - transient SQLite faults are reported as integrity failures, against the kernel's own classification and the API doc

`CovenantProtectedArtifactErasureKernel.StorageBlocker` (`CovenantProtectedArtifactErasureKernel.cs:450-472`) and
API.md:81 classify busy, locked, no-memory, read-only, interrupt, I/O, full-disk and cannot-open as "environmental ...
says nothing about the stored data, and can clear on its own" (`Covenant.Unavailable`), and everything else as integrity.
Five sites in this unit collapse every `SqliteException` to an integrity-style code:

- `CovenantDisclosureExposureReader.cs:191-197` -> `Covenant.IntegrityFailure` "could not be folded safely".
  Probe P5: a second connection with a 1-second timeout reading while another holds `BEGIN EXCLUSIVE` gets
  `IntegrityFailure`; the identical read succeeds once the lock is released.
- `ArtifactSensitivityLedger.WriteWithinAsync` (`:190-197`) -> `IntegrityFailure` "could not be persisted (N)": a full disk
  (13) while a turn is finalized is reported as an integrity failure of the label.
- `CovenantErasureInventorySource` bare catches (finding 3) and the adopter's catch-all (finding 2).
- `CampaignRollupStore.WithinAsync` (`CampaignRollupStore.cs:302-307`) -> `Grimoire.WriteFailed` "Campaign summary storage
  could not complete (N)" even for the read-only entry points (`write: false`).

The operator-visible effect is a wrong diagnosis ("integrity") and no retry hint for something that clears by itself.
Fix: one shared `SqliteFaultClassifier` (the kernel's rule) used by all five sites. Effort S-M.

### 6. Minor - connection-level refusals name no holder and the open-failure log carries no cause

- `CovenantConnectionDrain.DrainAsync` (`:226-232`) refuses with "A Covenant connection handle did not close, so no
  exclusive maintenance connection may be opened." The type remarks (`:13-23`) exist precisely because an exclusive open
  "fails on a lock whose holder the caller cannot name", but the refusal names neither the component that enrolled the
  handle nor how many are open. `Register` takes no label, so adding one is the whole fix.
- `CovenantConnectionEnrolmentInterceptor.LogFailure` (`:205-218`) logs "An ordinary Grimoire connection failed to open
  safely." at Error with no exception, no type and no SQLite code. A wrong key (SQLITE_NOTADB), a permission error, a full
  disk and a pragma read-back failure are one line. The test pins it (`GrimoireConnectionAdmissionInterceptorTests.cs:1087-1096`,
  `Assert.Null(entry.Exception)`), so the omission is deliberate, but the exception type and `SqliteErrorCode` carry no
  key material.
- `CovenantCanonicalErasureTransaction.Failure(GrimoireBusyTimeoutException)` (`:754-760`) says "another handle held the
  database for N attempt(s) over T". The drain already proved this process's handles are closed, so the holder is another
  process; the message gives no next step (close the other tool, retry).

Fix: add an enrolment label to `Register`; log `eventData.Exception.GetType().Name` and `SqliteErrorCode` in `LogFailure`
(update the test); reword the busy-timeout message to name "another process" and the retry. Effort S.

## Coverage

All 29 files read in full, every line: the list above, totalling 11,940 lines (`CovenantEntryErasurePlan.cs` read as lines
1-650 and 647-1266; all others in one read). Owning documents read for this unit: DESIGN 10.17-10.22 passages naming the
classes (1931, 1941, 1952, 2005, 2074, 2850-2905, 2941), DESIGN 465-474 (Campaign continuity), Command Reference 1130-1146
(durable operation error table), API.md 81, 919, Compendium.README.md 181. Read in part to trace callers and contracts:
`Loremaster.cs`, `CampaignRollupMaintenance.cs`, `CampaignRollupTurnPreparer.cs`, `GrimoireDatabaseBootstrapper.cs`
(1170-1345, 520-560), `CovenantErasureCoordinator.cs` (2190-2290, 2740-2830, 3595-3625), `CovenantDispatchGate.cs`
(585-625), `ArcanumErrorMapper.cs`, `CovenantProtectedArtifactErasureKernel.cs` (440-500), `CovenantEntryErasureService.cs`
(call sites only), schema files for the review-event cascades.

## Checked and found sound (and how)

- `CovenantCleanupWorker`: one outbox ordinal space across owners matches DESIGN 1931 and its comment; no-head deletions do
  not advance the search sequence; the delete order is child-first and the review-event/decision-receipt tables cascade
  (`covenant_review_events.sql`, `covenant_review_decision_receipts.sql`), so the omission of explicit deletes is safe.
- `CovenantCanonicalErasureTransaction.StampDatasetAsync`: its three zero-row causes (replayed own commit, wrong source
  state, absent singleton) each return a distinct message; the replay arm converges instead of refusing.
- `CovenantCurationKernel`: receipt-first replay, the idempotency-conflict message, and four distinct `StaleSnapshot` texts
  (dataset, key-reclamation epoch, dependency epoch, binding epoch) each say what moved.
- `CampaignRollupStore` publish refusals use nine distinct `Stale(...)` texts that say which part of the source changed.
- `CovenantAgentErasureGate`: the pinned and erased refusals are deliberately identical for the agent, documented, and the
  model gets a stop signal ("managed by the operator in this scope").
- `CampaignMaintenanceCheckpointStore`: the 5-minute claim lease is renewed every minute during a paid call
  (`CampaignRollupMaintenance.KeepClaimAliveAsync`), so a slow local model does not expire it. `Stale()` lumps eight
  causes, but one (`CovenantMaintenanceCheckpoint.Failed`) has no production writer, and the rest are fenced states a
  retry resolves, so I did not file it.
- `CampaignRollupStore.Read.cs:183-186` (`input_too_large` for a fold page) is unreachable: contributions are at most 8,192
  bytes and a page has at least 24,576 available.
- `CovenantEntryErasurePlan`'s thrown `InvalidOperationException`s (Campaign not registered, epoch moved) are guarded
  earlier by `CovenantEntryErasureService` (`CampaignIsLiveAsync` at lines 253 and 693).
- `CovenantConnectionDrain`: the documented order (direct handles first, pools after) and counted, idempotent enrolments
  match the code; `CovenantDisclosureExposureReader` avoids `SUM` and bounds the read to sixteen rows as it says.
- `CovenantEnvelopeStateStore` and `CovenantClosedPeriodAuthority` messages state the fail-closed consequence in the text.

## Probes

All in a scratch worktree (`wt/U30-usability`, detached at `ac0d2d8e`; removed at the end of the review), built with
`dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj`, run with
`dotnet test ... --no-build --filter FullyQualifiedName~U30 </dev/null`. Sources: new
`tests/RetroDownfall.Arcanum.Tests/Data/U30UsabilityProbeTests.cs` (P1, P5), one test added to
`Data/Covenant/CovenantDisclosureWriterTests.cs` (P2) and one to
`Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs` (P3). Raw output is in
`reports/C/U30-probes/output.txt`; copies of the sources are in the same directory.

```
P1 status mapping of campaign_rollup.input_too_large = 500
P1 noPreviousSummary entryBytes=24000 ok=True
P1 noPreviousSummary entryBytes=24577 ok=True
P1 noPreviousSummary entryBytes=32768 ok=True
P1 noPreviousSummary entryBytes=32769 ok=False code=campaign_rollup.input_too_large message=A native Entry exceeds the bounded Campaign contribution page.
P1 summary=8192B entryBytes=24000 ok=True | second attempt ok=True
P1 summary=8192B entryBytes=24576 ok=True | second attempt ok=True
P1 summary=8192B entryBytes=24577 ok=False code=campaign_rollup.input_too_large message=A native Entry exceeds the bounded Campaign contribution page. | second attempt ok=False
P1 summary=8192B entryBytes=30000 ok=False code=campaign_rollup.input_too_large message=A native Entry exceeds the bounded Campaign contribution page. | second attempt ok=False
P2 first   ok=False code=Covenant.MaintenanceFailed message=The test ordinary connection factory refused the open.
P2 second  ok=False code=Covenant.LifecycleConflict message=The Covenant disclosure writer is closed for maintenance. opened=0
P2 third   ok=False code=Covenant.LifecycleConflict message=The Covenant disclosure writer is closed for maintenance. opened=0
P2 reopen  ok=True; fourth ok=True opened=1
P3 case=[payload NULL | state = 99 | policy = 99 | version = 99 | reference uppercased | table renamed | two valid owners]
   failed=True code=Covenant.ManualRecoveryRequired message=Durable Covenant erasure ownership could not be reconstructed safely.   (identical, seven times)
P5 busy exposure read ok=False code=Covenant.IntegrityFailure message=The nonrevocable Covenant disclosure exposure could not be folded safely.
P5 same read after the lock is released ok=True
```

Not run, and why: the end-to-end consequence of finding 1 (a turn failing) needs `CampaignRollupMaintenance` with a
provider client, budget monitor and claim store; it is established by tracing the call chain at the cited lines. The
bootstrapper's silence in finding 2 is established by reading `GrimoireDatabaseBootstrapper.cs:1197-1204`, not by starting
a host on a damaged database. The published AOT binary was not present and is not needed for an internal unit.
