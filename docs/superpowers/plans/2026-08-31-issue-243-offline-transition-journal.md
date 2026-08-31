# Issue #243 Offline-Transition Journal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the authenticated, profile-scoped fixed journal slot that can preserve durable progress while a Grimoire transition cannot safely write to the Grimoire itself.

**Architecture:** A dedicated Infrastructure component stores one encrypted journal file beside the installation maintenance lock and protects its latest accepted revision with a profile-namespaced OS-credential anchor. The file layer owns no-follow/owner-only publication and identity-safe retirement; the journal layer owns epoch/revision compare-and-swap, exact recovery, and opaque handler payload bytes. This issue supplies no lifecycle phase graph or handler registry: issue #244 will decode the opaque payload through typed codecs.

**Tech Stack:** .NET 10, C# 14, `System.Security.Cryptography.AesGcm`, source-generated `System.Text.Json`, `IOsCredentialStore`, `ArcanumMaintenanceLock`, `SecureFileReader`, retained-parent Native-AOT filesystem interop, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`

## Global Constraints

- Implement only child issue #243: fixed-slot location, credentials, envelope/authenticator, secure file persistence, anchor CAS, exact recovery, and retirement.
- Do not add the #244 lifecycle state graph, codec registry, test-only handler, or production migration kind.
- Do not add admission gating, EF/raw connection integration, reset/factory handlers, startup wiring, API/CLI changes, configuration keys, database schema, numbered migrations, or backfill behavior.
- The canonical file is the sibling path `ArcanumMaintenanceLock.LockPathFor(guardedDirectory) + ".grimoire-transition.active.json"`; its parent is outside the guarded database directory.
- Reuse `BackupRestoreJournalAuthenticator.ResolveProfileNamespace` and `BackupRestoreJournalInstallationIdentityProvider`; add a distinct journal key and anchor, but no second installation-identity credential.
- Credential accounts are `grimoire-transition-journal-key-{PROFILE_NAMESPACE}` and `grimoire-transition-journal-anchor-{PROFILE_NAMESPACE}`. Both accept only the existing 64-character lowercase-hex profile suffix and remain excluded from ordinary credential cleanup.
- AES-256-GCM uses a random 12-byte nonce, 16-byte tag, a 32-byte single-take zeroizing key lease, and a content-free integrity refusal.
- Authenticate version, profile namespace, stable installation identity, slot epoch, operation id, transition kind, payload version, journal revision, previous-envelope digest, and physical journal-location digest in fixed binary order.
- The location digest binds profile namespace, the no-follow physical identity of the maintenance-lock parent, and the exact fixed child leaf; it contains no path text and does not include the operation id.
- The store treats handler payload bytes as opaque, bounded bytes. It repeats operation, kind, and payload version inside a strict source-generated encrypted wrapper; issue #244 owns typed payload interpretation.
- The anchor has an exact `Closed(0)` genesis, `Active(E)` and `Closed(E)` states, nullable publication fields for genesis/revision zero, and checked epoch/revision ceilings of `1_000_000`.
- Recovery accepts only an exact anchor/file match or a file exactly one chained revision ahead. `Active` revision zero without the canonical file is a blocker after restart.
- New operations compare `Closed(E)` to `Active(E+1)` only after exact file and temporary-residue absence. An active different operation conflicts; an exact active duplicate validates and resumes; a closed same operation never reopens.
- The file protocol retains the proved parent directory for every mutation and uses four exact sibling leaves: canonical `J`, working `W = J + ".publish"`, predecessor `P = J + ".previous"`, and retirement `D = J + ".retiring"`. No generic `.arcanum-cleanup-*` quarantine is permitted.
- An update atomically exchanges/replaces `W` and `J` while retaining the displaced target at `W`/`P`, then verifies that displaced identity before accepting publication. A target substituted in the last validation window is preserved as evidence and fails closed; it is never silently overwritten or accepted.
- Retirement writes and rereads the exact `Closed` anchor before moving the exact final file to `D`, authenticating it there, compare-unlinking it through the retained parent, verifying the retained handle lost its final link, fsyncing the parent, proving all four leaves absent twice, retaining the key and tombstone, and remaining idempotent.
- Unix has no unlink-by-open-handle syscall. The final `unlinkat(parent, D)` is therefore a deliberately narrow delegated-deletion window: compare the retained D handle to the relative name immediately before unlink, require that same handle's link count become zero immediately after, and return recovery-required on any mismatch or residual evidence. Success proves only the compare-before-unlink result, the expected handle's zero-link postcondition, and fixed-namespace absence; it cannot prove that the syscall removed no last-instant same-UID replacement. Never claim a portable guarantee stronger than those postconditions.
- Reject symlinks/reparse points, hard links, owner-posture failures, case aliases, stale temporary files, substitutions, ambiguous absence, unknown versions, noncanonical JSON, replay, rollback, skips, and partial evidence.
- Follow RED/GREEN/refactor for every behavior. Run each named focused test once per task; reserve the repository-wide suite and AOT gates for umbrella issue #239 qualification.
- Preserve the two unrelated untracked issue-221 documents byte-for-byte and do not include them in any commit.
- Follow repository C# style: file-scoped namespaces, source-generated JSON only, no reflection serializer overloads, and one blank line after each line of C# where the house style requires it.

---

### Task 1: Authenticated contracts, credential identities, and cryptographic codec

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalAuthenticator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalAuthenticationTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`

**Interfaces:**
- Consumes: `BackupRestoreProfileNamespace`, `BackupRestoreJournalAuthenticator.ResolveProfileNamespace`, `BackupRestoreJournalInstallationIdentityProvider`, `CovenantDigest`, `IOsCredentialStore`, and `ArcanumMaintenanceLock`.
- Produces: the exact records, enums, codec methods, key provider, key lease, and credential account factories used by Tasks 2–4.

- [ ] **Step 1: Write the failing authentication and credential tests**

Create tests that reference the following exact production surface before it exists:

```csharp
internal enum GrimoireOfflineTransitionKind : byte
{
    CovenantReset = 1,
    HealthyCatalogFactoryErasure = 2,
}

internal enum GrimoireOfflineTransitionAnchorState : byte
{
    Active = 1,
    Closed = 2,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionPayloadV1(
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    string PayloadBase64Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionEnvelopeV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    ulong SlotEpoch,
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    CovenantDigest JournalLocationDigest,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionAnchorV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    ulong SlotEpoch,
    GrimoireOfflineTransitionAnchorState State,
    Guid? OperationId,
    GrimoireOfflineTransitionKind? Kind,
    byte? PayloadVersion,
    ulong Revision,
    CovenantDigest? EnvelopeDigest,
    CovenantDigest JournalLocationDigest);
```

Pin these exact constants:

```csharp
EnvelopeVersion = 1;
AnchorVersion = 1;
KeyBytes = 32;
NonceBytes = 12;
TagBytes = 16;
MaxHandlerPayloadBytes = 256 * 1024;
MaxPlaintextBytes = 512 * 1024;
MaxJournalFileBytes = 1024 * 1024;
MaxAnchorCharacters = 2048;
MaxRevision = 1_000_000;
MaxSlotEpoch = 1_000_000;
```

The authentication suite must contain these named facts/theories and assertions:

```text
Location_digest_binds_profile_parent_identity_and_fixed_leaf_without_path_text
Seal_and_open_round_trip_exact_opaque_payload_bytes
Envelope_aad_rejects_each_changed_header_field
Ciphertext_tag_wrong_key_and_resealed_same_revision_are_refused
Envelope_and_anchor_require_one_canonical_source_generated_encoding
Unknown_duplicate_reordered_trailing_and_unknown_version_json_are_refused
Payload_envelope_anchor_and_revision_bounds_fail_before_unbounded_allocation
Anchor_shape_accepts_only_closed_genesis_active_revision_zero_and_exact_tombstones
Key_lease_is_single_take_and_zeroes_on_dispose
Recovery_key_open_never_creates_or_repairs_material
Transition_credentials_are_profile_scoped_and_excluded_from_ordinary_cleanup
```

For the AAD theory, mutate exactly one of profile digest, installation id, epoch, operation id, kind, payload version, revision, previous digest, and location digest on a valid envelope and assert `Open` fails without returning payload bytes. For canonical JSON, insert one unknown property, duplicate `revision`, reorder two properties, prefix whitespace, append whitespace, and replace the envelope or anchor `version` value `1` with `2`; assert `DecodeEnvelope` or `DecodeAnchor` fails. A nonzero typed `payloadVersion` remains opaque to #243 and is interpreted by #244.

- [ ] **Step 2: Run the tests to verify RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter 'FullyQualifiedName~GrimoireOfflineTransitionJournalAuthenticationTests|FullyQualifiedName~GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests|FullyQualifiedName~InstallationResetCredentialCatalogTests' \
  --no-restore --disable-build-servers -m:1
```

Expected: FAIL at compile time because the #243 contracts, authenticator, key provider, and credential factories do not exist.

- [ ] **Step 3: Implement the strict records and source-generated context**

Place every type under `RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions`. Register every record and enum explicitly:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GrimoireOfflineTransitionPayloadV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionEnvelopeV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionAnchorV1))]
[JsonSerializable(typeof(GrimoireOfflineTransitionKind))]
[JsonSerializable(typeof(GrimoireOfflineTransitionAnchorState))]
[JsonSerializable(typeof(CovenantDigest))]
internal sealed partial class GrimoireOfflineTransitionJournalJsonContext : JsonSerializerContext;
```

`ValidateAnchor` must accept only these shapes:

```text
Closed epoch 0: operation/kind/payloadVersion/envelopeDigest are null; revision is 0.
Active epoch > 0 revision 0: operation/kind/payloadVersion are present; envelopeDigest is null.
Active epoch > 0 revision > 0: operation/kind/payloadVersion and a valid envelopeDigest are present.
Closed epoch > 0 revision 0: operation/kind/payloadVersion are present; envelopeDigest is null (the live publisher proved first publication never landed).
Closed epoch > 0 revision > 0: operation/kind/payloadVersion and a valid envelopeDigest are present.
```

Every other nullable combination, empty operation, undefined kind, zero payload version, invalid digest, over-bound epoch, or over-bound revision returns a content-free typed failure.

- [ ] **Step 4: Implement the authenticator**

Use these exact domain strings:

```csharp
internal const string JournalLocationDomain =
    "Arcanum.GrimoireOfflineTransition.JournalLocation.v1";
internal const string EnvelopeAssociatedDataDomain =
    "Arcanum.GrimoireOfflineTransition.JournalEnvelope.v1";
internal const string EnvelopeDigestDomain =
    "Arcanum.GrimoireOfflineTransition.JournalEnvelopeDigest.v1";
```

Expose these exact methods:

```csharp
internal static Result<CovenantDigest> JournalLocation(
    CovenantDigest profileNamespaceDigest,
    CovenantDigest guardedParentPhysicalIdentityDigest,
    string journalChildLeaf);

internal static Result<GrimoireOfflineTransitionEnvelopeV1> Seal(
    GrimoireOfflineTransitionJournalKeyLease key,
    CovenantDigest profileNamespaceDigest,
    Guid installationId,
    ulong slotEpoch,
    Guid operationId,
    GrimoireOfflineTransitionKind kind,
    byte payloadVersion,
    ulong revision,
    CovenantDigest previousEnvelopeDigest,
    CovenantDigest journalLocationDigest,
    ReadOnlySpan<byte> payloadBytes);

internal static Result<byte[]> Open(
    GrimoireOfflineTransitionJournalKeyLease key,
    CovenantDigest expectedProfileNamespaceDigest,
    Guid expectedInstallationId,
    CovenantDigest expectedJournalLocationDigest,
    GrimoireOfflineTransitionEnvelopeV1 envelope);

internal static Result<CovenantDigest> EnvelopeDigest(
    GrimoireOfflineTransitionEnvelopeV1 envelope);
internal static Result<byte[]> EncodeEnvelope(
    GrimoireOfflineTransitionEnvelopeV1 envelope);
internal static Result<GrimoireOfflineTransitionEnvelopeV1> DecodeEnvelope(
    ReadOnlySpan<byte> utf8);
internal static Result<string> EncodeAnchor(
    GrimoireOfflineTransitionAnchorV1 anchor);
internal static Result<GrimoireOfflineTransitionAnchorV1> DecodeAnchor(string? value);
internal static Result ValidateAnchor(GrimoireOfflineTransitionAnchorV1 anchor);
```

Build AAD in this exact order and width:

```text
ASCII domain bytes
1 byte envelope version
32 bytes profile namespace digest
16 RFC-4122 big-endian installation UUID bytes
8-byte big-endian slot epoch
16 RFC-4122 big-endian operation UUID bytes
1 byte transition kind
1 byte typed payload version
8-byte big-endian journal revision
32 bytes previous-envelope digest
32 bytes journal-location digest
```

Serialize `GrimoireOfflineTransitionPayloadV1` with the explicit generated type info, canonical unpadded base64url payload bytes, then zero the serialized plaintext in `finally`. On open, authenticate before deserialization, require strict canonical decode, require the wrapper's operation/kind/payload version to equal the header, decode the canonical base64url payload under `MaxHandlerPayloadBytes`, and return a fresh exact byte array. `EnvelopeDigest` hashes all clear fields in declaration order and length-prefixes nonce/ciphertext/tag before hashing them.

- [ ] **Step 5: Implement the key lease/provider and credential catalog retention**

Add to `ArcanumCredentialIdentity`:

```csharp
internal const string GrimoireTransitionJournalKeyAccountPrefix =
    "grimoire-transition-journal-key-";
internal const string GrimoireTransitionJournalAnchorAccountPrefix =
    "grimoire-transition-journal-anchor-";
internal static string GrimoireTransitionJournalKeyAccount(string profileSuffix);
internal static string GrimoireTransitionJournalAnchorAccount(string profileSuffix);
internal static bool IsGrimoireTransitionJournalAccount(string? account);
```

Both factories must call the existing canonical suffix validator. Add `!IsGrimoireTransitionJournalAccount(account)` to the closed `InstallationResetCredentialCatalog.CollectAccounts` filter.

Implement `GrimoireOfflineTransitionJournalKeyLease` as a single `Interlocked.Exchange` take with zeroing disposal. Implement `GrimoireOfflineTransitionJournalKeyProvider.CreateOrOpen`, `OpenExisting`, and `IsPresent` using canonical 43-character unpadded base64url storage and exact readback. Only `CreateOrOpen` may generate a random key, and it must assert the caller-held maintenance lock. `OpenExisting` must return `Covenant.NotFound` for proven absence and never call `Set`.

The source inventory test must prove:

```text
TryTakeKey is called only by GrimoireOfflineTransitionJournalAuthenticator.
Mint is called only by GrimoireOfflineTransitionJournalKeyProvider.
The key/anchor account factories are called only by the matching provider/store and tests.
No ordinary credential deletion inventory names either transition account.
```

- [ ] **Step 6: Run GREEN and commit**

Run the Step 2 command again. Expected: PASS with zero failed tests.

Then:

```bash
git add \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions \
  src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs \
  src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs \
  tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions \
  tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs
git commit -m "feat: add authenticated Grimoire transition codec"
```

---

### Task 2: Fixed-slot location and secure file persistence

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 constants/codecs, `BackupRestoreJournalAuthenticator.ResolveProfileNamespace`, `ArcanumMaintenanceLock.LockPathFor`, `SecureFileReader`, `SecureFilePermissions`, `FileHandleIdentityInterop`, and `BackupRestoreJournalNativeMethods.TryFlushDirectory`.
- Produces: `GrimoireOfflineTransitionJournalLocation`, `GrimoireOfflineTransitionJournalFileRead`, and the exact file-store methods used by Tasks 3–4.

- [ ] **Step 1: Write the failing fixed-slot and file-security tests**

Define the expected records/API in the test:

```csharp
internal sealed record GrimoireOfflineTransitionJournalLocation(
    BackupRestoreProfileNamespace ProfileNamespace,
    string GuardedDirectory,
    string MaintenanceLockPath,
    string JournalPath,
    string JournalLeaf,
    string WorkingPath,
    string WorkingLeaf,
    string PreviousPath,
    string PreviousLeaf,
    string RetiringPath,
    string RetiringLeaf,
    CovenantDigest GuardedParentPhysicalIdentityDigest,
    CovenantDigest JournalLocationDigest);

internal sealed record GrimoireOfflineTransitionJournalEvidence(
    GrimoireOfflineTransitionJournalFileRead? Canonical,
    GrimoireOfflineTransitionJournalFileRead? Working,
    GrimoireOfflineTransitionJournalFileRead? Previous,
    GrimoireOfflineTransitionJournalFileRead? Retiring);

internal sealed class GrimoireOfflineTransitionJournalFileRead : IDisposable
{
    internal ReadOnlyMemory<byte> Bytes { get; }
    internal FileHandleMetadata Metadata { get; }
}
```

`GrimoireOfflineTransitionJournalEvidence` implements `IDisposable` and disposes every non-null read exactly once so encoded evidence buffers are zeroed on every classification path.

The file store must expose:

```csharp
internal Result<GrimoireOfflineTransitionJournalLocation> ResolveLocation(
    string guardedDirectory);
internal Result RequireNoEvidence(
    GrimoireOfflineTransitionJournalLocation location);
internal Task<Result<GrimoireOfflineTransitionJournalEvidence>> InspectEvidenceAsync(
    GrimoireOfflineTransitionJournalLocation location,
    CancellationToken cancellationToken);
internal Task<Result<GrimoireOfflineTransitionJournalFileRead?>> ReadIfPresentAsync(
    GrimoireOfflineTransitionJournalLocation location,
    CancellationToken cancellationToken);
internal Task<Result> ReplaceDurablyAsync(
    ArcanumMaintenanceLock heldInstallationLock,
    GrimoireOfflineTransitionJournalLocation location,
    ReadOnlyMemory<byte> bytes,
    FileHandleIdentity? expectedCurrentIdentity,
    CancellationToken cancellationToken);
internal Result DeleteDurably(
    ArcanumMaintenanceLock heldInstallationLock,
    GrimoireOfflineTransitionJournalLocation location,
    FileHandleMetadata expected,
    ReadOnlyMemory<byte> expectedBytes);
internal Result ProveAbsentDurably(
    ArcanumMaintenanceLock heldInstallationLock,
    GrimoireOfflineTransitionJournalLocation location);
```

Add these named cases:

```text
Location_is_the_maintenance_lock_sibling_with_the_exact_suffix
Location_digest_changes_with_profile_parent_identity_or_leaf
Publication_orders_create_write_file_fsync_rename_permissions_parent_fsync
Secure_reread_returns_the_exact_published_identity_and_bytes
Publication_preserves_and_refuses_a_target_substituted_after_final_validation
Publication_crash_after_atomic_exchange_retains_authentic_predecessor_evidence
Read_refuses_symlink_hardlink_non_owner_and_non_regular_evidence
Absence_refuses_case_alias_working_previous_retiring_legacy_temp_and_unreadable_parent_evidence
Delete_moves_to_retiring_authenticates_compare_unlinks_and_proves_the_handle_unlinked
Compare_unlink_detects_a_substitution_in_the_delegated_unlink_window
Publication_failure_before_exchange_preserves_current_and_removes_exact_working_file
Publication_failure_after_exchange_returns_recovery_required_and_preserves_all_evidence
Production_never_creates_a_generic_arcanum_cleanup_quarantine
```

The event-order assertion is exactly:

```text
file:temporary-created
file:temporary-written
file:temporary-flushed
file:atomic-replace
file:previous-retained
file:permissions-verified
file:parent-flushed
file:previous-retiring
file:previous-retiring-verified
file:previous-unlinked
file:previous-zero-link-verified
file:previous-delete-parent-flushed
file:residue-absence-proved
```

For revision one, omit the predecessor-specific events. For an update, assert the complete list. Inject a target substitution in the `beforeAtomicReplace` seam after the final identity check and prove the displaced object survives at `Working` or `Previous`, the method returns recovery-required, and no anchor caller can accept the new file.

Use deterministic callbacks in the file-store constructor:

```csharp
internal GrimoireOfflineTransitionJournalFileStore(
    Action<string>? afterStep = null,
    Func<string, bool>? failBeforeStep = null,
    Action? beforeAtomicReplace = null)
```

- [ ] **Step 2: Run the file-store tests to verify RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter FullyQualifiedName~GrimoireOfflineTransitionJournalFileStoreTests \
  --no-restore --disable-build-servers -m:1
```

Expected: FAIL at compile time because the fixed-slot file store does not exist.

- [ ] **Step 3: Implement fixed location resolution and evidence inspection**

`ResolveLocation` must:

```csharp
string guarded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(guardedDirectory));
string lockPath = ArcanumMaintenanceLock.LockPathFor(guarded);
string journalPath = lockPath + ".grimoire-transition.active.json";
string parent = Path.GetDirectoryName(lockPath)!;
string leaf = Path.GetFileName(journalPath);
string workingPath = journalPath + ".publish";
string previousPath = journalPath + ".previous";
string retiringPath = journalPath + ".retiring";
```

Resolve the profile namespace from `guarded`, open `parent` no-follow as a directory, derive its physical identity with `BackupRestoreJournalAuthenticator.PhysicalIdentity`, and derive the Task 1 location digest. Reject a missing/link/non-directory parent, malformed leaf, or path that is not a sibling of the lock. Every subsequent inspection or mutation opens a fresh retained-parent capability and requires that same physical parent identity for its full lifetime.

Evidence inspection must enumerate the parent and reject:

```text
any case-insensitive spelling of `J`, `W`, `P`, or `D` other than the exact ordinal spelling;
any legacy leaf beginning `J + ".tmp."` under ordinal-ignore-case comparison;
an enumeration or no-follow probe that cannot distinguish missing from denied/unsafe;
duplicate candidate spellings or any unrecognized journal-prefixed residue;
any evidence leaf whose retained-parent, no-follow metadata is not one regular single-link owner-controlled file.
```

Enumerate and open relative to the retained parent, not by resolving the parent path again. Do not use `File.Exists` as absence authority. `RequireNoEvidence` succeeds only when `J`, `W`, `P`, and `D` are all proven absent and no alias or legacy residue exists.

- [ ] **Step 4: Implement durable replacement, reread, and deletion**

Implement a #243-only `GrimoireOfflineTransitionJournalFilePrimitives` capability. On Linux use retained-parent `openat`, `renameat2(RENAME_NOREPLACE|RENAME_EXCHANGE)`, `unlinkat`, `fstat`, and `fsync`; on macOS use `openat`, `renameatx_np(RENAME_EXCL|RENAME_SWAP)`, `unlinkat`, `fstat`, and `fsync`; on Windows retain an owner-proved directory handle without delete sharing, create/open children relative with `NtCreateFile`/`NtOpenFile`, atomically replace with a backup using `ReplaceFileW`, rename/unlink exact opened children with handle-based file-information APIs, and flush the directory handle. Unknown platforms fail closed. All imports must be source-generated and Native-AOT-safe.

The cross-platform primitive exposes only `CreateWorkingExclusive`, `PublishFirstNoReplace`, `ExchangeRetainingPrevious`, `MoveNoReplace`, `ApplyOwnerOnlyAndVerify`, `CompareUnlink`, `EnumerateExactChildren`, and `FlushParent`. `ExchangeRetainingPrevious` is one atomic namespace exchange/replace: Linux/macOS swap `W` and `J`, leaving the displaced target at `W`, while Windows `ReplaceFileW(J, W, P)` (replaced, replacement, backup) retains old `J` at `P` and publishes `W` as `J`. The Unix arm then moves displaced `W -> P` without replacement. A crash between swap and that move is an explicit recoverable `Working` state. Permission application and verification operate on the captured file handle and the relative child reopened beneath the same retained parent; they never trust a newly resolved absolute parent path. Tests pin the Windows argument order and post-call identities even when executed through a deterministic fake primitive on non-Windows hosts.

Publication must execute this exact sequence while the supplied lock asserts `location.GuardedDirectory`:

```text
1. Retain and prove the parent; require all residues absent and prove canonical absence for revision one, or exact current identity for an advance.
2. Create exact fixed `W` owner-only with create-new semantics and capture its open-handle metadata.
3. Write the complete encoded envelope.
4. Flush the file with Flush(flushToDisk: true).
5. Revalidate the retained parent and expected canonical target immediately before publication, then invoke the deterministic substitution seam.
6. For revision one, atomically rename `W -> J` without replacement. For an update, atomically exchange/replace `W` and `J` while retaining the displaced target at `W`/`P`, then move Unix `W -> P` without replacement.
7. Prove `J` has the captured working identity and prove `P` has the exact expected prior identity. A mismatch preserves every leaf and returns recovery-required.
8. Apply and verify owner-only posture on the captured `J` handle and its retained-parent-relative name.
9. Fsync the retained parent, securely reread and compare `J`, then retire the authenticated predecessor through `P -> D ->` delegated compare-unlink, fsync, and prove residue absence before returning.
```

Before publication, failures unlink only the still-proved `W` and preserve `J`. Once the atomic exchange/replace occurs, cancellation, I/O, permission, identity, and injected failures return `Data.RecoveryRequired`; they never claim no publication occurred or delete ambiguous evidence. A substitution in the last window is retained at `W`/`P`, detected by its mismatched identity, and never accepted.

`ReadIfPresentAsync` opens `J` relative to the retained parent, applies the same bounded checks as `SecureFileReader.ReadBytesAsync` with `MaxJournalFileBytes` and `requireOwnerControlled: true`, returns its final same-handle metadata, and emits `file:secure-reread` only after that proof. Evidence reads use the same routine for `W`, `P`, and `D`.

`DeleteDurably` performs no generic quarantine. It moves exact `J -> D` without replacement, reopens `D` relative/no-follow, verifies the exact expected identity and bytes before emitting `file:retiring-verified`, fsyncs the parent, then calls `CompareUnlink`. On Unix that primitive compares the retained D handle with the relative D name immediately before `unlinkat`, calls `unlinkat`, and requires `fstat` on the still-open expected handle to report link count zero immediately after; Windows uses handle disposition and applies the same zero-link/post-name proof. Any mismatch, injected substitution, or residual evidence is recovery-required and cannot produce clean absence. After the proof, fsync again and prove `J/W/P/D` absent twice. `ProveAbsentDurably` inspects through the retained parent, flushes and emits `file:absence-parent-flushed`, inspects again, then emits `file:absence-proved`. The test inventory must prove this component never calls `IdentityOwnedFileSystemCleanup` and never creates `.arcanum-cleanup-*`.

- [ ] **Step 5: Run GREEN and commit**

Run the Step 2 command again. Expected: PASS with zero failed tests.

Then:

```bash
git add \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs \
  tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStoreTests.cs
git commit -m "feat: publish Grimoire transition journal securely"
```

---

### Task 3: Anchor CAS plus begin and advance

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalAnchorStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 codecs/key provider, Task 2 file store, `BackupRestoreJournalInstallationIdentityProvider.RequireMatchesDatabase`, and caller-held `ArcanumMaintenanceLock`.
- Produces: `IGrimoireOfflineTransitionJournalStore`, the publication record, `BeginAsync`, and `AdvanceAsync`; Task 4 adds the recovery records plus `RecoverAsync` and `RetireAsync` to this same interface/store.

- [ ] **Step 1: Write failing begin/advance protocol tests**

Define this exact public-to-Infrastructure contract:

```csharp
internal sealed record GrimoireOfflineTransitionJournalPublication(
    GrimoireOfflineTransitionJournalLocation Location,
    GrimoireOfflineTransitionEnvelopeV1 Envelope,
    CovenantDigest EnvelopeDigest,
    byte[] PayloadBytes,
    GrimoireOfflineTransitionAnchorV1 Anchor,
    FileHandleMetadata FileMetadata);

internal interface IGrimoireOfflineTransitionJournalStore
{
    Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken);

    Task<Result<GrimoireOfflineTransitionJournalPublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication current,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken);
}
```

Add these cases first:

```text
Begin_provisions_closed_genesis_then_active_zero_before_file_revision_one
Begin_requires_external_installation_identity_to_match_the_database_identity
Begin_publishes_file_then_secure_reread_then_anchor_revision_one
Begin_active_exact_same_operation_resumes_only_byte_identical_payload
Begin_active_different_operation_conflicts_without_mutation
Begin_closed_same_operation_never_reopens
Begin_closed_epoch_opens_only_next_epoch_for_a_different_operation
Advance_keeps_epoch_operation_kind_payload_version_and_chains_previous_digest
Advance_compares_current_file_identity_and_anchor_before_writing
Failure_before_first_file_publication_compare_closes_only_the_exact_opening
Failure_after_atomic_replace_preserves_active_authority_for_recovery
Anchor_writes_are_read_compare_write_readback_under_the_borrowed_lock
```

Record exact cross-layer event order for a successful first publication:

```text
key:read-or-created
anchor:genesis-written
anchor:genesis-readback
anchor:opening-written
anchor:opening-readback
file:temporary-created
file:temporary-written
file:temporary-flushed
file:atomic-replace
file:permissions-verified
file:parent-flushed
file:secure-reread
anchor:advance-written
anchor:advance-readback
```

- [ ] **Step 2: Run the store tests to verify RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter FullyQualifiedName~GrimoireOfflineTransitionJournalStoreTests \
  --no-restore --disable-build-servers -m:1
```

Expected: FAIL at compile time because the anchor and journal stores do not exist.

- [ ] **Step 3: Implement the anchor store**

`GrimoireOfflineTransitionJournalAnchorStore` owns only the transition anchor account. Give it `Read`, `WriteGenesisAndVerify`, `CompareWriteAndVerify`, and `RequireMatches` methods. Every writer asserts the borrowed maintenance lock, reads the current value, compares it to the exact expected value, writes the canonical encoded anchor, and rereads exact equality. Its deterministic constructor seam is:

```csharp
internal GrimoireOfflineTransitionJournalAnchorStore(
    IOsCredentialStore credentials,
    Action<string>? afterStep = null,
    Func<string, bool>? failBeforeStep = null)
```

`CompareWriteAndVerify` takes a non-durable `GrimoireOfflineTransitionAnchorWriteStage` value (`Opening`, `Advance`, or `Closed`) so it emits only the fixed `anchor:opening-*`, `anchor:advance-*`, or `anchor:closed-*` event pair. `WriteGenesisAndVerify` emits `anchor:genesis-written` then `anchor:genesis-readback`. The stage enum is test instrumentation, is never serialized, and is not the #244 lifecycle graph.

Genesis is:

```csharp
new GrimoireOfflineTransitionAnchorV1(
    GrimoireOfflineTransitionJournalAuthenticator.AnchorVersion,
    location.ProfileNamespace.Digest,
    installationId,
    SlotEpoch: 0,
    GrimoireOfflineTransitionAnchorState.Closed,
    OperationId: null,
    Kind: null,
    PayloadVersion: null,
    Revision: 0,
    EnvelopeDigest: null,
    location.JournalLocationDigest);
```

The opening CAS copies the fixed bindings, increments the prior closed epoch in `checked` context, sets `Active`, names the new operation/kind/payload version, and resets revision/digest to `0`/`null`.

- [ ] **Step 4: Implement BeginAsync**

Use this exact decision order:

```text
1. Assert the held lock and resolve the fixed location/profile.
2. Require nonempty installation/operation, a defined kind, nonzero payload version, and bounded nonempty payload bytes.
3. Require the external profile installation identity to equal installationId.
4. Read the anchor and inspect fixed-file/temp/case evidence.
5. If no anchor: prove no evidence, CreateOrOpen the dedicated key, dispose that lease, write/read back Closed(0) genesis.
6. If an Active anchor exists: accept only the narrow exact-current state needed for idempotent begin—J alone exactly matches the anchor revision/digest and every binding, opens with the existing key, and names the same operation/kind/payloadVersion with byte-identical payload. Return that publication. A different operation conflicts; any one-ahead or W/P/D/alias/temp state returns recovery-required for Task 4's full classifier.
7. If a Closed anchor names the same operation: refuse reopening.
8. For another operation: require the existing key, exact location/profile/installation binding, and proven journal absence.
9. Compare-write Closed(E) -> Active(E+1, revision 0).
10. Seal revision 1 with ZeroDigest, publish durably, securely reread/decode/open, and compare complete bytes.
11. Reread/compare the opening anchor, then write/read back revision 1 with the envelope digest.
```

If step 10 fails before the canonical file exists, the still-running publisher may compare-close only its exact revision-zero opening after `RequireNoEvidence` succeeds. If any canonical file or temp residue exists, preserve `Active` and return recovery-required.

- [ ] **Step 5: Implement AdvanceAsync**

Advance must authenticate and compare `current` before it writes:

```text
anchor equals current.Anchor and is Active;
current envelope digest equals both the file digest and anchor digest;
secure reread identity equals current.FileMetadata identity;
profile/install/epoch/operation/kind/payloadVersion/location are unchanged;
next revision is checked current revision + 1 and within MaxRevision.
```

Seal with `current.EnvelopeDigest` as previous digest, replace the exact current file identity, reread/authenticate/compare the new bytes, reread/compare the old anchor, then compare-write/readback the next revision. A post-rename failure returns recovery-required and leaves the old anchor so Task 4 can accept exactly one-ahead.

- [ ] **Step 6: Run GREEN and commit**

Run the Step 2 command again. Expected: PASS with zero failed tests.

Then:

```bash
git add \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalAnchorStore.cs \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs \
  tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalStoreTests.cs
git commit -m "feat: add transition journal begin and advance"
```

---

### Task 4: Exact recovery, retirement, sequential epochs, and crash matrix

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalAnchorStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's begin/advance interface, narrow exact-current begin classifier, and exact publication values.
- Produces: the complete #243 fixed-slot protocol that #244 can consume without reopening or interpreting payload bytes.

Task 4 adds these exact records and methods to the existing interface/store before writing the recovery tests:

```csharp
internal enum GrimoireOfflineTransitionJournalRecoveryOutcome : byte
{
    NoActiveJournal = 1,
    Authenticated = 2,
}

internal sealed record GrimoireOfflineTransitionJournalRecoveryState(
    GrimoireOfflineTransitionJournalRecoveryOutcome Outcome,
    GrimoireOfflineTransitionJournalPublication? Publication,
    Guid? OperationId = null);

Task<Result<GrimoireOfflineTransitionJournalRecoveryState>> RecoverAsync(
    ArcanumMaintenanceLock heldInstallationLock,
    string guardedDirectory,
    CancellationToken cancellationToken);

Task<Result> RetireAsync(
    ArcanumMaintenanceLock heldInstallationLock,
    GrimoireOfflineTransitionJournalPublication terminal,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Add failing recovery and sequential-operation tests**

Add these exact cases:

```text
Recover_returns_no_active_only_for_proven_anchor_and_file_absence
Recover_accepts_an_exact_anchor_file_match
Recover_adopts_exactly_one_chained_file_revision_ahead_before_returning
Recover_rejects_older_skipped_same_revision_resealed_and_two_ahead_files
Recover_rejects_cross_profile_installation_epoch_operation_kind_payload_version_and_location
Recover_rejects_active_revision_zero_without_a_file
Recover_rejects_missing_key_or_identity_beside_active_or_closed_evidence
Recover_rejects_unanchored_file_case_alias_stale_temp_unknown_residue_and_multiple_evidence
Recover_converges_exchange_crashes_with_exact_working_or_previous_predecessor
Recover_finishes_exact_predecessor_retirement_before_adopting_one_ahead
```

To prove one-ahead, save anchor revision `N`, publish file revision `N+1` chained to its digest without updating the anchor, call `RecoverAsync`, and assert the anchor is synchronously advanced/read back before the authenticated publication is returned.

- [ ] **Step 2: Add failing retirement and crash-boundary tests**

Add these exact cases:

```text
Retire_writes_and_reads_closed_anchor_before_deleting_the_file
Recover_finishes_exact_file_cleanup_beneath_a_closed_anchor
Retire_is_idempotent_after_closed_anchor_file_delete_and_parent_fsync
Closed_anchor_refuses_an_earlier_different_or_resealed_file
Delete_refuses_identity_substitution_before_the_delegated_unlink_window
Retiring_substitution_during_the_delegated_window_is_detected_and_never_reported_absent
Next_epoch_cannot_open_until_exact_canonical_working_previous_retiring_and_temp_absence_is_proved
```

Add one theory row for every publication boundary:

```text
anchor:opening-written
anchor:opening-readback
file:temporary-created
file:temporary-written
file:temporary-flushed
file:atomic-replace
file:previous-retained
file:permissions-verified
file:parent-flushed
file:secure-reread
file:previous-retiring
file:previous-retiring-verified
file:previous-unlinked
file:previous-zero-link-verified
file:previous-delete-parent-flushed
file:residue-absence-proved
anchor:advance-written
anchor:advance-readback
```

and every retirement boundary:

```text
anchor:closed-written
anchor:closed-readback
file:retiring-moved
file:retiring-verified
file:retiring-parent-flushed
file:retiring-unlinked
file:retiring-zero-link-verified
file:delete-parent-flushed
file:absence-parent-flushed
file:absence-proved
```

For each injected crash state, construct a fresh store over the same filesystem and credential store and assert exactly one of:

```text
the prior exact publication remains current;
the one-ahead publication is adopted;
the exact closed cleanup converges to NoActiveJournal; or
ambiguous revision-zero/temp/substitution evidence fails closed.
```

No row may report clean absence while active, aliased, partial, or unreadable evidence remains.

- [ ] **Step 3: Run the expanded store tests to verify RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter 'FullyQualifiedName~GrimoireOfflineTransitionJournalStoreTests|FullyQualifiedName~GrimoireOfflineTransitionJournalFileStoreTests' \
  --no-restore --disable-build-servers -m:1
```

Expected: FAIL because recovery does not yet classify/adopt all states and retirement does not yet close-before-delete.

- [ ] **Step 4: Implement RecoverAsync**

Recovery must use this exact classification:

```text
No anchor + no J/W/P/D/alias/temp evidence -> NoActiveJournal. A key alone is harmless pre-provisioning evidence.
No anchor + any file evidence -> ManualRecoveryRequired.
Closed anchor + exact absence -> require matching external installation identity and existing key, then NoActiveJournal.
Closed anchor + canonical J -> authenticate it and require exact epoch/operation/kind/payloadVersion/revision/digest/location; move exact J to D and finish retirement.
Closed anchor + exact D only -> authenticate it against the tombstone and finish unlink/fsync/absence proof.
Active revision 0 + no canonical file -> ManualRecoveryRequired.
Active anchor + missing/mismatched installation identity or key -> fail closed.
Active anchor + exact revision/digest/file -> Authenticated.
Active anchor + file revision anchor+1 whose previous digest equals anchor digest (or ZeroDigest at revision 0) -> reread/compare-write/readback anchor, then Authenticated.
Active current J + exact chained-next W -> resume the atomic exchange/replace; never treat the working file as generic stale residue.
Active anchor + one-ahead J plus exact anchored predecessor at W or P -> normalize W to P, retire the predecessor through D, then advance/read back the anchor.
Active anchor + one-ahead J plus exact anchored predecessor at D -> finish its unlink/fsync/absence suffix, then advance/read back the anchor.
Active current J plus exact immediate predecessor P or D (anchor already advanced before cleanup returned) -> finish predecessor retirement, then return the current publication.
Every other state -> content-free rollback/replay/manual-recovery failure.
```

Before returning `Authenticated`, compare all clear envelope bindings to the anchor and location, open the envelope, and carry the secure read's exact metadata into the publication.

- [ ] **Step 5: Implement RetireAsync and closed cleanup**

Retirement must:

```text
1. Assert the lock and reread the anchor.
2. Accept only terminal.Anchor or the exact Closed projection of terminal.Anchor.
3. Inspect J/W/P/D and aliases first. If the anchor is Active, require exactly J, securely reread/authenticate it against terminal, then compare-write/readback the exact Closed anchor and emit closed-written/closed-readback.
4. Beneath the exact Closed anchor, accept exactly one of: authenticated exact J; authenticated exact D; or proven total J/W/P/D absence. Reject every other combination before mutation.
5. For exact J, move `J -> D` without replacement, reopen/authenticate D against terminal, fsync the retained parent, compare-unlink D and prove the retained expected handle's final link was removed, fsync again, and prove J/W/P/D absence twice.
6. For exact D, finish only the compare-unlink/fsync/absence suffix. For total absence, fsync and repeat the absence proof.
7. Retain the Closed tombstone and stable key.
```

Do not delete the anchor or key. A replayed earlier/different authentic envelope below `Closed` is a blocker, never cleanup authority. Generic cleanup quarantine is forbidden; every crash-recoverable byte remains under J/W/P/D and every rename, identity reread, fsync, unlink, and absence scan has its own deterministic crash row.

- [ ] **Step 6: Run GREEN and commit**

Run the Step 3 command again. Expected: PASS with zero failed tests.

Then:

```bash
git add \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions \
  tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions
git commit -m "feat: recover and retire transition journal slots"
```

---

### Task 5: Owning documentation, contract inventory, and focused qualification

**Files:**
- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`

**Interfaces:**
- Consumes: the complete Task 1–4 behavior.
- Produces: accurate documentation and one focused, non-duplicative qualification record for child #243.

- [ ] **Step 1: Update the authoritative design and orientation docs**

Add a clearly labelled #243 foundation paragraph to DESIGN §10.20.3 that states:

```text
The fixed slot is implemented but is not yet wired into Covenant reset, factory erasure, startup, admission, or a migration handler.
The canonical file is the maintenance-lock sibling ending .grimoire-transition.active.json.
The distinct key/anchor accounts, envelope/AAD bindings, closed genesis, epoch CAS, one-ahead recovery, and closed-before-delete order are exact.
Retirement uses retained-parent compare-unlink plus a retained-handle zero-link postcondition; Unix has no unlink-by-handle primitive, so success proves those postconditions and fixed-namespace absence, not that `unlinkat` removed no last-instant same-UID replacement. Any observable ambiguity returns recovery-required and never becomes clean absence.
Opaque payload bytes require a typed Native-AOT codec from #244 before any production transition may use them.
The existing V3 same-database reset checkpoint remains the active runtime path until later #239 children replace it.
```

Add the two transition accounts to DESIGN §11.2.1 with no mirror/environment reference and owners `GrimoireOfflineTransitionJournalKeyProvider` / `GrimoireOfflineTransitionJournalAnchorStore`. Update both ordinary-cleanup retention statements (the closed-catalog rule in §3.4.2 and the credential-table rule in §11.2.1) so they exclude the restore trio, reset-active pair, transition-journal pair, and host-tools taint.

Add a README issue summary that describes #243 as a security/storage foundation and explicitly says it does not yet change reset behavior or implement migrations. Add the new authentication/file/store test classes to the DESIGN §13 test matrix.

- [ ] **Step 2: Run the complete focused #243 test set once**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter 'FullyQualifiedName~GrimoireOfflineTransitionJournal|FullyQualifiedName~InstallationResetCredentialCatalogTests' \
  --no-restore --disable-build-servers -m:1
```

Expected: PASS with zero failed tests and zero skipped tests.

- [ ] **Step 3: Run child-slice build and static checks**

Run exactly once:

```bash
dotnet build RetroDownfall.Arcanum.slnx \
  -c Release --no-restore --disable-build-servers -m:1
git diff --check
RIPGREP_CONFIG_PATH=/dev/null rg -n \
  'DatabaseMigration|GrimoireOfflineTransitionKind[^\n]*Migration' \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions \
  tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions \
  README.md docs/Arcanum.DESIGN.md
RIPGREP_CONFIG_PATH=/dev/null rg -n -U \
  'internal enum GrimoireOfflineTransitionKind : byte\n\{\n\n    CovenantReset = 1,\n\n    HealthyCatalogFactoryErasure = 2,\n\n\}' \
  src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalContracts.cs
```

Expected: solution build succeeds with zero errors and zero warnings; `git diff --check` is silent; the broad discriminator scan has no #243 production migration kind; and the multiline inventory matches the complete two-member transition-kind enum exactly. Documentation prose that says migration is not implemented is permitted and must be inspected rather than deleted.

- [ ] **Step 4: Verify scope and commit**

Run:

```bash
git status --short
git diff --name-only 6f527530..HEAD
```

Expected: only #243 production/tests/docs plus the two pre-existing unrelated untracked issue-221 documents appear; no API, CLI, schema, migration, admission, reset-handler, or existing issue-tracker file changed.

Then:

```bash
git add README.md docs/Arcanum.DESIGN.md
git commit -m "docs: document offline transition journal foundation"
```

Do not add either issue-221 document.
