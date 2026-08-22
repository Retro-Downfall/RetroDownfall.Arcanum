# Issue 120: Authenticated Installation-Reset Active Record Design

**Date:** 2026-08-21
**Status:** Approved
**Branch:** `codex/issue-120-installation-reset-active-v2`
**Integration branch:** `long-term-memory`

## Objective

Replace every newly published plain installation-reset active record with an authenticated,
profile-bound V2 envelope protected by a dedicated OS-secret AES-256-GCM key and an OS-secret
anti-rollback anchor. Recovery must reject replay, rollback, cross-profile adoption, operation
adoption, location substitution, malformed or ambiguous persistence, and missing authentication
material before any reset effect is authorized.

This slice implements GitHub issue #120 under the contracts of #94 and Plan 04 Task 17. It does not
add the externally attested full-reset entry point (#121), host-tools or Campaign marker deletion
(#122), managed-file reconciliation, restore-credential removal, or identity rotation (#123).

## Existing-flow conflict and required correction

The current global/all installation-reset flow is:

1. the CLI publishes a plain V1 handoff;
2. the running host applies or replays the online factory operation;
3. the CLI appends a completion proof;
4. the CLI shuts the host down and acquires the maintenance lock;
5. offline reset continues.

That sequence correctly publishes durable replay identity before the host effect, but the CLI cannot
borrow the maintenance-lock handle held by the running host. Issue #120 also requires every new V2
record, key creation, envelope publication, and anchor transition to be serialized by the caller-held
installation lock. A local CLI write, a second side lock, or post-effect authentication would violate
one of those two requirements.

The host therefore becomes the owner of the pre-shutdown V2 publication:

- the CLI prepares a typed handoff in memory and sends it with the authenticated factory request;
- the host validates the handoff and publishes V2 under its already-held maintenance lock before
  entering the factory coordinator;
- the host advances the authenticated record with the content-free online completion proof before
  returning the factory result;
- a proven pre-effect `Data.PlanChanged` closes and retires the handoff before the error response;
- every uncertain or post-effect failure leaves the authenticated record active;
- after shutdown, the CLI passes its newly held maintenance lock to the offline reset continuation.

This preserves the existing `prepare -> host apply/replay -> proof -> shutdown -> lock -> offline`
contract while making the first durable prepare operation lock-owned.

## Persistence model

### Domain record and internal V2 projection

`InstallationResetActiveRecord` remains the internal service-facing state. Version 2 retains:

- operation ID, plan ID, scope, and optional workspace binding;
- accepted reset binding;
- phase and point-of-no-return state;
- row, file, and estimated-byte counts;
- credential results and the last error code;
- optional host-factory handoff and its monotonic online completion proof.

The ciphertext uses a dedicated immutable internal V2 persistence projection. Mutable public wire
arrays are copied into bounded `ImmutableArray<T>` values on seal and copied back into fresh domain
arrays on open. The active-record JSON context owns only internal persistence types and explicitly
registers every nested enum and immutable-array shape. It has no reflection fallback.

The projection includes a `hostToolsMarkerPairReset` property whose canonical value is required to be
`null` in this slice. A non-null JSON value is rejected after decryption. Issue #122 will replace the
null-only reservation with `HostToolsMarkerPairResetCheckpointV1`; using `JsonElement?` for the
reservation preserves the canonical null byte sequence without prematurely defining or accepting
full-reset authority.

The legacy V1 projection remains separately and explicitly decodable within the existing 64 KiB
bound. It can represent only an ordinary reset. It has no host-tools checkpoint, restart proof, or
full-reset authority.

### V2 envelope

`InstallationResetActiveEnvelopeV2` contains exactly:

1. version `2`;
2. the 32-byte profile-namespace digest;
3. nonempty installation UUID;
4. nonempty reset-operation UUID;
5. checked positive revision;
6. the 32-byte previous-envelope digest;
7. the 32-byte active-location digest;
8. reset scope;
9. bounded nonempty plan ID;
10. canonical unpadded base64url nonce;
11. canonical unpadded base64url ciphertext;
12. canonical unpadded base64url authentication tag.

The nonce decodes to exactly 12 random bytes. The tag decodes to exactly 16 bytes. The key is exactly
32 bytes. The entire encoded envelope remains within the existing 64 KiB active-record limit.

AES-GCM additional authenticated data is encoded in this exact order:

1. ASCII `Arcanum.InstallationReset.ActiveEnvelope.v2` and a zero separator;
2. one-byte envelope version;
3. profile-namespace digest bytes;
4. RFC-4122 big-endian installation UUID bytes;
5. RFC-4122 big-endian operation UUID bytes;
6. positive revision as `UInt64BE`;
7. previous-envelope digest bytes;
8. active-location digest bytes;
9. one-byte closed scope code;
10. plan-ID UTF-8 byte length as `UInt32BE` and the bounded bytes.

After decryption, the V2 payload version, operation ID, scope, and plan ID must equal the authenticated
outer header before the payload can be returned. Outer scope and plan ID are display-only until that
comparison succeeds and never authorize an effect.

### Active-location and envelope digests

The profile namespace reuses Task 15's `BackupRestoreProfileNamespace`, derived from the retained
no-follow physical identity of the configured Grimoire root's parent and the bounded Grimoire child
leaf. The account suffix is the same 64-character lowercase digest used by the restore journal.

`ActiveLocationDigest` is SHA-256 under ASCII
`Arcanum.InstallationReset.ActiveLocation.v1`, a zero separator, then:

1. profile-namespace digest;
2. retained guarded-parent physical-identity digest;
3. active-record child-leaf length as `UInt16BE` and its UTF-8 bytes.

It contains no path text. The store recomputes it from current no-follow physical evidence on every
startup and operation.

`EnvelopeDigest` is SHA-256 under ASCII
`Arcanum.InstallationReset.ActiveEnvelopeDigest.v2`, a zero separator, then every canonical envelope
field in declaration order. Text fields are length-prefixed canonical ASCII or UTF-8. Ciphertext and
tag are included so resealing the same payload at the same revision produces a different digest.

### OS-secret identities and key lease

`ArcanumCredentialIdentity` owns these exact prefixes:

- `installation-reset-active-key-`
- `installation-reset-active-anchor-`

Both require the complete canonical 64-character lowercase profile-namespace suffix. Helpers reject a
bare prefix, upper-case suffix, malformed digest, unnamespaced alias, or another profile's name.

`InstallationResetActiveRecordKeyProvider` is the only accessor for the key account. Under the
caller-held installation lock it reads an existing canonical key or generates 32 random bytes, writes
canonical unpadded base64url, reads it back, and compares the decoded bytes before returning a lease.
Recovery only opens an existing key and never creates, substitutes, or repairs one.

The key lease is nonserializable and owns the exact decoded array. It is single-take through
`Interlocked.Exchange`; the authenticator zeroes the taken array in `finally`, and disposal zeroes an
unspent array. Architecture tests pin its only key-taking call sites to the authenticator.

### Installation identity

The envelope uses the current database installation UUID and Task 15's external profile installation
identity. New work requires those two identities to agree. If the external identity is absent, it is
seeded from the database UUID under the caller-held installation lock and verified by readback. A
malformed, unavailable, or mismatched external identity blocks publication and recovery.

After later full-reset slices remove the Task 15 installation account, the already authenticated
reset anchor remains the expected installation identity for that exact operation. This slice does not
remove that account.

## Store protocol

### Begin

Under the caller-held installation lock, a new V2 operation:

1. resolves the profile namespace, retained parent identity, canonical active file, and location
   digest;
2. requires no active canonical file, active anchor, or ambiguous lookalike;
3. creates or opens and verifies the dedicated key;
4. writes and reads back `InstallationResetActiveAnchorV1` at version `1`, state `Active`, revision
   zero, zero envelope digest, and the exact operation, installation, profile, and location bindings;
5. seals the immutable V2 payload as envelope revision one with a zero previous digest;
6. writes the temporary file, flushes file data, atomically replaces the canonical file, flushes the
   parent directory, and removes no evidence on an uncertain durability result;
7. rereads the canonical file through the secure no-follow reader, requires canonical encoding, and
   authenticates it;
8. compare-writes the anchor to revision one and the envelope digest, then reads it back and requires
   exact equality.

The revision-zero anchor intentionally precedes the first envelope. A crash after key creation but
before the anchor leaves only a removable unused key. A crash after the active anchor but before a
recoverable file leaves explicit blocking evidence rather than an apparently fresh installation.

### Advance

An advance under the same lock:

1. recovers the current authenticated publication;
2. requires the same operation, installation, location, immutable plan binding, and monotonic domain
   fields;
3. seals revision `current + 1` with `PreviousEnvelopeDigest = current.EnvelopeDigest`;
4. durably replaces, rereads, and authenticates the file;
5. compare-writes and verifies the matching active anchor.

Revision arithmetic is checked. Zero, overflow, skipped, repeated with different bytes, regressed, or
cross-operation state fails closed.

### Read and recovery

Read-only inspection may authenticate an exact file-and-anchor pair without mutating either. Any
decision that closes a crash window, migrates V1, cleans credentials, or authorizes an effect requires
the held installation lock.

Locked recovery accepts only:

- an exact active anchor/envelope revision and digest match; or
- exactly one envelope revision ahead, where the envelope's previous digest equals the anchor digest.

The one-ahead case is authenticated before the anchor is advanced and read back. Older envelopes,
multiple-revision jumps, mismatched previous digests, cross-profile, wrong installation, wrong
operation, wrong location, unknown versions, malformed or noncanonical JSON/base64url, wrong keys,
wrong tags, missing required fields, and trailing or unmapped JSON all fail closed.

The following evidence is never treated as absence:

- an active file without both its exact key and anchor;
- an `Active` anchor without its unique canonical file;
- a canonical or temporary lookalike;
- a noncanonical, unauthenticated, or symlinked file;
- a rolled-back anchor or envelope;
- credential-store unavailability.

### V1 compatibility and migration

A bounded, canonical V1 file may resume only an ordinary reset that the existing service rules accept.
It cannot carry a host-tools checkpoint or authorize a full-reset effect.

Before the next external effect, locked recovery:

1. validates the V1 operation, plan, binding, phase, handoff, and completion-proof shape;
2. resolves and verifies the current installation identity and location;
3. creates the dedicated key and revision-zero active anchor;
4. wraps the same semantic record as V2 envelope revision one;
5. durably publishes, authenticates, anchors, and reads it back.

A crash after the migration anchor but while the valid V1 file remains may resume only that same
matching ordinary migration. A malformed V1 lookalike, changed operation, non-ordinary checkpoint, or
any non-null reserved full-reset slot blocks instead.

Every record first created by this build is V2.

### Retirement and startup cleanup

Final or proven pre-effect retirement under the lock:

1. recovers the exact authenticated operation;
2. advances the anchor to state `Closed` at the current revision and digest, then verifies readback;
3. identity-captures and deletes the exact canonical active file;
4. flushes the parent directory and proves the canonical file absent;
5. removes the reset-active anchor account and verifies absence;
6. removes the reset-active key account last and verifies absence.

Neither credential is removed while an active envelope can be required for recovery.

Startup under the installation lock completes bounded idempotent cleanup for:

- a `Closed` anchor with an already absent active file;
- no file and no anchor but a leftover key from a crash after anchor removal;
- a file durably retired after the closed anchor but before credential cleanup.

An `Active` anchor with no file remains a blocker. Successful cleanup occurs before fresh installation
state is published.

## Host, CLI, and startup integration

### Typed host handoff

`FactoryResetRequest` gains one optional, source-generated `InstallationResetHostHandoff` projection.
It carries the nonempty requested operation ID, the confirmed installation plan identity, scope,
workspace binding, and complete accepted binding required to reproduce the active record. It contains
no secret, key, raw path capability, live lock, lease, or host-tools checkpoint.

The factory endpoint accepts the projection only when:

- `ExpectedPlanId` and `RequestedOperationId` are both present;
- the requested ID and data-plan ID equal the handoff fields;
- the scope is `Global` or `All`;
- all strings, arrays, counts, and workspace relationships pass the existing installation-plan
  validation and explicit bounds.

Immediately before the data coordinator, the endpoint borrows the host's exact live maintenance lock
from a nonserializable singleton accessor and begins or recovers the V2 record. It then invokes the
existing coordinator. On a trusted success it appends the exact content-free completion proof before
writing the response. On `Data.PlanChanged` before any effect it retires the handoff. Other failures
leave it active.

Ordinary API factory reset calls omit the handoff and preserve their existing behavior.

### Offline continuation

`InstallationResetApplyBoundary` retains the concrete maintenance lock after shutdown and invokes an
internal locked reset-service seam. Every active-record write, V1 migration, and retirement receives
that same borrowed lock. No inner component reacquires or disposes it.

The global/all CLI's local prepare step performs planning and builds the typed pending handoff but
does not write persistence. The host request is the linearization point. A crash before the request
has no effect and no active evidence; a crash after the request begins is recoverable from the host's
lock-owned record.

### Startup ordering

The host acquires the maintenance lock before deciding that active evidence is absent or recoverable.
It authenticates V2, closes the one-ahead anchor window, completes closed-record credential cleanup,
or decodes a bounded V1 compatibility record before database bootstrap or fresh-state publication.

The existing recovery-host exception remains exact: only a `Global` or `All` record at `Prepared`,
with `HostFactoryErasure` and without a durable online completion proof, may start the host so the
named operation can finish or replay. For V2 this decision uses only authenticated payload data. For
legacy V1 it permits startup only so the endpoint can migrate that exact ordinary record to V2 before
its next data effect. Every other active state blocks readiness.

## Credential catalog behavior

Every ordinary installation-reset inventory and deletion pass continues to exclude:

- the three Task 15 restore-journal accounts;
- the reset-active key account;
- the reset-active anchor account;
- the host-tools taint account.

Tests pin both the exact credential identity helpers and the negative catalog membership. Only the
active store's locked retirement owns the reset-active pair.

## Error handling

Persisted-evidence failures return one content-free typed reset-recovery error and never expose which
cryptographic field differed. Ownership conflicts retain `Data.ResetInProgress`. Missing or malformed
authentication evidence, rollback, durability uncertainty, or unsafe cleanup uses a recovery-required
or Covenant integrity/manual-recovery code according to the existing boundary mapping.

Cancellation is honored before durable publication. Once an envelope or anchor transition may have
committed, cleanup and checkpoint recording use a bounded lifecycle token independent of the aborted
HTTP request. An uncertain write is never reported as absence or success.

## TDD and verification

Implementation follows strict red-green-refactor cycles. Each production behavior is preceded by a
focused test that fails for the intended missing behavior.

### Cryptographic and codec tests

- exact envelope, anchor, scope, UUID, revision, digest, location, plan-ID, nonce, tag, and base64url
  encodings;
- wrong key, tag, profile, installation, operation, location, scope, and plan ID;
- unknown version, missing/unmapped/trailing JSON, truncation, over-limit input, and noncanonical
  re-encoding;
- key lease single-take and zeroization; creation readback and recovery no-create behavior;
- source-generated context completeness with no reflection fallback and no public wire ownership.

### Store and recovery tests

- begin ordering and 64 KiB bound;
- exact recovery and the single permitted envelope-ahead window;
- older, skipped, rolled-back, cross-profile, cross-operation, and lookalike state;
- file/key/anchor partial combinations and unavailable credential store;
- atomic replacement, file and parent durability, reread, and anchor readback failures;
- V1 ordinary migration before the next effect and categorical refusal of full-reset authority;
- closed-anchor retirement and crash-idempotent anchor-first/key-last cleanup;
- ordinary credential catalog retention.

### Integration tests

- host publishes V2 before the factory coordinator and proof before response;
- no data effect when host handoff validation or V2 publication fails;
- `Data.PlanChanged` pre-effect retirement versus uncertain-result retention;
- requested identity remains a replay key and never becomes the server operation or gate owner;
- offline writes borrow the boundary's exact maintenance lock;
- startup lock ordering, V2 authentication, ahead-window closure, V1 recovery-host migration, and all
  blocking states;
- DI registrations, API serialization coverage, CLI client coverage, and Native AOT closure.

### Final gates

Run from the repository root:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
git diff --check
```

The build must report zero warnings and zero errors. Focused suites are rerun during each TDD cycle,
and the complete matrix is rerun after independent code review fixes.

## Documentation ownership

Update in the implementation change set:

- `README.md`: Covenant roadmap status and active-reset behavior;
- `docs/Arcanum.DESIGN.md`: authoritative V2 protocol, lock ownership, migration, recovery, and
  retirement;
- `docs/Arcanum.API.md`: optional factory handoff projection and host publication semantics;
- `docs/Arcanum.Command.Reference.md`: unchanged command syntax but updated global/all recovery and
  V2 lifecycle;
- `docs/Arcanum.CHAT-LOOP.md`: reconcile the narrow recovery-host exception with general startup
  blocking;
- `docs/Arcanum.OATH.md` and `docs/ArcanumOATH.Human.md`: mark #120 delivered while leaving
  #121-#123 open and preserve mirrored guarantees;
- `docs/Arcanum.Design.Human.md` and `docs/Arcanum.DEBUGGING.Human.md`: operator/debugging explanation
  of V2 evidence and failure states.

`docs/Arcanum.CommandMap.json`, dated reviews, prior plans/specifications, and
`docs/Compendium.README.md` remain unchanged unless implementation introduces an actual surface drift.

## Delivery

All task changes remain on `codex/issue-120-installation-reset-active-v2`. The unrelated untracked IDE
file is preserved and excluded from staging. After fresh verification and independent review, stage
only confirmed task paths, create the green commit, merge it into `long-term-memory`, verify the merged
tree again, push `long-term-memory`, close only issue #120 as completed, and delete the merged feature
branch locally and remotely if it was pushed. Issues #94 and #74 remain open.
