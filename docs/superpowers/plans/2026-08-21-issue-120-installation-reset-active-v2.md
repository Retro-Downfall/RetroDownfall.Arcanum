# Issue 120: Authenticated Installation-Reset Active Record Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task and `superpowers:test-driven-development` for every behavior change.

**Goal:** Replace every newly created plain installation-reset active record with a lock-owned, AES-256-GCM-authenticated V2 envelope and anti-rollback anchor, while preserving bounded ordinary V1 recovery and the existing reset workflow.

**Architecture:** The running host becomes the linearization owner for global/all handoffs because it already holds the installation maintenance lock. A reset-specific codec, key lease/provider, anchor store, and V2 active-store facade reuse Task 15's profile and physical-identity primitives but keep independent domains and credentials. Startup authenticates external evidence under the lock before bootstrap, compares the database installation UUID before readiness, and admits only the exact existing recovery-host state. The CLI transports a typed confirmed handoff and later passes its exact post-shutdown lock into the offline service; it never creates or advances V2 evidence itself.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core minimal APIs, System.Text.Json source generation, AES-GCM/SHA-256, SQLCipher/SQLite, xUnit, FakeItEasy, Microsoft.Extensions.DependencyInjection, GitHub CLI.

**Spec:** [`docs/superpowers/specs/2026-08-21-issue-120-installation-reset-active-v2-design.md`](../specs/2026-08-21-issue-120-installation-reset-active-v2-design.md)

## Global constraints

- Work only in `/private/tmp/RetroDownfall.Arcanum-long-term-memory` on `codex/issue-120-installation-reset-active-v2`; integrate only after all gates are green.
- Preserve and never stage `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`.
- Follow strict red-green-refactor: add one focused behavior or table of the same behavior, run it and confirm the intended failure, implement the minimum production change, rerun green, then refactor with the same test green.
- Use `apply_patch` for hand edits. Do not add EF migrations, reflection-based JSON, anonymous API DTOs, or a second maintenance-lock acquisition.
- Keep one blank line after each C# statement in accordance with repository style.
- Do not change CLI syntax, `docs/Arcanum.CommandMap.json`, `docs/Compendium.README.md`, dated reviews, or prior plans/specifications unless a verified surface change requires it.
- Do not create intermediate commits. The user requested the commit only after the complete matrix is green; record task checkpoints with `git status --short`, `git diff --stat`, and the focused test output instead.
- Treat cancellation or durability uncertainty after a possible write as active evidence, never as absence. Never expose the key, tag, digest mismatch, raw path, lock, or lease in an API result.
- Every build invocation must finish with zero warnings and zero errors. Investigate every unexpected failure with `superpowers:systematic-debugging` before changing code.

---

### Task 1: Establish the clean baseline and freeze existing compatibility fixtures

**Files:**

- Inspect: `README.md`
- Inspect: `docs/Arcanum.DESIGN.md`
- Inspect: `docs/Arcanum.API.md`
- Inspect: `docs/Arcanum.Command.Reference.md`
- Inspect: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs`
- Inspect: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`

- [ ] Verify the worktree and branch before touching source:

  ```bash
  git branch --show-current
  git status --short
  git rev-parse long-term-memory
  git rev-parse origin/long-term-memory
  ```

  Expected: the feature branch is active; the two long-term-memory revisions match; only the approved spec/plan and the unrelated IDE file are untracked or modified.

- [ ] Run the baseline build and current reset suites, saving the exact warning/error counts in the task log:

  ```bash
  dotnet build RetroDownfall.Arcanum.slnx
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationReset"
  ```

- [ ] Add/retain explicit V1 compatibility fixtures for a bounded canonical ordinary record and a record without handoff fields. Do not make V1 an authority for host-tools/full-reset state. Name the fixtures:

  ```csharp
  [Fact]
  public async Task Legacy_V1_record_without_handoff_fields_remains_readable()

  [Fact]
  public async Task Legacy_V1_ordinary_handoff_remains_a_bounded_migration_candidate()
  ```

- [ ] Run the two compatibility tests green before changing the store codec:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Legacy_V1"
  ```

- [ ] Record the no-commit checkpoint with `git status --short` and `git diff --stat`.

---

### Task 2: Add reset-active credential identities and the single-take key lease

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordKeyProvider.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveAuthenticationTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveKeyLeaseCallSiteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`

- [ ] RED: add exact identity tests for `installation-reset-active-key-` and `installation-reset-active-anchor-`. Accept only a complete 64-character lowercase hexadecimal Task 15 profile suffix; reject bare, uppercase, malformed, alias, and cross-profile accounts. Run:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Installation_reset_active_accounts_require_one_canonical_profile_suffix"
  ```

- [ ] GREEN: add these owned helpers without changing Task 15 identities:

  ```csharp
  public const string InstallationResetActiveKeyAccountPrefix =
      "installation-reset-active-key-";

  public const string InstallationResetActiveAnchorAccountPrefix =
      "installation-reset-active-anchor-";

  internal static string InstallationResetActiveKeyAccount(string profileSuffix);

  internal static string InstallationResetActiveAnchorAccount(string profileSuffix);

  internal static bool IsInstallationResetActiveAccount(string account);
  ```

- [ ] RED: add tests named `Active_key_provider_creates_reads_back_and_returns_only_canonical_32_byte_material`, `Active_key_provider_recovery_never_creates_substitutes_or_repairs_a_key`, and `Active_key_lease_is_single_take_zeroized_and_not_serializable`. Prove `OpenExisting` does not write on absent/corrupt/unavailable credentials.

- [ ] GREEN: implement `InstallationResetActiveRecordKeyLease` and provider with this lock boundary:

  ```csharp
  internal sealed class InstallationResetActiveRecordKeyProvider(
      IOsCredentialStore credentials)
  {
      internal Result<InstallationResetActiveRecordKeyLease> CreateOrOpen(
          ArcanumMaintenanceLock heldInstallationLock,
          string guardedDirectory,
          BackupRestoreProfileNamespace profileNamespace);

      internal Result<InstallationResetActiveRecordKeyLease> OpenExisting(
          BackupRestoreProfileNamespace profileNamespace);

      internal Result<bool> IsPresent(
          BackupRestoreProfileNamespace profileNamespace);

      internal Result RemoveAndVerifyAbsent(
          ArcanumMaintenanceLock heldInstallationLock,
          string guardedDirectory,
          BackupRestoreProfileNamespace profileNamespace);
  }
  ```

  `CreateOrOpen` must call `AssertHeldFor`, generate 32 random bytes only when absent, store canonical unpadded base64url, read back, decode, and constant-time compare. The lease owns exactly one array; `TryTakeKey` uses `Interlocked.Exchange`; authenticator disposal zeroes a taken key in `finally`; lease disposal zeroes an untaken key.

- [ ] RED then GREEN: add source-inventory tests named `Only_the_active_authenticator_can_take_an_active_record_key_lease` and `Only_the_active_store_retirement_path_can_delete_active_record_credentials`. Limit key-taking to `InstallationResetActiveRecordAuthenticator`; limit pair deletion to the reset-active anchor/store retirement implementation.

- [ ] Run the entire task suite:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetActiveAuthenticationTests|FullyQualifiedName~InstallationResetActiveKeyLeaseCallSiteTests"
  ```

- [ ] Refactor only after green, then record the no-commit checkpoint.

---

### Task 3: Define the immutable V2 projection, strict JSON context, and sole authenticator

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActivePersistence.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveAuthenticationTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs`

- [ ] RED: add tests named:

  ```text
  Active_location_digest_binds_profile_parent_identity_and_active_leaf
  V2_envelope_uses_exact_aad_canonical_base64url_and_digest_encoding
  V2_envelope_refuses_wrong_key_tag_profile_installation_operation_location_scope_or_plan_without_detail
  V2_envelope_rejects_unknown_missing_unmapped_trailing_or_noncanonical_json
  V2_payload_rejects_a_nonnull_reserved_host_tools_marker_pair
  Active_anchor_requires_canonical_profile_bound_revision_and_digest
  ```

  Use fixed key/nonce/UUID/digest fixtures to assert the exact bytes, and separate tamper cases for every authenticated outer field.

- [ ] GREEN: add the closed persistence types. Keep them internal, immutable, V2-only, and source-generated:

  ```csharp
  internal enum InstallationResetActiveAnchorState : byte
  {
      Active = 1,
      Closed = 2,
  }

  internal sealed record InstallationResetActiveEnvelopeV2(
      byte Version,
      CovenantDigest ProfileNamespaceDigest,
      Guid InstallationId,
      Guid OperationId,
      ulong Revision,
      CovenantDigest PreviousEnvelopeDigest,
      CovenantDigest ActiveLocationDigest,
      InstallationResetScope Scope,
      string PlanId,
      string NonceBase64Url,
      string CiphertextBase64Url,
      string AuthenticationTagBase64Url);

  internal sealed record InstallationResetActiveAnchorV1(
      byte Version,
      InstallationResetActiveAnchorState State,
      CovenantDigest ProfileNamespaceDigest,
      Guid InstallationId,
      Guid OperationId,
      ulong Revision,
      CovenantDigest EnvelopeDigest,
      CovenantDigest ActiveLocationDigest);

  internal sealed record InstallationResetActiveWorkspaceV2(
      Guid CampaignId,
      string WorkspaceRoot);

  internal sealed record InstallationResetActiveFileIdentityV2(
      string Value,
      long Length,
      ulong HardLinkCount);

  internal sealed record InstallationResetActivePreservedBackupV2(
      string CanonicalPath,
      InstallationResetActiveFileIdentityV2 Identity);

  internal sealed record InstallationResetActiveAcceptedBindingV2(
      string BindingId,
      ImmutableArray<string> SelectedRoots,
      ImmutableArray<string> ExcludedRoots,
      ImmutableArray<InstallationResetActivePreservedBackupV2> PreservedBackups,
      ImmutableArray<string> CredentialAccounts,
      ImmutableArray<string> DataPlanIds);

  internal sealed record InstallationResetActiveCredentialResultV2(
      string Account,
      InstallationResetItemStatus Status,
      string? ErrorCode);

  internal sealed record InstallationResetActiveOnlineCompletionV2(
      Guid ServerOperationId,
      Guid RequestedOperationId,
      string DataPlanId,
      long RowsDeleted,
      long FilesDeleted,
      long EstimatedBytesDeleted,
      long DerivedRecordsDeleted);

  internal sealed record InstallationResetActivePayloadV2(
      byte Version,
      Guid OperationId,
      string PlanId,
      InstallationResetScope Scope,
      InstallationResetActiveWorkspaceV2? Workspace,
      InstallationResetActiveAcceptedBindingV2 AcceptedBinding,
      InstallationResetPhase Phase,
      bool PointOfNoReturn,
      long RowsDeleted,
      long FilesDeleted,
      long EstimatedBytesDeleted,
      ImmutableArray<InstallationResetActiveCredentialResultV2> CredentialResults,
      string? LastErrorCode,
      InstallationResetDataHandoff? DataHandoff,
      InstallationResetActiveOnlineCompletionV2? OnlineDataCompletion,
      JsonElement? HostToolsMarkerPairReset);

  [JsonSourceGenerationOptions(
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
  [JsonSerializable(typeof(InstallationResetActiveEnvelopeV2))]
  [JsonSerializable(typeof(InstallationResetActiveAnchorV1))]
  [JsonSerializable(typeof(InstallationResetActivePayloadV2))]
  [JsonSerializable(typeof(ImmutableArray<string>))]
  [JsonSerializable(typeof(ImmutableArray<InstallationResetActivePreservedBackupV2>))]
  [JsonSerializable(typeof(ImmutableArray<InstallationResetActiveCredentialResultV2>))]
  internal partial class InstallationResetActiveJsonContext : JsonSerializerContext;

  [JsonSourceGenerationOptions(
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
  [JsonSerializable(typeof(InstallationResetActiveRecord))]
  internal partial class InstallationResetActiveLegacyJsonContext : JsonSerializerContext;
  ```

  Also register `InstallationResetActiveWorkspaceV2`, `InstallationResetActiveFileIdentityV2`, `InstallationResetActivePreservedBackupV2`, `InstallationResetActiveAcceptedBindingV2`, `InstallationResetActiveCredentialResultV2`, `InstallationResetActiveOnlineCompletionV2`, `InstallationResetActiveAnchorState`, `InstallationResetScope`, `InstallationResetPhase`, `InstallationResetItemStatus`, `InstallationResetDataHandoff`, `CovenantDigest`, and `JsonElement` on the V2 context. Do not register the key lease or public API handoff there. Decode legacy V1 only through `InstallationResetActiveLegacyJsonContext` and the existing exact `InstallationResetActiveRecord` shape.

- [ ] GREEN: implement the sole codec as `InstallationResetActiveRecordAuthenticator`, with constants `EnvelopeVersion = 2`, `AnchorVersion = 1`, and `MaxActiveFileBytes = 64 * 1024`. Its public internal surface is:

  ```csharp
  internal static Result<InstallationResetActiveLocation> ResolveLocation(
      string guardedRoot,
      BackupRestoreProfileNamespace profileNamespace);

  internal static Result<CovenantDigest> ActiveLocation(
      CovenantDigest profileNamespaceDigest,
      CovenantDigest guardedParentPhysicalIdentityDigest,
      string activeLeaf);

  internal static Result<InstallationResetActiveEnvelopeV2> Seal(
      InstallationResetActiveRecordKeyLease key,
      InstallationResetActiveLocation location,
      Guid installationId,
      ulong revision,
      CovenantDigest previousEnvelopeDigest,
      InstallationResetActivePayloadV2 payload);

  internal static Result<InstallationResetActivePayloadV2> Open(
      InstallationResetActiveRecordKeyLease key,
      InstallationResetActiveLocation expectedLocation,
      Guid expectedInstallationId,
      InstallationResetActiveEnvelopeV2 envelope);

  internal static Result<CovenantDigest> EnvelopeDigest(
      InstallationResetActiveEnvelopeV2 envelope);
  ```

  Also add strict `EncodeEnvelope`, `DecodeEnvelope`, `EncodeAnchor`, and `DecodeAnchor` methods. Decode within 64 KiB, round-trip to identical canonical bytes, require canonical base64url, 12-byte nonce, 16-byte tag, nonempty RFC-4122 UUIDs, positive checked UInt64 revision, closed scope byte, and bounded UTF-8 plan ID.

- [ ] Encode AAD exactly as the spec: domain plus zero separator, version, profile digest, RFC-4122 big-endian installation and operation UUIDs, UInt64BE revision, previous digest, location digest, one-byte scope, UInt32BE plan byte count, and plan bytes. Encode `EnvelopeDigest` under its independent domain over every canonical envelope field including nonce, ciphertext, and tag.

- [ ] After decrypting, require payload version/operation/scope/plan equality with the outer header and require `HostToolsMarkerPairReset is null` before returning a domain record. Return one content-free integrity error for all authentication mismatches.

- [ ] Run focused tests and source-generation compilation:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetActiveAuthenticationTests|FullyQualifiedName~InstallationResetContractTests"
  dotnet build RetroDownfall.Arcanum.slnx
  ```

- [ ] Refactor duplicated Task 15 byte helpers only if both protocols retain independent domains and exact encodings; record the no-commit checkpoint.

---

### Task 4: Implement the anchored store protocol, crash windows, and V1 migration

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveAnchorStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveAuthenticationTests.cs`

- [ ] RED: add begin/advance tests named:

  ```text
  New_v2_publication_writes_revision_zero_anchor_before_revision_one_envelope
  New_v2_publication_rereads_authenticates_and_then_verifies_the_anchor
  Advance_chains_exactly_one_revision_and_rejects_regression_skip_overflow_or_changed_binding
  ```

  Use fake file/credential event logs to pin order: key readback, anchor revision zero readback, temp-file flush, atomic replace, parent flush, secure reread/authentication, anchor compare-write, anchor readback.

- [ ] GREEN: introduce reset-local location/publication/recovery records and a lock-required store surface:

  ```csharp
  internal enum InstallationResetActiveRecoveryOutcome : byte
  {
      NoActiveRecord = 1,
      AuthenticatedV2 = 2,
      LegacyV1 = 3,
  }

  internal sealed record InstallationResetActiveRecoveryState(
      InstallationResetActiveRecoveryOutcome Outcome,
      InstallationResetActivePublication? Publication,
      InstallationResetActiveRecord? LegacyRecord);

  internal interface IInstallationResetActiveStore
  {
      Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);

      Task<Result<InstallationResetActivePublication>> BeginAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          Guid installationId,
          InstallationResetActiveRecord record,
          CancellationToken cancellationToken = default);

      Task<Result<InstallationResetActivePublication>> AdvanceAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          InstallationResetActivePublication current,
          InstallationResetActiveRecord next,
          CancellationToken cancellationToken = default);

      Task<Result> RetireAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          Guid operationId,
          CancellationToken cancellationToken = default);
  }
  ```

  Keep a separate non-mutating authenticated inspection method for `IInstallationStartupProbe`; it may accept only an exact V2 anchor/file pair or bounded V1 and may never close, migrate, create, or delete evidence.

- [ ] Reuse `BackupRestoreProfileNamespace`, `BackupRestoreJournalAuthenticator.ResolveProfileNamespace`, `BackupRestoreJournalInstallationIdentityProvider`, `FileHandleIdentityInterop`, `SecureFileReader`, `IdentityOwnedFileSystemCleanup`, owner-only permissions, and Task 15 parent-flush patterns. Do not reuse Task 15's domains, envelope/anchor/context, retained tombstone policy, or staging-location digest.

- [ ] RED: add recovery matrix tests:

  ```text
  Recovery_accepts_only_an_exact_anchor_envelope_pair_or_one_authenticated_envelope_ahead
  Recovery_rejects_rollback_skipped_revision_cross_profile_cross_operation_and_location_substitution
  Recovery_treats_file_key_anchor_partial_combinations_and_lookalikes_as_blocking_evidence
  Recovery_never_creates_or_repairs_missing_authentication_material
  ```

- [ ] GREEN: exact-match recovery authenticates and returns; one-ahead recovery authenticates the envelope, requires `revision == anchor + 1` and `previousDigest == anchor.digest`, advances/readbacks the anchor, then returns. All other combinations fail closed. Never treat canonical/temp lookalikes, symlinks, credential unavailability, malformed JSON, missing key, or missing active anchor as absence.

- [ ] RED: add `V1_ordinary_record_migrates_to_authenticated_v2_before_the_next_effect` and `V1_record_with_full_reset_authority_or_nonnull_reserved_slot_is_refused`.

- [ ] GREEN: preserve the existing canonical V1 decoder within 64 KiB. Under the held lock, validate it as an ordinary operation, seed/require Task 15 external installation identity from the database UUID, publish revision-zero anchor, wrap identical semantics into V2 revision one, reread/authenticate, and anchor it before returning. A migration crash may resume only the same valid V1 operation; V1 never creates or authorizes the reserved full-reset checkpoint.

- [ ] RED: add `Closed_anchor_retirement_deletes_file_then_anchor_then_key_idempotently` and `Startup_cleanup_removes_only_closed_or_orphaned_key_evidence_and_never_active_evidence`.

- [ ] GREEN: retirement must verify the operation, write/readback `Closed`, identity-capture/delete file, flush parent/prove absent, remove/verify anchor, and remove/verify key last. Startup cleanup completes those suffix steps idempotently; an Active anchor without its exact file always blocks.

- [ ] Run the task suite and repeat it after refactor:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~InstallationResetActiveAuthenticationTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 5: Make startup lock-first and verify installation identity before readiness

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetMaintenanceLockAccessor.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/IInstallationResetStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationStartupProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationStartupProbeTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`

- [ ] RED: add `StartAsync_acquires_the_maintenance_lock_before_classifying_active_reset_evidence` and an accessor lifetime test proving no borrower owns/disposes the host lock.

- [ ] GREEN: implement a singleton nonserializable accessor. Only `GrimoireDatabaseHostedService` may attach/detach; consumers borrow the exact live handle after `AssertHeldFor`:

  ```csharp
  internal interface IInstallationResetMaintenanceLockAccessor
  {
      Result<ArcanumMaintenanceLock> BorrowHeldLock(string guardedDirectory);
  }
  ```

  Attach immediately after `TryAcquire`; detach before every disposal/failure path. Never acquire inside the accessor.

- [ ] GREEN: replace the host's pre-lock `IInstallationStartupProbe` decision with an internal locked recovery seam:

  ```csharp
  internal sealed record InstallationResetStartupRecoveryState(
      ActiveInstallationReset? ActiveReset,
      Guid? ExpectedInstallationId,
      bool IsLegacyV1);

  internal interface IInstallationResetStartupRecovery
  {
      Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);
  }
  ```

  It resolves physical/profile/location evidence, authenticates V2 against the existing external installation identity, closes only the one-ahead/Closed cleanup windows, or returns a bounded V1 candidate. It never seeds identity before the database is safely available.

- [ ] RED: add startup cases named:

  ```text
  StartAsync_authenticates_v2_and_closes_the_single_envelope_ahead_window_before_bootstrap
  StartAsync_allows_only_an_authenticated_prepared_global_or_all_host_handoff_without_proof
  StartAsync_allows_eligible_v1_only_for_locked_migration_and_blocks_every_other_legacy_state
  StartAsync_compares_the_active_envelope_installation_uuid_before_readiness
  ```

- [ ] GREEN: preserve `InstallationResetHostStartupAdmission` exactly: only global/all + Prepared + `HostFactoryErasure` + no completion proof may start a recovery host. Authenticate V2 first; V1 gets only compatibility admission for the same ordinary state. Every other phase/proof/scope blocks.

- [ ] GREEN: thread the expected active installation UUID into `GrimoireDatabaseBootstrapper.EnsureInitializedAsync`. After schema convergence gives a safe `covenant_authority_state` row, but before protected recovery and `MarkReady`, query/parse its `InstallationIdentity`, compare to the authenticated expected UUID, and fail closed on missing/malformed/mismatch. This is the second stage of startup identity validation.

- [ ] Preserve `IInstallationStartupProbe.ReadActiveResetAsync` for CLI/read-only use, but make it exact-authentication-only and non-mutating. Authentication failures remain typed failures; they never become `null` or `IsFreshInstallation == true`.

- [ ] Run:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationStartupProbeTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 6: Add the typed API handoff and host-owned publication coordinator

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/InstallationResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostHandoffCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/InstallationResetHostHandoffEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`

- [ ] RED: add exact optional-wire-shape, required-member, numeric-scope, and context-reachability tests for:

  ```csharp
  public sealed record InstallationResetHostHandoff(
      [property: JsonRequired] Guid RequestedOperationId,
      [property: JsonRequired] string InstallationPlanId,
      [property: JsonRequired] InstallationResetScope Scope,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      DataRetentionWorkspaceBinding? Workspace,
      [property: JsonRequired] InstallationResetAcceptedBinding AcceptedBinding);
  ```

- [ ] GREEN: add the record and append `InstallationResetHostHandoff? InstallationResetHandoff = null` to `FactoryResetRequest`. Register it explicitly in `ArcanumJsonContext`; preserve ordinary request JSON when the field is absent.

- [ ] RED: add endpoint tests named:

  ```text
  Factory_reset_handoff_rejects_missing_mismatched_or_non_global_binding_before_publication_or_data_apply
  Factory_reset_handoff_publishes_authenticated_v2_before_the_factory_coordinator_runs
  Factory_reset_handoff_records_the_content_free_completion_proof_before_responding
  Factory_reset_handoff_retires_only_a_proven_pre_effect_plan_changed_record
  Factory_reset_handoff_retains_replay_evidence_after_uncertain_or_post_effect_failure
  Requested_operation_id_is_a_replay_key_and_never_the_server_operation_or_authority_identity
  ```

- [ ] GREEN: add an internal coordinator consumed by the endpoint, not by the CLI:

  ```csharp
  internal interface IInstallationResetHostHandoffCoordinator
  {
      Task<Result> BeginOrRecoverAsync(
          InstallationResetHostHandoff handoff,
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);

      Task<Result> RecordOnlineCompletionAsync(
          InstallationResetHostHandoff handoff,
          DataRetentionApplyResult result,
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);

      Task<Result> RetirePreEffectAsync(
          InstallationResetHostHandoff handoff,
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);
  }
  ```

  It reads the current database installation UUID, requires/seeds the Task 15 external identity under the borrowed host lock, begins/replays V2 before data apply, and advances the proof before success response serialization.

- [ ] Validate before publication: paired request plan/operation IDs, exact equality with handoff, singleton nonempty accepted data-plan ID matching `ExpectedPlanId`, global/all scope, scope/workspace relationship, and all current plan bounds. A named legacy V1 request may only resume/migrate the exact eligible existing V1; it may not create fresh state without the typed handoff.

- [ ] Call `IDataRetentionService.ApplyAsync` only after publication succeeds. On exact trusted pre-effect `Data.PlanChanged`, close/retire before returning the error. Retain evidence for cancellation, uncertainty, other failures, and any result that cannot prove zero effect. Use lifecycle cleanup/checkpoint tokens after a possible durable write.

- [ ] Prove ordinary handoff-free factory calls retain existing behavior in `DataRetentionEndpointTests`.

- [ ] Run:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetHostHandoffEndpointTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~InstallationResetContractTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 7: Move CLI handoff ownership to the host and thread the exact offline lock

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.DataLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs`

- [ ] RED: add command/boundary tests:

  ```text
  Global_apply_builds_the_host_handoff_from_the_confirmed_bound_plan
  Crash_before_host_request_leaves_no_local_active_evidence
  Fresh_global_apply_sends_a_typed_handoff_without_local_active_record_publication
  Offline_continuation_passes_the_boundarys_exact_held_maintenance_lock_to_the_locked_reset_seam
  Resume_reuses_authenticated_host_proof_and_never_republishes_or_replays_the_data_effect
  ```

- [ ] GREEN: keep `BindOnlineDataPlan`, but replace CLI-local `PrepareAsync`, `RecordCompletedAsync`, and `RetirePreEffectAsync` ownership with this effect-free construction/read surface:

  ```csharp
  public interface IInstallationResetOnlineDataHandoff
  {
      Result<InstallationResetPlan> BindOnlineDataPlan(
          InstallationResetPlanRequest request,
          InstallationResetPlan localPlan,
          DataRetentionPlan onlinePlan);

      Result<InstallationResetHostHandoff> CreateHostHandoff(
          InstallationResetApplyRequest request,
          InstallationResetPlan confirmedPlan);

      Task<Result<InstallationResetHostHandoff?>> ReadAsync(
          InstallationResetApplyRequest request,
          CancellationToken cancellationToken = default);
  }
  ```

  `CreateHostHandoff` only copies the confirmed bound plan and mints the replay UUID in memory. A fresh global/all path must write no active file/key/anchor before the host request.

- [ ] GREEN: change the boundary's acquisition delegate and local variable from `IDisposable?` to concrete `ArcanumMaintenanceLock?`. Send the full typed handoff in `FactoryResetRequest`; do not append proof or retire locally. Preserve `host apply/replay -> proof -> shutdown -> lock -> offline continuation` as externally observed ordering.

- [ ] Add the locked offline seam and make the boundary pass its exact instance:

  ```csharp
  internal interface IInstallationResetLockedService
  {
      Task<Result<InstallationResetResult>> ApplyUnderMaintenanceLockAsync(
          InstallationResetApplyRequest request,
          ArcanumMaintenanceLock heldInstallationLock,
          CancellationToken cancellationToken = default);
  }
  ```

  The service must call `AssertHeldFor` and pass this lock through every V2 migration/advance/retire. It must never reacquire or dispose it. Workspace reset remains offline and handoff-free.

- [ ] GREEN: for resumed global/all state, project a full `InstallationResetHostHandoff` from authenticated V2 or valid V1, including the complete accepted binding. If proof is absent, resend the same host operation for idempotent replay; if proof is durable, skip host data apply and continue offline.

- [ ] Update `ArcanumApiClientTests` to assert the optional handoff JSON contains no secret, raw path capability, key, anchor, tag, lease, or lock material.

- [ ] Run:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetApplyBoundaryTests|FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~ArcanumApiClientTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationResetExistingGrimoireTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 8: Complete credential containment, DI graphs, and AOT closure

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCompositionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`

- [ ] RED: add `Ordinary_installation_reset_catalog_excludes_the_active_key_and_anchor_accounts`. Include configured, stale provider, missing, corrupt, and environment-backed ordinary credentials while proving the three restore accounts, two reset-active accounts, and host-tools taint account are never enumerated/deleted by ordinary cleanup.

- [ ] GREEN: make exclusions explicit in `InstallationResetCredentialCatalog`. Only reset-active locked retirement may derive/delete the new pair; ordinary `key list` and factory credential deletion do not expose them.

- [ ] RED: add real graph tests:

  ```text
  Real_host_graph_registers_the_singleton_lock_accessor_and_authenticated_handoff_publisher
  Cli_and_host_graphs_resolve_the_authenticated_active_store_dependencies
  Factory_reset_host_handoff_is_reachable_from_arcanum_json_context
  ```

- [ ] GREEN: factor one idempotent installation-reset registration used by both host and CLI graphs. Register one concrete reset service per scope and alias interfaces to it; singleton key provider/anchor dependencies over the existing `IOsCredentialStore`; singleton lock accessor; scoped authenticated store; host handoff coordinator; startup recovery; and locked offline seam. Ensure `AddArcanumInfrastructure` activates host registrations and `AddArcanumInstallationReset` remains safe for CLI composition.

- [ ] Compile with trim/AOT analyzers and run the focused graph tests:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~InstallationResetCompositionTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
  dotnet build RetroDownfall.Arcanum.slnx
  ```

- [ ] Resolve every compiler/analyzer warning now; do not defer warnings to the final gate. Record the no-commit checkpoint.

---

### Task 9: Update authoritative and human documentation with the implemented contract

**Files:**

- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Arcanum.CHAT-LOOP.md`
- Modify: `docs/Arcanum.OATH.md`
- Modify: `docs/ArcanumOATH.Human.md`
- Modify: `docs/Arcanum.Design.Human.md`
- Modify: `docs/Arcanum.DEBUGGING.Human.md`

- [ ] Update `README.md` and OATH mirrors to mark only #120 delivered. Leave #121, #122, #123, #94, and #74 open; do not describe future full-reset effects as implemented.

- [ ] Replace the plain-active-record caveat in `Arcanum.DESIGN.md` with the exact V2 envelope/AAD/location/envelope-digest/key/anchor protocol, host lock ownership, two-stage installation identity check, V1 migration, one-ahead recovery, closed retirement, and key-last cleanup.

- [ ] Document the additive optional `InstallationResetHostHandoff` request shape and validation/publication/proof/PlanChanged semantics in `Arcanum.API.md`. State that outer fields are display-only until authenticated and that ordinary handoff-free factory requests remain unchanged.

- [ ] Reconcile `Arcanum.CHAT-LOOP.md` and `Arcanum.Command.Reference.md`: all noncompleted or unreported completed records block normal startup; the only exception is the owning operation's authenticated or eligible V1 `Prepared + HostFactoryErasure + no proof` recovery host, which must migrate before its next effect.

- [ ] Document the exact before/after credential inventory: dynamic provider slots remain ordinary Arcanum-owned identities; restore accounts, reset-active key/anchor, and host-tools taint are excluded from ordinary deletion; retirement deletes only the reset-active pair after Closed/file removal, key last.

- [ ] Update human design/debugging docs with operator-visible states and content-free recovery errors. Explain exact anchor/envelope, one-ahead, Closed cleanup, partial evidence blockers, and why manual deletion/substitution is unsafe.

- [ ] Search for stale contradictions and unintended command/config drift:

  ```bash
  rg -n "plain.*active|V1 active|issue #120|#120|Prepared.*HostFactoryErasure|installation-reset-active" README.md docs
  git diff -- docs/Arcanum.CommandMap.json docs/Compendium.README.md
  ```

- [ ] Run documentation/contract tests included in the main suite, then record the no-commit checkpoint.

---

### Task 10: Run the attack matrix, full verification, and independent review

**Files:**

- Review: all task-modified source, tests, and docs
- Do not modify: `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`

- [ ] Run all focused reset and endpoint suites once more from a clean process:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationReset"
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~ArcanumApiClientTests"
  ```

- [ ] Audit the acceptance attack matrix explicitly: wrong key/tag/profile/installation/operation/location/scope/plan; replay/rollback/skipped revision; file/key/anchor partial states; noncanonical/oversize/trailing/unmapped JSON; symlink/lookalike paths; credential failures; one-ahead only; V1 migration before effect; every startup phase; cancellation and uncertain durability; Closed cleanup; ordinary catalog retention.

- [ ] Invoke `superpowers:requesting-code-review` and delegate independent reviews of (1) crypto/store, (2) host/CLI/startup ordering, and (3) API/docs/tests. Resolve every valid finding with a new red-green cycle; use `superpowers:receiving-code-review` before applying reviewer suggestions.

- [ ] Invoke `superpowers:verification-before-completion`, then run the complete matrix exactly:

  ```bash
  dotnet build RetroDownfall.Arcanum.slnx
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
  dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
  ./scripts/coverage.sh --threshold
  ./scripts/verify-aot-il-warnings.sh
  git diff --check
  ```

  Record the exact build warning/error counts and every exit code. Any edit after this run invalidates it and requires the affected focused test plus the complete matrix again.

- [ ] Inspect final scope and secrets before staging:

  ```bash
  git status --short
  git diff --stat
  git diff --check
  git diff -- . ':(exclude).idea/.idea.RetroDownfall.Arcanum/.idea/.name'
  ```

- [ ] Confirm the unrelated IDE file remains unstaged and the command map/config docs have no diff.

---

### Task 11: Commit, integrate, push, close #120, and remove merged feature branches

**Files:**

- Stage: only reviewed issue #120 source, tests, specs/plans, and owning documentation
- Preserve: `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`

- [ ] Invoke `superpowers:finishing-a-development-branch`. Stage explicit task paths only; inspect `git diff --cached --check` and `git diff --cached --stat`; create the single green feature commit:

  ```bash
  git commit -m "feat: authenticate installation reset active records"
  ```

- [ ] Switch the linked worktree to `long-term-memory`, fast-forward or merge the feature commit without touching the unrelated main checkout, and rerun at least the solution build plus the complete Arcanum test project on the merged tree. If the merge changes the tree, rerun the entire Task 10 matrix.

- [ ] Push only the integration branch:

  ```bash
  git push origin long-term-memory
  ```

- [ ] Verify the remote branch contains the green commit, then close only issue #120 with a concise implementation-and-verification comment. Do not close #94 or #74:

  ```bash
  gh issue close 120 --comment "Implemented on long-term-memory with authenticated V2 installation-reset active records; all repository build, test, coverage, and AOT warning gates pass."
  ```

- [ ] Delete the merged feature branch locally. Delete its remote counterpart only if it exists; never delete `long-term-memory`:

  ```bash
  git branch -d codex/issue-120-installation-reset-active-v2
  git push origin --delete codex/issue-120-installation-reset-active-v2
  ```

  Treat a nonexistent remote feature branch as already clean, not as a delivery failure.

- [ ] Final read-only verification: `git status --short`, `git branch --merged long-term-memory`, `git ls-remote --heads origin long-term-memory`, and `gh issue view 120`. Report the commit SHA, pushed branch, issue state, deleted branches, full verification results, and preserved unrelated IDE file.
