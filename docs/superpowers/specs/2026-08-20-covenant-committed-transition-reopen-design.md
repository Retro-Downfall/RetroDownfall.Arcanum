# Covenant Committed Authority Transition and Same-Process Reopen Design

## Status and authority

This document records the approved implementation design for the internal Covenant erasure lifecycle. `docs/Arcanum.DESIGN.md` remains the authoritative product architecture and must be updated with the landed behavior. This design narrows implementation decisions; it does not activate the public Covenant reset or healthy-catalog factory-reset routes.

## Outcome

After canonical deletion and local secure-erasure proof, the erasure coordinator publishes one complete committed runtime generation while its exact exclusive lease remains held, restarts the warm disclosure writer against that generation, and makes exactly one disposition attempt. A successful `CommitAndReopen` makes status, canonical reads and writes, inference context acquisition, and disclosure journaling usable in the same process against the fresh dataset.

The implementation must also make every old authority context, operation lease, and opaque token unusable, refuse a healthy-catalog factory erasure when the catalog cannot be proven healthy, preserve nonrevocable disclosure count semantics, and leave any post-destruction failure durably recoverable with admission closed.

## Non-goals

- Do not map or enable the public Covenant memory-reset route.
- Do not map or enable the healthy-catalog factory-erasure route.
- Do not remove `Data.CovenantResetRequiresErasureCoordinator`; update its wording only to say the built coordinator is not yet wired to the route.
- Do not add API or CLI request/response DTOs.
- Do not add a new reset phase or modify the frozen version-3 mutation and version-1 factory-erasure checkpoint shapes.
- Do not implement the broader full-installation-reset workflow.
- Do not replace the disclosure journal with the previously proposed batching, group-commit, or compaction architecture. The required production warm writer is serialized and bounded to this lifecycle.

## Invariants

1. The coordinator is the only owner of the erasure authority transition and the only caller that may reopen general admission.
2. The exact `(OperationId, CovenantExclusiveOperation, EffectDigest)` recovery owner is reconstructed from the durable checkpoint. A requested operation id is never substituted for the durable operation id.
3. The exclusive gate remains closed throughout verified candidate publication and disclosure-writer restart.
4. The immutable, sidecar-free verified reopen is the last database read before publication. No ordinary handle may reread candidate state after that proof.
5. A resumed pass at `ReopenedVerified` repeats the immutable verification because the verified candidate is intentionally not persisted in either frozen checkpoint.
6. Publication uses one complete input: the immutable persisted candidate plus one atomically captured expected runtime generation. Dataset, canonical envelope, installation authority, cleanup capability, canonical position, accelerator position, feature state, and health state cannot be selected from different runtime snapshots.
7. All six envelope-purpose families change key generation on a successful publication, even when the durable recovery epoch is preserved. Operator contexts, read-authority epochs, and operation leases additionally bind the in-process runtime authority generation, so unchanged durable authority counters cannot keep an old in-process capability valid. A turn acquired from a read epoch must match that epoch's runtime generation before any Covenant store read or mutation staging occurs.
8. Every failure from the post-commit publisher, including lease revalidation failure, conditionally retires the key and authority generation it observed before revalidation. If a competing complete publication already replaced that generation, the failed publisher must not retire the replacement; the old generation is already unusable.
9. Keys, authority, and availability publish through one composite runtime-generation compare-exchange. No reader can observe an intermediate provider swap, and a stale compare-exchange publishes nothing. The capability transition is the sole source of feature, capability generation, and dataset facts; the enclosing authority transition contains no duplicate values that can disagree with it.
10. The disclosure writer cannot accept a new acknowledgement after quiesce begins. Quiesce waits for the one in-flight serialized acknowledgement, closes and unregisters the warm connection, and is idempotent.
11. The disclosure writer opens only against the currently published dataset and only when the database dataset read through the new initialized handle equals it. After a pre-effect failure that is the unchanged old generation; after committed erasure it is the freshly published generation.
12. Only a proven pre-erasure failure whose old-generation disclosure writer was successfully restored may choose `RollbackAndReopen`. A failed restoration and every failure after the first possible durable effect choose or preserve `KeepClosed`.
13. `CommitAndReopen` is attempted exactly once. If that one-shot call fails, there is no compensating `KeepClosed` call and no second disposition.
14. A failed, cancelled, timed-out, or throwing one-shot disposition is normalized and recorded in the existing durable operation ledger as `ReconciliationRequired`, with a content-free error code, without inventing a new erasure phase. Recording uses its own bounded token and retries revision races until it succeeds or proves the row is already recoverable.
15. `CanonicalResetApplied`, `LocalSecureErasureComplete`, and external nonrevocable disclosure exposure remain independent facts.
16. Disclosure exposure includes only `Nonrevocable` buckets. Its count is a sum and its kind is `LowerBound` if any folded bucket is lower-bound; a boolean is derived from whether the count is positive, never used as a substitute for the count and kind.
17. Healthy-catalog factory erasure refuses before its checkpoint and revalidates under the exclusive gate before any artifact is touched. The refusal names restore, Covenant-family reinitialize, and full installation reset.
18. Catalog, key, path, cursor, digest-preimage, checkpoint, and content details never enter public errors, logs, metrics, or traces.
19. The normal route refusal remains until the subsequent route-integration slice.
20. Inventory memory is bounded by one label page. A complete first pass validates every managed producer before effects; a second deterministic pass executes one page at a time and may safely restart from the beginning because every erasure kernel is idempotent.
21. A process restart reconstructs the exact durable erasure gate owner before Covenant readiness is published. Malformed or conflicting durable erasure ownership refuses readiness rather than opening admission.

## Runtime publication model

### Verified candidate

`CovenantVerifiedCandidateState` is extended so its immutable read contains every persisted fact needed for runtime publication:

- dataset generation;
- canonical search sequence;
- the current core Campaign-deletion sequence read in the same immutable statement;
- accelerator applied dataset and search sequence;
- applied Campaign and Session deletion sequences;
- accelerator epoch and rebuild state;
- canonical envelope master version, fingerprint, and envelope epoch;
- installation identity, authority epoch, current master version and fingerprint, recovery envelope epoch, host-tools state, and transition id;
- the Covenant capability cleanup row's Campaign and Session sequences and full-sweep flag.

Verification continues to compare the canonical envelope master with installation authority and the canonical cleanup watermarks with the shared cleanup row. It additionally validates the all-or-nothing applied accelerator tuple and closed enum codes before constructing the candidate.

Immediately before projecting the committed transition, `CovenantErasureTransition` captures one immutable `CovenantRuntimeGenerationState`. Its availability member supplies the expected generation, feature flag, canonical and accelerator capability health, installed schema versions and fingerprints, and diagnostic codes. Those values are carried whole into the successor and the final publication compare-exchanges against that exact state object. A runtime mutation between capture and publication is therefore a stale transition, never a mixed successor.

### Production transition

A new `CovenantErasureTransition` implements `ICovenantErasureTransition` by composing the existing canonical transaction, local-erasure storage-health proof, and authority-transition publisher. The seam changes are:

```csharp
Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
    CancellationToken cancellationToken);

Task<Result> PublishCommittedAsync(
    ICovenantExclusiveOperationLease lease,
    CovenantVerifiedCandidateState candidate,
    CancellationToken cancellationToken);
```

`ReadCandidateDatasetGenerationAsync` is removed. On a fresh run the returned verified candidate flows directly to publication. On a resumed run, including one whose durable phase is already `ReopenedVerified`, the immutable verification is repeated and its new value flows directly to publication.

### Authority and capability publication

`CovenantCommittedAuthorityTransition` carries the complete content-free runtime transition, including one canonical `CovenantCommittedCapabilityTransition` for availability and the verified Session/cleanup facts that are not public availability fields. The enclosing transition has no independent capability-generation, dataset-generation, or enabled fields. Key derivation and publication both read those facts from `Capability`.

A singleton `CovenantRuntimeGenerationProvider` owns one immutable state containing an internal monotonic runtime authority-generation number, the live envelope generation, authority snapshot, and availability snapshot. The published `CovenantAuthoritySnapshot` is stamped with that runtime generation. `OperatorAuthorityContext`, `CovenantReadAuthorityEpoch`, and `CovenantOperationLeaseSnapshot` capture it and require it during revalidation in addition to the retained durable authority fields. `CovenantEnvelopeMasterKeyProvider.Current`, `CovenantAuthoritySnapshotProvider.Current`, and `CovenantAvailability.Current` all project from that one state. The operation gate captures and revalidates from one composite read rather than combining authority and availability reads. One private lock serializes composite publication, purpose-key copy/counter reservation, retirement, and live-key disposal. Ordinary availability publishers replace a successor that preserves the same runtime authority generation, key, and authority references.

`CovenantContextProvider` does not treat nonnull `ReadAuthorityEpoch` as sufficient. After acquiring the turn lease and before its first store call, it requires the lease's runtime authority generation and durable authority epoch to equal the invocation epoch. A mismatch disposes the lease and returns stale without reading or staging. This closes the pause race where a request minted under generation G crosses a complete reset and would otherwise acquire a fresh G+1 lease. Conversely, a matching G lease pins generation G through the store read because an exclusive reset must drain it before publication.

The transition publisher:

1. captures the expected composite authority generation;
2. revalidates the still-held exclusive lease;
3. checks installation identity and monotonic authority, master, canonical-envelope, recovery-envelope, and capability generations;
4. prepares a complete new envelope-key generation off to the side;
5. validates the complete availability successor against the captured state;
6. enters the exclusive lease's exact-held callback, proves the exact closure and live registration under the gate lock, then requires the exact captured runtime-state reference under the holder lock and publishes one successor containing the prepared keys, authority, and availability while admission is closed;
7. transfers ownership of the prepared keys and retires the previous generation only after that single linearization point.

`CovenantAvailability` gains one compare-exchange publication for a committed reset. It advances exactly one generation and preserves schema health, installed fingerprints, diagnostics, and the configured feature flag while publishing the fresh canonical and accelerator tuple with `CovenantHealthTransition.Reset`.

Successful publication compares the exact captured `CovenantRuntimeGenerationState` reference, not only its runtime authority number. An availability-only update therefore makes a built successor stale instead of being overwritten. Failure retirement intentionally uses the broader runtime-authority predicate and preserves the latest availability from the then-current state. Thus a feature/schema-health race fails closed without losing diagnostics, while a competing authority winner remains untouched.

The gate's exact-held callback is the publication linearization boundary. Under the gate lock it requires the same installed `Closure` object and the same `LiveRegistration`; only then may the short callback acquire the holder lock. The lock order is always gate then holder, holder methods never call the gate, and gate fact reads remain lock-free. A claimed disposition, released registration, removed closure, or replacement registration fails revalidation and cannot invoke publication after admission reopens.

If any publisher step fails, including lease revalidation or exact-held proof, it conditionally publishes a fail-closed successor only while the runtime authority-generation number observed before revalidation is still current. That successor preserves the latest diagnosable availability, increments the runtime authority generation, makes the live envelope generation and externally issuable authority unavailable, and records an internal content-free recovery marker for the exact exclusive recovery owner. It may retain the predecessor's durable authority counters only behind that marker; ordinary issuance and admission cannot see them. An availability-only race cannot defeat retirement; a competing authority publication changes the runtime authority generation, so the loser leaves that replacement alone because the observed old keys are already no longer current. The gate remains closed.

The gate has one recovery-only exception to ordinary null-authority refusal. An exact owner whose closure matches the retired marker may resume and revalidate an exclusive lease bound to the retired runtime generation. A wrong owner, a different closure, and every ordinary lease remain refused. This makes same-process retry possible: the resident HKDF root derives another prepared generation and successful publication clears the marker. It never turns retirement into an authorization grant.

Bootstrap publishes schema plus persisted canonical/accelerator state, prepares keys offside, and atomically initializes authority and keys against that exact availability generation. Host-tools denial, row mismatch, or derivation failure exposes neither authority nor keys. The persisted canonical and accelerator tuple advances availability once, not through two successive publications. Deterministic interleaving tests hold readers at each former swap boundary and prove they observe either the complete predecessor or complete successor, never a mix.

Bootstrap key preparation uses a distinct internal `CovenantEnvelopeBootstrapKeyInput`, not a fabricated committed capability transition. It carries installation identity, master-key version, canonical and recovery envelope epochs, and a nullable healthy canonical dataset. When the canonical tier is unavailable, degraded, or has a master-version mismatch, initialization atomically publishes clean operator authority plus only the three installation-keyed recovery families; dataset-keyed families remain unkeyed and refuse issuance. The committed reset transition keeps its nonempty `Capability.DatasetGeneration` invariant and remains the sole dataset source for post-commit rekeying.

### All-six-token invalidation

The envelope master-key provider creates a fresh random 256-bit in-process generation salt for every initial or prepared key generation and mixes it into all six purpose-key derivations. The process boot salt continues to prevent cross-process nonce reuse. The diagnostic key remains free of both salts and epochs except the master-key version, retaining its documented cross-restart and cross-reset correlation behavior.

This preserves the durable recovery epoch and retained recovery evidence while ensuring:

- tokens from all six purpose families fail authentication after publication;
- per-purpose counters may restart at one without repeating a `(key, nonce)` pair;
- an abandoned prepared generation is zeroized without affecting the current generation;
- a retired current generation exposes no usable purpose key while preserving the root needed for recovery.

Partial derivation zeroizes every purpose key already allocated, the diagnostic key if allocated, generation salt, every HKDF info buffer, and every binding temporary before returning or throwing. A generation never exposes a span into its owned arrays: codec and diagnostic callers copy key material through the composite holder into caller-owned temporary buffers, then zero those buffers. Codec operations capture the runtime authority generation with the key and recheck it after cryptography but before constructing or returning the token/plaintext result; a concurrent publication makes the result stale and zeroizes the unpublished result bytes. Publication/retirement races therefore cannot zero an array while cryptography is reading it, and an encoder that started just before retirement cannot return an old token after the linearization point.

## Warm disclosure writer

`CovenantDisclosureJournal` becomes the singleton warm writer and implements `ICovenantDisclosureWriterLifecycle` and `IAsyncDisposable` in addition to `ICovenantDisclosureJournal`. Both service interfaces resolve to the same instance.

The writer owns:

- one unpooled initialized SQLCipher connection opened by `ICovenantMaintenanceConnectionFactory`;
- the connection's `ICovenantConnectionDrain` enrollment;
- a small synchronous state lock recording whether admission is open;
- one `SemaphoreSlim` serializing acknowledgement transactions and lifecycle transitions.

Acknowledgement checks admission before waiting and again after acquiring the serializer. It lazily opens the initial warm connection, then runs the existing immediate transaction unchanged. Quiesce first closes admission synchronously, then acquires the serializer, which proves any admitted transaction finished, and closes, disposes, and unregisters the connection. Repeated quiesce calls succeed without reopening anything.

Reopen holds the serializer, requires the published canonical tier to be healthy with a nonempty dataset, and first handles an already-open connection left by an interrupted quiesce. It otherwise opens and initializes a fresh connection, reads that connection's dataset generation, and compares it with `ICovenantAvailability.Current.DatasetGeneration`. Only a match is enrolled and admitted. Every partial-open failure disposes its connection and leaves admission closed.

The coordinator owns a bounded restoration token for failures proven to precede the first erasure effect. Before attempting `RollbackAndReopen`, it calls the same reopen operation against the unchanged published predecessor while still holding the gate. Only a successful restore permits rollback. A quiesce timeout, inventory failure, or second catalog-check failure whose restore also fails selects `KeepClosed` instead.

The scoped egress guard continues to depend on `ICovenantDisclosureJournal`, now delegating to the one process-wide writer. No caller receives or owns the warm connection.

## Erasure inventory and disclosure exposure

`CovenantErasureInventorySource` uses a caller-owned, unpooled, initialized maintenance read connection under the closed gate. It never opens the scoped EF/Grimoire connection, and it disposes its connection before canonical erasure can request an exclusive handle.

Inventory is a two-stage bounded protocol over deterministic `LabelId` pages of at most `CovenantProtectedArtifactErasurePage.MaxItems`:

1. a complete effect-free preflight uses one read transaction to scan every page, validate the lease-captured dataset, revalidate a factory catalog on the same snapshot when required, fold disclosure exposure, classify every label, and prove every managed label has an adopted ownership-bearing producer; it retains only the exposure and counts;
2. execution replays bounded pages: database-owned pages before canonical erasure, then managed-file pages after the canonical checkpoint. Every page call closes its owned connection before the coordinator invokes a kernel.

A crash during database replay restarts preflight and replay from the beginning while the durable phase remains `InventoryPrepared`. Already-applied items are safe to repeat, and no later page was ever retained in memory. A resume at `CanonicalApplied` exhaustively revalidates the remaining managed producers without comparing the lease's old dataset to the newly stamped unpublished candidate, then replays managed pages. At `ManagedArtifactsProcessed` and later it reads only the preserved disclosure exposure. Any replay failure after an item may have been applied is post-effect and keeps admission closed.

Label materialization is shared with `ArtifactSensitivityLedger`; the inventory does not implement a second decoder. Each label is classified through `CovenantSensitiveArtifactPurgePolicy`:

- database-owned artifacts become immutable `CovenantProtectedArtifactErasureItem` pages bound to the lease's captured dataset generation;
- managed files resolve the adopted write intent and any nonterminal local-erasure work item through a shared managed-file request reader, using the durable erasure operation id for every request;
- a managed label without an adopted, ownership-bearing producer refuses inventory before any erasure effect rather than guessing a path.

The inventory verifies that the dataset generation supplied by the exclusive lease is nonempty and matches the canonical state visible to its owned connection. It never silently truncates: both passes continue until a short page is observed.

`CovenantDisclosureExposureReader` is the single SQL fold used by both retention status and erasure inventory. It filters `RevocabilityCode = Nonrevocable`, sums joined counts, and joins count kind so one lower-bound bucket makes the total lower-bound. Erasure work and completion carry the count and kind; `ExternalDisclosuresNotRevocable` is derived from a positive count.

## Healthy-catalog factory-erasure guard

`CovenantHealthyCatalogErasureGuard.RequireHealthyAsync` opens and owns a pooling-disabled, non-immutable, initialized read-only maintenance connection. Its internal `RequireHealthyWithinAsync(SqliteConnection, SqliteTransaction, CancellationToken)` overload borrows the inventory preflight's caller-owned connection and transaction without opening, closing, reinitializing, or committing either. That overload proves catalog and dataset/label facts from the same read snapshot. Both paths validate the canonical manifest with `GrimoireSchemaManifestInspector` and require one exact healthy canonical `grimoire_feature_schemas` row whose version, source, installed fingerprint, health code, and empty diagnostic match the trusted manifest. Non-immutable mode is required here because pre-erasure catalog facts may still reside in a live WAL; the immutable sidecar-free mode remains reserved for the final post-erasure proof. The optional accelerator is accepted only when either:

- every accelerator manifest object is absent and its feature-schema metadata is absent; or
- the complete accelerator manifest validates.

Partial presence, missing metadata beside objects, missing objects beside metadata, definition drift, index or trigger drift, unexpected Covenant-owned objects, malformed catalog reads, and an unhealthy canonical tier all refuse with `Covenant.IntegrityFailure`. The safe message names all three operator remedies and contains no object name, SQL, or path.

The guard runs before the factory-erasure checkpoint is committed and again from the production inventory source while the exclusive gate is held. The second check closes the time-of-check/time-of-use window.

## Coordinator and durable recovery

`CovenantErasureCoordinator.RunAsync` no longer accepts a caller-supplied dataset generation. It derives the inventory generation from the acquired or resumed exclusive lease snapshot, which is the generation the gate closed over.

After immutable verification, publication, writer restart, disposition, writer rollback restoration, and failure-ledger recording use distinct coordinator-owned bounded cancellation rather than the caller's request token. The existing storage phases continue to honor caller cancellation; an interruption there leaves the checkpoint and closed gate adoptable.

The coordinator records the last safe completion facts and an optional closed `BlockingErrorCode` in its successful completion value. That code explains a deliberate `RollbackAndReopen` or `KeepClosed` result without exposing content and remains null after `CommitAndReopen`. It normalizes a returned failure, `OperationCanceledException`, timeout, or unexpected exception from the one-shot disposition to one content-free lifecycle failure. It then rereads the operation under a fresh bounded recording token and compare-and-swaps it to `ReconciliationRequired` with `Covenant.MaintenanceFailed`. Revision races are retried; a competing row is accepted only when a fresh read proves it is already `ReconciliationRequired` with a closed error or otherwise remains an active checkpoint the pre-readiness adopter can recover. No path makes a second gate disposition.

Recovery handlers obtain their owner id from `operation.LeaseOwner`; a missing or blank owner returns attention with a closed error instead of inventing one. They do not change the global recovery-handler contract.

The existing data-retention mutation and factory-reset recovery handlers are extended, not duplicated. Their version-3 and version-1 Covenant arms reconstruct `CovenantErasureCheckpointState`, call the production coordinator, and map:

- `CommitAndReopen` to `Completed`;
- `RollbackAndReopen` to `Failed` with `BlockingErrorCode` or `Covenant.MaintenanceFailed`;
- `KeepClosed` to `ReconciliationRequired` with `BlockingErrorCode` or `Covenant.MaintenanceFailed`;
- a failed coordinator result to `ReconciliationRequired` with its closed error code.

Legacy checkpoint versions retain their current recovery behavior.

### Pre-readiness owner reconstruction

A new bootstrap recovery pass queries the two existing data-retention kinds through the already-open initialized install connection before ordinary readiness. It considers only nonterminal rows and bounded current checkpoint versions, decodes them through `CovenantRecoveryCheckpointCodec`, and calls `CovenantOperationGate.AdoptDurableRecoveryOwner` with the exact checkpoint owner. A version-3 mutation without a Covenant arm and legacy factory-reset checkpoints are ignored because they closed no Covenant scope. Malformed Covenant evidence, two conflicting installation owners, or adoption failure refuses readiness with content-free operator guidance.

The adopter validates the closed recovery policy, exact operation-id and checkpoint-reference formats, payload size, operation code, digest, and phase while streaming rows; it retains at most one parsed owner. The gate exposes one typed resume-or-acquire operation for the first phase: it resumes an exact adopted closure, acquires only after atomically observing no closure, and refuses an owner mismatch without taking the acquisition arm. Later phases remain resume-only. `Covenant.MaintenanceFailed` attention rows are explicitly eligible in the existing store's expired/lease predicates, so durable recording does not create an operation the reconciler can never select.

Schema-repair startup recovery distinguishes a successfully adopted `KeptClosed` owner from a read, decode, or adoption failure. Only the former permits bootstrap to continue toward readiness; the latter refuses readiness. This prevents the readiness freeze from making a missing closure permanent.

For a bootstrap that owns the installation lock, the erasure adopter runs after restore-authority recovery, followed by an effect-free schema-repair journal read/decode/adoption prepass. Only after both durable owner sources have been validated and at most one exact owner has been installed may local-erasure or schema-repair recovery perform an effect. This exposes conflicting durable installation owners before either protected recovery can act while preserving the established local-erasure-before-schema-repair execution order. The later schema-repair execution consumes the prepass result and neither rereads nor adopts its owner a second time. After every adopter finishes and protected recovery completes, bootstrap calls `CovenantOperationGate.PublishReadiness()` immediately before `IGrimoireDbReadiness.MarkReady()`. This closes the previously missing production boundary: no later code may synthesize an in-memory closure from a durable row. Host startup reconciliation can then lease the row and resume the coordinator; a standalone lock-owning CLI safely observes the reconstructed closure and cannot admit ordinary Covenant work. A CLI coexisting with a lock-owning host is not a recovery authority and does not claim or freeze that pre-readiness boundary in its separate process.

## Same-process acceptance proof

A file-backed SQLCipher integration fixture builds the real canonical and optional accelerator tiers, one real composite runtime-generation provider, real envelope codec, real operation gate, production transition, production writer lifecycle, inventory, status service, and coordinator. Without recreating the service provider or process, the successful test:

1. seeds old canonical content and a disclosure writer connection;
2. captures an old read lease, operator context, and one token from each of the six purpose families;
3. runs the coordinator;
4. observes admission closed inside the publication callback;
5. proves the old lease, context, and all six tokens are rejected;
6. observes one atomic fresh runtime generation with the new dataset and proves the root/provider/writer singleton identities did not change;
7. obtains fresh status through the real retention-status service and fresh read/write leases;
8. performs a real canonical mutation and read through `CovenantMutationKernel` and `CovenantStore`;
9. seeds fresh content, begins a real inference context through `CovenantContextProvider`, observes the fresh content, and proves old content is absent;
10. commits a disclosure acknowledgement through the restarted warm writer.

Fault tests cover publication failure, writer restart and rollback-restoration failure, failed/cancelled/throwing one-shot completion, every same-process resume phase, catalog damage including WAL-resident drift, mixed disclosure buckets, stale publication races, composite-publication interleavings, partial key derivation, key-copy retirement races, partial writer-open cleanup, and bounded inventory beyond two pages. A fresh-process fixture proves pre-readiness owner adoption and recovery from every phase and the post-proof crash boundaries without opening ordinary admission.

## Registration and public boundary

Both Grimoire compositions register exactly one singleton maintenance connection factory, composite runtime-generation provider, warm disclosure writer, connection drain, key provider, authority provider, availability provider, and operation gate. Scoped registrations own the inventory source, erasure transition, erasure coordinator, operation ledger, and kernels. The full host composition owns the existing data-retention recovery services and startup reconciler; the lightweight CLI composition owns the pre-readiness adopter and closed gate but does not duplicate the host-only `DataRetentionService` graph.

Architecture tests resolve both service graphs, prove shared singleton identity where required, prove the scoped components do not escape their scope, and prove the reset/factory public routes remain refused.

## Documentation

The landed change updates:

- `README.md` for cumulative Covenant status and the remaining route-integration boundary;
- `docs/Arcanum.DESIGN.md` for lifecycle, persistence, authority publication, recovery, and test architecture;
- `docs/Arcanum.Design.Human.md` for the same behavior in operator-oriented language;
- `docs/Arcanum.OATH.md` and `docs/ArcanumOATH.Human.md` for delivery and invariant status;
- `docs/Arcanum.DEBUGGING.Human.md` for breakpoints and recovery recipes;
- `docs/Arcanum.API.md` and `docs/Arcanum.Command.Reference.md` only to correct the stale statement that no coordinator exists while preserving the route refusal.

No config, command-map, generated API payload, constraint inventory, or historical review document changes are expected.

## Verification

Completion requires all of the following from the merged `long-term-memory` worktree:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
```

The build must report zero warnings and zero errors. Focused red/green evidence is retained in the task transcript for every behavioral slice, and the final diff receives specification-compliance and code-quality reviews before integration.
