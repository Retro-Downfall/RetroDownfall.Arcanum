# Issue #100: Selective hard erasure with keyed resurrection suppression

**Status:** Designed on 2026-09-28. The operator approved the design before any code was written. Revised on 2026-09-29 after an independent four-lens review (template, code reality, security and privacy, concurrency and lifecycle), and after two further operator decisions, recorded in §2.

**Branch:** `claude/issue-100-selective-erasure`, cut from `memory-enhancement` at `af212a90`. It is merged back with `--no-ff` after qualification, and `memory-enhancement` is then merged to `main`.

**Issues:**
- #100 is an XL delivery slice under #78. Its blockers #87, #90, #94, #96, #97, #98 and #102 are closed.
- #225 is the remaining blocker, and it is finished here (§15.1).
- #78's remaining gaps are closed here, so #78 can close. Its carve-outs are recorded rather than delivered: agent recall goes to #101, and Long Rest consolidation and decay go to #93 and #95.

**Names settled with the operator:**
- **Erasure:** permanently removes one exact memory item, together with every local artifact derived from it that the store owns.
- **Erasure fingerprint:** the content-free, installation-keyed evidence an erasure leaves behind, so that automatic writers and restores cannot bring the item back.
- **Release:** lifts one erasure fingerprint when the operator supplies the plaintext identity or content it covers.
- **Erasure key:** the installation-private HMAC key fingerprints are computed under.

These are plain descriptive names. The operator waived thematic naming for this slice.

## 1. Objective

Two capabilities already exist:
- **Retirement:** the operator can retire a memory, which keeps it inspectable and reversible.
- **Deletion:** the operator can delete a Saga memory or a Lexicon entry, but that leaves nothing to stop the next extraction pass, agent write, or backup restore from recreating it.

No path removes one Covenant entry's history at all.

This slice adds the missing promise: exact, confirmed, single-store erasure of one Saga memory, one Lexicon entry, or one Covenant entry. Erasure is:

- **verified locally**, and reported honestly;
- **remembered** as content-free, keyed evidence that survives every lifecycle path except a full installation reset;
- **enforced** at every automatic write chokepoint and inside restore staging;
- **candid** about which copies Arcanum cannot revoke because they already left the machine, and which local copies it deliberately does not touch.

## 2. Scope, settled before design

The operator settled these before and during design:

1. **Key custody: the OS credential store.** The erasure key lives in the macOS Keychain or the Windows Credential Manager, under the same custody as the Campaign root-identity key.
   - It is never written to the Grimoire or any backup.
   - Only a full installation reset removes it.
   - Consequences:
     - an archive can never be used to test guesses about erased content;
     - two installations restored from one archive never share a key;
     - after disaster recovery onto a fresh installation, older fingerprints cannot be verified and are dropped.
2. **Lifetime.** Fingerprints survive everything except a full installation reset: retention, store deletes, `DELETE /api/saga`, memory and embeddings resets, Campaign and Session deletion, Covenant family reset and reinitialize, and factory reset.
3. **Equivalence, and who is blocked.** Where a store has an identity, the fingerprint is identity-level. Erasure is never weaker than retirement.
   - **Saga:** the exact content in the exact scope.
   - **Lexicon:** the normalized name in the exact scope.
   - **Covenant:** the normalized key in the exact scope.
   - **Who is blocked:** agents and automatic writers.
   - **Release:** a per-store `release` verb lifts one fingerprint when given the plaintext identity or content.
4. **Plain names.** No new thematic vocabulary.
5. **One delivery.** Saga, Lexicon and Covenant erasure, restore suppression, the adjacent defect fixes, and #78's remaining gaps ship together.
6. **An operator write that re-creates an erased identity releases its fingerprint.** It does so in the same transaction, and the result says so. This covers:
   - a Covenant operator `set` of an erased key, which the preflight also discloses;
   - a Saga correction to byte-identical erased content in the same scope.

   Operator writes are never refused because of a fingerprint. A later restore therefore never deletes operator-authored content.
7. **Key-loss recovery.** Fingerprints can exist while the erasure key is lost, replaced, or unreadable. The installation then fails closed for that store's automatic writers until the operator acts. Two operator surfaces handle it:
   - `arcanum memory erasure status` reports the key state and counts.
   - A confirmed `arcanum memory erasure reset-key` discards the fingerprints that can no longer be verified, reporting how many, then mints a fresh key and unblocks the writers.

Settled by design and approved with it:

- **Legacy deletes keep their meaning.** `DELETE /api/saga/{id}`, `DELETE /api/saga`, `DELETE /api/memory/lexicon/{name}`, `arcanum saga delete` and `arcanum memory lexicon delete` remain unsuppressed deletes. Their documentation and messages now say so. Only `erase` suppresses and verifies.
- **The Covenant erasure unit is the whole entry.** Every set version holds its content in plaintext, in a chain the append-only guards protect. So erasure takes both lanes and every version, bound to the current heads the operator inspected.
- **Saga erasure takes the whole identical-content class in scope.** It also removes every other memory in the same scope with byte-identical `Content`. The preflight discloses these twins and the effect digest binds them.
- **Covenant erasure drains in-flight turns.** Saga and Lexicon have no drain; §12 states that limit.
- **No store owns managed files.** Saga, Lexicon and Covenant items own no managed-file artifacts. Attachments are *sources* of attachment-derived memories, not derivatives of them, and are reported as retained local copies (§12). The "managed artifacts" clause of the acceptance criteria is satisfied by construction and pinned by a test (§19.3).

## 3. Governing constraints

- **Core v13 is pinned before any change.** Core schema v12 publishes the normalized source fingerprint `616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B`. Core v13 pins it before any head object changes. A `CoreSchemaVersionTwelveFixture` reconstructs v12, and a test proves it hashes to the pin.
- **Canonical v6 is pinned the same way.** Covenant canonical v5 publishes the **raw** source fingerprint `E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925`. Canonical v6 pins it. A `CovenantCanonicalSchemaVersionFiveFixture` freezes the byte-exact v5 text of every object v6 edits. The canonical tier keeps the raw computation.
- **No new backfill sweep.** Neither Core v13 nor canonical v6 declares one. A step without a sweep finalizes in its own transaction.
- **SQLite's own declaration shapes.** `ALTER TABLE … ADD COLUMN` reproduces SQLite's splice layout in every edited head file, so fresh and evolved catalogs converge exactly.
- **Trigger rewrites drop first.** A rewrite is `DROP TRIGGER IF EXISTS` followed by `CREATE TRIGGER IF NOT EXISTS`.
- **The declarative schema tree.** Raw SQL, one object per file. No EF entity, no numbered migration, no compiled-model change. Every application read and write is parameterized direct SQLite.
- **Native AOT.** Every `/api` payload is on `ArcanumJsonContext`, and every CLI payload on `CliJsonContext`. Every failable `Results.Json` gets explicit `JsonTypeInfo`. New wire enums are string-only. No `[JsonPropertyName]` and no anonymous DTOs. No configuration key is added.
- **Existing numeric enums.** Numeric Saga wire enums (`SagaMemoryScopeKind`) keep their numeric form. `SagaRetrievalEligibility` becomes string-only, a documented response change (§16).
- **`Result` flow.** Domain operations return `Result` or `Result<T>`, and only endpoints map them to envelopes and status codes.
- **No new plaintext.** Nothing new stores plaintext. Every stored digest that a guess could be tested against is keyed, and durable digests bind only non-content fields.
- **Content-free logs.** Every new log line on the erase, release, chokepoint, restore, status and reset-key paths is content-free, with no names, keys or content. A source test pins that.
- **Documentation rule.** Documentation under `docs/` names capabilities, never issue numbers. The exceptions are `docs/Arcanum.OATH.md`, `docs/Arcanum.Engineering.md` and `docs/superpowers/**`. The root `README.md` does not change.
- **Platforms.** Linux is not a shipping RID. The shipping targets, macOS and Windows, both have an OS credential store.

## 4. Considered approaches

### 4.1 Key custody

- **Rejected: reuse `saga_suppression_key`.**
  - The key sits in the Grimoire, so every backup carries it next to its digests: an offline confirmation oracle.
  - A restore adopts it verbatim on another machine.
  - A whole-store Saga reset deletes it.
- **Rejected: an in-database keyring rotated on every restore.** It survives disaster recovery, but it still puts key and fingerprints in every archive.
- **Selected: the OS-held key, with a per-row `KeyId` (§5.2).** A lost, replaced, or foreign key makes rows detectably unverifiable. Chokepoints then fail closed instead of silently allowing resurrection (§5.4).

### 4.2 Evidence storage

- **Rejected: reuse `saga_retirement_suppressions`.**
  - Reinstatement deletes retirement digests.
  - Saga resets clear them.
  - It hashes scope text, which is why it needs a permanent two-spelling compatibility pair.
- **Selected: one new store-agnostic Core table.** It holds only a fingerprint, a store code, and a key identifier. Scope lives inside the keyed preimage as Campaign GUID bytes.

### 4.3 Restoring an archive older than the evidence schema

- **Rejected: install the v13 table in staging "regardless".** It puts a head object ahead of its step.
- **Rejected: carry the rows in the restore journal.** The journal is a frozen format, and the approach leaves a live window with no evidence.
- **Selected: drain, or refuse.** When the destination holds evidence, restore staging drains every tier's pending sweeps to head before applying it. If that cannot be done, it refuses before any displacement.

### 4.4 Stopping in-flight Covenant turns from re-sending erased text

- **Rejected: per-entry invalidation checked at revalidation.** No inference path revalidates its turn lease, and any check-then-send leaves a race.
- **Selected: a new exclusive operation that drains every turn that could hold the bytes before commit.**

### 4.5 Deleting guarded Covenant canonical rows

- **Rejected: borrow `OwnerCleanup`.** It opens 24 Core guards, including disclosure receipts and labels, and it does not open the outbox guard.
- **Selected: a new narrow authorization kind, `CovenantEntryErasure`.** Canonical v6 admits it.

## 5. Erasure fingerprints and the erasure key

### 5.1 The key

**Where it lives.**
- Account `memory-erasure-fingerprint-key`, service `arcanum`.
- 32 random bytes, stored as canonical unpadded base64url.
- The account name is fixed and deliberately **not** profile-namespaced. The profile namespace hashes the physical identity of the profile root's parent directory, so recreating `~/.config` would lose the key. Production has one profile per OS user, and a shared account across test homes is detected by `KeyId`.

**One process-wide object.**
- `MemoryErasureKeyring` is one singleton per process. It implements two interfaces:
  - `IMemoryErasureKeyProvider` (Core): reads only. Used by chokepoints, release, status, and restore.
  - `IMemoryErasureKeyCreator` (Infrastructure, internal): may create. Used by erase prepare and `reset-key`. It is registered only in the host container.
- Every keychain read, write, and read-back runs under one lock, so concurrent first erasures perform exactly one create.
- The keyring holds one shared latch: `Unresolved`, `Present(KeyId)`, `Absent`, or `Failed(status)`.
  - Automatic callers use the latched value.
  - Operator-initiated calls re-probe any value other than `Present` and publish the result into the same latch: erase, release, status, `reset-key`, and restore.
  - A successful create publishes `Present`.
  - So a transient keychain failure at the first automatic probe clears on the next operator status or erase.

**Creation rules.**
- Only on a proven `NotFound`, and only when no fingerprint rows exist.
- Existing rows without a key means `MemoryErasure.KeyLost`, never a fresh key. The exception is `reset-key` (§5.7).
- A create writes, reads back, and compares in constant time.
- A malformed stored value is never overwritten.

**Handling.**
- Key material is copied into a disposable `MemoryErasureKey`, which zeroes itself on dispose.
- The keyring does no keychain I/O at composition or construction.
- Keychain I/O never runs inside a SQLite transaction or while a Covenant lease or closure is held (§5.4).

**Dependency and lifetime.**
- The provider is a **required** constructor dependency at every chokepoint owner.
- A test-only constructor without it must fail closed whenever fingerprint rows exist. It must never skip the check.
- The account joins the fixed seed of `InstallationResetCredentialCatalog` and the `VerifyIdentitiesRotated` identity check, so a full installation reset removes both the key and the Grimoire.
- `BackupSecretSnapshotReader` never reads the account. A source-inventory test pins that.

### 5.2 Store codes, key identifier, and digest grammar

**Store codes.** Every new table, preimage, and enum in this slice uses one store code space: the existing `MemoryReviewStore` codes, **1 Covenant, 2 Saga, 3 Lexicon**. A test pins the mapping.

**Encoding.** Every preimage follows the same rules:
- It is a sequence of fields, each written as one of:
  - `u8`;
  - `u32be`;
  - `u64be`;
  - `lp(bytes)` = `u32be` length followed by the bytes;
  - `guid` = 16 bytes, RFC 4122 big-endian;
  - `opt(x)` = `u8 0`, or `u8 1` followed by `x`;
  - `list(x)` = `u32be` count followed by the items.
- Text is strict UTF-8, which rejects unpaired surrogates.
- Every preimage begins with an ASCII domain label followed by `0x00`.

```
KeyId        := HMAC-SHA256(K, "Arcanum.MemoryErasure.KeyId.v1\0")[0..16]

Fingerprint  := HMAC-SHA256(K, "Arcanum.MemoryErasure.Fingerprint.v1\0"
                  || u8 StoreCode || u8 ScopeKind || opt(guid Campaign) || lp(identity))
   ScopeKind: Saga 0 Unclassified, 1 Global, 2 Campaign, 3 LegacyUnresolved;
              Lexicon and Covenant 1 Global, 2 Campaign.
   Campaign present iff ScopeKind = 2.
   identity: Saga = exact stored Content bytes; Lexicon = Name.Trim().ToUpperInvariant(),
             always computed from the row's Name column under the current rule, never from the
             stored NameNormalized; Covenant = NormalizedKey.
   One fingerprint per erased identity. Erase, the scribe chokepoint, release, and restore
   staging all derive the Lexicon identity the same way, so a legacy NameNormalized spelling
   cannot split it.

ContentBinding := HMAC-SHA256(K, "Arcanum.MemoryErasure.ContentBinding.v1\0" || u8 StoreCode
                  || lp(row id) || lp(exact stored content bytes))
   Saga only. It is carried in the short-lived preflight token, never stored, and recomputed
   over the live row at apply, so a claimless memory cannot be corrected between prepare and
   apply without the erase refusing. Lexicon binds content through CurationGeneration, and the
   Covenant through immutable version ids.

RequestDigest := HMAC-SHA256(K, "Arcanum.MemoryErasure.Request.v1\0" || u8 StoreCode
                  || guid MutationId || store-specific non-content fields)
   Saga:     lp(MemoryId) || opt(guid ExpectedClaimVersionId)
   Lexicon:  u8 ScopeKind || opt(guid Campaign) || guid EntryId || u64be CurationGeneration
             || opt(guid AnnalVersionId) || opt(guid LabelId || u64be ArtifactRevision)
   Covenant: u8 ScopeKind || opt(guid Campaign) || guid EntryId
             || opt(guid ConfirmedVersionId || u64be LaneRevision)
             || opt(guid ProposedVersionId || u64be LaneRevision)

SubjectDigest := HMAC-SHA256(K, "Arcanum.MemoryErasure.Subject.v1\0" || u8 StoreCode || lp(row id))
   Row id: Saga MemoryId (each erased memory, twins included); Lexicon EntryId; Covenant EntryId.

EffectDigest  := HMAC-SHA256(K, "Arcanum.MemoryErasure.Effect.v1\0" || u8 StoreCode
                  || list(lp(row id)) sorted ordinal || list(opt(guid version)) aligned
                  || list(u8 table code, u64be count) for every plan target
                  || u32be labels || u32be retirement suppressions || u8 flags bitset
                  || store facts (Covenant: guid DatasetGeneration, u64be KeyEpoch,
                     u64be KeyReclamationEpoch, flags ReclaimsKey/RetainsCampaignMask/
                     GlobalConfirmedResurfaces/Pinned; Lexicon: flag GlobalEntryResurfaces)
                  || 5 × u8 evidence codes || u32be retained-copies mask)
```

**What the durable digests may contain.** The durable digests are the request, subject and effect digests. They bind identifiers, counts and flags only, never content-derived values:
- `ExpectedContentHash`, `SnapshotDigest`, rendered hashes, names and keys are compared at apply time.
- They are never stored in a digest.

So a stored receipt can never be replayed to test a guess about content (§6.3).

### 5.3 Storage (Core v13)

```sql
CREATE TABLE IF NOT EXISTS memory_erasure_fingerprints (
    Fingerprint BLOB NOT NULL PRIMARY KEY CHECK (length(Fingerprint) = 32),
    StoreCode INTEGER NOT NULL CHECK (StoreCode IN (1, 2, 3)),
    KeyId BLOB NOT NULL CHECK (length(KeyId) = 16)
);
CREATE INDEX IF NOT EXISTS idx_memory_erasure_fingerprints_store_key
    ON memory_erasure_fingerprints(StoreCode, KeyId);
```

**What the table does not hold.** It has no memory identity, no scope column, and no timestamp. With no timestamp it stays out of the UTC-instant inventory.

**Who may delete.** There is no delete-guard trigger. A source-scan architecture test pins the only statements that may delete rows:
- release;
- operator re-creation (§5.6);
- `reset-key`;
- restore staging's destination-authoritative join;
- full installation reset, by deleting the file.

**Lifecycle lists.** The table is **absent** from:
- every memory-reset selection;
- the reset residue lists;
- the factory-reset plan and deletion lists;
- `BackupRestoreProtectedStateInspector.CanonicalContentTables`.

It is **present** in the retention status inventory, as content-free counts under a new never-aged retention class with an explicit null rule arm.

### 5.4 Chokepoints

**A catalog below v13 has no fingerprints.** If the recorded Core version is below 13, or the table is absent, the answer is always "no fingerprints". That is safe, because rows can only be committed at Core ≥ 13:
- the erase routes require it;
- a restore that applies evidence drains to head.

This covers the live-upgrade window, in which a host serves traffic while an earlier sweep drains. The same rule applies to every other reader of the evidence tables:
- the retention status inventory;
- the factory-reset preview;
- `memory erasure status`;
- restore's destination read.

A test writes through each chokepoint while the v12 sweep is pending.

**Two-phase key protocol.** Every chokepoint follows it:

1. **Before** any SQLite transaction, Covenant lease or closure, run a read-only probe: `SELECT EXISTS(… WHERE StoreCode = @store)`.
   - If it is true, resolve the key through the latch.
   - If the key is not `Present`, fail closed before any provider or model call (see "Store by store" below).
2. **Inside** the store's write transaction, re-run the `EXISTS`.
   - If it is now true but no key is in hand, roll back, resolve the key outside the transaction, and retry once. On a second miss, fail closed with a typed retryable error.
   - Keychain I/O never happens inside the transaction, and the check is never skipped.
3. **Inside** the transaction, also run `EXISTS(… WHERE StoreCode = @store AND KeyId <> @currentKeyId)`. A hit means the store holds evidence this key cannot verify, so fail closed as `KeyLost`.
4. Compute the fingerprint and look it up.

**Store by store.**

| Store | Where | Pre-provider gate | Refusal |
|---|---|---|---|
| Saga | Authoritative: `SagaMemoryStore.InsertCoreAsync`, after the retirement-suppression check and before the insert. Advisory: `SagaExtractionService` runs the same Core helper for each candidate **before `EmbedAsync`**. | While Saga fingerprints exist and the key is not `Present`, extraction defers the page **before** the extraction model call. The deferral does not consume retry-ladder attempts. | Returns the existing `SagaMemoryWriteOutcome.Suppressed`; extraction counts it as progress and advances the cursor. The advisory check skips the candidate so its text is never re-embedded. |
| Lexicon | `LexiconService.UpsertCoreAsync`, under `BEGIN IMMEDIATE`, after the exact-scope read. It runs whether or not a row exists. | `LexiconService.UpsertAsync` runs step 1 before opening its transaction. | New `Lexicon.SuppressedNameRefused` (409). The tool message is shared with the pin refusal (§15.6): "This Lexicon entry is managed by the operator in this scope, so nothing was recorded." |
| Covenant (authoritative) | `CovenantMutationKernel.ApplyIntentAsync`, directly after the pin check, for origins `AgentProposed` and `AgentApproved`. | `ApplyBatchAsync` receives the key as a parameter. Its callers read the latch before `BEGIN` and pass the key whenever the latch is `Present`, whatever the probe says; reading the latch is never keychain I/O. The callers are turn publication (`GrimoireRepository.TurnCommit`), `CovenantMutationService`, and `CovenantMemoryReviewService`. Erase prepare publishes `Present` into this process's latch before the first fingerprint can commit, so turn publication never races a first fingerprint without a key. If fingerprints exist and no key is passed, the kernel fails closed: the key-loss case (§5.7). | The frozen `Covenant.ForbiddenAuthority`. The message is shared with the pin refusal: "This Covenant key is managed by the operator in this scope." |
| Covenant (courtesy) | `ResolveProposedLaneAsync`, through a new `IsAgentWithheld` on `CovenantLaneHeadProbe`; `ResolveRetirementPreflightAsync`, through the same flag on `CovenantRetirementTarget`. | While Covenant fingerprints exist and the latch is not `Present`, staging refuses every agent proposal and agent retirement. | Refused at staging, so a turn's reply is never discarded at publication. The staging handler also starts honouring the existing `IsPinned`, which it currently ignores (§15.2). |

**Operator writes are never refused.** §5.6 says what they release.

**Exact-scope boundaries.** These are stated in the preflight notes and in DESIGN:
- Agents only author Campaign Proposed lanes, so a Global Covenant fingerprint never blocks an agent proposing that key inside a Campaign.
- A `LegacyUnresolved` Saga fingerprint stops matching once that Session's Campaign binding is resolved.
- Content erased in Campaign A remains extractable in Global scope or in Campaign B.

### 5.5 Release

**Contract.**
- Release is operator-only and store-scoped.
- It is authenticated and requires `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)` on every store. Release is the unsafe direction, because it re-enables agent authorship. The data-lifecycle routes already use this authority, and the issuer is registered whether the Covenant feature is on or off.
- The CLI confirms before calling it.
- Release is naturally idempotent and takes no `MutationId`.

**What it does.**
- It recomputes the fingerprint from the supplied scope and identity (or content) and deletes that row.
- For Saga content it fingerprints both the exact supplied bytes and their `Trim()` form, and deletes whichever exist.

**Outcomes.**
- `Released` with a count, or `NotFingerprinted`.
- It never creates the key.
- If rows exist for the store but the key is lost or foreign, it returns `KeyLost`, not `NotFingerprinted`.

### 5.6 Operator re-creation releases

An operator write that makes an erased identity live again deletes the matching fingerprint **in its own transaction** and reports it:

- **Covenant.**
  - Operator `set` prepare reports `ReleasesErasureFingerprint = true` on its effect, as a new trailing optional field, so the CLI shows it before confirmation.
  - Commit deletes the fingerprint for `(scope, Campaign, key)` in the mutation transaction.
  - The result reports `ReleasedErasureFingerprint = true`.
- **Saga.** A correction whose new content equals fingerprinted content in the row's scope deletes that fingerprint. This applies both to the single-item correction and to #99 bulk review `Correct`. The correction result reports `ReleasedErasureFingerprint = true`.
- **Lexicon and the other paths.** Lexicon has no operator create path, and Covenant `correct` and `retire` require a live head. None of these can re-create an erased identity.

The operator path resolves the key before its transaction exactly as §5.4 does. If fingerprints exist for the store but the key is lost, the operator write still succeeds, but it cannot release anything. It reports that `memory erasure status` needs attention.

### 5.7 Status, scrub, and key reset

**Status: `GET /api/memory/erasure`** (read-only, operator re-probe). It reports:
- the key state: `Absent`, `Present`, `Unavailable`, or `Lost`;
- fingerprint counts per store;
- unverifiable-row counts per store (rows whose `KeyId` differs, or rows that exist when the key is `NotFound`);
- the count of receipts still pending scrub.

The probe uses `IOsCredentialPresenceProbe` where possible, so it reads no secret.

**Scrub retry: `POST /api/memory/erasure/scrub`** (no body).
- Retries the post-commit WAL checkpoint (§10).
- Advances every `Pending` receipt whose only outstanding reasons were WAL reasons to `Verified`.
- Returns the counts.

**Key reset: prepare, then apply.**
- `POST /api/memory/erasure/reset-key/prepare` (body: none) measures the unverifiable fingerprint and receipt counts per store and the key state. It issues a five-minute `MemoryReviewTokenCodec` token with a new `ErasureKeyReset` purpose. The token binds those counts and the key state.
- `POST /api/memory/erasure/reset-key` (body: the token) applies it, and requires `LifecycleManage` and CLI confirmation. It has no `MutationId` and no receipt. It is naturally idempotent: a second run finds nothing to discard.
  1. **Outside any transaction:**
     - **`Present`:** keep the key.
     - **`NotFound`:** create the key (this is the one creation path that does not require zero rows), read it back, and publish `Present`.
     - **`Unavailable`, `Failed`, or malformed:** refuse with `MemoryErasure.KeyUnavailable`, delete nothing, and change no keychain item. The documented remedy for a malformed item is to remove it with the OS credential tool, then re-run `reset-key`.
  2. **Inside `BEGIN IMMEDIATE`:**
     - re-measure against the token; a mismatch is `StalePlan`;
     - delete every fingerprint and receipt whose `KeyId` differs from the current key's (subjects cascade);
     - commit.
  3. Report the counts.
- The confirmation text warns that the discarded erasures may be re-learned by extraction or agent writes.

### 5.8 Core v13 contents

Core v13 is one step with no backfill sweep. Its statements, in order:

1. Create `memory_erasure_fingerprints` and its index (§5.3).
2. Create `memory_erasure_receipts`, `memory_erasure_receipt_subjects` with their indexes, and the receipt update guard (§6.3).
3. Create the disclosure partial index `idx_disclosure_subject_state_unfolded` (§15.4).
4. `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 1);`
5. `INSERT INTO lexicon_fts(lexicon_fts) VALUES('optimize');` This runs once, merging every segment. It drops the tokens that pre-v13 non-secure deletes left behind: scribe merges, corrections, retirements and deletes.

`CoreGrimoireSchemaDataInitializer` also converges `secure-delete = 1` on `lexicon_fts` and reads it back, throwing on a mismatch, so fresh installs have it from their first write. It never runs `optimize`.

The head files of `lexicon_fts`, `disclosure_subject_state`, `external_disclosure_state` and `disclosure_subject_aggregates` correct their comments where the meaning changed. Comment edits move the normalized fingerprint only as the schema rules require.

## 6. The erase protocol

The shape is common to all three stores. The per-store targets are in §7–§9.

### 6.1 Prepare

Prepare is an authenticated POST whose body names the exact target. The CLI obtains the target from the store's `show`, so the operator never transcribes hashes or version identities.

**Authority.** Every erase route declares exactly one operator authority:
- **Saga and Lexicon:** `SensitivityRetentionPurge`, the authority their label purge requires.
- **Covenant:** `LifecycleManage`.

Both are refused on a host-tools-tainted installation, and both are available in both Covenant gate states.

Steps:
1. Before any lease or snapshot, obtain the key through the creator. It is created only when no fingerprint rows exist, and fails with `KeyLost` or `KeyUnavailable` otherwise.
2. Re-read the target in one read snapshot.
3. Measure the plan using the shared plan runner's `Count` mode, which uses the same predicates apply deletes by.
4. Compute external exposure and retained local copies.
5. Compute the request and effect digests.
6. Issue a five-minute preflight token:
   - **Saga and Lexicon** use `MemoryReviewTokenCodec` with a new `ErasurePlan` purpose. The payload is the store, request digest, effect digest, and the dataset generation only when a label was measured. This works with the Covenant feature off.
   - **Covenant** uses `CovenantEnvelopeCodec` purpose `OperatorPreflight` with a new `CovenantErasurePreflightBody`. That body has its own format byte, so it can never decode as a set, correct, or curate body, nor they as it. It carries:
     - the request digest;
     - the authority epoch;
     - the dataset generation;
     - the key epoch;
     - the key-reclamation epoch;
     - `ReclaimsKey`;
     - the entry id;
     - both head version ids;
     - the effect digest;
     - issue and expiry times.

**Response:** `MemoryErasurePreflightDto`. It carries:
- the store, `MutationId`, and the request and effect digests (hex);
- the plan: content-free counts and flags, plus `CovenantErasurePlanFacts` or `LexiconErasurePlanFacts`;
- external exposure;
- retained local copies;
- scope-boundary notes;
- issue and expiry times, and the token.

**Refusals.**
- A prepare for a subject that no longer exists answers `MemoryErasure.SubjectErased` (410) when a receipt's subject digest matches the requested row id and no live row has that id.
- Otherwise it answers the store's `NotFound`.

### 6.2 Apply

1. **Validate and digest.** Validate the body shape, resolve the key through the provider, and compute the request digest from the request fields alone.
2. **Receipt probe.** Look up `memory_erasure_receipts` by `MutationId`:
   - **Match** (constant-time): **replay**. A replay returns the stored result with `Replayed = true` and needs no token, so it survives expiry and restarts.
   - **Present but different:** `Security.IdempotencyConflict` (409).
   - For Covenant, this probe runs under a short read lease, disposed before any closure is acquired.
3. **Token.** Decode the token and require its request digest to match the one just computed. Failure is `MemoryErasure.InvalidPreflight` (400).
4. **Leases.** Acquire the store's leases (§7–§9).
5. **Transaction.** One `BEGIN IMMEDIATE` transaction under `SqliteBusyRetry`:
   1. Re-probe the receipt by `MutationId`: replay on a match. Then check the subject digests: 410 if the subject was erased by another mutation.
   2. Re-read the exact target and compare every expected field. For Saga, also recompute the keyed `ContentBinding` over the live row and compare it with the token's (§5.2).
   3. Re-measure the plan, exposure and retained copies, and recompute the effect digest. A mismatch is `MemoryErasure.StalePlan` (409).
   4. Delete through the plan.
   5. `INSERT OR IGNORE` the fingerprint or fingerprints.
   6. Insert the receipt and its subject rows.
   7. **Authoritative absence proof:** re-run the plan's `Count` mode over every target and require zero.
   8. Revalidate any held lease.
   9. `COMMIT`.
6. **Post-commit.** Attempt the WAL checkpoint (§10(d)), and update the receipt when it succeeds.
7. **Result.** Return `MemoryErasureResultDto`.

### 6.3 Receipts (Core v13)

`memory_erasure_receipts`:

| Column | Holds |
|---|---|
| `MutationId` | Primary key; governed uppercase, dashed |
| `StoreCode` | `IN (1, 2, 3)` |
| `KeyId` | 16 bytes |
| `RequestDigest` | 32 bytes, keyed, non-content fields only |
| `EffectDigest` | 32 bytes, keyed |
| `ErasedItemCount` | ≥ 1 |
| `RemovedRowCount` | ≥ 1 |
| `RemovedLabelCount` | ≥ 0 |
| `RemovedRetirementSuppressionCount` | ≥ 0 |
| `AuthorshipEvidenceCode`, `ContextEvidenceCode`, `EmbeddingEvidenceCode`, `BackupEvidenceCode`, `OtherExternalEvidenceCode` | Each `IN (1..4)` |
| `RetainedCopiesMask` | ≥ 0 |
| `ScrubStateCode` | 1 Pending, 2 Verified |
| `ScrubPendingReasonMask` | ≥ 0 |

**Subjects.** `memory_erasure_receipt_subjects` has columns `(MutationId REFERENCES memory_erasure_receipts ON DELETE CASCADE, SubjectDigest BLOB(32))`. Its primary key is `(MutationId, SubjectDigest)`, and it has an index on `SubjectDigest`. There is one row per erased subject:
- each Saga memory, twins included;
- the Lexicon entry;
- the Covenant entry.

**Insert-time state.** A receipt is inserted with `ScrubStateCode = 1` (Pending). Its `ScrubPendingReasonMask` is:
- `WalCheckpointPending`;
- plus any **non-upgradable** reason already known inside the transaction: `FullTextSecureDeleteUnverified` and `VectorIndexScrubUnverified` (§10).

**Update guard.** One update-guard trigger permits exactly two changes:
- clearing the `WalCheckpointPending` bit;
- `ScrubStateCode` 1→2, only when the resulting mask is zero.

It permits nothing else. So a receipt that carries a non-upgradable reason can never become Verified.

**No timestamps.** Neither table has one.

**Lifetime.** Receipts and subjects have the fingerprints' lifetime (§13). Release leaves receipts intact. A re-created identity is a new row with a new row id, so it gets a fresh subject digest and can be erased again.

### 6.4 Results

`MemoryErasureResultDto` carries:
- `Store`, `MutationId`, `Replayed`, `EffectDigest`;
- `Local`:
  - `Outcome`: `Verified` or `RowsRemovedScrubPending`;
  - `PendingReasons`;
  - `WalCheckpointAttempt`;
  - `ErasedItemCount`, `RemovedRowCount`, `RemovedLabelCount`, `RemovedRetirementSuppressionCount`;
  - `SuppressionFingerprintRecorded`;
- `External`: `Revocation = NotPerformed` and one evidence value per channel;
- `RetainedLocalCopies`;
- `Notes`: scope-boundary notes, and the Covenant availability note (§9.2).

**Closed string enums:**

| Enum | Values |
|---|---|
| `MemoryLocalErasureOutcome` | `Verified`, `RowsRemovedScrubPending` |
| `MemoryErasureScrubPendingReason` | `WalCheckpointPending` (upgradable), `FullTextSecureDeleteUnverified`, `VectorIndexScrubUnverified` (neither upgradable) |
| `MemoryErasureWalCheckpointAttempt` | `Truncated`, `Busy`, `Unavailable`, `NotAttempted` |
| `MemoryExternalChannel` | `InferenceProviderAuthorship`, `InferenceProviderContext`, `EmbeddingProvider`, `EncryptedBackup`, `OtherExternal` |
| `MemoryExternalEvidence` | `Known`, `ReceiptWindow`, `NotRecorded`, `NotApplicable` |
| `MemoryExternalRevocation` | `NotPerformed` |
| `MemoryRetainedLocalCopy` | `SessionTranscripts`, `SearchAndSummaryDerivatives`, `Attachments`, `ResponseCaches`, `ApplicationLogs`, `AuditLog`, `BackupArchives`, `OtherLocalState` |
| `MemoryErasureNote` | `GlobalKeyStillProposableInCampaigns`, `UnresolvedScopeStopsMatchingOnResolution`, `OtherScopesUnaffected`, `CovenantDrainsInFlightTurns` |

**What never appears.**
- No value means "not disclosed".
- No DTO carries content, a content hash, a name or key outside the operator's own request echo, a provider name, or a disclosure-receipt count.
- The request and effect digests appear on the wire and in the receipt only. They are keyed and bind no content.

## 7. Saga erasure

**Target.** `SagaErasePrepareRequest(string MemoryId, string ExpectedContentHash, string? ExpectedClaimVersionId, Guid MutationId)`.
- A null `ExpectedClaimVersionId` means claimless, and it must still be claimless at apply.
- A non-GUID memory id is refused as an invalid target.

**Plan.** The target plus every twin: same scope and byte-identical `Content`, found by seeking `idx_saga_memories_scope`. No content index is added. For each memory, the plan covers:
- the Saga row of `CovenantArtifactPurgePlans`:
  - the full Annals chain, including review events and receipts;
  - `saga_memory_embeddings`;
  - the vector mirror, classified by `SagaVectorMirror` (§15.5);
  - attachment provenance;
- any sensitivity label;
- the retirement-suppression digest pair for the content and scope, computed with the shared helper and the retirement key. The retirement key is read, never created.

`saga_suppression_key` and the extraction watermarks are untouched.

**Leases.**
- If a label is present, apply acquires a `CovenantWriteLease` over the owner scope after resolving the key and **before** `BEGIN`, and forms `CovenantArtifactErasureAuthority.ForOrdinary`. Inside the transaction it re-reads the exact label and deletes it under `SensitivityRetentionPurge` authorization.
- If no label is present, the transaction re-proves that none has appeared since prepare.
- Both Saga erase routes declare `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)`. A route carries exactly one authority requirement, and the ordinary erasure authority used for labels requires exactly this one. The apply handler passes the issued context to the erase service.

**State of the target.** Pins do not block erase, and a retired memory can be erased.

**Exposure over the twin class.**
- Authorship is always `Known`. Extraction is the only Saga writer, and a structural test pins that.
- The backup window starts at the minimum `CreatedAt` across the target and its twins.

## 8. Lexicon erasure

**Target.** `LexiconErasePrepareRequest(LexiconCurationTarget Target, Guid MutationId)`, which carries the complete show target.
- The existing `TargetsEqual` compare-and-swap runs under `BEGIN IMMEDIATE`.
- Core must be at version 13 or later.
- There are no twins, because `(ScopeCampaignId, NameNormalized)` is unique.

**Refusals.** `daemon_state:*` names are refused on their own terms with `Lexicon.InvalidName` (400): "Unseen Servant daemon_state entries are managed by their daemon job and cannot be erased."

**Plan.**
- The Lexicon row of `CovenantArtifactPurgePlans`: the Annals chain including historical fact provenance, plus current fact provenance.
- The entry row.
- Any label, under the Lexicon exact-scope `CovenantWriteLease` pattern.

When a Global entry of the same name exists, the plan reports `GlobalEntryResurfaces`.

**FTS.**
- Core v13 enables `secure-delete` on `lexicon_fts` and merges the index once with `optimize` (§5.8). So from v13 on:
  - every token delete scrubs;
  - every token left by earlier non-secure deletes is gone, including retirement, correction and scribe merges.
- At the start of the erase transaction, `lexicon_fts_config` must read `secure-delete = 1`. If it does not, the transaction first enables it, then runs `optimize` to merge away earlier residue, then reads it back. If it still is not 1, the recorded verdict is `FullTextSecureDeleteUnverified` (§10(b)).
- A retired entry has no index row. Its tokens were scrubbed at retirement, or merged away by v13.

**Routes.**
- Both Lexicon erase routes declare `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)`, for the same reason as Saga.
- The preflight carries no content, so prepare needs no protected read authority.
- The erase service owns the single exact-scope write lease.

## 9. Covenant entry erasure

### 9.1 Target and authority

**Target.**
- `CovenantErasePrepareRequest(CovenantScope Scope, Guid? CampaignId, string Key, Guid EntryId, CovenantEraseHeadExpectation? Confirmed, CovenantEraseHeadExpectation? Proposed, Guid MutationId)`
- `CovenantEraseHeadExpectation(Guid VersionId, long LaneRevision)`

Both come from `covenant show`. A Global entry with a Proposed expectation is invalid.

**Authority.** Both routes require `LifecycleManage`.
- Prepare holds a `CovenantInstallationReadLease` through `CovenantProtectedJsonResult`, but only after the key was obtained.
- Apply never hands an exclusive lease to a protected result. It writes `Results.Json` with the protected-header hook.

### 9.2 Drain

**New codes.** `CovenantExclusiveOperation.CovenantEntryErasure = 9` and `CovenantLeaseKind.EntryErasure = 11`.

**Acquiring the drain.**
- `ICovenantOperationGate.AcquireEntryErasureAsync(entryScope, reclaimsKey, owner, ct)` returns a compound read-and-exclusive `CovenantEntryErasureLease`.
- The caller passes `reclaimsKey` **from the authenticated token**, which the effect digest also binds.
- The closure:
  - a Campaign entry that does not reclaim its key takes a **Campaign** closure;
  - a Global entry, or any reclaiming erase, takes an **Installation** closure.
- The kernel refuses to reclaim a key unless the held lease covers the installation.
- Every existing exclusive acquisition deny-list becomes an allow-list, so code 9 is refused everywhere else. `ClassifyOwner` refuses code 9, because it has no durable owner.

**Order of operations.**
- The owner is `CovenantExclusiveRecoveryOwner(MutationId, CovenantEntryErasure, effectDigest)`.
- After the drain, the lease's authority generation and epoch must equal the request's authority context.

**Dispositions.** Every disposition completes with `CancellationToken.None`.

| Situation | Disposition |
|---|---|
| Success | `CommitAndReopen` |
| Proven pre-commit refusal | `RollbackAndReopen` |
| Commit uncertain | Re-read the receipt **on a fresh connection**, after disposing the failed transaction. Present means `CommitAndReopen`, absent means `RollbackAndReopen`. If the re-read itself fails, `KeepClosed` plus `Covenant.ManualRecoveryRequired`, which clears on host restart. |

**Health evidence.** A committed erase appends absent deltas and moves the canonical search sequence, so after its own `COMMIT`, while it still holds the closure, it republishes `CanonicalMutation` on the connection that committed, and only then reopens the closure, so no exclusive transition can publish between the two. A refused or rolled-back erase changes nothing and republishes nothing. DESIGN §21.15.7 states the same rule.

**Availability costs.** These are stated in the preflight notes, the CLI text, and DESIGN:
- The drain waits up to the gate's 30-second bound for covered turns to finish. If it cannot drain, the erase fails with 503 and changes nothing.
- Turns that start during the closure run without Covenant content.
- Installation-coverage leases fail fast during any closure. These include backups, prepares, inventory, and the accelerator.

### 9.3 Transaction

The transaction runs under `CovenantEntryErasure` authorization and `SqliteBusyRetry`. It first verifies:
- the dataset generation;
- the key-reclamation epoch;
- the key epoch;
- both heads against their expectations;
- for a Campaign entry, the live Campaign row.

It then deletes, in this order:

1. **Search documents.** `covenant_search_documents` by `EntryId`, synchronously, **whenever the table exists**. The `covenant_fts` secure-delete read-back only sets the delete-time full-text verdict.
2. **Outbox.** Pending `covenant_search_outbox` rows for the entry's `SearchRowId`s. Then one absent delta per erased head is appended at `CanonicalSearchSequence + 1`, and the sequence advances once.
3. **Mutation receipts** for the entry:
   - by the new `EntryId` column;
   - by `ResultingVersionId` among the entry's versions;
   - legacy `NoChange` rows with a null `EntryId` in the same scope, Campaign and lanes, committed at or after the entry's `CreatedAtUtc`. These are deleted conservatively and counted. DESIGN documents the consequence: a very late replay of another key's `NoChange` mutation in that window answers `StaleSnapshot` instead of replaying.
4. **Canonical rows.** `covenant_version_attachment_provenance`, then `covenant_heads` (the key-epoch trigger fires), then `covenant_versions` in one statement (review events and decision receipts cascade), then `covenant_entries`.
5. **Curation rows** of the erased subject: scope, Campaign and key, both lanes, every epoch. Heads go before versions, and receipts are matched regardless of epoch.
   - **Exception:** when the key is not reclaimed, a Campaign Confirmed-lane subject whose current head is masked is kept. That mask is operator policy about the Global key.
   - `RetainsCampaignMask = masked && !ReclaimsKey`.
6. **Key reclamation**, when no `covenant_heads` or `covenant_entries` row in any scope still names the key:
   1. delete every curation row for the key in every scope;
   2. delete the `covenant_key_epochs` row;
   3. compare-and-swap `KeyReclamationEpoch + 1`.

   The preflight warns that outstanding Covenant preflights go stale installation-wide.

**Absence proof.** The in-transaction proof counts every target above. For the outbox it counts rows whose `DesiredVersionId` is an erased version, so it excludes this erase's own content-free absent deltas.

**Not touched.** Review markers, turn receipts, disclosure receipts, and labels on derived artifacts.

**Plan facts.** The plan reports:
- versions per lane;
- provenance leaves;
- mutation receipts;
- curation rows;
- outbox rows;
- search documents;
- `ReclaimsKey`;
- `RetainsCampaignMask`;
- `GlobalConfirmedResurfaces`;
- `IsPinned`;
- the count of affected Campaigns.

## 10. Verified local erasure

`Local.Outcome` is `Verified` only if all four of these hold. Otherwise it is `RowsRemovedScrubPending`, and the result lists the reasons that remain.

**(a) Rows removed: the in-transaction absence proof.** This is §6.2 step 5.7. It re-runs the plan's `Count` mode inside the erase transaction and must find zero for every target, including:
- the exact rows: every Saga memory id, the Lexicon `EntryId`, and the Covenant `EntryId` and version ids;
- the subject-keyed targets: twins, curation, key epochs, retirement pairs, and mutation receipts;
- for the Covenant outbox, the rows whose `DesiredVersionId` is an erased version (the erase's own content-free absent deltas are excluded).

A non-zero count aborts the transaction and returns `MemoryErasure.ErasureIncomplete` (500). No receipt is written, because a receipt exists only for a commit that proved absence.

The transaction is `BEGIN IMMEDIATE` under the store's authorization. No second post-commit re-read is needed: row ids are random, so nothing could re-create one of those rows.

**(b) Full-text verdict, recorded when the rows are deleted:**
- Lexicon:
  - requires `secure-delete = 1` on `lexicon_fts` **at the start** of the erase transaction;
  - if the flag was off, the transaction enables it, runs `INSERT INTO lexicon_fts(lexicon_fts) VALUES('optimize')` to merge away earlier residue, and reads the flag back before deleting;
  - otherwise the verdict is `FullTextSecureDeleteUnverified`.
- Covenant: `covenant_fts` reads `secure-delete = 1` whenever the accelerator exists.
- Saga: not applicable.

`FullTextSecureDeleteUnverified` never upgrades.

**(c) Vector mirrors.** Every vector mirror the plan touched must be absent or a plain table. A legacy `vec0` virtual table is unreachable residue and records `VectorIndexScrubUnverified`, which never upgrades.

**(d) WAL checkpoint.** A checked `PRAGMA wal_checkpoint(TRUNCATE)` must return `busy = 0` with no remaining frames. It runs:
- on a dedicated, unpooled `GrimoireOrdinaryFreshConnectionKind.ReadWrite` connection;
- with `busy_timeout = 250`;
- after every transaction of the request has been disposed;
- for the Covenant, after the closure reopens.

Success clears `WalCheckpointPending`. A busy checkpoint, or a connection that could not be opened, leaves the reason set, and the result says which of the two happened.

**Moving from Pending to Verified.** A receipt moves only when `WalCheckpointPending` was its only reason and a later checkpoint succeeds. Two paths can do that:
- a replay of the same apply;
- `POST /api/memory/erasure/scrub` (§5.7), which takes no body and retries every pending receipt.

**Wording.** The result never claims physical erasure. Page-level `secure_delete = ON`, which every Grimoire connection already verifies, together with WAL truncation are logical scrubs. DESIGN §5.4.7 already states that.

## 11. External exposure

Evidence is:
- computed in prepare;
- recomputed in apply before any delete;
- bound into the effect digest;
- persisted on the receipt.

Counts are never bound or returned.

| Store | Channel | Rule |
|---|---|---|
| Saga | `InferenceProviderAuthorship` | `Known`. Extraction is the only writer, and a structural test pins that. |
| Saga | `InferenceProviderContext` | `NotRecorded` |
| Saga | `EmbeddingProvider` | `Known`. Every insert and correction embeds. |
| Saga | `EncryptedBackup` | `ReceiptWindow` if at least one backup operation's latest receipt is at or after the class's minimum `CreatedAt`. Otherwise `NotRecorded`. |
| Lexicon | `InferenceProviderAuthorship` | `Known` if any claim version's origin is `AgentAsserted` or `AgentExtracted`. Otherwise `NotRecorded`. |
| Lexicon | `InferenceProviderContext` | `NotRecorded` |
| Lexicon | `EmbeddingProvider` | `NotApplicable` |
| Lexicon | `EncryptedBackup` | Unbounded: `ReceiptWindow` if any backup operation's latest receipt exists, otherwise `NotRecorded`. A Lexicon entry records no creation time, and its Annals claim can open after the entry exists, so no window start is safe. |
| Covenant | `InferenceProviderAuthorship` | `Known` if any version's origin is agent. |
| Covenant | `InferenceProviderContext` | `ReceiptWindow` if any nonrevocable, Covenant-derived provider-dispatch receipt is at or after the entry's `CreatedAtUtc`. The window is time only, with no generation predicate. Otherwise `NotRecorded`. |
| Covenant | `EmbeddingProvider` | `NotApplicable` |
| Covenant | `EncryptedBackup` | As Saga, windowed from the entry's creation. |
| All | `OtherExternal` | `NotRecorded` |

**Amendment (Task 12 review).** The Lexicon `EncryptedBackup` row once windowed from the claim's creation. That window could under-report, because a claim opened after the entry (a scribe with the Annals off, then an operator correction) excluded backups taken in between, so the row is now unbounded.

**CLI rendering order.** All of this goes before the confirmation prompt, on the diagnostics stream, in every mode:
1. `CovenantExternalRetentionDisclosure.DestructiveOperationText`. It is amended once to name "selective erasure", and remains the one golden copy.
2. One line per channel, from a closed map.
3. Help targets, resolved on the client.

Provider names never cross the wire.

## 12. Retained local copies and the in-flight limit

**Retained local copies are reported, never purged.** The list is a constant per store and always present:

| Store | Retained copies |
|---|---|
| All three | `SessionTranscripts`: any Session transcript may quote the content (source turns, tool calls and results, assistant restatements, forks). |
| All three | `SearchAndSummaryDerivatives`: `Entries_fts`, entry embeddings, Session summaries, Tapestry nodes. |
| All three | `ResponseCaches`: cached inference responses. |
| All three | `ApplicationLogs` |
| All three | `BackupArchives` |
| All three | `Attachments`: the attachment sources behind attachment-derived memories, Lexicon facts, and Covenant versions. |
| Any | `AuditLog`, whenever inference audit files exist. |
| All three | `OtherLocalState`, an always-present catch-all: Apprentice and subagent checkpoints, batch checkpoints and files, and workspace files an agent wrote. |

**In-flight limit.** Only the Covenant drains in-flight turns. A Saga memory or Lexicon entry already loaded into a running turn can still be sent until that turn ends. DESIGN states this. The result carries no note for it, because it is not item-specific.

## 13. Lifecycle

| Path | Fingerprints, receipts, subjects | Key |
|---|---|---|
| Retention prune; `DELETE /api/saga[/{id}]`; `DELETE /api/memory/lexicon/{name}`; `delete_lexicon` | kept | kept |
| Memory reset (every scope, whole-store or Campaign); embeddings reset; workspace reset | kept | kept |
| Campaign or Session deletion; Covenant family reset or reinitialize | kept | kept |
| Factory reset (Grimoire-level) | kept | kept |
| Release; operator re-creation | the one fingerprint deleted; receipts kept | kept |
| `reset-key` | unverifiable rows deleted | minted when absent |
| Full installation reset (`--global` / `--all`) | removed with the Grimoire | removed through the credential catalog |
| Backup | carried inside the database file; inert without the OS key | never exported |
| Restore | destination-authoritative (§14) | destination's, retained |

**Reset residue.** The premise holds unchanged, because no new table enters the residue list.

**Reporting.**
- The retention status reports the fingerprint, receipt, and subject counts.
- The factory-reset preview states that N erasure fingerprints remain in force.

## 14. Restore cannot resurrect

### 14.1 Destination read

`ReadDestinationErasureEvidenceAsync` reads the destination's fingerprints and receipts as values. It runs:
- in `BuildPlanAsync`, under the held maintenance lock;
- in `ExecuteAsync`, before extraction.

It never holds a live handle. The Grimoire is read **first**, so a restore whose destination holds no evidence never touches the keychain.

| Grimoire | Rows | OS key (presence probe or read) | Result |
|---|---|---|---|
| readable | table absent, recorded Core < 13, or 0 rows | not consulted | `None`; proceed |
| readable | ≥ 1 row | `Present` and every row's `KeyId` matches | `Present(keyId, rows)` |
| readable | ≥ 1 row | `Present` and some `KeyId` differs, or `NotFound` | refuse `backup.restore_erasure_key_missing` |
| readable | ≥ 1 row | `Unavailable` | refuse `backup.restore_erasure_key_unavailable` |
| absent or unreadable | — | `NotFound` | `None`; proceed: no key means no committed evidence |
| absent or unreadable | — | `Present` or `Unavailable` | refuse `backup.restore_erasure_evidence_unavailable` |

**Ways out.** A refusal names the ways out that can work in the state it describes:
- make the key or the credential store readable and retry (put the original key back, or unlock the credential store);
- run `arcanum memory erasure reset-key` on the destination (§5.7), which discards the erasures it cannot prove;
- run a full installation reset.

Two refusals narrow that list (amended after the restore-refusal review):
- An `evidence_unavailable` refusal caused by an unreadable Grimoire does **not** name `reset-key`. The host cannot open the database for `reset-key` to run, so the refusal names only the remedies that can work: make the Grimoire readable again and retry, or run a full installation reset.
- The malformed-key refusal (`key_unavailable` over an item that is not a valid key) carries an unlock-first caution. Unlock the credential store and retry first, and only if the item is confirmed malformed remove it with the OS credential tool before running `reset-key`. Removing a key makes every erasure fingerprint unverifiable, and the reset then discards them.

### 14.2 Staged drain

Conditions: the destination read is `Present` with at least one row, and the staged copy has journal rows, or any tier recorded below head. This covers Core, the Covenant canonical tier, and the accelerator. A tier that is at head but unhealthy is not a drain failure: no pass can repair it, and the evidence step's absence verification (§14.3) decides it, by proving every purge or refusing with `backup.restore_erasure_verification_failed`.

**When it runs,** `PrepareStagedGenerationAsync` alternates `InstallAsync` with bounded `GrimoireSchemaBackfillRunner` passes over the staged connection, until every tier is at head and every journal is empty.

**Refusal.** Any of the following refuses with `backup.restore_erasure_evidence_unjoinable`:
- a throw;
- a refusal (including `TransitionUnresumable`, which now surfaces as this typed code instead of a generic failure);
- a pass that makes no progress.

The refusal happens before any safety backup or rename.

**Cancellation is not a refusal** (amended after the staged-drain review). A caller cancellation during the staged drain is reported as a cancellation, exit 130, with nothing displaced, exactly as a cancellation at any other point before commit is.

**Destinations without evidence** keep today's behaviour exactly.

### 14.3 Core evidence step

`ReconcileStagedMemoryEvidenceAsync` runs for every `ReplaceInstallation`, in both Covenant gate states:
- after `MigrateAsync` and the drain;
- before the Covenant arm;
- as one `BEGIN IMMEDIATE` transaction under `CovenantFamilyMaintenance` plus `SensitivityRetentionPurge` authorization. Staging never uses the new kind, because older staged canonical tiers do not know it.

Every table is probed with `TableExistsAsync`. When the destination is `None` and the staged table is absent, the fingerprint and receipt work is skipped entirely.

Steps:

1. **Full-text.** Enable and read back FTS `secure-delete` on `lexicon_fts` and on `covenant_fts` when it is present.
2. **Disclosure.** Fold the unfolded staged disclosure tails (§15.4), then join the destination's effective disclosure buckets into staging. This join moves out of the Covenant arm, so a restore with the gate off no longer drops the destination's buckets.
3. **Match.** Compare staged rows against the destination fingerprints, using the same Core fingerprint function:
   - Saga on `(ScopeKindCode, Campaign, Content)`;
   - Lexicon on `(scope, Name.Trim().ToUpperInvariant())`, computed from each staged row's `Name`;
   - Covenant on `(ScopeCode, Campaign, NormalizedKey)`.

   Stores the destination holds no fingerprint for are skipped.
4. **Purge**, mirroring the live erase's side effects:
   - Saga and Lexicon through the shared plan runner, plus labels. The owning Session's tainted-artifact count is recounted, not folded to zero.
   - For every purged Saga memory, delete its retirement-suppression pair using the staged retirement key, which is read, never created.
   - Covenant entries through the shared `CovenantEntryErasurePlan`, including the key-reclamation rule. After any Covenant purge, set the applied FTS tuple to `FullRebuildRequired`.
5. **Destination-authoritative evidence.**
   - Delete all staged `memory_erasure_fingerprints`, receipts, and subjects, then insert the destination's rows verbatim.
   - Archive rows under a foreign `KeyId`, and archive rows the destination has since released, are dropped and counted.
   - A plain union would undo releases.
6. **Post-conditions,** all before `COMMIT`:
   - no staged row matches;
   - every purged identity counts zero across every target, including the retirement pairs;
   - the fingerprint, receipt, and subject sets equal the destination's;
   - after any Covenant purge, the applied tuple is null.

   A failure is `backup.restore_erasure_verification_failed`, followed by a rollback.
7. **After commit.** Run a checked staged WAL checkpoint. For `ReplaceInstallation`, the extracted archive database under `work/extract` is deleted, with a check, right after staging composes.

### 14.4 Modes, reporting, and the other restore paths

**Modes.**
- `Reject` still refuses archives carrying Covenant rows, even when every one is an erased entry. That is conservative, and documented.
- `RestoreProtectedState` preserves the archive's Covenant data **except the entries this installation erased**.
- `PurgeProtectedState` runs the evidence step first.

**Reporting.**
- `BackupRestorePlan.DestinationErasureEvidence` carries the status and per-store counts.
- `BackupRestoreReconciliation.ErasureApplication` carries the removed Saga, Lexicon, and Covenant counts; the retirement pairs removed; the fingerprints and receipts joined; the archive rows dropped; and the scrub status.
- A post-commit absence proof in `ReconcileAsync` turns any match into `ReconciliationRequired`.
- The frozen restore effect digest and the V2 journal payload are unchanged.

**Other restore paths.**
- **New-profile-root restores** are outside every arm. The plan warns when this installation holds an erasure key.
- **Selective Session import** relies on the chokepoints, because it writes into the live database.

## 15. Adjacent defects fixed in this slice

### 15.1 The remainder of #225

- The Saga and Lexicon purge-policy text names the Annals claim, its versions, heads, edges, review events and receipts, and, for Lexicon, the historical fact provenance. The false Saga FTS wording goes.
- The type-closed pin in `CovenantDerivedOutputInventoryTests` is replaced by a plan-closed structural test. For each Annals store S and kind K:
  - the plan's projections contain every `AnnalsErasurePlan.ForStore(S)` table, in order, before the artifact;
  - each `DeleteBy` equals the `ForSubjectQuery` predicate;
  - the artifact table is S's subject table.
- Runtime tests erase a labelled Saga memory that carries a full Annals graph through the kernel and through the staged purger, and assert that no orphan claim remains.
- A shared no-orphan-claim assertion ends every erasure-path test.
- Stale remarks in `DataRetentionService` and DESIGN §10.17 and §21.12 are corrected.

### 15.2 Covenant curation

**Binding epoch.** Canonical v6 adds `covenant_key_epochs.IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0)`. This is the epoch pins and masks bind to.
- Existing key rows are backfilled with their current `KeyEpoch`, so every live curation head stays live without rewriting append-only rows.
- Key rows created after v6 get `0`.
- A missing key row reads as `0`.

That keeps keyless curation working when a key is first created, and ordinary writes never touch the binding epoch. Every key-row deleter purges the key's curation rows in the same transaction:
- entry-erasure reclamation;
- family reset and factory erasure (after the fix below);
- restore purge;
- staged reclamation.

A key's earlier curation can therefore never re-bind to a later key. Tokens stay guarded by the `KeyReclamationEpoch` compare-and-swap.

Column meanings:
- `covenant_curation_heads.KeyEpoch` and `covenant_curation_versions.KeyEpoch` store the binding epoch.
- `covenant_curation_receipts.KeyEpoch` keeps the dependency epoch the replay digest binds.
- `CovenantCurationSubject.KeyEpoch` is renamed `KeyDependencyEpoch`, and a binding epoch is added.
- Pin and mask reads join on `COALESCE(k.IncarnationEpoch, 0)`.

**Replay.** A curation replay reads the receipt by `MutationId` first, recomputes the request digest from the request plus the receipt's stored dependency epoch, and never decodes the token. The dead expiry check is removed.

**Reset.** A single `CovenantCanonicalContentTables.InDeletionOrder` feeds:
- the family-erasure transaction;
- its storage-health proof;
- the restore inspector;
- the reset inventory.

Curation rows are therefore erased by reset and factory erasure. A manifest-closed test derives the expected set from the canonical catalog and checks foreign-key order.

**Staging.** The staging handler refuses a pinned lane early. `CovenantRetirementTarget` gains `IsAgentWithheld`.

**Receipts.** `covenant_mutation_receipts.EntryId` is populated for both outcomes.

**Canonical v6 statement order** (one step, no sweep):
1. Add the authorization kind in code.
2. `ALTER TABLE covenant_mutation_receipts ADD COLUMN EntryId TEXT NULL`, plus its index.
3. `ALTER TABLE covenant_key_epochs ADD COLUMN IncarnationEpoch …`.
4. Drop `covenant_key_epochs_guard_overflow`; `UPDATE covenant_key_epochs SET IncarnationEpoch = KeyEpoch`; recreate the overflow guard; add `covenant_key_epochs_guard_incarnation`, which makes the column immutable.
5. Drop the delete guards on curation versions and curation receipts, then purge the reset-defect leftovers: heads, then versions, then receipts, for keys that have no key row and a nonzero `KeyEpoch`.
6. Recreate every rewritten guard with `arcanum_covenant_entry_erasure_authorized()` admitted alongside the authorizations it already admits:
   - the entries, versions, provenance, mutation-receipt, curation-version and curation-receipt delete guards: owner cleanup, family maintenance, or entry erasure;
   - the outbox delete guard: accelerator synchronization, family maintenance, or entry erasure.

   Add two new guards that admit owner cleanup, family maintenance, or entry erasure: `covenant_key_epochs_guard_delete` and `covenant_curation_heads_guard_delete`. That set keeps every existing deleter working:
   - Campaign cleanup under owner cleanup;
   - family reset, factory erasure, and the restore purger under family maintenance;
   - staged reclamation under family maintenance.
7. Rewrite `covenant_heads_key_epoch_insert`, `_update`, and `_delete`, so that a new key row is inserted with `IncarnationEpoch = 0` and the conflict branch leaves the column alone.

**Tests.** A v5-to-v6 migration test is seeded with live pins, stale pins, keyless pins, and reset leftovers. It proves:
- live stays live;
- stale stays inert;
- keyless stays live across the first operator `set`;
- leftovers are purged.

### 15.3 Purge coordinator reporting

`CovenantSensitiveRetentionPurgeCoordinator` dispatches items one at a time and classifies each from its own progress:

| Outcome | Classification |
|---|---|
| The kernel erased the item | `Purged` |
| Blocked | `Blocked`; every later item is marked `Blocked` without being examined |
| Examined but not erased | Re-read under the held lease: `Unlabeled` if absent, `Blocked(AuthorityStale)` if present |

Label resolution fails closed on `SqliteException`.

### 15.4 The disclosure fold

**Live producer.** It folds each acknowledged receipt into `external_disclosure_state` in the journal's own transaction, using `CovenantDisclosureStateAlgebra.IncrementLocal`.
- It folds the subject's whole unfolded tail and advances `disclosure_subject_state.LastFoldedOrdinal`.
- When the tail is longer than one receipt, the bucket is marked `LowerBound`.

**Effective reader.** In **one** `BEGIN DEFERRED` snapshot, it returns the persisted buckets plus an in-memory fold of every unfolded tail, weakened to `LowerBound`. A Core v13 partial index, `idx_disclosure_subject_state_unfolded`, finds those tails. The reader feeds:
- the reset, factory, and restore previews;
- the restore destination read.

No backfill sweep is needed.

**Restore.** Staging folds the unfolded staged tails, as `LowerBound`, before the join (§14.3).

**Seed.** `CovenantRetentionSeed` now seeds real receipts through the journal.

### 15.5 Vector mirrors

One `SagaVectorMirror` helper replaces the process-flag gate at all eight store sites. The shared plan runner uses it in `Count`, `Delete`, and staged mode alike. It:
- probes `sqlite_master` inside the caller's transaction;
- classifies the mirror as `Absent`, `PlainTable`, or `LegacyVirtualTable`;
- deletes a plain-table mirror whatever the process flag says;
- writes only when the flag is on;
- skips a legacy `vec0` virtual table, which the shipping runtime cannot open, reporting it as unreachable residue. That residue is reported as `VectorIndexScrubUnverified`, never as a failure.

### 15.6 Agent tool refusals

- `delete_lexicon` refuses retired and pinned entries:
  - a tool-level pre-check;
  - `Lexicon.RetiredMutationRefused` for retired entries;
  - a new `Lexicon.PinnedMutationRefused` (409) for pinned entries;
  - an authoritative in-transaction check through an agent-origin overload of `DeleteByNameAsync`.
- The operator's delete is unchanged.
- **Covenant.** Pinned and fingerprinted agent refusals share the operator-managed wording (§5.4), so a Covenant tool result cannot tell an erased key from a pinned one.
- **Lexicon.** A pin does not block agent scribes (a settled Lexicon curation decision), so on `scribe_lexicon` the operator-managed refusal comes only from a fingerprint. That is an **accepted, bounded oracle**, stated in DESIGN: the agent already holds the name it tried, and it learns only that the operator withheld that name in this scope. It learns nothing about content.

## 16. Finishing #78

- **Search.**
  - Saga hits in `memory search` carry `SagaLifecycle` and `SagaEligibility`.
  - One Core eligibility classifier replaces the two copies.
  - `SagaRetrievalEligibility` becomes a string-only wire enum; API §8.30 documents the change.
  - The CLI prints Saga lifecycle the way it prints Lexicon lifecycle.
- **Listing and explain.**
  - `saga list` gains a `State` column.
  - `memory explain` counts only retrievable Saga rows.
- **Pruning.** `data prune --dry-run` renders the Saga pin exemption, and API §8.20 documents `sagaCuration`.
- **Covenant `show`.** `CovenantDetailDto` gains per-lane curation state (pinned, masked, curation revision), read in the same snapshot.
  - Human `show` prints version id, lane revision, lifecycle, origin, rendered hash, byte cost, pin and mask state, and curation revision.
  - `--history` prints the version id and rendered hash.
  - The CLI JSON gains `RenderedHash` and curation state.
  - `show` stays content-free by rule, and #78's "show full content" wording is amended for the Covenant.
- **Cross-store isolation.** A reusable `MemoryStoreSnapshot` test helper proves that every per-item verb leaves the other stores' bytes unchanged and actually changes the target store. The covered verbs are:
  - Saga, Lexicon, and Covenant curation;
  - bulk review apply for each store;
  - erase and release for each store;
  - the legacy deletes.
- **Doc drift.**
  - API §1 lists the fourteen Covenant management routes.
  - Command Reference: `Next:` is corrected, and `saga delete` reads "deleted, not erased".
  - `MemoryEndpoints` retention strings are corrected, and the dead string is removed.
  - DESIGN corrections:
    - the §5.4.7 Lexicon pin row;
    - the pin-report claim in §10.26.3;
    - the "unmapped query" and verb-list claims in §10.22.6;
    - the claim that a turn "aborts at revalidation".
  - The OATH #78, #98, and #100 rows.

## 17. Surfaces

### 17.1 HTTP

All routes are authenticated static POSTs with typed bodies, except the status GET. None is on `/v1`. Every erase, release, status, scrub, and reset-key response is protected: `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0`, and no validators. **Authority.** Each route declares exactly one operator authority requirement. The erasure-route inventory test pins each one.

| Routes | Requirement |
|---|---|
| Saga and Lexicon erase (prepare and apply) | `SensitivityRetentionPurge` |
| Covenant erase (prepare and apply) | `LifecycleManage` |
| Every release route | `LifecycleManage` |
| Scrub, and reset-key (prepare and apply) | `LifecycleManage` |
| The read-only status GET | none; authentication only, because it returns only counts and states |

Operator authority is issued in both Covenant gate states, and it is refused on a host-tools-tainted installation.

| Route | Name | Request → Response |
|---|---|---|
| `POST /api/memory/saga/erase/prepare` | `PrepareSagaMemoryErasure` | `SagaErasePrepareRequest` → `MemoryErasurePreflightDto` |
| `POST /api/memory/saga/erase` | `EraseSagaMemory` | `SagaEraseRequest` (+`PreflightToken`) → `MemoryErasureResultDto` |
| `POST /api/memory/saga/release` | `ReleaseSagaErasure` | `SagaErasureReleaseRequest(SagaMemoryScopeKind ScopeKind, Guid? CampaignId, string Content)` → `MemoryErasureReleaseResultDto` |
| `POST /api/memory/lexicon/erase/prepare` | `PrepareLexiconEntryErasure` | `LexiconErasePrepareRequest` → `MemoryErasurePreflightDto` |
| `POST /api/memory/lexicon/erase` | `EraseLexiconEntry` | `LexiconEraseRequest` (+`PreflightToken`) → `MemoryErasureResultDto` |
| `POST /api/memory/lexicon/release` | `ReleaseLexiconErasure` | `LexiconErasureReleaseRequest(LexiconCurationScope Scope, string Name)` → `MemoryErasureReleaseResultDto` |
| `POST /api/memory/covenant/erase/prepare` | `PrepareCovenantErasure` | `CovenantErasePrepareRequest` → `MemoryErasurePreflightDto` |
| `POST /api/memory/covenant/erase` | `EraseCovenantEntry` | `CovenantEraseRequest` (+`PreflightToken`) → `MemoryErasureResultDto` |
| `POST /api/memory/covenant/release` | `ReleaseCovenantErasure` | `CovenantErasureReleaseRequest(CovenantScope Scope, Guid? CampaignId, string Key)` → `MemoryErasureReleaseResultDto` |
| `GET /api/memory/erasure` | `GetMemoryErasureStatus` | → `MemoryErasureStatusDto` |
| `POST /api/memory/erasure/scrub` | `ScrubMemoryErasures` | → `MemoryErasureScrubResultDto` |
| `POST /api/memory/erasure/reset-key/prepare` | `PrepareMemoryErasureKeyReset` | no body → `MemoryErasureKeyResetPreflightDto` |
| `POST /api/memory/erasure/reset-key` | `ResetMemoryErasureKey` | `MemoryErasureKeyResetRequest(string PreflightToken)` → `MemoryErasureKeyResetResultDto` |

**Schema not ready.** Routes whose Core or canonical tier has not reached this slice's version return a retryable `MemoryErasure.Unavailable` (503).

**New error codes.**

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

**Reused codes, unchanged:** `Saga.NotFound`, `Saga.StaleContent`, `Lexicon.InvalidName`, `Lexicon.StaleCurationTarget`, `Covenant.RevisionConflict`, `Covenant.StaleSnapshot`, `Covenant.ForbiddenAuthority`, `Covenant.Unavailable`, `Covenant.MaintenanceFailed`, `Covenant.ManualRecoveryRequired`, `Security.IdempotencyConflict`.

**Restore blocker codes** are CLI strings: `backup.restore_erasure_key_missing`, `backup.restore_erasure_key_unavailable`, `backup.restore_erasure_evidence_unavailable`, `backup.restore_erasure_evidence_unjoinable`, `backup.restore_erasure_verification_failed`.

**Covenant `set` additions.** The prepare effect gains `ReleasesErasureFingerprint`, and the result gains `ReleasedErasureFingerprint`. Both are trailing optional fields. The Saga correction result gains `ReleasedErasureFingerprint`.

### 17.2 CLI

Each erase verb is a thin HTTP client. It runs:
1. `show`;
2. prepare;
3. render the plan, notes, and the external disclosure;
4. confirm through `IConfirmationPrompt` (or `--yes`);
5. apply.

**Output and exit codes.**
- `--json` writes exactly one document to stdout: the result, or `MemoryErasureCancellationPayload` on decline, which exits 0.
- The result prints its `MutationId`.
- Exit codes follow `CliFailureExit`.

**Commands:**
- `arcanum memory saga erase <id> [--expected-content-hash <hex>]`
- `arcanum memory saga release --file <path|-> [--campaign <guid> | --scope global|campaign|unresolved|unclassified]`. The content is read from a file or stdin, so it never reaches shell history. `--file -` requires `--yes`.
- `arcanum memory lexicon erase <name> [--campaign <guid>]`
- `arcanum memory lexicon release <name> [--campaign <guid>]`
- `arcanum memory covenant erase <key> [--campaign <guid>]`
- `arcanum memory covenant release <key> [--campaign <guid>]`
- `arcanum memory erasure status`
- `arcanum memory erasure scrub`
- `arcanum memory erasure reset-key`

**Positional identities.** Lexicon names and Covenant keys are positional, as they are on every existing Lexicon and Covenant verb. The Command Reference states the shell-history residue.

**What does not change.**
- There is still no generic `memory erase` or `memory delete`, and `Memory_has_no_generic_delete_command` extends to `erase`.
- Erase is not added to search actions or to review actions.
- `CliSurfaceTests` gains the new Covenant verb count (12).

## 18. Documentation

**`docs/Arcanum.DESIGN.md`:**
- New §21.15, "Selective hard erasure and erasure fingerprints".
- Updates:
  - §5.4.5a (the Core v13 narrative);
  - §5.4.7 (lifecycle rows);
  - §5.4.9 (the restore convergence sentence);
  - §10.6.2 and §10.6.3;
  - §10.13 (the fold);
  - §10.17;
  - §10.19.3, §10.19.9, §10.19.10 and §10.19.13;
  - §10.20.1 and §10.20.5;
  - §10.21 (the drain limit for Saga and Lexicon);
  - §10.22.6;
  - §10.25.1 (the restore-staging drain exception);
  - §10.26.2, §10.26.3 and §10.26.6;
  - §11.2.1 (the credential inventory);
  - §16.2;
  - §21.12;
  - §21.13.3 and §21.13.6.

**`docs/Arcanum.API.md`:** the §1 table, §8.20, §8.23, §8.28, §8.30 and §8.33, and a new §8.35 "Selective erasure".

**`docs/Arcanum.Command.Reference.md`:** the `arcanum memory` family, including the new `memory erasure` group; `saga delete`; `backup restore`; and `data`.

**Other files:**
- `docs/Arcanum.CommandMap.json`: regenerated.
- `docs/Arcanum.Engineering.md`: erasure, the disclosure fold, and fingerprint custody.
- `docs/Arcanum.OATH.md`: the #78, #98 and #100 rows, and §15.4.
- `docs/Arcanum.DEBUGGING.Human.md`: the restore blocker codes, and key-loss recovery.
- `docs/Compendium.README.md`: unchanged.

## 19. Testing

### 19.1 Discipline

- **Strict red-green-refactor.** Every production behaviour lands test-first, and the test is watched failing for the intended reason.
- **Production entry points.** Acceptance tests enter through the mapped route or the registered CLI verb. They establish preconditions through production write paths:
  - a memory by the store's insert;
  - a fingerprint by an actual erase;
  - a retirement by the retire route.
- **Isolated credentials.** Tests inject `InMemoryOsCredentialStore`. Tests that span the host and the CLI container share one instance, because `TestCredentialStorePolicy` creates a fresh store per container.
- **No keychain at composition.** A guard proves that composing `AddArcanumInfrastructure` never touches a credential store.

### 19.2 End-to-end proofs

1. **Saga resurrection.** Erase a Saga memory through the route. Then run extraction through its own service with the same conclusion. The row does not come back, the cursor advances, and **the embedding provider fake receives no call** for that candidate.
2. **Lexicon.** Erase a Lexicon entry that was corrected and retired before v13. No token of it remains in `lexicon_fts_data`, and `scribe_lexicon` of that name is refused.
3. **Covenant.** Erase a Covenant entry while a turn is in flight. The erase waits for the drain, and a later agent proposal of the key is refused at staging.
4. **Restore.** Take a backup, erase, then restore. The item is absent, and the evidence sets equal the destination's. An archive at an older Core version is drained, then purged. An archive taken while the memory was retired leaves no retirement digest.
5. **Release.** Release, then extract again: the item may come back. Erase, release, re-create, then erase again: the second erase succeeds.
6. **Operator re-creation.** Erase a Covenant key, `set` it as the operator: the fingerprint is released, the result says so, and a later restore keeps the entry.
7. **Key loss.** Delete the OS key while rows exist:
   - extraction defers before the model call;
   - scribe and proposal fail closed;
   - `status` reports `Lost`;
   - `reset-key` discards the unverifiable rows and unblocks the writers.
8. **Upgrade window.** Write through every chokepoint while the v12 sweep is pending.

### 19.3 Structural pins

- Extraction is the only Saga writer.
- No Saga, Lexicon, or Covenant item owns a managed-file artifact.
- `BackupSecretSnapshotReader` never reads the erasure-key account.
- Only the named deleters touch the evidence tables.
- New log statements are content-free.
- No agent tool references the erase or release services.
- The authority metadata of every erase, release, status, scrub, and reset-key route.

### 19.4 Mutation checks

Before completion, each of the following is broken in turn, and the suite must fail:

- **Chokepoints and keys:**
  - the fingerprint check at each chokepoint;
  - the pre-embed gate;
  - the two-phase key retry;
  - the `KeyId`-mismatch fail-closed branch;
  - the pre-v13 "no fingerprints" rule;
  - a race between two key creators.
- **Apply protocol:**
  - the effect-digest recompute;
  - receipt-first replay, both before and inside the transaction;
  - subject-digest 410;
  - the durable digest excluding content.
- **Plan contents:**
  - twin inclusion;
  - retirement-suppression deletion, both live and staged;
  - search documents deleted even when the full-text verdict is unverified.
- **Covenant:**
  - the closure slot mapping;
  - the kernel refusing reclamation without installation coverage;
  - key reclamation;
  - the binding-epoch join;
  - keyless pin followed by an operator `set`.
- **Verification:**
  - the absence proof;
  - the full-text verdict never upgrading;
  - the WAL-checkpoint verdict and the scrub endpoint;
  - the receipt update guard (a non-upgradable reason can never reach Verified);
  - the Lexicon flag-off-at-start path, which merges before deleting;
  - the Saga `ContentBinding` compare;
  - `reset-key`, which refuses on an unavailable key and deletes only rows under a foreign `KeyId`.
- **Restore:**
  - the restore purge and its post-conditions;
  - the destination-authoritative join (a union must fail);
  - the drain gating over every tier.
- **Adjacent fixes:**
  - the disclosure fold's watermark and its single-snapshot reader;
  - the purge coordinator's per-item classification;
  - the v6 guard ordering around the leftover purge;
  - operator re-creation releasing the fingerprint.

### 19.5 Qualification

- A Release build with zero warnings.
- Both test suites.
- `./scripts/coverage.sh --threshold`.
- The hosted-producer analysis.
- `./scripts/verify-aot-il-warnings.sh`.
- `./scripts/verify-shipping-publish.sh --rid osx-arm64`.
- `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`.

## 20. Acceptance-criteria trace

| #100 criterion | Where it is satisfied |
|---|---|
| Exact item and version | §6.1, §7, §8, §9.1: show-derived targets bind Saga claim versions, the Lexicon curation target, and Covenant heads. |
| Typed preflight | §6.1: `MemoryErasurePreflightDto` and store-specific facts. |
| Explicit confirmation | §17.2: `IConfirmationPrompt` or `--yes`, after the disclosure renders. |
| Bound effect digest | §5.2, §6.2 step 5.3: a keyed effect digest, recomputed inside the transaction and carried in the token. |
| Canonical versions purged | §9.3: every Covenant version, both lanes. §7 and §8: the Annals chain. |
| Projections purged | §7–§9: Annals heads, curation, search documents, outbox. |
| Embeddings purged | §7: embedding table and classified vector mirror. |
| FTS rows purged | §5.8 and §8: v13 secure-delete plus a one-time merge, and the delete-time verdict. §9.3: `covenant_fts` through the search documents. §14.3: staging. |
| Labels purged | §7 and §8, under the owner-scope write lease and `SensitivityRetentionPurge`. |
| Managed artifacts | §2: none are owned (structural pin, §19.3). Attachments are reported as retained sources (§12). |
| Under the required generation leases | §7 and §8: the write lease for labelled items, the Grimoire request lease and `BEGIN IMMEDIATE` compare-and-swap otherwise. §9.2: the Covenant exclusive drain. §6.1: prepare's read lease. |
| Fingerprints retain no plaintext | §5.2 and §5.3. |
| Fingerprints do not correlate across installations | §5.1: OS key, never exported. §14.3: foreign-key rows dropped. |
| Ordinary extraction or restore cannot resurrect | §5.4 chokepoints and pre-embed gate; §14. |
| Local verification distinguished from non-revocable disclosure | §6.4, §10, §11, §12. |

## 21. Closed inventories this change grows

**Schema.**
- The Core and canonical version constants, pins and fixtures: the v12 and v5 reconstructions, with older fixtures rebased on them.
- `GrimoireSchemaTransitionResourceTests` and `GrimoireSchemaVersionChainTests`.
- The fresh-versus-evolved equivalence tests.
- `CovenantCanonicalSchemaTests`.

**Inventories and analysis.**
- The benchmark input catalog and its `.sql` count.
- The connection-acquisition inventory.
- The hosted-producer inventory and capsules.

**Covenant enumerations and contracts.**
- `CovenantSqliteAuthorizationKind` and `FunctionNames`.
- `CovenantExclusiveOperation` and `CovenantLeaseKind`.
- Every `ICovenantOperationGate` test fake.
- `CovenantArchitectureBoundaryTests`.
- `CovenantPublicContractInventory` and `CovenantErrorContractTests`.

**Credentials and backup.**
- `ArcanumCredentialIdentity`, `InstallationResetCredentialCatalog` and `VerifyIdentitiesRotated`.
- `BackupSecretSnapshotReader`.

**Retention.** The data-class enum pins, and the status and parser tests.

**API and CLI contracts.**
- `ErrorCodes`, `ArcanumErrorMapper` and `ArcanumErrorMapperTests`.
- `ArcanumJsonContext` and `CliJsonContext`.
- The route inventories:
  - `CovenantSensitivePurgeRouteInventoryTests`;
  - `LexiconCurationRouteInventoryTests`;
  - `ApiSurfaceContractTests`;
  - `ApiDomainSplitContractTests`;
  - `DocumentationStructureTests`;
  - a new erasure-route inventory.
- The CLI tree, `CliSurfaceExamples`, `CliSurfaceTests`, and the regenerated command map.
- `Memory_has_no_generic_delete_command`.

**Other.**
- `SagaMemoryWriteOutcome` documentation.
- `NullableInterfaceConstructorDefaultTests`.
- `DocumentationIssueReferenceTests`.
