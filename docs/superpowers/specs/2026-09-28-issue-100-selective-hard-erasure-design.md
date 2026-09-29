# Issue #100: Selective hard erasure with keyed resurrection suppression

**Status:** Designed on 2026-09-28. The operator approved the design before any code was written.

**Branch:** `claude/issue-100-selective-erasure`, cut from `memory-enhancement` at `af212a90`. It is merged back with `--no-ff` after qualification, and then `memory-enhancement` is merged to `main`.

**Issues:**
- #100 is an XL delivery slice under #78. Its blockers #87, #90, #94, #96, #97, #98 and #102 are closed.
- #225, the remaining blocker, is finished here.
- This change also closes #78's remaining gaps so that #78 can close.
- Work #78 carved out is recorded rather than delivered: agent recall is #101, and Long Rest consolidation and decay are #93 and #95.

**Names settled with the operator:**
- **Erasure** permanently removes one exact memory item and every local artifact derived from it that the store owns.
- An **erasure fingerprint** is the content-free, installation-keyed evidence an erasure leaves behind, so that automatic writers and restores cannot bring the item back.
- **Release** lifts one erasure fingerprint when the operator supplies the plaintext identity or content it covers.

These are plain descriptive names. The operator waived thematic naming for this slice.

## 1. Objective

An operator can retire a memory today, which keeps it inspectable and reversible. An operator can also delete a Saga memory or a Lexicon entry, but that leaves nothing behind to stop the next extraction pass, agent write, or backup restore from recreating it. No path removes one Covenant entry's history at all.

This slice adds the missing promise: exact, confirmed, single-store erasure of one Saga memory, one Lexicon entry, or one Covenant entry. It has four properties:

- **Verified locally.** The local removal is verified and reported honestly.
- **Remembered.** The erasure is recorded as content-free keyed evidence that survives every lifecycle path except a full installation reset.
- **Enforced.** That evidence is enforced at every automatic write chokepoint and inside restore staging.
- **Honest about what leaves.** The result reports which copies Arcanum cannot revoke because they already left the machine, and which local copies it deliberately does not touch.

## 2. Scope, settled before design

The operator settled five questions before design.

1. **Key custody: the OS credential store.** The fingerprint HMAC key lives in the macOS Keychain or Windows Credential Manager, under the same custody as the Campaign root-identity key. It is never written to the Grimoire or to any backup. Only a full installation reset removes it.
   - An archive therefore can never be used to test guesses about erased content.
   - Two installations restored from one archive never share a key.
   - Cost: after disaster recovery onto a fresh installation, older fingerprints no longer match.
2. **Lifetime: fingerprints survive everything except a full installation reset.** That includes retention pruning, store deletes, `DELETE /api/saga`, memory and embeddings resets, Campaign and Session deletion, Covenant family reset and reinitialize, and factory reset. Disclosure receipts already survive factory erasure; fingerprints follow that precedent.
3. **Equivalence is identity-level wherever a store has an identity, plus a release verb.** Erasure is never weaker than retirement.
   - **Saga:** a fingerprint covers the exact content in the exact scope. Saga has no identity beyond its text.
   - **Lexicon:** it covers the normalized name in the exact scope.
   - **Covenant:** it covers the normalized key in the exact scope.
   - **Who is blocked:** agents and automatic writers. Operator writes are never blocked.
   - **Release:** a per-store verb removes one fingerprint when given the plaintext identity or content.
4. **Plain names.** No new thematic vocabulary.
5. **One delivery.** Saga, Lexicon and Covenant erasure, restore suppression, the adjacent defect fixes, and #78's remaining gaps ship together. The operator was offered a split into two deliveries and declined it.

Settled by design and approved with it:

- **Legacy deletes keep their meaning.** `DELETE /api/saga/{id}`, `DELETE /api/saga`, `DELETE /api/memory/lexicon/{name}`, `arcanum saga delete` and `arcanum memory lexicon delete` remain deletes without suppression. Their documentation and messages now say so. Only `erase` suppresses and verifies.
- **Covenant erases the whole entry.** A Covenant entry keeps every set version's plaintext, in a chain linked through a non-cascading predecessor key under append-only guards. The erasure unit is therefore the whole entry: both lanes, every version. It is bound to the current head of each lane the operator inspected.
- **Saga erases the whole identical-content class in scope.** A Saga erasure also removes every other memory holding byte-identical content in the same scope. The preflight discloses these twins and the effect digest binds them. A fingerprint covers content, not one row; leaving a twin would leave the plaintext locally and make restore semantics disagree with live semantics.
- **Covenant erasure drains in-flight turns.** Saga and Lexicon have no such drain.

## 3. Governing constraints

- **Core v13 starts from a pinned v12 fingerprint.** Core schema v12 publishes the normalized source fingerprint `616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B`. Core v13 pins it before any head object changes. A `CoreSchemaVersionTwelveFixture` reconstructs the v12 tree and a test proves it hashes to that pin.
- **Covenant canonical v6 starts from a pinned v5 fingerprint.** Canonical v5 publishes the **raw** source fingerprint `E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925`. Canonical v6 pins it, and a `CovenantCanonicalSchemaVersionFiveFixture` freezes the byte-exact v5 text of every object v6 edits. The canonical tier keeps the raw computation.
- **Core v13 has no backfill sweep.** A step without a sweep finalizes in the same transaction, including under CLI bootstrap and restore staging.
- **`ALTER TABLE … ADD COLUMN` reproduces SQLite's splice layout.** Every edited head file must match what the ALTER produces, and fresh and evolved catalogs must converge exactly.
- **Triggers are replaced with an explicit drop.** A trigger rewrite uses `DROP TRIGGER IF EXISTS` followed by `CREATE TRIGGER IF NOT EXISTS`. A `CREATE … IF NOT EXISTS` alone is a no-op on evolved catalogs.
- **Raw SQL follows the declarative schema tree.** One object per file. No EF entity, no numbered migration, no compiled-model change. Every application read and write is parameterized direct SQLite, as the EF/Native-AOT boundary tests require.
- **Native AOT and API conventions apply throughout.**
  - Every `/api` payload is registered on `ArcanumJsonContext`, and every CLI payload on `CliJsonContext`.
  - Every failable `Results.Json` passes explicit `JsonTypeInfo`.
  - Wire enums are string-only.
  - No `[JsonPropertyName]` and no anonymous DTOs.
  - Configuration objects use `{ get; set; }`. No configuration key is added.
- **Errors use `Result` / `Result<T>`.** Domain operations return them, and only the endpoint converts a result into an envelope and a status code.
- **Erasure reads and writes no plaintext outside the rows it deletes.** Nothing new stores plaintext. Every stored digest a guess could be tested against is keyed.
- **Documentation under `docs/` names capabilities, never issue numbers.** Tracker references are allowed only in `docs/Arcanum.OATH.md`, `docs/Arcanum.Engineering.md` and `docs/superpowers/**`. The root `README.md` does not change.
- **Linux is not a shipping RID.** The shipping targets, macOS and Windows, both have an OS credential store.

## 4. Considered approaches

### 4.1 Key custody

**Rejected: reuse `saga_suppression_key`.** That key lives in the Grimoire, so it has three problems:

- Every backup carries it together with its digests, which makes each backup an offline confirmation oracle.
- A restore adopts it verbatim on another machine.
- A whole-store Saga reset deletes it along with every retirement digest.

**Rejected: an in-database keyring that rotates on every restore.** It survives disaster recovery, but it still puts the key and the fingerprints in every archive, and installations cloned from one archive share inherited fingerprints.

**Selected: an OS-held key with a per-row key identifier.** Every fingerprint row records `KeyId`, an HMAC of a fixed label under the key. A lost or foreign key is therefore detectable, and the system fails closed instead of silently allowing resurrection.

### 4.2 Evidence storage

**Rejected: reuse `saga_retirement_suppressions`.** Reinstatement deletes retirement digests by recomputing them, and whole-store and Campaign Saga resets clear them. Either would silently release an erasure. That table also puts scope text into the preimage, which forces a permanent two-spelling compatibility pair.

**Selected: one new store-agnostic Core table.** It holds only a fingerprint, an identity kind and a key identifier. Scope lives inside the keyed preimage as Campaign GUID bytes, so spelling cannot matter.

### 4.3 Restoring an archive older than the schema that holds the evidence

**Rejected: install the v13 table in staging "regardless".** It places a head object ahead of its step and forces every later step to be idempotent DDL.

**Rejected: carry the rows across the swap in the restore journal.** The payload is a frozen format, and the approach leaves a live window with no evidence.

**Selected: drain the staged sweeps first, or refuse.** When the destination holds evidence, restore staging drains the staged Grimoire's pending schema sweeps to head before applying that evidence. If the staged database cannot reach head, the restore is refused before anything is displaced.

### 4.4 Stopping in-flight Covenant turns from re-sending erased text

**Rejected: per-entry invalidation checked at revalidation.** No inference path revalidates its turn lease today, and any check-then-send leaves a race.

**Selected: an exclusive drain.** A new exclusive operation drains every turn that could hold the bytes before the erase commits: a Campaign closure for a Campaign entry, and an Installation closure for a Global entry or a key reclamation.

### 4.5 Deleting guarded Covenant canonical rows

**Rejected: borrow `OwnerCleanup`.** It opens 24 Core guards, including disclosure receipts and sensitivity labels, which an erase must never reach. It also does not open the search-outbox guard.

**Selected: a new narrow authorization kind, `CovenantEntryErasure`.** Canonical v6 teaches the relevant guards to accept it.

## 5. Erasure fingerprints

### 5.1 The key

**Account.** The key lives under the fixed account `memory-erasure-fingerprint-key`, service `arcanum`, stored as 32 random bytes in canonical unpadded base64url.

The account is deliberately **not** profile-namespaced:
- The profile namespace hashes the physical identity of the profile root's parent directory, so recreating `~/.config` would silently lose the key.
- Production has exactly one profile per OS user.
- Sharing the account across test homes is caught by the per-row `KeyId`.

**Interfaces.**
- `IMemoryErasureKeyProvider` (Core) opens the existing key only. Chokepoints, release, status and restore use it.
- An internal `IMemoryErasureKeyCreator` (Infrastructure) can create the key. Only the erase path calls it, and it is registered only in the host container.

**Creation rules.**
- The key is created only on a proven `NotFound`, and only when the caller has proven that no fingerprint rows exist.
- If rows exist but the key is `NotFound`, the result is `MemoryErasure.KeyLost`. A fresh key is never minted over existing evidence.
- A create writes the key, reads it back, and compares in constant time.
- A malformed value is never overwritten.

**Caching.** The state is cached per process: `Unresolved`, `Present`, `Absent` or `Failed`.
- Automatic callers use the latched outcome.
- Operator-initiated calls re-probe any state other than `Present`.

**Handling and I/O.**
- Key material is copied out into a disposable `MemoryErasureKey`, which zeroes itself on dispose.
- There is no keychain I/O at composition or construction time.
- Callers resolve the key before opening a write transaction, so an OS prompt can never hold the SQLite write lock.

**Removal.** The account joins the fixed seed of `InstallationResetCredentialCatalog` and the `VerifyIdentitiesRotated` identity check. A full installation reset (`--global` or `--all`) therefore removes both the key and the Grimoire. `BackupSecretSnapshotReader` never reads the account, and a source-inventory test pins that.

### 5.2 Key identifier and fingerprint grammar

**Key identifier.**

```
KeyId       := HMAC-SHA256(K, ASCII "Arcanum.MemoryErasure.KeyId.v1" || 0x00)[0..16]
```

**Fingerprint.**

```
Fingerprint := HMAC-SHA256(K, preimage)
preimage    := ASCII "Arcanum.MemoryErasure.Fingerprint.v1" || 0x00
               || u8  IdentityKind        (1 SagaContent, 2 LexiconName, 3 CovenantKey)
               || u8  ScopeKind           (Saga: 0 Unclassified, 1 Global, 2 Campaign, 3 LegacyUnresolved;
                                           Lexicon and Covenant: 1 Global, 2 Campaign)
               || [16] Campaign GUID, RFC 4122 big-endian — present iff ScopeKind = 2
               || u32 big-endian byte length || strict UTF-8 identity bytes
```

**Identity bytes for each kind:**
- **Saga:** the exact stored `Content`.
- **Lexicon:** `NameNormalized`, meaning `Trim().ToUpperInvariant()` of the name.
- **Covenant:** `NormalizedKey`.

**Encoding rules.**
- Strict UTF-8 rejects unpaired surrogates.
- Any other scope/Campaign pairing throws.
- Other keyed digests in this slice (request, effect, subject) use the same key under their own domain labels: `Arcanum.MemoryErasure.Request.v1`, `…Effect.v1` and `…Subject.v1`. Each is length-prefixed in the same way.

### 5.3 Storage (Core v13)

```sql
CREATE TABLE IF NOT EXISTS memory_erasure_fingerprints (
    Fingerprint BLOB NOT NULL PRIMARY KEY CHECK (length(Fingerprint) = 32),
    IdentityKindCode INTEGER NOT NULL CHECK (IdentityKindCode IN (1, 2, 3)),
    KeyId BLOB NOT NULL CHECK (length(KeyId) = 16)
);
CREATE INDEX IF NOT EXISTS idx_memory_erasure_fingerprints_kind ON memory_erasure_fingerprints(IdentityKindCode);
```

What the table deliberately leaves out:
- No memory identity, scope column or timestamp. The absence of a timestamp keeps the table out of the UTC-instant inventory.
- No delete guard trigger. The only code allowed to delete rows is:
  - the release path;
  - restore staging's destination-authoritative join;
  - full installation reset, which deletes the file.

  A source-scan architecture test pins that set.

**Lifecycle membership.**
- The table is **absent** from every memory-reset selection, the reset residue lists, the factory-reset plan and deletion lists, and `BackupRestoreProtectedStateInspector.CanonicalContentTables`.
- It is **present** in the retention status inventory as a content-free count under a new never-aged retention class. That class has an explicit null rule arm.

### 5.4 Chokepoints

Every chokepoint runs inside the store's existing write transaction:

1. `SELECT EXISTS(… WHERE IdentityKindCode = @kind)`. When it is false, skip. An installation that never erased anything never touches the keychain.
2. Otherwise resolve the key, which was already resolved outside the transaction.
3. Compute the fingerprint and look it up.
4. If rows exist but the key is unavailable or lost, **fail closed**.

Each store:

| Store | Chokepoint | Refusal |
|---|---|---|
| Saga | `SagaMemoryStore.InsertCoreAsync`, after the retirement-suppression check and before the insert. Scope comes from the Session classifier; the Campaign is parsed to a GUID. | Returns the existing `SagaMemoryWriteOutcome.Suppressed`, which extraction already counts as progress. A missing key throws a typed exception, which extraction turns into a retry that holds its cursor. |
| Lexicon | `LexiconService.UpsertCoreAsync`, under `BEGIN IMMEDIATE`, after the exact-scope read. The check runs whether or not a row exists. | New `Lexicon.SuppressedNameRefused` (409), with the tool message "This Lexicon name is not available to agent memory in this scope, so nothing was recorded." |
| Covenant (authoritative) | `CovenantMutationKernel.ApplyIntentAsync`, directly after the pin check, for origins `AgentProposed` and `AgentApproved`. The key and the "any Covenant fingerprints" flag are resolved once per batch, and only when the batch carries an agent intent. | The frozen `Covenant.ForbiddenAuthority`, with "This Covenant key is not available for agent proposals in this scope." |
| Covenant (courtesy) | `ResolveProposedLaneAsync` and `CovenantTurnHeadProbe.ResolveRetirementPreflightAsync`, through a new `IsAgentWithheld` on the lane probe. The staging handler now also honors the existing `IsPinned`, which it currently ignores. | Refused at staging, so a turn's reply is never discarded at publication. |

Operator writes are never checked. These include Saga and Lexicon correction and reinstatement, #99 review decisions, and Covenant operator set, correct and retire. The Covenant operator set result and the docs say that agent authorship of an erased key stays withheld until a release.

### 5.5 Release

Release is operator-only, confirmed, authenticated and store-scoped.

- It recomputes the fingerprint from the supplied scope and identity (or content), then deletes that row.
- Its outcome is `Released` or `NotFingerprinted`.
- It never creates the key. With no key, the outcome is `NotFingerprinted`.
- A release names exactly one store and changes nothing else.

## 6. The erase protocol

Every store follows the same five-part shape, and differs only in its exact target (sections 7-9).

### 6.1 Prepare

Prepare is an authenticated POST whose body names the exact target. The CLI obtains that target from the store's `show`, so the operator never transcribes hashes or version identifiers.

Prepare does the following:
- re-reads the target in one read snapshot;
- measures the plan with the same predicates apply will delete by (a `Count` mode of the shared plan runner);
- computes external exposure and retained local copies;
- computes the keyed request and effect digests;
- issues a five-minute preflight token.

It creates the key on first use, and only when no fingerprint rows exist.

**Tokens:**
- **Saga and Lexicon** use `MemoryReviewTokenCodec` with a new `ErasurePlan` purpose. The payload holds the store, request digest, effect digest, and dataset generation only when a label was measured. This path works with the Covenant feature off.
- **Covenant** uses `CovenantEnvelopeCodec` purpose `OperatorPreflight` with a new `CovenantErasurePreflightBody`. That body has its own format byte, so it can never decode as a set, correct or curate body, and none of those can decode as it. It carries:
  - the request digest;
  - the authority epoch;
  - the dataset generation;
  - the key epoch;
  - the key-reclamation epoch;
  - the entry and both head version identities;
  - the effect digest;
  - the issue and expiry times.

**Response:** `MemoryErasurePreflightDto`, carrying:
- the store;
- `MutationId`;
- the request and effect digests (hex);
- the plan, as content-free counts plus flags;
- external exposure;
- retained local copies;
- issue and expiry times;
- the token.

### 6.2 Apply

1. Validate the body shape, then compute the request digest from the request fields alone.
2. **Receipt first.**
   - If a `memory_erasure_receipts` row exists for `MutationId` and its digest matches in constant time, replay it.
   - If the row exists and the digest differs, return `Security.IdempotencyConflict` (409).
   - A replay also retries a pending scrub (section 10). A replay needs no token, so it survives token expiry and host restarts.
3. Decode the token and require that its request digest equals the one just computed.
4. Take the store's leases (sections 7-9).
5. Open one `BEGIN IMMEDIATE` transaction, wrapped in `SqliteBusyRetry`:
   1. re-read the exact target;
   2. re-measure the plan, exposure and retained copies, and recompute the effect digest; a mismatch refuses with `MemoryErasure.StalePlan` (409);
   3. delete through the plan;
   4. insert the fingerprint with `INSERT OR IGNORE`;
   5. insert the receipt;
   6. re-count every plan target inside the transaction and require zero;
   7. revalidate any held lease;
   8. `COMMIT`.
6. Run the post-commit scrub (section 10).
7. Return `MemoryErasureResultDto`, which is identical to a replay apart from `Replayed`.

### 6.3 Receipt (Core v13)

`memory_erasure_receipts` has these columns:

| Column | Holds |
|---|---|
| `MutationId` | Primary key; governed, uppercase and dashed |
| `StoreCode` | 1 Covenant, 2 Saga, 3 Lexicon (the `MemoryReviewStore` codes) |
| `RequestDigest` | Keyed, 32 bytes |
| `SubjectDigest` | Keyed over the store and subject identity, 32 bytes; never the identity itself |
| `EffectDigest` | Keyed, 32 bytes |
| `ErasedItemCount` | ≥ 1 |
| `RemovedRowCount` | ≥ 1 |
| `RemovedLabelCount` | ≥ 0 |
| `AuthorshipEvidenceCode`, `ContextEvidenceCode`, `EmbeddingEvidenceCode`, `BackupEvidenceCode`, `OtherExternalEvidenceCode` | Each `IN (1, 2, 3, 4)` |
| `RetainedCopiesMask` | ≥ 0 |
| `ScrubStateCode` | 1 Pending, 2 Verified |
| `ScrubPendingReasonMask` | ≥ 0 |

**Guard.** An update guard permits exactly one transition: `ScrubStateCode` from 1 to 2, with the reason mask cleared. The table has no timestamps.

**Lifetime.** Receipts survive exactly what fingerprints survive.

**A later request against an erased subject.**
- A new `MutationId` whose subject digest matches a receipt is refused with `MemoryErasure.SubjectErased` (410).
- A Lexicon or Covenant prepare that finds no row but a matching fingerprint gets the same answer.

### 6.4 Results

`MemoryErasureResultDto` carries:
- the store;
- `MutationId`;
- `Replayed`;
- the effect digest;
- `Local`, which has:
  - `Outcome`: `Verified` or `RowsRemovedScrubPending`;
  - `PendingReasons`;
  - `ErasedItemCount`;
  - `RemovedRowCount`;
  - `RemovedLabelCount`;
  - `SuppressionFingerprintRecorded`, which is always true;
- `External`: `Revocation = NotPerformed` plus one evidence value per channel;
- `RetainedLocalCopies`.

**Closed string enums:**

| Enum | Values |
|---|---|
| `MemoryErasureStore` | Reuses `MemoryReviewStore` |
| `MemoryLocalErasureOutcome` | `Verified`, `RowsRemovedScrubPending` |
| `MemoryErasureScrubPendingReason` | `WalCheckpointBusy`, `WalCheckpointUnavailable`, `FullTextSecureDeleteUnverified`, `VectorIndexScrubUnverified` |
| `MemoryExternalChannel` | `InferenceProviderAuthorship`, `InferenceProviderContext`, `EmbeddingProvider`, `EncryptedBackup`, `OtherExternal` |
| `MemoryExternalEvidence` | `Known`, `ReceiptWindow`, `NotRecorded`, `NotApplicable` |
| `MemoryExternalRevocation` | `NotPerformed` |
| `MemoryRetainedLocalCopy` | `SourceTranscript`, `DerivedSummaries`, `ToolCallArguments`, `BackupArchives`, `AuditLog` |

No value means "not disclosed". No DTO carries content, a content hash, a provider name, a receipt count, or anything that joins against a receipt or a fingerprint.

## 7. Saga erasure

**Target.** `SagaErasePrepareRequest(string MemoryId, string ExpectedContentHash, string? ExpectedClaimVersionId, Guid MutationId)`.
- A null version means the memory is claimless, and it must still be claimless at apply.
- A non-GUID memory id is refused as an invalid target.

**Plan.**
- The target plus every twin: same scope, byte-identical `Content`, found by seeking `idx_saga_memories_scope`. No content index is added.
- For each item, the Saga row of `CovenantArtifactPurgePlans`: the full Annals chain including review events and receipts, both embedding tables through a catalog probe, and attachment provenance.
- Any sensitivity label.
- The retirement-suppression digest pair for the content and scope. It is computed with the shared helper, and the retirement key is read, never created. `saga_suppression_key` and the extraction watermarks are untouched.

**Leases.**
- When a label is present, apply acquires a `CovenantWriteLease` over the owner scope **before** `BEGIN`, forms `CovenantArtifactErasureAuthority.ForOrdinary`, re-reads the exact label in the transaction, and deletes it under `SensitivityRetentionPurge` authorization.
- When no label is present, the transaction re-proves that none has appeared since prepare.
- The routes carry `.RequireConditionalSensitivityRetentionPurge()`.

**Pins and retirement.** A pin does not block an erase: pins bind automatic paths only. A retired memory can be erased.

**Scope of the fingerprint.** The fingerprint uses the row's scope. An exact-scope fingerprint means content erased in Campaign A is still extractable in Global scope or in Campaign B. That is the same boundary retirement has, and the docs state it.

## 8. Lexicon erasure

**Target.** `LexiconErasePrepareRequest(LexiconCurationTarget Target, Guid MutationId)`, with the complete show target. The existing `TargetsEqual` compare-and-swap runs under `BEGIN IMMEDIATE`, and Core must be at least 13.

**Refusals.**
- `daemon_state:*` names are refused with `Lexicon.InvalidName` (400), matching `delete_lexicon`'s existing refusal. A fingerprint on one would permanently break the daemon that rewrites that name every cycle.
- Twins cannot exist, because `(ScopeCampaignId, NameNormalized)` is unique.

**Plan.**
- The Lexicon row of `CovenantArtifactPurgePlans`: the Annals chain including historical fact provenance, and current fact provenance.
- The entry row. Its delete trigger removes the FTS tokens.
- Any label, under the Lexicon exact-scope `CovenantWriteLease` pattern.

**FTS scrub.** Inside the transaction, before the delete, `lexicon_fts_config` must read `secure-delete = 1`. If it does not, the transaction enables it and reads it back. If it still is not 1, the outcome carries `FullTextSecureDeleteUnverified`.

**Routes.**
- Prepare carries `.RequireConditionalCovenantReadAuthority()`.
- Apply carries the purge metadata only. The purge path owns the single write lease.

## 9. Covenant entry erasure

### 9.1 Target and authority

**Target.** `CovenantErasePrepareRequest(CovenantScope Scope, Guid? CampaignId, string Key, Guid EntryId, CovenantEraseHeadExpectation? Confirmed, CovenantEraseHeadExpectation? Proposed, Guid MutationId)`, with `CovenantEraseHeadExpectation(Guid VersionId, long LaneRevision)`. The CLI reads it from `covenant show`.
- A Global entry with a `Proposed` expectation is invalid.
- A version identity pins its rendered hash, because versions are immutable.

**Authority.** Both routes carry `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)`.
- Prepare holds a `CovenantInstallationReadLease` through `CovenantProtectedJsonResult`.
- Apply never hands an exclusive lease to a protected result. It writes `Results.Json` with the protected-header hook.

### 9.2 Drain

`CovenantExclusiveOperation.CovenantEntryErasure = 9` and `CovenantLeaseKind.EntryErasure = 11` are new.

**Acquisition.** `ICovenantOperationGate.AcquireEntryErasureAsync(entryScope, owner, reclaimsKey, ct)` returns a compound read-and-exclusive `CovenantEntryErasureLease`. The slot mapping is structural, so a caller cannot choose GlobalScope for a Global entry:

| Entry | Closure |
|---|---|
| Campaign, key not reclaimed | Campaign closure |
| Global, or any entry whose erase reclaims the key | Installation closure |

**Existing guards.**
- Every existing exclusive-acquisition deny-list becomes an allow-list, so that code 9 is refused everywhere except this method.
- `ClassifyOwner` refuses code 9, because the operation has no durable owner.

**Sequence.**
1. Probe the receipt under a short read lease, and dispose that lease before acquiring. Holding it would make the drain wait on its own lease.
2. The owner is `CovenantExclusiveRecoveryOwner(MutationId, CovenantEntryErasure, effectDigest)`.
3. After the drain, the lease's authority generation and epoch must equal the request's authority context.

**Dispositions.** Every disposition is completed with `CancellationToken.None`.

| Situation | Disposition |
|---|---|
| Success | `CommitAndReopen` |
| Proven pre-commit refusal | `RollbackAndReopen` |
| Commit uncertain | Re-read the receipt: present means `CommitAndReopen`, absent means `RollbackAndReopen`. If the re-read fails, `KeepClosed` plus `Covenant.ManualRecoveryRequired`. |

The erase publishes no availability, generation or authority transition, so its evidence is `HealthPublished: true` by construction.

A drain that does not finish within the gate's 30-second bound fails as it does today (503) and changes nothing.

### 9.3 Transaction

The transaction runs under `CovenantEntryErasure` authorization and `SqliteBusyRetry`. It first verifies:
- dataset generation;
- key-reclamation epoch;
- key epoch;
- both heads equal their expectations;
- for a Campaign entry, the live Campaign row.

It then deletes, in this order:

1. `covenant_search_documents` by `EntryId`, synchronously, when the accelerator exists and `covenant_fts` reads `secure-delete = 1`. Otherwise it records `FullTextSecureDeleteUnverified`.
2. Pending `covenant_search_outbox` rows for the entry's `SearchRowId`s. One absent delta per erased head is then appended at `CanonicalSearchSequence + 1`, and the sequence advances once.
3. `covenant_mutation_receipts` for the entry:
   - by the new `EntryId` column;
   - by `ResultingVersionId` in the entry's versions;
   - legacy `NoChange` rows with a null `EntryId` in the same scope, Campaign and lanes, committed at or after the entry's `CreatedAtUtc`. These are removed conservatively and counted.
4. `covenant_version_attachment_provenance`, then `covenant_heads` (the key-epoch trigger fires), then `covenant_versions` in one statement (review events and decision receipts cascade), then `covenant_entries`.
5. Curation rows for the erased subject: its scope, Campaign and key, both lanes, every epoch. Heads go before versions, and receipts are matched without regard to epoch. **Exception:** a Campaign Confirmed-lane subject whose current head is masked is kept. That mask is operator policy about the Global key, not erased content; the preflight reports it as `RetainsCampaignMask`.
6. Key reclamation, when no `covenant_heads` or `covenant_entries` row in any scope still names the key:
   1. delete every curation row for the key in every scope;
   2. delete the `covenant_key_epochs` row;
   3. compare-and-swap `KeyReclamationEpoch + 1`.

   The preflight states `ReclaimsKey` and warns that outstanding Covenant preflights go stale installation-wide.

Review markers, turn receipts, disclosure receipts and labels on derived artifacts are not touched.

### 9.4 Plan facts

The plan reports:
- versions per lane;
- provenance leaves;
- mutation receipts;
- curation rows;
- outbox rows;
- search documents;
- `ReclaimsKey`;
- `RetainsCampaignMask`;
- `GlobalConfirmedResurfaces`, for a Campaign entry whose key also exists as a live Global Confirmed entry;
- `IsPinned`;
- the number of affected Campaigns.

## 10. Verified local erasure

`Local.Outcome = Verified` requires **all** of the following:

- **(a)** The erase transaction committed.
- **(b)** A post-commit absence proof passed. It is a fresh read transaction that re-counts every plan target by key and finds zero, and finds the fingerprint row present. If (b) fails, the result is not "pending": it is the failure `MemoryErasure.ErasureIncomplete`, because rows remain.
- **(c)** Every FTS index the plan touched reads back `secure-delete = 1`: `lexicon_fts` for Lexicon, and `covenant_fts` when the accelerator is installed.
- **(d)** Every vector mirror the plan touched is absent or proven scrubbed. A legacy `vec0` virtual-table mirror is unreachable residue, so it yields `VectorIndexScrubUnverified`.
- **(e)** A checked `PRAGMA wal_checkpoint(TRUNCATE)` returns `busy = 0` and no remaining frames. It runs on a dedicated, unpooled `GrimoireOrdinaryFreshConnectionKind.ReadWrite` connection, with `busy_timeout = 250`, after every transaction of the request has been disposed. For Covenant it runs after the closure reopens.

Anything short of that is `RowsRemovedScrubPending` with closed reasons.

The receipt moves from Pending to Verified only when (a) through (e) hold. Replaying the same apply re-runs only (b) through (e), and may upgrade the result.

The wording never claims physical erasure. Page-level `secure_delete = ON`, which every Grimoire connection already verifies, together with WAL truncation are logical scrubs, as DESIGN §5.4.7 already states.

## 11. External exposure and retained local copies

Evidence is computed in prepare, recomputed in apply before any delete, bound into the effect digest, and persisted on the receipt. Counts are never bound or returned.

| Store | Channel | Rule |
|---|---|---|
| Saga | `InferenceProviderAuthorship` | `Known` if any version of the claim has origin `AgentAsserted` or `AgentExtracted`; otherwise `NotRecorded` |
| Saga | `InferenceProviderContext` | `NotRecorded` (prompt injection and `read_saga` are not journalled) |
| Saga | `EmbeddingProvider` | `Known`, by construction: every insert and every correction embeds |
| Saga | `EncryptedBackup` | `ReceiptWindow` if at least one backup operation's latest receipt is at or after the memory's `CreatedAt`; otherwise `NotRecorded` |
| Lexicon | `InferenceProviderAuthorship` | as Saga |
| Lexicon | `InferenceProviderContext` | `NotRecorded` |
| Lexicon | `EmbeddingProvider` | `NotApplicable` |
| Lexicon | `EncryptedBackup` | as Saga, windowed from the claim's creation. Unbounded when claimless. |
| Covenant | `InferenceProviderAuthorship` | `Known` if any version origin is agent |
| Covenant | `InferenceProviderContext` | `ReceiptWindow` if any nonrevocable, Covenant-derived provider-dispatch receipt is at or after the entry's `CreatedAtUtc` (time window only, no generation predicate); otherwise `NotRecorded` |
| Covenant | `EmbeddingProvider` | `NotApplicable` |
| Covenant | `EncryptedBackup` | as Saga, windowed from the entry's creation |
| All | `OtherExternal` | `NotRecorded` |

**Retained local copies** are reported, never purged.

| Copy | When reported |
|---|---|
| `SourceTranscript` | Saga, when the owning Session still exists |
| `DerivedSummaries` | Saga, Lexicon and Covenant |
| `ToolCallArguments` | Lexicon and Covenant, whose writes arrive through tool calls recorded in transcripts |
| `BackupArchives` | Always |
| `AuditLog` | Only when the inference audit log is enabled with unredacted tool arguments |

**CLI order.** The CLI prints `CovenantExternalRetentionDisclosure.DestructiveOperationText` first. It is amended once to name "selective erasure" and remains the only golden copy. After that come one line per channel from a closed map, then the client-resolved help targets. All of this goes before the confirmation prompt, on the diagnostics stream in every mode. Provider names never cross the wire.

## 12. Restore cannot resurrect

**Destination read.** `ReadDestinationErasureEvidenceAsync` reads the destination's fingerprint rows as values. It runs in `BuildPlanAsync` under the held maintenance lock, and again in `ExecuteAsync` before extraction. It never holds a live handle.

It is three-valued and **fail-closed**, and the OS key is its anchor: the key is persisted before the first fingerprint row can commit, so "no key" proves "no evidence".

| OS key | Grimoire | Result |
|---|---|---|
| NotFound | absent, unreadable, table absent, or 0 rows | `None`; proceed |
| NotFound | ≥ 1 row | refuse `backup.restore_erasure_key_missing` |
| Unavailable | any | refuse `backup.restore_erasure_key_unavailable` |
| Present | absent, unreadable, or table absent | refuse `backup.restore_erasure_evidence_unavailable` |
| Present | table present | `Present(keyId, rows)`, possibly zero rows |

Every refusal names two ways out:
- restore readability of the database or credential and retry;
- run a full installation reset.

**Staged drain.**
- It runs only for `ReplaceInstallation`, only when the destination read is `Present` with at least one row, and only when the staged recorded Core version is below head.
- `PrepareStagedGenerationAsync` alternates `InstallAsync` with bounded `GrimoireSchemaBackfillRunner` passes over the staged connection until the transition journal is empty.
- A throw, a refusal, a no-progress pass, or cancellation refuses with `backup.restore_erasure_evidence_unjoinable`. That happens before any safety backup or rename.
- Destinations without evidence keep today's behaviour exactly.

**Core evidence step.** A new step, `ReconcileStagedMemoryEvidenceAsync`, runs for every `ReplaceInstallation` in both Covenant gate states. It runs after `MigrateAsync` and the drain, and before the Covenant arm. It is one `BEGIN IMMEDIATE` transaction under `CovenantFamilyMaintenance` plus `SensitivityRetentionPurge` authorization. Staging never uses the new kind, because older staged canonical tiers do not know it.

1. Enable and read back FTS `secure-delete` on `lexicon_fts`, and on `covenant_fts` when present.
2. Fold unfolded staged disclosure tails (section 14.4), then join the destination's effective disclosure buckets into staging. This join moves out of the Covenant arm, so a restore with the gate off no longer drops the destination's disclosure buckets.
3. Match staged rows against the destination fingerprints using the same Core fingerprint function:
   - Saga: `(ScopeKindCode, Campaign, Content)`;
   - Lexicon: `(scope, NameNormalized)` and `Name.Trim().ToUpperInvariant()`;
   - Covenant: `(ScopeCode, Campaign, NormalizedKey)`.

   Stores the destination holds no fingerprint for are skipped.
4. Purge the matches:
   - Saga and Lexicon through the shared plan runner, plus labels. The owning Session's tainted-artifact count is recounted, not folded to zero.
   - Covenant entries through the shared `CovenantEntryErasurePlan`, with `TableExistsAsync` probes. When any Covenant entry is purged, the applied FTS tuple is invalidated to `FullRebuildRequired`.
5. Make the fingerprint set destination-authoritative:
   - `DELETE FROM memory_erasure_fingerprints`, then insert the destination rows verbatim.
   - Archive rows under a foreign `KeyId`, and archive rows under the destination key that the destination has since released, are dropped and counted.
   - A plain union would undo releases.
6. Post-conditions, all checked before `COMMIT`:
   - no staged row matches;
   - every purged identity counts zero across every target;
   - the fingerprint set equals the destination's set;
   - after any Covenant purge, the applied tuple is null.

   A failure is `backup.restore_erasure_verification_failed`, followed by a rollback.
7. After commit, run a checked staged WAL checkpoint. For `ReplaceInstallation`, the extracted archive database under `work/extract` is deleted with a check right after staging composes.

**Protected-state modes.**
- `Reject` still refuses archives carrying Covenant rows, even when those rows are erased entries. That is conservative, and the docs say so.
- `RestoreProtectedState` preserves the archive's Covenant data **except entries this installation erased**.
- `PurgeProtectedState` runs the evidence step first.

**Reporting.**
- `BackupRestorePlan.DestinationErasureEvidence` carries status plus per-kind counts.
- `BackupRestoreReconciliation.ErasureApplication` carries removed Saga, Lexicon and Covenant counts, fingerprints joined, archive fingerprints dropped, and the scrub status.
- A post-commit absence proof in `ReconcileAsync` turns any match into `ReconciliationRequired`.
- The frozen restore effect digest and the V2 journal payload are unchanged.

**Other restore paths.**
- New-profile-root restores are outside every arm. The plan warns when this installation holds a key.
- Selective Session import relies on the chokepoints, because it writes into the live database.

## 13. Lifecycle

| Path | Fingerprints and receipts | Key |
|---|---|---|
| Retention prune; `DELETE /api/saga[/{id}]`; `DELETE /api/memory/lexicon/{name}`; `delete_lexicon` | kept | kept |
| Memory reset (every scope, whole-store or Campaign); embeddings reset; workspace reset | kept | kept |
| Campaign or Session deletion; Covenant family reset or reinitialize | kept | kept |
| Factory reset (Grimoire-level) | kept | kept |
| Full installation reset (`--global` / `--all`) | removed with the Grimoire | removed through the credential catalog |
| Backup | carried inside the database file; inert without the OS key | never exported |
| Restore | destination-authoritative (section 12) | destination's, retained |

- The reset residue premise holds unchanged, because neither new table enters the residue list.
- The retention status reports a fingerprint count and a receipt count.
- The factory-reset preview states that N erasure fingerprints remain in force.

## 14. Adjacent defects fixed in this slice

### 14.1 The remainder of #225

- The Saga and Lexicon purge-policy text names the Annals claim, its versions, heads, edges, review events and receipts, and for Lexicon the historical fact provenance. The false Saga FTS wording goes.
- The `CovenantDerivedOutputInventoryTests` pin, which is closed over types, is replaced by a plan-closed structural test. For each Annals store S and kind K:
  - the plan's projections contain every `AnnalsErasurePlan.ForStore(S)` table, in order, before the artifact;
  - each target's `DeleteBy` equals the `ForSubjectQuery` predicate;
  - the artifact table is S's subject table.
- New runtime tests erase a labelled Saga memory that has a full Annals graph, both through the kernel and through the staged purger, and assert that no orphan claim remains.
- A shared "no orphan claim" assertion runs at the end of every erasure-path test.
- Stale remarks in `DataRetentionService` and in DESIGN §10.17 and §21.12 are corrected.

### 14.2 Covenant curation

**Incarnation.** Canonical v6 adds `covenant_key_epochs.IncarnationEpoch`.
- It is stamped once, from `KeyReclamationEpoch`, when the key row is created, and never changes after that.
- Pin and mask reads join on the incarnation. Every compare-and-swap still uses the dependency `KeyEpoch`.
- Operator writes and cross-scope writes no longer disarm pins and masks.

The v6 migration runs in this order:
1. Drop the overflow guard.
2. Set `IncarnationEpoch = KeyEpoch`. Every live curation head stays live, with no append-only rewrite.
3. Recreate the overflow guard and add an immutability guard.
4. Rewrite the three head key-epoch triggers.
5. Purge curation leftovers for keys that have no key row and a nonzero epoch.

`CovenantCurationSubject.KeyEpoch` is renamed `KeyDependencyEpoch`.

**Replay.**
- A curation replay reads the receipt by `MutationId` first.
- It recomputes the request digest from the request plus the receipt's stored epoch.
- It never decodes the token.
- The dead expiry check is removed.

**Reset.** One `CovenantCanonicalContentTables.InDeletionOrder` list feeds four consumers:
- the family-erasure transaction;
- its storage-health emptiness proof;
- the restore inspector;
- the reset inventory.

Curation rows are therefore erased by reset and factory erasure. A manifest-closed test derives the expected set from the canonical catalog, and checks the foreign-key order.

**Staging pin probe.** The staging handler refuses a pinned lane early, as its probe already says it should.

**Receipt entry.** `covenant_mutation_receipts.EntryId` is populated for both outcomes.

### 14.3 Purge coordinator reporting

`CovenantSensitiveRetentionPurgeCoordinator` dispatches items one at a time and classifies each item from its own progress:

| Outcome | Classification |
|---|---|
| The kernel erased it | `Purged` |
| Blocked | `Blocked`; every later item is marked `Blocked` too and is never examined |
| Examined and not erased (the kernel skipped a moved label) | Re-read under the held lease: absent means `Unlabeled`, present means `Blocked(AuthorityStale)` |

Label resolution fails closed on `SqliteException`.

### 14.4 The disclosure fold

**Live producer.** It folds each acknowledged receipt into `external_disclosure_state` in the journal's transaction, through `CovenantDisclosureStateAlgebra.IncrementLocal`. It folds the subject's whole unfolded tail and advances `disclosure_subject_state.LastFoldedOrdinal`.

**Effective reader.** It returns the persisted buckets plus an in-memory fold of every unfolded tail, weakened to `LowerBound`. A Core v13 partial index finds those tails.
- It feeds the reset, factory and restore previews and the restore destination read.
- No backfill sweep is needed: history that was never folded stays counted, as `LowerBound`.

**Restore.** Staging folds the unfolded staged tails before the join.

**Seed fix.** `CovenantRetentionSeed` seeds real receipts through the journal.

### 14.5 Vector mirrors

One `SagaVectorMirror` helper replaces the process-flag gate at all eight sites: insert, delete, delete-all, retire, reinstate, correct, and both review writes. It:
- probes `sqlite_master` inside the caller's transaction;
- classifies the mirror as `Absent`, `PlainTable` or `LegacyVirtualTable`;
- deletes a plain-table mirror whatever the flag says;
- writes only when the flag is on;
- treats a legacy `vec0` table as unreachable residue.

## 15. Finishing #78

- **Saga lifecycle in `memory search`.** Saga hits carry `SagaLifecycle` and `SagaEligibility`. One Core eligibility classifier replaces the two copies. `SagaRetrievalEligibility` becomes a string-only wire enum. The CLI prints Saga lifecycle the way it prints Lexicon lifecycle.
- **`saga list`.** It gains a `State` column.
- **`memory explain`.** It counts only retrievable Saga rows.
- **Prune dry-run.** `data prune --dry-run` renders the Saga pin exemption line.
- **API docs.** API §8.20 documents `sagaCuration`.
- **Covenant `show`.**
  - `CovenantDetailDto` gains per-lane curation state: pinned, masked and curation revision, read in the same snapshot.
  - Human `show` prints version identity, lane revision, lifecycle, origin, rendered hash, byte cost, pin and mask, and curation revision.
  - `--history` prints version identity and rendered hash.
  - The CLI JSON payload gains `RenderedHash` and curation state.
  - `show` stays content-free by rule. #78's "show full content" wording is amended for the Covenant.
- **`delete_lexicon`.** It refuses retired and pinned entries: with a tool-level pre-check, with `Lexicon.RetiredMutationRefused`, and with a new `Lexicon.PinnedMutationRefused`. An authoritative in-transaction check, through an agent-origin overload of `DeleteByNameAsync`, backs the pre-check. The operator's delete is unchanged.
- **Cross-store isolation.** A reusable `MemoryStoreSnapshot` test helper discovers tables from `sqlite_master` and partitions shared tables by store. Every per-item verb gets a theory row proving the other stores' bytes are unchanged and the target store did change:
  - Saga, Lexicon and Covenant curation;
  - bulk review apply for each store;
  - erase and release for each store;
  - the legacy deletes.
- **Doc drift.**
  - API §1 lists the fourteen Covenant management routes.
  - Command Reference: the `Next:` sentence is corrected, and `saga delete` says "deleted, not erased".
  - `MemoryEndpoints` retention strings are corrected, and the dead `CovenantRetention` string is removed.
  - DESIGN: the §5.4.7 Lexicon pin row is added, and the claims at §10.22.6 and §10.26.3, the "turn aborts at revalidation" claim, and the covenant verb list are corrected.
  - OATH: the #78 row is updated.

## 16. Surfaces

### 16.1 HTTP

All routes are authenticated static POSTs with typed bodies, and none is on `/v1`. Every erase and release response is protected: `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0`, and no validators.

| Route | Name | Request → Response |
|---|---|---|
| `POST /api/memory/saga/erase/prepare` | `PrepareSagaMemoryErasure` | `SagaErasePrepareRequest` → `MemoryErasurePreflightDto` |
| `POST /api/memory/saga/erase` | `EraseSagaMemory` | `SagaEraseRequest(+PreflightToken)` → `MemoryErasureResultDto` |
| `POST /api/memory/saga/release` | `ReleaseSagaErasure` | `SagaErasureReleaseRequest(SagaMemoryScopeKind ScopeKind, Guid? CampaignId, string Content)` → `MemoryErasureReleaseResultDto` |
| `POST /api/memory/lexicon/erase/prepare` | `PrepareLexiconEntryErasure` | `LexiconErasePrepareRequest` → `MemoryErasurePreflightDto` |
| `POST /api/memory/lexicon/erase` | `EraseLexiconEntry` | `LexiconEraseRequest(+PreflightToken)` → `MemoryErasureResultDto` |
| `POST /api/memory/lexicon/release` | `ReleaseLexiconErasure` | `LexiconErasureReleaseRequest(LexiconCurationScope Scope, string Name)` → `MemoryErasureReleaseResultDto` |
| `POST /api/memory/covenant/erase/prepare` | `PrepareCovenantErasure` | `CovenantErasePrepareRequest` → `MemoryErasurePreflightDto` (with `CovenantErasurePlanFacts`) |
| `POST /api/memory/covenant/erase` | `EraseCovenantEntry` | `CovenantEraseRequest(+PreflightToken)` → `MemoryErasureResultDto` |
| `POST /api/memory/covenant/release` | `ReleaseCovenantErasure` | `CovenantErasureReleaseRequest(CovenantScope Scope, Guid? CampaignId, string Key)` → `MemoryErasureReleaseResultDto` |

Routes whose Core or canonical schema has not reached this slice's version return a retryable `MemoryErasure.Unavailable` (503).

**New error codes:**

| Code | Status |
|---|---|
| `MemoryErasure.Unavailable` | 503 |
| `MemoryErasure.KeyUnavailable` | 503 |
| `MemoryErasure.KeyLost` | 409 |
| `MemoryErasure.InvalidPreflight` | 400 |
| `MemoryErasure.StalePlan` | 409 |
| `MemoryErasure.SubjectErased` | 410 |
| `MemoryErasure.ErasureIncomplete` | 500 |
| `Lexicon.SuppressedNameRefused` | 409 |
| `Lexicon.PinnedMutationRefused` | 409 |

Existing codes reused as they are: `Saga.NotFound`, `Saga.StaleContent`, `Lexicon.InvalidName`, `Lexicon.StaleCurationTarget`, `Covenant.RevisionConflict`, `Covenant.StaleSnapshot`, `Covenant.ForbiddenAuthority`, `Covenant.Unavailable`, `Covenant.MaintenanceFailed`, `Covenant.ManualRecoveryRequired`, and `Security.IdempotencyConflict`.

### 16.2 CLI

Each verb is a thin HTTP client. It runs:

1. `show`;
2. prepare;
3. render the plan and the external disclosure;
4. confirm through `IConfirmationPrompt`, which accepts `--yes`;
5. apply.

`--json` writes exactly one document to stdout: the result, or a `MemoryErasureCancellationPayload` on decline, which exits 0. Exit codes follow `CliFailureExit`.

| Command | Notes |
|---|---|
| `arcanum memory saga erase <id>` | Optional `--expected-content-hash` pins the hash `show` must report |
| `arcanum memory saga release --file <path\|-> [--campaign <guid> \| --scope global\|campaign\|unresolved\|unclassified]` | Content from a file or stdin, so it never reaches shell history |
| `arcanum memory lexicon erase <name> [--campaign <guid>]` | |
| `arcanum memory lexicon release <name> [--campaign <guid>]` | |
| `arcanum memory covenant erase <key> [--campaign <guid>]` | |
| `arcanum memory covenant release <key> [--campaign <guid>]` | |

There is still no generic `memory erase` or `memory delete`. `Memory_has_no_generic_delete_command` extends to `erase`. Erase is not added to search actions or to review actions.

## 17. Documentation

**`docs/Arcanum.DESIGN.md`:**
- A new §21.15, "Selective hard erasure and erasure fingerprints".
- Updates to:
  - §5.4.5a (Core v13 narrative);
  - §5.4.7 (lifecycle table rows);
  - §5.4.9 (restore convergence sentence);
  - §10.6.2, §10.6.3, §10.13, §10.17, §10.19.3, §10.19.9, §10.19.10, §10.19.13, §10.20.1, §10.20.5, §10.22.6;
  - §10.25.1 (the restore-staging drain exception);
  - §10.26.2, §10.26.3, §10.26.6;
  - §11.2.1 (credential inventory);
  - §16.2;
  - §21.12;
  - §21.13.3 and §21.13.6.

**Other documents:**
- `docs/Arcanum.API.md`: §1 table, §8.20, §8.23, §8.28, §8.30, §8.33, and a new §8.35 "Selective erasure".
- `docs/Arcanum.Command.Reference.md`: the `arcanum memory` family, `saga delete`, `backup restore`, and `data`.
- `docs/Arcanum.CommandMap.json`: regenerated.
- `docs/Arcanum.Engineering.md`: erasure, the disclosure fold, and fingerprint custody.
- `docs/Arcanum.OATH.md`: #78, #98 and #100 rows, and §15.4.
- `docs/Arcanum.DEBUGGING.Human.md`: the new restore blocker codes.
- `docs/Compendium.README.md`: unchanged, because no configuration key is added.

## 18. Testing

**Strict red-green-refactor.** Every production behaviour lands test-first, and the test is watched failing for the intended reason.

**Acceptance tests enter through production entry points.** They use the mapped route or the registered CLI verb, and they establish preconditions through production write paths:
- a memory is written by the store's insert;
- a fingerprint is written by an actual erase;
- a retirement is written by the retire route.

**Keychain isolation.** Tests inject `InMemoryOsCredentialStore`, and never touch the real keychain.

**End-to-end proofs that matter most:**
1. Erase a Saga memory through the route. Then run extraction through its own service with the same conclusion, and assert that the row did not come back and that the cursor advanced.
2. Erase a Lexicon entry, then call `scribe_lexicon` for that name. The call is refused and nothing is written.
3. Erase a Covenant entry while a turn is in flight. The erase waits for the turn to drain. A later agent proposal of the key is refused at staging.
4. Take a backup, erase, then restore the backup. The erased item is absent, the fingerprint set equals the destination's, and an archive with an older Core version is drained and then purged.
5. Release, then extract again. The item may come back.

**Mutation checks.** Before completion, each of the following is broken in turn and the suite must fail each time:
- the fingerprint check at each chokepoint;
- the key-lost fail-closed branch;
- the effect-digest recompute;
- the receipt-first replay;
- twin inclusion;
- the retirement-suppression delete;
- the closure slot mapping;
- key reclamation;
- the incarnation join;
- the restore purge and its post-condition;
- the destination-authoritative join (a union must fail);
- the absence proof;
- the WAL-checkpoint verdict;
- the disclosure fold's watermark;
- the purge coordinator's per-item classification.

**Qualification:**
- Release build with zero warnings.
- Both test suites.
- `./scripts/coverage.sh --threshold`.
- The hosted-producer analysis.
- `./scripts/verify-aot-il-warnings.sh`.
- `./scripts/verify-shipping-publish.sh --rid osx-arm64`.
- `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`.

## 19. Closed inventories this change grows

**Schema:**
- the Core and canonical version constants, pins and fixtures (v12 and v5);
- `GrimoireSchemaTransitionResourceTests` and `GrimoireSchemaVersionChainTests`;
- the fresh versus evolved equivalence tests;
- `CovenantCanonicalSchemaTests`;
- the benchmark input catalog and its `.sql` count;
- the connection-acquisition inventory;
- the hosted-producer inventory and capsules.

**Covenant runtime:**
- `CovenantSqliteAuthorizationKind` and `FunctionNames`;
- `CovenantExclusiveOperation` and `CovenantLeaseKind`;
- the `ICovenantOperationGate` test fakes, nine files;
- `CovenantArchitectureBoundaryTests`;
- `CovenantPublicContractInventory` and `CovenantErrorContractTests`.

**Credentials and backup:**
- `ArcanumCredentialIdentity`, `InstallationResetCredentialCatalog` and `VerifyIdentitiesRotated`;
- `BackupSecretSnapshotReader`, pinned by a source test.

**Retention:**
- the retention data-class enum pins, and the status and parser tests.

**API, errors and serialization:**
- `ErrorCodes`, `ArcanumErrorMapper` and `ArcanumErrorMapperTests`;
- `ArcanumJsonContext` and `CliJsonContext`;
- the route inventories: `CovenantSensitivePurgeRouteInventoryTests`, `LexiconCurationRouteInventoryTests`, `ApiSurfaceContractTests`, `ApiDomainSplitContractTests`, `DocumentationStructureTests`, plus a new erase-route inventory.

**CLI:**
- the CLI tree, `CliSurfaceExamples`, `CliSurfaceTests` (the covenant verb count moves from 10 to 12), and the regenerated command map;
- `Memory_has_no_generic_delete_command`.

**Suppression and nullability:**
- `SagaMemoryWriteOutcome` documentation;
- `NullableInterfaceConstructorDefaultTests`.

**Documentation:**
- `DocumentationIssueReferenceTests`, which keeps governed documents free of tracker references.
