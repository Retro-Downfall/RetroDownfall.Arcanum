# Issue 122: Host-Tools Marker Pair and Campaign Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task, and use `superpowers:test-driven-development` for every behavior change. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Advance an attested full installation reset through exact resumable compare-deletion of the database/OS host-tools marker pair and through durable per-Campaign marker cleanup, while leaving the full reset active and recovery-required for issue #123.

**Architecture:** A sealed reset coordinator owns the authenticated phase machine, opens the OS capability before one unpooled core SQLCipher connection, reuses the shared pair joiner, and publishes each proven effect through the existing V2 envelope/anchor protocol. Typed V1 restart evidence permits fixed-time signature revalidation after a crash without a fresh statement or fresh live admission, and exact legal phase/sibling-state proofs close both effect-before-publication crash windows. The existing Campaign marker lifecycle owns registry inventory, codec/opener/root capabilities, idempotent kind-four prepare/rehydration, and reconciliation behind revision-bound private cleanup authorities; the coordinator never receives a generic database, credential, path, or filesystem deletion capability. Startup authenticates the active file first and initializes only existing SQLCipher key access before scoped checkpoint recovery, without running ordinary schema bootstrap or readiness.

**Tech Stack:** .NET 10, C# 13, System.Text.Json source generation, SHA-256/P-256 IEEE-P1363, SQLCipher/SQLite, platform credential APIs, xUnit, FakeItEasy, Microsoft.Extensions.DependencyInjection, Git, and GitHub CLI.

**Spec:** [`docs/superpowers/specs/2026-08-22-issue-122-host-tools-marker-pair-campaign-cleanup-design.md`](../specs/2026-08-22-issue-122-host-tools-marker-pair-campaign-cleanup-design.md)

## Global constraints

- Work only in `/private/tmp/RetroDownfall.Arcanum-long-term-memory`, directly on `long-term-memory`, whose upstream is `origin/long-term-memory`. Issue #122 explicitly forbids a separate enable/feature branch, so there is no branch merge step; the final verified commit is already on the requested integration branch.
- Preserve and never stage `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`. Treat every unrelated change discovered later as user-owned until proven otherwise.
- The root `README.md` and every file under `docs/` were reviewed before this plan. Re-read the owning sections immediately before updating them, because code and docs travel together.
- Follow strict red-green-refactor. Every test name listed below is an individual microcycle, not a batch: add one focused test, run it and record the intended assertion/`Result` failure, make the smallest production change, rerun green, then move to the next name. When a production type is wholly absent, first add only an inert compile-safe contract/skeleton whose operation returns an explicit failure; contract-shape tests alone may use compiler failure as red. Refactor only while the just-green focused test remains green.
- Do not create intermediate commits. The user authorized one commit only after all implementation, documentation, review, build, test, coverage, and AOT gates are green. Use `git status --short`, `git diff --stat`, and captured focused-test output as task checkpoints.
- Use `apply_patch` for hand edits. Do not add a numbered EF migration, reflection-based serialization, anonymous wire DTOs, generic credential compare-delete, generic authority-row clear method, or a second pair classifier.
- Keep the C# house style: file-scoped namespaces, positional records for DTOs, source-generated JSON, primary constructors for DI, and one blank line after every C# statement.
- Keep issue #123 out of scope. Do not rotate installation/authority identity, delete the Grimoire or reset-control credentials, retire the active record, publish `InstallationResetPhase.Completed`, or report a clean installation.
- After `PairJournaled`, caller cancellation cannot abandon possible effects. Use the existing bounded five-second recovery/checkpoint token pattern for durability/publication completion; represent any uncertain outcome as recovery required.
- The OS capability is process-local and nonserializable. Live deletion requires the retained native item identity; restart may reopen only the fixed slot whose exact bytes and normalized evidence reproduce the signed checkpoint.
- The database reset path reads only `StateKey`, `InstallationIdentity`, `HostToolsStateCode`, `TransitionId`, `TaintTimeMasterVersion`, and `TaintFingerprint`; its exact raw value/storage-class capability never enters the persisted checkpoint.
- Recovery never performs fresh pair admission. It reconstructs the checkpoint evidence, reruns the one shared pair joiner, and requires exactly `TaintedMatched` with a nonnull pair before considering the current post-effect state.
- Before `PairJournaled` on fresh work, and before any resumed pair or Campaign effect, require the already-opened SQLCipher connection to reproduce the exact #122 Core schema manifest. An exact #121 predecessor, missing companion object, changed parent/trigger definition, manifest drift, or inspection uncertainty is a content-free manual blocker before either host-tools marker effect; this slice performs no ad hoc DDL or legacy table rewrite.
- Accept effect-before-publication recovery only for the two exact ordered crash suffixes: `PairJournaled` plus same-installation clean database plus unchanged original OS marker, or `DatabaseMarkerCompareDeleted` plus the same clean database plus exact fixed-slot OS absence. Every out-of-order absence, missing singleton, changed survivor, or generic read failure blocks.
- Make the existing host-tools transition authenticate reset-active evidence after acquiring the installation lock and before reading either marker; only proven `NoActiveRecord` may proceed. Active, legacy, malformed, or unreadable reset evidence blocks, so another supported process cannot fill an effect/publication crash window.
- Every active publication makes an existing cleanup proof/authority stale. Reread/authenticate and remint after publishing the prepared receipt; never carry an authority across any active revision.
- The typed active checkpoint persists no raw display path. Kind-four parent rows use a nullable path hint: exact `Opened` evidence requires a digest-matching nonnull hint, while `Unavailable`/`Mismatch` require null so a missing registry row can still receive its mandatory blocked child.
- Freeze the clarified canonical values from the spec: intent-vector domain `Arcanum.FullInstallationReset.CampaignMarkerIntentVector.v1`, empty digest `26b63be668fe309add01922ea6dd3fefe222c7833ff9dfa379bda0275cf98574`, inventory-entry domain `Arcanum.FullInstallationReset.CampaignMarkerInventoryEntry.v1`, observation domain `Arcanum.FullInstallationReset.CampaignMarkerObservation.v1`, and observation codes `Opened = 1`, `Unavailable = 2`, `Mismatch = 3`.
- Keep legacy V1 active-record input bounded at 64 KiB. Bound V2 plaintext at 4 MiB and its encoded envelope/file at 8 MiB before allocation; retain the 4,096 Campaign/intent ceiling.
- Every build command must finish with `0 Warning(s)` and `0 Error(s)`. If a test, build, runtime, schema, or platform contract fails unexpectedly, invoke `superpowers:systematic-debugging` before changing production code.

---

### Task 1: Reconfirm branch, issue contract, documentation baseline, and current green state

**Files:**

- Inspect: `README.md`
- Inspect: `docs/Arcanum.DESIGN.md`
- Inspect: `docs/Arcanum.API.md`
- Inspect: `docs/Arcanum.Command.Reference.md`
- Inspect: `docs/Arcanum.DEBUGGING.Human.md`
- Inspect: `docs/Arcanum.Design.Human.md`
- Inspect: `docs/Arcanum.OATH.md`
- Inspect: `docs/ArcanumOATH.Human.md`
- Inspect: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Inspect: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActivePersistence.cs`
- Inspect: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs`
- Inspect: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerStore.cs`

- [ ] Verify the exact worktree, branch, upstream, and user-owned untracked state before source edits:

  ```bash
  pwd
  git branch --show-current
  git status --short
  git rev-parse long-term-memory
  git rev-parse origin/long-term-memory
  git worktree list --porcelain
  ```

  Expected: `pwd` is `/private/tmp/RetroDownfall.Arcanum-long-term-memory`; `long-term-memory` is active and matches its upstream; the approved spec/plan and `.idea/.idea.RetroDownfall.Arcanum/.idea/.name` are the only expected untracked files.

- [ ] Re-fetch issue #122 and its comments, record its URL/title/state, confirm parent #94 and dependencies #120/#121, and save no generated output file:

  ```bash
  gh issue view 122 --repo Retro-Downfall/RetroDownfall.Arcanum --comments
  gh issue view 94 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,title,url
  gh issue view 74 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,title,url
  gh issue view 120 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,title,url
  gh issue view 121 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,title,url
  gh issue view 123 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,title,url
  ```

- [ ] Re-read the owning documentation sections listed above and compare the current #121 boundary with the approved #122 spec. Do not edit historic plans, specs, reviews, `docs/Arcanum.CommandMap.json`, `docs/Arcanum.ConstraintInventory.md`, `docs/Compendium.README.md`, or `docs/CHAT-LOOP-OPTIMIZATION-PLAN.md` unless an actual public surface discovered during implementation requires it.

- [ ] Run the baseline build and focused areas before adding tests:

  ```bash
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~FullInstallationReset|FullyQualifiedName~InstallationResetActive|FullyQualifiedName~HostProcessTools|FullyQualifiedName~CampaignPathMarkerLifecycle"
  ```

  Record exact test totals and verify the build summary says `0 Warning(s)` and `0 Error(s)`. Any baseline failure is diagnosed before implementation rather than normalized into this change.

- [ ] Record the no-commit checkpoint:

  ```bash
  git status --short
  git diff --stat
  ```

---

### Task 2: Freeze canonical evidence digests and fixed-time attestation recovery

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/FullInstallationResetRemediationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetMarkerPairResetContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetMarkerPairResetDigests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetRemediationAttestationVerifierTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetMarkerPairResetDigestTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`

- [ ] RED: add literal byte-preimage and expected SHA-256 tests named:

  ```text
  Pair_evidence_preimage_uses_the_exact_fourteen_field_v1_order_and_encodings
  Pair_evidence_rejects_clean_partial_default_non_strict_or_oversized_evidence
  Campaign_display_path_digest_uses_strict_utf8_and_checked_uint16be_framing
  Same_handle_ownership_digest_uses_one_retained_root_and_exact_network_order_fields
  Campaign_inventory_entry_digest_uses_the_exact_six_entry_fields_without_count
  Campaign_inventory_digest_freezes_zero_one_many_and_rfc4122_uuid_order
  Campaign_inventory_rejects_default_duplicate_reordered_zero_revision_invalid_or_oversized_vectors
  Full_reset_effect_digest_uses_only_the_ten_unframed_fields_and_scope_byte_01
  Full_reset_intent_vector_freezes_the_empty_literal_and_preserves_authenticated_campaign_order
  Full_reset_intent_vector_rejects_default_zero_duplicate_or_more_than_4096_ids
  Campaign_observation_digest_has_exact_opened_and_blocked_code_dependent_preimages
  Owner_effect_rejects_a_same_shaped_digest_from_another_lifecycle_domain
  ```

  Use UUID fixtures for which legacy `.NET Guid.ToByteArray()` lexicographic order and RFC-4122 network-byte order disagree; assert that `Guid.CompareTo` agrees with RFC field/network order and that production writes UUIDs explicitly with `TryWriteBytes(bigEndian: true)`. Assert raw preimages, not only final digests. Execute each name as the separate microcycle required above; start with inert digest contracts so every behavioral red is an assertion or explicit failure rather than a batch of missing-type compiler errors.

- [ ] Run the digest class after each microcycle and record the expected focused red, then green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~FullInstallationResetMarkerPairResetDigestTests
  ```

- [ ] GREEN: add the exact phase and detached persistence value types, keeping live capabilities out of them:

  ```csharp
  internal enum HostToolsMarkerPairResetPhase : byte
  {
      PairJournaled = 1,
      DatabaseMarkerCompareDeleted = 2,
      OsMarkerCompareDeleted = 3,
      PairAbsenceVerified = 4,
  }

  internal sealed record FullInstallationResetSignedAttestationProjectionV1(
      byte Version,
      Guid OperationId,
      Guid InstallationId,
      Guid HostToolsTransitionId,
      ulong TaintMasterKeyVersion,
      CovenantDigest AuthorityFingerprint,
      CovenantDigest DatabaseMarkerDigest,
      CovenantDigest OsMarkerDigest,
      CovenantDigest RemediationActionDigest,
      string NonceBase64Url,
      string Issuer,
      DateTimeOffset IssuedAtUtc,
      DateTimeOffset ExpiresAtUtc,
      string SignatureBase64Url);

  internal sealed record CampaignMarkerInventoryEntryV1(
      Guid CampaignId,
      long PriorPathRevision,
      CovenantDigest MarkerDigest,
      CovenantDigest IndexedPhysicalIdentityDigest,
      CovenantDigest CanonicalDisplayPathDigest,
      CovenantDigest SameHandleOwnershipEvidenceDigest);

  internal enum CampaignPathFullResetCleanupObservationCode : byte
  {
      Opened = 1,
      Unavailable = 2,
      Mismatch = 3,
  }
  ```

  Give the signed projection a closed `FromAttestation` constructor and `ToAttestation` reconstruction that copies every bounded public field, including signature spelling, without interpretation.

- [ ] GREEN: implement one canonical helper owner for strict UTF-8, checked `UInt16BE` text, checked `UInt64BE`, RFC-4122 network-order UUIDs, and raw 32-byte digests. Expose narrow `Result<CovenantDigest>` calculators for pair evidence, display path, same-handle ownership, inventory entry, inventory vector, owner effect, intent vector, and observation. In Core, add exact public nonauthorizing contract `public static Result<CovenantDigest> FullInstallationResetRemediationAttestationDigest.Calculate(FullInstallationResetExternalRemediationAttestation attestation)`. It reuses the canonical remediation preimage, requires the canonical decoded 64-byte signature, owns domain `Arcanum.FullInstallationReset.ExternalRemediationDigest.v1` and its existing length framing, consults no trust root/time, and returns only a detached digest. Refactor fresh/fixed-time verification to consume this calculator rather than a private digest owner. Validate every vector completely before hashing and deep-copy all accepted arrays.

- [ ] RED: extend verifier tests with:

  ```text
  Recovery_verification_accepts_a_now_expired_statement_at_the_authenticated_acceptance_time
  Recovery_verification_rejects_acceptance_before_issue_or_at_expiry
  Recovery_verification_requires_whole_second_utc_acceptance
  Recovery_verification_rejects_signature_root_projection_action_or_pair_tampering
  Signed_attestation_digest_reuses_the_exact_signature_inclusive_v1_golden_vector
  Recovery_verification_returns_the_same_signed_attestation_digest_as_fresh_acceptance
  Recovery_verification_rejects_same_shaped_signed_attestation_and_action_digest_substitution
  Public_signed_attestation_digest_calculator_freezes_the_signature_inclusive_v1_vector
  Verifier_authorization_digest_equals_the_nonauthorizing_calculator
  Signed_attestation_digest_calculator_rejects_noncanonical_signature_without_consulting_trust_or_time
  Authenticated_claim_matching_remains_nonauthorizing
  ```

- [ ] Run each verifier test against the inert fixed-time arm to record its intended failure, then rerun it green before adding the next:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~FullInstallationResetRemediationAttestationVerifierTests
  ```

- [ ] GREEN (compile seam first): update `InstallationResetServiceTests.FakeRemediationVerifier` with an inert `VerifyAtAcceptedTime` implementation that returns an explicit test failure result, then extend the verifier port and implementation with:

  ```csharp
  Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
      FullInstallationResetExternalRemediationAttestation attestation,
      Guid authenticatedInstallationId,
      HostProcessToolsMatchedPair persistedPair,
      DateTimeOffset acceptedAtUtc);
  ```

  The fake method is a compile-safe seam only; Task 10 replaces the affected service expectations. Factor a private verification core. Fresh `Verify` supplies `TimeProvider.GetUtcNow()`; recovery supplies only the authenticated acceptance time. Both run complete canonical shape, pair equality, action, nonce, issuer, independent trust-root, P-256/SHA-256 IEEE-P1363 signature, and half-open time-window validation. `MatchesAuthenticatedClaim` remains equality-only and is never called as restart authorization. Run the existing service test class once after this single compile-safe edit to prove the whole test assembly compiles before continuing.

- [ ] Run both focused classes green, then refactor only shared byte writers whose tests freeze independent domains:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~FullInstallationResetMarkerPairResetDigestTests|FullyQualifiedName~FullInstallationResetRemediationAttestationVerifierTests"
  ```

- [ ] Record `git status --short` and `git diff --stat`; do not commit.

---

### Task 3: Authenticate the typed checkpoint and enforce monotonic restart state

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetMarkerPairResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActivePersistence.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveAuthenticationTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/InstallationResetActiveStoreTestExtensions.cs`

- [ ] RED: replace the old “reserved nonnull slot rejects” expectation with authenticated typed-checkpoint tests:

  ```text
  V2_payload_authenticates_the_closed_host_tools_marker_pair_checkpoint
  V2_checkpoint_requires_version_one_exact_claim_all_scope_operation_installation_and_recovery_state
  V2_restart_proof_requires_accepted_at_utc_and_signed_attestation_digest_exact_claim_equality
  V2_checkpoint_rejects_zero_unknown_skipped_regressed_or_inconsistent_phase
  V2_checkpoint_rejects_changed_signed_projection_pair_digest_inventory_or_owner_effect
  V2_checkpoint_rejects_same_shaped_attestation_action_effect_or_reconstructed_child_digest_substitution
  V2_checkpoint_authentication_reuses_the_Core_signed_attestation_digest_calculator
  V2_checkpoint_rejects_unknown_missing_reordered_trailing_or_noncanonical_nested_json
  V2_checkpoint_receipt_fields_are_all_null_or_all_present
  V2_checkpoint_receipt_rejects_default_oversized_duplicate_reordered_or_aliased_vectors
  V2_checkpoint_rejects_partial_cleanup_counts_between_prepared_and_terminal
  V2_checkpoint_terminal_counts_require_checked_deleted_plus_orphan_equal_count
  V2_projection_deep_copies_inventory_and_intent_vectors_in_both_directions
  V2_context_owns_the_complete_closed_checkpoint_graph_and_no_live_authority_type
  Legacy_v1_never_adopts_the_ignored_checkpoint_member
  ```

- [ ] Add and run these tests one at a time against inert typed projections, recording the intended focused red and green for each:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetActiveAuthenticationTests|FullyQualifiedName~InstallationResetContractTests"
  ```

- [ ] GREEN: add the exact restart/checkpoint graph:

  ```csharp
  internal sealed record FullInstallationResetRestartProofV1(
      byte Version,
      FullInstallationResetSignedAttestationProjectionV1 SignedAttestation,
      DateTimeOffset AcceptedAtUtc,
      CovenantDigest SignedAttestationDigest,
      HostProcessToolsDatabaseMarkerEvidence DatabaseMarkerEvidence,
      HostProcessToolsOsMarkerEvidence OsMarkerEvidence,
      CovenantDigest PairEvidenceDigest);

  internal sealed record HostToolsMarkerPairResetCheckpointV1(
      byte Version,
      HostToolsMarkerPairResetPhase Phase,
      FullInstallationResetRestartProofV1 RestartProof,
      ImmutableArray<CampaignMarkerInventoryEntryV1> CampaignInventory,
      CovenantDigest CampaignMarkerInventoryDigest,
      CovenantDigest OwnerEffectDigest,
      ulong? MarkerIntentCount,
      ImmutableArray<Guid>? OrderedMarkerIntentIds,
      CovenantDigest? MarkerIntentVectorDigest,
      ulong? DeletedCount,
      ulong? OrphanCount);
  ```

  Replace `InstallationResetActivePayloadV2.HostToolsMarkerPairReset` with the typed checkpoint. Add the corresponding record member only as the final backward-compatible optional positional parameter—`[property: JsonIgnore] HostToolsMarkerPairResetCheckpointV1? HostToolsMarkerPairReset = null`—after the existing optional claim, so every pre-Task-3 construction site continues to compile. Project it with defensive nested copies in `FromRecord` and `ToRecord`; do not make it a new required constructor argument.

- [ ] GREEN: register every checkpoint DTO, enum, evidence record, digest, and immutable vector only on `InstallationResetActiveJsonContext`. Do not add proof, cleanup authority, native capability, runtime observation, SQL capability, or checkpoint types to `ArcanumJsonContext`, `CliJsonContext`, `/v1`, configuration, MCP, or legacy V1 contexts.

- [ ] GREEN: split the codec bounds:

  ```csharp
  internal const int MaxActivePayloadBytes = 4 * 1024 * 1024;

  internal const int MaxActiveFileBytes = 8 * 1024 * 1024;
  ```

  Use the payload bound before encrypt/decrypt allocation and the file bound before envelope read/decode. Keep `InstallationResetActiveStore.MaxBytes = 64 * 1024` for legacy V1 input only. Add exact-bound and plus-one tests.

- [ ] RED: add active-store transition tests:

  ```text
  Advance_allows_only_null_to_pair_journaled_then_same_or_next_proven_pair_phase
  Advance_cannot_remove_regress_skip_or_substitute_restart_or_inventory_evidence
  Advance_allows_pair_absent_null_receipt_to_exact_prepared_receipt
  Advance_allows_only_fixed_vector_prepared_zero_counts_then_one_terminal_count_publication
  Recovery_round_trips_structurally_equal_immutable_checkpoint_vectors
  One_ahead_anchor_recovery_preserves_the_exact_typed_checkpoint
  ```

- [ ] Add and run each store transition test as its own focused red/green cycle:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~InstallationResetActiveStoreTests
  ```

- [ ] GREEN: make payload validation reconstruct the exact public attestation and call Core's nonauthorizing `FullInstallationResetRemediationAttestationDigest.Calculate`; compare that result to `SignedAttestationDigest`, enforce exact proof-to-claim equality for it and `AcceptedAtUtc`, and recompute every remaining digest through its canonical owner. The static authenticator gains no verifier, clock, or trust-root dependency. Implement explicit structural equality for `ImmutableArray<T>` and every reference evidence object. `SamePayload` and `IsMonotonicTransition` must keep claim, signed projection, restart proof, pair evidence, Campaign entries/order/digest, and owner effect immutable. After pair absence, allow exactly null receipt to one fixed prepared receipt with zero deleted/orphan counts, then that same vector to one terminal publication with checked `DeletedCount + OrphanCount == MarkerIntentCount`; reject every partial count shape, vector/count substitution, repeat publication, or later transition. For zero campaigns the prepared zero-count shape is already terminal and receives no second count publication.

- [ ] Run all task tests green and a source-generation build with zero warnings/errors:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetActiveAuthenticationTests|FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~InstallationResetContractTests"
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 4: Implement the six-column database projection, attempt-local raw CAS, and durability proof

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostProcessToolsPairReader.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetDatabaseTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetHostProcessToolsPairReaderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs`

- [ ] RED: add SQLCipher scratch-database tests named:

  ```text
  Marker_projection_selects_only_the_six_allowed_authority_columns
  Marker_projection_accepts_canonical_eight_byte_blob_taint_version
  Marker_projection_accepts_legacy_positive_integer_taint_version
  Marker_projection_accepts_valid_guid_text_spelling_and_preserves_its_raw_value
  Marker_projection_rejects_malformed_guid_version_state_or_singleton_before_mutation
  Compare_clear_predicates_all_six_raw_values_and_storage_classes
  Compare_clear_changes_only_state_transition_taint_version_and_taint_fingerprint
  Compare_clear_loses_when_any_raw_value_or_storage_class_changes
  Compare_clear_does_not_read_or_assign_epoch_master_or_recovery_authority_fields
  Committed_clear_runs_checked_wal_durability_and_proves_same_installation_clean
  Recovery_clean_suffix_reruns_checked_wal_durability_before_phase_publication
  Recovery_clean_suffix_barrier_failure_preserves_pair_journaled
  Clean_proof_rejects_missing_duplicate_changed_installation_or_partial_marker_shape
  Recovery_observation_accepts_only_exact_original_tainted_or_same_installation_clean_shape
  Session_opens_one_unpooled_initialized_core_connection_per_attempt
  ```

  Assert the SQL text/projection ordinals where useful, and mutate each raw field independently between logical read and CAS. Use both INTEGER and BLOB valid taint-version representations.

- [ ] Add and run each database test as its own focused microcycle against an inert narrow session:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~HostToolsMarkerPairResetDatabaseTests
  ```

- [ ] GREEN: create a reset-only session owner over `ICovenantMaintenanceConnectionFactory.OpenAsync` plus `ICovenantSqliteConnectionInitializer`. The session owns exactly one pooling-disabled core `SqliteConnection` until disposed and exposes only:

  ```csharp
  internal interface IHostToolsMarkerPairResetDatabase
  {
      Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenAsync(
          CancellationToken cancellationToken);
  }

  internal sealed class HostToolsMarkerPairResetDatabaseSession : IAsyncDisposable
  {
      internal SqliteConnection BorrowCoreConnection();

      internal Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadTaintedAsync(
          CancellationToken cancellationToken);

      internal Task<Result<HostToolsDatabaseMarkerRecoveryObservation>>
          ObserveExpectedOrCleanAsync(
              HostProcessToolsDatabaseMarkerEvidence expected,
              CancellationToken cancellationToken);

      internal Task<Result<HostToolsDatabaseMarkerCompareDeleteCapability>>
          BeginImmediateAndCaptureAsync(
              HostProcessToolsDatabaseMarkerEvidence expected,
              CancellationToken cancellationToken);

      internal Task<Result> CompareClearCommitAndProveDurableAsync(
          HostToolsDatabaseMarkerCompareDeleteCapability capability,
          CancellationToken cancellationToken);

      internal Task<Result> ProveSameInstallationCleanDurableAsync(
          string expectedInstallationIdentity,
          CancellationToken cancellationToken);
  }
  ```

  `HostToolsDatabaseMarkerRecoveryObservation` is a closed nonauthorizing value with exactly
  `OriginalTainted = 1` and `SameInstallationClean = 2`; every other shape is a failed `Result`.
  Implement this exact surface. The session owns transaction lifetime, and the caller cannot obtain
  raw values or authorize an arbitrary update.

- [ ] GREEN: query exactly `StateKey`, `InstallationIdentity`, `HostToolsStateCode`, `TransitionId`, `TaintTimeMasterVersion`, and `TaintFingerprint`. Validate one required singleton, state code, GUID text, positive INTEGER or canonical eight-byte BLOB version, and 32-byte fingerprint. Construct existing normalized evidence and compare every property with the journaled expected evidence.

- [ ] GREEN: inside the same immediate transaction, capture each raw `object` plus SQLite storage-class string in a private, single-use, transaction-bound capability. Execute one `UPDATE` assigning only clean state and null transition/taint columns. Its `WHERE` binds every raw value and `typeof(...)` result, including `StateKey` and `InstallationIdentity`; require exactly one changed row. Roll back on mismatch, malformed storage, cancellation before possible write, or capability reuse.

- [ ] GREEN: commit with the existing bounded checkpoint token, run a checked WAL checkpoint/durability barrier using `CovenantWalCheckpointOutcome.RequireTruncated`, and reread only the same six columns to prove the singleton retains the same installation identity with exact clean marker shape. `ProveSameInstallationCleanDurableAsync` must run that same checked barrier and reread even when recovery first observes clean state, closing a crash before the original attempt's barrier. Missing or duplicate singleton evidence is integrity failure, never absence; barrier failure preserves the prior active phase.

- [ ] GREEN: make `InstallationResetHostProcessToolsPairReader` reuse the narrow logical projection while preserving its existing OS-before-database order. Stop `InstallationResetExistingGrimoire` from reading a whole `HostProcessToolsAuthorityRow` for full-reset marker evidence; retain its unrelated planning/data/workspace/identity roles unchanged.

- [ ] Run all database/pair-reader compatibility tests and build green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~HostToolsMarkerPairResetDatabaseTests|FullyQualifiedName~InstallationResetHostProcessToolsPairReaderTests|FullyQualifiedName~InstallationResetExistingGrimoireTests"
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 5: Build the authenticated pair phase coordinator and private cleanup authority

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICampaignPathMarkerLifecycle.FullInstallationReset.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathFullInstallationResetContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCleanupAuthorityTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCallSiteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreStartupRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs`

- [ ] RED: add fresh phase-machine tests:

  ```text
  Begin_requires_the_callers_exact_held_installation_lock_and_authenticated_claim_publication
  Begin_opens_and_retains_the_os_capability_before_opening_or_reading_the_database
  Begin_calls_the_shared_joiner_and_accepts_only_tainted_matched_with_a_nonnull_pair
  Begin_reverifies_the_signed_statement_at_claim_accepted_time_against_the_reopened_pair
  Begin_completes_campaign_inventory_before_pair_journal_publication
  Begin_publishes_pair_journaled_before_either_marker_effect
  Database_effect_advances_only_to_database_marker_compare_deleted_after_durability
  Os_effect_advances_only_to_os_marker_compare_deleted_after_exact_delete_and_absence
  Final_pair_proof_advances_only_to_pair_absence_verified
  Every_effect_rereads_and_authenticates_the_current_envelope_and_anchor
  Failure_or_uncertainty_leaves_the_last_proven_phase_active_and_recovery_required
  Caller_cancellation_before_pair_journaled_performs_no_marker_effect
  Caller_cancellation_after_pair_journaled_uses_a_bounded_recovery_owned_checkpoint_token
  ```

- [ ] RED: add restart matrix tests:

  ```text
  Resume_from_pair_journaled_reverifies_projection_signature_pair_and_database_cas
  Resume_from_database_deleted_reopens_only_the_exact_fixed_os_slot
  Resume_from_os_deleted_requires_exact_database_and_os_absence
  Resume_from_pair_absence_verified_replays_no_pair_mutation
  Resume_reconstructs_persisted_evidence_and_requires_shared_joiner_tainted_matched_nonnull
  Resume_never_falls_back_to_a_fresh_live_pair_admission_read_or_second_classifier
  Resume_rejects_zero_unknown_skipped_regressed_or_tampered_checkpoint_state
  Begin_refuses_an_exact_issue_121_predecessor_schema_before_pair_journal_or_marker_effect
  Resume_refuses_missing_or_drifted_issue_122_cleanup_schema_before_pair_effect
  Exact_issue_122_core_schema_is_proven_on_the_same_connection_before_inventory_or_effect
  Pair_journaled_clean_database_with_unchanged_os_recovers_the_database_effect_publication_gap
  Database_deleted_clean_database_with_exact_os_absence_recovers_the_os_effect_publication_gap
  Recovered_database_effect_reruns_wal_durability_before_advancing
  Recovered_os_effect_reruns_platform_durability_and_second_absence_readback_before_advancing
  Recovered_effect_barrier_failure_preserves_the_prior_authenticated_phase
  Pair_journaled_os_absence_or_both_absent_is_out_of_order_and_blocks
  Any_missing_singleton_generic_read_failure_or_nonadjacent_phase_state_blocks
  Changed_surviving_database_or_os_evidence_is_preserved_and_blocks
  Restart_after_statement_expiry_uses_only_authenticated_accepted_at_utc
  ```

- [ ] Add and run each coordinator behavior as an individual microcycle against inert coordinator contracts:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests
  ```

- [ ] GREEN: add the internal coordinator port:

  ```csharp
  internal interface IHostToolsMarkerPairResetCoordinator
  {
      Task<Result<InstallationResetActivePublication>> BeginAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          InstallationResetActivePublication acceptedClaim,
          FullInstallationResetExternalRemediationAttestation attestation,
          CancellationToken cancellationToken);

      Task<Result<InstallationResetActivePublication>> ResumeAsync(
          ArcanumMaintenanceLock heldInstallationLock,
          InstallationResetActivePublication checkpoint,
          CancellationToken cancellationToken);
  }
  ```

  Add required constructor dependency `IFullInstallationResetCampaignSchemaReadiness`, whose exact member is `Task<Result> RequireExactAsync(SqliteConnection liveCoreConnection, CancellationToken cancellationToken)`. Task 5 tests use an inert-success fake; Task 7 supplies the manifest-backed implementation. `BeginAsync` reauthenticates the claim, opens OS first, opens one database session, calls the readiness port on that same connection, calls the shared joiner, uses `VerifyAtAcceptedTime`, asks the lifecycle for complete initial inventory, recomputes every digest, and advances `PairJournaled`. It then converges exact next phases. `ResumeAsync` starts only from the authenticated checkpoint, opens OS then the one database session, proves readiness before any recovery effect, reconstructs its original evidence, calls the same shared joiner and requires exactly `TaintedMatched` with a nonnull pair, and never asks for a fresh admission read or statement file. A readiness failure disposes attempt-owned resources, preserves the last authenticated active publication, and performs zero database-marker, OS-marker, Campaign-marker, schema, or filesystem mutation.

- [ ] GREEN: define the reset-only OS port consumed by the coordinator with this exact runtime-only surface. Factory-only result types enforce the stated nullable shape, and every enum value outside the closed sets is rejected before use:

  ```csharp
  internal enum HostToolsMarkerPairResetOsOpenStatus : byte
  {
      Opened = 1,
      Absent = 2,
      Mismatch = 3,
      Unavailable = 4,
  }

  internal enum HostToolsMarkerPairResetOsDeleteStatus : byte
  {
      Deleted = 1,
      Mismatch = 2,
      Unavailable = 3,
  }

  internal enum HostToolsMarkerPairResetOsAbsenceStatus : byte
  {
      Absent = 1,
      Mismatch = 2,
      Unavailable = 3,
  }

  internal interface IHostToolsMarkerPairResetOsCapability : IDisposable
  {
  }

  internal sealed class HostToolsMarkerPairResetOsOpenResult
  {
      private HostToolsMarkerPairResetOsOpenResult(
          HostToolsMarkerPairResetOsOpenStatus status,
          HostProcessToolsOsMarkerEvidence? evidence,
          IHostToolsMarkerPairResetOsCapability? capability)
      {
          Status = status;
          Evidence = evidence;
          Capability = capability;
      }

      internal HostToolsMarkerPairResetOsOpenStatus Status { get; }

      internal HostProcessToolsOsMarkerEvidence? Evidence { get; }

      internal IHostToolsMarkerPairResetOsCapability? Capability { get; }

      internal static HostToolsMarkerPairResetOsOpenResult Opened(
          HostProcessToolsOsMarkerEvidence evidence,
          IHostToolsMarkerPairResetOsCapability capability) =>
          new(
              HostToolsMarkerPairResetOsOpenStatus.Opened,
              evidence ?? throw new ArgumentNullException(nameof(evidence)),
              capability ?? throw new ArgumentNullException(nameof(capability)));

      internal static HostToolsMarkerPairResetOsOpenResult Absent() =>
          new(HostToolsMarkerPairResetOsOpenStatus.Absent, null, null);

      internal static HostToolsMarkerPairResetOsOpenResult Mismatch() =>
          new(HostToolsMarkerPairResetOsOpenStatus.Mismatch, null, null);

      internal static HostToolsMarkerPairResetOsOpenResult Unavailable() =>
          new(HostToolsMarkerPairResetOsOpenStatus.Unavailable, null, null);
  }

  internal interface IHostToolsMarkerPairResetOsPort
  {
      HostToolsMarkerPairResetOsOpenResult OpenExact();

      HostToolsMarkerPairResetOsOpenResult ReopenExact(
          HostProcessToolsOsMarkerEvidence expectedEvidence);

      Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
          IHostToolsMarkerPairResetOsCapability capability,
          HostProcessToolsOsMarkerEvidence expectedEvidence,
          CancellationToken cancellationToken);

      Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
          CancellationToken cancellationToken);
  }
  ```

  The private constructor is implemented, not left as a declaration; factories are the only construction surface. `Opened` rejects null/default evidence or capability and carries both; every other factory carries neither. `OpenExact()` is the fresh OS-first admission arm: it captures and returns canonical normalized evidence from the fixed slot without requiring evidence the fresh coordinator does not yet possess. `ReopenExact(expectedEvidence)` is checkpoint-only and compares every normalized field with authenticated persisted evidence. The coordinator owns and disposes an opened capability exactly once on every success, refusal, cancellation, and exception path. `Mismatch` means a definite malformed/noncanonical item on fresh open, a present-but-nonmatching item on recovery reopen, or a present item during absence proof; an ambiguous/native error is always `Unavailable`. Coordinator tests use these factories with a fake port and opaque fake capability; Task 8 supplies the sole production adapter. No type exposes an account name, raw native handle, encoded secret, or generic credential delete.

- [ ] GREEN: Task 8's `HostProcessToolsMarkerStore`-adjacent reset adapter is the only production implementation of this port. `OpenExact`/`ReopenExact` call `IHostProcessToolsMarkerCredentialCapabilitySource.OpenFixedSlot`; `PresentInvalid` maps directly to Infrastructure `Mismatch` with no capability or database access. For `Opened`, the adapter takes ownership of the disposable Secrets capability, copies its bounded exact encoded-secret UTF-8 bytes, strictly decodes them through Core's one marker codec, and wraps valid evidence in an Infrastructure-private capability implementation; malformed/noncanonical content disposes and maps to `Mismatch`. Only the recovery method then compares valid normalized evidence with caller-supplied authenticated evidence. `CompareDeleteExactAsync` accepts only that wrapper, rechecks the normalized expected evidence, calls the same retained Secrets capability's `CompareDeleteExact(expectedEncodedSecretUtf8)`, and maps its closed delete status one-for-one while holding the shared mutation gate around the complete Secrets-owned delete/durability/readback operation. `ProveExactAbsenceAsync` holds that same gate around one call to the Secrets source's exact `ProveFixedSlotDurablyAbsent()` operation; Secrets owns both fixed-slot reads and the intervening platform durability/synchronization barrier, and Infrastructure maps its `Absent`/`Present`/`Unavailable` result to `Absent`/`Mismatch`/`Unavailable`. Wrapper disposal disposes the Secrets capability and zeroes every Infrastructure-owned encoded-byte copy exactly once. A changed retained item or a present item during absence proof returns `Mismatch`; a capability from another adapter, a disposed/already-consumed capability, or adapter/native uncertainty returns `Unavailable`. No layer repeats or splits the durability sequence.

- [ ] GREEN (single compile-safe edit): add these exact internal, nonserializable, factory-only runtime contracts. The signatures below freeze the callable surface; implement every private constructor and factory body in the same edit:

  ```csharp
  internal sealed class CampaignPathFullInstallationResetInventory
  {
      private CampaignPathFullInstallationResetInventory(
          Guid ownerOperationId,
          ImmutableArray<CampaignMarkerInventoryEntryV1> entries,
          CovenantDigest inventoryDigest);

      internal Guid OwnerOperationId { get; }

      internal ImmutableArray<CampaignMarkerInventoryEntryV1> Entries { get; }

      internal CovenantDigest InventoryDigest { get; }

      internal static Result<CampaignPathFullInstallationResetInventory> Create(
          Guid ownerOperationId,
          ImmutableArray<CampaignMarkerInventoryEntryV1> entries,
          CovenantDigest inventoryDigest);
  }

  internal sealed class CampaignPathFullInstallationResetCleanupPreparation
  {
      private CampaignPathFullInstallationResetCleanupPreparation(
          Guid ownerOperationId,
          CovenantDigest ownerEffectDigest,
          CampaignPathFullInstallationResetInventory inventory);

      internal Guid OwnerOperationId { get; }

      internal CovenantDigest OwnerEffectDigest { get; }

      internal CampaignPathFullInstallationResetInventory Inventory { get; }

      internal static Result<CampaignPathFullInstallationResetCleanupPreparation> Create(
          Guid ownerOperationId,
          CovenantDigest ownerEffectDigest,
          CampaignPathFullInstallationResetInventory inventory);
  }

  internal sealed class CampaignPathFullInstallationResetCleanupReceipt
  {
      private CampaignPathFullInstallationResetCleanupReceipt(
          Guid ownerOperationId,
          CovenantDigest ownerEffectDigest,
          ImmutableArray<Guid> orderedMarkerIntentIds,
          CovenantDigest markerIntentVectorDigest,
          ulong deletedCount,
          ulong orphanCount);

      internal Guid OwnerOperationId { get; }

      internal CovenantDigest OwnerEffectDigest { get; }

      internal ulong MarkerIntentCount { get; }

      internal ImmutableArray<Guid> OrderedMarkerIntentIds { get; }

      internal CovenantDigest MarkerIntentVectorDigest { get; }

      internal ulong DeletedCount { get; }

      internal ulong OrphanCount { get; }

      internal static Result<CampaignPathFullInstallationResetCleanupReceipt> CreatePrepared(
          Guid ownerOperationId,
          CovenantDigest ownerEffectDigest,
          ImmutableArray<Guid> orderedMarkerIntentIds,
          CovenantDigest markerIntentVectorDigest);

      internal static Result<CampaignPathFullInstallationResetCleanupReceipt> CreateTerminal(
          Guid ownerOperationId,
          CovenantDigest ownerEffectDigest,
          ImmutableArray<Guid> orderedMarkerIntentIds,
          CovenantDigest markerIntentVectorDigest,
          ulong deletedCount,
          ulong orphanCount);
  }

  internal static class CampaignPathFullInstallationResetContractComparer
  {
      internal static bool InventoryEquals(
          CampaignPathFullInstallationResetInventory? left,
          CampaignPathFullInstallationResetInventory? right);

      internal static bool PreparationEquals(
          CampaignPathFullInstallationResetCleanupPreparation? left,
          CampaignPathFullInstallationResetCleanupPreparation? right);

      internal static bool ReceiptEquals(
          CampaignPathFullInstallationResetCleanupReceipt? left,
          CampaignPathFullInstallationResetCleanupReceipt? right);
  }
  ```

  Each factory rejects a zero operation, default/oversized arrays, invalid digests, aliased mutable input, and noncanonical recalculated vector digest; it deep-copies every entry/digest/ID before the private constructor. Preparation additionally rejects null inventory, owner mismatch, or invalid effect. `MarkerIntentCount` is derived with a checked conversion from the copied ID array, never accepted independently; IDs must be nonzero and distinct while their supplied Campaign order is preserved. `CreatePrepared` fixes both counts to zero. `CreateTerminal` requires checked `DeletedCount + OrphanCount == MarkerIntentCount`. Counts are nonnegative and bounded; a zero-Campaign prepared shape uses the frozen positive empty-vector digest and is already terminal without filesystem work. The comparer implements explicit field-by-field equality, sequence-compares immutable vectors in order, and compares nested entries/digests by value; it does not use `ImmutableArray.Equals`, reference identity, generated record equality, or `object.Equals`. Every coordinator, authority, lifecycle, store-replay, and test comparison of these runtime contracts uses this one helper. These classes never contain a display path, marker bytes, root, handle, delegate, codec result, or SQL/filesystem authority, and no JSON context registers them.

- [ ] GREEN (same compile-safe edit): add the coordinator-only Campaign lifecycle interface now so the phase suite can use a fake while later tasks implement the lifecycle:

  ```csharp
  internal partial interface ICampaignPathMarkerLifecycle
  {
      Task<Result<CampaignPathFullInstallationResetInventory>>
          InventoryFullInstallationResetCleanupAsync(
              Guid ownerOperationId,
              SqliteConnection liveCoreConnection,
              CancellationToken cancellationToken);

      Task<Result> RevalidateFullInstallationResetInventoryAsync(
          CampaignPathFullInstallationResetInventory inventory,
          SqliteConnection liveCoreConnection,
          CancellationToken cancellationToken);

      Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
          PrepareFullInstallationResetCleanupAsync(
              CampaignPathFullInstallationResetCleanupPreparation preparation,
              CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
              FullInstallationResetMarkerCleanupAuthority authority,
              SqliteConnection liveCoreConnection,
              SqliteTransaction liveCoreTransaction,
              CancellationToken cancellationToken);

      Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
          ReconcileFullInstallationResetCleanupAsync(
              CampaignPathFullInstallationResetCleanupReceipt prepared,
              FullInstallationResetMarkerCleanupAuthority authority,
              SqliteConnection liveCoreConnection,
              CancellationToken cancellationToken);
  }
  ```

  In the same edit, add an inert partial `CampaignPathMarkerLifecycle.FullInstallationReset.cs` implementation for all four new members, each returning an explicit content-free failure without SQL, registry, opener, codec, or filesystem access. Update `FakeCampaignPathMarkerLifecycle` in `BackupRestoreStartupRecoveryTests` and `RecordingRestoreMarkerLifecycle` in `CovenantRestoreStagingTests` with the same compile-safe inert members before compiling. Runtime inventory/observation types remain internal and nonserializable. Inventory and revalidation are read-only, borrow the already opened core connection, and are callable only by the coordinator in production. Revalidation rereads the exact ordered registry vector and rechecks every retained same-handle observation without reopening a root; it succeeds only if it reproduces `Entries` and `InventoryDigest`. These skeletons exist only so Task 5's coordinator microcycles and the whole test assembly compile; Tasks 6, 7, and 9 replace the inventory, revalidation, preparation, and reconciliation arms one at a time.

- [ ] RED: add authority tests:

  ```text
  Pair_absence_verified_mints_one_operation_revision_effect_inventory_and_lock_bound_proof
  Cleanup_authority_cannot_be_constructed_from_a_digest_path_lock_or_public_attestation
  Cleanup_authority_revalidates_envelope_anchor_phase_and_exact_lock_before_every_use
  Preparation_authority_rejects_owner_effect_or_inventory_input_substitution
  Reconciliation_authority_rejects_owner_effect_intent_vector_or_count_input_substitution
  Cleanup_authority_rejects_released_wrong_or_stale_lock_and_changed_revision
  Prepared_receipt_publication_invalidates_old_authority_and_fresh_revision_authority_succeeds
  Terminal_receipt_publication_invalidates_the_last_reconciliation_authority
  Cleanup_authority_is_nonserializable_and_absent_from_all_json_contexts
  Coordinator_is_the_only_production_caller_of_full_reset_inventory
  Coordinator_does_not_resolve_campaign_codec_opener_marker_store_or_filesystem_primitives
  ```

- [ ] GREEN: nest private `AuthenticatedFullInstallationResetJournalProof` and internal sealed `FullInstallationResetMarkerCleanupAuthority` inside the sealed coordinator. The proof binds the exact held lock reference, operation, envelope revision/digest, restart proof, pair-absence phase, owner effect, immutable Campaign inventory, and the current receipt shape (null or exact prepared vector/counts). Keep both constructors private; only a private coordinator minting method may construct the authority from the proof, so no assembly-wide factory exists. The lifecycle contract refers to the nested authority type (a local `using` alias is permitted for readability). The authority exposes exactly these two members and no generic/no-input overload:

  ```csharp
  internal Task<Result> RevalidatePreparationAsync(
      CampaignPathFullInstallationResetCleanupPreparation preparation,
      CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
      CancellationToken cancellationToken);

  internal Task<Result> RevalidateReceiptAsync(
      CampaignPathFullInstallationResetCleanupReceipt receipt,
      CancellationToken cancellationToken);
  ```

  Creation and every call delegate to the coordinator-owned revalidator for the bound proof, exact held-lock reference/state, active envelope revision/digest, anchor, phase, owner effect, and inventory. The preparation member additionally uses `CampaignPathFullInstallationResetContractComparer.PreparationEquals` for the supplied owner/effect/full inventory and requires `expectedReceipt` to match the proof's exact null-or-nonnull receipt shape through `ReceiptEquals`; it is null for first preparation and child-commit-before-receipt replay, and nonnull for receipt-present replay/rehydration. The receipt member uses `ReceiptEquals` against the proof's exact nonnull checkpoint receipt. Lifecycle preparation/replay calls the first before every read/write; receipt-present replay may additionally call the second, and reconciliation calls the second before its vector read, each child, each short child transaction, and each effect. The authority exposes no bound values, path, handle, SQL authorization, or generic lease. Treat any successful `AdvanceAsync` as immediate revocation: discard the proof/authority, reread the new envelope and anchor, and remint before another Campaign lifecycle call.

- [ ] GREEN: before every marker effect or proof mint, call `activeStore.RecoverAsync`, require the supplied publication equals the current authenticated envelope/anchor by value, recompute closed proof state, and then call `AdvanceAsync` with only one permitted change. Treat `Deleted`, `Mismatch`, and `Unavailable` as distinct; never infer exact absence from a generic read error. Encode the exact recovery matrix from the spec: only clean-database/original-OS at `PairJournaled` and clean-database/exact-OS-absence at `DatabaseMarkerCompareDeleted` may close the immediately preceding effect/publication gap. Before advancing, the former reruns checked WAL durability plus clean reread and the latter reruns platform durability/synchronization plus a second exact absence readback. Barrier failure preserves the prior phase. Every other absence/order combination blocks.

- [ ] Run pair, authority, and call-site tests green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~FullInstallationResetCleanupAuthorityTests|FullyQualifiedName~HostToolsMarkerPairResetCallSiteTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 6: Inventory every Campaign through retained no-follow root authority before pair effects

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathFullInstallationResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.MarkerOwnershipProof.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestartRootProof.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerRootAuthority.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/TheForge/PhysicalCampaignRootOpener.MarkerCapabilities.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/FileHandleIdentity.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/SecureFilePermissions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/CampaignRootIdentityKeyProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathMarkerLifecycleTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetInventoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathMarkerRootProofCallSiteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/CampaignRootIdentityKeyProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/FileHandleIdentityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/SecureFilePermissionsTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/TheForge/PhysicalCampaignRootOpenerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/TheForge/CampaignPathMarkerRootAuthorityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`

- [ ] RED: add inventory tests:

  ```text
  Initial_full_reset_inventory_reads_the_complete_registry_from_the_borrowed_core_connection
  Initial_inventory_orders_by_rfc4122_campaign_bytes_not_sqlite_or_guid_runtime_order
  Initial_inventory_opens_each_registered_root_once_without_following_links
  Initial_inventory_authenticates_exact_marker_bytes_and_same_handle_root_ownership
  Initial_inventory_refuses_before_journal_when_any_root_marker_or_ownership_is_unavailable
  Initial_inventory_refuses_physical_identity_marker_campaign_revision_or_root_binding_mismatch
  Initial_inventory_refuses_registry_change_before_pair_journal_publication
  Initial_inventory_retains_only_runtime_root_authority_and_returns_detached_digest_evidence
  Inventory_revalidation_failure_releases_every_attempt_owned_root_exactly_once
  Initial_inventory_rejects_default_duplicate_zero_revision_and_more_than_4096_entries
  Empty_initial_inventory_is_a_positive_authenticated_vector
  Nonempty_initial_inventory_with_missing_root_identity_key_makes_no_credential_write_root_open_codec_or_marker_call
  Empty_initial_inventory_never_reads_or_creates_the_root_identity_key
  Recovery_existing_key_read_returns_the_cached_or_stored_key_without_Set
  Recovery_missing_malformed_or_unavailable_key_never_calls_Set
  Recovery_not_found_does_not_negative_cache_or_block_later_ordinary_first_registration
  Ordinary_first_registration_still_creates_the_key_on_NotFound
  Existing_only_full_reset_open_does_not_create_a_missing_marker_directory
  Existing_marker_directory_refuses_unix_0755_wrong_owner_and_windows_permissive_or_inherited_acl_without_repair
  Marker_directory_posture_is_validated_from_the_exact_retained_handle_after_substitution
  Marker_directory_and_marker_file_open_relative_to_their_retained_parent_handles_without_following_links
  Copied_authentic_marker_in_a_same_volume_replacement_directory_is_never_adopted
  Begin_refuses_every_noncanonical_claim_only_field_before_any_downstream_call
  Prejournal_failure_after_caller_cancellation_propagates_the_exact_caller_cancellation
  Prejournal_release_failure_never_masks_the_primary_result_or_caller_cancellation
  Scoped_lifecycle_disposal_drains_both_retained_authority_maps_exactly_once
  Prejournal_digest_or_owner_effect_failure_releases_attempt_roots_exactly_once
  Prejournal_revalidation_or_publication_failure_releases_attempt_roots_exactly_once
  Prejournal_cancellation_releases_attempt_roots_and_propagates
  Successful_pair_journal_publication_and_later_failure_do_not_release_attempt_roots
  ```

- [ ] Add and run each inventory behavior as its own focused red/green cycle against an inert lifecycle arm:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathFullInstallationResetInventoryTests|FullyQualifiedName~CampaignRootIdentityKeyProviderTests"
  ```

- [ ] GREEN: add exact internal port `ICampaignRootIdentityRecoveryKeyProvider` with member `bool TryCopyExistingRootIdentityKey(Span<byte> destination)` beside `CampaignRootIdentityKeyProvider`. The destination must be exactly the established root-identity key width; success copies the existing key and populates the same provider cache later consumed by the codec/opener. Implement it through the provider's existing lock/cache but with a non-generating read mode: `NotFound`, malformed, unavailable, failed, or wrong destination returns false and never calls `Set` or mutates the cache. In particular, recovery `NotFound` is not negatively cached: a later ordinary `ICampaignRootIdentityKeyProvider.TryCopyRootIdentityKey` call on the same singleton still performs its established create-on-`NotFound` first-registration behavior. Register both interfaces to the same singleton concrete provider and pass the recovery port as an optional final constructor dependency to `CampaignPathMarkerLifecycle`, preserving every existing non-reset direct construction; a #122 method with no recovery port fails content-free. This preflight is mandatory before every #122 root open/reopen on a fresh process that lacks a retained root—not only initial inventory—including receipt-null preparation/replay and receipt-present rehydration added in Task 7.

- [ ] GREEN: implement `InventoryFullInstallationResetCleanupAsync` in the Campaign lifecycle. Query and validate the complete `campaign_path_identities` registry first. A zero-row registry returns the positive frozen empty inventory without touching the configured recovery-key port. A missing recovery-key dependency makes every #122 lifecycle method fail content-free, including the zero-row arm; legacy non-reset direct construction remains source-compatible. For a nonempty registry, require `TryCopyExistingRootIdentityKey` and zero its detached stack/local copy before opening anything; missing/unreadable/malformed key evidence fails before a credential write, root open, codec call, or marker call. Then parse/validate every Campaign/revision/display path/indexed digest, sort in RFC-4122 byte order, enforce the bound, and open each exact root through the same no-follow producer's new existing-only mode. That mode must retain the root and marker-directory handles while requiring the owner-only `.arcanum` directory to already exist; it never calls `Directory.CreateDirectory`, and ordinary registration keeps the existing create-capable mode. A pre-check followed by the create-capable open is forbidden because removal between the two would still mutate. Open `.arcanum` relative to the retained root handle and open the fixed marker leaf relative to the retained marker-directory handle (`openat` on Unix and a genuine `RootDirectory` native open on Windows), both no-follow and without a path-open fallback. Read and parse the owned marker through those same retained handles; require Campaign ID, positive revision, indexed root identity, marker digest, and root volume/file IDs to agree.

- [ ] GREEN: validate owner-only marker-directory posture from the exact retained directory handle before adoption and again during retained-authority revalidation. Unix requires the effective owner and exact `0700`; Windows requests `READ_CONTROL`, reads owner/DACL from the handle, requires a protected DACL, and permits allow ACEs only for the current user. Existing insecure directories are refused without repair. Ordinary creation uses the established owner-only creation primitive and is handle-verified before child activity. Add deterministic root-to-directory and directory-to-marker substitution seams only where needed for tests; copied authentic marker bytes in a same-volume replacement never become authority.

- [ ] GREEN: extract one `CampaignPathMarkerLifecycle.MarkerOwnershipProof` helper from the existing restart proof so restore and full reset derive same-handle evidence at one controlled call site. Keep independent digest domains. Update the source-inventory test to permit only the two explicit owners.

- [ ] GREEN: retain opened roots by `(ownerOperationId, CampaignId)` before child IDs exist. Return only `CampaignPathFullInstallationResetInventory`, whose detached entries and digest deep-copy all arrays and contain no display path, raw marker, root, handle, codec result, delegate, or filesystem authority. On any partial inventory failure, release every root acquired by that attempt exactly once.

- [ ] GREEN: implement `RevalidateFullInstallationResetInventoryAsync` and make coordinator `BeginAsync` call that exact lifecycle method immediately before `PairJournaled`. The lifecycle rereads the exact ordered registry vector and every observation through the already retained same handles, opens no root a second time, and must reproduce the detached entries and inventory digest exactly. The existing `Initial_inventory_refuses_registry_change_before_pair_journal_publication` RED is driven through this seam. Any changed row, count, ordering, marker, ownership evidence, or digest aborts before either pair effect; the coordinator never reads the Campaign registry directly. On partial inventory or lifecycle revalidation refusal, the lifecycle releases every `(ownerOperationId, CampaignId)` root retained by the attempt exactly once before returning. After successful inventory, the coordinator must also release the owner's retained roots on every digest/owner-effect/publication refusal, exception, or caller cancellation before `PairJournaled` is successfully published, and preserve caller-cancellation propagation. It must not release after successful `PairJournaled`, including a later post-journal pair failure, because Tasks 7 and 9 consume those frozen roots. The release contract is idempotent and cleanup must continue across disposal failures so one bad handle cannot leak the rest.

- [ ] GREEN: before any fresh-path collaborator call, require the current authenticated payload to pass the canonical payload validator and the exact claim-only shape: `All`, `Prepared`, pre-point-of-no-return, zero counters, empty credential results, null handoffs/completion/pair checkpoint, and `Data.RecoveryRequired`. Route every pre-`PairJournaled` refusal through one cancellation-aware content-free helper. Best-effort no-token root release may not replace the primary result or the exact caller-token cancellation. After successful `PairJournaled`, keep using the frozen recovery path and stop consulting the caller token as already designed.

- [ ] GREEN: make scoped `CampaignPathMarkerLifecycle` implement `IAsyncDisposable`. Disposal atomically prevents new retention, drains both retained-root maps, and continues after an individual authority disposal failure. This is scope ownership, not an early release: successfully journaled handles remain live for the duration of the recovery scope and close only when that scope ends.

- [ ] Run the new class, existing marker lifecycle suite, and coordinator pre-effect ordering tests green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathFullInstallationResetInventoryTests|FullyQualifiedName~CampaignRootIdentityKeyProviderTests|FullyQualifiedName~CampaignPathMarkerLifecycleTests|FullyQualifiedName~PhysicalCampaignRootOpenerTests|FullyQualifiedName~CampaignPathMarkerRootAuthorityTests|FullyQualifiedName~FileHandleIdentityTests|FullyQualifiedName~SecureFilePermissionsTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 7: Persist exact kind-four Campaign children and companion observation evidence

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_marker_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_full_reset_cleanup_evidence.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_delete.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_marker_intents_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathFullResetCleanupEvidenceStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathMarkerIntentStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestartRootProof.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathFullInstallationResetContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetCampaignSchemaReadiness.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantRetainedEvidence.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupSchemaTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCampaignSchemaReadinessTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`

- [ ] RED: add companion schema tests:

  ```text
  Full_reset_companion_is_one_to_one_with_a_kind_four_parent_and_cascades_with_it
  Full_reset_companion_rejects_non_kind_four_parent_or_missing_parent
  Full_reset_companion_requires_every_32_byte_inventory_digest
  Full_reset_companion_accepts_only_opened_1_unavailable_2_or_mismatch_3
  Opened_requires_equal_opened_and_authenticated_ownership_digests
  Blocked_observations_require_no_opened_ownership_digest
  Full_reset_companion_evidence_is_immutable_after_insert
  Full_reset_companion_rejects_direct_delete_but_allows_parent_driven_cascade
  Kind_four_parent_requires_null_gate_apply_request_payload_temp_and_pending_disposition
  Kind_four_parent_allows_a_null_path_hint_only_for_blocked_full_reset_cleanup
  Kind_four_parent_rejects_target_path_change_in_both_null_to_value_and_value_to_null_directions
  Kinds_one_through_three_still_require_a_nonnull_target_display_path
  Kind_four_parent_advances_only_prepared_to_completed_or_manual_blocker
  Kind_four_completed_and_manual_blocker_rows_are_terminal
  Covenant_family_erasure_retains_kind_four_parent_and_companion_evidence
  ```

- [ ] Add and run each schema invariant as its own red/green microcycle:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CampaignPathFullInstallationResetCleanupSchemaTests
  ```

- [ ] GREEN: add one canonical table whose columns are:

  ```text
  IntentId TEXT PRIMARY KEY REFERENCES campaign_path_marker_intents(IntentId) ON DELETE CASCADE
  CampaignInventoryEntryDigest BLOB NOT NULL length 32
  IndexedPhysicalIdentityDigest BLOB NOT NULL length 32
  CanonicalDisplayPathDigest BLOB NOT NULL length 32
  SameHandleOwnershipEvidenceDigest BLOB NOT NULL length 32
  ObservationCode INTEGER NOT NULL in (1, 2, 3)
  OpenedSameHandleOwnershipEvidenceDigest BLOB NULL length 32
  ObservationDigest BLOB NOT NULL length 32
  ```

  The table `CHECK` enforces the opened/blocked nullable shape. The insert trigger requires connection-local Campaign intent mutation authorization plus a parent with `IntentKindCode = 4`; it also requires an `Opened` parent to carry a nonnull canonical path hint and both blocked codes to carry null. The update trigger always aborts because evidence is immutable. Add a `BEFORE DELETE` trigger that aborts whenever the corresponding parent still exists; a direct child delete therefore fails, while SQLite's `ON DELETE CASCADE` runs only after the parent row is gone and is permitted. Expose no companion delete method. Do not add a migration or schema version bump.

- [ ] GREEN: update the canonical parent table and row contract so `TargetDisplayPath` is nullable only for kind four; kinds one through three retain their nonnull 1–4,096-character requirement. Add kind-four checks requiring positive prior revision and null `ExclusiveOwnerOperationCode`, `EncryptedMarkerPayload`, `ApplyRequestDigest`, `TemporaryBaseName`, `TemporaryPhysicalIdentityDigest`, `TargetObservationCode`, `ReopenedTargetPhysicalIdentityDigest`, and `PendingDispositionCode`, with phase limited to `Prepared`, `Completed`, or `ManualBlocker`. Make the store row's path `string?` and prove all existing kind-one-through-three call sites still supply nonnull values. In `CampaignPathMarkerLifecycle.RestartRootProof`, reject a null path before calling the legacy-kind `IdentifyExact`/`OpenAsync` seams, capture the proven nonnull value once, and use only that narrowed local so nullable analysis emits no warnings and a malformed legacy row cannot reach filesystem authority.

- [ ] GREEN: change the parent update guard's target-path immutability comparison from nullable-unsafe `<>` to `IS NOT`, and prove both null-to-value and value-to-null attempts abort. Then extend the guard only for kind four: its sole legal transition is `Prepared -> Completed` or `Prepared -> ManualBlocker`, with phase revision advancing exactly once; either destination is terminal for kind four. Preserve every existing kind-one-through-three transition and terminal rule byte-for-byte.

- [ ] RED: now that the intended #122 catalog is the source manifest, add reset-schema readiness and cross-version tests:

  ```text
  Exact_issue_122_core_manifest_is_accepted_without_ddl_or_metadata_mutation
  Exact_issue_121_predecessor_manifest_is_rejected_without_ddl_or_marker_effect
  Missing_companion_or_changed_parent_or_trigger_is_rejected_without_ddl_or_marker_effect
  Manifest_catalog_read_uncertainty_is_not_treated_as_readiness
  Claim_only_v2_on_issue_121_schema_stays_claim_only_before_pair_journal_or_marker_effect
  Checkpoint_resume_with_drifted_issue_122_schema_preserves_the_checkpoint_before_pair_effect
  Coordinator_calls_schema_readiness_on_begin_and_resume_before_inventory_join_or_effect
  Existing_database_access_preparer_success_is_not_treated_as_schema_readiness
  ```

- [ ] Run every readiness behavior as an individual red/green microcycle:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~FullInstallationResetCampaignSchemaReadinessTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests"
  ```

- [ ] GREEN: implement `IFullInstallationResetCampaignSchemaReadiness` as a narrow read-only adapter over `GrimoireSchemaManifestInspector`. On the coordinator's already-opened, non-pooled core connection, inspect `GrimoireSchemaManifests.Core` with no transaction and succeed only for the complete exact current manifest containing the nullable kind-four parent, companion table, and all guards. Map every missing object, definition/index drift, unexpected object, catalog failure, exact #121 source shape, or other invalid inspection to a content-free failure. Do not call `GrimoireSchemaInstaller`, execute DDL/DML/PRAGMA mutation, update `grimoire_feature_schemas`, or attempt a table rebuild. This repository has no numbered/in-place legacy migration path, so an older claim-only V2 database remains active and manually recoverable without losing either marker. Source/call-order tests prove the coordinator is the only production caller, invokes the port on both begin and resume before inventory, join, or any marker effect, and never treats the preparer's key-only success as schema readiness.

- [ ] RED: add kind-four preparation/store tests:

  ```text
  Preparation_requires_current_cleanup_authority_before_read_or_write
  Preparation_inserts_one_distinct_random_kind_four_child_per_authenticated_campaign
  Every_child_shares_owner_and_owner_effect_but_has_a_distinct_intent_id
  Preparation_records_opened_unavailable_and_mismatch_observation_shapes
  Preparation_borrows_and_never_begins_commits_rolls_back_or_disposes_the_callers_transaction
  Parent_and_companion_insert_atomically_in_the_same_caller_transaction
  Replay_returns_the_same_child_only_when_parent_and_every_companion_field_match
  Receipt_present_replay_rehydrates_only_exact_opened_children_without_inserting_or_updating
  Receipt_present_blocked_children_make_no_opener_codec_marker_store_or_filesystem_call
  Receipt_present_terminal_children_are_authenticated_and_skipped_with_zero_filesystem_calls
  Opened_rehydration_failure_preserves_immutable_evidence_for_manual_blocker_reconciliation
  Receipt_present_prepared_opened_rehydration_with_missing_root_identity_key_makes_no_credential_write_or_marker_effect
  Fresh_process_pair_journaled_without_receipt_and_missing_root_identity_key_makes_no_credential_write_root_open_codec_or_marker_effect
  Fresh_process_child_commit_without_receipt_and_missing_root_identity_key_makes_no_credential_write_root_open_codec_or_marker_effect
  Replay_rejects_same_count_replacement_reordering_changed_observation_or_digest_substitution
  Missing_registry_location_after_pair_journal_creates_a_null_path_unavailable_child
  First_preparation_reobserves_every_current_registry_entry_before_reusing_a_retained_root
  Changed_registry_location_after_pair_journal_creates_a_null_path_mismatch_child
  Joined_read_returns_exact_children_in_authenticated_campaign_order
  Zero_campaign_preparation_returns_the_frozen_nonzero_empty_receipt_without_lifecycle_dml_or_filesystem_effect
  No_campaign_marker_effect_occurs_before_the_caller_commits_and_publishes_the_receipt
  ```

- [ ] Add and run each preparation/store behavior as its own focused red/green cycle:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CampaignPathFullInstallationResetCleanupTests
  ```

- [ ] GREEN: add narrow `InsertOrReadFullInstallationResetCleanupAsync`, exact joined-read, and kind-four owner-count methods. On replay, compare intent ID/owner/Campaign/kind/null gate/effect/marker/nullable display/revision/phase plus every companion field; never return an existing ID after checking only uniqueness. Preserve hard-coded kind-three restore methods and their SQL unchanged.

- [ ] GREEN: implement exact idempotent preparation after pair absence. Call `authority.RevalidatePreparationAsync(preparation, expectedReceipt, ...)` before any read/write and compare the request entries/digests/effect with the proof. `expectedReceipt` is null only when the authenticated checkpoint receipt is null; it is the exact checkpoint receipt on receipt-present replay. Construct all `Opened`/`Unavailable`/`Mismatch` seeds before first insertion. On first preparation, reread every current registry entry and compare its path/revision/digests with the authenticated pre-effect inventory before considering any retained root. Only after that current observation agrees may the retained root supply deletion authority; otherwise perform the exact no-follow reopen needed by the closed observation arm. A missing registry location creates a blocked unavailable child and a changed location/evidence creates a blocked mismatch child, both with null path and neither with replacement authority. On replay, join and compare the complete existing vector and create or substitute nothing; a nonnull `expectedReceipt` must also reproduce its count, ordered IDs, vector digest, and counts before rehydration. Rehydrate only `Prepared` plus `Opened` children from their committed, digest-matching parent path hint through the exact restart proof. Before every #122 root open/reopen on a fresh process with no retained root—including receipt-null first preparation after a `PairJournaled` crash, receipt-null exact-child replay after child commit but before receipt publication, and receipt-present `Prepared + Opened` rehydration—require `TryCopyExistingRootIdentityKey` first. Missing, malformed, or unavailable key evidence fails before `Set`, root opener, codec, marker store, or filesystem access; reuse the populated cache for all later opens in that process. Prepared blocked companions, terminal `Completed`/`ManualBlocker` children, and an empty inventory require no key and are skipped with no filesystem collaborators. If a prepared opened root can no longer be rehydrated after the key preflight succeeds, retain a runtime blocked seed so reconciliation advances that immutable child to `ManualBlocker`.

- [ ] GREEN: return a structurally comparable `CampaignPathFullInstallationResetCleanupReceipt` containing owner operation, owner effect, ordered intent IDs, checked count, frozen intent-vector digest, and zero initial deleted/orphan counts. Intent IDs remain aligned with authenticated Campaign order and are not sorted by random UUID.

  For a zero-Campaign vector, the coordinator still supplies the required caller-owned immediate transaction and runs its commit/durability boundary, preserving one closed call shape. The lifecycle executes no intent/companion DML and no filesystem collaborator; “zero effect” does not mean the transaction object is null or that the caller skips its durability protocol.

- [ ] GREEN: in the coordinator, commit the caller-owned immediate transaction, run database durability/readback, and only then advance the active checkpoint from null receipt fields to the exact prepared receipt. No Campaign marker effect may precede authenticated receipt publication.

- [ ] Run schema, preparation, retained-evidence, active-store, and coordinator ordering tests green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathFullInstallationResetCleanupSchemaTests|FullyQualifiedName~CampaignPathFullInstallationResetCleanupTests|FullyQualifiedName~FullInstallationResetCampaignSchemaReadinessTests|FullyQualifiedName~InstallationResetActiveStoreTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 8: Add a retained native OS-marker capability and shared mutation gate

**Files:**

- Create: `src/RetroDownfall.Arcanum.Secrets/Security/HostProcessToolsMarkerCredentialCapability.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostToolsMutationAdmission.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/OsCredentialStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/InMemoryOsCredentialStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/MacOsCredentialStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/LinuxOsCredentialStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/WindowsOsCredentialStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerMutationGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsPorts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsTransitionService.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerCredentialCapabilityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsTransitionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsStartupGateTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerNativeCapabilityContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/MacOsCredentialStoreHandleOwnershipTests.cs`

- [ ] RED: add deterministic capability and race tests:

  ```text
  Capability_opens_only_the_fixed_arcanum_host_tools_slot
  Opened_capability_captures_exact_encoded_bytes_and_fresh_native_record_identity
  Replacement_before_final_revalidation_survives_compare_delete
  Byte_identical_live_replacement_is_not_deleted_by_the_stale_native_capability
  Restart_reopen_accepts_byte_identical_same_slot_evidence_with_a_fresh_native_identity
  Restart_reopen_rejects_absent_changed_malformed_or_unidentifiable_marker
  Compare_delete_revalidates_native_identity_bytes_and_decoded_fields_at_the_delete_boundary
  Compare_delete_reports_only_deleted_mismatch_or_unavailable
  Open_and_absence_result_factories_enforce_their_exact_nullable_shapes
  Definite_present_invalid_or_malformed_fixed_slot_maps_to_infrastructure_mismatch
  Capability_copies_only_bounded_exact_encoded_utf8_and_zeroes_it_on_dispose
  Capability_compare_delete_is_single_use_and_disposed_or_consumed_use_is_unavailable
  Delete_runs_platform_durability_readback_and_proves_the_fixed_slot_absent
  Recovery_absence_proof_reruns_platform_durability_and_a_second_fixed_slot_readback
  Recovery_absence_durability_or_second_readback_failure_is_unavailable
  Recovery_absence_proof_holds_the_shared_mutation_gate_through_the_second_readback
  Marker_transition_and_reset_compare_delete_share_one_process_mutation_gate
  Marker_transition_authenticates_no_active_reset_after_lock_and_before_either_marker_read
  Marker_transition_refuses_active_legacy_malformed_or_unreadable_reset_evidence_without_marker_access
  Macos_native_capability_retains_rereads_and_deletes_the_exact_SecKeychainItemRef
  Macos_native_capability_releases_the_retained_item_ref_on_every_terminal_path
  Linux_native_capability_retains_one_SecretItem_and_never_requeries_or_clears_by_attributes
  Linux_native_capability_rereads_the_same_SecretItem_before_targeted_delete
  Windows_native_capability_snapshots_LastWritten_and_the_complete_credential_record
  Windows_native_capability_rereads_the_complete_record_immediately_before_CredDeleteW
  Ordinary_marker_store_interface_and_implementation_expose_no_compare_delete_surface
  ```

  The in-memory adapter must expose a supported replacement injection seam, not test-only reflection into private state. The six per-platform cases and the ordinary-store surface case are cross-platform contract/source-inventory tests over production source (or deterministic injected native backends if that extraction is smaller); they must fail if a backend falls back to service/account lookup-delete, attribute clear, a bytes-only comparison, or leaves `CompareDelete` on `IHostProcessToolsMarkerStore`/`HostProcessToolsMarkerStore`. Update the existing macOS handle-ownership inventory so a reference transferred into the capability is proven exact-once released rather than mistaken for an immediate leak.

- [ ] Add and run each capability/race behavior as its own focused red/green cycle against an inert fixed-slot adapter:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~HostProcessToolsMarkerCredentialCapabilityTests|FullyQualifiedName~HostProcessToolsMarkerNativeCapabilityContractTests|FullyQualifiedName~MacOsCredentialStoreHandleOwnershipTests|FullyQualifiedName~HostProcessToolsTransitionServiceTests|FullyQualifiedName~HostProcessToolsStartupGateTests"
  ```

- [ ] GREEN: add a Secrets-owned, host-tools-specific capability source without changing `IOsCredentialStore`. Implement this exact closed internal surface:

  ```csharp
  internal enum HostProcessToolsMarkerCredentialOpenStatus : byte
  {
      Opened = 1,
      Absent = 2,
      Unavailable = 3,
      PresentInvalid = 4,
  }

  internal enum HostProcessToolsMarkerCredentialDeleteStatus : byte
  {
      Deleted = 1,
      Mismatch = 2,
      Unavailable = 3,
  }

  internal enum HostProcessToolsMarkerCredentialAbsenceStatus : byte
  {
      Absent = 1,
      Present = 2,
      Unavailable = 3,
  }

  internal interface IHostProcessToolsMarkerNativeRecordCapability : IDisposable
  {
      HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
          ReadOnlySpan<byte> expectedEncodedSecretUtf8);
  }

  internal sealed class HostProcessToolsMarkerCredentialCapability : IDisposable
  {
      private HostProcessToolsMarkerCredentialCapability(
          byte[] ownedEncodedSecretUtf8,
          IHostProcessToolsMarkerNativeRecordCapability ownedNativeCapability);

      internal int EncodedSecretUtf8Length { get; }

      internal bool TryCopyEncodedSecretUtf8(
          Span<byte> destination,
          out int bytesWritten);

      internal HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
          ReadOnlySpan<byte> expectedEncodedSecretUtf8);

      internal static HostProcessToolsMarkerCredentialCapability CreateOwned(
          ReadOnlySpan<byte> encodedSecretUtf8,
          IHostProcessToolsMarkerNativeRecordCapability ownedNativeCapability);

      public void Dispose();
  }

  internal sealed class HostProcessToolsMarkerCredentialOpenResult
  {
      private HostProcessToolsMarkerCredentialOpenResult(
          HostProcessToolsMarkerCredentialOpenStatus status,
          HostProcessToolsMarkerCredentialCapability? capability);

      internal HostProcessToolsMarkerCredentialOpenStatus Status { get; }

      internal HostProcessToolsMarkerCredentialCapability? Capability { get; }

      internal static HostProcessToolsMarkerCredentialOpenResult Opened(
          HostProcessToolsMarkerCredentialCapability capability);

      internal static HostProcessToolsMarkerCredentialOpenResult Absent();

      internal static HostProcessToolsMarkerCredentialOpenResult Unavailable();

      internal static HostProcessToolsMarkerCredentialOpenResult PresentInvalid();
  }

  internal sealed class HostProcessToolsMarkerCredentialAbsenceResult
  {
      private HostProcessToolsMarkerCredentialAbsenceResult(
          HostProcessToolsMarkerCredentialAbsenceStatus status);

      internal HostProcessToolsMarkerCredentialAbsenceStatus Status { get; }

      internal static HostProcessToolsMarkerCredentialAbsenceResult Absent();

      internal static HostProcessToolsMarkerCredentialAbsenceResult Present();

      internal static HostProcessToolsMarkerCredentialAbsenceResult Unavailable();
  }

  internal interface IHostProcessToolsMarkerCredentialCapabilitySource
  {
      HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot();

      HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent();
  }
  ```

  Implement all private constructors/factory bodies in the same edit. Secrets remains isolated and does not reference or duplicate Core's marker codec. `CreateOwned` accepts any nonempty fixed-slot value whose exact UTF-8 bytes fit the pinned `MaxEncodedSecretUtf8Bytes = 4096` resource bound, copies it into its own byte array without interpreting Base64/payload semantics, and takes exact ownership of a nonnull host-tools-specific native record capability; a source-inventory test permits calls only from the four Secrets backends. Empty, over-bound, or non-round-trippable definite present data returns `PresentInvalid`, not absent/unavailable. Infrastructure maps that status directly to `Mismatch`; for an `Opened` capability it alone performs strict Base64 and `HostProcessToolsMarkerPayload` decoding and also maps malformed/noncanonical content to `Mismatch` before database access. The capability exposes no string or backing buffer: Infrastructure allocates one bounded buffer from `EncodedSecretUtf8Length`, calls `TryCopyEncodedSecretUtf8`, and zeroes its copy after decode/use. Copy fails with `bytesWritten = 0` for a short destination or disposed/consumed capability. `CompareDeleteExact` may be called exactly once; it constant-time compares the caller's expected encoded bytes, delegates the complete native retained-item delete/durability/readback operation, consumes the capability for all three closed outcomes, and returns `Unavailable` on disposed/repeated use. `Dispose` is idempotent, zeroes the owned byte array, and releases the native capability exactly once.

  The open result's factories are its sole construction surface: only `Opened` carries a nonnull capability and transfers its disposal obligation to the source caller; `Absent`/`Unavailable`/`PresentInvalid` carry null. The absence result is factory-only and carries exactly one closed status, with no secret or capability. `ProveFixedSlotDurablyAbsent` synchronously owns the complete first fixed-slot read, platform durability/synchronization barrier, and second fixed-slot read; it returns `Absent` only for two exact not-found observations, `Present` if either read observes an item (including invalid data), and `Unavailable` for any barrier/read ambiguity. Neither operation accepts arbitrary service/account names.

- [ ] GREEN: implement platform identity correctly:

  - macOS retains and releases the exact `SecKeychainItemRef`, rereads content from that item, deletes that item reference, and proves the fixed query absent;
  - Linux retains a stable Secret Service item identity/object rather than repeating a password lookup and clearing by attributes;
  - Windows captures the complete credential identity including `LastWritten`, rereads and constant-time compares the complete record immediately before targeted delete;
  - in-memory uses a monotonic record identity so a byte-identical replacement is distinguishable live.

  Ambiguous platform errors are `Unavailable`. `NotFound` never mints a capability and counts as exact absence only when the complete `ProveFixedSlotDurablyAbsent` operation succeeds and the coordinator's authenticated legal phase/sibling-state crash matrix permits absence; every other use blocks.

- [ ] GREEN: add the singleton mutation gate and authenticated transition admission with these exact closed surfaces:

  ```csharp
  internal sealed class HostProcessToolsMarkerMutationGate
  {
      internal ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
          CancellationToken cancellationToken = default);
  }

  internal enum HostProcessToolsResetMutationAdmissionOutcome : byte
  {
      NoActiveRecord = 1,
      ActiveOrLegacyRecord = 2,
      Unverifiable = 3,
  }

  internal interface IHostProcessToolsResetMutationAdmission
  {
      Task<Result<HostProcessToolsResetMutationAdmissionOutcome>> InspectAsync(
          CancellationToken cancellationToken = default);
  }
  ```

  The gate owns one `SemaphoreSlim`; acquisition honors cancellation before ownership, returns an idempotent async-disposable lease, and releases exactly once. Remove `CompareDelete` from `IHostProcessToolsMarkerStore` and the raceful implementation. Every Arcanum host-tools transition retains the existing cross-process installation lock, calls admission after that acquisition and before either marker read, proceeds only for exact successful `NoActiveRecord`, then holds this exact shared process gate across its first marker access, write/delete boundary, and readback. Active/legacy, unverifiable, Result failure, or an unknown enum refuses without authority-row or marker access. Inject the shared instances in the single constructor edit below rather than creating a per-service gate or admission.

- [ ] GREEN (single compile-safe constructor edit): implement `InstallationResetHostToolsMutationAdmission` over authenticated `IInstallationResetActiveStore.InspectAsync` with the exact mapping above. Inject both that admission and the exact shared `HostProcessToolsMarkerMutationGate` into `HostProcessToolsTransitionService`. In the same edit, update the constructor harness in `HostProcessToolsTransitionServiceTests` and `HostProcessToolsStartupGateTests.Harness.Taint` to pass the same gate instance plus an inert admission fake implementing the exact `InspectAsync` signature and returning `Result<HostProcessToolsResetMutationAdmissionOutcome>.Success(NoActiveRecord)` by default; recording outcomes forward cancellation and call count. Do not let either harness or production composition allocate a second gate. The transition service calls admission only after acquiring its installation lock and before its first authority-row or OS-marker read; it proceeds only for exact `NoActiveRecord`, acquires the gate, and keeps it through all later marker accesses. Authenticated active V2 maps `ActiveOrLegacyRecord`; legacy V1 also maps there; tamper, ambiguity, unknown state, or read failure maps `Unverifiable` or a content-free failure. This admission is read-only, is not a coordinator authority, exposes no active payload, and has only the transition service as a production caller.

- [ ] GREEN: keep the existing ordinary `HostProcessToolsMarkerStore(IOsCredentialStore)` read/write constructor unchanged and remove only its raceful compare-delete member. Beside it in the same source file, add a separate internal reset adapter implementing the new narrow Infrastructure reset port from the host-tools-specific capability source plus the shared mutation gate. The reset adapter opens/reopens the capability, decodes exact bytes into `HostProcessToolsOsMarkerEvidence`, compares every field and digest, and exposes exact absence proof; no ordinary store caller or retained-evidence test gains reset authority or needs a new dependency. Every recovery absence proof acquires the shared process mutation gate and keeps it held across the single Secrets-owned `ProveFixedSlotDurablyAbsent` call, whose implementation owns the first fixed-slot query, platform durability/synchronization operation, and second fixed-slot readback; `Present` maps to `Mismatch` and any failure maps to `Unavailable`, never absence. Slot identity remains the fixed service/account digest; the native record identity stays only in the live capability.

- [ ] Run the full marker/transition suite green and verify the Secrets project compiles Native AOT-safe with no warnings:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~HostProcessToolsMarkerCredentialCapabilityTests|FullyQualifiedName~HostProcessToolsMarkerNativeCapabilityContractTests|FullyQualifiedName~MacOsCredentialStoreHandleOwnershipTests|FullyQualifiedName~HostProcessToolsMarkerStoreTests|FullyQualifiedName~HostProcessToolsTransitionServiceTests|FullyQualifiedName~HostProcessToolsStartupGateTests"
  dotnet build src/RetroDownfall.Arcanum.Secrets/RetroDownfall.Arcanum.Secrets.csproj --no-incremental --consoleloggerparameters:Summary
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 9: Reconcile Campaign children, publish the terminal receipt, and release retained roots

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathFullInstallationResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathFullResetCleanupEvidenceStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathMarkerIntentStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCleanupAuthorityTests.cs`

- [ ] RED: add reconciliation tests:

  ```text
  Reconciliation_requires_current_cleanup_authority_before_each_child_read_or_effect
  Reconciliation_rereads_and_compares_every_parent_and_companion_field_not_only_count
  Reconciliation_rejects_reordered_replaced_extra_or_same_cardinality_child_vectors
  Opened_child_uses_only_its_retained_no_follow_root_and_exact_marker_codec_evidence
  Opened_child_compare_deletes_exact_bytes_flushes_parent_and_advances_to_completed
  Changed_marker_or_opened_ownership_is_preserved_and_advances_to_manual_blocker
  Unavailable_child_advances_to_manual_blocker_without_marker_opener_codec_or_filesystem_call
  Mismatch_child_advances_to_manual_blocker_without_marker_opener_codec_or_filesystem_call
  Post_pair_deletion_root_loss_becomes_a_content_free_manual_blocker
  Parent_durability_failure_becomes_a_manual_blocker_and_never_reports_deletion
  Kind_four_never_uses_gate_disposition_reopen_finalizer_compensation_or_orphan_phase
  Reconciliation_is_idempotent_for_completed_and_manual_blocker_children
  Terminal_receipt_counts_completed_as_deleted_and_manual_blocker_as_orphan
  Retained_roots_release_exactly_once_only_after_terminal_receipt_publication
  Failure_cleanup_releases_every_attempt_owned_root_exactly_once
  ```

- [ ] Add and run each reconciliation behavior as its own focused red/green cycle:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CampaignPathFullInstallationResetCleanupTests
  ```

- [ ] GREEN: implement `ReconcileFullInstallationResetCleanupAsync` over the coordinator's same borrowed non-pooled core connection. Call `authority.RevalidateReceiptAsync(prepared, ...)` before the joined vector read, before each child, before each short child transaction, and before each effect; never dispose the borrowed connection. Compare intent ID, owner, Campaign, kind, owner effect, marker, revision, phase/revision, every companion digest, observation code, opened evidence, and observation digest against the authenticated preparation/checkpoint vector.

- [ ] GREEN: for `Opened`, use only the retained `CampaignPathMarkerRootAuthority` or the one exact restart reopen produced during preparation. Re-derive same-handle ownership, parse via the existing codec, compare-delete exact bytes, and flush/read back parent durability. Advance directly from `Prepared` to `Completed` only after the complete proof. Any changed evidence, lost root after a possible effect, or durability uncertainty advances to `ManualBlocker` with content-free evidence and preserves whatever marker still survives.

- [ ] GREEN: for `Unavailable` and `Mismatch`, make no marker-store, root-opener, codec, compare-delete, or filesystem call during reconciliation. Revalidate the companion outcome and advance directly to `ManualBlocker`. Never use `TargetReopenedOrAbsent`, Covenant exclusive disposition, a gate finalizer, `OrphanReopenPending`, `Orphaned`, or compensation for kind four.

- [ ] RED: add coordinator Campaign-handoff/crash tests:

  ```text
  Campaign_preparation_is_not_called_before_pair_absence_verified
  Pair_absence_verified_mints_authority_then_commits_children_before_receipt_publication
  Prepared_receipt_advance_rejects_the_old_authority_before_any_reconciliation_read
  Coordinator_reauthenticates_and_remints_authority_after_prepared_receipt_publication
  No_campaign_filesystem_effect_occurs_before_prepared_receipt_is_authenticated
  Crash_at_pair_journaled_on_a_fresh_process_with_missing_root_identity_key_stops_before_prepare_root_open
  Crash_after_child_commit_before_receipt_replays_the_exact_same_children
  Crash_after_child_commit_before_receipt_on_a_fresh_process_with_missing_root_identity_key_stops_before_replay_root_open
  Crash_after_receipt_before_first_child_replays_prepare_to_rehydrate_without_creating_or_substituting_children
  Receipt_present_replay_uses_a_fresh_caller_owned_immediate_transaction_and_durability_closes_before_reconciliation
  Replay_transaction_or_durability_failure_preserves_the_prepared_receipt_and_performs_no_marker_effect
  Crash_after_each_child_phase_authenticates_terminal_children_without_rehydration_and_reconciles_only_remaining_prepared_children
  Changed_campaign_inventory_before_effects_blocks_without_digest_substitution
  Shared_owner_effect_produces_distinct_children_for_every_campaign
  Terminal_receipt_publishes_exact_vector_deleted_and_orphan_counts
  Crash_after_terminal_receipt_publication_authenticates_children_releases_roots_and_returns_without_advance_or_filesystem
  Campaign_failure_leaves_pair_absence_and_the_last_authenticated_receipt_active
  Terminal_campaign_cleanup_remains_recovery_required_and_does_not_retire_rotate_or_complete
  ```

- [ ] GREEN: extend coordinator convergence after `PairAbsenceVerified`:

  1. reauthenticate the current envelope/anchor and mint a proof/authority bound to that exact revision;
  2. if receipt fields are null, begin an immediate transaction on the same attempt's core connection, call prepare-or-replay with `expectedReceipt: null`, commit/durability-readback, and publish the exact receipt with zero counts; on a fresh process, the lifecycle's non-generating root-key preflight runs before any root reopen whether rows are absent or already committed;
  3. discard the now-stale authority, reread/authenticate the prepared-receipt publication, and mint a fresh revision-bound proof/authority;
  4. when the receipt is present, begin a fresh caller-owned immediate transaction on the same live connection, call prepare-or-replay with that exact receipt as `expectedReceipt` so the authority validates it before the first row read, exact existing rows are compared, and only `Prepared` plus `Opened` runtime roots are rehydrated, then commit and run the checked durability/readback barrier before reconciliation; authenticate and skip terminal children with zero filesystem calls, create or substitute no child on this path, and on begin/call/commit/barrier failure preserve the prepared receipt and perform no marker effect;
  5. derive exact joined child state after replay; if every child is terminal and the authenticated publication's deleted/orphan counts already equal those rows, call neither reconciliation nor `AdvanceAsync`, authenticate the same publication, release retained roots idempotently, and return `Data.RecoveryRequired` with zero filesystem calls;
  6. otherwise call reconciliation with the fresh authority, derive terminal counts from exact joined child phases, and publish the fixed vector plus terminal counts exactly once;
  7. prove the reconciliation authority is stale after that publication, reread/authenticate the terminal publication, then release retained roots exactly once;
  8. return `Data.RecoveryRequired` without full-reset completion.

- [ ] Run Campaign, coordinator, authority, active-store, and crash matrix tests green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CampaignPathFullInstallationResetCleanupTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~FullInstallationResetCleanupAuthorityTests|FullyQualifiedName~InstallationResetActiveStoreTests"
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 10: Integrate service, startup, CLI resume, and dependency injection without fresh authority

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/InstallationResetContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingDatabaseAccessPreparer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/IInstallationResetStartupRecovery.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationStartupProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Program.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationStartupProbeTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingDatabaseAccessPreparerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetArgvPreflightTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CliGrimoireDiWiringTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantResetBootstrapBarrierTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/DependencyInjection/HostToolsMarkerPairResetCompositionTests.cs`

- [ ] RED: replace obsolete service admission-only expectations and add:

  ```text
  Full_locked_apply_publishes_the_claim_then_delegates_to_pair_coordinator
  Full_locked_apply_claim_only_retry_requires_the_exact_live_statement_and_pair
  Full_locked_apply_checkpoint_retry_bypasses_live_pair_and_resumes_from_authenticated_projection
  Claim_only_issue_121_schema_through_locked_apply_stays_claim_only_with_both_markers_untouched
  Full_locked_apply_rejects_operation_plan_claim_or_statement_substitution
  Full_locked_apply_preserves_replan_then_final_pair_change_detection_before_begin
  Full_locked_apply_never_completes_retires_or_rotates_after_terminal_campaign_cleanup
  Ordinary_global_and_all_apply_still_require_a_clean_pair
  Ordinary_workspace_reset_behavior_is_unchanged
  ```

- [ ] Add and run each service behavior as its own focused red/green cycle against the current claim-only seam:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~InstallationResetServiceTests
  ```

- [ ] GREEN (single compile-safe constructor edit): inject required `IHostToolsMarkerPairResetCoordinator` into `InstallationResetService` immediately after the four existing required service dependencies and before its optional tail. In the same edit, update `InstallationResetServiceTests.CreateService` to accept/pass an inert coordinator fake by default and update both direct constructions (`Service_construction_requires_a_host_process_tools_pair_reader` and the restarted-service case) to pass that fake explicitly; do not add a nullable production fallback. New work retains existing replan/request/live-pair/fresh-verifier/claim publication, then calls `BeginAsync`. An exact claim-only retry still requires the supplied statement and live pair. A checkpoint retry authenticates the current record first, requires exact operation/scope/plan/claim equality, and calls `ResumeAsync` without live admission. All coordinator outcomes remain active `RecoveryRequired` results.

- [ ] RED: add startup recovery tests:

  ```text
  Startup_resumes_authenticated_pair_checkpoint_under_the_exact_host_held_lock
  Startup_does_not_resume_claim_only_state_without_the_external_statement
  Startup_recovery_never_uses_current_time_or_a_fresh_live_pair_after_pair_journaled
  Startup_recovery_reauthenticates_after_coordinator_convergence_before_projection
  Checkpointed_full_reset_projects_no_ordinary_host_handoff_or_recovery_host_admission
  Startup_authenticates_the_active_checkpoint_before_preparing_existing_database_access
  Cold_start_database_access_preparation_initializes_sqlcipher_native_runtime_without_opening_sqlite
  Startup_derives_the_passphrase_without_sqlite_before_the_coordinator_observes_os_then_opens_sqlcipher
  Startup_claim_only_or_no_active_record_never_invokes_checkpoint_database_access_preparation
  Checkpointed_host_without_an_injected_preparer_fails_before_coordinator_resume_or_ordinary_bootstrap
  Checkpoint_database_access_preparation_refuses_a_missing_grimoire_without_creating_root_database_key_or_schema
  Checkpoint_database_access_preparation_refuses_legacy_pending_only_missing_or_malformed_kdf_evidence_without_sqlite_open
  Preparer_rejects_wrong_or_released_lock_and_database_outside_guarded_root_before_any_key_or_database_access
  Symlinked_or_reparse_database_or_kdf_sidecar_inside_the_root_is_refused_before_native_or_key_access
  Checkpoint_database_access_preparation_uses_a_non_generating_read_only_data_protection_provider
  Symlinked_key_ring_directory_key_file_or_grimoire_secret_is_refused_before_provider_creation
  Group_or_other_accessible_key_ring_or_secret_is_refused_without_chmod_or_other_mutation
  Missing_empty_or_malformed_key_ring_creates_no_directory_key_or_secret_file
  Missing_or_corrupt_grimoire_secret_creates_or_modifies_no_key_ring_or_secret_file
  Existing_key_ring_and_secret_are_byte_for_byte_unchanged_after_successful_passphrase_derivation
  Checkpoint_database_access_preparation_runs_no_schema_convergence_host_tools_gate_or_readiness
  Ordinary_restore_and_no_checkpoint_startup_keep_restore_topology_before_key_ordering
  Start_async_keeps_readiness_closed_after_pair_and_campaign_recovery
  Startup_tampered_checkpoint_stays_content_free_manual_recovery_required
  Singleton_startup_recovery_creates_one_scope_and_captures_no_scoped_reset_dependency
  ```

- [ ] GREEN (single compile-safe edit): first add the narrow `IInstallationResetExistingDatabaseAccessPreparer` port with exact member `Task<Result> PrepareAsync(ArcanumMaintenanceLock heldLock, string guardedRoot, string databasePath, CancellationToken cancellationToken)`. Keep `InstallationResetStartupRecovery` singleton without capturing the scoped active store, coordinator, database session owner, or Campaign lifecycle. Inject only `IServiceScopeFactory`, assert the exact lock, and create exactly one scope for the whole recovery attempt. Change the startup port's exact entry member to `Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(ArcanumMaintenanceLock heldInstallationLock, Func<CancellationToken, Task<Result>> prepareCheckpointDatabaseAccess, CancellationToken cancellationToken = default)`, supplied at entry by `GrimoireDatabaseHostedService`; in the same edit, change the production factory in `ServiceCollectionExtensions` to `new InstallationResetStartupRecovery(sp.GetRequiredService<IServiceScopeFactory>())`, while leaving its lifetime/composition assertions for the later DI step. Add a private nullable preparer field to the hosted service, append an optional final preparer parameter to each internal recovery-capable constructor (not the public constructor), and forward it through the internal chain. Update the hosted call site immediately to pass a delegate that uses the exact held lock/root/database path; when the field is null the delegate returns content-free `Data.RecoveryRequired`. The delegate is supplied at recovery entry but startup recovery invokes it only after authenticating a typed checkpoint, so ordinary direct test constructors remain source-compatible. Also update both existing test implementations—`CovenantResetBootstrapBarrierTests.RejectingStartupRecovery` and `GrimoireDatabaseBootstrapperTests.DelegateStartupRecovery`—preserving their current behavior while accepting that callback. Replace the two direct `new InstallationResetStartupRecovery(guardedRoot, store)` constructions in `GrimoireDatabaseBootstrapperTests` with scope-factory graphs that register the same test store and inert coordinator at the production scoped lifetimes; keep their existing assertions. Resolve `IInstallationResetActiveStore`, perform the initial authenticated `RecoverAsync`, and invoke the callback exactly once only for a typed pair checkpoint; only after it succeeds may the same scope resolve/call `IHostToolsMarkerPairResetCoordinator.ResumeAsync`. Recover/authenticate again and project before disposing the scope. Claim-only/no-active states never invoke the callback and remain externally attested/manual. Run both affected classes with the startup tests so the interface/constructor expansion never leaves the full test project uncompilable.

- [ ] GREEN: implement the singleton preparer behind that port in the new InstallationReset file. The hosted-service callback now passes the exact held lock, guarded root, and database path; leave offline CLI injection/forwarding to its later boundary microcycle. The preparer first validates the live exact lock, canonical root, and no-follow database plus derived committed-sidecar containment before any native runtime, key-ring, secret, or database access; a wrong/released lock, out-of-root path, symlink, or reparse point fails closed. It then initializes `SqliteNativeRuntime.Instance` without opening SQLite and requires the existing database plus committed modern KDF sidecar. Before unprotecting the dedicated secret, perform an owner-safe read-only preflight of every existing Data Protection ring directory/key file and the Grimoire secret path. Refuse symlinks/reparse points and owner/permission failures without repairing, chmodding, creating, or following them, then create a separate provider with application name `ArcanumCore`, the existing ring, and `DisableAutomaticKeyGeneration`; expose/reuse the one `Arcanum.Core.GrimoireEncryption` purpose through a narrow internal `DataProtectionSecretStore` helper rather than the normal auto-generating `ISecretStore`. Derive the passphrase through one extracted internal Bootstrapper helper and initialize `IGrimoireDbPassphraseSource`. A legacy database, pending-only/missing/malformed sidecar, unsafe/missing/empty/malformed/unusable ring, or unsafe/missing/corrupt secret is a content-free manual blocker. Snapshot tests prove every failure preserves path topology, file type/link identity, ownership/permission metadata, length, last-write time, and bytes and creates/modifies no root, database, sidecar, key directory/file, secret, or schema; access-time bookkeeping is excluded. Successful derivation likewise leaves ring/secret bytes and write metadata unchanged. This path never probes/rekeys SQLite, classifies host tools, inspects or converges schema, recovers ordinary restore topology, or publishes readiness. After it succeeds, the scoped coordinator still observes/retains the OS fixed slot before opening its one SQLCipher connection, then independently calls the exact read-only #122 schema-readiness port before inventory, join, or any effect. After scoped recovery, `GrimoireDatabaseHostedService` observes the still-active full reset and fails closed before ordinary `EnsureInitializedAsync`; ordinary startup ordering remains unchanged when no typed checkpoint exists.

- [ ] Run the existing-database access preparer tests green before continuing to the CLI boundary:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~InstallationResetExistingDatabaseAccessPreparerTests
  ```

- [ ] RED: add command/boundary tests proving persisted restart authority needs no file:

  ```text
  Active_claim_only_full_reset_still_requires_external_remediation_file
  Active_checkpointed_full_reset_rejects_a_new_statement_file_and_uses_persisted_resume
  Fresh_full_apply_probes_active_state_before_reading_then_uses_the_signed_file_operation
  Claim_only_full_retry_probes_before_reading_and_requires_the_matching_statement_file
  Checkpointed_full_retry_with_a_supplied_path_never_opens_the_file_and_uses_the_persisted_operation
  Program_defers_attestation_checkpoint_routing_to_the_configured_command_without_a_preconfiguration_probe
  Checkpointed_resume_passes_operation_plan_and_the_boundarys_exact_held_lock
  Fresh_and_checkpointed_full_boundaries_prepare_existing_database_access_after_lock_before_locked_service
  Database_access_preparation_failure_reaches_neither_locked_service_nor_marker_port
  Checkpointed_resume_never_calls_clean_pair_admission_or_online_data_handoff
  Checkpointed_resume_returns_generic_error_with_recovery_required_result_until_issue_123
  ```

- [ ] GREEN: append one backward-compatible optional flag to the local active projection:

  ```csharp
  public sealed record ActiveInstallationReset(
      InstallationResetScope Scope,
      string? WorkspaceRoot,
      string PlanId,
      Guid OperationId = default,
      InstallationResetPhase Phase = InstallationResetPhase.Prepared,
      InstallationResetDataHandoff? DataHandoff = null,
      bool OnlineDataCompletionDurable = false,
      InstallationResetHostHandoff? HostHandoff = null,
      bool RequiresExternalRemediationAttestation = false,
      bool HasAuthenticatedExternalRemediationCheckpoint = false);
  ```

  Projection makes the two remediation booleans mutually exclusive. Claim-only sets `RequiresExternalRemediationAttestation`; typed checkpoint sets `HasAuthenticatedExternalRemediationCheckpoint`.

- [ ] GREEN: make raw argv shape validation the first boundary, then have the configured `InstallationFactoryResetCommand` call `ReadActiveResetAsync` before any attestation-file read. Fresh full apply and claim-only retry read/decode the supplied statement only after that authenticated routing probe. When the mutually exclusive checkpoint flag is true, do not call the attestation reader even if a path was supplied; treat that path as rejected nonauthority input and continue through persisted `ResumeFullAsync` with the active operation ID. Replace the old command-order expectation accordingly. Keep `Program.RunBeforeConfigurationAsync` from performing a second preconfiguration probe when an attestation option is present, but update its stale “after decode” comment/test to state that routing is deferred to the configured command. No argv path value may be opened, logged, or echoed during checkpoint resume.

- [ ] GREEN (single compile-safe edit): add `Task<Result<InstallationResetResult>> ResumeFullAsync(Guid operationId, InstallationResetApplyRequest request, CancellationToken cancellationToken)` to `IInstallationResetApplyBoundary` and exactly `Task<Result<InstallationResetResult>> ResumeFullUnderMaintenanceLockAsync(Guid operationId, InstallationResetApplyRequest request, ArcanumMaintenanceLock heldInstallationLock, CancellationToken cancellationToken = default)` to `IInstallationResetLockedService`. Before compiling, update the two existing test implementers in that same edit: `InstallationFactoryResetCommandTests.RecordingApplyBoundary` records/forwards the resume call, and `InstallationResetApplyBoundaryTests.RecordingResetService` records/forwards the locked resume call with the exact argument order and lock instance. Add required `IInstallationResetExistingDatabaseAccessPreparer` parameters to both `InstallationResetApplyBoundary` constructors, forward it through the public-to-internal constructor chain, and update every direct construction in `InstallationResetApplyBoundaryTests` in the same edit with an inert or recording preparer as appropriate; do not hide a second preparer behind an optional fallback. The command selects resume only for the authenticated checkpoint flag, shuts the host down, and acquires the exact lock once. Both fresh `ApplyFullAsync` and checkpoint `ResumeFullAsync` then call that injected shared preparer with the exact lock/path before invoking the locked service; preparation failure touches neither marker nor service. Resume does not read an attestation file, call clean-pair admission, create a host handoff, or infer authority from the public flag; the locked service authenticates the checkpoint again.

- [ ] RED: add composition tests:

  ```text
  Reset_composition_registers_one_shared_host_tools_marker_mutation_gate
  Reset_composition_registers_one_authenticated_host_tools_reset_mutation_admission
  Reset_composition_registers_one_shared_existing_database_access_preparer
  Reset_composition_registers_one_read_only_full_reset_campaign_schema_readiness_port
  Reset_composition_resolves_one_narrow_database_owner_and_pair_coordinator_graph
  Cli_and_host_startup_resolve_the_same_coordinator_contract
  Host_recovery_core_resolves_coordinator_with_shared_joiner_lifecycle_trust_root_and_verifier
  Campaign_recovery_and_ordinary_key_ports_resolve_the_same_singleton_provider
  Shared_recovery_core_excludes_cli_only_daemon_mutation_and_offline_reset_dependencies
  Singleton_startup_recovery_does_not_capture_any_scoped_coordinator_dependency
  Coordinator_receives_campaign_lifecycle_but_not_codec_opener_store_or_filesystem_services
  Generic_os_credential_store_and_authority_store_gain_no_compare_delete_surface
  ```

- [ ] GREEN: extract an idempotent `AddArcanumInstallationResetRecoveryCore(ArcanumSettings settings)` registration in `ServiceCollectionExtensions` containing only dependencies safe and required in both processes: active store, remediation trust-root provider and fixed-time attestation verifier singletons, mutation gate, authenticated no-active-reset transition admission, shared existing-database-access preparer, the single read-only `IFullInstallationResetCampaignSchemaReadiness` adapter over the existing singleton manifest inspector, Secrets capability adapter, narrow OS/database reset ports, the coordinator consuming the already-shared pair joiner and Campaign lifecycle, and singleton startup recovery. Leave `HostProcessToolsMarkerPairJoiner`, `CampaignPathMarkerLifecycle`, and `GrimoireSchemaManifestInspector` ownership in their existing shared registrations; the reset core must neither move nor duplicate them because non-reset callers use them independently. Have both `AddArcanumInstallationReset(settingsSnapshot)` and `AddArcanumInfrastructure(IConfiguration configuration)` call the recovery core after deriving the same settings snapshot. Move the safe trust-root/verifier `TryAdd` registrations out of the full CLI-only layer into this core. Keep `InstallationResetDaemonMutation`, `IInstallationResetPreDataMutation`, offline cleanup, credential catalog, full `InstallationResetService`, and any `IDaemonManager`-dependent registrations only in the existing full CLI reset layer; do not import them into the API host. Fold/remove the overlapping host active-store/startup factories so there is one core graph. `ApiBootstrapper` already calls `AddArcanumInfrastructure(configuration)`, so it needs no parallel registration path. Update the production `GrimoireDatabaseHostedService` factory to pass the registered preparer explicitly into the already-added internal constructor seam. A typed checkpoint with a null field fails closed after authentication but before coordinator resume or ordinary bootstrap; it never falls back or creates a preparer. Every new checkpoint-ordering test passes a recording preparer explicitly, and `CovenantResetBootstrapBarrierTests`' direct recovery-host construction is checked in the same microcycle. Preserve current service lifetimes and avoid a second singleton gate, joiner, lifecycle, verifier, readiness adapter, or preparer in CLI or host registrations.

- [ ] Run service/startup/CLI/DI tests and a full solution build green:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationStartupProbeTests|FullyQualifiedName~InstallationResetExistingDatabaseAccessPreparerTests|FullyQualifiedName~FullInstallationResetCampaignSchemaReadinessTests|FullyQualifiedName~InstallationResetExistingGrimoireTests|FullyQualifiedName~InstallationResetHostProcessToolsPairReaderTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~InstallationFactoryResetArgvPreflightTests|FullyQualifiedName~InstallationResetApplyBoundaryTests|FullyQualifiedName~HostToolsMarkerPairResetCompositionTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~CliGrimoireDiWiringTests|FullyQualifiedName~CovenantResetBootstrapBarrierTests"
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 11: Update every owning document to the landed #122 boundary

**Files:**

- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Arcanum.DEBUGGING.Human.md`
- Modify: `docs/Arcanum.Design.Human.md`
- Modify: `docs/Arcanum.OATH.md`
- Modify: `docs/ArcanumOATH.Human.md`
- Modify only if a verified generated/public surface changed: `docs/Arcanum.CommandMap.json`
- Modify only if a verified config surface changed: `docs/Compendium.README.md`

- [ ] Re-read each owning section immediately before editing. Preserve historical statements in dated plans/specs/reviews; describe current behavior only in living docs.

- [ ] Update `README.md` architecture/reset status, local Grimoire reinstall, unified retention/deletion, and CLI quick-reference text. State that #122 now removes the exact attested marker pair and reconciles Campaign markers, that an exact #121 predecessor schema is refused before marker effects and follows the documented deliberate reinstall/repair policy rather than an in-place migration, and that #123 still owns terminal installation deletion/rotation.

- [ ] Update `docs/Arcanum.DESIGN.md` at schema installation, unified data lifecycle/full reset, Campaign binding, Campaign marker lifecycle/restart root proof, full-reset recovery boundary, and persistence summary. Document:

  - exact phase/digest/checkpoint contracts and V2 bounds;
  - expiration as an initial-admission limit rather than revocation of an already authenticated operation: fresh acceptance uses current time, while exact checkpoint recovery revalidates only at authenticated `AcceptedAtUtc` and adds no later wall-clock restriction;
  - OS-first retained native capability, active-reset writer exclusion, and same-user adversary boundary;
  - six-column raw SQLite CAS, durability proof, and the two exact effect/publication crash suffixes;
  - same-connection exact #122 Core-manifest readiness before `PairJournaled`/resume effects, including read-only refusal of an exact #121 predecessor;
  - initial Campaign inventory before effects;
  - nullable-path exact kind-four parent/companion schema and three observation arms;
  - idempotent receipt-present root rehydration without child substitution;
  - private proof/authority revision invalidation, reminting, and receipt ordering;
  - pre-bootstrap existing-key initialization through a non-generating, read-only Data Protection ring/secret path, restart without a fresh statement, and the continued #123 boundary.
  - recovery-only non-generating Campaign root-identity key acquisition before every fresh-process #122 root open, including receipt-null crash replay, receipt-present rehydration, zero-Campaign, and ordinary-registration distinctions.

- [ ] Update `docs/Arcanum.API.md` for `POST /api/data/factory-reset` and unified lifecycle. Clarify that checkpoint/proof/authority/runtime inventory are internal, not HTTP payloads, and that an attested full reset still returns recovery-required rather than completion after this slice.

- [ ] Update `docs/Arcanum.Command.Reference.md` for `arcanum serve` startup recovery and `arcanum data factory-reset`. Document claim-only `--external-remediation-attestation`, active-state routing before file open, persisted checkpoint resume that never opens a newly supplied path, zero ordinary host handoff, and #123's remaining work.

- [ ] Update `docs/Arcanum.DEBUGGING.Human.md` recipe 30 with exact #122 schema-readiness versus #121-predecessor refusal, inventory-before-effects, pair phase inspection, child/companion/receipt diagnostics, blocked no-filesystem arms, tamper symptoms, and every crash point. Keep errors content-free; diagnostic queries may name schema fields but never marker bytes or native handles.

- [ ] Update `docs/Arcanum.Design.Human.md` persistence/recovery and Campaign/tool sections with the human-readable authority story: exact schema substrate before journaling, journal evidence first, exact compare-delete, blocked evidence preservation, and why digest/path knowledge cannot delete.

- [ ] Update `docs/Arcanum.OATH.md` status/header, landed/open issue lists, core support records, crash-safe cross-resource work, reset/family erasure, delivery sequence, verification model, and source map. Mark #122 landed only after implementation tests pass; keep #123 open.

- [ ] Update `docs/ArcanumOATH.Human.md` reset/erasure, noninventable recovery authority, and current/next work sections.

- [ ] Verify no unsupported surface docs drifted and no placeholder language remains:

  ```bash
  git diff -- README.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.Design.Human.md docs/Arcanum.OATH.md docs/ArcanumOATH.Human.md
  RIPGREP_CONFIG_PATH= rg -n "issue #122.*(defer|future|not yet)|#122.*(defer|future|not yet)|TODO|TBD|FIXME|placeholder|implement later|NotImplementedException" README.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.Design.Human.md docs/Arcanum.OATH.md docs/ArcanumOATH.Human.md
  ```

- [ ] Run documentation/source-inventory tests and a build to catch stale maps or XML/source-generation errors:

  ```bash
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Documentation|FullyQualifiedName~Contract|FullyQualifiedName~CallSite|FullyQualifiedName~RetainedEvidence"
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  ```

- [ ] Record the no-commit checkpoint.

---

### Task 12: Independent review, full green matrix, one commit, push, issue closure, and branch cleanup

**Files:**

- Review: every file changed by Tasks 2–11
- Preserve: `.idea/.idea.RetroDownfall.Arcanum/.idea/.name`

- [ ] Invoke `superpowers:verification-before-completion` and run the complete repository-required matrix from a fresh working-tree state:

  ```bash
  git diff --check
  dotnet build RetroDownfall.Arcanum.slnx --no-incremental --consoleloggerparameters:Summary
  dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
  ./scripts/coverage.sh --threshold
  ./scripts/verify-aot-il-warnings.sh
  ```

  Read every command's final output. The build must report exactly `0 Warning(s)` and `0 Error(s)`; both test projects must report zero failures/skips beyond documented existing policy; coverage must meet every tiered gate; AOT verification must report no first-party IL/AOT warnings.

- [ ] After all gates, inspect the final diff and repository state:

  ```bash
  git status --short
  git diff --stat
  git diff --name-status
  git diff --check
  ```

  Confirm every changed file belongs to #122 or its required docs/tests, no build/coverage artifact is staged, and `.idea/.idea.RetroDownfall.Arcanum/.idea/.name` remains untracked and excluded.

- [ ] Invoke `superpowers:finishing-a-development-branch`. Because issue #122 requires direct work on `long-term-memory`, select the direct-integration outcome: no feature branch or merge commit is created. Reconfirm branch/upstream immediately before staging:

  ```bash
  git branch --show-current
  git rev-parse long-term-memory
  git rev-parse origin/long-term-memory
  ```

- [ ] Stage only the reviewed allowlisted #122 files explicitly. Inspect the index and run the staged whitespace gate:

  ```bash
  git add -- \
    README.md \
    docs/Arcanum.DESIGN.md \
    docs/Arcanum.API.md \
    docs/Arcanum.CommandMap.json \
    docs/Arcanum.Command.Reference.md \
    docs/Arcanum.DEBUGGING.Human.md \
    docs/Arcanum.Design.Human.md \
    docs/Arcanum.OATH.md \
    docs/ArcanumOATH.Human.md \
    docs/Compendium.README.md \
    docs/superpowers/specs/2026-08-22-issue-122-host-tools-marker-pair-campaign-cleanup-design.md \
    docs/superpowers/plans/2026-08-22-issue-122-host-tools-marker-pair-campaign-cleanup.md \
    src/RetroDownfall.Arcanum.Core/DataLifecycle/FullInstallationResetRemediationContracts.cs \
    src/RetroDownfall.Arcanum.Core/DataLifecycle/InstallationResetContracts.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/HostProcessToolsMarkerCredentialCapability.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/OsCredentialStore.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/InMemoryOsCredentialStore.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/MacOsCredentialStore.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/LinuxOsCredentialStore.cs \
    src/RetroDownfall.Arcanum.Secrets/Security/WindowsOsCredentialStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetMarkerPairResetContracts.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetMarkerPairResetDigests.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActivePersistence.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetContracts.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetCampaignSchemaReadiness.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostProcessToolsPairReader.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostToolsMutationAdmission.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingDatabaseAccessPreparer.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/IInstallationResetStartupRecovery.cs \
    src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationStartupProbe.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/ICampaignPathMarkerLifecycle.FullInstallationReset.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathFullInstallationResetContracts.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.MarkerOwnershipProof.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestartRootProof.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathFullResetCleanupEvidenceStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathMarkerIntentStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_marker_intents.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_full_reset_cleanup_evidence.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_insert.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_update.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_full_reset_cleanup_evidence_guard_delete.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_marker_intents_guard_update.sql \
    src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerMutationGate.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsPorts.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsTransitionService.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Security/CampaignRootIdentityKeyProvider.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs \
    src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs \
    src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs \
    src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs \
    src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs \
    src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs \
    src/RetroDownfall.Arcanum.Cli/Program.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetRemediationAttestationVerifierTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetMarkerPairResetDigestTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveAuthenticationTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetActiveStoreTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetDatabaseTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetHostProcessToolsPairReaderTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCampaignSchemaReadinessTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCleanupAuthorityTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCallSiteTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationStartupProbeTests.cs \
    tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingDatabaseAccessPreparerTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Support/InstallationResetActiveStoreTestExtensions.cs \
    tests/RetroDownfall.Arcanum.Tests/Data/InstallationResetContractTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerCredentialCapabilityTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/CampaignRootIdentityKeyProviderTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerNativeCapabilityContractTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/MacOsCredentialStoreHandleOwnershipTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsMarkerStoreTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsTransitionServiceTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsStartupGateTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathMarkerLifecycleTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetInventoryTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathMarkerRootProofCallSiteTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupSchemaTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantRetainedEvidence.cs \
    tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreStartupRecoveryTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetArgvPreflightTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Api/DiWiringSmokeTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Cli/CliGrimoireDiWiringTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Operations/CovenantResetBootstrapBarrierTests.cs \
    tests/RetroDownfall.Arcanum.Tests/DependencyInjection/HostToolsMarkerPairResetCompositionTests.cs
  git diff --cached --name-status
  git diff --cached --check
  git status --short
  ```

  If implementation legitimately adds, renames, or conditionally modifies another issue-scoped file, update this allowlist explicitly before staging; do not replace it with `git add .`, `git add -A`, or a wildcard. The conditional command-map and Compendium docs are listed already; staging an unchanged existing file is harmless.

  If any unrelated file is staged, stop and unstage only that explicit path without discarding its working-tree content.

- [ ] Invoke `superpowers:requesting-code-review` only after staging, and give an independent reviewer the approved spec, this plan, issue #122, `git status --short`, `git diff --cached --stat`, and the complete `git diff --cached`. Reviewing the index is mandatory because newly created source/test/schema/doc files do not appear in an ordinary unstaged diff. Require explicit checks for:

  ```text
  every issue acceptance checkbox
  exact digest domains/preimages/golden vectors and same-shaped substitution tests
  one Core-owned nonauthorizing signed-attestation digest calculator reused by verifier and active authenticator
  persisted-evidence shared-joiner restart after expiry and every phase/effect crash point
  native live-capability replacement race and legal exact-absence matrix
  macOS item-ref, Linux stable SecretItem, and Windows LastWritten/full-record native contract coverage
  ordinary host-tools marker interface/implementation has no legacy compare-delete surface
  authenticated no-active-reset admission on every ordinary host-tools marker writer
  rerun durability barriers before publishing either recovered effect suffix
  six-column raw SQLite storage-class CAS
  journal-before-database-before-OS-before-absence ordering
  no-SQLite startup passphrase derivation followed by OS-first coordinator access before ordinary bootstrap
  same-connection read-only exact #122 Core-schema readiness before PairJournaled/resume effects and safe #121-schema refusal
  fresh/claim-only/checkpoint CLI probe-versus-attestation-reader ordering with zero checkpoint file access
  non-generating read-only Data Protection ring/secret access with byte-for-byte no-side-effect evidence
  wrong/released-lock, out-of-root, symlink/reparse, ownership, and permission failures precede provider/key/database access and preserve topology/metadata
  inventory-before-effects and shared-effect/distinct-child invariants
  non-generating existing Campaign root-identity key recovery before every fresh-process root open, with zero writes on missing evidence across receipt-null and receipt-present crash suffixes
  exact kind-four parent/companion shape including nullable blocked path
  companion direct-delete guard with parent-driven cascade as the only delete path
  receipt-present idempotent root rehydration without child substitution
  zero-filesystem replay of terminal children
  unavailable/mismatch no-filesystem behavior
  private proof/authority stale-lock and stale-revision rejection/reminting
  active-record bounds/deep-copy/monotonicity/source-generation closure
  issue #123 scope exclusions
  docs/code agreement
  ```

- [ ] If review finds anything, invoke `superpowers:receiving-code-review`, reproduce each valid finding with a failing test, apply the minimum fix, rerun the focused class, and restage only the explicit touched allowlisted paths. Then rerun the complete verification matrix above, rerun `git diff --cached --check`, and request follow-up review of the complete staged diff. Do not accept a suggestion solely because it sounds plausible, and do not commit with an unresolved issue-scoped finding.

- [ ] Immediately before commit, prove the reviewed index still matches the fully verified state:

  ```bash
  git status --short
  git diff --cached --stat
  git diff --cached --name-status
  git diff --cached --check
  git diff --cached --unified=0 -- . ':(exclude)docs/superpowers/plans/2026-08-22-issue-122-host-tools-marker-pair-campaign-cleanup.md' | RIPGREP_CONFIG_PATH= rg -n '^\+.*(TODO|TBD|FIXME|placeholder|implement later|NotImplementedException)'
  ```

  The final scan is expected to return no matches (exit `1`). The preserved `.idea/.idea.RetroDownfall.Arcanum/.idea/.name` must remain untracked, and every staged path must be present in the explicit allowlist above.

- [ ] Create the single requested commit only now:

  ```bash
  git commit -m "feat: compare-delete full-reset markers (#122)"
  ```

- [ ] Push the verified commit directly to the requested branch and prove the remote tip matches:

  ```bash
  git push origin long-term-memory
  git rev-parse HEAD
  git ls-remote --heads origin long-term-memory
  ```

- [ ] Compose the issue-closing comment from the outputs actually recorded in this run: pushed commit SHA, build `0 Warning(s)`/`0 Error(s)`, exact pass totals for both test projects, coverage threshold result, and AOT warning result. Do not use a generic “green” assertion or a value copied from an earlier run. Close only issue #122 with that concrete comment, then verify #122 is closed while umbrella/parent #74/#94 and downstream #123 remain open:

  ```bash
  gh issue view 122 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,closedAt,url
  gh issue view 74 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
  gh issue view 94 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
  gh issue view 123 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
  ```

  Run `gh issue close 122 --repo Retro-Downfall/RetroDownfall.Arcanum --comment` with the fully composed literal comment as its final argument immediately before these readbacks.

- [ ] Enumerate merged local/remote branches and linked worktrees before deleting anything:

  ```bash
  git worktree list --porcelain
  git for-each-ref --format='%(refname:short)' --merged long-term-memory refs/heads
  git for-each-ref --format='%(refname:short)' --merged origin/long-term-memory refs/remotes/origin
  ```

  Exclude `long-term-memory`, `main`, protected release branches, `origin/HEAD`, and any branch not clearly a merged feature/issue branch. Resolve every deletion target to an explicit name and confirm it is an ancestor of `long-term-memory`. Never use a glob, broad recursive delete, or destructive reset.

- [ ] Delete each proven merged feature branch explicitly with `git branch -d <name>` and, when the corresponding remote branch exists, `git push origin --delete <name>`. If a candidate is checked out in another worktree or that worktree is dirty/untracked, do not remove user state; report the exact blocker instead. In the expected direct-branch flow there may be no #122 feature branch to delete.

- [ ] Finish with read-only evidence:

  ```bash
  git status --short
  git log -1 --oneline --decorate
  git branch --show-current
  gh issue view 122 --repo Retro-Downfall/RetroDownfall.Arcanum --json state,url
  ```

  Expected: current branch is `long-term-memory`, its committed tree is clean apart from the preserved untracked IDE file, the pushed commit is at the remote tip, and #122 is closed.
