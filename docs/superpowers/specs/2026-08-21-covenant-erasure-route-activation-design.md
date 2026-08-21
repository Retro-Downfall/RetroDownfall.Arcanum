# Covenant Erasure Route Activation Design

**Date:** 2026-08-21

**Issue:** GitHub #128, sub-issue of #119

**Status:** Approved, including the reviewed durable installation-reset handoff and lease-ownership refinements

## Objective

Activate the existing Covenant memory-reset and healthy-catalog factory-erasure routes. Both routes must enter the durable `CovenantErasureCoordinator` directly, preserve the existing ten-phase recovery protocol, and return only after authority publication, disclosure-writer reopen, general-admission reopen, and exclusive-lease release.

The change also repairs the reset CLI's broken preview call, preserves the existing broad factory-reset contract, and makes the shared external-retention disclosure visible at the owning confirmations.

## Decisions

### Service-owned orchestration

`DataRetentionService` owns the combined lifecycle because it already owns the durable long-running operation, plan identity, terminal transition, and recovery handlers. Endpoints remain thin HTTP adapters.

Endpoint-owned orchestration was rejected because it would split durable operation ownership from recovery. Expanding `CovenantErasureCoordinator` to absorb ordinary factory deletion was rejected because the coordinator's ten phases are frozen and its storage proof is intentionally limited to Covenant-protected state.

### Dedicated memory-reset preview

Add `POST /api/data/memory/reset/plan` with the existing `MemoryResetRequest` request and `ApiResponse<DataRetentionPlan>` response. The endpoint builds `DataRetentionOperation.ResetMemory` and never applies a mutation.

The CLI uses this endpoint before the reset confirmation. The prune-plan endpoint remains prune-only; broadening it would weaken an established contract and conceal invalid operations.

### Combined factory reset

The public data factory reset keeps its existing behavior: it removes ordinary factory-owned data as well as Covenant-protected state.

One `DataRetentionFactoryReset` long-running operation owns both stages inside the coordinator's
exclusive lifecycle:

1. Save the healthy-catalog `InventoryPrepared` V1 checkpoint.
2. Enter `CovenantErasureCoordinator` and run the protected database and managed-file kernels.
3. While the exclusive lease is still held, rebuild the ordinary factory inventory and run the existing transactionally revalidated ordinary factory deletion as the factory-only continuation.
4. Continue the existing handle closure, WAL truncation, compaction, accelerator initialization, sidecar proof, authority publication, writer reopen, general-admission reopen, and exclusive-lease release.
5. Mark the durable operation complete only after the coordinator returns a reconciled completion.

The continuation runs after `ManagedArtifactsProcessed` and before `HandlesClosed`. No new phase or checkpoint version is introduced. If the process stops before `HandlesClosed` is durable, recovery repeats the restart-idempotent ordinary deletion; a later phase proves the continuation already completed. This closes the admission window that would otherwise allow new protected writes between Covenant erasure and ordinary cleanup, and it places every ordinary write before the coordinator's final WAL and sidecar proofs.

The public result retains the originally confirmed plan ID. Its row/file/byte counters are the executor-observed ordinary factory-deletion counts from the internal post-protected plan. The five Covenant aggregates remain the content-free preview authority and are never misreported as exact deletion deltas.

V1 recovery first relies on pre-readiness startup adoption to close the exact checkpoint-derived owner, then resumes the coordinator. A checkpoint at `ManagedArtifactsProcessed` reruns the ordinary factory continuation; a checkpoint at `HandlesClosed` or later skips it. A recovered operation is complete only when the coordinator and continuation reconcile. Legacy V0 recovery remains unchanged.

### Durable installation-reset handoff

Fresh `data factory-reset --global --apply` and `--all --apply` operations must run the
healthy-catalog data stage in the authenticated host before host shutdown. The installation reset
must not attempt to compose the Covenant gate, authority publisher, disclosure writer, or erasure
coordinator in its reduced offline `InstallationResetExistingGrimoire`; that composition is an
ordinary-data V0 recovery reader only and cannot safely manufacture the live host lifecycle.

Before dry-run output or confirmation, the command obtains the authenticated host's Covenant-aware
factory plan and asks `InstallationResetService` to bind that exact data-plan ID into the otherwise
local installation inventory. Binding validates that the local ordinary database targets, selected
filesystem roots, exclusions, preserved backups, credential accounts, and daemon target are still
the same candidates. It then replaces the accepted data-plan ID, recomputes the accepted binding ID,
and recomputes the public installation plan ID. The rebound plan is the only plan shown, confirmed,
and published in durable installation state. A healthy global/all operation fails before disclosure,
confirmation, shutdown, daemon mutation, or deletion when the authenticated host or its exact
Covenant inventory is unavailable. Workspace-only reset retains its existing offline sequence.

After confirmation and before the online data mutation, the installation service re-plans and
publishes an owner-only `Prepared` active record carrying the exact rebound plan and an explicit
online-data-handoff discriminator. Its `OperationId` becomes the normalized requested-operation ID
for the host factory-erasure request; it is never stored in `LongRunningOperation.RootOperationId`
and is never a Covenant gate owner. The host request carries both the confirmed data-plan ID and this
requested-operation ID. The existing request-identity ledger atomically starts or replays exactly one
factory long-running operation under fixed-time apply/effect digests.

The host response is accepted only when it proves the named operation completed under the confirmed
data plan and echoes the requested identity separately from the server-created operation ID. That
content-free completion proof is appended durably to the still-`Prepared` active record before the
command requests host shutdown. Then, and only then, the command acquires the installation
maintenance lock and resumes `InstallationResetService`. The offline service recognizes the exact
completed data operation from that requested-identity proof, runs the existing daemon mutation,
advances `Prepared` to `DataResetComplete`, and continues the already-frozen offline cleanup,
verification, credential, and retirement phases. It never invokes the public factory route through
the lease-free offline service.

If the host proves `Data.PlanChanged` before the first protected effect, the installation service may
retire the still-`Prepared` handoff so the operator can request a new plan. Cancellation, connection
loss, timeout, process termination, a non-pre-effect typed failure, or any uncertain outcome preserves
the active record for replay. On restart, only a global/all active record in `Prepared` with the
online-data-handoff discriminator and no durable completion proof permits `serve`; this exception
exists solely so startup recovery can finish or replay the named host operation. A proof-complete
`Prepared` record, every legacy active record, and every later installation phase remains
startup-blocking. The matching factory-reset CLI resume command remains admitted and uses the same
requested ID, so a crash after host commit cannot start a second erasure.

### Durable lease ownership

A factory long-running operation is lease-maintained from immediately after durable start through
replan, exact healthy-catalog proof, V1 checkpoint publication, coordinator execution, ordinary
factory continuation, and terminal transition. There is no unmaintained V0 window in which a
reconciler can adopt the row and run the legacy recovery algorithm while the initiating request is
still preparing V1.

V1 factory and V3 Covenant-reset recovery run under the same ownership-loss-aware lease maintainer as
direct execution. The maintainer is given the exact adopted owner and stops the action if renewal
fails. Every ordinary factory continuation update compares operation ID, `Running` state, exact lease
owner, and a live lease expiry before it may renew or delete. A former worker cannot renew a new
owner's row or continue effects after ownership changes.

The capability-bearing `PlanAdmissionAsync` interface is mandatory for protected planning. A caller
requesting installation or Covenant planning capability must receive the corresponding live snapshot
lease or a typed refusal; the interface default may not silently downgrade the admission to a
lease-free plan.

### Covenant reset

`MemoryResetScope.Covenant` no longer carries `Data.CovenantResetRequiresErasureCoordinator`.

After the durable data-retention mutation starts, the service derives the pinned reset effect, saves the V3 `InventoryPrepared` checkpoint through `CovenantResetCheckpointInitiator`, and calls the coordinator without holding a planning or ordinary route lease. A successful completion returns the approved content-free `DataRetentionApplyResult` under the originally confirmed plan ID. Covenant preview inventory is not converted into deletion totals because it deliberately includes retained disclosure evidence. The reset result therefore reports no unproved row, file, or byte delta; `Reconciled`, operation identity, and plan identity are the completion facts.

Any failure is normalized to a typed, content-free error. Pre-effect drain or inventory failure changes no data and may roll back and reopen. Failure from the first effect through publication, writer reopen, or disposition keeps admission closed and the checkpoint recoverable, as enforced by the existing coordinator.

### Authority, response timing, and headers

The new reset-plan endpoint, existing factory-plan endpoint, and both destructive apply endpoints require `CovenantAuthorityRequirement.LifecycleManage` metadata. Existing pre-binding authority issuance and endpoint-filter epoch revalidation remain the authority boundary; Infrastructure receives only the authenticated request's already-authorized operation.

Both planning endpoints use a lease-bearing planning admission. The service acquires exactly one installation read lease, builds the content-free inventory without nesting another lease, and transfers ownership to `CovenantProtectedJsonResult<DataRetentionPlan>`. That result revalidates immediately before the first byte and releases the lease only after JSON serialization or typed refusal completes.

Destructive endpoints await `IDataRetentionService` fully. They do not use `CovenantProtectedJsonResult`, acquire an ordinary Covenant lease, or write a response early. The authority middleware applies the protected response tuple to success and failure:

- `Cache-Control: no-store, private`
- `Pragma: no-cache`
- `Expires: 0`

It also removes validators such as `ETag` and `Last-Modified`. Data lifecycle errors delegate to the central `ArcanumErrorMapper`, so closed-state Covenant failures such as erasure-incomplete, maintenance failure, or manual artifact erasure return their frozen typed HTTP status rather than a route-local 500.

### Confirmation and disclosure

The CLI has one reusable disclosure renderer around `CovenantExternalRetentionDisclosure`. It writes, in order:

1. the shared destructive-operation text;
2. the receipt-backed possible-attempt count with exact or lower-bound wording;
3. every resolved official-provider or fallback help target;
4. only then, the owning confirmation prompt.

`data reset-memory --scope covenant` obtains its inventory from the new reset-plan endpoint. A healthy online global factory plan passes its Covenant inventory to the installation factory-reset confirmation owner. Automated acknowledgement still emits the disclosure to diagnostics. Recovery never prompts, and no route-specific paraphrase becomes a second policy copy.

The disclosure never claims that provider logs, automatic prompt caches, encrypted backups, unmanaged files, or other external disclosures were erased.

For installation global/all apply, disclosure renders from the same online Covenant inventory whose
ID was rebound into the installation plan. It therefore occurs after successful authenticated
binding and before confirmation. A fresh global/all apply never treats an unreachable host or a
missing API key as permission to continue with an ordinary-only offline deletion.

### Documentation

Update the owning current documents in the same change:

- `README.md`
- `docs/Arcanum.DESIGN.md`
- `docs/Arcanum.API.md`
- `docs/Arcanum.Command.Reference.md`
- `docs/Arcanum.DEBUGGING.Human.md`
- `docs/Arcanum.Design.Human.md`
- `docs/Arcanum.OATH.md`
- `docs/ArcanumOATH.Human.md`

Historical issue #127 plans, specifications, and dated review documents remain unchanged. No CLI command-tree or configuration-key change is required.

## Verification strategy

Implementation is test-driven. Each behavior begins with a focused failing test, followed by the smallest production change and a focused green run. Coverage includes:

- the retired reset refusal and five-count preview;
- the dedicated preview endpoint and client routing;
- lifecycle authority metadata and protected headers;
- checkpoint-before-gate ordering;
- no mutation when exclusive drain cannot complete;
- no response completion before exclusive release;
- successful and failed reset result mapping;
- combined in-coordinator factory sequencing and V1 recovery;
- real local-versus-online installation plan rebinding before dry-run and confirmation;
- durable `Prepared` handoff before the host mutation, exact requested-operation replay, and
  shutdown only after proven host completion;
- pre-effect plan-change retirement versus uncertain-outcome preservation;
- startup admission only for the explicit global/all `Prepared` online-handoff state;
- no ordinary-only offline fallback when the authenticated global inventory is unavailable;
- lease maintenance from factory LRO start through V1 publication and terminalization;
- exact-owner, live-expiry renewal in direct factory execution and V1/V3 recovery;
- lease coverage through reset/factory plan serialization;
- shared disclosure ordering and decline behavior;
- absence of the retired conflict code.

Completion requires the full solution build with zero warnings, both test projects, coverage thresholds, and the first-party AOT/IL-warning verifier.
