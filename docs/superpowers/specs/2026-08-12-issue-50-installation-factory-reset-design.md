# Issue #50 Installation Factory Reset Design

**Date:** 2026-08-12

**Status:** Design approved, final artifact review pending

**Issue:** [#50, Add complete installation reset](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/50)

**Related:** [#47, Secure credential storage](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/47), [#54, Canonical CLI placement](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/54)

## Objective

Extend the existing `arcanum data factory-reset` command into the sole installation-reset workflow. The command must erase the selected Arcanum-owned state, preserve documented exclusions, and recover safely from interruption. Global and all scope must also prove that the next normal launch will enter the canonical first-run bootstrap path. Workspace scope proves targeted absence while preserving the running installation.

The implementation reuses the existing data-retention factory-reset engine for Grimoire rows, encrypted blobs, indexes, and durable reconciliation. Installation-wide coordination adds exact filesystem inventory, workspace metadata handling, host and daemon lifecycle control, closed-catalog credential purge, an external recovery journal, and fresh-start verification.

## Public CLI contract

```text
arcanum data factory-reset (--workspace | --global | --all) (--dry-run | --apply) [--force]
```

The command retains its current canonical spelling. No `total-reset` command, alias, or parallel deletion engine is added.

### Required choices

- Exactly one scope is required: `--workspace`, `--global`, or `--all`.
- Exactly one mode is required: `--dry-run` or `--apply`.
- Repeating the same scope or mode selector is also invalid; selection is one occurrence, not merely one distinct value.
- Missing or conflicting scope or mode choices return exit code `2` before API client creation, prompting, or filesystem access.
- `--force` is command-specific and valid only with `--apply`.

### Confirmation

- `--dry-run` never prompts and never mutates.
- Apply with both the recursive global `--yes` option and command-specific `--force` bypasses the prompt on every terminal.
- Apply with neither acknowledgement requires the exact, ordinal, case-sensitive text `RESET` on an interactive terminal.
- Apply with exactly one of `--yes` or `--force` returns exit code `2` before prompting or sending a mutation request. Partial automation is invalid.
- Redirected input, redirected output, `--json`, `--output-format json`, `--print`, EOF, blank input, or incorrect text without both acknowledgements returns exit code `2` and sends no mutation request.
- Ctrl+C before the journal exists returns exit code `130`. Cancellation after journal creation follows the phase-aware result contract below.
- There is no yes/no decline path. This is a documented factory-reset exception to the ordinary mutation-confirmation contract.
- `--force` with `--dry-run` returns exit code `2`. Recursive `--yes` is accepted by dry-run and has no effect.

## Scope ownership

The planner builds a manifest from a versioned `InstallationStateCatalog`, a server-resolved Campaign identity, the canonical data-retention plan, daemon state, and the closed credential catalog. The catalog resolves paths through `ArcanumPaths` and explicit first-party path owners. Adding a new state writer requires a catalog entry and coverage test before global reset may claim complete installation coverage.

| State class | `--workspace` | `--global` | `--all` |
| --- | --- | --- | --- |
| Registered current workspace `.arcanum` metadata | Delete except a backup-only carve-out | Preserve | Delete except a backup-only carve-out |
| Registered current workspace derived rows | Delete | Delete through canonical global reset | Delete |
| Current Campaign registration | Preserve | Delete with the Grimoire | Delete with the Grimoire |
| Workspace source files and repository state | Preserve | Preserve | Preserve |
| Other workspace `.arcanum` directories | Preserve | Preserve | Preserve |
| Other Campaign registrations | Preserve | Delete with the Grimoire | Delete with the Grimoire |
| Global Grimoire rows, blobs, and indexes | Preserve except selected workspace derivatives | Delete | Delete |
| Global configuration, preset state, CLI context, and sidecars | Preserve | Delete | Delete |
| Canonical certificates, logs, trust pages, caches, PID state, and Data Protection keys | Preserve | Delete | Delete |
| Global MCP configuration, Spells, agent instructions, and Arcanum-authored global state | Preserve | Delete | Delete |
| The Forge state stored beneath canonical Arcanum roots | Preserve | Delete | Delete |
| Arcanum OS credentials and protected mirrors | Preserve | Delete | Delete |
| Arcanum daemon registration | Preserve | Delete | Delete |
| Canonical and external backup archives | Preserve | Preserve | Preserve |
| External configured audit logs | Preserve and report as external | Preserve and report as external | Preserve and report as external |
| Out-of-root workspace content and source code | Preserve | Preserve | Preserve |
| Third-party MCP configuration outside Arcanum roots, SuperCompress state, OS-wide application installs, and unrelated applications | Preserve | Preserve | Preserve |
| Environment-variable values | Preserve and report as external | Preserve and report as external | Preserve and report as external |
| External reset-control journal | Retire active journal to a 30-day replay receipt | Retire active journal to a 30-day replay receipt | Retire active journal to a 30-day replay receipt |

`--workspace` and `--all` require the current directory to be contained by a registered Campaign. With a live host, the CLI proves same-host identity and reads the authenticated Campaign catalog. With no host, the shared no-write planner reads the same catalog projection from the existing local Grimoire. The CLI selects the most specific Campaign whose path contains the local current directory and sends only its opaque `WorkspaceId` in reset requests. A participating server resolves the canonical path again. A directory named `.arcanum` does not prove Arcanum ownership, so an unregistered workspace is a `Data.NotFound` blocker and is never deleted.

`--workspace` targets that Campaign's exact `.arcanum` directory and derived rows whose normalized `RootPath`, `WorkspacePath`, or workspace Tapestry `ScopeId` is contained by its canonical path, excluding any row contained by a more-specific nested Campaign. The predicate uses the repository's platform-aware canonical path comparer and rejects malformed or identity-ambiguous stored paths as conflicts. Embeddings and vector mirrors are reached only through the selected chunks or Tapestry nodes. This containment rule cleans existing subdirectory-keyed rows. Producers are changed to key new Campaign-bound workspace derivatives by the selected Campaign's canonical root. The reset preserves the Campaign, Sessions, Prompts, Spells outside `.arcanum`, attachments owned by Sessions, every nested Campaign, and every source file. If valid backup archives were stored inside `.arcanum`, the metadata directory is recreated only as a documented backup-only carve-out after its generated state is removed.

`--global` runs the canonical factory reset and removes the installation's global authoritative state. Campaign registrations are Grimoire rows and are deleted. Every workspace directory and `.arcanum` directory remains untouched.

`--all` is exactly `--global` plus the registered current workspace target captured before the Grimoire is deleted. It does not scan for other workspace directories. Other registered and unregistered workspace metadata is an explicit preserved exclusion because discovery after global deletion would require an unsafe filesystem scan.

## Architecture

### Component ownership

- Core owns typed scope, plan, phase, result, verification, and error contracts, plus the portable cross-process gate, presence protocol, and deterministic control-path resolver needed by every first-party client.
- Infrastructure owns the installation planner, state catalog, in-process reset admission, coordinator, external journal, credential catalog, identity-safe quarantine, host identity, daemon reset lease, and bootstrap-readiness verifier.
- API owns authenticated planning and the live-host phase.
- CLI owns pre-bootstrap routing, parsing, confirmation, daemon preparation, graceful-shutdown handoff, offline continuation, damaged-installation recovery, final output, and exit mapping.
- The existing `IDataRetentionService` remains the sole authority for database rows, blobs, derived indexes, and its durable operation marker.

The CLI registers a narrow, no-create infrastructure slice for planning and offline reset work. It never issues SQL or row-level deletes. The slice may open an existing clean Grimoire snapshot under the no-host restrictions below, and only the restricted recovery host may open it for mutation recovery. A new `DataRetentionOperation.ResetWorkspace` extends `IDataRetentionService` with `TargetId=WorkspaceId`; it uses the existing `data-retention-mutation` recovery descriptor and adds a `reset-workspace` journal subtype. The retention service owns the targeted SQL transaction, watcher drain, `.arcanum` quarantine, rollback before commit, and forward reconciliation after commit.

Installation planning supplies `IDataRetentionService` with an internal, plan-derived `DataRetentionPreservationSet`. It contains only canonical identities of verified backup artifacts discovered beneath data-owned targets. Apply binds the confirmed set to the external journal before staging. Both plan and apply exclude those identities from deletion counts and fingerprints. A staged artifact remains the same logical exclusion after its physical rename, so preservation cannot cause nested plan drift. Callers cannot supply preservation paths.

For a healthy reset, the coordinator preallocates one nested data operation ID, stores it in the external journal, and passes it to a new idempotent retention-start contract before the point of no return. The retention service durably binds that ID to the expected plan, operation subtype, installation operation ID, and optional workspace ID before mutation. The restricted recovery host uses a new exact-operation recovery entry point that validates all of those fields and dispatches only that marker. It does not run the ordinary startup reconciler or scan unrelated expired operations.

### Typed contracts

The following names and fields are the public wire contract. Plan issue arrays aggregate by closed code, data class, and phase, so their cardinality is bounded by the contract. Per-file durable detail lives in the segmented recovery manifest. The final credential array contains one metadata-only result per admitted account because issue #50 requires individual reporting. An unavailable dynamic-provider enumeration adds one catalog-level result and blocks apply before the point of no return rather than claiming that zero accounts exist.

JSON output uses a hand-written `Utf8JsonWriter` composition routine. It writes the fixed `InstallationResetResult` properties with source-generated type metadata, streams each `InstallationResetCredentialResult` from validated manifest segments into the `credentialResults` array, and then closes the single document. It never uses reflection or materializes the complete credential array. Byte-for-byte JSON shape tests compare this writer with the public record contract, including empty, paged, cancelled, and partial results.

- `InstallationResetScope`: `Workspace`, `Global`, `All`.
- `InstallationResetState`: `Planned`, `Prepared`, `ServerApplying`, `ServerCommitted`, `OfflineApplying`, `Completed`, `RolledBack`, `Partial`, `Cancelled`.
- `InstallationResetPhase`: `Planning`, `Admission`, `WorkspaceData`, `BackupPreservation`, `CanonicalData`, `Daemon`, `HostShutdown`, `Filesystem`, `Credentials`, `Verification`.
- `InstallationResetItemStatus`: `Pending`, `Deleted`, `Absent`, `Preserved`, `Unavailable`, `Blocked`, `Failed`, `Cancelled`.
- `InstallationResetCredentialKind`: `MasterApiKey`, `GrimoireEncryptionSecret`, `FileEncryptionMasterKey`, `WebResearchApiKey`, `InferenceProviderApiKey`, `InferenceProviderCatalog`.
- `InstallationResetTargetRole`: `WorkspaceMetadata`, `WorkspaceDerivedData`, `Grimoire`, `Configuration`, `ConfigurationSidecars`, `CliState`, `GlobalMcp`, `GlobalSpells`, `GlobalAgentInstructions`, `Certificates`, `CanonicalLogs`, `TrustedMcpMetadata`, `DataProtectionKeys`, `SecretMirrors`, `BlobFiles`, `Attachments`, `TheForgeState`, `PidState`, `BackupRecoveryArtifacts`, `DaemonRegistration`, `OsCredentials`, `UnclassifiedOwnedState`, `EmptyRootContainer`.

Every enum uses `StringOnlyJsonStringEnumConverter<T>`. Numeric spellings are invalid.

Credential results use only `Pending`, `Deleted`, `Absent`, `Unavailable`, `Failed`, or `Cancelled`. `Preserved` and `Blocked` are invalid for that record. The six summary counters equal the number of emitted entries in their corresponding status, including an `InferenceProviderCatalog` sentinel when enumeration is unavailable. `credentialAccounts` counts admitted OS-backed accounts only, not filesystem-only logical credentials or the catalog sentinel.

`installationOperationId` is allocated with the journal before any preparation, so every post-journal result has a stable ID. `dataOperationId` identifies the one nested `IDataRetentionService` operation and is null before that operation exists or on damaged catalog-leaf recovery. Workspace uses `ResetWorkspace`; global and all use `FactoryReset`.

`dataInventoryAvailable=false` requires plan `dataPlanId`, `rows`, and `derivedRecords`, plus result `dataPlanId`, `dataOperationId`, `rowsDeleted`, and `derivedRecordsDeleted`, to be null. Zero is reserved for a known empty inventory. This represents damaged recovery or a lost data checkpoint without inventing totals. `credentialInventoryAvailable=false` likewise requires `credentialAccounts=null`; a catalog-level `InferenceProviderCatalog` result explains why the provider account set is unknown. Apply cannot pass `Prepared` while credential inventory is unavailable. A damaged global catalog-leaf operation may pass its separately named prepared checkpoint with unavailable data inventory because it performs no nested data operation and cannot claim a completed fresh-install result while blockers remain.

`recoveryManifestPath` and `recoveryManifestDigest` are both non-null only for `Partial` or post-point-of-no-return `Cancelled` results that require operator retry. Completed and fully rolled-back results keep the internal replay receipt private and return both fields as null.

`InstallationResetVerification` has state-specific invariants:

- `Completed` workspace sets `performed=true`, `freshStartApplicable=false`, `bootstrapReady=false`, `setupRequired=false`, `selectedStateAbsent=true`, `originalStateRestored=null`, and both preservation facts to true.
- `Completed` global or all sets `performed=true`, `freshStartApplicable=true`, `bootstrapReady=true`, `setupRequired=true`, `selectedStateAbsent=true`, `originalStateRestored=null`, and both preservation facts to true.
- `RolledBack` and a fully reverted pre-point-of-no-return `Cancelled` result set `performed=true`, `freshStartApplicable=false`, the fresh-start and absence facts to null, `originalStateRestored=true`, both preservation facts to true, and zero failures.
- `Partial` and post-point-of-no-return `Cancelled` set `performed` according to whether terminal verification ran. Facts not proved are null, proved facts retain their value, `originalStateRestored` is false or null, and `failureCount` plus `firstFailureCode` describe verification failures. No null fact can be interpreted as success.

```text
InstallationResetPlanRequest(
  scope: InstallationResetScope required,
  workspaceId: Guid? required for Workspace and All)

InstallationResetTargetSummary(
  role: InstallationResetTargetRole,
  location: string,
  items: long,
  estimatedBytes: long)

InstallationResetExclusion(
  category: string,
  location: string,
  reason: string)

InstallationResetIssueSummary(
  code: string,
  dataClass: RetentionDataClass?,
  phase: InstallationResetPhase,
  count: long,
  firstResourceId: string?,
  message: string)

InstallationResetPlan(
  planId: string,
  dataPlanId: string?,
  stateCatalogVersion: string,
  scope: InstallationResetScope,
  workspaceId: Guid?,
  hostInstanceId: Guid?,
  stateRootFingerprint: string,
  generatedAt: DateTimeOffset,
  targets: InstallationResetTargetSummary[],
  exclusions: InstallationResetExclusion[],
  blockers: InstallationResetIssueSummary[],
  conflicts: InstallationResetIssueSummary[],
  dataInventoryAvailable: bool,
  rows: long?,
  files: long,
  estimatedBytes: long,
  derivedRecords: long?,
  credentialInventoryAvailable: bool,
  credentialAccounts: long?,
  requiresHostShutdown: bool,
  requiresConfirmation: bool)

InstallationResetApplyRequest(
  scope: InstallationResetScope required,
  workspaceId: Guid?,
  expectedPlanId: string required,
  expectedDataPlanId: string required,
  journalId: Guid required,
  hostInstanceId: Guid required,
  confirmation: string required)

InstallationResetHandoff(
  installationOperationId: Guid,
  dataOperationId: Guid,
  journalId: Guid,
  scope: InstallationResetScope,
  planId: string,
  dataPlanId: string,
  state: InstallationResetState,
  phase: InstallationResetPhase,
  rowsDeleted: long,
  filesDeleted: long,
  estimatedBytesDeleted: long,
  derivedRecordsDeleted: long,
  blockers: InstallationResetIssueSummary[],
  conflicts: InstallationResetIssueSummary[],
  reconciled: bool,
  pointOfNoReturnReached: bool,
  hostShutdownRequired: bool,
  offlineContinuationRequired: bool)

InstallationResetServerStatus(
  installationOperationId: Guid,
  journalId: Guid,
  scope: InstallationResetScope,
  planId: string,
  state: InstallationResetState,
  phase: InstallationResetPhase,
  dataOperationId: Guid?,
  handoff: InstallationResetHandoff?,
  retryRequired: bool,
  errorCode: string?)

InstallationResetPhaseResult(
  phase: InstallationResetPhase,
  status: InstallationResetItemStatus,
  affectedItems: long,
  errorCode: string?)

InstallationResetCredentialSummary(
  pending: long,
  deleted: long,
  absent: long,
  unavailable: long,
  failed: long,
  cancelled: long)

InstallationResetCredentialResult(
  kind: InstallationResetCredentialKind,
  account: string?,
  status: InstallationResetItemStatus,
  errorCode: string?)

InstallationResetVerification(
  performed: bool,
  freshStartApplicable: bool,
  bootstrapReady: bool?,
  setupRequired: bool?,
  selectedStateAbsent: bool?,
  originalStateRestored: bool?,
  backupsPreserved: bool?,
  externalStatePreserved: bool?,
  failureCount: long,
  firstFailureCode: string?)

InstallationResetResult(
  installationOperationId: Guid,
  dataOperationId: Guid?,
  journalId: Guid,
  scope: InstallationResetScope,
  planId: string,
  dataPlanId: string?,
  state: InstallationResetState,
  phases: InstallationResetPhaseResult[],
  dataInventoryAvailable: bool,
  rowsDeleted: long?,
  filesDeleted: long,
  estimatedBytesDeleted: long,
  derivedRecordsDeleted: long?,
  credentialInventoryAvailable: bool,
  credentials: InstallationResetCredentialSummary,
  credentialResults: InstallationResetCredentialResult[],
  exclusions: InstallationResetExclusion[],
  verification: InstallationResetVerification,
  recoveryManifestPath: string?,
  recoveryManifestDigest: string?,
  reconciled: bool,
  retryRequired: bool)

ArcanumHostInstanceIdentity(
  instanceId: Guid,
  processId: int,
  processStartedAt: DateTimeOffset,
  executableIdentity: string,
  stateRootFingerprint: string)
```

Every native API payload and closed `ApiResponse<T>` envelope is registered on `ArcanumJsonContext`. Journal serialization uses a dedicated source-generated context. Native API records use camel-case serialization and no `JsonPropertyName` attributes.

### Pre-bootstrap routing

`Program.Main` performs a no-write `InstallationResetPreflight` before `AddArcanumConfiguration`, Data Protection registration, key bootstrap, or Grimoire initialization. The probe checks only the deterministic journal location and existing path metadata. It never creates a directory.

- A handler-free, no-DI surface parser runs first from the same `System.CommandLine` descriptors used by the live command tree. Its `ParseResult` recognizes recursive options before or between command tokens, options with values, `--` termination, the root `help <topic>` command, `-?`, `-h`, `--help`, `/?`, root version, and every published alias including `-p`. A shared shape validator inspects the parsed option occurrences, including repeated selectors, rather than maintaining a second token grammar. Help at any command path and root version bypass execution validation. Otherwise, when the parsed command is `data factory-reset`, the validator requires exactly one scope and one mode plus the command-specific option relationships, without resolving a current directory or touching the filesystem. Invalid shape returns exit code `2` before reset preflight.
- An unfinished reset admits help at every command path, root version, metadata-only `doctor list`, `doctor explain`, and the same recorded scope with `data factory-reset <scope> --apply`. Help, version, list, and explain use the no-create surface path. The default diagnostic run is blocked because opening a WAL database can materialize sidecars even in read-only mode; every doctor repair shape is also blocked. A dry-run, a different scope, or any other executable command returns exit code `2` with the journal ID and exact retry command.
- The exact `data factory-reset` command is admitted when `arcanum.json` is malformed. It uses the no-create reset client stack and canonical default roots. Other `data` commands keep the existing malformed-configuration failure.
- The transient reset host, restricted recovery host, and credential worker are private modes of the shipping `arcanum` executable, outside the public command tree and generated command map. They are admitted before public recovery filtering only when an inherited owner-only handle proves the parent process, random nonce, exact executable identity, and a typed capability. Transient and recovery hosts plus credential probe/delete require the active journal capability. Dry-run credential enumeration uses a short-lived read-only planning capability bound to the parent, requested scope, exact `ListAccounts` operation, and response budget; it cannot authorize probe or delete and creates no journal. Public arguments or environment variables alone cannot enter a private mode. Every private mode starts a purpose-built service graph and exits when its parent-owned channel closes.
- Configuration loading no longer creates the global root. Data Protection uses a path descriptor and creates its key directory only when a normal write is authorized.
- `run` evaluates the shared `FirstRunStateDetector` after command-shape validation and before reading a required prompt, initializing Grimoire, creating keys, or launching a host.
- `serve` refuses normal startup while an unfinished journal exists. A global or all-scope retry can proceed offline after proving that the host and daemon are absent. The same workspace retry may start a restricted recovery host that loads only configuration, keys, Grimoire, reset admission, and the registered `data-retention-mutation` recovery handler. It exposes only authenticated loopback reset status/apply and shutdown endpoints, starts no producer or watcher, and exits after reconciliation.

### Reset admission

One `InstallationResetAdmission` contract prevents selected state from being recreated during apply.

- The host registry names every in-process producer: inference, uploads and batches, attachment publication, workspace indexing, backup and restore, retention, configuration and preset mutation, credential rotation, MCP lifecycle, A2A work, Apprentices and Familiars, audit and managed logs, and child-process ownership. Apply closes admission, waits for registered leases to drain, and reports non-drainable work as a typed blocker.
- A current-user cross-process gate covers configuration writers, CLI context/session/recent stores, Compendium, and The Forge. Its protocol, deterministic path resolver, process-presence record, and portable BCL lease implementation live in Arcanum Core, which every first-party client may already reference. Infrastructure owns reset orchestration. Short-lived writers hold a shared lease from state read through commit. For global and all scope, long-lived Compendium and The Forge processes hold an identity-bound presence lease for their lifetime, observe reset intent, stop watchers, discard selected unsaved state, and exit. The lease holds an OS lock and binds a random nonce, PID, process start time, and executable identity; an unlocked stale record may be retired, and reset never signals a PID. Reset cannot pass global or all admission while another first-party presence lease remains. Workspace scope drains only target-bound host work and does not terminate clients that write preserved global state. This prevents a stale process from recreating deleted files after the journal is retired. New selected-state writers observe the intent and fail with recovery guidance. The exclusive lease passes from the host to the CLI across shutdown while the journal keeps the intent durable.
- Contract tests enumerate every first-party mutator and fail when a new writer lacks an admission registration. External configured audit writers are drained but their out-of-root files remain preserved.
- Acquisition order is the fixed control-root single-flight lock, atomic active-journal ownership and reset intent, durable daemon-posture checkpoint, daemon restart inhibition, cross-process exclusive admission after all other presence leases exit, in-process admission, configuration transaction, managed-log gate, daemon-start gate, and storage transaction. The coordinator holds the single-flight lock through terminal receipt publication and final verification, then releases in reverse order. A contender cannot publish `active.json` or observe a receipt as terminal while that lock is held.

The host must acquire `ArcanumMaintenanceLock` at startup. A second host or active offline reset is a startup failure. The previous best-effort warning is removed because reset and restore cannot safely reason about a host that ignores the lock.

Global and all scopes use a two-phase `DaemonResetLease`. Before the point of no return it verifies service-manager access, records the exact registration identity and prior enabled posture in the flushed journal, disables automatic restart reversibly, and proves that the service manager accepted the inhibitor in a second checkpoint. Failure leaves selected data unchanged. After the host exits, the same identity-bound lease removes the registration and verifies absence. Recovery before the point of no return may restore the captured posture. Recovery after the point of no return may only finish removal. The reset never disables, enables, or removes a registration whose identity changed.

## Planning

Planning is read-only and deterministic for the observed installation state.

Plan and dry-run always return the typed plan with `200`, even when inventory is unavailable. The condition appears in its availability flag and blocker array; dry-run itself completed and uses exit `0`. Apply refuses an unavailable credential inventory or scope-required data inventory. Before journal creation, the CLI emits `CliErrorPayload` with `Data.CredentialInventoryUnavailable` or `Data.InventoryUnavailable` and exits `1`. If admission revalidation discovers the condition after journal creation, reversible preparation is restored and one `InstallationResetResult` with state `RolledBack` and the matching phase error is emitted with exit `1`. Damaged global catalog-leaf apply is the sole data-inventory exception and carries its explicit partial-state contract instead of returning the 409 rejection.

1. Validate the explicit scope. For workspace or all, use the authenticated Campaign catalog from a verified live host. When no host exists, the shared local planner may read the same Campaign projection only from a stable clean main database with no WAL or SHM files. It revalidates database identity, opens with read-only immutable semantics, and performs no copy, recovery, checkpoint, migration, or sidecar creation. A WAL or SHM file, identity drift, or an incomplete immutable snapshot makes data and Campaign inventory unavailable. Dry-run reports that blocker without writing. Workspace and all apply cannot establish ownership and stop before journal creation; global apply may use typed damaged recovery only under the restrictions below. Select the most specific containing Campaign locally and send only its ID when a host participates; the server resolves its path again. The target owns derived paths contained by that Campaign root except paths contained by a more-specific nested Campaign.
2. Resolve the versioned closed path roles. Canonicalize, deduplicate, and topologically order equal, parent, and child roots. Unix commonly has one coincident root; Windows may have distinct Grimoire and secret-store roots.
3. Stream filesystem identities in canonical lexical order into the fingerprint and aggregate one bounded summary per target role. Detect valid backup archives, hash them before any rename, and build the identity-bound preservation set. A bounded-memory, no-follow prefix-partition walker may rescan a large directory instead of materializing or sorting it in memory. Dry-run writes no manifest, journal, temporary file, credential, or migration.
4. Obtain the canonical retention plan for `ResetWorkspace` or `FactoryReset`. The read-only planning core is shared by `IDataRetentionService`, the API route, and the CLI's no-host planner. The no-host adapter follows the clean immutable-snapshot rule above and uses a no-promotion secret probe. It cannot create, migrate, recover, checkpoint, copy, or delete anything. The nested plan uses the preservation set from step 3, so verified backup artifacts are exclusions rather than deletion targets.
5. Read daemon registration metadata and authenticated host identity. `IResetAuthenticationKeyReader` reads the master API key from the OS account or protected fallback without promoting, rewriting, or deleting either source. Its immutable value is scoped to one disposable HTTP request, is never copied into a plan or journal, and remains covered by the existing header redaction and log filters.
6. Enumerate exact-service credential account names and mirror filenames through metadata-only APIs. These APIs never return secret values, unlock or promote a mirror, update a digest cache, or create a key ring.
7. List fixed exclusion categories and external references explicitly.
8. Compute a stable SHA-256 plan ID over the catalog version, scope, resolved workspace, nested data plan, root identity, target stream, credential-account stream, captured daemon registration identity and prior posture, and exclusions. Ephemeral host presence, host instance identity, and this journal's prepared daemon-inhibitor state are excluded from the deletion-plan fingerprint; apply validates them independently. Final comparison projects a matching prepared lease back to its captured prior posture, while any registration identity drift fails closed.

The host publishes a random instance ID at startup. An owner-only JSON PID record binds that ID to PID, process start time, executable identity, and state-root fingerprint. Authenticated `GET /api/server/identity` returns the same values. The CLI requires a loopback peer, byte-for-byte record agreement, a matching live process start time and executable identity, and a held maintenance lock. It never kills a PID. Missing proof blocks live handoff and local fallback until no process holds the lock and the daemon is absent.

Apply first obtains confirmation for the dry-run plan. It atomically acquires the active-reset pointer, creates and flushes the journal plus reset intent, records prior daemon posture, prepares and checkpoints the reversible daemon restart inhibitor, then asks the host to enter reset admission. The host rebuilds both plan fingerprints while all selected writers are blocked. Any drift restores the daemon state, releases admission, retires an otherwise empty journal and active pointer, and returns `Data.PlanChanged` before the point of no return. Apply never confirms one plan and mutates another.

The CLI first requests an authenticated server plan when a verified local host exists. With no host or daemon, it uses the shared local read-only planner. A running host with failed authentication or an unverifiable process blocks local fallback. A local damaged-installation plan sets `dataInventoryAvailable=false`, `dataPlanId=null`, and reports only verified filesystem and credential totals. It never guesses row counts or workspace ownership.

After confirmation and journal creation, a healthy installation with no running host starts an explicit transient reset host on a private loopback coordinator address. The CLI preallocates its instance ID, passes only the journal capability through the inherited private-mode channel, waits for identity agreement, and shuts down that CLI-owned process after the workspace result or global handoff. Its purpose-built graph disables canonical file logging and ordinary `GrimoireDatabaseHostedService` startup. Until admission and final plan validation succeed, it uses the same immutable no-sidecar planning adapter and does not open the database writeable, reserve an LRO, create WAL or SHM files, run schema bootstrap, or start a producer. Only then does it enter the journal-bound retention-start path. A pre-existing normal host remains running after workspace reset. This explicit reset startup is allowed in headless apply even when ordinary auto-serve is disabled. It never contacts or mutates a configured remote host. Connection-establishment failures use exit `3`; executable, permission, local identity, and other process-start failures use exit `1`. Damaged catalog-leaf fallback is available only after typed probes prove damaged configuration, key, or Grimoire state and its narrower safety requirements are met.

No arbitrary total file, row, credential, or runtime limit is introduced. Individual provider allocations, pages, filesystem retry windows, manifest segments, control documents, and cleanup waits remain bounded and checkpointed.

## Apply state machine

### Shared opening phases

1. Build the initial read-only plan and obtain typed or automated confirmation for its plan ID.
2. Create the owner-only journal through the create-new active pointer and flush its installation operation ID, reset intent, exact targets, prior daemon posture, and deterministic preservation destinations. Global and all scopes then verify permission to control the daemon and take a reversible restart-inhibition lease without stopping the serving process.
3. The host validates journal and host identities, closes cross-process and in-process admission, drains existing leases, and revalidates filesystem and workspace identities.
4. While admission is held, rebuild the filesystem stream and backup preservation set first, then the nested data plan and complete installation plan. Rehash every admitted archive and revalidate its source identity during this stream. Any drift ends the operation before the point of no return and restores every reversible preparation.
5. Write and flush a preservation-intent segment containing each confirmed digest, source identity, deterministic same-volume destination, and no-clobber posture. The nested retention plan and later apply treat those journal-bound identities as preserved exclusions. Only then stage each artifact by same-volume rename. Reopen it by staged identity and checkpoint a matching streamed digest after each transition. External backup destinations are recorded as exclusions and never opened for mutation.
6. For a healthy path, allocate the nested data operation ID, append and fsync that ID plus its expected subtype, installation operation ID, and optional workspace ID to the external journal, then durably reserve the same binding through the retention service. Checkpoint the reservation back to the external journal before apply. The reservation is reversible operation metadata, not a deletion commit; pre-point-of-no-return rollback retires it idempotently. Write the remaining final manifest segments, preallocate the bounded header/checkpoint replacement budget, flush the complete chain, and verify it from disk before a durable `Prepared` checkpoint. Insufficient control-volume space fails before mutation. The next storage commit is the point of no return. A failure before that commit restores every reversible preparation and produces terminal state `RolledBack`; a rollback failure produces `Partial`.

### Workspace scope

The live host completes workspace reset through `DataRetentionOperation.ResetWorkspace`:

1. Resolve `WorkspaceId` through the Campaign repository and acquire a drainable indexing lease for its canonical path.
2. Stop and unregister the watcher, cancel pending changes, and await the per-workspace reconciliation gate. New indexing for that path remains blocked.
3. Open the preallocated `data-retention-mutation` operation with subtype `reset-workspace`, validate its installation and workspace bindings, and mark it applying before mutation.
4. Quarantine the exact `.arcanum` directory inside the retention operation, then delete only the targeted `WorkspaceContexts`, chunks, embeddings, vector mirrors, and workspace-scoped Tapestry graph in one database transaction.
5. A failure before transaction commit restores the unchanged quarantine. Transaction commit is the point of no return. Any later failure keeps the quarantine and rolls forward through the existing recovery handler.
6. Reconcile row absence and finalize the quarantine. After the closed retention call returns, the installation coordinator restores any pre-staged backup archives into the clean `.arcanum/backups` carve-out, verifies Campaign and source-file preservation, and returns the completed handoff.
7. A post-commit backup-restore failure remains journaled and rolls forward through the restricted recovery host. Release reset admission without re-registering the watcher. Explicit reindexing or the next inference turn may create a new clean index later.

A pre-existing normal host remains running. The API returns the final workspace result without requesting installation shutdown. A CLI-owned transient or restricted recovery host exits after its final result is durable and acknowledged. No installation coordinator attempts to interleave work inside a closed `IDataRetentionService.ApplyAsync` call.

### Global and all scopes

The live host and the still-running CLI use an explicit handoff:

1. Open and run the preallocated canonical factory-reset operation while database keys, authentication, and open services are available. It is the sole nested data operation for both global and all scope. Its first commit is the point of no return.
2. For all scope, the factory reset already deletes all global workspace-derived rows. The external journal retains the pre-bound current Campaign path only so the offline phase can remove that exact `.arcanum`; it does not run a second retention operation or count the same rows twice.
3. Persist `ServerCommitted` in the external journal, including the durable data operation ID and totals, before returning the handoff. Repeated apply calls with the same journal ID return the recorded handoff.
4. The CLI obtains the handoff from the apply response or the authenticated status endpoint, revalidates host identity, then uses the existing authenticated `POST /api/server/quit` contract for graceful shutdown. It never signals an arbitrary process.
5. The daemon reset lease prevents restart. After the verified host exits, the CLI completes service-registration removal and records its absence.
6. The CLI waits for PID cleanup and maintenance-lock release, then acquires and holds the lock through the complete offline phase. The reset intent prevents a new first-party writer from entering during the handle-transfer gap.
7. The CLI refuses to signal or kill a PID. It proceeds only after the bound host instance is gone and both admission and maintenance ownership are proven.
8. The offline coordinator quarantines the ordered owned-root entries and, for all scope, the exact pre-bound workspace metadata target. It deletes ciphertext before its keys, deletes the master API credential last, finalizes quarantines, restores preserved backups without clobbering, and runs coordinator-owned prepublication verification while every other first-party presence lease remains absent. It then performs the receipt transition and final publication check described below before releasing admission.

Authentication and the master credential remain available until the server result has been received and shutdown has been requested. Credential purge occurs only after the host is gone and the coordinator holds the maintenance lock exclusively.

The point of no return begins at the first server-side data commit. Recovery never restores pre-reset configuration or ciphertext after that checkpoint, even when credential deletion has not started. Every subsequent phase rolls forward.

### Damaged-installation path

If corrupt configuration, keys, or database state prevents host startup, the CLI may continue through the same journaled coordinator after proving that no Arcanum host or daemon owns the installation.

Global scope may remove only catalog leaves whose ownership is independently proven by an exact file marker, schema, or identity recorded in an existing valid journal. It never recursively removes a directory merely because its parent has a known role. Because a corrupt database cannot disprove a legacy Campaign nested inside `files`, `attachments`, `spells`, or another normally owned directory, every unmarked recursive subtree becomes preserved `UnclassifiedOwnedState` and blocks a clean-install result. All scope may continue only when an existing journal already binds the current workspace identity; otherwise the operator must use global scope because an unreadable Grimoire cannot prove workspace ownership. Workspace scope remains partial until canonical targeted reconciliation can open the database.

The damaged path uses the same backup staging, root topology, admission intent, credential purge, and journal. A damaged reset may start only from an existing owner-valid control root or active journal established while Campaign ownership was readable. It never creates a new control root when Campaign overlap cannot be disproved. It treats the first proven catalog-leaf quarantine as the point of no return. It does not implement table-by-table deletion, recursively delete an unproven directory, delete an entire root container, adopt an unknown entry, or broaden the manifest. A running or unverifiable host blocks the fallback. These restrictions can yield a safe partial result that requires repair and retry instead of claiming a fresh installation.

CLI bootstrap admits the exact `data factory-reset` command in degraded-configuration mode, alongside existing repair and diagnosis commands. Other data commands remain blocked by malformed configuration. The degraded planner uses only canonical paths it can prove and reports unavailable data counts or unresolved external references without guessing targets.

## Filesystem safety

- `InstallationStateCatalog` version 1 owns the target roles listed in the typed contract. It resolves explicit files and directories from `ArcanumPaths` plus their established owners, including configuration and preset files, database sidecars, global MCP and Spells, agent instructions, CLI state, trust metadata, PID state, Data Protection keys, protected mirrors, canonical logs, certificates, attachments, uploaded files, The Forge files, and backup-recovery artifacts.
- Canonical Grimoire and secret-store root containers are Arcanum-owned except for preserved backups and any legacy overlapping Campaign discovered in the healthy Grimoire. Healthy planning rejects global or all scope with `Data.WorkspaceOverlap` if a registered Campaign root equals, contains, or is contained by a selected canonical root. Registration and update validation reject such overlap going forward. After that proof, an otherwise unknown no-follow entry under the roots is classified as `UnclassifiedOwnedState`, shown in dry-run totals, and deleted. Damaged recovery cannot make the Campaign-overlap proof, so it preserves unknown root entries and every unproven descendant of a recursive role as blockers. Additions outside those roots require a new explicit catalog role.
- Equal and nested roots are deduplicated. Children are quarantined before parents. A preserved subtree prevents deletion of its container, so successful reset may leave an owner-only root containing only `backups`.
- Workspace filesystem targets are restricted to the server-resolved registered Campaign's exact `.arcanum` directory. Derived-row selection uses normalized containment with more-specific nested Campaign exclusion. Journals never live beside a workspace.
- Recursive deletion never accepts a profile directory, workspace root, unresolved variable, glob, or user-supplied arbitrary path.
- Planning records file identity. Apply reopens and revalidates the object before quarantine and finalization.
- Symlinks, junctions, mount changes, directory replacement, ownership changes, and identity drift fail closed.
- Quarantine requires a verified same-volume atomic rename and durable directory-entry semantics. Capability is probed before the point of no return for every selected filesystem. Unsupported SMB, NFS, or platform semantics return `Data.UnsupportedFileSystem` and leave selected state unchanged. There is no copy-then-delete fallback.
- The canonical `backups` subtree is preserved wholesale. While streaming every selected root for the deletion manifest, the planner recognizes any other backup archive by the bounded `BackupArchiveFormat` magic and header, independent of filename or extension. It computes the streamed SHA-256 digest before rename, revalidates source identity, and derives `backups/preserved-<content-digest>-<source-entry-digest>.arcbackup`. The source-entry digest covers the canonical parent-relative entry path and the captured file identity, so byte-identical archives and hard-linked aliases at different entries remain distinct without relying on enumeration order. Each directory entry is preserved because the contract preserves archive placement multiplicity. Before deleting a containing entry, it stages each archive entry to a journal-indexed preservation root on the same selected volume. Distinct Windows roots therefore use distinct staging roots. Finalization restores the archive without clobbering. This applies inside global roots and a selected workspace `.arcanum`; the metadata directory may remain only as a documented `backups` carve-out. External destinations are reported without recursive traversal and never adopted, copied, renamed, or deleted.
- Every preservation intent, including the final destination and pre-rename digest, is flushed before same-volume rename. The staged file is then checkpointed with post-rename identity and a matching streamed digest. A staging or restore preflight failure aborts before the point of no return.
- Planning does not recursively enumerate source trees or external exclusions. For workspace scope it captures the identity of the workspace root, the exact `.arcanum` child, and the server-resolved containment relationship. Handle-relative, no-follow operations plus revalidation prove that no sibling source entry was opened for mutation.
- The reset owns a filesystem retry classifier for sharing violations, transient busy errors, and antivirus races. It makes five attempts with delays of 0, 50, 200, 800, and 2,000 milliseconds through `TimeProvider`; permission, identity, link, and not-a-directory failures are never retried. Cancellation is observed between attempts. Exhaustion records `Data.FileLocked`, retains the journal, and returns a partial result.

## Credential purge

Bulk deletion follows the closed credential inventory from `Arcanum.DESIGN.md` and issue #47:

- Master API key account and protected mirror.
- File-encryption master-key account and protected mirror.
- Perplexity research credential account and protected mirror.
- Dynamic inference-provider accounts that match the exact Arcanum service plus the canonical provider account prefix and suffix, and their validated protected mirrors.
- Future credentials only after they are added to the closed catalog and its coverage tests.

The Grimoire encryption secret is the Data Protection file `grimoire-key.dat`, not an OS credential account. The filesystem catalog removes it only after the database and KDF sidecar are quarantined. The Data Protection key ring is likewise a filesystem role and is deleted after every protected mirror.

The closed catalog is the exact service namespace `arcanum` plus two admitted account sets: the fixed account constants and `ArcanumCredentialIdentity.IsInferenceProviderApiKeyAccount`. `IOsCredentialStore` gains metadata-only `ListAccounts(service)` and `Probe(service, account)` operations. The caller cannot supply a service or account to reset. The implementation always supplies the constant service, filters before recording or deleting, and rejects every account outside the two sets.

- Windows uses `CredEnumerateW("arcanum/*")` and reads target names and usernames only.
- macOS uses an attribute-only generic-password query constrained to `kSecAttrService=arcanum`; it never requests password data.
- Linux uses the existing libsecret schema with the exact `service=arcanum` attribute and materializes account attributes only.
- Locked, unavailable, and failed backends remain distinct statuses. Native enumeration, probe, and delete calls run in the private `CredentialResetWorker` mode through the existing capped child-process and OS resource-limiter infrastructure. The worker service graph contains no HTTP stack or network client, accepts only the compiled service constant plus a catalog-valid account, scrubs inherited proxy, cloud, token, and secret-bearing environment variables and handles, and returns no secret value. Existing process infrastructure does not claim OS-level network isolation, so this design does not rely on that property. Any future worker dependency that can initiate network traffic requires a separate fail-closed confinement design. Parent-side deadlines are ten seconds for enumeration and five seconds per probe or delete, frames are at most 64 KiB, and a 64 MiB worker memory ceiling bounds the platform's one-shot exact-service allocation. Timeout, memory exhaustion, malformed output, or a hung backend terminates only the verified worker and records `Unavailable`; it never broadens deletion. Account frames are filtered and persisted incrementally, so there is no arbitrary successful account-count cap when the platform call fits the worker ceiling.

This formally extends the authoritative closed-catalog rule. It still forbids global credential enumeration and deletion based on an arbitrary prefix outside the exact Arcanum service. Platform delete revalidates the exact service and account predicate immediately before mutation.

Credential identities are snapshotted before deleting configuration. Secret values are never written to the plan, journal, result, diagnostics, logs, or metrics.

Each result entry represents one logical credential. `account` is populated only for an OS-backed fixed or provider account. A logical credential reports `Deleted` only when every selected OS account, protected mirror, and related key file that can authorize or decrypt it was present and is now absent. It reports `Absent` only when every authority was already absent. Mixed, locked, or unverifiable authorities report `Unavailable` or `Failed`, never a misleading aggregate success. The Grimoire encryption secret has no OS account and therefore returns `account=null`. If provider enumeration is unavailable, an `InferenceProviderCatalog` entry with `account=null` reports `Unavailable`, `credentialInventoryAvailable=false`, and apply stops before the point of no return. Human output lists every emitted credential result after its aggregate summary.

Planning and verification use only account metadata and filesystem metadata. They never call the current secret read paths that promote Data Protection mirrors into OS storage. Each credential delete is idempotent and followed by the metadata-only presence check. Locked, unavailable, corrupt, or failed stores produce a named partial result. Disk mirrors and key rings are retained until their authoritative ciphertext is quarantined. Provider and research credentials are deleted next, file and Grimoire keys follow their ciphertext, and the master API key is deleted last.

Environment-referenced HTTPS passwords, Comm Link URLs, and provider values remain external process state. The plan and result disclose that Arcanum removed its references but could not erase those environment values.

## Journal and recovery

Every scope uses one deterministic owner-only control root resolved from all canonical global roots, including equal and nested topologies. `InstallationControlPathResolver` sorts the outermost roots, evaluates an Arcanum-named sibling of each, and selects the first canonical candidate that is outside every global root and selected deletion target. A pre-existing candidate must have the exact owner-only control marker or planning fails closed. Healthy planning also blocks a legacy Campaign that overlaps the candidate, and Campaign registration rejects it going forward. Successful setup and healthy host startup establish the marker after Campaign overlap validation, so later damaged recovery can trust it. Healthy apply may establish it after the same validation. Damaged recovery requires that pre-existing trusted marker or an existing active journal and never creates a candidate while Campaign ownership is unreadable. If no safe candidate exists, reset returns `Data.ControlPathUnavailable`. The cross-process gate, active pointer, journals, and completion receipts live there. Discovery performs the same no-create calculation before configuration, Data Protection, key, or database bootstrap.

Control-root creation is crash-atomic. The creator makes an owner-only staging directory beside the final candidate, writes and fsyncs the complete marker, fsyncs the staging directory and parent, then publishes it with a platform atomic no-replace directory rename and fsyncs the parent again. A competing valid final marker wins; an unmarked final entry fails closed. An unpublished staging directory carries no active pointer and may be identity-safely retired. If the volume cannot provide the required primitive, reset returns `Data.UnsupportedFileSystem` before mutation.

Exactly one unfinished operation exists per user installation. Before creating a contender journal, a process takes the fixed owner-only single-flight lock with OS release-on-process-death semantics. It holds that lock through active-pointer publication, the entire operation, terminal receipt transition, final publication verification, and admission release. A contender writes and flushes a complete minimal journal header in its GUID directory, then publishes a fixed `active.json` pointer with an atomic no-overwrite rename. Only the lock owner may publish reset intent or prepare the daemon. A loser removes its untouched directory. An owner-valid orphan created before pointer publication contains no mutation checkpoint and is safe to retire; anything else blocks for diagnosis. The active pointer is removed only after rollback or completion is durably recorded. Readers treat a newly published receipt as provisional while the single-flight lock is held and neither replay it nor admit normal bootstrap. Per-root quarantine and preserved-backup staging directories use catalog-derived, GUID-suffixed same-volume sibling names and are indexed by the active journal. No caller supplies any control, journal, staging, or quarantine path.

The journal is segmented so complete work never requires one unbounded control document:

- A fixed header of at most 64 KiB stores format version, journal ID, installation operation ID, optional nested data operation ID and binding, scope, opaque workspace ID, plan IDs, aggregate totals, current phase, point-of-no-return flag, host and daemon identities, head and tail segment IDs, segment count, and chain digest.
- Chained manifest segments are each at most 1 MiB. They contain exact target identities, preservation records, admitted credential account names, and per-item completion status. Each segment stores the prior digest; no unbounded index is required.
- Paths come only from the state catalog or the server-resolved Campaign. Credential values are never stored.
- Segment append is ordered and durable. The writer emits an immutable segment to a new temporary file, fsyncs it, atomically renames it to its final ID, and fsyncs the journal directory. It then writes and fsyncs a replacement header, atomically replaces the old header, and fsyncs the directory again. A published segment that is not referenced by the current header is ignored and may be identity-safely retired. A header that references a missing, owner-invalid, or digest-invalid segment fails closed with `Data.JournalMismatch`. Header and segment JSON use dedicated source-generated metadata.

Recovery resumes the recorded phase idempotently. It cannot adopt new paths or expand scope.

Before the point of no return, recovery may restore daemon posture, canonical backups, and quarantined state if continuation is impossible and every identity remains unchanged. At or after the first data commit or damaged-root quarantine, recovery moves forward only. Restoring pre-reset state after a committed deletion could create a mixed installation.

`POST /api/data/factory-reset` is idempotent by active journal ID on the original bound host. `GET /api/data/factory-reset/{journalId}` returns the current active server status while that host exists. If the server commits and the response is lost before external handoff publication, the CLI polls status and safely receives the same installation operation ID, data operation ID, and totals after the durable `ServerCommitted` checkpoint. A crash after database commit but before that checkpoint is reconciled against the preallocated exact data-retention operation marker. For global and all scopes, an unavailable Grimoire still permits safe roll-forward from `ServerApplying`, because explicit catalog deletion is the selected terminal state. Workspace retry starts the restricted recovery host and invokes only the journal-bound operation ID after validating subtype and workspace identity. Completed receipt replay is local to the CLI and does not depend on a replacement host accepting the original host identity.

Cancellation is phase-aware:

- Before journal creation, cancellation emits the normal CLI cancellation error and exit `130`.
- After journal creation but before the point of no return, apply restores reversible preparation. After rollback verification it converts the journal to a terminal `Cancelled` receipt and emits one `InstallationResetResult` with state `Cancelled`.
- After the point of no return, cancellation stops admission of new optional work, completes the current identity-safe transition and durable checkpoint, then emits one partial `InstallationResetResult` with state `Cancelled`, `retryRequired=true`, and the recovery manifest path. Exit remains `130` and never implies zero mutation.
- Process termination can prevent output. The journal remains the recovery authority.

The API never removes the external journal. Completion flushes the terminal segments, then atomically renames `active.json` to the GUID-named receipt pointer on the same control volume with `publicationVerified=false`. That provisional receipt remains blocking and replayable even if the coordinator dies and its single-flight lock is released. Under the still-held admission and single-flight locks, the coordinator runs the publication verification described below and then atomically replaces the pointer with `publicationVerified=true` and fsyncs the control directory. This durable bit is the sole point at which a successful receipt becomes nonblocking. The CLI then streams its result and per-credential entries, flushes stdout, and best-effort marks the receipt as reported. A kill before or during output leaves the same receipt replayable; a kill after a flushed output may cause at-least-once duplicate output on retry. Exact once across process death is not claimed. Owner-valid, publication-verified `Completed`, `RolledBack`, and fully reverted `Cancelled` receipts with `retryRequired=false` never block setup or normal startup. A provisional receipt, `Partial` receipt, or any receipt with `retryRequired=true` blocks exactly like an active pointer and is never garbage-collected. Receipts contain no secrets and are replaced by a later reset of the same fresh-state fingerprint. A write-authorized normal command may garbage-collect a reported nonblocking receipt after 30 days; dry-run, planning, verification, help, and first-run detection never do so. Receipts are lifecycle control metadata, not authoritative installation state.

A partial result ordinarily leaves the active journal and provides a stable retry instruction. Startup preflight blocks normal credential or Grimoire bootstrap while an active reset remains incomplete. If a crash leaves a provisional receipt or a failed publication check leaves a blocking `Partial` receipt, same-scope `--apply` takes the single-flight lock, validates the complete chain and recorded fingerprint, and atomically renames that receipt back to `active.json` with no-replace semantics. A provisional terminal receipt reruns only its recorded publication verification. A `Partial` receipt resumes its recorded phase. Any mismatch fails closed without creating another journal. Unreported publication-verified `Completed`, `RolledBack`, and fully reverted `Cancelled` receipts replay their exact terminal result locally; they do not reacquire active ownership. A later reset starts only after normal planning when no blocking record exists.

## Fresh-start verification

Every scope requires the read-only verifier to prove selected target absence, preserved exclusions, and zero verification failures. For workspace scope, selected metadata absence permits only the verified backup-only `.arcanum/backups` carve-out. Workspace success additionally proves that its Campaign, Sessions, attachments, source-root identity, and every other workspace remain present, and that the selected watcher remains unregistered until explicit reindexing or a later inference-triggered index action. A workspace result sets `freshStartApplicable=false`, `bootstrapReady=false`, and `setupRequired=false`; it neither stops a pre-existing host nor routes the installation to setup.

Global and all-scope success sets `freshStartApplicable=true` and additionally requires the verifier to prove:

- Every selected catalog entry is absent. An owner-only root container may remain only when its sole content is the preserved canonical `backups` subtree.
- Every selected OS credential reports `Absent` through metadata-only probing.
- No selected protected mirror or key-ring file survives.
- The daemon registration is absent for global and all scopes.
- No stale Arcanum PID record or live verified process survives. The verifier runs while the CLI owns the maintenance lock and proves that no competing owner exists.
- No Compendium, The Forge, CLI writer, or other first-party presence lease survives. A process that did not acknowledge reset intent blocks before the point of no return.
- No database/KDF/secret mismatch or blob/key mismatch can remain.
- No selected configuration, preset journal, CLI context, MCP trust page, generated certificate, canonical log, cache, or derived index survives.
- Preserved canonical backups match the staged identity and streamed digest at their intended location. External backups were never opened for mutation.
- Workspace root identity and exact target containment remain unchanged through the reset. Source trees are not recursively enumerated, and no non-target child is opened for mutation.
- No active reset pointer, unfinished journal, staging transition, or reset quarantine remains. A bounded nonblocking terminal receipt may remain under the external control root and is ignored by the first-run predicate; partial or retry-required receipts still block.
- The first-run setup detector reports that guided setup is required.
- After argument-shape validation, the next interactive `arcanum run` evaluates first-run state before prompt-required validation, Grimoire initialization, or inference startup. It enters the existing guided setup workflow even when no prompt was supplied. If setup succeeds and an original prompt exists, the run may continue. With no original prompt, setup exits successfully and prints the standard next-command guidance. Noninteractive or JSON `run` returns exit code `2`, points interactive users to `arcanum setup`, explains that automation uses `arcanum setup --apply` with every required input, and never opens a prompt.
- After setup, normal startup creates fresh keys and schema through the standard bootstrappers.

Verification has two publication stages. First, an internal coordinator mode proves the terminal state's required invariants while ignoring only its own exact active pointer; the public first-run detector remains blocked. The coordinator flushes a complete terminal result and atomically renames `active.json` to the blocking receipt pointer with `publicationVerified=false`. It then reruns the state-specific no-write predicate in coordinator mode, ignoring only that exact provisional receipt. `Completed` global or all proves first-run setup, `Completed` workspace proves targeted absence with `setupRequired=false`, and `RolledBack` or fully reverted `Cancelled` proves the captured original fingerprint and reversible daemon posture. A failure keeps or restores blocking state and records `Partial`. A crash in this window leaves the provisional receipt blocking for recovery. On success, the coordinator atomically sets `publicationVerified=true`, fsyncs the control directory, and verifies the pointer readback before releasing admission. Only that durable transition publishes success to normal bootstrap or stdout.

The verifier reports `bootstrapReady`; it does not promise that future permissions, credential backends, providers, or environment overrides will remain usable. It does not create replacement keys, configuration, or a database. It evaluates the same first-run predicate used by `arcanum run`, so verification and subsequent routing cannot drift. An end-to-end first-launch test separately proves that setup followed by normal startup creates a new master key, file key, Grimoire secret, schema, and usable authenticated host while the old key is rejected.

## API contract

### Host identity

`GET /api/server/identity` returns `200 + ApiResponse<ArcanumHostInstanceIdentity>` after normal API-key authentication. It is loopback-only because its purpose is process handoff. The response is compared with the owner-only PID record and local process identity before reset planning.

### Plan

`POST /api/data/factory-reset/plan` accepts `InstallationResetPlanRequest` and returns `200 + ApiResponse<InstallationResetPlan>`. `workspaceId` is required for `Workspace` and `All`, forbidden for `Global`, and resolved server-side. No request contains a filesystem path.

Installation-reset planning, status, and apply are local-machine operations. They require an authenticated loopback request. Apply also requires the opaque journal ID created by the CLI to resolve under the deterministic journal store, pass owner and digest validation, contain the prepared daemon lease when required, and match the host instance plus expected plans. A request can never supply a journal path. A non-loopback request returns `403 Data.LocalCoordinatorRequired` without creating a plan or journal. Listening on non-loopback interfaces does not relax this rule.

Normal API-key authentication remains mandatory. Missing or incorrect authentication returns the existing `401` response before reset-specific validation.

### Apply

`POST /api/data/factory-reset` accepts `InstallationResetApplyRequest`. `confirmation` must equal ordinal, case-sensitive `RESET`. Workspace success returns `200 + ApiResponse<InstallationResetHandoff>` with no offline continuation. Global and all success return `202 + ApiResponse<InstallationResetHandoff>` after the server phase is durable and before host shutdown.

For workspace scope, the handoff reports that no offline continuation or host shutdown is required and contains the completed, verified server outcome. For global and all scopes, it reports the durable server outcome and requires the authenticated CLI to perform shutdown and offline continuation. The CLI combines that handoff with offline phase outcomes into one `InstallationResetResult`.

A successful workspace handoff has `state=Completed`, `phase=Verification`, `reconciled=true`, `pointOfNoReturnReached=true`, `hostShutdownRequired=false`, and `offlineContinuationRequired=false`. A successful global or all handoff has `state=ServerCommitted`, `phase=CanonicalData`, `reconciled=true`, `pointOfNoReturnReached=true`, `hostShutdownRequired=true`, and `offlineContinuationRequired=true`. An API error has no handoff payload.

The API never performs global or all-scope apply for an ordinary HTTP caller. A valid locally created journal and prepared daemon lease are mandatory capabilities. This prevents the server phase from committing unless an identity-bound offline coordinator can resume it.

### Status and replay

`GET /api/data/factory-reset/{journalId}` returns `200 + ApiResponse<InstallationResetServerStatus>` only for a valid active journal bound to the current host. Before a data operation starts, `dataOperationId` and `handoff` are null. After the retention commit, `dataOperationId` may be populated while `handoff` remains null. The handoff appears only after the external `ServerCommitted` checkpoint is durable, or after the completed-workspace checkpoint for workspace scope. An unknown valid GUID returns `404 Data.NotFound`. Owner-invalid, digest-invalid, or corrupt active state returns `409 Data.JournalMismatch`. Completed receipt replay is a local CLI operation, so a new host never accepts the prior host binding. Apply is idempotent only for the active journal on the original host and returns the same handoff after its publication.

### Status mapping

- `Validation.UnsupportedMediaType`: `415` for a missing or non-JSON content type.
- `Validation.InvalidBody`: `400` for malformed or null JSON.
- `Data.InvalidRequest`: `400` for a malformed route journal ID, missing field, scope and workspace-ID mismatch, or unknown string or numeric enum spelling.
- `Data.ConfirmationRequired`: `400` for anything except exact `RESET`.
- `Data.LocalCoordinatorRequired`: `403` for an authenticated non-loopback request.
- `Data.NotFound`: `404` for an unknown workspace or status-lookup journal.
- `Data.PlanChanged`: `409` for installation or nested data-plan drift.
- `Data.JournalMismatch`: `409` when apply names an absent capability journal, or when the supplied journal ID resolves to caller-forged, owner-invalid, digest-invalid, host-mismatched, or daemon-unprepared state.
- `Data.InventoryUnavailable`: `409` when a write-free complete data snapshot cannot be obtained for the selected scope.
- `Data.CredentialInventoryUnavailable`: `409` when exact-service provider account enumeration cannot prove the closed deletion set.
- `Data.ResetInProgress`: `409` when another installation reset owns admission.
- `Data.RecoveryRequired`: `409` when an unfinished journal must be resumed instead of starting a new reset.
- `Data.Blocked`: `409` for active work or a first-party writer that cannot drain.
- `Data.Conflict`: `409` for filesystem identity drift, unverifiable host ownership, or storage conflicts.
- `Data.FileLocked`: `409` after the bounded filesystem retry schedule is exhausted.
- `Data.UnsupportedFileSystem`: `409` when a selected volume cannot prove atomic same-volume quarantine semantics.
- `Data.WorkspaceOverlap`: `409` when a registered Campaign or control root overlaps selected global state.
- `Data.ControlPathUnavailable`: `409` when no deterministic external control root can be proven safe.
- `Data.ReconciliationFailed`: `500` when a committed phase cannot prove its terminal state.

Strict string-only enum parsing rejects numeric spellings before mutation.

## CLI output and exit codes

- `--json --dry-run` writes exactly one typed plan document to stdout.
- `--json --apply` requires `--yes --force` and writes exactly one final typed result document to stdout.
- `--output-format json` is identical to `--json`. `--print`, redirected stdin, redirected stdout, and any invocation without an interactive terminal are headless. Headless apply requires both `--yes --force`; otherwise it returns `2` without prompting or mutation.
- Before journal creation, parser, configuration, planning, or confirmation failures write one existing `CliErrorPayload` document in JSON mode. After journal creation, every cooperative success, failure, partial state, or cancellation writes one `InstallationResetResult` document.
- A process kill can prevent stdout. The recovery journal remains authoritative and the retry emits the eventual single result.
- Progress, shutdown, and recovery diagnostics go only to stderr.
- Human output summarizes scope, deletion totals, exclusions, verification, and retry guidance, then lists every credential result and its `Pending`, `Deleted`, `Absent`, `Unavailable`, `Failed`, or `Cancelled` status without a secret value.
- Factory reset is a documented exception to the data-family rule that `--json` preserves the exact API payload. Workspace apply projects the final handoff into `InstallationResetResult`; global and all apply combine the API handoff with offline phases.

Exit codes retain the documented automation contract:

- `0`: dry-run completed, or every selected apply phase verified successfully.
- `1`: API or domain failure, partial reset, failed verification, or a safely journaled retry requirement.
- `2`: invalid command shape, semantic option conflict, ordinary invalid configuration, missing confirmation, or failed confirmation. Malformed configuration is not an automatic exit `2` for the admitted reset path.
- `3`: network or connection-establishment failure while reaching the exact local host. Local executable, permission, process-start, and identity failures use exit `1`.
- `130`: cancellation. Once a journal exists, stdout still carries the phase-aware result when cooperative cancellation can complete a durable checkpoint.

Reinvoking the same scope with `--apply` discovers and resumes its unfinished journal. It does not create a second operation. No new public resume command or alias is added.

## Test-driven implementation

Every behavior slice begins with a focused failing test, followed by the smallest production change, a passing focused test, and refactoring while green.

### CLI and wire contracts

- All missing, duplicate, and conflicting scope and mode combinations return `2` with zero side effects.
- Both acknowledgements, neither acknowledgement, each invalid partial-automation combination, typed `RESET`, wrong or blank text, EOF, redirected input, cancellation, JSON, stdout, and stderr behavior match the public contract.
- Plan and apply routes cover strict enums, content type, malformed bodies, confirmation, loopback-only coordination, plan drift, source-generated metadata, and status mapping.
- The no-I/O surface parser covers recursive options before and between command tokens, all help/version and `-p` aliases, option values, duplicates, and `--` termination using the live parser descriptors.
- The streamed result writer has byte-for-byte AOT-safe contract tests for empty, paged, cancelled, and partial credential arrays.
- The exact reset command survives malformed configuration through degraded CLI bootstrap, while neighboring data commands remain blocked.
- The live command tree and generated map contain the parse-tested safe example `arcanum data factory-reset --all --dry-run`. The former irreversible-command exemption is removed.

### Coordinator and persistence

- Dry-run lists exact targets and mutates no directory, journal, key, credential, mirror, migration, digest cache, or secret promotion path.
- Workspace reset deletes only target-derived `WorkspaceContexts`, chunks, embeddings, vector mirrors, workspace-scoped Tapestry records, and generated `.arcanum` state, while preserving registration, sessions, attachments, source files, and a verified backup-only carve-out.
- Workspace predicates cover legacy subdirectory-keyed rows and exclude a more-specific nested Campaign. New producers canonicalize Campaign-bound rows to the Campaign root.
- Healthy canonical global and all reset each invoke exactly one `FactoryReset` data operation, including Tapestry and every current factory-reset data class. All adds only the pre-bound current `.arcanum` filesystem target and never double-counts rows. Damaged recovery invokes no nested data operation, returns `dataOperationId=null`, and reports unavailable row totals.
- Lost apply responses recover through status and return the same installation operation ID, data operation ID, and totals. Segmented manifests resume datasets larger than one segment without loading all entries.
- Each crash point before and after backup staging, quarantine, database commit, external checkpoint, host handoff, daemon removal, credential deletion, backup restoration, and verification resumes idempotently.
- Equal and nested Unix and Windows root topologies, identity changes, symlinks, junctions, mount drift, permissions, locked stores, and unavailable credential backends fail closed.
- The filesystem retry schedule is asserted through `TimeProvider`, including non-retryable identity and permission failures plus `Data.FileLocked` exhaustion.
- Active-pointer races permit one winner and retire untouched losing journals. Kills before pointer publication, after publication, and before or during final stdout recover without ambiguous ownership.
- Real process-kill tests cover control-root staging and marker publication, segment publication before header replacement, header replacement, daemon-posture checkpoint, active-pointer publication, backup rename, credential deletion before checkpoint, provisional receipt transition, publication verification, and final stdout. A kill before `publicationVerified=true` must keep bootstrap blocked.
- An observable two-process test pauses at receipt transition and proves that the fixed single-flight lock prevents a contender from publishing a new active pointer or replaying a provisional receipt.
- A restricted workspace recovery host reconciles a committed retention marker without starting any ordinary producer.
- Damaged recovery returns null row totals, preserves unknown entries, and cannot report bootstrap readiness. Healthy Campaign/control-root overlap blocks before mutation.
- Preservation tests cover canonical backups, valid archives with arbitrary names nested anywhere in a selected target, intent-before-rename crash recovery, digest/no-clobber restoration, and untouched external archives.
- Preservation tests cover byte-identical archives, distinct hard-linked directory entries, and deterministic multiplicity-preserving destinations.
- Filesystems without verified atomic rename semantics return `Data.UnsupportedFileSystem` before the point of no return.

### Concurrency and startup

- Contract tests inventory every first-party mutator against in-process or cross-process reset admission, including Compendium and The Forge writers.
- Active inference, uploads, batches, attachments, indexing, backup and restore, retention, configuration, credentials, MCP, A2A, child processes, managed logs, daemon work, and another reset use observable test gates rather than sleeps.
- Daemon tests cover reversible inhibition, preflight permission failure, identity drift, lost handoff, crash recovery, and verified removal without killing a PID.
- Compendium and The Forge tests hold lifetime presence leases, acknowledge reset intent by discarding stale drafts and exiting, and prove that a non-acknowledging process blocks before mutation. Short-lived writers hold one lease across read and commit.
- Old keys stop authenticating after reset.
- The read-only verifier and `arcanum run` share one first-run predicate. Interactive `run` enters guided setup, while noninteractive and JSON invocations fail with setup guidance and no prompt.
- The clean bootstrap path creates fresh keys and schema with no stale state after setup.
- Partial credential deletion cannot report success.
- Metadata-only credential planning never reads secret values or promotes protected mirrors. Deletion revalidates the exact service and account predicate.
- Unavailable provider enumeration yields a catalog-level result, a nullable account count, and a pre-point-of-no-return blocker. Summary counters include pending and cancelled entries.
- The no-promotion authentication reader covers OS-primary and protected-fallback keys without writing either store.
- In-memory credential stores cover normal tests. Credential-worker tests cover deadlines, memory/frame bounds, malformed output, and a hung backend. Disposable Windows, macOS, and Linux CI jobs exercise exact-service enumeration, metadata probing, and deletion against unique test accounts without touching a developer credential namespace.
- Pre-bootstrap tests admit help, version, `doctor list`, and `doctor explain` during recovery while blocking the default diagnostic run and every doctor repair shape.
- Private transient-host and exact-ID recovery-host modes require a journal-bound inherited channel. Credential-worker tests separately prove that the inherited read-only planning capability can list accounts during dry-run but cannot probe or delete, while journal-bound capabilities authorize only their exact apply operation. No private mode can be entered through public arguments or environment variables.

Any relevant defect discovered on the changed execution path receives a failing regression test before its fix. Historical review leads outside the execution path remain separate work.

## Documentation ownership

Implementation updates travel with the code:

- `docs/Arcanum.DESIGN.md`: architecture, persistence inventory, state machine, recovery, credentials, and tests.
- `docs/Arcanum.API.md`: exact request, plan, apply, result, validation, and status contracts.
- `docs/Arcanum.Command.Reference.md`: syntax, confirmation, output, shutdown, and exit behavior.
- `docs/Arcanum.CommandMap.json`: regenerated from the live parser.
- `docs/Arcanum.README.md`: operator-visible ownership, preservation, daemon behavior, and first-run guidance.
- `docs/Arcanum.Design.Human.md`: navigation-level explanation.
- `docs/Arcanum.DEBUGGING.Human.md`: breakpoints, recovery inspection, and safe reset recipe.
- `docs/Compendium.README.md`: configuration deletion and first-run recreation.
- `docs/Arcanum.CHAT-LOOP.md`: reset admission, cancellation boundaries, and crash classification.

## Release gates

The command map is regenerated from the live parser and then verified byte for byte without update mode. Before integration, the branch must pass:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
```

The AOT gate must prove that both CLI ILC and regex smoke logs were freshly produced and scanned, and that allowlisting matches message text rather than hiding a first-party warning by project name. Shipping Windows, macOS, and Linux CI jobs run platform path-identity and credential-store tests plus disposable Windows SCM, launchd, and systemd-user registration inhibition, removal, and recovery tests. Packaged AOT smoke tests enter the transient host, exact-ID recovery host, and credential-worker private modes through a valid inherited channel and prove public arguments cannot enter them. Real process-kill tests terminate the CLI at daemon-posture, control-marker, active-pointer, segment/header, backup-rename, credential-checkpoint, and completion-receipt boundaries, then prove idempotent recovery on the next invocation. Exception-only fault injection does not satisfy these durability gates.

An independent code review and final diff inspection occur before the implementation commit, merge to `main`, and push.
