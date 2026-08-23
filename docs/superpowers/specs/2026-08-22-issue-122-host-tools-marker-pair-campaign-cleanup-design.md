# Issue #122: Host-tools Marker Pair and Campaign Cleanup Design

**Status:** Approved in chat on 2026-08-22; implementation has not started.

**Branch:** `long-term-memory`

**Issue:** #122, a delivery slice of #94 and a prerequisite of #123.

## 1. Objective

An externally attested full installation reset currently stops after authenticating and journaling its
authorization claim. This slice advances that same operation through two additional durable effects:

1. compare-delete the exact database and operating-system host-tools taint-marker pair; and
2. journal and reconcile one Campaign-marker cleanup child for every authenticated Campaign inventory
   entry.

The result remains an active, recovery-required full reset. Issue #123 still owns managed-file
reconciliation, restore-credential terminalization, Grimoire and sidecar deletion, installation and
authority identity rotation, reset-control retirement, and publication of a clean installation.

## 2. Governing constraints

- The authenticated V2 installation-reset envelope, anti-rollback anchor, exact held
  `ArcanumMaintenanceLock`, and signed operation identity remain the authority boundary established by
  issues #120 and #121.
- New work proceeds only from the shared `IHostProcessToolsMarkerPairJoiner.Join` result
  `TaintedMatched` with a nonnull pair. No second pair classifier is permitted.
- Every destructive effect is compare-bound to evidence journaled before that effect. A path, digest,
  transition ID, caller-supplied attestation, or reconstructed object is never deletion authority.
- The existing ordinary-reset path continues to require `Clean` and cannot use this full-reset
  continuation.
- All persisted records are bounded, closed, source-generated, canonical, and unknown-member
  rejecting. Live locks, handles, connections, trust roots, and delegates are never serialized.
- Failures are content-free and leave the reset active. Nothing in this slice reports full reset
  completion or identity rotation.
- Existing unrelated user state, including `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`, is
  preserved and excluded from commits.

## 3. Considered approaches

### 3.1 Dedicated authenticated coordinator — selected

Add one sealed coordinator that owns the pair phase machine and is the sole producer of a private
journal proof. Extend the existing Campaign marker lifecycle with a kind-four cleanup arm that accepts
only an authority minted from that proof.

This keeps reset orchestration, cryptographic recovery proof, and filesystem authority separate while
reusing the existing pair joiner, Campaign codec, no-follow root authority, intent store, and
compare-delete implementation.

### 3.2 Inline the flow in `InstallationResetService` — rejected

This would reduce the file count, but the service would become a second owner of marker
classification, checkpoint validation, compare-delete ordering, and Campaign cleanup authorization.
It would also make lock and journal revalidation a call-site convention instead of a capability rule.

### 3.3 Add generic marker-clear methods — rejected

Broad database and credential-store deletion methods would make the attested full-reset authority
reusable outside its authenticated journal. The required operations are deliberately narrow
compare-deletes owned by the coordinator and retained marker capability.

## 4. Persisted contract

### 4.1 Pair phase

The active-record persistence assembly defines exactly:

```csharp
internal enum HostToolsMarkerPairResetPhase : byte
{
    PairJournaled = 1,
    DatabaseMarkerCompareDeleted = 2,
    OsMarkerCompareDeleted = 3,
    PairAbsenceVerified = 4
}
```

Zero, unknown, skipped, and regressed phases fail authentication. A phase names the last effect and
durability barrier proven complete.

### 4.2 Signed projection and restart proof

`FullInstallationResetSignedAttestationProjectionV1` copies every bounded signed attestation field,
including `SignatureBase64Url`, exactly as accepted. Its version is exactly one.

`FullInstallationResetRestartProofV1`, also version one, contains:

- the whole signed projection;
- the whole-second UTC `AcceptedAtUtc` recorded by the successful verifier;
- `SignedAttestationDigest`;
- the exact `HostProcessToolsDatabaseMarkerEvidence` and
  `HostProcessToolsOsMarkerEvidence`; and
- `PairEvidenceDigest`.

The existing minimal `FullInstallationResetRemediationClaimV1` remains present and must agree with the
restart proof. It is not sufficient by itself to authorize deletion.

Agreement includes exact value equality between the proof and claim for `AcceptedAtUtc` and
`SignedAttestationDigest`; neither may be recomputed into a merely equivalent value or accepted from a
same-shaped statement. The projection's remediation-action digest and every other signed field must
also equal the claim's authenticated evidence before recovery verification runs.

`SignedAttestationDigest` reuses the existing
`Arcanum.FullInstallationReset.ExternalRemediationDigest.v1` canonical calculation: the exact
version-one signed preimage plus the canonical decoded P-256 signature, whose canonical unpadded
base64url spelling is one-to-one. The result must equal the digest already stored in the authenticated
claim. The signature itself is reverified against the independent trust root at the authenticated
original acceptance time. Restart ignores later wall-clock expiry but still requires
`IssuedAtUtc <= AcceptedAtUtc < ExpiresAtUtc`.

Core owns one public nonauthorizing calculator,
`FullInstallationResetRemediationAttestationDigest.Calculate(attestation)`, returning
`Result<CovenantDigest>`. It canonicalizes the signed projection and 64-byte base64url signature and
computes that signature-inclusive V1 digest, but consults no trust root or clock and grants no
authorization. The fresh/fixed-time verifier and static active-record authenticator both reuse this
calculator; Infrastructure never duplicates the private domain/preimage logic.

The remediation-verifier port gains a fixed-time recovery arm that accepts the reconstructed public
attestation, the persisted exact matched-pair evidence, and `AcceptedAtUtc`. It runs the same canonical
shape, evidence-equality, independent-root, P-256/SHA-256 IEEE-P1363, action-digest, nonce, issuer,
issue-time, and expiry checks as fresh verification, but evaluates time against only the authenticated
acceptance time. It does not consult the current wall clock and does not require a live marker pair.
`MatchesAuthenticatedClaim` remains a nonauthorizing equality helper and cannot substitute for this
signature-verifying recovery arm.

`PairEvidenceDigest` is SHA-256 over ASCII
`Arcanum.FullInstallationReset.MarkerPairEvidence.v1`, a zero separator, then these exact fields:

1. database installation identity as bounded strict UTF-8;
2. database state code as one byte;
3. database transition UUID;
4. database taint-time master-key version as positive `UInt64BE`;
5. database taint fingerprint;
6. database taint-identity digest;
7. database-marker digest;
8. OS installation identity as bounded strict UTF-8;
9. OS transition UUID;
10. OS taint-time master-key version as positive `UInt64BE`;
11. OS taint fingerprint;
12. OS marker-bytes digest;
13. OS durable slot-identity digest; and
14. OS taint-identity digest.

UUIDs use RFC-4122 network order, digests are raw 32-byte values, and bounded text uses strict UTF-8
with a checked `UInt16BE` length. All tainted database fields are mandatory; no optional-presence
encoding is accepted in this checkpoint.

### 4.3 Campaign inventory

Before any pair effect, the coordinator reads the complete current Campaign registry and opens each
registered root once with no link following. It authenticates and observes the exact owned marker from
that retained handle. Failure to obtain complete initial marker and same-handle ownership evidence
refuses before journaling; an unavailable or mismatched observation after journaling becomes a typed
cleanup orphan instead.

For a nonempty inventory, this reset-only path first acquires the already-existing
`campaign-root-identity-key` through a non-generating provider arm. Missing, malformed, or unavailable
key evidence blocks before any credential write, root open, codec call, or marker effect; it never uses
the ordinary first-registration `LoadOrCreate` behavior. The recovery arm populates the same provider
instance/cache used by the established opener and codec, so later calls cannot mint a substitute key.
A recovery not-found result is not negatively cached, so outside active reset a later ordinary first
registration on that same singleton retains its established create-on-absence behavior.
A zero-Campaign inventory succeeds without reading or creating the credential, and ordinary first
registration outside active reset retains its existing create-on-absence behavior. On every fresh
process, the same non-generating existing-key check runs before every #122 root open/reopen for which
no retained root already exists. This includes receipt-null first preparation after a crash at
`PairJournaled`, receipt-null exact-child replay after a child commit but before receipt publication,
and receipt-present rehydration when at least one exact `Prepared + Opened` child needs filesystem
authority. Missing, malformed, or unavailable key evidence stops before credential `Set`, root
opening, codec, marker-store, or filesystem access. Blocked or terminal children and an empty vector
require no key and remain zero-filesystem.

This timing is intentional. The initial inventory has the complete nonoptional digest shape required
by the issue. Preparation occurs only after pair absence and reobserves every journaled entry; a root
that became unavailable or mismatched during that interval still receives its required child and typed
orphan. A root already unavailable before `PairJournaled` has no marker or same-handle ownership
evidence to authenticate, so the operation safely refuses before either pair member changes.

The detached `CampaignMarkerInventoryEntryV1` stores only bounded recovery evidence:

- Campaign UUID;
- positive prior path revision;
- marker digest;
- indexed physical-identity digest;
- canonical display-path digest; and
- same-handle ownership-evidence digest.

The checkpoint retains the canonically ordered immutable entry vector as well as its digest so a
post-pair crash can reproduce cleanup even when a root later becomes unavailable. It stores no raw
marker bytes, display-path text, handle, codec output, or filesystem capability. A separate existing
registered-root lookup supplies the display path only as a location hint; the journaled digests and
opened-handle evidence decide whether the result may be used.

Entries are strictly sorted by RFC-4122 Campaign UUID bytes. Duplicate or out-of-order Campaigns,
zero revisions, invalid digests, default arrays, count overflow, and more than 4,096 entries fail
before journal or marker effect.

`CampaignMarkerInventoryDigest` is SHA-256 over ASCII
`Arcanum.FullInstallationReset.CampaignMarkerInventory.v1`, a zero separator, checked `UInt64BE`
count, and each entry's fields in the order listed above. UUIDs and digests use their policy-v1 raw
encodings; the revision uses checked positive `UInt64BE`.

`CanonicalDisplayPathDigest` is SHA-256 over ASCII
`Arcanum.FullInstallationReset.CampaignDisplayPath.v1`, a zero separator, and the canonical display
path's strict UTF-8 bytes framed by checked `UInt16BE` length. The canonical path itself remains in the
core registry and, only for an `Opened` observation, in the kind-four child as a location hint. A
blocked kind-four child stores null even if a changed location was observed. No path text enters the
active envelope.

`SameHandleOwnershipEvidenceDigest` is SHA-256 over ASCII
`Arcanum.FullInstallationReset.CampaignMarkerOwnership.v1`, a zero separator, Campaign UUID, positive
revision as `UInt64BE`, marker digest, indexed physical-identity digest, the root authority's observed
physical-identity digest, and the authenticated marker content's root volume and file IDs as
`UInt64BE`. All values come from the one retained no-follow root and marker handle. Reopening later
must reproduce this digest before the opened arm may delete.

### 4.4 Owner effect and checkpoint

`FullInstallationResetEffectDigest` is SHA-256 over ASCII
`Arcanum.FullInstallationReset.Effect.v1`, a zero separator, then:

1. reset operation UUID;
2. installation UUID;
3. host-tools transition UUID;
4. positive taint-time master-key version as `UInt64BE`;
5. authority fingerprint;
6. database-marker digest;
7. OS marker-bytes digest;
8. signed remediation-action digest;
9. Campaign inventory digest; and
10. reset-scope code `All = 1`.

There are no optional fields or text encodings in this preimage.
UUIDs are the 16 RFC-4122 network-order bytes, every digest is its raw 32 bytes, the taint version is
an eight-byte big-endian unsigned integer, and the scope is the single byte `0x01`. No field is
length-framed, presence-framed, decimal, hexadecimal, base64, or UTF-8 text. A literal byte-preimage
and expected SHA-256 test vector freeze this encoding.

`HostToolsMarkerPairResetCheckpointV1` has version exactly one and carries:

- phase and restart proof;
- detached Campaign inventory and its digest;
- owner-effect digest;
- nullable marker intent count and immutable intent-ID vector;
- nullable intent-vector digest, deleted count, and orphan count.

The marker receipt fields are all null before Campaign preparation is published and all nonnull
afterward. Counts use checked `UInt64`; the vector is nondefault, bounded, canonically ordered,
distinct, deep-copied, and immutable. The first nonnull publication fixes both counts at zero. Its
only later shape keeps the exact vector and has checked deleted plus orphan equal to intent count;
partial counts are never authenticated or published. For an empty vector, the zero-count prepared
shape is already terminal and there is no second count publication.
The full-reset intent vector uses ASCII
`Arcanum.FullInstallationReset.CampaignMarkerIntentVector.v1`, a zero separator, checked `UInt64BE`
count, and the ordered intent UUIDs as RFC-4122 network-order bytes. It accepts at most 4,096 distinct,
nonempty IDs in authenticated Campaign order; it does not sort random intent IDs. Its frozen nonzero
empty-vector digest is
`26b63be668fe309add01922ea6dd3fefe222c7833ff9dfa379bda0275cf98574`.

`CampaignMarkerInventoryEntryDigest` uses ASCII
`Arcanum.FullInstallationReset.CampaignMarkerInventoryEntry.v1`, a zero separator, and one entry's
six fields in the exact order and encodings from §4.3, without a count or additional framing. It is
the companion row's commitment to the corresponding authenticated checkpoint entry.

The active-record codec keeps the legacy V1 decoder at its existing 64-KiB ceiling. V2 separates a
4-MiB maximum decrypted payload from an 8-MiB maximum encoded envelope/file, which is sufficient for
the closed 4,096-entry inventory and receipt vectors while remaining bounded before allocation.

`InstallationResetActivePayloadV2.HostToolsMarkerPairReset` changes from reserved `JsonElement?` to
the typed checkpoint. The active-record JSON context registers every nested record, enum, digest, and
immutable vector. The HTTP and CLI JSON contexts do not register internal proof, authority, or
persistence types.

`InstallationResetActiveRecord` gains the corresponding ignored internal checkpoint member.
`InstallationResetActivePayloadV2.FromRecord` deep-copies it into the persistence projection and
`ToRecord` deep-copies it back. Payload validation, canonical equality, monotonic-transition checks,
envelope/installation matching, legacy migration, active-store readback, and service resume matching
all understand the typed checkpoint. A nonnull checkpoint is legal only beside the exact version-one
remediation claim, `Scope.All`, the same operation and installation, and the recovery-required reset
state.

## 5. Pair coordinator

`HostToolsMarkerPairResetCoordinator` is sealed and owns this sequence under the caller-held exact
maintenance lock:

1. Read and retain the OS marker first through a dedicated capability that binds its platform item
   identity, service/account identity, and exact encoded bytes.
2. Open one non-pooled, core-only SQLCipher connection. On that same connection, require the complete
   installed Core catalog to reproduce the exact current #122 schema manifest, including the
   nullable kind-four parent, companion evidence table, and every guard. An exact #121 predecessor,
   a missing or drifted object, catalog uncertainty, or any other mismatch refuses content-free
   before inventory, pair journaling, or either marker effect. This gate is read-only: it never calls
   the schema installer, rewrites a legacy table, updates schema metadata, or executes ad hoc DDL.
3. Read only the marker projection from the
   required `covenant_authority_state` singleton: `StateKey`, `InstallationIdentity`,
   `HostToolsStateCode`, `TransitionId`, `TaintTimeMasterVersion`, and `TaintFingerprint`. No current
   master, authority epoch, recovery epoch, or other unrelated authority field is part of this
   coordinator read.
4. Construct the existing evidence records and call the shared pair joiner.
5. Verify the signed remediation statement at the captured acceptance time, build the complete
   Campaign inventory, compute all digests, and immediately before publication call the lifecycle's
   exact read-only inventory-revalidation seam. It rereads the registry and rechecks observations
   through the retained same handles without opening a root twice. Only an exact reproduction may
   publish `PairJournaled` through the authenticated envelope-and-anchor advance protocol.
6. Reread and authenticate that publication, begin an immediate transaction, reread that same narrow
   marker projection, and require its validated logical values to reproduce the journaled database
   evidence. That same read captures the six raw SQLite values and types in a nonserializable,
   transaction-bound compare-delete capability. Compare-clear only `HostToolsStateCode`,
   `TransitionId`, `TaintTimeMasterVersion`, and `TaintFingerprint`; the SQL predicate binds the exact
   raw values and types captured by this attempt, including `StateKey` and `InstallationIdentity`.
   This supports a valid legacy integer or canonical eight-byte-BLOB taint-version representation and
   a valid GUID text spelling without pretending the normalized restart evidence preserved their raw
   encodings. Malformed storage refuses before mutation. The statement does not read or assign
   authority epoch, current-master, recovery-epoch, or other unrelated authority columns, so they are
   preserved by construction rather than falsely claimed as restart-journaled evidence. Commit, run
   the database durability barrier, prove the required singleton still has the same installation
   identity and the exact clean marker shape, and publish `DatabaseMarkerCompareDeleted`.
7. Reread and authenticate the new publication, compare-delete the OS marker only through the
   retained slot-and-bytes capability, run the platform-required credential-store durability/readback
   barrier, prove exact absence, and publish `OsMarkerCompareDeleted`.
8. Reauthenticate once more, prove both checkpoint-owned members absent, and publish
   `PairAbsenceVerified`.

Every phase publication uses the existing monotonic V2 envelope revision and anti-rollback anchor
protocol. A failed compare, durability barrier, readback, or publication leaves the last proven phase
active and returns recovery required.

The OS side is not implemented as the existing `Read` followed by account-name `Delete`, which has a
replacement race. A new narrow marker-reset port has a parameterless fresh `OpenExact()` arm that
captures normalized evidence plus a sealed platform capability before the coordinator possesses pair
evidence, and a checkpoint-only `ReopenExact(expectedEvidence)` arm that requires every authenticated
persisted field to match. Only an opened result carries evidence and an opaque disposable capability;
absent, mismatch, and unavailable results carry neither. The capability is caller-owned and disposed
exactly once on every terminal/failure path.

Secrets stays protocol-isolated: it captures any definite-present fixed-slot value as bounded exact
UTF-8 bytes plus native record identity, without referencing or duplicating Core's marker codec.
Empty, over-bound, or non-round-trippable present data has a closed present-invalid result;
Infrastructure maps it to mismatch. Infrastructure alone copies the bounded bytes, performs strict
Base64/payload decoding, maps other malformed/noncanonical content to mismatch before database
access, and zeroes every copy. Disposed or consumed capabilities are unavailable, never absence.

The port exposes `CompareDeleteExactAsync(capability, expectedEvidence)`. Its implementation
revalidates the retained platform item identity, slot identity, exact encoded bytes, and decoded
marker fields immediately at the platform delete boundary, deletes that retained item rather than a
caller-named account, and returns a closed `Deleted`, `Mismatch`, or `Unavailable` result. The
Secrets-owned retained capability owns the complete compare, delete, platform durability, and
absence-readback sequence before returning. No generic credential compare-delete is added to
`IOsCredentialStore`.

The cross-process `ArcanumMaintenanceLock` is the mutation-serialization authority for this slot:
every Arcanum host-tools transition and full-reset marker mutation must hold that same lock, and the
narrow adapter additionally holds one process-wide marker mutation gate around the complete
Secrets-owned compare-delete operation. A recovery-only `ProveFixedSlotDurablyAbsent` operation is
also Secrets-owned and contains its first fixed-slot query, platform durability/synchronization
operation, and second fixed-slot readback. Infrastructure holds the same gate around that one complete
call and maps only two exact not-found observations to absence; a present item is mismatch and any
barrier/read uncertainty is unavailable. No layer splits or repeats either durability sequence. Native
adapters retain the strongest stable record identity their
platform exposes (Keychain item reference, Secret Service item identity, or the complete Windows
credential record identity including last-write evidence). They refuse deletion when the platform
identity or bytes cannot be revalidated. Thus an in-product writer cannot replace the item between
the final comparison and deletion, and a replacement observed before that boundary survives. A
separately malicious same-user process is outside this guarantee because it already has sufficient OS
credential authority to delete the marker directly, and no absence readback can reliably distinguish
that adversary's race. Adapter faults or ambiguous native results remain uncertain and keep reset
recovery active. Adapter and deterministic race tests inject replacement before final revalidation
through supported Arcanum mutation seams and prove the replacement is never deleted.

Lock acquisition alone is not enough across a process crash because a different supported command can
reacquire an abandoned lock. The ordinary host-tools transition therefore authenticates the reset-
active evidence under its acquired installation lock and refuses every active, legacy, malformed, or
unreadable reset before reading or mutating either marker. Only proven `NoActiveRecord` may enter that
transition. Together with the existing reset/restore admission checks, this makes the authenticated
active checkpoint the durable writer exclusion used by the effect/publication recovery matrix; the
process-wide marker gate then closes only the within-process replacement window.

The retained capability is deliberately process-local and is never serialized. After a crash at
`PairJournaled` or `DatabaseMarkerCompareDeleted`, recovery opens the fixed host-tools slot again under
the same maintenance lock into a fresh recovery-only capability. The open succeeds only when the
current service/account slot, exact encoded-byte digest, decoded fields, durable slot-identity digest,
and taint-identity digest all reproduce the authenticated OS evidence in the restart proof. The
adapter captures the newly observed platform record identity inside that capability and revalidates it
during this attempt; it does not claim that the normalized restart evidence preserved the original
process's native handle or record identity. A byte-identical replacement in the same fixed slot is
semantically the same checkpoint-owned marker. That newly opened item identity, not the persisted
digest or account name, is the only deletion capability. An absent, changed, malformed, or
unidentifiable item cannot mint a deletion capability.

Recovery starts from the authenticated checkpoint, never from a fresh live admission read. It first
reconstructs the original database and OS evidence, calls the one shared
`HostProcessToolsMarkerPairJoiner.Join`, and requires `TaintedMatched` with the exact nonnull pair. It
then reverifies canonical shape, signed projection, signature at `AcceptedAtUtc`, exact claim equality,
pair digest, and owner effect before proving or completing only the next legal effect.

There are two unavoidable effect/publication crash windows. They converge without inventing a new
authority only under the authenticated before-image, the same installation-lock identity reacquired
and held for the entire recovery attempt, the active reset's exclusion of every supported writer while
the process was down, the protocol's strict database-before-OS ordering, and exact unchanged sibling
evidence:

- From `PairJournaled`, exact original database plus exact original OS evidence performs the database
  compare-clear. Exact same-installation clean database shape plus the exact original OS evidence is
  the one accepted database-effect/publication crash suffix, but it first reruns the checked WAL
  durability barrier and rereads the exact clean shape before publishing
  `DatabaseMarkerCompareDeleted`. A barrier failure stays at `PairJournaled`. An absent OS marker at
  this phase is out of order and blocks.
- From `DatabaseMarkerCompareDeleted`, exact same-installation clean database shape plus the exact
  original OS evidence performs the retained-item compare-delete. That same database proof plus exact
  fixed-slot absence is the one accepted OS-effect/publication crash suffix, but the recovery-only
  absence proof reruns the platform durability/synchronization operation and a second fixed-slot
  readback before publishing `OsMarkerCompareDeleted`. A barrier or readback failure stays at
  `DatabaseMarkerCompareDeleted`.
- From `OsMarkerCompareDeleted`, exact same-installation clean database shape plus exact fixed-slot
  durably re-proven absence publishes `PairAbsenceVerified`. `PairAbsenceVerified` rechecks the same
  terminal pair state and performs no pair mutation.

No other phase/state combination is idempotent. A changed survivor, out-of-order absence, another
operation or statement, a missing/duplicate database singleton, or a generic database/credential read
failure is a manual blocker. Database-marker absence means the preserved singleton has the journaled
installation identity and exact clean host-tools shape; the statement never assigns an unrelated
authority field. OS-marker absence means the exact fixed slot is proven `NotFound`, not merely
unavailable. The same-user adversary boundary above is what makes that exact OS crash suffix
distinguishable enough for recovery; all supported Arcanum writers are serialized by the held lock.

## 6. Campaign cleanup authority and lifecycle

After `PairAbsenceVerified`, the coordinator reauthenticates the envelope and anchor, revalidates the
restart proof and Campaign inventory, and creates
`AuthenticatedFullInstallationResetJournalProof`. Its constructor is private inside the coordinator.
The proof binds the exact lock, operation, current envelope revision and digest, signed attestation,
owner effect, Campaign inventory, pair-absence checkpoint, and current null-or-exact Campaign receipt
shape.

`FullInstallationResetMarkerCleanupAuthority` is an internal sealed type nested in the coordinator,
beside the private proof. Its constructor is private and only the containing coordinator's private
minting method can call it with that proof; there is no assembly-wide factory or constructor seam that
another Infrastructure type can invoke. Both creation and every use call the producer-owned
revalidator. The authority exposes only `RevalidatePreparationAsync(preparation, expectedReceipt,
cancellationToken)` and `RevalidateReceiptAsync(receipt, cancellationToken)`. The first structurally
compares the supplied owner/effect/inventory and exact nullable receipt with the bound proof before
any prepare/replay read; the second structurally compares owner/effect/ordered intent IDs/vector
digest/counts before any reconciliation read, child transaction, or effect. One canonical explicit
field/sequence comparer is used instead of `ImmutableArray.Equals`, generated record equality, or
reference identity. A released or wrong lock, changed journal revision, changed anchor, changed
proof, substituted input, or non-pair-absent phase rejects without an effect. The authority is
nonserializable and exposes no bound value, path, handle, SQL authorization, or general Covenant
lease.

Every active-record publication invalidates the old proof and authority by advancing the bound
envelope revision and digest. In particular, the authority used to commit prepared children is stale
immediately after the prepared receipt is published. The coordinator must reread and authenticate that
new publication and mint a fresh proof and authority before replay/rehydration or reconciliation. Tests
prove the old authority fails and the newly minted one succeeds; no authority is carried across a
receipt or terminal-count publication.

`ICampaignPathMarkerLifecycle` gains two kind-four mutation methods in addition to its coordinator-only
inventory and inventory-revalidation methods:

- `PrepareFullInstallationResetCleanupAsync` is an exact idempotent prepare-or-replay seam. On first
  preparation it borrows one caller-owned initialized core connection and immediate transaction and
  commits one distinct random `FullInstallationResetCleanup` intent for every authenticated inventory
  entry, including entries currently observed as opened, unavailable, or mismatched. Each child copies
  the same owner operation and owner-effect digest, carries its own marker/inventory/observation
  evidence, and keeps gate-operation and apply-request fields null. Every first-preparation or replay
  call receives a fresh caller-owned immediate transaction on the same live connection; the caller
  commits and durability-rereads it before any Campaign marker effect. On receipt-present or
  child-commit-before-receipt recovery it rereads and compares the exact existing parent/companion
  vector and creates or substitutes no child. A nullable `expectedReceipt` is passed explicitly:
  null must match a null authenticated checkpoint receipt, while a nonnull receipt must structurally
  equal the authenticated publication before the first row read. For an
  existing `Opened` child it rehydrates process-local root authority only by digest-validating the
  committed display-path hint and repeating the exact no-follow/root/marker proof, but only while that
  child is still `Prepared`. Existing `Unavailable` and `Mismatch` children, plus already terminal
  `Completed` and `ManualBlocker` children of any observation code, are authenticated and skipped with
  no opener, codec, marker store, or filesystem service. Failed rehydration remains runtime blocked and
  lets reconciliation terminalize that immutable prepared child as `ManualBlocker`.
- `ReconcileFullInstallationResetCleanupAsync` borrows the same live core connection without
  disposing it and revalidates authority before each child and each short child transaction. An
  opened seed uses only its retained no-follow root authority and the existing marker codec and
  same-handle compare-delete path. Exact deletion plus parent durability advances directly to `Completed`.
  Unavailable, physical mismatch, ownership mismatch, post-deletion root loss, or durability failure
  advances to `ManualBlocker` with content-free authenticated evidence. Unavailable and mismatch arms
  make no marker-store, opener, codec, or filesystem call.

The lifecycle's coordinator-only, read-only `InventoryFullInstallationResetCleanupAsync` seam borrows
the coordinator's already opened core connection, reads the complete registry, and owns all
codec/opener/retained-root work needed to produce the detached §4.3 inventory. Its companion
`RevalidateFullInstallationResetInventoryAsync` seam is called immediately before `PairJournaled`;
it rereads the exact ordered registry and rechecks observations through the already retained same
handles without reopening a root. Only an exact structural reproduction succeeds. Neither method
grants SQL or filesystem mutation authority. A source-inventory test keeps the coordinator as their
only production caller, so the coordinator never resolves the marker codec, root opener, registry
store, or filesystem primitives directly.

The existing `campaign_path_marker_intents` row remains the child identity and phase owner. Its
canonical table definition and update guard gain an exact kind-four shape: no exclusive gate owner,
encrypted payload, temporary name/identity, apply-request digest, target-observation fields, pending
disposition, or zero prior revision; phase is only `Prepared`, `Completed`, or `ManualBlocker`, and the
two terminal phases cannot advance. `TargetDisplayPath` becomes nullable only for kind four. An
`Opened` companion requires a nonnull digest-matching location hint; `Unavailable` and `Mismatch`
require it null, so a registry row that disappeared after `PairJournaled` still produces its mandatory
blocked child without persisting raw path text in the active checkpoint or manufacturing a path.
Kinds one through three continue to require a nonnull target display path and retain their existing
shape and transitions. The parent update guard compares the now-nullable path with SQLite `IS NOT`,
so both null-to-value and value-to-null substitution attempts violate immutability.

A new canonical companion object, `campaign_path_full_reset_cleanup_evidence`, is keyed one-to-one by the
kind-four intent ID and stores the per-Campaign inventory-entry digest, indexed-identity digest,
display-path digest, same-handle ownership-evidence digest, closed observation code, and observation
digest. The closed observation codes are `Opened = 1`, `Unavailable = 2`, and `Mismatch = 3`. A
nullable `OpenedSameHandleOwnershipEvidenceDigest` is present only for `Opened` and must equal the
authenticated expected same-handle digest; it is null for the two blocked arms. `ObservationDigest`
is SHA-256 over ASCII `Arcanum.FullInstallationReset.CampaignMarkerObservation.v1`, a zero separator,
the one-byte observation code, the raw inventory-entry digest, and, only for `Opened`, the raw opened
same-handle digest. The code therefore determines the preimage shape without an optional-presence
byte. It stores no marker bytes or filesystem authority. Its constraints reject a non-kind-four
parent, incomplete digest evidence, an unknown observation code, or an opened/blocked nullable shape
that does not match that code. Preparation inserts the intent and companion evidence in the same
caller-owned transaction; reconciliation compares both rows before every effect and advances only
the parent phase. The object
is added as its own schema file under `Infrastructure/Data/Schema/**`, following the repository's
schema-object convention rather than adding a numbered migration.

SQLite cannot express the parent-kind condition in a companion-table `CHECK`. The schema therefore
uses a foreign key for one-to-one lifetime plus explicit insert/update triggers that require parent
`IntentKindCode = 4`, enforce observation-code shape, and make the evidence immutable. A `BEFORE
DELETE` trigger aborts while the corresponding parent still exists, so direct child deletion cannot
strip replay evidence; the foreign-key cascade is permitted because it runs after the parent row is
gone. There is no independent companion mutation surface.

The lifecycle rereads kind-four rows and their companion evidence in authenticated Campaign order and
compares every intent ID, owner, Campaign, effect, marker, revision, observation, and evidence digest
with the checkpoint vector. Count is only one invariant; a same-cardinality replacement still
conflicts. It does not use a Covenant gate disposition or a post-disposition finalizer. Roots retained
by this process are released exactly once after the terminal receipt is durably published or on
failure cleanup.

An authenticated retry whose exact joined children are already terminal and whose persisted
deleted/orphan counts equal those rows does not republish the same terminal receipt or advance the
active-envelope revision. It authenticates and skips every child with zero filesystem calls, releases
any process-local retained roots idempotently, and returns recovery required from the unchanged
publication. This makes the crash after terminal publication but before return/root release stable
across every startup until #123 consumes the checkpoint.

## 7. Service and recovery integration

`InstallationResetService.ApplyFullUnderMaintenanceLockAsync` delegates all #122 work to the
coordinator. New work still replans, validates the exact full request, verifies the live pair, and
publishes its initial authorization claim. An authenticated retry with a pair checkpoint bypasses the
live-pair admission branch and resumes only the same signed operation through the coordinator. The
local active-reset projection distinguishes claim-only state, which still requires the external
statement file, from a typed pair checkpoint, which resumes through a dedicated locked boundary
without rereading that file. The locked service reauthenticates the checkpoint; the projection flag
is routing information, not authority.

A valid claim-only V2 record written by #121 does not imply that its existing Grimoire contains
#122's kind-four child substrate. After key-only access preparation and OS-first capability open, the
coordinator validates the exact current Core manifest on its one SQLCipher connection. An exact #121
schema therefore stays claim-only and recovery-required with both host-tools markers untouched; no
`PairJournaled` publication or Campaign root effect occurs. The same read-only gate runs on typed
checkpoint resume before any remaining pair or Campaign effect, so later schema drift preserves the
last authenticated checkpoint. This issue neither runs ordinary bootstrap inside active recovery nor
adds an in-place/numbered migration; deliberate schema repair remains an external manual prerequisite.

For full-apply CLI routing, raw argv shape validation remains first, then the configured command reads
authenticated active-reset state before opening any attestation path. Fresh work and claim-only retry
read/decode the supplied file only after that probe. A typed checkpoint takes precedence over a
supplied path: the path is rejected as an authority input and is never opened, while resume continues
from the persisted operation ID/checkpoint. The preconfiguration argv guard defers this decision to the
configured command and performs no competing active-state read.

The offline CLI boundary and hosted startup share one narrow existing-database-access preparer. After
the CLI has stopped the host and acquired the exact installation lock, but before either fresh
`BeginAsync` or checkpoint `ResumeAsync` can reach the coordinator, it derives and initializes the
passphrase from the already committed KDF sidecar and dedicated secret using the same no-SQLite rules
below. A failure returns before the locked reset service or either marker is touched. This is key
availability only: it is not bootstrap, pair admission, a database connection, or deletion authority.

The startup probe continues to keep readiness closed for every active full reset. The hosted service
supplies a one-shot pre-bootstrap database-access callback when it enters singleton startup recovery,
but the recovery authenticates the active envelope and anchor without opening SQLCipher before it may
invoke that callback. Claim-only, absent, legacy, malformed, or unauthenticated evidence never invokes
it; only an authenticated typed #122 checkpoint does. Under the already-held installation lock, the
callback first validates the exact live lock plus canonical no-follow containment of both the
database and derived committed-sidecar path before native, key, secret, or database access. It then
requires the existing Grimoire, its committed modern KDF sidecar, and the still-retained dedicated
encryption secret, initializes the SQLCipher native
runtime without opening SQLite, derives the passphrase, and initializes
`IGrimoireDbPassphraseSource`. A #122 checkpoint can exist only after
ordinary initialization already converged that KDF state; a legacy database, pending-only sidecar,
missing sidecar/secret, or sidecar failure is therefore a manual blocker rather than permission to
probe, rekey, or create anything.

The preparer must not resolve the dedicated secret through the application's ordinary auto-generating
ASP.NET Data Protection provider: `Unprotect` against a missing or unusable ring may create a new key
before it reports failure. It first performs an owner-safe, read-only key-ring and secret-path preflight
that refuses symlinks/reparse points and unsafe ownership or permissions without following, repairing,
or chmodding them, then uses a
separate provider configured with `DisableAutomaticKeyGeneration` and the existing application name,
ring, protected-secret path, and protector purpose. Missing, empty, malformed, or unusable key-ring
evidence and missing/corrupt secret bytes fail without creating or modifying the key directory, key
files, or secret. Tests snapshot topology, file type/link identity, ownership/permission metadata,
length, last-write time, and bytes before and after every failure arm; access-time bookkeeping is not
treated as an application write.

The callback creates no missing database or guarded root, installs or repairs no schema, runs no
ordinary host-tools startup gate, and publishes no readiness. The same single scoped recovery attempt
then resolves the coordinator, which still opens/observes the OS slot before its first SQLCipher
connection, and resumes the checkpoint before ordinary bootstrap. Claim-only, no-active-record, and
ordinary restore startup keep their existing restore-topology-before-key ordering because the callback
is never invoked for them.

Recovery entry points may drive the coordinator only while holding the same installation lock and
authenticated operation. Reaching terminal Campaign cleanup still returns `Data.RecoveryRequired`,
because #123 has not completed the full reset; the hosted service therefore remains closed and never
continues into ordinary schema convergence or readiness for this active reset. No result from this
slice uses `InstallationResetPhase.Completed`, retires the active record, deletes reset-control
credentials, or publishes a clean identity.

Dependency injection registers the coordinator and each narrow collaborator exactly once. The service
and coordinator do not resolve the Campaign codec, filesystem opener, or compare-delete implementation
directly; those stay behind `ICampaignPathMarkerLifecycle`.

## 8. Error and cancellation behavior

- Before `PairJournaled`, request cancellation propagates and no marker effect has occurred.
- After `PairJournaled`, lifecycle continuation uses a bounded recovery-owned cancellation token;
  caller cancellation cannot erase the durable operation or turn an uncertain effect into absence.
- Expected evidence, revision, capacity, and authority failures return existing typed content-free
  Covenant/Data errors. No error includes paths, marker bytes, signature text, issuer, nonce, or
  trust-root material.
- Any uncertain or partially proven state remains active and blocks clean startup and identity
  publication. Retry is idempotent only for the exact authenticated operation.

## 9. TDD and verification

Implementation proceeds in red-green-refactor slices:

1. active-record types, source-generation coverage, canonical validation, and digest vectors;
2. exact pair compare-delete ordering and all four crash/restart phases;
3. private proof/authority construction and lock/journal revalidation;
4. Campaign inventory, kind-four child preparation, reconciliation, orphan classification, and receipt
   invariants;
5. service, startup, and dependency-injection integration; and
6. owning documentation.

Each named behavior is its own red-green microcycle, not a batch. Where a production type is wholly
absent, add only the inert compile-safe contract or skeleton needed to let the first behavioral test
fail on an assertion or explicit `Result` before implementing it; then repeat one test at a time.
Contract-shape tests may intentionally use a compiler failure as red. Refactoring happens only while
the just-green focused test remains green.
Required acceptance coverage includes every checkbox in issue #122, exact digest vectors, zero/one/many
Campaigns, changed inventory before effects, changed surviving marker, post-expiry restart, signature
and acceptance-time tamper, same-shaped digest substitution, both effect/publication crash suffixes,
default/oversized/reordered/aliased receipt vectors, receipt-present retained-root rehydration,
partial-count rejection, no-filesystem blocked arms, exact #122 schema readiness and #121 claim-only
refusal before marker effects, non-generating existing Campaign root-identity key recovery before
every fresh-process root open (including both receipt-null crash suffixes and receipt-present replay)
with zero credential writes on missing evidence, and architecture tests excluding internal
authorities from wire JSON.
Cross-platform contract tests additionally pin each native retained-identity algorithm: macOS owns and
deletes the exact `SecKeychainItemRef`, Linux owns one stable Secret Service item object and never
requeries/clears by attributes, and Windows snapshots `LastWritten` plus the complete credential record
and rereads it immediately before targeted deletion. These tests run on every development platform via
injectable native seams or production-source inventory, while the platform CI lanes retain their real
backend coverage.

Final verification runs:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
git diff --check
```

Build and test output must contain zero warnings and zero errors. An independent code review must find
no unresolved issue-scoped defect before commit.

## 10. Documentation and delivery

The final change updates:

- `README.md`;
- `docs/Arcanum.DESIGN.md`;
- `docs/Arcanum.API.md`;
- `docs/Arcanum.Command.Reference.md`;
- `docs/Arcanum.DEBUGGING.Human.md`;
- `docs/Arcanum.Design.Human.md`;
- `docs/Arcanum.OATH.md`; and
- `docs/ArcanumOATH.Human.md`.

Generated command/config inventories remain unchanged unless implementation proves their public
surface changed. Earlier historical plans, specs, and reviews are not rewritten; this approved #122
specification and its implementation plan ship with the slice.

All work stays directly on `long-term-memory`, matching the issue delivery contract. After every gate
is green, stage only issue #122 files, independently review the complete staged diff, create one commit,
push `long-term-memory`, verify the remote tip, close only issue #122 with the actual recorded
verification evidence, and delete any merged feature branches. Issues #74, #94, and #123 remain open.
