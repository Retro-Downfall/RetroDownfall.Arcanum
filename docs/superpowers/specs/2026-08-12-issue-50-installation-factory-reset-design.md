# Issue #50 Installation Factory Reset, Reduced Design

**Status:** Approved reduced design, written specification pending final review

**Scope ceiling:** 15,000 total added physical lines reported by `git diff --numstat main...HEAD`, including this specification, the implementation plan, production code, tests, generated command-map changes, and product documentation. Deletions do not offset additions. Stop and request approval before exceeding this ceiling.

## Purpose

Add one safe command that removes Arcanum-owned local state. Workspace scope leaves that workspace empty of Arcanum-authored local state. Global and all leave the installation ready for first-run setup.

```text
arcanum data factory-reset (--workspace | --global | --all) (--dry-run | --apply) [--force]
```

The implementation must reuse the existing data-retention factory-reset engine. It must not introduce a second database-deletion engine.

This design replaces the earlier comprehensive design for issue #50. The earlier implementation is archived outside the working tree and is not an implementation source of truth.

## Deliberate limits

The reduced implementation does not add:

- a generic installation-operation API;
- a public reset status or receipt API;
- a segmented or hash-chained journal;
- historical receipt retention;
- a credential subprocess protocol;
- a new daemon abstraction;
- a generic process-presence framework;
- exhaustive process-kill matrices on every operating system.

It adds only the state, recovery, and platform behavior required to make this command safe and resumable.

## Command contract

### Scope and mode

The command requires exactly one scope selector and exactly one mode selector.

Scopes:

- `--workspace` resets the most-specific registered Campaign root containing the current directory. That registered root is the authoritative workspace path.
- `--global` resets installation-wide Arcanum state and credentials. It does not delete authored `.arcanum` directories in external registered workspaces.
- `--all` performs the global reset and the current-workspace reset in one accepted plan.

Modes:

- `--dry-run` produces the exact current plan and performs no mutation.
- `--apply` executes or resumes the selected reset.

Omitted, repeated, or conflicting scope or mode options return exit code 2. Validation is occurrence-aware and runs before configuration loading, filesystem access, API calls, dependency injection, or prompting.

`--force` is valid only with `--apply`. Noninteractive acknowledgement requires both the recursive global `--yes` option and the command-local `--force` option. Supplying only one returns exit code 2 before mutation.

### Workspace resolution

`--workspace` does not accept a path or ID argument. The planner canonicalizes the current directory, resolves its most-specific containing registered Campaign, and uses that Campaign's canonical registered root as the authoritative workspace path. The accepted binding contains the Campaign ID and that root. `--all` captures the same binding before global data deletion.

Planning fails without mutation when:

- no registered Campaign contains the current directory;
- canonicalization fails;
- the target is ambiguous;
- the Campaign catalog is unavailable;
- the selected workspace overlaps the reset control directory.

Nested registered Campaigns are independent ownership boundaries. A parent workspace reset excludes every more-specific registered Campaign subtree.

### Confirmation

Interactive apply prints the accepted plan summary and reads one line. Only the ordinal, case-sensitive text `RESET` authorizes execution.

The prompt is unavailable when stdin or stdout is redirected, JSON output is selected, `--print` is selected, or the process is otherwise headless. Those cases require both `--yes` and `--force`.

Blank input, end of input, different casing, and any other text return exit code 2 without creating a reset operation.

### Output and exit codes

Dry-run emits one `InstallationResetPlan`. Apply emits one `InstallationResetResult` after an operation record exists. JSON modes emit exactly one JSON document on stdout, with diagnostics on stderr.

Exit codes are:

| Code | Meaning |
|---:|---|
| `0` | Dry-run completed, or apply completed and final verification passed. |
| `1` | Apply failed, is partial, requires recovery, or final verification failed. |
| `2` | Invalid command shape, invalid configuration, unavailable confirmation, or declined confirmation. |
| `3` | A required authenticated local-host request failed because the host was unreachable or timed out. |
| `130` | Cancellation was observed. After the point of no return, the current identity-sensitive step is stabilized and the resumable state is persisted before return. |

Pre-operation failures use the existing CLI error payload. Once the operation record exists, failure and cancellation output uses `InstallationResetResult` so automation can see the durable phase and resume requirement.

## Ownership by scope

### Workspace

Workspace reset removes only Arcanum-owned state for the accepted Campaign-and-workspace binding:

- `WorkspaceContexts` whose canonical `RootPath` equals the workspace root;
- workspace file chunks whose canonical `WorkspacePath` equals the root, plus their embedding rows;
- Tapestry generations, nodes, and embeddings whose stored workspace scope equals the root;
- `{authoritative workspace root}/.arcanum` and its Arcanum-authored contents.

It preserves:

- every source file outside `.arcanum`;
- nested registered Campaigns and their `.arcanum` directories;
- global sessions, prompts, attachments, configuration, keys, credentials, daemon registration, and global stores;
- backup archives.

Sessions, prompts, attachments, apprentices, Campaign registration, Campaign-scoped Tapestry, and other global or user-authored Campaign records are not workspace-local memory and are preserved.

The workspace database phase is a new `ResetWorkspace` operation on `IDataRetentionService`. Its request binds the Campaign ID and canonical workspace root. Planning and apply use the same closed ownership descriptor, and deletion occurs in one existing Grimoire transaction.

### Global

Global reset uses the existing `DataRetentionOperation.FactoryReset` for the database and managed-byte phase, including its plan conflict checks, durable long-running-operation recovery, managed-log gate, quarantine, and reconciliation.

After that canonical phase and authenticated host shutdown, the offline phase removes the remaining installation-owned state from the closed `ArcanumPaths` and backup-inventory catalogs:

- configuration and preset state;
- Grimoire database, KDF, WAL, and SHM state;
- Data Protection key material and protected secret mirrors;
- global MCP configuration and trust metadata;
- the complete contents of `ArcanumPaths.GrimoireDirectory` and, when physically distinct, `ArcanumPaths.SecretStoreDirectory`, except preserved backup files and their required ancestor directories;
- exact `arcanum` OS credentials;
- daemon registration and Arcanum PID or lock state owned by this installation.

External registered workspace roots are not traversed or changed by `--global`.

### All

All captures the current workspace binding before global database deletion, then performs the global reset and the one current-workspace filesystem reset. It does not traverse or reset other registered Campaigns.

The current workspace's more-specific nested Campaign roots remain excluded. Source outside the selected `.arcanum` directory is never selected.

## Preserved and excluded state

The following are never deletion targets:

- the Arcanum executable or installed application bundle;
- repository or workspace source outside `.arcanum`;
- SuperCompress data;
- unrelated OS credentials or credentials under a service other than exact `arcanum`;
- third-party MCP servers and data outside canonical Arcanum roots;
- backup archives outside selected roots;
- the reset control directory.

Every regular `.arcbackup` file encountered inside a selected deletion root is treated as a preserved backup only when the existing bounded backup-header reader validates its `ARCABACK` header and supported format. The selective deletion walker leaves that exact file and each required ancestor directory in place.

Hard-linked, identity-changing, unreadable, unsupported, or symlinked backup candidates block apply before deletion begins. Identity is revalidated immediately before neighboring selected entries are deleted. The reset never follows a symlink.

Files with an `.arcbackup` suffix but an invalid header are ordinary selected-root contents. Dry-run reports them as deletion targets rather than preserved backups.

## Credential contract

The closed credential inventory is exact service `arcanum` and these account identities:

- the fixed master API key account;
- the fixed file-encryption key account;
- the fixed web-research account;
- every configured non-Familiar inference provider captured from readable configuration;
- every canonical `provider-*-key.dat` protected mirror name that can be mapped back to one normalized provider identity.

This is the same bounded model used by the existing `key list` inventory, extended with provider mirror names so configuration damage does not hide a locally protected provider credential. The reset never enumerates arbitrary OS credentials.

Planning probes each admitted identity through the existing `IOsCredentialStore.TryGet` status contract and never exposes the value. A dry-run with an unavailable store returns the plan with a blocker and exit code 0. Apply rejects an unavailable accepted inventory before creating an operation and returns exit code 1. Apply then probes, calls the existing idempotent `Delete`, and probes again. Each result is `Deleted`, `Absent`, `Unavailable`, or `Failed`. Inventory that becomes unavailable after operation creation, a surviving credential, or a failed deletion makes the result non-success and resumable.

Protected mirror files are cataloged separately and deleted through identity-safe filesystem cleanup. Reset planning and verification must not call high-level secret reads that can promote a mirror into the OS store.

## Architecture

### Pure command-shape validation

`Program.Main` first calls a tiny dependency-free argv preflight before `AddArcanumConfiguration`. It recognizes only the exact `data factory-reset` command path, occurrence-counts its six command-local options and recursive `--yes`, exempts help and version, and rejects missing, repeated, or conflicting shape with exit code 2. The normal `System.CommandLine` parser then performs the complete parse and uses the same option spellings asserted by contract tests.

Help and version continue to work without reset recovery. A matching `--apply` invocation is admitted to resume an active operation. `run` reports recovery guidance while an active operation exists. Other commands reach no reset state because apply always owns the maintenance lock before mutation.

### CLI orchestration

`InstallationFactoryResetCommand` owns the command workflow:

1. Resolve and validate the command shape.
2. Run the read-only installation-state and active-operation probe.
3. Build the composite plan from the local catalog and canonical data-retention plan. For `--all`, capture the one current workspace before building the global plan.
4. Emit dry-run, or obtain exact confirmation.
5. Request authenticated loopback shutdown when a host is running.
6. Acquire the existing maintenance lock and wait for process-held resources to close.
7. Rebuild and compare the accepted plan while the maintenance lock is held.
8. Create the durable active operation.
9. Stop or uninstall the daemon.
10. Execute the canonical data-retention phase through a restricted offline graph.
11. Complete idempotent offline file and credential cleanup.
12. Verify the selected scope and publish the result.

Every apply runs offline after shutdown while holding the maintenance lock. The CLI may invoke the shared Infrastructure coordinator in this state, following the existing backup-restore exception. Database and managed-byte deletion still belongs exclusively to `IDataRetentionService`.

### API data phase

The data API exposes planning for the two canonical data scopes:

- `Global` maps to `DataRetentionOperation.FactoryReset`;
- `Workspace` maps to `DataRetentionOperation.ResetWorkspace` with the accepted Campaign-and-workspace binding.

`POST /api/data/factory-reset/plan` accepts the API-only data scope `Global` or `Workspace` plus the required workspace binding when applicable, and returns `ApiResponse<DataRetentionPlan>`.

The existing authenticated `POST /api/data/factory-reset` remains available for its existing data-retention-only contract and lowercase `factory-reset` confirmation. The installation command does not call it. Installation apply calls `IDataRetentionService` only after shutdown through the restricted local graph.

The new planning endpoint requires normal API authentication and a loopback peer. `All` is not an API data scope. Its database phase uses the one global factory-reset operation; its accepted current-workspace binding adds the selected `.arcanum` filesystem target to the composite installation plan.

The CLI rejects a configured non-loopback API base before planning or shutdown. Authentication plus later maintenance-lock acquisition is the local-host handoff proof. A PID alone is not trusted as process identity.

### No-host and damaged-state behavior

When no host is running, apply acquires the maintenance lock and uses a restricted local service graph to call the same `IDataRetentionService` plan and apply methods. Dry-run does not acquire or probe the maintenance lock because the current lock implementation creates a lock file.

The restricted graph omits ordinary hosted producers and file logging. It does not start a daemon, publish a PID, create a database, install schema, or generate credentials. Planning opens an existing Grimoire through a no-create read-only connection. If a safe read-only view is unavailable, planning marks the data inventory unavailable instead of creating SQLite sidecars or checkpointing a WAL.

If the Grimoire cannot be opened:

- workspace apply is blocked because Campaign ownership cannot be proven;
- global may continue with the closed filesystem and credential catalogs after recording `dataInventoryAvailable: false` in the accepted plan;
- all may continue only when its current-workspace binding was already proven from the readable Campaign catalog and is present in the accepted plan; otherwise it returns a blocker without mutation;
- dry-run reports unavailable row totals and the exact filesystem, backup, credential, and daemon inventory it could prove;
- apply removes the damaged Grimoire files as cataloged installation targets.

An ordinary network failure while a configured local host should be running returns exit code 3. It is not silently reclassified as damaged-state authorization.

## Bounded durable operation

### Location and locking

The active record uses a stable owner-only sibling location beside the canonical Grimoire root. The reset lock uses the existing maintenance-lock path for that root. Workspace planning rejects a selected root that overlaps either control path.

The maintenance lock provides one cross-process reset owner. It is acquired before publishing `active.json` and held through the final phase transition. Its file may be created and deleted by the existing implementation; `active.json` remains the crash-recovery authority.

### Record format

One versioned, owner-only `active.json`, capped at 64 KiB, stores the operation ID, scope, workspace binding, immutable accepted target binding, admitted credential account names, canonical data plan IDs, phase, point-of-no-return flag, aggregate outcomes, and last error code. It contains no secret values. Planning fails if this bounded representation would exceed the cap.

The immutable binding contains the selected canonical filesystem roots, preserved backup identities, and credential names. The record uses source-generated JSON, bounded serialization, atomic replacement, and file flush. It contains no per-target history. Resume validates the immutable binding, then recomputes only remaining status and safely skips targets already absent. It does not require the pre-mutation inventory fingerprint to reappear.

### Phases and resumption

The durable phases are:

1. `Prepared`
2. `DataResetComplete`
3. `OfflineCleanupComplete`
4. `Verified`
5. `Completed`

The point of no return is the first committed canonical data deletion or first successful offline quarantine, whichever occurs first.

Before the point of no return, a failure retires the operation record and returns a failed result. After it, every closed delete and credential delete is forward-idempotent. A same-scope, same-workspace `--apply` invocation reacquires the lock, validates the immutable accepted binding, and continues. A different invocation returns `Data.ResetInProgress` without mutation.

A completed operation remains available until one matching invocation emits its final result, then the active record is retired. No historical receipt is retained.

### Filesystem rules

All destructive filesystem actions:

- normalize and prove canonical containment;
- capture no-follow identity before mutation;
- revalidate identity immediately before the native move or deletion;
- never follow symlinks;
- use the existing identity-owned quarantine and deletion primitive where it applies;
- make uncertain post-mutation outcomes recovery-required.

Transient sharing violations and lock errors use bounded exponential backoff through `TimeProvider`. Identity drift, containment failure, permission denial, collision, unsupported filesystem behavior, and symlink ambiguity fail immediately with a typed item result.

## Shutdown boundary

Every apply scope uses the same bounded boundary:

1. call the authenticated loopback shutdown endpoint when a host is reachable;
2. acquire the existing maintenance lock, which proves the host, watcher, indexer, Tapestry writers, and other hosted producers released installation state;
3. stop or uninstall the daemon through `IDaemonManager`;
4. re-plan and apply only through the restricted local graph while retaining the lock.

Host startup first checks the stable `active.json` and refuses to start while it represents any non-completed or unreported completed reset. It then acquires the existing maintenance lock. The active record closes the crash window after the reset process dies, and the lock excludes a live reset. API-backed CLI and desktop writers lose their host before mutation begins. Read-only dry-run remains available through the authenticated planning endpoint before shutdown.

Failure to shut down or acquire the maintenance lock produces a pre-operation nonzero result. Daemon uninstall occurs after `active.json` exists; its failure is recorded as a resumable nonzero result before data or filesystem deletion. No PID signaling or process-name killing is permitted.

## First-run detection

Host bootstrap and `RunCommand` read the same bounded active-record probe before creating installation state. `RunCommand` performs its fresh-install preflight before `IGrimoireCliInitialization.EnsureInitializedAsync`:

- an active `active.json` returns recovery guidance and exit code 1;
- absence of configuration, Grimoire database/KDF, protected master-key state, and the fixed master credential triggers the existing Setup Wizard interactively;
- the same fresh state in JSON or another noninteractive mode returns exit code 2 with setup guidance and performs no initialization.

Backups alone do not make the installation initialized. `serve` gains only the active-reset startup block. Operators complete setup through `arcanum run` before starting a host.

Final reset verification directly probes the same closed targets:

- global and all must find every selected authoritative target and credential absent;
- workspace must find the exact selected database rows and `.arcanum` state absent;
- every preserved backup must remain at its original path with the same captured identity and length.

Success is published only after verification passes.

## Public contracts

The new portable contracts are intentionally small:

- `InstallationResetScope`: `Workspace`, `Global`, `All`;
- `InstallationResetPhase`: the five durable phases above;
- `InstallationResetItemStatus`: `Pending`, `Preserved`, `Deleted`, `Absent`, `Unavailable`, `Failed`;
- `InstallationResetPlan`: scope, workspace binding, data plan ID, availability flags, exact target descriptors with category, canonical path or database predicate, captured identity when applicable, files, bytes, and role, aggregate counts, preserved backups, credential accounts, exclusions, blockers, deterministic plan ID, and the bounded immutable resume binding accepted by apply;
- `InstallationResetResult`: operation ID, scope, phase, point-of-no-return flag, aggregate deletion counts, credential results, preserved backups, verification outcome, and resume requirement.

Every enum uses the repository's string-only JSON converter. Every API or CLI JSON type is registered in the appropriate source-generated context.

New public error codes are limited to:

- `Data.InventoryUnavailable`;
- `Data.CredentialInventoryUnavailable`;
- `Data.ResetInProgress`;
- `Data.RecoveryRequired`;
- `Data.FileLocked`;
- `Data.WorkspaceOverlap`;
- `Data.ControlPathUnavailable`.

Existing invalid-request, confirmation, plan-changed, blocked, conflict, not-found, and reconciliation errors are reused.

## TDD acceptance plan

Implementation proceeds in bounded slices. Each slice starts with a focused failing test and ends with its focused suite green before the next slice.

### Command and contracts

- exact scope and mode cardinality, including repeated options;
- exit 2 before any configuration, filesystem, API, DI, or prompt access;
- exact `RESET` confirmation and `--yes --force` relationship;
- source-generated JSON and one-document output;
- safe help example: `arcanum data factory-reset --all --dry-run`.

### Canonical data authority

- global and all reuse the existing factory-reset plan, apply, and recovery path;
- workspace deletes only the closed exact-root row set and `{authoritative workspace root}/.arcanum`;
- nested Campaign and source preservation;
- plan identity recheck and one-transaction database deletion;
- exact-root table predicates and maintenance-lock exclusion of hosted producers.

### Bounded operation and filesystem

- owner-only creation and stable single-flight locking;
- resume at every durable phase;
- pre-point-of-no-return retirement and post-point-of-no-return roll forward;
- canonical containment, symlink, identity-swap, hard-link, collision, and unsupported-filesystem failures;
- bounded lock retry with fake time;
- recognized in-root backup preservation during selective deletion;
- external backup and invalid-lookalike behavior;
- bounded active record serialization and plan reproduction on resume.

### Credentials, host, and first run

- fixed, configured, and protected-mirror-discovered provider account deletion with post-probe;
- unrelated credential preservation;
- loopback-only API and shutdown;
- daemon uninstall and maintenance-lock handoff;
- no-host and damaged global/all behavior;
- side-effect-free first-run preflight before run initialization.

### Integration and release

- live authenticated planning endpoint and existing data-reset compatibility endpoint;
- dry-run performs no writes;
- interrupted apply resumes through the same command;
- partial and verification failures are nonzero and never report clean;
- maintenance-lock exclusion and restricted-local-graph tests;
- command-map regeneration and verification;
- full solution build, all test projects, coverage threshold, and Native AOT warning gate.

The test suite uses deterministic seams for phase crashes and races. It does not add a full cross-platform process-kill matrix for every internal write.

## Documentation ownership

The implementation updates:

- `docs/Arcanum.Command.Reference.md` for syntax, confirmation, output, and exit codes;
- `docs/Arcanum.API.md` for the planning endpoint and unchanged compatibility apply endpoint;
- `docs/Arcanum.DESIGN.md` for ownership, recovery, first-run detection, and retention reuse;
- `docs/Arcanum.README.md` for operator guidance and API/command maps;
- `docs/Arcanum.Design.Human.md` for the human architecture summary;
- `docs/Arcanum.DEBUGGING.Human.md` for reset recovery diagnostics;
- `docs/Arcanum.CHAT-LOOP.md` for shutdown handoff and fresh-start behavior;
- generated `docs/Arcanum.CommandMap.json`.

## Completion rule

The change is complete only when:

- every focused TDD slice is green;
- the command-map snapshot is regenerated and then verifies byte-for-byte;
- the solution build and all test projects pass;
- coverage and Native AOT warning gates pass;
- product documentation matches the shipped behavior;
- the implementation stays at or below the 15,000-line ceiling, or the user explicitly approves a revised ceiling;
- the branch is reviewed, committed, merged to `main`, and pushed only after all required gates are green.
