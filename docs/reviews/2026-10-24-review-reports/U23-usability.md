# U23 (Arcanum.Infrastructure, "Covenant (1)") - usability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Lens: usability (operator, model as tool caller, next engineer,
and, for an internal unit, whether a failure can be diagnosed from the outside).

Unit list: `scratchpad/review/units.json` was not present when this review started; the unit was reconstructed from its
stated size. The alphabetical listing of `src/RetroDownfall.Arcanum.Infrastructure/Covenant/*.cs` reaches exactly 9,964
cumulative lines at `CovenantManagementService.cs` (the same reconstruction `u23-files.json` later confirmed), so U23 is
the first 20 files of that directory.

## Reachability, established before judging severity

- `campaign_path_identities` has no production writer (`rg` finds only reads and a restore-time `DELETE`; Campaign-path
  administration is "deliberately absent", DESIGN 10.22.6). The restore-cleanup and full-reset Campaign cleanup arms of
  `CampaignPathMarkerLifecycle` therefore only ever see an empty registry in production. Defects there are latent.
- `ICovenantMaintenanceService` is registered in no container; `CovenantFamilyReinitializeCoordinator` is constructed
  nowhere (DESIGN and `CovenantPersistedAvailabilityPublisher` say so). Latent.
- Live: `CovenantContextProvider`, `CovenantManagementService` (status, list, query, detail, versions, sources, explain),
  `CovenantEntryErasureService`, `CovenantExportPolicy`, `CovenantCampaignScopeProbe`,
  `CovenantFeatureConfigurationPublisher`, `CovenantMaintenanceHostedService` with its three coordinators.

## Findings (most severe first)

### 1. Important - Covenant refusals and the per-turn absence reason are logged at Debug, which no sink can receive

- `CovenantMaintenanceHostedService.RunSweepAsync` (lines 214-225) logs every refused sweep (owner cleanup, search outbox,
  receipt compaction) with `LogDebug`, and and so is a successful sweep that removed heads (225). Only a thrown exception reaches
  `LogError` (229).
- `CovenantContextProvider.BeginTurnAsync` turns an unavailable gate, an unhealthy tier, a missing Campaign and an empty
  plan into a typed `CovenantTurnAbsence` (lines 39-56, 69-71). The enum remark (`ICovenantContextProvider.cs:7-14`) and
  DESIGN 10.13 ("every absence carries a typed reason rather than a null plan and a log line") say an operator reads it in
  `context inspect`. `rg Absence` shows its only consumer is `CovenantDispatchGate.Record`
  (`Api/Intelligence/CovenantDispatchGate.cs:378-390`), which writes it with `LogDebug`, saying it is "answerable from a log".
  `context inspect` carries no such field: a Covenant lane with zero tokens gets the generic reason
  "No effective content was available for this source." (`WizardIntelligenceProvider.ContextPreview.cs:1006`) whether the
  feature is off, the tier unavailable, no Campaign resolved or the plan empty. Only admission pressure has its own text
  (`:920-935`).
- The host pipeline floor is `MinimumLevel.Information()` (`LoggingBootstrapper.cs:45`); `AuditLogPageReader.cs:246` states
  the consequence in the code base's own words ("a Debug event reaches neither the rolling file, nor the ring buffer behind
  `GET /api/logs`, nor `arcanum logs`").
- `host.minLogLevelInBuffer` (Compendium label "Debug information level", `Compendium.README.md:100` lists `trace`,
  `debug`) only filters inside `SerilogLogRingBufferSink.Emit` (`SerilogLogRingBufferSink.cs:25-31`), after the pipeline
  floor, so it can raise the floor but never lower it. No test touches `MinLogLevelInBuffer`.

Probe P3 (see Probes): with `MinLogLevelInBuffer = Debug` and `AddArcanumSerilog()`,
`IsEnabled(Debug)=False IsEnabled(Trace)=False`, and the ring buffer holds only the Information marker.

Failure scenario: an erase whose COMMIT outcome could not be read back leaves its Covenant scope closed until restart
(`CovenantEntryErasureService.CommitUnsettled`), or a canonical tier fault makes the cleanup worker return
`Covenant.IntegrityFailure` / `StaleSnapshot` every pass. Every five minutes the three sweeps refuse; deleted Campaigns'
Covenant rows are not removed and the outbox is not drained, and nothing in the log, `arcanum logs`, `memory status` or
`doctor` says so. An operator who follows the Compendium label and sets the buffer to Debug still sees nothing. Likewise
"why was my standing preference not injected on that turn" has no answer anywhere.

Fix: log refused sweeps at Warning (first occurrence and on every change of error code, with the code and the sweep name,
as the Error branch already does); surface `CovenantTurnAbsence` in the context-inspect DTO or log it at Information only
for `CapabilityUnavailable`; make the Serilog floor a `LoggingLevelSwitch` driven by `host.minLogLevelInBuffer`, or drop
`trace`/`debug` from the documented values. Effort M (S for the sweep Warning alone).

### 2. Important - list/search report constant `shadow` and `materialization` when an evaluation Campaign is named

`CovenantManagementService.Head` (lines 726-733) returns `NotShadowed` and, for any Confirmed lane, `Eligible` whenever
`effectiveForCampaignId` is non-null. It never reads `item.Lifecycle`, never compares with another head, and the list
query does not even receive the evaluation Campaign (lines 71-77 build `CovenantListQuery` without it). The enum remarks
(`CovenantReadContracts.cs:8-45`) promise the opposite: `Shadow` says whether a head "is currently shadowed for the
evaluated Campaign", `Materialization` says whether it "would reach a prompt ... right now", "deliberately separate from
lifecycle ... a management surface that conflated the two would tell an operator their preference is active when nothing
has ever rendered it". `Shadowed`, `ShadowedByCampaignConfirmed` and `Ineligible` are never produced anywhere.
API.md:1015 documents the pair as "effective for the evaluated Campaign".

Probe P1: a Global `tone` head plus a Campaign `tone` head that shadows it, plus a Retired Campaign head, listed with
`EffectiveForCampaignId = C` and lifecycle `Any`:

```
Global//tone                lifecycle=Set     shadow=NotShadowed materialization=Eligible
Campaign/<C>/tone           lifecycle=Set     shadow=NotShadowed materialization=Eligible
Campaign/<C>/old.rule       lifecycle=Retired shadow=NotShadowed materialization=Eligible
```

Operator path that is live today: `arcanum memory covenant list --campaign <C> --lifecycle Retired --json` (or `Any`;
`CovenantCommands.cs:382` passes `EffectiveForCampaignId: campaignId`, `:804-805` copies both fields to
`CovenantEntryPayload`) reports every retired entry as `"materialization": "Eligible"`. The same on `search`. Over HTTP,
any client sending `scope: AllScopes|Global` with `effectiveForCampaignId` gets `NotShadowed` for Global heads that a
Campaign head shadows. No existing test pins either value (`rg NotShadowed tests` finds nothing; only `NotEvaluated` is
asserted), so every test passes with the constants.

Fix: derive both from data already in the page (same-key Campaign Confirmed head in the page's snapshot, lifecycle, lane,
curation mask), or report `NotEvaluated` for what is not computed and at least `Ineligible` for Retired. Effort M.

### 3. Minor - `explain` reports `Admission = Admitted` and zero tokens as if measured, and the docs say admission runs

`ExplainAsync`/`Explain` (`CovenantManagementService.cs:626-671`) loads and links only. It hard-codes `ConfirmedTokens: 0`,
`ProposedTokens: 0` (642-643), `CovenantAdmissionDecision.Admitted` (648) and `EstimatedTokens: 0` per section (667). The
inline comment (645-647) says "leaving admission unclaimed is the honest shape", but `Admitted` is a claim, and the
enum has no `NotEvaluated` member. `CovenantExplainDto`'s remark (`CovenantReadContracts.cs:303-311`) says it "runs the
same loader, linker, and admission code as a live turn", `CovenantExplainSectionDto` says default inspection reports
"byte and token counts", and DESIGN.md:2220 repeats "live loader, linker, and admission code".

Probe P2: a Global Confirmed entry that renders 87 bytes:
`admission=Admitted confirmedTokens=0 proposedTokens=0`, `section GlobalConfirmed items=1 renderedBytes=87 estimatedTokens=0`.

Failure scenario: an operator or client asks "why is my preference not honoured" for a Confirmed entry that the live
admission would refuse (`RequiredNoFit` fails the attempt) and is told `Admitted`, 0 tokens. Reach is the HTTP route and
`ArcanumApiClient` only (no verb, no Forge or TUI caller), and the operator-facing admission answer already lives in
`context inspect` (`WizardIntelligenceProvider.ContextPreview.cs:920-935`), hence Minor. Fix: run `CovenantAdmissionPlanner` against a
stated default budget, or add a `NotEvaluated` admission value and make the token members nullable; correct the three doc
sentences either way. Effort M (S for the contract change plus docs).

### 4. Minor - the erase's commit-failure cause is dropped and nothing is logged

`CovenantEntryErasureService` has no logger ("Nothing here logs" is deliberate for content). The COMMIT failure is wrapped
in `UncertainCommitException(failure, rollbackFailed)` (809) but the catch at 551-585 reads only `RollbackFailed`; the
inner `SqliteException` is never read. `ReReadReceiptAsync` swallows its own exception (621-624) and the closing
`CompleteAsync` result is discarded (601). The operator gets `Covenant.ManualRecoveryRequired`: "The erase's commit failed
and its outcome could not be read back, so its Covenant scope stays closed until the host restarts" or
`Covenant.MaintenanceFailed`: "commit failed and recorded nothing ... retry it".

Probe P6 (existing test seams `CommitForTesting`/`ReceiptReReadForTesting`, commit throwing `SqliteException("database or
disk is full", 13)`): see Probes. Failure scenario: disk full or an I/O error during an erase. The operator is told to
retry (or to restart the host), the cause that decides whether a retry can ever succeed is unavailable, and a closed
Covenant scope plus a failed privacy erase is exactly the state in which the cause matters. The primary error code is
content-free, so logging `SqliteErrorCode` and the exception type is safe. Fix: log one Warning with the mutation id,
disposition chosen and the SQLite result code (never the message); for the retry advice, say whether the rollback
succeeded. Effort S.

### 5. Minor (latent) - marker lifecycle refusals drop the Campaign and the closed blocker code they hold, and never log

Restore: `ObserveRegisteredRootAsync`/`Blocked` build a `CampaignPathCleanupRootBlockerEvidence` (Campaign id, closed
code `RootUnavailable|PhysicalIdentityMismatch|OwnershipMismatch`, observation digest) whose remark says "This value is
what a blocked cleanup records and what an operator later reads" (`CampaignPathMarkerLifecycleContracts.cs:94-101`;
`RestoreInventory.cs:36-40` "something to correlate two runs by"). `ValidateSeed` (`CampaignPathMarkerLifecycle.cs:589-594`)
then refuses the whole restore with a fixed `Covenant.ManualRecoveryRequired` string, "A restore cannot proceed while a
destination Campaign root cannot be opened.", discarding all of it, and `BackupRestoreService.Issue(error)` shows exactly
that string. No Campaign, no reason, no next step, and the only `Log.` call in the roughly 4,900 lines of lifecycle files is a
disposal-failure Warning with no identifiers (1087-1089). Probe P5 prints the message. The restart-proof messages
(`RestartRootProof.cs:165`) are justified by "the reader is the operator who now has to look at the directory", then
purposely name no Campaign, so the operator is not told which directory.

Full-installation reset: every one of roughly fifty refusal sites in `CampaignPathMarkerLifecycle.FullInstallationReset.cs`
returns the same `Inert()` (2014-2022): `Data.RecoveryRequired` / "The full-installation reset Campaign cleanup is not
available.", and `HostToolsMarkerPairResetCoordinator` collapses it again to "requires recovery". A missing root key, an
unopenable root, a marker that no longer proves ownership, a journal mismatch and a stale authority are
indistinguishable.

Reach: latent today (no registry writer), but the registration surface is the next thing this area gains and these
strings are what its first failure will print. Fix: put the Campaign id (a GUID, not a path) and the closed blocker code in
the message, log one Warning per refusal with owner operation id, Campaign id and reason code, and replace `Inert()` with a
closed reason enum. The content-free rule is about paths and digests, not about a GUID. Effort M.

### 6. Minor (latent) - `CovenantFamilyReinitializeCoordinator.RunAsync` discards the reason every time it keeps admission closed

`catch (CovenantReinitializeStepFailedException)` (312-316) never reads `.Error`; the failed `erased`, `published` and
`writer` results (211, 333, 344) are likewise dropped; every branch returns `Success(KeepClosed)`, a disposition not an
error. An operation that leaves the whole Covenant feature closed after the family may already be dropped records no cause
anywhere. The class is unwired (constructed nowhere), so there is no operator today; whoever wires it inherits an
undiagnosable failure. Fix: carry the `Error` code into the result or the long-running operation's terminal code, and log
it. Effort S.

### 7. Minor - a default (Covenant disabled) installation prints a "Refused" census in `memory status`

`StatusAsync` initialises `state = CovenantCensusReadState.Refused` (510) and skips the census when the feature is off
(516). `arcanum memory status` then prints `Covenant: disabled, available` followed by
`Covenant entries were not counted (Refused). This is not a report that you have none.` (`MemoryCommands.cs:159-164`). The
enum doc defines `Refused` as "the installation read capability was declined"; here nothing was asked. `Covenant` defaults
to off, so every new installation shows the line, and a script reading `census: "Refused"` cannot tell a disabled feature
from a closing exclusive operation (the case the member exists for). The behaviour is pinned by `CovenantStatusTests.cs:163`
and the Command Reference says "never on an installation with the feature off", so it is intended, but the reason is
wrong. Fix: add a `NotAttempted`/`Disabled` member or have the CLI say "Covenant is off, so entries were not counted".
Effort S.

## Coverage

Read in full, every line (20 files, 9,964 lines), all under `src/RetroDownfall.Arcanum.Infrastructure/Covenant/`:
`CampaignPathFullInstallationResetContracts.cs`, `CampaignPathMarkerLifecycle.FullInstallationReset.cs`,
`CampaignPathMarkerLifecycle.MarkerOwnershipProof.cs`, `CampaignPathMarkerLifecycle.RestartRootProof.cs`,
`CampaignPathMarkerLifecycle.RestoreInventory.cs`, `CampaignPathMarkerLifecycle.cs`, `CampaignPathMarkerLifecycleContracts.cs`,
`CampaignPathMarkerRootAuthority.cs`, `CovenantCampaignScopeProbe.cs`, `CovenantContextProvider.cs`,
`CovenantEntryErasureService.cs`, `CovenantExclusiveDisposition.cs`, `CovenantExportPolicy.cs`,
`CovenantFamilyReinitializeCoordinator.cs`, `CovenantFeatureConfigurationPublisher.cs`, `CovenantIndexRebuildCoordinator.cs`,
`CovenantMaintenanceCoordinators.cs`, `CovenantMaintenanceHostedService.cs`, `CovenantMaintenanceService.cs`,
`CovenantManagementService.cs`. Also read in full, because small: `ICampaignPathMarkerLifecycle.cs`,
`ICampaignPathMarkerLifecycle.FullInstallationReset.cs`, `DeferredCampaignPathMarkerConnectionSource.cs`.

Read in part to trace callers and contracts: `BackupRestoreCovenantCoordinator` (+`.MarkerConnections`),
`BackupRestoreService.Issue`, `HostToolsMarkerPairResetCoordinator` (inventory, `Inert`, `PreJournalRefusal`),
`ArcanumErrorMapper` (Covenant arms), `LoggingBootstrapper`, `SerilogLogRingBufferSink`, `AuditLogPageReader.LogUnreadable`,
`CovenantDispatchGate.Record`, `MemoryEndpoints.BuildCovenantStatusAsync`, `MemoryCommands.WriteCovenantStatus`,
`CovenantCommands.List/Search`, `CovenantReadContracts.cs`, `CovenantWireValidation`, `CovenantAvailability`,
`CovenantCleanupWorker`/`CovenantSearchOutboxWorker` error returns, DESIGN 10.11, 10.13, 10.16-10.22 passages and
`10.19.9`/`10.19.11`, API.md 8.28 and 8.35, Command Reference `memory`, `backup restore` and Covenant sections,
Compendium.README `features.covenant` and `host.minLogLevelInBuffer`.

## Checked and found sound (and how)

- Every status code the erase documents in API.md 8.35 (404/409/410/403/503 families) is produced by the matching `Error`
  in `CovenantEntryErasureService` and mapped by `ArcanumErrorMapper.ResolveStatusCode` (lines 160-230); traced by hand.
- The erase's single-disposition protocol (commit, proven refusal, uncertain commit settled by a fresh-connection receipt
  read) matches the code at 508-607 and the existing tests `An_uncertain_commit_*`; the operator-facing sentences name the
  action ("show the key again", "prepare it again", "retry it").
- `CovenantContextProvider` lease ownership (`retained`/`finally`) and the documented gate order; no path leaks a lease.
- `CovenantExportPolicy` fails closed on a missing ledger table with a named recovery code; the case-sensitive identity
  spellings (`LedgerIdentity` vs `CanonicalIdentity`) are handled and explained; `CovenantCampaignScopeProbe` uppercases.
- `CovenantFeatureConfigurationPublisher` serialises callbacks and republishes the monitor's current value, so a late
  callback cannot undo a later disable.
- Cursor handling in `CovenantManagementService`: filter digest binds the clamped page size, key-version drift is
  `InvalidCursor`, dataset drift is `StaleCursor`, and limit 0/negative resolves to the default; matches API.md 8.28.
- `StatusAsync` attempts no census when disabled (documented reason: latching `CovenantProcessResidence`), and reports
  `Failed` versus `Refused` separately on an enabled installation.
- Every DESIGN section reference in the unit's remarks (10.12, 10.13, 10.16-10.19.9) resolves to an existing heading.
- `RunSweepAsync` isolates each sweep, so one refusal cannot starve the other two (the structure is right; only the log
  level is wrong, finding 1).
- The exit-code, `--json` and confirmation behaviour of the CLI verbs that call this unit is covered by the Command
  Reference text quoted in the Covenant section; the unit itself has no CLI surface.

## Probes

All in a scratch worktree (`wt/U23-usability`, detached at `ac0d2d8e`), test project built there, run with
`dotnet test ... --no-build --filter FullyQualifiedName~U23 </dev/null`.

- P1/P2/P3: new `tests/RetroDownfall.Arcanum.Tests/Covenant/U23UsabilityProbeTests.cs` (kept in the scratch worktree).
  Output:
  ```
  P3 IsEnabled(Debug)=False IsEnabled(Information)=True IsEnabled(Trace)=False
  P3 ring buffer entries=Information:P3 information marker
  P2 admission=Admitted confirmedTokens=0 proposedTokens=0
  P2 section GlobalConfirmed items=1 renderedBytes=87 estimatedTokens=0
  P2 section CampaignConfirmed items=0 renderedBytes=0 estimatedTokens=0
  P2 section CampaignProposed items=0 renderedBytes=0 estimatedTokens=0
  P1 Global//tone lifecycle=Set lane=Confirmed shadow=NotShadowed materialization=Eligible
  P1 Campaign/cccccccc-.../tone lifecycle=Set lane=Confirmed shadow=NotShadowed materialization=Eligible
  P1 Campaign/cccccccc-.../old.rule lifecycle=Retired lane=Confirmed shadow=NotShadowed materialization=Eligible
  P1 search health reported by a list page: Healthy/CanonicalFallback/None
  ```
- P5 (restore refusal text for a blocked seed) and P6 (erase commit failure with a disk-full `SqliteException`): see the
  appended results below.

- P5 (added to `CampaignPathMarkerLifecycleTests`, scratch worktree): a restore preparation with one `Unavailable` seed
  (closed code `RootUnavailable`) is refused with
  ```
  code=Covenant.ManualRecoveryRequired
  message=A restore cannot proceed while a destination Campaign root cannot be opened.
  containsCampaignId=False
  containsBlocker=False
  ```
  The existing test `A_seed_whose_root_is_blocked_refuses_the_whole_preparation` asserts only the code, so any message
  passes.
- P6 (added to `CovenantEntryErasureServiceTests`, scratch worktree): COMMIT throws `SqliteException("database or disk is
  full", 13)` and the receipt re-read fails:
  ```
  code=Covenant.ManualRecoveryRequired
  message=The erase's commit failed and its outcome could not be read back, so its Covenant scope stays closed until the host restarts.
  mentionsDiskFull=False
  ```
  The service has no `ILogger`, so the SQLite result code appears nowhere. The existing test pins the code only.
- Not run, and why: the published AOT binary was not present, and the hosted-service Debug branch (`outcome.IsFailure`)
  needs a scoped `ArcanumDbContext`; finding 1 rests on the source (`CovenantMaintenanceHostedService.cs:216`) plus P3
  rather than a captured log from a failing sweep. The existing `One_sweep_refusing_does_not_stop_the_pass` and
  `AdmittedCovenantPassDrainsAllThreeSweepsAndScopeDisposal` tests never reach that branch (they log Error from a missing
  DbContext), so a regression that moved the refusal log to any level would pass them.
