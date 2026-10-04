# Selective Hard Erasure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add exact, confirmed, single-store erasure of one Saga memory, one Lexicon entry, or one Covenant entry.

Each erasure:
- is verified locally;
- is recorded as content-free, installation-keyed erasure fingerprints, which every automatic writer and every restore honours;
- reports external exposure that Arcanum cannot revoke.

The plan also closes #225, fixes the adjacent defects found during design, and finishes #78.

**Architecture:**

*Keys and fingerprints*
- An OS-held HMAC key (`MemoryErasureKeyring`) signs content-free fingerprints stored in Core v13 (`memory_erasure_fingerprints`).
- A two-phase guard enforces them. Outside the transaction it probes for evidence and resolves the key; inside, it re-checks. It runs at the Saga insert, the Lexicon upsert, and the Covenant agent-mutation chokepoints.

*The erase path*
- Every erase is prepare, then apply:
  - Prepare issues a keyed effect digest inside a five-minute token.
  - Apply looks for a replay receipt first, then does all its work in one `BEGIN IMMEDIATE` transaction.
- Deletion goes through a shared `Count`/`Delete` plan runner, one per store:
  - Saga and Lexicon use `CovenantArtifactPurgePlans`.
  - Covenant uses `CovenantEntryErasurePlan`.
- The same runner serves live erasure and restore staging.

*Covenant specifics*
- Covenant erasure drains in-flight turns under a new exclusive operation.
- Canonical v6 adds a narrow authorization kind and the fixed curation binding epoch.

*Restore*
- Restore staging treats the destination's evidence as authoritative.
- It drains staged schema sweeps first, and purges any matching row in the archive.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core Minimal APIs, source-generated `System.Text.Json`, Microsoft.Data.Sqlite on hermetic SQLCipher (SQLite 3.53.3), FTS5 external content, macOS Keychain / Windows Credential Manager through `IOsCredentialStore`, System.CommandLine, xUnit 2.9.3, Native AOT.

**Spec:** `docs/superpowers/specs/2026-09-28-issue-100-selective-hard-erasure-design.md`. That spec is binding; every section number `§N` below refers to it.

## Global Constraints

### Workspace and process
- **Where to work.** Work only in `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/53102535-51c3-4138-9d62-f71ec51672f5/scratchpad/wt100`, on branch `claude/issue-100-selective-erasure`. The approved spec head is `fe63b204`.
- **Leave the primary checkout alone.** `/Users/mat/Source/apps/RetroDownfall.Arcanum` is on `main` with uncommitted user edits. Never read from it, stage, reset, clean, or write into it.
- **TDD is mandatory.** RED → GREEN → REFACTOR for every production behaviour.
  - A production edit is allowed only after a focused test has failed for the expected reason (feature missing, not a typo).
  - After GREEN, temporarily break each load-bearing line the task names, confirm the named test turns RED, then restore it and re-run GREEN.
- **Acceptance tests use production machinery.** They enter through the mapped route (`ArcanumWebApplicationFactory`, `[Collection("ApiHost")]`) or the registered CLI tree. They establish preconditions through production write paths:
  - `ISagaMemoryStore.InsertAsync`;
  - `ILexiconService.UpsertAsync`;
  - the Covenant set prepare/commit routes;
  - an actual erase, for fingerprints.
- **Never touch the real keychain.** Every test injects `InMemoryOsCredentialStore`. A test that spans the host and a CLI or restore container passes **one** shared instance to both.
- **Commits.** Commit each task separately after its focused matrix is green, with Conventional Commit subjects (`feat:`, `fix:`, `test:`, `docs:`). End every commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push, merge, or close issues inside a task.

### Code conventions
- **Match the file you are in.** Write code that reads like the surrounding file: its blank-line rhythm, file-scoped namespaces, primary constructors for DI, and positional records for DTOs.
- **Serialization.**
  - No reflection JSON, no anonymous DTOs, no `[JsonPropertyName]` on `/api` types.
  - Every `/api` payload goes on `ArcanumJsonContext`; every CLI payload on `CliJsonContext`.
  - Every failable `Results.Json` passes explicit `JsonTypeInfo`.
  - New wire enums use `StringOnlyJsonStringEnumConverter<T>` and have no zero member.
- **Results.** Domain operations return `Result`/`Result<T>`. Only endpoints map them to envelopes and status codes, through `ArcanumErrorMapper`.
- **SQL.**
  - Raw parameterized SQLite on the admitted connection.
  - Schema objects live one per file under `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/**`, written as `CREATE … IF NOT EXISTS`.
  - Trigger rewrites use `DROP TRIGGER IF EXISTS` and then `CREATE TRIGGER IF NOT EXISTS`.
  - No EF entity, no numbered migration, no compiled-model change. `EfNativeAotBoundaryTests` must stay green.
- **Content-free logging.** New log lines on erase, release, chokepoint, restore, status and reset-key paths carry no content, names or keys.
- **Documentation.**
  - `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.CommandMap.json` and `docs/Arcanum.DEBUGGING.Human.md` never name tracker issues (`DocumentationIssueReferenceTests`).
  - `docs/Arcanum.OATH.md`, `docs/Arcanum.Engineering.md` and `docs/superpowers/**` may.
  - The root `README.md` and `docs/Compendium.README.md` do not change. No configuration key is added.

### Exact values
- **Schema pins.**
  - Core v12 normalized source pin: `616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B`.
  - Covenant canonical v5 raw source pin: `E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925`. The canonical tier stays on the raw computation.
  - Neither step declares a backfill.
- **Store codes** everywhere are `MemoryReviewStore`: `Covenant = 1`, `Saga = 2`, `Lexicon = 3`.
- **Erasure key.**
  - OS credential: service `arcanum`, account `memory-erasure-fingerprint-key`.
  - Material: 32 random bytes, encoded as canonical unpadded base64url.
- **Domain labels.** Each is ASCII followed by a `0x00` byte:
  - `Arcanum.MemoryErasure.KeyId.v1`
  - `Arcanum.MemoryErasure.Fingerprint.v1`
  - `Arcanum.MemoryErasure.Request.v1`
  - `Arcanum.MemoryErasure.Subject.v1`
  - `Arcanum.MemoryErasure.Effect.v1`
  - `Arcanum.MemoryErasure.ContentBinding.v1`
- **Encoding rules.**
  - `KeyId` is the first 16 bytes of its HMAC. Every other digest is the full 32 bytes.
  - Field encodings: `u8`, `u32be`, `u64be`; `lp` (u32be length + bytes); `guid` (16 bytes, RFC 4122 big-endian); `opt` (u8 0, or u8 1 + value); `list` (u32be count + items).
  - Strings use strict UTF-8.
- **Identity values.**
  - Saga: the exact stored `Content`.
  - Lexicon: `Name.Trim().ToUpperInvariant()`, always computed from `Name`.
  - Covenant: `NormalizedKey`.
- **Scope byte.**
  - Saga: `0` Unclassified, `1` Global, `2` Campaign, `3` LegacyUnresolved.
  - Lexicon and Covenant: `1` Global, `2` Campaign.
- **Tables.** `memory_erasure_fingerprints`, `memory_erasure_receipts`, `memory_erasure_receipt_subjects`, and the index `idx_disclosure_subject_state_unfolded`.
- **Canonical v6 changes.**
  - Authorization kind `CovenantEntryErasure = 12`, function `arcanum_covenant_entry_erasure_authorized`.
  - `CovenantExclusiveOperation.CovenantEntryErasure = 9`.
  - `CovenantLeaseKind.EntryErasure = 11`.
  - Columns `covenant_key_epochs.IncarnationEpoch` and `covenant_mutation_receipts.EntryId`.
- **Error codes → HTTP status.**
  - `MemoryErasure.Unavailable` 503
  - `MemoryErasure.KeyUnavailable` 503
  - `MemoryErasure.KeyLost` 409
  - `MemoryErasure.InvalidPreflight` 400
  - `MemoryErasure.StalePlan` 409
  - `MemoryErasure.SubjectErased` 410
  - `MemoryErasure.ErasureIncomplete` 500
  - `Lexicon.SuppressedNameRefused` 409
  - `Lexicon.PinnedMutationRefused` 409
- **Restore blocker codes:** `backup.restore_erasure_key_missing`, `backup.restore_erasure_key_unavailable`, `backup.restore_erasure_evidence_unavailable`, `backup.restore_erasure_evidence_unjoinable`, `backup.restore_erasure_verification_failed`.
- **Agent-facing refusal messages.**
  - Lexicon: "This Lexicon entry is managed by the operator in this scope, so nothing was recorded."
  - Covenant: "This Covenant key is managed by the operator in this scope." The Covenant pin refusal uses the same text.
  - Lexicon `daemon_state:*` erase: "Unseen Servant daemon_state entries are managed by their daemon job and cannot be erased." (`Lexicon.InvalidName`, 400).
- **Token and WAL timing.**
  - Tokens live five minutes.
  - The WAL checkpoint uses `busy_timeout = 250` on a dedicated unpooled `GrimoireOrdinaryFreshConnectionKind.ReadWrite` connection.

### Commands
Run tests the way #98 and #99 did: Release, one node, focused filters.

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "<filter>"
```

- `GrimoireAdmissionBenchmarkManifestTests` reads **git-tracked** files, so `git add` new source files before running it.
- Tests that read credentials from stdin need `</dev/null`.

## Stable Contract Names

Every task uses these names and signatures verbatim. A task that introduces one lists it under **Produces**. A task that uses one lists it under **Consumes**.

### Core: `src/RetroDownfall.Arcanum.Core/Memory/`

- **`MemoryErasureIdentity.cs`**
  - `public enum MemoryErasureScopeKind : byte { Unclassified = 0, Global = 1, Campaign = 2, LegacyUnresolved = 3 }`
  - `public readonly record struct MemoryErasureIdentity(MemoryReviewStore Store, MemoryErasureScopeKind Scope, Guid? CampaignId, string Value)`
    - static factories `ForSaga(SagaMemoryScopeKind scope, Guid? campaignId, string exactContent)`, `ForLexicon(Guid? campaignId, string name)` (applies `Trim().ToUpperInvariant()`), `ForCovenant(CovenantScope scope, Guid? campaignId, string normalizedKey)`.
    - Invalid scope/Campaign pairings throw `ArgumentException`.
- **`MemoryErasureDigestGrammar.cs`**: `public static class MemoryErasureDigestGrammar`
  - `byte[] KeyId(ReadOnlySpan<byte> key)`
  - `byte[] Fingerprint(ReadOnlySpan<byte> key, MemoryErasureIdentity identity)`
  - `byte[] SagaRequest(ReadOnlySpan<byte> key, Guid mutationId, string memoryId, Guid? expectedClaimVersionId)`
  - `byte[] LexiconRequest(ReadOnlySpan<byte> key, Guid mutationId, LexiconCurationTarget target)`
  - `byte[] CovenantRequest(ReadOnlySpan<byte> key, Guid mutationId, CovenantErasePrepareRequest request)`
  - `byte[] Subject(ReadOnlySpan<byte> key, MemoryReviewStore store, string rowId)`
  - `byte[] Effect(ReadOnlySpan<byte> key, MemoryErasureEffectFacts facts)`
  - `byte[] SagaContentBinding(ReadOnlySpan<byte> key, string memoryId, string content)`
- **`MemoryErasureEffectFacts.cs`**: `public sealed record MemoryErasureEffectFacts(MemoryReviewStore Store, IReadOnlyList<string> RowIds, IReadOnlyList<Guid?> Versions, IReadOnlyList<MemoryErasureTableCount> Targets, int Labels, int RetirementSuppressions, MemoryErasureEffectFlags Flags, CovenantErasureEffectFacts? Covenant, IReadOnlyList<MemoryExternalEvidence> Evidence, int RetainedCopiesMask)`
  - with `public sealed record MemoryErasureTableCount(string Table, long Rows)`,
  - `[Flags] public enum MemoryErasureEffectFlags { None = 0, Pinned = 1, GlobalEntryResurfaces = 2, ReclaimsKey = 4, RetainsCampaignMask = 8, GlobalConfirmedResurfaces = 16 }`,
  - and `public sealed record CovenantErasureEffectFacts(Guid DatasetGeneration, long KeyEpoch, long KeyReclamationEpoch)`.
- **`MemoryErasureKey.cs`**: `public sealed class MemoryErasureKey : IDisposable`
  - holds a private copy of the 32 key bytes;
  - exposes `ReadOnlySpan<byte> KeyId`, `byte[] Fingerprint(MemoryErasureIdentity)` and instance wrappers for every grammar method;
  - `Dispose` zeroes the bytes;
  - `public static MemoryErasureKey FromBytes(ReadOnlySpan<byte> key)`. It is public because Core internals are not visible to Infrastructure.
- **`IMemoryErasureKeyProvider.cs`**
  - `public enum MemoryErasureKeyState { Unresolved = 1, Absent = 2, Present = 3, Unavailable = 4, Malformed = 5 }`
  - `public enum MemoryErasureKeyProbe { UseLatched = 1, Reprobe = 2 }`
  - `public sealed record MemoryErasureKeyOpenResult(MemoryErasureKeyState State, MemoryErasureKey? Key)`
  - `public sealed record MemoryErasureKeyLatch(MemoryErasureKeyState State, byte[]? KeyId)`
  - `public interface IMemoryErasureKeyProvider { MemoryErasureKeyLatch Latch { get; } MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe); MemoryErasureKey? TryCopyLatched(); }`
  - `Latch` and `TryCopyLatched` never perform I/O.
- **`MemoryErasureContracts.cs`**: store-neutral wire types, all string-only enums.
  - Enums:
    - `MemoryLocalErasureOutcome { Verified = 1, RowsRemovedScrubPending = 2 }`
    - `[Flags]`-free `MemoryErasureScrubPendingReason { WalCheckpointPending = 1, FullTextSecureDeleteUnverified = 2, VectorIndexScrubUnverified = 3 }`, with a mask helper `MemoryErasureScrubPendingReasons.ToMask/FromMask` (bits `1 << (code-1)`)
    - `MemoryErasureWalCheckpointAttempt { Truncated = 1, Busy = 2, Unavailable = 3, NotAttempted = 4 }`
    - `MemoryExternalChannel { InferenceProviderAuthorship = 1, InferenceProviderContext = 2, EmbeddingProvider = 3, EncryptedBackup = 4, OtherExternal = 5 }`
    - `MemoryExternalEvidence { Known = 1, ReceiptWindow = 2, NotRecorded = 3, NotApplicable = 4 }`
    - `MemoryExternalRevocation { NotPerformed = 1 }`
    - `MemoryRetainedLocalCopy { SessionTranscripts = 1, SearchAndSummaryDerivatives = 2, Attachments = 3, ResponseCaches = 4, ApplicationLogs = 5, AuditLog = 6, BackupArchives = 7, OtherLocalState = 8 }`
    - `MemoryErasureNote { GlobalKeyStillProposableInCampaigns = 1, UnresolvedScopeStopsMatchingOnResolution = 2, OtherScopesUnaffected = 3, CovenantDrainsInFlightTurns = 4 }`
    - `MemoryErasureReleaseOutcome { Released = 1, NotFingerprinted = 2 }`
    - `MemoryErasureKeyStatus { Absent = 1, Present = 2, Unavailable = 3, Lost = 4 }`
  - Records:
    - `MemoryExternalExposureChannelDto(MemoryExternalChannel Channel, MemoryExternalEvidence Evidence)`
    - `MemoryErasureExternalExposureDto(MemoryExternalRevocation Revocation, MemoryExternalExposureChannelDto[] Channels)`
    - `LexiconErasurePlanFacts(bool GlobalEntryResurfaces)`
    - `CovenantErasurePlanFacts(int ConfirmedVersions, int ProposedVersions, int ProvenanceLeaves, int MutationReceipts, int CurationRows, int OutboxRows, int SearchDocuments, bool ReclaimsKey, bool RetainsCampaignMask, bool GlobalConfirmedResurfaces, bool IsPinned, int AffectedCampaigns)`
    - `MemoryErasurePlanDto(int ErasedItemCount, long RowsToRemove, int LabelsToRemove, int RetirementSuppressionsToRemove, bool Pinned, LexiconErasurePlanFacts? Lexicon, CovenantErasurePlanFacts? Covenant)`
    - `MemoryErasurePreflightDto(MemoryReviewStore Store, Guid MutationId, string RequestDigest, string EffectDigest, MemoryErasurePlanDto Plan, MemoryErasureExternalExposureDto External, MemoryRetainedLocalCopy[] RetainedLocalCopies, MemoryErasureNote[] Notes, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string PreflightToken)`
    - `MemoryErasureLocalResultDto(MemoryLocalErasureOutcome Outcome, MemoryErasureScrubPendingReason[] PendingReasons, MemoryErasureWalCheckpointAttempt WalCheckpointAttempt, int ErasedItemCount, long RemovedRowCount, int RemovedLabelCount, int RemovedRetirementSuppressionCount, bool SuppressionFingerprintRecorded)`
    - `MemoryErasureResultDto(MemoryReviewStore Store, Guid MutationId, bool Replayed, string EffectDigest, MemoryErasureLocalResultDto Local, MemoryErasureExternalExposureDto External, MemoryRetainedLocalCopy[] RetainedLocalCopies, MemoryErasureNote[] Notes)`
    - `MemoryErasureReleaseResultDto(MemoryReviewStore Store, MemoryErasureReleaseOutcome Outcome, int ReleasedCount)`
    - `MemoryErasureStoreCountsDto(MemoryReviewStore Store, long Fingerprints, long Unverifiable, long Receipts)`
    - `MemoryErasureStatusDto(MemoryErasureKeyStatus KeyStatus, MemoryErasureStoreCountsDto[] Stores, long PendingScrubReceipts)`
    - `MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt WalCheckpointAttempt, long Verified, long StillPending)`
    - `MemoryErasureKeyResetPreflightDto(MemoryErasureKeyStatus KeyStatus, MemoryErasureStoreCountsDto[] Stores, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string PreflightToken)`
    - `MemoryErasureKeyResetRequest(string PreflightToken)`
    - `MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus KeyStatus, long FingerprintsDiscarded, long ReceiptsDiscarded, bool KeyCreated)`
    - `SagaErasePrepareRequest(string MemoryId, string ExpectedContentHash, string? ExpectedClaimVersionId, Guid MutationId)`
    - `SagaEraseRequest(string MemoryId, string ExpectedContentHash, string? ExpectedClaimVersionId, Guid MutationId, string PreflightToken)`
    - `SagaErasureReleaseRequest(SagaMemoryScopeKind ScopeKind, Guid? CampaignId, string Content)`
    - `LexiconErasePrepareRequest(LexiconCurationTarget Target, Guid MutationId)`
    - `LexiconEraseRequest(LexiconCurationTarget Target, Guid MutationId, string PreflightToken)`
    - `LexiconErasureReleaseRequest(LexiconCurationScope Scope, string Name)`
- **`IMemoryErasureServices.cs` (Task 1).** It holds **Prepare and Apply only** (R1), in the namespace `RetroDownfall.Arcanum.Core.Memory`.
  - `ISagaMemoryErasureService`: `PrepareAsync(SagaErasePrepareRequest, CancellationToken) → Task<Result<MemoryErasurePreflightDto>>`; `ApplyAsync(SagaEraseRequest, OperatorAuthorityContext, CancellationToken) → Task<Result<MemoryErasureResultDto>>`.
  - `ILexiconErasureService`: the same two methods over the Lexicon request types.
  - `ICovenantEntryErasureService`: `PrepareAsync(CovenantErasePrepareRequest, OperatorAuthorityContext, CancellationToken)` and `ApplyAsync(CovenantEraseRequest, OperatorAuthorityContext, CancellationToken)`, with the same result types.
- **`IMemoryErasureRelease.cs` (Task 17).** `public interface IMemoryErasureRelease` with `ReleaseSagaAsync(SagaErasureReleaseRequest, CancellationToken)`, `ReleaseLexiconAsync(LexiconErasureReleaseRequest, CancellationToken)` and `ReleaseCovenantAsync(CovenantErasureReleaseRequest, CancellationToken)`. Each returns `Task<Result<MemoryErasureReleaseResultDto>>`.
- **`IMemoryErasureAdministration.cs` (Task 18):** `GetStatusAsync`, `ScrubAsync`, `PrepareKeyResetAsync`, `ResetKeyAsync(MemoryErasureKeyResetRequest, …)`.

### Core: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantErasureContracts.cs`

These types are declared in `CovenantPublicContractInventory`.

- `CovenantEraseHeadExpectation(Guid VersionId, long LaneRevision)`
- `CovenantErasePrepareRequest(CovenantScope Scope, Guid? CampaignId, string Key, Guid EntryId, CovenantEraseHeadExpectation? Confirmed, CovenantEraseHeadExpectation? Proposed, Guid MutationId)`
- `CovenantEraseRequest(…same fields…, string PreflightToken)`
- `CovenantErasureReleaseRequest(CovenantScope Scope, Guid? CampaignId, string Key)` with `Result Validate()`. **Owned by Task 17**, not Task 1 (R2).
- `CovenantErasurePreflightBody`: format byte `0x01`, an encode/decode pair modeled on `CovenantOperatorPreflightBody`, and the §6.1 fields.

### Core: primitives
- `ErrorCodes.MemoryErasure` family, plus `ErrorCodes.Lexicon.SuppressedNameRefused` and `ErrorCodes.Lexicon.PinnedMutationRefused`.

### Infrastructure
- **`Security/MemoryErasureKeyring.cs`**
  - `internal interface IMemoryErasureKeyCreator { MemoryErasureKeyOpenResult OpenOrCreate(bool evidenceRowsExist); MemoryErasureKeyOpenResult CreateForReset(); }`
  - `internal sealed class MemoryErasureKeyring(IOsCredentialStore credentials) : IMemoryErasureKeyProvider, IMemoryErasureKeyCreator` is registered as one singleton behind both interfaces. The creator is registered only in the host container.
  - `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`.
- **`Data/MemoryErasureEvidence.cs`**: `internal static class MemoryErasureEvidence`. Every method takes `(SqliteConnection, SqliteTransaction?, …, CancellationToken)`, and every method treats `Core < 13 or table absent` as empty.
  - `IsInstalledAsync`
  - `AnyAsync(MemoryReviewStore)`
  - `AnyForeignAsync(MemoryReviewStore, byte[] keyId)`
  - `ContainsAsync(byte[] fingerprint)`
  - `InsertFingerprintAsync(byte[] fingerprint, MemoryReviewStore, byte[] keyId) → bool`
  - `DeleteFingerprintAsync(byte[] fingerprint) → int`
  - `InsertReceiptAsync(MemoryErasureReceiptRow, IReadOnlyList<byte[]> subjectDigests)`
  - `ReadReceiptAsync(Guid mutationId) → MemoryErasureReceiptRow?`
  - `SubjectErasedAsync(byte[] subjectDigest) → bool`
  - `ClearWalPendingAsync(Guid? mutationId) → long`
  - `CountAsync(byte[]? currentKeyId) → MemoryErasureEvidenceCounts`
  - `DeleteUnverifiableAsync(byte[] currentKeyId) → (long Fingerprints, long Receipts)`
  - Also: `internal sealed record MemoryErasureReceiptRow(...)`, which mirrors §6.3 columns, and `MemoryErasureEvidenceCounts`.
- **`Data/MemoryErasureGuard.cs`**
  - `internal sealed class MemoryErasureGuardContext : IDisposable` with `Store`, `EvidencePresent`, `Key`.
  - `internal enum MemoryErasureGuardVerdict { Allowed = 1, Withheld = 2, RetryWithKey = 3, KeyLost = 4 }`
  - `internal static class MemoryErasureGuard { Task<Result<MemoryErasureGuardContext>> PrepareAsync(SqliteConnection, MemoryReviewStore, IMemoryErasureKeyProvider, CancellationToken); Task<MemoryErasureGuardVerdict> CheckAsync(SqliteConnection, SqliteTransaction?, MemoryErasureGuardContext, MemoryErasureIdentity, CancellationToken); }`. The transaction is nullable because Lexicon writes run under a raw `BEGIN IMMEDIATE`. There is also an overload that takes `Func<MemoryErasureIdentity>`.
- **`Data/Covenant/CovenantArtifactPlanRunner.cs`**
  - `internal enum CovenantArtifactPlanMode { Count = 1, Delete = 2 }`
  - `internal sealed record CovenantArtifactPlanTally(IReadOnlyList<MemoryErasureTableCount> Targets, SagaVectorMirrorKind VectorMirror, long ArtifactRows)`
  - `internal static class CovenantArtifactPlanRunner { Task<CovenantArtifactPlanTally> RunAsync(SqliteConnection, SqliteTransaction?, SensitiveArtifactKind kind, string artifactKey, CovenantArtifactPlanMode mode, CancellationToken); }`
  - `Targets` lists every projection in plan order, then the artifact table last, each exactly once (R9).
- **`Data/SagaVectorMirror.cs`**
  - `internal enum SagaVectorMirrorKind { Absent = 1, PlainTable = 2, LegacyVirtualTable = 3 }`
  - `internal static class SagaVectorMirror`. Its members take `(DbConnection, DbTransaction?, …)`: `ClassifyAsync`, `ClassifyAsync(string table)`, `DeleteAsync(…, string memoryId) → long`, `DeleteAllAsync() → long`, and `UpsertAsync(…, string memoryId, ReadOnlyMemory<float> vector, bool vecAvailable) → SagaVectorMirrorKind`.
- **`Data/GrimoireWalCheckpoint.cs`**: `internal static class GrimoireWalCheckpoint { Task<Result<CovenantWalCheckpointOutcome>> TruncateAsync(SqliteConnection, CancellationToken); }`. `CovenantWalCheckpointOutcome` gains `internal bool IsTruncated`.
- **`Memory/MemoryErasureScrubber.cs`**: `internal sealed class MemoryErasureScrubber(IGrimoireOrdinaryConnectionFactory connections) { Task<MemoryErasureWalCheckpointAttempt> CheckpointAsync(CancellationToken); }`
- **`Memory/MemoryErasureExposure.cs`**: `internal static class MemoryErasureExposure` with `ReadSagaAsync(…, IReadOnlyList<string> memoryIds)`, `ReadLexiconAsync(…, Guid entryId, string normalizedName, Guid? campaignId)`, `ReadCovenantAsync(…, Guid entryId)`. Each returns `MemoryErasureExternalExposureDto`. `MemoryErasureRetainedCopies.For(MemoryReviewStore store, bool auditFilesExist) → MemoryRetainedLocalCopy[]`.
- **`Memory/MemoryErasureTokens.cs`**: `MemoryReviewTokenCodec` gains purposes `ErasurePlan = 4` and `ErasureKeyReset = 5`.
  - `internal sealed record MemoryErasurePlanTokenFacts(MemoryReviewStore Store, byte[] RequestDigest, byte[] EffectDigest, byte[]? ContentBinding, Guid? DatasetGeneration)`
  - `internal sealed record MemoryErasureKeyResetTokenFacts(MemoryErasureKeyStatus KeyStatus, long UnverifiableFingerprints, long UnverifiableReceipts)`
- **Services:**
  - `Memory/SagaMemoryErasureService.cs`: `ISagaMemoryErasureService`.
  - `Lexicon/LexiconService.Erasure.cs`: a partial `LexiconService : ILexiconErasureService`.
  - `Covenant/CovenantEntryErasureService.cs`: `ICovenantEntryErasureService`.
  - `Memory/MemoryErasureAdministration.cs`: `IMemoryErasureAdministration`.
- **Covenant gate:**
  - `ICovenantOperationGate.AcquireEntryErasureAsync(CovenantOperationScope entryScope, bool reclaimsKey, CovenantExclusiveRecoveryOwner owner, CancellationToken) → ValueTask<Result<CovenantEntryErasureLease>>`
  - `CovenantEntryErasureLease : CovenantExclusiveOperationLease, ICovenantSnapshotReadLease` with `bool CoversInstallation`.
- **Covenant plan:** `Data/Covenant/CovenantEntryErasurePlan.cs`: `internal static class CovenantEntryErasurePlan { Task<CovenantEntryErasureTally> RunAsync(SqliteConnection, SqliteTransaction, CovenantEntryErasureSubject subject, CovenantArtifactPlanMode mode, CovenantEntryErasureMode erasureMode, CancellationToken); }`
  - `CovenantEntryErasureMode { Live = 1, Staged = 2 }`
  - `CovenantEntryErasureSubject(Guid EntryId, CovenantScope Scope, Guid? CampaignId, string NormalizedKey, bool IsMasked, bool ReclaimsKey)`
- **Covenant agent gate:** `Data/Covenant/CovenantAgentErasureGate.cs`: `internal sealed record CovenantAgentErasureGate(MemoryErasureKey? Key, MemoryErasureKeyState LatchState) : IDisposable`, with `FromLatch`, `None`, `OperatorManagedRefusal`, `ClassifyAsync(…)` and `RefusalFor(CovenantAgentErasureState)`.
  - It is captured by `CovenantMutationKernel.CaptureErasureGate()` (R12) before `BEGIN`, and passed to `ApplyBatchAsync(batch, transaction, gate, ct)` for agent and operator paths alike.
- **Canonical content list:** `Data/Covenant/CovenantCanonicalContentTables.cs`: `internal static class CovenantCanonicalContentTables { static IReadOnlyList<string> InDeletionOrder }`.
- **Disclosure:**
  - `Data/Covenant/ExternalDisclosureStateFold.cs`: `internal static class ExternalDisclosureStateFold { Task FoldSubjectTailAsync(SqliteConnection, SqliteTransaction, CovenantDisclosureSubject subject, ExternalDisclosureFoldOrigin origin, CancellationToken); }` with `ExternalDisclosureFoldOrigin { Live = 1, RestoreStaging = 2 }`.
  - `ExternalDisclosureStateReader.ReadEffectiveAsync(SqliteConnection, CancellationToken)`.
- **Restore:**
  - `BackupRestoreErasureEvidence`, the destination read result: `None`, `Present(keyId, rows)`, or `Refused(code)`.
  - `BackupRestoreService.ReadDestinationErasureEvidenceAsync`.
  - `BackupRestoreService.ReconcileStagedMemoryEvidenceAsync`.
  - DTOs `BackupRestoreErasureEvidenceSummary` and `BackupRestoreErasureApplication`.

### Api
- `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs`: `internal static RouteGroupBuilder MapMemoryErasureEndpoints(this RouteGroupBuilder api)`, mapping the thirteen routes of §17.1 with the names given there.

### CLI
- `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.MemoryErasure.cs`
- `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Erasure.cs`, containing the Saga, Lexicon, release and `memory erasure` handlers
- `CovenantCommands` gains the `Erase` and `Release` handlers
- `src/RetroDownfall.Arcanum.Cli/UX/CovenantExternalRetentionDisclosureWriter.cs` gains `WriteErasure(MemoryErasureExternalExposureDto)` and a shared `WriteHelpTargets()`

### Names introduced by individual tasks

These names belong to the task that owns them. Other tasks consume them verbatim.

**Amendments already applied above**, listed here for completeness:
- The three store ports in `IMemoryErasureServices.cs` carry only `PrepareAsync`/`ApplyAsync` (T1). Remove `ReleaseAsync`, and move `IMemoryErasureAdministration` to its own file (T18).
- `ICovenantEntryErasureService` lives in the `RetroDownfall.Arcanum.Core.Memory` namespace (T1).
- `CovenantErasureReleaseRequest(CovenantScope Scope, Guid? CampaignId, string Key)` gains `Result Validate()`, and its owner is T17, not T1.
- `public static MemoryErasureKey FromBytes(ReadOnlySpan<byte>)`, public, not internal (T1).
- `MemoryErasureGuard.CheckAsync(SqliteConnection, SqliteTransaction?, …)` (T6).
- `CovenantArtifactPlanRunner.RunAsync(SqliteConnection, SqliteTransaction?, …)` (T4).
- `SagaVectorMirror` members on `(DbConnection, DbTransaction?, …)`, with `UpsertAsync → Task<SagaVectorMirrorKind>` (T4).
- `CovenantAgentErasureGate(MemoryErasureKey? Key, MemoryErasureKeyState LatchState) : IDisposable` (T11).
- `MemoryErasureExposure.Read*Async` take a `SqliteTransaction?` second parameter (T12).
- `BackupRestoreErasureEvidence.Refused(string code, MemoryErasureEvidenceSnapshot? rows)` (T22).
- `ExternalDisclosureStateReader.ReadEffectiveAsync(SqliteConnection, CancellationToken, Func<CancellationToken, Task>? afterPersistedReadForTests = null)`, plus the overload `(SqliteConnection, SqliteTransaction, CancellationToken)` (T21).

**Add, by owning task.** Types are `internal` unless marked.

| Task | Name and signature |
|---|---|
| 1 | `MemoryErasureDigestGrammar.KeyBytes = 32`, `KeyIdBytes = 16`, `DigestBytes = 32`; `static string CanonicalRowId(string rowId)` |
| 1 | public `MemoryErasureTableCodes { static IReadOnlyList<string> Tables; static byte For(string table); }`, in `MemoryErasureEffectFacts.cs`, codes 1–28 append-only |
| 1 | public `MemoryErasureScrubPendingReasons.ToMask(IEnumerable<MemoryErasureScrubPendingReason>) → int` / `FromMask(int) → MemoryErasureScrubPendingReason[]`; public `MemoryRetainedLocalCopies.ToMask/FromMask` (same shape) |
| 1 | `MemoryErasureKey.HasKeyId(ReadOnlySpan<byte>) → bool`; `CovenantEraseRequest.ToPrepareRequest() → CovenantErasePrepareRequest` |
| 1 | public `CovenantErasurePreflightBody(CovenantDigest RequestDigest, ulong OperatorAuthorityEpoch, Guid DatasetGeneration, ulong KeyEpoch, ulong KeyReclamationEpoch, bool ReclaimsKey, Guid EntryId, Guid? ConfirmedVersionId, Guid? ProposedVersionId, CovenantDigest EffectDigest, long IssuedAt, long ExpiresAt)`, with `EncodedBytes = 171`, `Encode()` (172 bytes, `[0] = 0x01`) and `static Result<…> TryDecode(ReadOnlySpan<byte>)` |
| 2 | `MemoryErasureKeyring : IMemoryErasureKeyProvider, IMemoryErasureKeyCreator, IDisposable`, also registered as itself |
| 3 | test `CoreSchemaVersionTwelveFixture { PublishedFingerprint, Objects, Fingerprint, ChainSet() }`; `CoreGrimoireSchemaDataInitializer.ConvergeLexiconSecureDeleteAsync(SqliteConnection, SqliteTransaction, CancellationToken)`; indexes `idx_memory_erasure_fingerprints_store_key`, `idx_memory_erasure_receipts_store_key`, `idx_memory_erasure_receipts_pending`, `idx_memory_erasure_receipt_subjects_digest`; trigger `memory_erasure_receipts_guard_update` (with its fixed abort text) |
| 4 | `SagaVectorMirror.ClassifyAsync(string table)`, `DeleteAllAsync() → Task<long>`; `CovenantArtifactPurgeTarget.CountBy(string parameter) → string`; test `SagaStoreHarness.VectorAccelerator` |
| 5 | test `AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(DbConnection, CancellationToken = default)` |
| 6 | `MemoryErasureReceiptRow(Guid MutationId, MemoryReviewStore Store, byte[] KeyId, byte[] RequestDigest, byte[] EffectDigest, int ErasedItemCount, long RemovedRowCount, int RemovedLabelCount, int RemovedRetirementSuppressionCount, MemoryExternalEvidence Authorship, …Context, …Embedding, …Backup, …OtherExternal, int RetainedCopiesMask, int ScrubStateCode, int ScrubPendingReasonMask)`; `MemoryErasureEvidenceCounts(IReadOnlyList<MemoryErasureStoreCountsDto> Stores, long UnverifiableReceipts, long PendingScrubReceipts)` |
| 6 | `MemoryErasureGuard.CheckAsync(…, Func<MemoryErasureIdentity> identity, …)`; `RunWithRetryAsync<T>(SqliteConnection, MemoryReviewStore, IMemoryErasureKeyProvider, Func<MemoryErasureGuardContext, Task<T>>, CancellationToken)`; `static Error RefusalFor(MemoryErasureKeyState)`; `KeyLostError`, `KeyUnavailableError`, `UnavailableError`; `MemoryErasureGuardException(Error)`; `MemoryErasureRetryException` |
| 6 | `MemoryErasureKeyWarmup.RunAsync(SqliteConnection, IMemoryErasureKeyProvider, CancellationToken)`; test `MemoryErasureTestKeys.{Isolated, CreateKey, SeedFingerprintAsync}`, `CountingOsCredentialStore`, `MemoryErasureEvidenceDeleterTests.AllowedCallers` |
| 7 | `SagaErasureWriteGate(ArcanumDbContext, IMemoryErasureKeyProvider)` with `PrepareAsync(CancellationToken)`, `IsWithheldAsync(MemoryErasureGuardContext, Guid?, string, CancellationToken) → Task<bool>` and `static SagaIdentity(SagaMemoryScopeKind, string?, string)`; `SagaMemoryStore(…, IMemoryErasureKeyProvider erasureKeys, ICovenantLabeledArtifactGuard? = null)`; `SagaExtractionOutcome.DeferredForErasureKey = 4`; `SagaExtractionService.ErasureKeyDeferralDelayForTests`; test `ErasureChokepointOwners`, `SagaStoreHarness.CreateAsync(bool, IMemoryErasureKeyProvider? = null)` |
| 8 | public `LexiconDeletionOrigin { Operator = 1, Agent = 2 }`, `LexiconAgentDeletionTarget(Guid EntryId, bool IsRetired, bool IsPinned)`, `LexiconAgentRefusals.{OperatorManaged, RetiredDeletion}`; `ILexiconService.FindAgentDeletionTargetAsync(string, LexiconScope, CancellationToken = default) → Task<Result<LexiconAgentDeletionTarget?>>` and `DeleteByNameAsync(string, LexiconScope, LexiconDeletionOrigin, CancellationToken = default) → Task<Result<bool>>`, both default-implemented to fail closed; `LexiconService(db, logger, options, IMemoryErasureKeyProvider erasureKeys, guard? = null, codec? = null, TimeProvider? = null, …)` |
| 9 | test `CovenantCanonicalSchemaVersionFiveFixture`; `idx_covenant_mutation_receipts_entry`; triggers `covenant_key_epochs_guard_incarnation`, `covenant_key_epochs_guard_delete`, `covenant_curation_heads_guard_delete` |
| 10 | public `CovenantCurationSubject(CovenantOperationScope, CovenantKey NormalizedKey, CovenantLane, long KeyDependencyEpoch, long? KeyBindingEpoch = null)`; `CovenantKeyEpochPair(long Dependency, long Binding)`; `CovenantKeyEpochs.ReadAsync(CovenantMutationTransaction, string, CancellationToken) → ValueTask<CovenantKeyEpochPair>`; `CovenantMutationService.TryReplayCurationAsync(CovenantCurationRequest, CovenantOperationScope, string, CancellationToken)`; `BackupRestoreProtectedStateInspector.CanonicalContentTables` becomes a property |
| 11 | public `CovenantAgentErasureState : byte { Clear = 1, Withheld = 2, KeyUnavailable = 3, KeyLost = 4 }`; `CovenantLaneHeadProbe` / `CovenantRetirementTarget` gain `AgentErasure` and `IsAgentWithheld`; gate `FromLatch`, `None`, `OperatorManagedRefusal`, `ClassifyAsync(SqliteConnection, SqliteTransaction, CovenantScope, Guid?, string, CancellationToken)`, `static Error? RefusalFor(CovenantAgentErasureState)` |
| 11 | `CovenantMutationKernel(CovenantQuotaGuard, IMemoryErasureKeyProvider)`, `CaptureErasureGate()`, `ApplyBatchAsync(batch, transaction, CovenantAgentErasureGate, ct)`; `CovenantStore(ICovenantConnectionSource, IMemoryErasureKeyProvider)`; `IsInstalledAsync` checks the table first |
| 11 | test `CovenantSchemaScratchDatabase.RecordCoreSchemaVersionAsync`, `CovenantCanonicalFixture.CreateAsync(…, withErasureEvidence)` with `.Credentials` and `.ErasureKeys`, `CovenantServiceHarness.{SeedCovenantFingerprintAsync, KeyringInState}` |
| 12 | `IMemoryErasureTokenCodec { IssueErasurePlan, ReadErasurePlan, IssueErasureKeyReset, ReadErasureKeyReset }`; `MemoryErasureIssuedToken(string Token, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc)` |
| 12 | `MemoryErasureExposure.EvidenceCodes(MemoryErasureExternalExposureDto)`; `MemoryErasureRetainedCopies.AuditFilesExist(ArcanumSettings)` |
| 12 | `MemoryErasureProtocol.{RequireInstalledAsync, OpenKeyForPrepareAsync(SqliteConnection, IMemoryErasureKeyCreator, MemoryReviewStore, ct), OpenKeyForApplyAsync(…, IMemoryErasureKeyProvider, …), ProbeReceiptAsync(SqliteConnection, SqliteTransaction?, MemoryReviewStore, Guid, byte[], ct), FinishAsync(SqliteConnection, MemoryErasureScrubber, MemoryErasureReceiptRow, bool, MemoryErasureNote[], ct)}` |
| 12 | `MemoryErasureNotes.For(MemoryReviewStore, MemoryErasureScopeKind, bool reclaimsKey)`; test `MemoryErasureStructuralTests.ContentFreeLogFiles` |
| 13 | `SagaRetirementSuppression.{Digests(byte[], SagaMemoryScopeKind, string?, string), CountPairAsync(DbConnection, DbTransaction?, SagaMemoryScopeKind, string?, string, ct), DeletePairAsync(same)}`; `MemoryErasureLabels.{ReadAsync, DeleteExactAsync}`; `MemoryErasureLabelRow(Guid LabelId, string ArtifactId, Guid? OwnerCampaignId)` |
| 13 | `SagaMemoryErasureService(ArcanumDbContext, IMemoryErasureKeyCreator, IMemoryErasureKeyProvider, IMemoryErasureTokenCodec, MemoryErasureScrubber, ICovenantOperationGate, IOperatorAuthorityContextIssuer, ICovenantSqliteConnectionInitializer, IOptionsMonitor<ArcanumSettings>)`; test `MemoryErasureRouteInventoryTests.Routes`, `MemoryErasureRouteDriver` (member table in 04/T13), `MemoryErasureRoundTrip<TApply>` |
| 14 | `LexiconErasureDependencies(IMemoryErasureKeyCreator KeyCreator, IMemoryErasureTokenCodec Tokens, MemoryErasureScrubber Scrubber, ICovenantOperationGate Gate, IOperatorAuthorityContextIssuer Issuer, ICovenantSqliteConnectionInitializer Initializer)`; `LexiconService` gains a trailing `LexiconErasureDependencies? erasure = null`; `LexiconDaemonStateNames.Is(string)` |
| 15 | `CovenantEntryErasureLease(ICovenantExclusiveLeaseRegistration)` constructor |
| 16 | `CovenantEntryErasurePlan.ReadSubjectAsync(SqliteConnection, SqliteTransaction, Guid, ct) → Task<CovenantEntryErasureSubject?>`; `ProveAbsentAsync(…, CovenantEntryErasureSubject, IReadOnlyList<Guid>, ct) → Task<IReadOnlyList<MemoryErasureTableCount>>`; `CovenantEntryErasureTally(IReadOnlyList<MemoryErasureTableCount> Targets, IReadOnlyList<Guid> VersionIds, int ConfirmedVersions, int ProposedVersions, bool FullTextSecureDeleteVerified, bool RetainsCampaignMask, bool KeyReclaimed)` |
| 16 | `ICovenantEntryErasurePreparer.PrepareHeldAsync(CovenantErasePrepareRequest, OperatorAuthorityContext, ct) → Task<Result<CovenantEntryErasurePrepared>>`; `CovenantEntryErasurePrepared(MemoryErasurePreflightDto Preflight, ICovenantSnapshotReadLease ReadLease)`; seams `CommitForTesting` and `ReceiptReReadForTesting` |
| 17 | public `IMemoryErasureRelease { ReleaseSagaAsync, ReleaseLexiconAsync, ReleaseCovenantAsync }` (R2); `MemoryErasureRelease(ArcanumDbContext, IMemoryErasureKeyProvider, ILogger<MemoryErasureRelease>)` in `Infrastructure/Memory/`; `MemoryErasureFingerprintRelease.{DeleteCandidatesAsync, WouldReleaseAsync, ReleaseForOperatorWriteAsync}` in `Data/` |
| 17 | trailing `bool? ReleasesErasureFingerprint = false` on `CovenantMutationEffectDto` and `CovenantMutationPlanPayload`; trailing `bool? ReleasedErasureFingerprint = false` on `CovenantMutationResultDto`, `CovenantMutationResultPayload`, `SagaCurationOutcome`, `SagaCurationResult` and `MemoryReviewBulkItemResultDto`; `SagaMemoryReviewService` gains a required `IMemoryErasureKeyProvider`; the driver's release trio |
| 18 | `MemoryErasureAdministration(ArcanumDbContext, MemoryErasureKeyring, IMemoryErasureTokenCodec, MemoryErasureScrubber, ILogger<…>)`, plus an internal constructor that also takes a checkpoint delegate; `MemoryErasureEvidence.ReadWalPendingAsync(SqliteConnection, SqliteTransaction?, ct) → Task<IReadOnlyList<Guid>>`; `MemoryErasureKeyring.ProbePresence() → OsCredentialStoreStatus?`; test `SecretAccessRecordingCredentialStore`, `WithRecordingCredentials` |
| 19 | `ArcanumApiClient.{PrepareSagaErasureAsync, EraseSagaMemoryAsync, PrepareLexiconErasureAsync, EraseLexiconEntryAsync, PrepareCovenantErasureAsync, EraseCovenantEntryAsync}`; `MemoryCommands.{SagaErase(string, string?, ct), LexiconErase(string, Guid?, ct)}`; `CovenantCommands.Erase(string, Guid?, ct)` |
| 19 | `Cli/UX/MemoryErasureRenderer.{WritePreflight(IConsoleDispatcher, MemoryErasurePreflightDto, bool), WriteResult(IConsoleDispatcher, MemoryErasureResultDto)}`; public `MemoryErasureCancellationPayload(string Operation, MemoryReviewStore? Store, Guid? MutationId, bool Cancelled)` on `CliJsonContext`; `MemoryCommands` and `CovenantCommands` constructors gain `IOptions<ArcanumSettings>` |
| 20 | client `{ReleaseSagaErasureAsync, ReleaseLexiconErasureAsync, ReleaseCovenantErasureAsync, GetMemoryErasureStatusAsync, ScrubMemoryErasuresAsync, PrepareMemoryErasureKeyResetAsync, ResetMemoryErasureKeyAsync}`; handlers `SagaRelease(string file, Guid?, string? scope, ct)`, `LexiconRelease`, `CovenantCommands.Release`, `ErasureStatus`, `ErasureScrub`, `ErasureResetKey` |
| 21 | public `CovenantDisclosureStateAlgebra.WeakenToLowerBound(CovenantDisclosureState)`; `CovenantDisclosureSubject(Guid OriginInstallationId, CovenantDisclosureSubjectKind Kind, Guid SubjectId)` with `From(draft)`; `ExternalDisclosureFoldOrigin : byte`; `ExternalDisclosureStateFold.FoldAllUnfoldedAsync(…) → Task<int>` |
| 21 | `ExternalDisclosureStateStore.{ReadAllAsync(SqliteConnection, SqliteTransaction?, ct), ReadAsync(…, CovenantEgressDestination, CovenantDisclosureRevocability, ct), WriteAsync(…, CovenantDisclosureState, DateTimeOffset, ct)}`; test `PreFoldDisclosureHistory` |
| 22 | `BackupRestoreErasureCodes.{KeyMissing, KeyUnavailable, EvidenceUnavailable, EvidenceUnjoinable, VerificationFailed}`; `BackupRestoreErasureEvidenceKind { None = 1, Present = 2, Refused = 3 }`; `BackupRestoreErasureEvidence(Kind, byte[] KeyId, MemoryErasureEvidenceSnapshot Rows, BackupVerifyIssue? Refusal)`, with `ToSummary()` |
| 22 | `MemoryErasureEvidence.ReadSnapshotAsync(…) → Task<MemoryErasureEvidenceSnapshot?>`; `MemoryErasureFingerprintRow(byte[] Fingerprint, MemoryReviewStore Store, byte[] KeyId)`; `MemoryErasureReceiptSubjectRow(Guid MutationId, byte[] SubjectDigest)`; `MemoryErasureEvidenceSnapshot(Fingerprints, Receipts, Subjects)` with `HasRows` and `Empty` |
| 22 | public `BackupRestoreErasureEvidenceStatus { None = 1, Present = 2, Refused = 3 }`; public `BackupRestoreErasureEvidenceSummary(Status, long SagaFingerprints, long LexiconFingerprints, long CovenantFingerprints, long Receipts)`; `BackupRestorePlan.DestinationErasureEvidence = null`; `BackupRestoreService` becomes partial and gains a required `IMemoryErasureKeyProvider`; `BackupCliCatalog.Format(BackupRestoreErasureEvidenceStatus)`; test `MemoryErasureRestoreHarness` |
| 23 | `BackupRestoreSchemaDrainReceipt(int Passes, int BatchesRun, long RowsProcessed)`; `BackupRestoreSchemaDrain.{MaxBatchesPerPass = 64, DrainAsync(…)}`; `GrimoireSchemaInstaller.Chains`; `PrepareStagedGenerationAsync` gains `BackupRestoreErasureEvidence erasure` (13 parameters); harness `CreateArchiveAtAsync` |
| 24 | `BackupRestoreErasureEvidenceApplier.{ApplyAsync(…), FindMatchesAsync(…)}`; `BackupRestoreErasureMatches(SagaIds, LexiconIds, CovenantEntryIds)`; `BackupRestoreErasureApplicationReceipt(…9 fields)` |
| 24 | public `BackupRestoreErasureScrubStatus { Verified = 1, ScrubPending = 2, NotApplicable = 3 }`; public `BackupRestoreErasureApplication(…8 fields)`; `BackupRestoreReconciliation.ErasureApplication = null` |
| 24 | `MemoryErasureEvidence.ReplaceAllAsync(SqliteConnection, SqliteTransaction, MemoryErasureEvidenceSnapshot, ct) → Task<long>`; `BackupRestoreServiceOptions.AfterErasurePurgeForTests`; `ReconcileStagedMemoryEvidenceAsync(SqliteConnection, BackupRestoreErasureEvidence, IReadOnlyList<CovenantDisclosureState>, ct)`; `ReconcileAsync` has 7 parameters; `StageResult.Erasure`; remove `JoinDisclosureAsync` / `JoinedDisclosureBuckets` |
| 26 | public `SagaRetrievalEligibilityClassifier.Classify(SagaMemoryCurationRow)`; `ISagaMemoryStore.ListCurationRowsAsync(string?, Guid?, MemoryScope, int, int, ct)` and `AnyRetrievableAsync(MemoryScope, ct)`; `MemorySearchResultDto(…, SagaMemoryLifecycle? SagaLifecycle = null, SagaRetrievalEligibility? SagaEligibility = null)`; `SagaCommands.DescribeState(SagaMemoryDto)` |
| 27 | `CovenantStoreSql.BindingEpoch(string) → string` and `DetailCuration(bool)`; public `CovenantCurationStateDto(bool IsPinned, bool IsMasked, long Revision)` with `None`; `CovenantDetailDto` and `CovenantDetail` gain `ConfirmedCuration` / `ProposedCuration`; `CovenantEntryPayload.RenderedHash = null`; `CovenantShowPayload` gains its curation members |
| 28 | `RetentionDataClass.MemoryErasureEvidence = 30`; public `DataRetentionMemoryErasureInventory(long Fingerprints, long Receipts, long ReceiptSubjects)`; `DataRetentionStatus.MemoryErasure = null` and `DataRetentionPlan.MemoryErasure = null`; test `MemoryErasureRetainedEvidence` / `MemoryErasureRetainedSnapshot` |
| 29 | test `MemoryStoreFamily` (`[Flags]`), `MemoryStoreSnapshot.{CaptureAsync, QuiesceAsync, AssertOnlyChanged, CountRows}` |

Paths the spine's file list should also gain:
- `Core/Memory/IMemoryErasureRelease.cs` and `Core/Memory/IMemoryErasureAdministration.cs`;
- `Core/Lexicon/LexiconDeletionContracts.cs`;
- `Infrastructure/Data/{MemoryErasureKeyWarmup, SagaErasureWriteGate, SagaRetirementSuppression, MemoryErasureLabels, MemoryErasureFingerprintRelease}.cs`;
- `Infrastructure/Memory/{MemoryErasureProtocol, MemoryErasureRelease}.cs`;
- `Infrastructure/Data/Covenant/{CovenantKeyEpochs, ExternalDisclosureStateReader, ExternalDisclosureStateStore}.cs`;
- `Infrastructure/Backup/{BackupRestoreErasureEvidence, BackupRestoreService.ErasureEvidence, BackupRestoreSchemaDrain, BackupRestoreErasureEvidenceApplier}.cs`;
- `Cli/UX/MemoryErasureRenderer.cs`;
- tests `Support/MemoryErasureRouteDriver.cs`, `Build/MemoryErasureEvidenceDeleterTests.cs`, `Build/MemoryErasureStructuralTests.cs`, `Support/MemoryErasureTestKeys.cs`.

Note: if an existing type, namespace or path the plan names differs in the code, the implementer uses the existing one, records the correction in its report, and keeps the new names above unchanged.

## Task Index and Dependencies

Execute the tasks in numeric order. The dependency column lists every task whose Produces a task consumes. It was corrected after the second critic pass (N9).

| # | Task | Depends on |
|---|---|---|
| 1 | Erasure grammar, identities, wire contracts and error codes | — |
| 2 | Erasure key custody in the OS credential store | 1 |
| 3 | Core schema v13 | — |
| 4 | Shared artifact plan runner and vector-mirror classification | 1 |
| 5 | Finish #225: purge policy truth and plan-closed Annals pins | 4 |
| 6 | Erasure evidence store, two-phase guard, deleter scan, key warm-up | 1, 2, 3 |
| 7 | Saga chokepoint and extraction gates | 4, 6 |
| 8 | Lexicon chokepoint and agent delete refusals | 1, 6, 7 |
| 9 | Covenant canonical v6 schema step | 3 |
| 10 | Covenant curation fixes: binding epoch, replay, reset table list | 9 |
| 11 | Covenant agent chokepoints and staging refusals | 1, 2, 3, 6, 7, 8, 10 |
| 12 | Erase protocol infrastructure: tokens, receipts, exposure, retained copies, WAL scrub, structural pins | 1, 2, 4, 6, 7, 11 |
| 13 | Saga erase: service, routes, route driver, route inventory | 4, 5, 7, 10, 12 |
| 14 | Lexicon erase: service and routes | 3, 8, 12, 13 |
| 15 | Covenant gate: entry-erasure exclusive operation | 9 |
| 16 | Covenant entry erasure: plan, service and routes | 1, 9, 10, 11, 12, 13, 15 |
| 17 | Release verbs and operator re-creation release | 1, 2, 6, 7, 11, 12, 13, 14, 16 |
| 18 | Erasure status, scrub and key reset | 4, 6, 7, 8, 11, 12, 13, 14, 16 |
| 19 | CLI erase verbs and disclosure rendering | 13, 14, 16 |
| 20 | CLI release and `memory erasure` administration verbs | 17, 18, 19 |
| 21 | Disclosure fold: live producer, effective reader, seed | 3, 12 |
| 22 | Restore: destination evidence read and plan reporting | 2, 3, 6, 7, 12, 13 |
| 23 | Restore: staged drain across every tier | 3, 9, 13, 22 |
| 24 | Restore: staged memory-evidence reconciliation | 1, 2, 4, 6, 12, 13, 14, 16, 17, 18, 21, 22, 23 |
| 25 | Purge coordinator per-item classification | 4 |
| 26 | #78: Saga lifecycle in search, list, explain and prune dry-run | — |
| 27 | #78: Covenant show reports exact head identity and curation state | 10, 16 |
| 28 | Lifecycle survival and retention inventory | 2, 3, 6, 8, 10, 13, 14, 16, 17 |
| 29 | Cross-store isolation suite | 13, 14, 16, 17, 28 |
| 30 | Design narrative, doc drift and OATH/Engineering records | all feature tasks |
| 31 | Mutation audit and branch qualification | 1-30 |

## Review Focus

These are input classes the spec implies but that no single feature test naturally exercises. Each line has a test pinned in the task that owns the code.

1. **Campaign identities stored in any spelling.** They may be uppercase-dashed, lowercase-dashed, undashed, or not yet canonicalized by a pending sweep. They must yield the same fingerprint, because the preimage uses GUID bytes. An unparseable stored Campaign must fail closed, never match loosely. *Owner:* Tasks 1, 7 and 24.
2. **Saga content that differs only in trailing whitespace, CRLF versus LF, or Unicode normalization form.**
   - Erase and chokepoint use exact bytes: `café` in NFC and NFD are different identities, and that is documented.
   - Release tries both the exact and the `Trim()` forms.
   - A trailing newline in a `--file` must still release.

   *Owner:* Tasks 1, 7 and 17.
3. **Two first-ever erasures racing on a fresh installation, and two test homes sharing one keychain account.**
   - Exactly one key is created.
   - Rows written under an overwritten key are detected as unverifiable and fail closed.

   *Owner:* Tasks 2 and 6.
4. **A host restart or token expiry between prepare and apply, and between commit and scrub.**
   - Apply after a restart replays by receipt when the erasure committed, and otherwise refuses as `InvalidPreflight`.
   - `/scrub` finishes a pending WAL scrub.

   *Owner:* Tasks 12, 13 and 18.
5. **Large identical-content classes and an extraction page that mixes erased and fresh candidates.**
   - An erase over hundreds of twins stays one transaction, with exact counts.
   - An extraction page with some candidates withheld still inserts the others, advances the cursor, and embeds only the non-withheld candidates.

   *Owner:* Tasks 7 and 13.

---

### Task 1: Erasure grammar, identities, wire contracts and error codes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureIdentity.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureDigestGrammar.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureEffectFacts.cs` (facts records, flags, and the new `MemoryErasureTableCodes` registry)
- Create: `src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureKey.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureServices.cs` (the three store ports, **Prepare and Apply only**; release is Task 17's `IMemoryErasureRelease.cs`, administration is Task 18's `IMemoryErasureAdministration.cs`)
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantErasureContracts.cs` (head expectation, prepare and erase requests, preflight body; **not** `CovenantErasureReleaseRequest`, which Task 17 adds)
- Modify: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs` (new nest `MemoryErasure`; `Lexicon.SuppressedNameRefused`, `Lexicon.PinnedMutationRefused`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs` (`Ports`: the `ICovenantEntryErasureService` port; `Contracts`: `CovenantErasePrepareRequest` and `CovenantEraseRequest`)
- Modify: `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs` (`ResolveStatusCode` arms for all nine new codes; `ResolveStatusCodeDefaultBadRequest` explicit-500 list). Task 1 is the only task that edits the mapper for these codes.
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs` (registrations)
- Modify: `docs/Arcanum.API.md` (§8.23 catalog rows only; no issue numbers)
- Modify: all three benchmark files, because the catalog lists every `.cs` under `src/RetroDownfall.Arcanum.{Core,Infrastructure,Secrets}` and the seven new Core files must be added:
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt` (seven `R\t<path>` lines);
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`, line 69);
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19).
- Test: `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureDigestGrammarTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureContractTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasurePreflightBodyTests.cs` (new; the only body test file in the plan)
- Test (modify): `tests/RetroDownfall.Arcanum.Tests/Api/Primitives/ArcanumErrorMapperTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/ErrorCodeCatalogContractTests.cs`, and `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs` (`Only_the_outbox_worker_and_rebuilder_write_accelerator_state` admits `MemoryErasureEffectFacts.cs` as a declared non-writer)
- Test (run only; it auto-discovers): `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantPublicContractInventoryTests.cs`
- Inventories not touched: no new `OpenConnectionAsync`/`GetOpenCoreConnectionAsync` member, so `ExpectedProductionAcquisitionCount` stays **446** (delta 0); no `UtcInstantText` caller; no hosted-producer capsule change.

**Interfaces:**
- Consumes: existing `MemoryReviewStore` (Covenant 1, Saga 2, Lexicon 3), `SagaMemoryScopeKind` (`Core.Weave`), `CovenantScope`, `LexiconCurationTarget`/`LexiconCurationScope`/`LexiconCurationAnnalHead`/`LexiconCurationSensitivityLabel` (`Core/Lexicon/LexiconCurationContracts.cs`), `CovenantDigest`, `CovenantOperatorPreflightBody`, `OperatorAuthorityContext` (`Core.Intelligence`), `StringOnlyJsonStringEnumConverter<T>`. Nothing from any other task.
- Produces (all spine names verbatim, plus the additions listed):
  - `src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureServices.cs`, **Prepare and Apply only**:
    - `public interface ISagaMemoryErasureService { Task<Result<MemoryErasurePreflightDto>> PrepareAsync(SagaErasePrepareRequest request, CancellationToken cancellationToken); Task<Result<MemoryErasureResultDto>> ApplyAsync(SagaEraseRequest request, OperatorAuthorityContext authority, CancellationToken cancellationToken); }`
    - `public interface ILexiconErasureService { Task<Result<MemoryErasurePreflightDto>> PrepareAsync(LexiconErasePrepareRequest request, CancellationToken cancellationToken); Task<Result<MemoryErasureResultDto>> ApplyAsync(LexiconEraseRequest request, OperatorAuthorityContext authority, CancellationToken cancellationToken); }`
    - `public interface ICovenantEntryErasureService { Task<Result<MemoryErasurePreflightDto>> PrepareAsync(CovenantErasePrepareRequest request, OperatorAuthorityContext authority, CancellationToken cancellationToken); Task<Result<MemoryErasureResultDto>> ApplyAsync(CovenantEraseRequest request, OperatorAuthorityContext authority, CancellationToken cancellationToken); }`
    - No `ReleaseAsync` and no administration interface: Task 17 creates `IMemoryErasureRelease` and Task 18 creates `IMemoryErasureAdministration`, each in its own file. Tasks 13, 14 and 16 implement these interfaces as declared here and add no member.
  - `MemoryErasureDigestGrammar.KeyBytes = 32`, `KeyIdBytes = 16`, `DigestBytes = 32`, and `public static string CanonicalRowId(string rowId)`.
  - `public static class MemoryErasureTableCodes { static IReadOnlyList<string> Tables; static byte For(string table); }`, declared in `MemoryErasureEffectFacts.cs`.
  - `public static class MemoryErasureScrubPendingReasons { static int ToMask(IEnumerable<MemoryErasureScrubPendingReason>); static MemoryErasureScrubPendingReason[] FromMask(int); }` (spine) and `public static class MemoryRetainedLocalCopies { static int ToMask(IEnumerable<MemoryRetainedLocalCopy>); static MemoryRetainedLocalCopy[] FromMask(int); }`. Task 12 uses `MemoryRetainedLocalCopies` and declares no second mask helper.
  - `MemoryErasureKey.FromBytes` is **public** (see notes) and gains `bool HasKeyId(ReadOnlySpan<byte> keyId)`.
  - `CovenantEraseRequest.ToPrepareRequest() → CovenantErasePrepareRequest`.
  - `public sealed record CovenantErasurePreflightBody(CovenantDigest RequestDigest, ulong OperatorAuthorityEpoch, Guid DatasetGeneration, ulong KeyEpoch, ulong KeyReclamationEpoch, bool ReclaimsKey, Guid EntryId, Guid? ConfirmedVersionId, Guid? ProposedVersionId, CovenantDigest EffectDigest, long IssuedAt, long ExpiresAt)`, with `public const int EncodedBytes = 171`, `byte[] Encode()` (172 bytes, `[0] = 0x01`) and `static Result<CovenantErasurePreflightBody> TryDecode(ReadOnlySpan<byte>)`. The digest fields are `CovenantDigest` and the numeric types are exactly `CovenantOperatorPreflightBody`'s (`ulong` epochs, `long` instants). Task 16 reads `body.EffectDigest` as a `CovenantDigest` directly and converts the epochs to `long` with `checked` casts when it builds `CovenantErasureEffectFacts`.

- [ ] **Step 1: Write the failing tests.**

`MemoryErasureDigestGrammarTests` computes every expected value with an **independent reference**: `HMACSHA256.HashData(Key, <hand-assembled bytes>)`, never through the grammar under test. Each test asserts both that the reference equals the pinned hex and that the grammar equals the reference.

```csharp
private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(static value => (byte)value)]; // 00..1F
private const string Campaign = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";   // guid bytes 3F2504E04F8911D39A0C0305E82C3301
private const string Mutation = "6F9619FF-8B86-D011-B42D-00C04FC964FF";   // guid bytes 6F9619FF8B86D011B42D00C04FC964FF
private const string Memory = "7C9E6679-7425-40DE-944B-E07FC1F90AE7";     // canonical row id, 36 ASCII bytes (lp prefix 00000024)
private static byte[] Reference(params byte[][] parts) => HMACSHA256.HashData(Key, [.. parts.SelectMany(static part => part)]);
private static byte[] Label(string ascii) => [.. Encoding.ASCII.GetBytes(ascii), 0x00];
```

Pinned vectors. The preimage is shown after the domain label, in hex:

| Test / vector | Preimage after label | Expected (uppercase hex) |
|---|---|---|
| `Key_id_is_the_first_sixteen_bytes_of_the_labelled_hmac` (`KeyId.v1`) | nothing; take `[..16]` | `D5046E85FE5A45250A18B7BE9B743D3D` |
| `Saga_fingerprint_binds_exact_content_bytes_and_campaign_guid_bytes`: Campaign, `"café"` (NFC) | `02 02 01 <C> 00000005 636166C3A9` | `C1E1157273EAAD6380189566B10E1D310C363C8ED7339E827AF49B0FBEBCF9FC` |
| same test: Campaign, `"café"` (NFD) | `02 02 01 <C> 00000006 63616665CC81` | `2DF703C75F5C1A30CC578D527363C663767D0CB9833820FC7D367C2B036FF310` |
| `Saga_scope_byte_distinguishes_every_scope_kind`: Global NFC | `02 01 00 00000005 636166C3A9` | `0264C8974C6663D3EC3957A012A137EEC48864FB39F5BC07C309B233653216CA` |
| same: Unclassified NFC | `02 00 00 00000005 636166C3A9` | `AB0C9532CB4F9A54E8253379C0EC82DA6DA3857283410F0F64F66D20B55FD717` |
| `Lexicon_fingerprint_trims_and_uppercases_the_name`: Global `" alice "` and `"ALICE"` | `03 01 00 00000005 414C494345` | `995141892E6F98DC4B8CDF90DEC4EB595FCFADD0263C7639BA20FFE364D54D48` |
| same: Campaign `"alice"` | `03 02 01 <C> 00000005 414C494345` | `88534824EA5CCF3A253FF227B811ADCCE5D60D73C9379F2E8E7C615CF636ED26` |
| `Covenant_fingerprint_binds_the_normalized_key_verbatim`: Campaign `"persona.tone"` | `01 02 01 <C> 0000000C 706572736F6E612E746F6E65` | `0CF3673C21260CAC6A7E94A9C9E7AF72CA2CCA3330C95F183A71A263EF48AA46` |
| same: Global | `01 01 00 0000000C 706572736F6E612E746F6E65` | `27B804DB028B681F2221519B3EE9EDCDE29EDD4004A13CF9A1C54411E52A3C25` |
| `Request_digests_bind_non_content_fields_only`: Saga claimless | `02 <M> 00000024 <Memory ASCII> 00` | `9AAAB777F97F4FA9C49802C42C78E8ED5A963A80E5FE9E7545F2A2C0DC62CE13` |
| same: Saga, claim `11111111-2222-4333-8444-555555555555` | `… 01 11111111222243338444555555555555` | `42B74D9E3DF5EAE19D6C9FE5E1721BA4463D514438523561E36AD173FF5CBDB1` |
| same: Lexicon target, Campaign `<C>`, entry `AAAAAAAA-1111-4111-8111-111111111111`, generation 3, head version `22222222-3333-4444-8555-666666666666`, label `33333333-4444-4555-8666-777777777777` at revision 2 | `03 <M> 02 01 <C> <E> 0000000000000003 01 <AV> 01 <LB> 0000000000000002` | `5A12E136E2D2823CE8875B3383AC6652CDD18D1E0477D956A740ECEE02693BEE` |
| same: Lexicon Global, generation 1, no head, no label | `03 <M> 01 00 <E> 0000000000000001 00 00` | `41BE807E5C5D9FA1801EAE1A08AE09C502A5E6F0BB1156910B3C55C8C3497853` |
| same: Covenant Campaign, entry `BBBBBBBB-2222-4222-8222-222222222222`, Confirmed `44444444-5555-4666-8777-888888888888` at lane revision 4, no Proposed | `01 <M> 02 01 <C> <E2> 01 <CV> 0000000000000004 00` | `689486726CE84404F6F3E18F4891456EAE2B93F3221335C1BB5DB845D92AC86E` |
| `Subject_and_content_binding_digests_canonicalize_the_row_id`: Saga subject, three spellings of `Memory` (upper-dashed, lower-dashed, `N`) | `02 00000024 <Memory ASCII>` | `A1748872FE5B50C5CB5449686C607887E2FD1E3A693D95D94240AB635E0BB1FA` |
| same: Lexicon subject `<E>` | `03 00000024 <"AAAAAAAA-1111-4111-8111-111111111111">` | `0E9235CF9C2A86B4326972E124EBC813BB6542C9830D6651D52D41C30E8B2444` |
| same: Saga content binding, NFC `café` | `02 00000024 <Memory ASCII> 00000005 636166C3A9` | `422101860C7015FE6E11C414B2794CC570EA61CA98012E99139FEC7AB6E83CB4` |
| `Effect_digest_sorts_rows_and_aligns_versions`: Saga; rows `["7c9e6679-…-e07fc1f90ae7", "0a1b2c3d-0000-4000-8000-000000000002"]`, versions `[claim 1111…, null]`; targets `[("saga_memories",2),("saga_memory_embeddings",2)]`; labels 0; suppressions 1; flags `Pinned`; evidence `[Known, NotRecorded, Known, ReceiptWindow, NotRecorded]`; mask 223 | `02 00000002 00000024 "0A1B2C3D-…-000000000002" 00000024 "7C9E6679-…" 00000002 00 01 <claim> 00000002 01 0000000000000002 02 0000000000000002 00000000 00000001 01 0103010203 000000DF` | `7CB701C67C4CDDC768EF4D38A8B25028723829C91D71B4F97045C3820388B41A` |
| same: Covenant; row `<E2>`, version `[<CV>]`; targets `[("covenant_versions",3),("covenant_entries",1)]`; flags `ReclaimsKey|Pinned`; facts `(55555555-6666-4777-8888-999999999999, 7, 2)`; evidence `[Known, ReceiptWindow, NotApplicable, NotRecorded, NotRecorded]`; mask 223 | `01 00000001 00000024 <E2 ASCII> 00000001 01 <CV> 00000002 11 0000000000000003 10 0000000000000001 00000000 00000000 05 <G> 0000000000000007 0000000000000002 0102040303 000000DF` | `31ABEBE1DE834B96327C64E2641CD67C90668925FF834B6238F8EB0C7BFF7FB4` |

The effect preimage is ordered as follows:
1. the rows, sorted by ordinal on the canonical id, with each version moved alongside its row;
2. the target list (`u32be` count, then `u8` code and `u64be` rows for each);
3. `u32be` labels, then `u32be` suppressions;
4. the `u8` flags;
5. the Covenant facts (Covenant only);
6. the five evidence bytes;
7. the `u32be` mask.

Every Lexicon target the vectors use passes `LexiconCurationTarget.Validate()`. Fill the fields the preimage does not read (`NormalizedName`, `SnapshotDigest`, `Lifecycle`, the head's `ClaimId`/`Revision`/`Operation`/`ContentHashFormat`/`ContentHash`, the label's `ArtifactContentDigest`/`GenerationProvenance`) with any valid values; the pinned hex does not depend on them.

`Request_digests_bind_non_content_fields_only` also asserts that the durable request digest excludes content (spec §5.2, §19.4 "the durable digest excluding content"):
- Two Lexicon targets that differ **only** in `NormalizedName`, `SnapshotDigest`, `AnnalHead.ContentHash` and `SensitivityLabel.ArtifactContentDigest` give byte-equal `LexiconRequest` digests, and both equal the pinned Campaign vector.
- Two Covenant prepare requests that differ **only** in `Key` give byte-equal `CovenantRequest` digests.
- `SagaRequest` has no content parameter by signature; the claimless and claim vectors above are its pins.
- The Global no-head, no-label Lexicon vector is built from a target whose `AnnalHead` is `new(false, null, null, null, null, null, null)`. It pins that `opt(AnnalVersionId)` is written as `00` when `IsPresent` is false and that `VersionId` is never read then.

Also in `MemoryErasureDigestGrammarTests`:
- `Campaign_identities_in_any_spelling_yield_one_fingerprint`: `Guid.Parse` of the upper-dashed, lower-dashed and `N` spellings of `Campaign` produce byte-identical Saga, Lexicon and Covenant fingerprints. This is Review Focus 1 at the grammar level.
- `Saga_identity_is_exact_bytes`: `"café"` and `"café "` differ, `"a\r\nb"` and `"a\nb"` differ, and NFC and NFD differ. This is Review Focus 2.
- `Invalid_shapes_throw_rather_than_digest`: each case below throws `ArgumentException` (`Assert.ThrowsAny<ArgumentException>`):
  - `ForSaga(Campaign, null|Guid.Empty, …)` and `ForSaga(Global, <C>, …)`;
  - a Lexicon or Covenant identity whose scope is `Unclassified` or `LegacyUnresolved`;
  - an empty `Value`;
  - `"a\uD800b"` (strict UTF-8);
  - row id `"not-a-guid"`;
  - a Covenant Global request with a Proposed expectation;
  - a negative lane revision;
  - `RowIds.Count != Versions.Count`, or duplicate canonical row ids;
  - an evidence count other than 5;
  - an unregistered table name, a negative row count, or the same table listed twice in `Targets` (so a caller that appends the artifact table a second time fails loudly rather than double-counting it; see R9);
  - a Covenant store with null `Covenant` facts, or a Saga store with facts;
  - a flag outside the store's set: Saga `{Pinned}`; Lexicon `{Pinned, GlobalEntryResurfaces}`; Covenant `{Pinned, ReclaimsKey, RetainsCampaignMask, GlobalConfirmedResurfaces}`;
  - a 31-byte key;
  - a Lexicon target that fails `Validate()`;
  - a Lexicon target whose present `AnnalHead.VersionId` is not a GUID (`Validate()` accepts any non-blank text, so the grammar must refuse it; the `FormatException` is wrapped).
- `Key_copies_its_input_exposes_the_key_id_and_zeroes_on_dispose`:
  - `FromBytes` of a buffer that is then overwritten keeps its original `KeyId` (`D5046E85…`).
  - Every instance wrapper equals the static grammar.
  - `HasKeyId` is true for its own id and false for a flipped byte.
  - After `Dispose()`, `Fingerprint(...)` throws `ObjectDisposedException`, and `KeyId` stays readable.
- `Table_codes_are_append_only`: `MemoryErasureTableCodes.Tables` equals this exact list, where code = index + 1:
  - `saga_memories`, `saga_memory_embeddings`, `saga_memory_embeddings_vec`, `saga_memory_attachment_provenance`, `saga_retirement_suppressions`;
  - `lexicon_entries`, `lexicon_fact_attachment_provenance`, `lexicon_annal_fact_provenance`;
  - `annal_claims`, `annal_versions`, `annal_heads`, `annal_dependencies`, `annal_review_events`, `annal_review_decision_receipts`;
  - `artifact_sensitivity`;
  - `covenant_entries` (16), `covenant_versions` (17), `covenant_heads`, `covenant_version_attachment_provenance`, `covenant_mutation_receipts`, `covenant_search_outbox`, `covenant_search_documents`, `covenant_curation_heads`, `covenant_curation_versions`, `covenant_curation_receipts`, `covenant_key_epochs`, `covenant_review_events`, `covenant_review_decision_receipts` (28).

`MemoryErasureContractTests`:
- `Store_codes_are_the_memory_review_codes`: `(byte)MemoryReviewStore.Covenant == 1`, `Saga == 2`, `Lexicon == 3`.
- `Every_erasure_enum_is_string_only_and_has_no_zero_member`: a `[Theory]` over the ten wire enums. For each, `Enum.IsDefined(t, 0)` is false, the type carries `JsonConverter(typeof(StringOnlyJsonStringEnumConverter<>))`, and deserializing `1` through `ArcanumJsonContext.Default.GetTypeInfo(t)` throws `JsonException`.
- `Store_erasure_ports_carry_exactly_prepare_and_apply`: by reflection, each of `ISagaMemoryErasureService`, `ILexiconErasureService` and `ICovenantEntryErasureService` declares exactly two methods, `PrepareAsync` and `ApplyAsync`, with the parameter types and return types listed under **Produces**. This pins R1: release and administration live on their own interfaces.
- `Scrub_reason_mask_uses_one_bit_per_code` (`MemoryErasureScrubPendingReasons`):
  - `ToMask([WalCheckpointPending]) == 1`;
  - `ToMask([FullTextSecureDeleteUnverified, VectorIndexScrubUnverified]) == 6`;
  - `FromMask(7)` returns all three in code order;
  - `FromMask(8)` throws `ArgumentOutOfRangeException`.
- `Retained_copy_mask_uses_one_bit_per_code` (`MemoryRetainedLocalCopies`): all eight values give 255, all but `AuditLog` give 223, `FromMask(223)` returns the seven in code order, and `FromMask(256)` throws `ArgumentOutOfRangeException`.
- `Result_dto_writes_camel_case_names_and_enum_names`: the JSON contains `"outcome":"RowsRemovedScrubPending"` and `"revocation":"NotPerformed"`.

`CovenantErasurePreflightBodyTests`:
- `Round_trip_preserves_every_field` (both heads present; only Confirmed).
- `Encoded_body_is_172_bytes_with_format_byte_one`.
- `Operator_and_erasure_bodies_never_decode_as_each_other`:
  - a `CovenantOperatorPreflightBody.Encode()` payload fails `CovenantErasurePreflightBody.TryDecode` with `MemoryErasure.InvalidPreflight`;
  - an erasure payload fails `CovenantOperatorPreflightBody.TryDecode`.
- `Presence_bytes_above_one_and_wrong_lengths_are_refused`:
  - byte 73 (`ReclaimsKey`), byte 90 or byte 107 set to 2;
  - lengths 171 and 173;
  - `[0] = 0x02`.

Extensions to the existing test classes:
- **`ArcanumErrorMapperTests`:** add `InlineData`:
  - `InvalidPreflight` → 400;
  - `KeyLost`, `StalePlan`, `Lexicon.SuppressedNameRefused`, `Lexicon.PinnedMutationRefused` → 409;
  - `SubjectErased` → 410;
  - `Unavailable`, `KeyUnavailable` → 503;
  - `ErasureIncomplete` → 500.

  Add the fact `ResolveStatusCodeDefaultBadRequest_ErasureIncomplete_Returns500`.
- **`ErrorCodeCatalogContractTests.Catalog_and_constant_table_both_carry_every_code_a_route_emits`:** add nine `InlineData` rows, one per new code spelled in full (`"MemoryErasure.Unavailable"` … `"Lexicon.PinnedMutationRefused"`). The theory asserts the full string is a substring of API §8.23, so the catalog rows in Step 3 must spell every code in full.
- **`ArcanumJsonContextCompletenessTests`:** a new theory, `Memory_erasure_wire_types_have_explicit_source_generation_registrations(Type type)`, in the same `[InlineData(typeof(…))]` shape as `Lexicon_wire_types_have_explicit_source_generation_registrations`. This is the only erasure completeness theory in the plan; Tasks 13, 14, 16 and 18 do not re-create it, and Task 17 adds only `CovenantErasureReleaseRequest`. Its rows:
  - the ten wire enums of `MemoryErasureContracts`;
  - its 21 records: `MemoryExternalExposureChannelDto`, `MemoryErasureExternalExposureDto`, `LexiconErasurePlanFacts`, `CovenantErasurePlanFacts`, `MemoryErasurePlanDto`, `MemoryErasurePreflightDto`, `MemoryErasureLocalResultDto`, `MemoryErasureResultDto`, `MemoryErasureReleaseResultDto`, `MemoryErasureStoreCountsDto`, `MemoryErasureStatusDto`, `MemoryErasureScrubResultDto`, `MemoryErasureKeyResetPreflightDto`, `MemoryErasureKeyResetRequest`, `MemoryErasureKeyResetResultDto`, `SagaErasePrepareRequest`, `SagaEraseRequest`, `SagaErasureReleaseRequest`, `LexiconErasePrepareRequest`, `LexiconEraseRequest`, `LexiconErasureReleaseRequest`;
  - the arrays `MemoryErasureScrubPendingReason[]`, `MemoryExternalExposureChannelDto[]`, `MemoryRetainedLocalCopy[]`, `MemoryErasureNote[]`, `MemoryErasureStoreCountsDto[]`;
  - `CovenantEraseHeadExpectation`, `CovenantErasePrepareRequest`, `CovenantEraseRequest`;
  - `ApiResponse<T>` for the seven response DTOs: preflight, result, release, status, scrub, key-reset preflight, key-reset result.
- **`CovenantArchitectureBoundaryTests.Only_the_outbox_worker_and_rebuilder_write_accelerator_state`:** add `"src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureEffectFacts.cs"` to the expected list as a third declared exception, first in the list because the scan orders paths ordinally (`…Arcanum.Core/` sorts before `…Arcanum.Infrastructure/`), with a comment in the style of the two existing ones: the file names `covenant_search_documents` only as an entry in the append-only effect-digest table-code registry; Core has no SQLite access, so it cannot write the projection. The table name stays a plain literal in the registry. Splitting or encoding it to dodge the scan would hide a real name from the next reader.

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureDigestGrammarTests|FullyQualifiedName~MemoryErasureContractTests|FullyQualifiedName~CovenantErasurePreflightBodyTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~ErrorCodeCatalogContractTests"
```
Expected RED: the test project fails to build with CS0246/CS0117 for `MemoryErasureDigestGrammar`, `MemoryErasureIdentity`, `ErrorCodes.MemoryErasure`, `ISagaMemoryErasureService` and the other new types. Nothing may fail for any other reason. (The allow-list edit is also RED on its own, because the expected list names a file that does not exist yet; the build failure masks it.)

- [ ] **Step 3: Implement.**

The spine signatures are used verbatim. Approach:
- **Strict UTF-8.** One `static readonly UTF8Encoding Strict = new(false, true)`. Big-endian writes use `BinaryPrimitives`, and GUIDs use `Guid.TryWriteBytes(span, bigEndian: true, out _)`. Assemble each preimage in a pooled `ArrayBufferWriter<byte>`, then `HMACSHA256.HashData(key, preimage)`. Zero the preimage after use, since it holds content.
- **`CanonicalRowId`.** Returns `Guid.Parse(rowId).ToString("D").ToUpperInvariant()`. A non-GUID row id throws `ArgumentException` (`FormatException` is wrapped). Every `lp(row id)`, including the Saga request `MemoryId`, uses it.
- **`LexiconRequest`** (spec §5.2 Lexicon row):
  - `u8 ScopeKind || opt(guid Campaign) || guid EntryId || u64be CurationGeneration`;
  - then `AnnalHead.IsPresent ? (01 || guid Guid.Parse(AnnalHead.VersionId!)) : 00`. `VersionId` is read only when `IsPresent` is true (stored Annal ids may be lowercase `D` or `N`, and `Guid.Parse` accepts both);
  - then `SensitivityLabel.IsPresent ? (01 || guid LabelId || u64be ArtifactRevision) : 00`;
  - it calls `target.Validate()` first, and never encodes `NormalizedName`, `SnapshotDigest`, `Lifecycle`, or any content hash.
- **`CovenantRequest`.** Never encodes `Key`.
- **`CovenantErasureContracts.cs`.** Contains `CovenantEraseHeadExpectation`, `CovenantErasePrepareRequest` and `CovenantEraseRequest` exactly as in the spine, plus `ToPrepareRequest()`. `CovenantErasureReleaseRequest` is **not** declared here; Task 17 adds it to this file with its inventory entry and JSON registration. The file also contains `CovenantErasurePreflightBody`, which mirrors `CovenantOperatorPreflightBody` field by field with fixed big-endian widths and the same field types:
  - byte offsets: RequestDigest 1–32, epoch 33, DatasetGeneration 41, KeyEpoch 57, KeyReclamationEpoch 65, ReclaimsKey 73, EntryId 74, Confirmed presence 90 (id 91), Proposed presence 107 (id 108), EffectDigest 124, IssuedAt 156, ExpiresAt 164;
  - every decode failure is `Error(ErrorCodes.MemoryErasure.InvalidPreflight, "This erasure preflight token could not be read.")`.
- **`CovenantPublicContractInventory`.** Add the port `"RetroDownfall.Arcanum.Core.Memory." + nameof(ICovenantEntryErasureService)` with a one-sentence rationale. Add `CovenantErasePrepareRequest` and `CovenantEraseRequest` as `OperatorApi`/`Request` on that port. These are the two `…Request` shapes the inventory's discovery finds; `CovenantEraseHeadExpectation` and the body carry neither suffix and are not declared.
- **`ErrorCodes.MemoryErasure`.** Holds `Unavailable`, `KeyUnavailable`, `KeyLost`, `InvalidPreflight`, `StalePlan`, `SubjectErased` and `ErasureIncomplete`. Each value is `"MemoryErasure.<Member>"`. `Lexicon.SuppressedNameRefused` and `Lexicon.PinnedMutationRefused` go beside the existing `Lexicon.RetiredMutationRefused`.
- **`ArcanumErrorMapper`.** Arms for all nine codes as tested. Add `ErrorCodes.MemoryErasure.ErasureIncomplete` to the explicit list in `ResolveStatusCodeDefaultBadRequest`.
- **API §8.23.** Five rows (400, 409, 410, 500, 503), each with a content-free semantics sentence. Every code is spelled in full (`MemoryErasure.KeyLost`, not `/ KeyLost`), because the catalog theory is a substring match. The two Lexicon codes join the 409 row.
- **`CovenantArchitectureBoundaryTests`.** Already edited in Step 1; nothing else in production changes for it.
- **Benchmark catalog.**
  1. Add seven `R\t<path>` lines in ordinal order, one per new Core file.
  2. Run `git add` on the new files, because the test reads git-tracked files.
  3. Run `GrimoireAdmissionBenchmarkManifestTests`. Its digest assertion reports the new shape digest as the expected value.
  4. Paste that digest into both `AdmissionBenchmarkManifest.cs:19` and `grimoire-admission-workload-v1.json:69`.

- [ ] **Step 4: Run the tests to verify they pass.** Run the Step 2 command, then the wider cluster:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantPublicContractInventoryTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~CovenantErrorContractTests|FullyQualifiedName~CovenantOperatorPreflight|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~InternalsVisibleToInventoryTests"
```
Expected: all green, with zero new build warnings. `CovenantArchitectureBoundaryTests.Only_the_outbox_worker_and_rebuilder_write_accelerator_state` now lists three declared exceptions.

- [ ] **Step 5: Mutation check.** Break each of the following in turn, confirm the named test turns RED, then restore it:

| Break | Test that must turn RED |
|---|---|
| Write one `lp` length little-endian | `Saga_fingerprint_binds_exact_content_bytes…` |
| Encode `lp(NormalizedName)` into `LexiconRequest`, or `lp(Key)` into `CovenantRequest` (the durable digest binding content) | `Request_digests_bind_non_content_fields_only` (the invariance assertions) |
| Parse `AnnalHead.VersionId` whether or not `IsPresent` | `Request_digests_bind_non_content_fields_only` (the Global no-head vector throws) |
| Add a `ReleaseAsync` member to `ICovenantEntryErasureService` | `Store_erasure_ports_carry_exactly_prepare_and_apply` |
| Write GUIDs with `ToByteArray()` (mixed-endian) | `Campaign_identities_in_any_spelling_yield_one_fingerprint` and the Lexicon/Covenant request vectors |
| Drop `.Trim()` in `ForLexicon` | `Lexicon_fingerprint_trims_and_uppercases_the_name` |
| Skip `CanonicalRowId` in `Subject` | `Subject_and_content_binding_digests_canonicalize_the_row_id` |
| Sort row ids without moving versions | `Effect_digest_sorts_rows_and_aligns_versions` |
| Drop the repeated-table check on `Targets` | `Invalid_shapes_throw_rather_than_digest` |
| Use `Encoding.UTF8` instead of strict UTF-8 | `Invalid_shapes_throw_rather_than_digest` |
| Delete the 410 arm | `ArcanumErrorMapperTests` |
| Remove `ErasureIncomplete` from the explicit list | the DefaultBadRequest fact |
| Delete one `[JsonSerializable]` | the completeness theory |
| Relax the erasure decoder's length check | `Operator_and_erasure_bodies_never_decode_as_each_other` |
| Delete the `CovenantEraseRequest` inventory entry | `CovenantPublicContractInventoryTests.Every_public_covenant_wire_type_is_declared` |

- [ ] **Step 6: Commit.**

```bash
git add src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureIdentity.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureDigestGrammar.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureEffectFacts.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureKey.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureContracts.cs src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureServices.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantErasureContracts.cs src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs docs/Arcanum.API.md tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureDigestGrammarTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureContractTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasurePreflightBodyTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Primitives/ArcanumErrorMapperTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs tests/RetroDownfall.Arcanum.Tests/Api/ErrorCodeCatalogContractTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs
git commit -m "feat: define the erasure digest grammar and wire contracts" -m "Keyed, domain-separated preimages for key id, fingerprint, request, subject, effect and content binding, pinned by hand-assembled vectors; store-neutral erasure DTOs, the prepare and apply ports for each store, Covenant erase requests and preflight body, and the MemoryErasure error family." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Erasure key custody in the OS credential store

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureKeyProvider.cs` (spine enums, records, interface)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs` (`IMemoryErasureKeyCreator` and `MemoryErasureKeyring`)
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs` (`MemoryErasureFingerprintKeyAccount = "memory-erasure-fingerprint-key"`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs` (`CollectAccounts` fixed seed)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetTerminalContinuation.cs` (`VerifyIdentitiesRotated`, line 638)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`:
  - a new private `AddMemoryErasureKeyring()`;
  - called from `AddArcanumGrimoireForCli` (line 252), `AddArcanumBackup` (line 654) and `AddArcanumInfrastructure` (line 882, next to `AddCampaignPathIdentity()`);
  - the creator is registered in `AddArcanumInfrastructure` only.
  - `AddArcanumCliClientStack` (line 457) calls both `AddArcanumGrimoireForCli` and `AddArcanumBackup`, and `AddArcanumInfrastructure` calls `AddArcanumBackup` (line 949). Every registration is `TryAdd`, so each container, including the CLI and restore containers Task 22 uses, holds exactly one keyring and one latch.
- Modify: all three benchmark files, adding the two new `src` `.cs` paths (`src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureKeyProvider.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs`) and the new digest:
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt`;
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`);
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19).
- Inventories not touched: no connection acquisition (count stays **446**, delta 0), no `UtcInstantText` caller, no capsule change.
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyringTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyCustodyTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs` (modify: `Catalog_is_closed_to_fixed_configured_and_canonical_mirror_identities`, and `Installation_reset_active_accounts_require_one_canonical_profile_suffix`, where the count goes from 12 to 13)
- Test: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetTerminalContinuationTests.cs` (modify: `An_identity_a_full_reset_must_rotate_that_is_still_present_refuses` becomes a `[Theory]`)
- Ownership: Task 2 is the only task that edits the two reset-credential test files for the erasure key. Task 28 consumes them and does not re-add either case.

**Interfaces:**
- Consumes: `MemoryErasureKey.FromBytes`, `MemoryErasureKey.KeyId`, `MemoryErasureKey.HasKeyId` and `MemoryErasureDigestGrammar.KeyBytes` from Task 1 (so Task 2 depends on Task 1); the existing `IOsCredentialStore`, `InMemoryOsCredentialStore` and `OsCredentialStoreStatus`; `System.Buffers.Text.Base64Url`.
- Produces:
  - Spine-verbatim `MemoryErasureKeyState`, `MemoryErasureKeyProbe`, `MemoryErasureKeyOpenResult(MemoryErasureKeyState State, MemoryErasureKey? Key)`, `MemoryErasureKeyLatch(MemoryErasureKeyState State, byte[]? KeyId)` and `IMemoryErasureKeyProvider { MemoryErasureKeyLatch Latch { get; } MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe); MemoryErasureKey? TryCopyLatched(); }`.
  - `internal interface IMemoryErasureKeyCreator { MemoryErasureKeyOpenResult OpenOrCreate(bool evidenceRowsExist); MemoryErasureKeyOpenResult CreateForReset(); }`.
  - `internal sealed class MemoryErasureKeyring(IOsCredentialStore credentials) : IMemoryErasureKeyProvider, IMemoryErasureKeyCreator, IDisposable`.
  - `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`.
  - Every returned `Key` is a fresh copy that the caller disposes.

- [ ] **Step 1: Write the failing tests.** `MemoryErasureKeyringTests` uses private fakes:
- `CountingCredentialStore` wraps `InMemoryOsCredentialStore`. It counts `TryGet`, `Set` and `Delete`, records the maximum number of concurrent calls, and has an optional read delay.
- `ScriptedCredentialStore` returns queued `OsCredentialStoreResult`s or throws queued exceptions.
- `ForbiddenCredentialStore` fails the test from every member, including `IsAvailable`.

Tests:
1. `Construction_latch_and_copy_perform_no_credential_io`, using `ForbiddenCredentialStore`: `Latch.State == Unresolved`, `Latch.KeyId is null`, and `TryCopyLatched() is null`.
2. `Automatic_open_of_an_absent_account_latches_absent_and_never_writes`: two `OpenExisting(UseLatched)` calls give `State == Absent`, `TryGetCount == 1` and `SetCount == 0`.
3. `Unavailable_failed_or_throwing_store_latches_unavailable_for_automatic_callers`, a `[Theory]` over `Unavailable`, `Failed`, `IOException`, `UnauthorizedAccessException`, `InvalidOperationException` and `NotSupportedException`: `State == Unavailable` and `Key is null`. A second `UseLatched` call does no read (`TryGetCount == 1`).
4. `Operator_reprobe_recovers_and_publishes_present_to_automatic_callers`: after an `Unavailable` script, the store returns a canonical value. `OpenExisting(Reprobe).State == Present`, then `Latch.State == Present`, and `TryCopyLatched()` is not null, with no further read.
5. `Malformed_values_are_reported_and_never_overwritten`, a `[Theory]` over `""`, a padded 44-character value, a 42-character value, a value with `+` or `/`, and non-canonical trailing bits: `OpenExisting(Reprobe)`, `OpenOrCreate(false)` and `CreateForReset()` all give `Malformed`, and `SetCount == 0`.
6. `OpenOrCreate_writes_canonical_unpadded_base64url_reads_back_and_publishes_present`:
   - the stored value has 43 characters and round-trips through `Base64Url.EncodeToString(Base64Url.DecodeFromChars(v))`;
   - `key.KeyId.ToArray()` equals `HMACSHA256.HashData(decoded, "Arcanum.MemoryErasure.KeyId.v1\0"u8)[..16]`, which is independent of the grammar;
   - `Latch.KeyId` equals that value.
7. `OpenOrCreate_refuses_to_mint_when_evidence_rows_exist`: `OpenOrCreate(true)` on an absent account gives `(Absent, null)` and `SetCount == 0`.
8. `A_readback_mismatch_is_unavailable_and_writes_once`: the script returns a different canonical value on read-back. The result is `(Unavailable, null)`, `SetCount == 1` and `Latch.State == Unavailable`.
9. `Concurrent_first_creates_write_exactly_once` (Review Focus 3): eight dedicated threads are released by one `Barrier`, each calling `OpenOrCreate(false)` on one keyring with a 50 ms read delay. Assert `SetCount == 1`, `MaxConcurrentCalls == 1`, that every result is `Present`, and that exactly one distinct `KeyId` hex appears.
10. `Two_keyrings_sharing_one_account_converge_on_the_first_key`: two keyrings over one counting store both call `OpenOrCreate(false)`. Same `KeyId`, and `SetCount == 1`.
11. `An_overwritten_account_keeps_the_latched_key_and_its_key_id_mismatch_is_detectable` (Review Focus 3):
    1. Keyring A creates the key.
    2. The test writes a different canonical value straight into the store.
    3. `A.Latch.State` is still `Present` with the original `KeyId`, and `A.OpenExisting(Reprobe).Key!.HasKeyId(original)` is true, because a Present latch is not re-probed.
    4. A fresh keyring's `OpenExisting(Reprobe).Key!.HasKeyId(original)` is **false**.
12. `CreateForReset_keeps_creates_or_refuses_by_what_the_store_reports`, a `[Theory]` over the store's state:
    - Present: same `KeyId`, `SetCount == 0`;
    - NotFound: `SetCount == 1`, and the result is `Present`;
    - Unavailable: `Unavailable`, with `Set == Delete == 0`;
    - Malformed: `Malformed`, `SetCount == 0`.
13. `Returned_keys_are_independent_copies`: disposing one copy leaves `TryCopyLatched()` usable, and the disposed copy throws `ObjectDisposedException`. After `keyring.Dispose()`, every call returns `(Unavailable, null)` with no I/O.
14. `Host_composition_registers_one_keyring_behind_both_ports`: pre-register `InMemoryOsCredentialStore`, then use `Host.CreateApplicationBuilder()` and `AddArcanumInfrastructure(new ConfigurationBuilder().Build())` (the pattern from `CovenantArchitectureBoundaryTests.Full_host_composition_validates_the_complete_covenant_graph`). Assert `Assert.Same(GetRequiredService<IMemoryErasureKeyProvider>(), GetRequiredService<IMemoryErasureKeyCreator>())`.
15. `Cli_composition_resolves_the_provider_but_never_the_creator`: a `ServiceCollection` with `AddLogging()`, a pre-registered `InMemoryOsCredentialStore` as `IOsCredentialStore` (so the test never reaches the real keychain), and `AddArcanumCliClientStack()`. The provider resolves and is an instance of `MemoryErasureKeyring`, `GetService<IMemoryErasureKeyCreator>()` is null, and exactly one `ServiceDescriptor` has `ServiceType == typeof(IMemoryErasureKeyProvider)`.
16. `Composing_the_host_performs_no_credential_io`: `ForbiddenCredentialStore` is pre-registered, then `AddArcanumInfrastructure` is composed and the provider is built. After resolving both ports, `Latch.State == Unresolved`, `Latch.KeyId is null`, `TryCopyLatched() is null`, and the forbidden store's call counter is 0.

`MemoryErasureKeyCustodyTests`, which uses `ProductionSourceInventory.Sources()`:
- `Only_the_keyring_and_the_reset_paths_name_the_erasure_key_account`: the files naming `MemoryErasureFingerprintKeyAccount` or `memory-erasure-fingerprint-key` are exactly `ArcanumCredentialIdentity.cs`, `MemoryErasureKeyring.cs`, `InstallationResetCredentialCatalog.cs` and `FullInstallationResetTerminalContinuation.cs`.
- `Backup_secret_snapshot_reader_never_reads_the_erasure_key`: `BackupSecretSnapshotReader.cs` names neither.

Changes to the reset tests:
- **Catalog test:** the expected ordered accounts become `campaign-root-identity-key`, `file-encryption-master-key`, `inference-provider-MY_CO-api-key`, `inference-provider-OPENAI-api-key`, `master-api-key`, **`memory-erasure-fingerprint-key`**, `provider-perplexity-api-key`.
- **Reserved-accounts test:** add `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount` to `reservedAccounts`, change `Assert.Equal(12, …)` to 13, and assert that `IsInstallationResetActiveAccount` and `IsGrimoireTransitionJournalAccount` are both false for it.
- **Terminal-continuation theory:** `[InlineData(ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount)]` and `[InlineData(ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount)]`. Each seeds its account, and completion fails with `harness.Deleted` empty.

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureKeyringTests|FullyQualifiedName~MemoryErasureKeyCustodyTests|FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~FullInstallationResetTerminalContinuationTests"
```
Expected RED: the build fails because `MemoryErasureKeyring`, `IMemoryErasureKeyProvider` and `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount` do not exist.

- [ ] **Step 3: Implement.**

`MemoryErasureKeyring` has:
- one `Lock _gate`;
- the cached `byte[]? _key` and `byte[]? _keyId`;
- a `_state` initialised to `Unresolved`;
- `internal const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount` and `internal const int EncodedKeyCharacters = 43`.

All keychain I/O runs under `_gate`, and construction does none.

The probe classifies one `TryGet(Service, Account)`:
- `NotFound` → `Absent`;
- `Ok` with a canonical value → `Present`;
- any other `Ok` value → `Malformed`;
- `Unavailable`, `Failed`, or one of the four exceptions → `Unavailable`.

A canonical value is exactly 43 characters that decode through `Base64Url.TryDecodeFromChars` to 32 bytes and re-encode to the same string. Copy `TryDecodeCanonical` from `BackupRestoreJournalKeyProvider.cs:346`.

| Call | Latch `Present` | Latch not `Present` |
|---|---|---|
| `Latch`, `TryCopyLatched` | copy, no I/O | no I/O |
| `OpenExisting(UseLatched)` | copy, no I/O | probe once only if `Unresolved`; otherwise return the latched state |
| `OpenExisting(Reprobe)` | copy, no I/O | probe and publish |
| `OpenOrCreate(rows)` | copy, no I/O | probe. `Absent` with `rows` returns `(Absent, null)` and writes nothing. `Absent` without `rows` creates. |
| `CreateForReset()` | always probe the store, and publish what it finds | probe. `Absent` creates. `Unavailable` and `Malformed` are returned with no write or delete. |

Create does the following:
1. `RandomNumberGenerator.GetBytes(32)`, then `Set(Service, Account, Base64Url.EncodeToString(bytes))`.
2. Re-probe.
3. It succeeds only if the re-probe is `Present` and `CryptographicOperations.FixedTimeEquals(read, created)`. Any `Set` failure, exception, re-probe failure or mismatch publishes and returns `Unavailable`. It never makes a second `Set`.
4. Zero the `created` and decoded buffers on every path.

Other rules:
- `Dispose` zeroes the cache and latches a terminal disposed flag, which reads as `Unavailable`.
- No logger, and so no log lines.
- No start-up warm-up here. Task 6's `MemoryErasureKeyWarmup.RunAsync` calls `OpenExisting(Reprobe)` once before readiness when any fingerprint row exists. That uses the members above and needs nothing new from this task.
- DI:
  - `AddMemoryErasureKeyring()` does `services.TryAddSingleton<IOsCredentialStore>(TestCredentialStorePolicy.Create)`;
  - it does `services.TryAddSingleton(static sp => new MemoryErasureKeyring(sp.GetRequiredService<IOsCredentialStore>()))`;
  - it does `services.TryAddSingleton<IMemoryErasureKeyProvider>(static sp => sp.GetRequiredService<MemoryErasureKeyring>())`;
  - `AddArcanumInfrastructure` alone adds `services.TryAddSingleton<IMemoryErasureKeyCreator>(static sp => sp.GetRequiredService<MemoryErasureKeyring>())`.
- `VerifyIdentitiesRotated` loops over both fixed accounts with the existing refusal texts and codes.
- Add the account to the seed at `InstallationResetCredentialCatalog.cs:36-42`, and extend the comment above the seed to name it.

- [ ] **Step 4: Run the tests to verify they pass.** Run the Step 2 command, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~RetroDownfall.Arcanum.Tests.InstallationReset|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~CampaignRootIdentityKeyProviderTests|FullyQualifiedName~CovenantRetainedEvidence|FullyQualifiedName~BackupServiceTests|FullyQualifiedName~InternalsVisibleToInventoryTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~EnvironmentIsolationContractTests"
```

- [ ] **Step 5: Mutation check.** Break each of the following in turn, confirm the named test turns RED, then restore it:

| Break | Test that must turn RED |
|---|---|
| Move the create outside `_gate` | test 9 (`SetCount > 1` or `MaxConcurrentCalls > 1`) |
| Ignore `evidenceRowsExist` | test 7 |
| Skip the `FixedTimeEquals` read-back | test 8 |
| Make `UseLatched` always probe | tests 2 and 3 (`TryGetCount == 2`) |
| Make `Reprobe` use the latch | test 4 |
| Re-probe a `Present` latch | test 11 (latched `HasKeyId` true) |
| Overwrite a malformed value in `CreateForReset` | test 5 |
| Remove the catalog seed entry | the catalog test |
| Drop the account from `VerifyIdentitiesRotated` | its theory row |
| Register the creator in `AddArcanumGrimoireForCli` | test 15 |
| Register the provider in `AddArcanumBackup` with `AddSingleton` instead of `TryAddSingleton` | test 15 (two `IMemoryErasureKeyProvider` descriptors) |

- [ ] **Step 6: Commit.**

```bash
git add src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureKeyProvider.cs src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetTerminalContinuation.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyringTests.cs tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyCustodyTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetTerminalContinuationTests.cs
git commit -m "feat: hold the erasure key in the OS credential store" -m "One latched keyring per process: automatic callers read the latch, operator calls re-probe, creation happens once under a lock with read-back, and a full installation reset removes the key." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Core schema v13

**Files:**
- Create (head):
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_fingerprints.sql`
  - `.../Tables/memory_erasure_receipts.sql`
  - `.../Tables/memory_erasure_receipt_subjects.sql`
  - `.../Triggers/memory_erasure_receipts_guard_update.sql`
- Create, under `.../Data/Schema/Transitions/V13/`, in this exact order:
  - `010_memory_erasure_fingerprints.sql`
  - `020_memory_erasure_receipts.sql`
  - `030_memory_erasure_receipt_subjects.sql`
  - `040_memory_erasure_receipts_guard_update.sql`
  - `050_disclosure_subject_state_unfolded_index.sql`
  - `060_lexicon_fts_secure_delete.sql`
  - `070_lexicon_fts_optimize.sql`
- Modify the four head files whose meaning changes (spec §5.8). Task 3 owns all four; Task 21 edits no `.sql` file:
  - `.../Tables/disclosure_subject_state.sql` (append the partial index and its comment; correct the leading comment);
  - `.../FullTextSearch/lexicon_fts.sql` (leading comment only);
  - `.../Tables/external_disclosure_state.sql` (leading comment only);
  - `.../Tables/disclosure_subject_aggregates.sql` (leading comment only).
- Modify: `.../Data/Schema/GrimoireSchemaVersionChains.cs`:
  - `CoreSchemaVersion` becomes 13;
  - remarks paragraphs for v12 and v13;
  - a `SourcePins[(Core, 13)]` entry with a provenance comment;
  - no `Backfills` entry.
- Modify: `.../Data/Schema/CoreGrimoireSchemaDataInitializer.cs` (new `ConvergeLexiconSecureDeleteAsync`, called last in `InitializeAsync`)
- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTwelveFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionElevenFixture.cs` and `CoreSchemaVersionTenFixture.cs`, rebasing `Objects` on `CoreSchemaVersionTwelveFixture.Objects`
- Test (new): `tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryErasureSchemaEvolutionTests.cs` and `.../Data/Schema/MemoryErasureReceiptGuardTests.cs`
- Test (modify):
  - `.../Data/Schema/GrimoireSchemaSourceFingerprintTests.cs`
  - `.../Data/Schema/GrimoireSchemaVersionChainTests.cs` (12 becomes 13)
  - `.../Data/Schema/GrimoireSchemaTransitionResourceTests.cs` (`(Core, 13)` and seven names inserted right after `"annal_review_events_head_update"`)
  - `.../Data/Schema/GrimoireSchemaCatalogTests.cs`
  - `.../Benchmarks/GrimoireAdmissionBenchmarkManifestTests.cs` (451 becomes **462**)
- Modify: all three benchmark files, adding the 11 new `.sql` paths (4 head, 7 transition) and the new digest. Task 3 adds no `src` `.cs` file.
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt`;
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`);
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19).
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated: the initializer's new database calls are reached from `GrimoireDatabaseHostedService`, `GrimoireSchemaTransitionHostedService` and `BackupRestoreService.RestoreAsync`)
- Inventories not touched: `ConvergeLexiconSecureDeleteAsync` runs on the initializer's existing connection and transaction, so the acquisition count stays **446** (delta 0). The new tables have no instant column, so `UtcInstantColumnInventoryTests` stays at 118/13.

**Interfaces:**
- Consumes: nothing from Tasks 1 and 2. Store codes 1, 2 and 3 are `MemoryReviewStore`. The reason-mask bits are 1 = `WalCheckpointPending`, 2 = `FullTextSecureDeleteUnverified`, 4 = `VectorIndexScrubUnverified`.
- Produces:
  - the tables `memory_erasure_fingerprints`, `memory_erasure_receipts` and `memory_erasure_receipt_subjects`;
  - the indexes `idx_memory_erasure_fingerprints_store_key`, `idx_memory_erasure_receipts_store_key`, `idx_memory_erasure_receipts_pending`, `idx_memory_erasure_receipt_subjects_digest` and `idx_disclosure_subject_state_unfolded`;
  - the trigger `memory_erasure_receipts_guard_update` and its abort message `'memory_erasure_receipts permits only clearing the WAL checkpoint reason and moving Pending to Verified once no reason remains.'`;
  - `GrimoireSchemaVersionChains.CoreSchemaVersion == 13`;
  - `CoreSchemaVersionTwelveFixture.ChainSet()`, which later tasks use to write "while v12" and "upgrade window" tests;
  - `lexicon_fts` with `secure-delete = 1` on every Core install.

- [ ] **Step 1: Write the failing tests.**

Create `CoreSchemaVersionTwelveFixture`, modelled on `CoreSchemaVersionElevenFixture`:
- `PublishedFingerprint = "616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B"`.
- `Objects` is `GrimoireSchemaCatalog.CoreObjects` without the names starting `"memory_erasure_"`. Three objects are replaced by `private const string` byte-exact copies of their `git show HEAD:src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/<name>.sql` text, each with `.ReplaceLineEndings("\n")`:
  - `disclosure_subject_state` (`DisclosureSubjectStateSql`, sha256 `8e1240b3…`): v13 appends an index and corrects the comment;
  - `external_disclosure_state` (`ExternalDisclosureStateSql`, sha256 `6e6e8e9b…`): comment only;
  - `disclosure_subject_aggregates` (`DisclosureSubjectAggregatesSql`, sha256 `a7d31c7f…`): comment only.

  The two comment-only copies are required. The normalized v12 and v11 fingerprints ignore comments, but the Five, Four, Three, Two and One fixtures hash **raw** bytes (`ComputeRawSourceFingerprint`), and they inherit these three objects through Ten → Seven → Six. So without the copies the head comment edits in Step 3 would move `Version_five_reconstruction_matches_the_pinned_fingerprint`. `lexicon_fts` needs no copy: `CoreSchemaVersionTenFixture` already freezes it (`LexiconFtsSql`), which is how its v11 comment edit was absorbed.
- `ChainSet()` is version 12, taking the steps with `ToVersion <= 12`.

Rebase the Eleven and Ten fixtures' `Objects` on `CoreSchemaVersionTwelveFixture.Objects`, keeping their existing filters and switches. Then add to `GrimoireSchemaSourceFingerprintTests`, in this assertion order:

```csharp
[Fact]
public void Version_twelve_reconstruction_matches_the_pinned_fingerprint()
{
    Assert.Equal(CoreSchemaVersionTwelveFixture.PublishedFingerprint, CoreSchemaVersionTwelveFixture.Fingerprint);
    GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);
    Assert.Equal(CoreSchemaVersionTwelveFixture.Fingerprint, core.SourceDefinitionFingerprintFor(12));
    Assert.True(core.TryGetStep(12, out GrimoireSchemaVersionStep step));   // keyed by FromVersion
    Assert.Equal(13, step.ToVersion);
    Assert.Null(step.Backfill);
}
```

The other schema test changes:
- In `GrimoireSchemaVersionChainTests.The_shipped_head_versions_are_the_ones_this_binary_declares`, change `12` to `13`.
- In `GrimoireSchemaTransitionResourceTests`, add `(Core, 13)` and the statement names `"memory_erasure_fingerprints", "memory_erasure_receipts", "memory_erasure_receipt_subjects", "memory_erasure_receipts_guard_update", "disclosure_subject_state_unfolded_index", "lexicon_fts_secure_delete", "lexicon_fts_optimize"`.
- Add `GrimoireSchemaCatalogTests.Erasure_evidence_is_installed_with_the_core_tables`: the three tables are `Core`/`Tables`, and the guard is `Core`/`Triggers`.

`MemoryErasureSchemaEvolutionTests` uses `EvolutionScratchDatabase` and plain `[Fact]`, with `InstallAsync` and `DefinitionsAsync` helpers copied from `LexiconCurationEvolutionTests`:
- **`Fresh_and_evolved_version_thirteen_catalogs_have_identical_definitions`.** Fresh means `GrimoireSchemaVersionChains.Default`. Evolved means `CoreSchemaVersionTwelveFixture.ChainSet()` (Healthy, 12) and then `Default` (Healthy, 13). The key sets must be equal, every normalized definition must be equal, and `fresh.Keys` must contain the three tables, the trigger and the five indexes.
- **`Version_thirteen_evolves_in_one_step_with_no_sweep_and_empty_evidence_tables`.** After the evolve:
  - `SELECT count(*) FROM grimoire_schema_transitions WHERE FamilyCode = 0 AND TransactionTierCode = 0` is 0;
  - each of the three tables has 0 rows.
- **`Lexicon_residue_from_before_version_thirteen_leaves_no_token_after_the_upgrade`** (spec §19.2.2 at the schema level):
  1. Install the Twelve fixture.
  2. Run `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0);`. A pre-v13 build never enabled it, and this build's initializer already did during the fixture install.
  3. In a `using` block, build `LexiconMidUpgradeCompatibilityTests.CreateService(SagaMemoryMidUpgradeWriteTests.CreateContext(file))`. Go through that helper and never construct `LexiconService` directly: Task 8 adds a required `IMemoryErasureKeyProvider` constructor parameter and updates `CreateService`, so this test needs no later edit.
  4. `UpsertAsync("Residue", "Concept", ["holds zqxerasuremarkerqz"], LexiconScope.Global)`.
  5. `ShowExactAsync(Global, "Residue", null)`, then `CorrectAsync(target, new("Concept", ["replaced fact"]), null)`, then show again, then `RetireAsync(target, null)`. That is three FTS transactions, fewer than the automerge threshold.
  6. **Positive control:** `ShadowCountAsync(marker) > 0`, where `ShadowCountAsync` counts `lexicon_fts_data WHERE instr(block, CAST($m AS BLOB)) > 0` plus `lexicon_fts_idx WHERE instr(term, CAST($m AS BLOB)) > 0`.
  7. Dispose the service context and install `Default` (Healthy, 13).
  8. Assert `ShadowCountAsync(marker) == 0`, `SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete'` is 1, and `SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'zqxerasuremarkerqz'` is 0.
- **`Core_install_converges_lexicon_secure_delete`.** A fresh `Default` install reads `secure-delete` as 1. Set it to 0, re-install `Default` (the Converge path runs `FinalizeRunAsync`), and it reads 1 again. The source of `CoreGrimoireSchemaDataInitializer.cs` does not contain `"optimize"`.

`MemoryErasureReceiptGuardTests` runs on a fresh `Default` scratch database. The helper `InsertReceiptAsync(mutationId, state, mask)` inserts with raw SQL (no production writer exists until Task 12): store 2, `randomblob` digests, counts 1/1/0/0, evidence 1/3/1/2/3, mask 223. The scratch connection must report `PRAGMA foreign_keys` as 1. Tests:
- `A_wal_only_receipt_becomes_verified_when_the_wal_reason_clears`: `(1, 1)` is updated to `(2, 0)` and is accepted.
- `Clearing_the_wal_reason_keeps_a_non_upgradable_receipt_pending`: `(1, 3)` is updated to `(1, 2)` and is accepted.
- `A_non_upgradable_reason_can_never_reach_verified`, a `[Theory]` over masks 2, 3, 4, 6 and 7, each with `SET ScrubStateCode = 2, ScrubPendingReasonMask = ScrubPendingReasonMask & ~1`. Each throws a `SqliteException` whose message contains the guard text, and the row is unchanged.
- `A_reason_can_never_be_added_or_a_non_wal_reason_cleared`, a `[Theory]` over masks 3→7, 3→1 and 1→3 with the state held at 1, plus 2→0 with the state held at 1. Each is refused with the guard text.
- `Verified_never_returns_to_pending`: refused with the guard text.
- `Every_other_column_is_immutable`, a `[Theory]` over the 15 non-scrub columns, each given a valid replacement value. Each is refused with the guard text.
- `Table_checks_refuse_malformed_rows`, a `[Theory]` over:
  - a 31-byte fingerprint;
  - `StoreCode` 4;
  - a 15-byte `KeyId`;
  - a lowercase `MutationId`, and an `N`-format `MutationId`;
  - `ErasedItemCount` 0, and `RemovedRowCount` 0;
  - evidence code 5;
  - state 2 with mask 1, and state 1 with mask 0;
  - mask 8.

  Each fails with `SQLITE_CONSTRAINT_CHECK`.
- `Deleting_a_receipt_cascades_to_its_subjects_and_fingerprints_have_no_delete_guard`: subjects count 0 after the delete; a `DELETE FROM memory_erasure_fingerprints` succeeds.

- [ ] **Step 2: Run the tests to verify they fail.** **Do not edit any head file before this run.**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireSchemaSourceFingerprintTests|FullyQualifiedName~GrimoireSchemaVersionChainTests|FullyQualifiedName~GrimoireSchemaTransitionResourceTests|FullyQualifiedName~GrimoireSchemaCatalogTests|FullyQualifiedName~MemoryErasureSchemaEvolutionTests|FullyQualifiedName~MemoryErasureReceiptGuardTests"
```

Expected RED:
- `Version_twelve_…` fails **at `TryGetStep(12)`**. Its first two assertions pass, which confirms the pin against today's head. If the first assertion fails instead, stop: the pin is wrong, so recompute it and change no head file.
- The head version is 12, not 13.
- There are no v13 transition names.
- The evolution tests fail with `SchemaVersion 12 ≠ 13` or with a missing table.
- The receipt tests fail with `no such table: memory_erasure_receipts`.
- `Current_pre_review_trees_…`, `Version_ten_…` and `Version_five_…` stay **green**, which proves the rebase before any head file changes.

- [ ] **Step 3: Implement.** Each new head file also becomes its V13 transition file with byte-identical statement text, written `CREATE … IF NOT EXISTS`. Each new head file starts with a short comment explaining what the object is for and why it has no timestamp. The Step 1 tests pin every rule below.

**`memory_erasure_fingerprints`** (`010`): spec §5.3's table and `idx_memory_erasure_fingerprints_store_key`, copied verbatim.

**`memory_erasure_receipts`** (`020`): 17 columns, all `NOT NULL`, no timestamp:

| Column | Type and `CHECK` |
|---|---|
| `MutationId` | `TEXT PRIMARY KEY`; `= upper(MutationId)`, length 36, `'-'` at positions 9, 14, 19 and 24 |
| `StoreCode` | `INTEGER`, `IN (1, 2, 3)` |
| `KeyId` | `BLOB`, length 16 |
| `RequestDigest`, `EffectDigest` | `BLOB`, length 32 each |
| `ErasedItemCount`, `RemovedRowCount` | `INTEGER`, `>= 1` |
| `RemovedLabelCount`, `RemovedRetirementSuppressionCount` | `INTEGER`, `>= 0` |
| `AuthorshipEvidenceCode`, `ContextEvidenceCode`, `EmbeddingEvidenceCode`, `BackupEvidenceCode`, `OtherExternalEvidenceCode` | `INTEGER`, `IN (1, 2, 3, 4)` |
| `RetainedCopiesMask` | `INTEGER`, `>= 0` |
| `ScrubStateCode` | `INTEGER`, `IN (1, 2)` (1 Pending, 2 Verified) |
| `ScrubPendingReasonMask` | `INTEGER`, `BETWEEN 0 AND 7` |

Plus one table-level biconditional: `CHECK ((ScrubStateCode = 1 AND ScrubPendingReasonMask <> 0) OR (ScrubStateCode = 2 AND ScrubPendingReasonMask = 0))`. Indexes: `idx_memory_erasure_receipts_store_key ON (StoreCode, KeyId)`, and the partial `idx_memory_erasure_receipts_pending ON (ScrubStateCode) WHERE ScrubStateCode = 1`.

**`memory_erasure_receipt_subjects`** (`030`): `MutationId TEXT NOT NULL REFERENCES memory_erasure_receipts(MutationId) ON DELETE CASCADE`, `SubjectDigest BLOB NOT NULL` of length 32, `PRIMARY KEY (MutationId, SubjectDigest)`, and `idx_memory_erasure_receipt_subjects_digest ON (SubjectDigest)`.

**Guard rule, `memory_erasure_receipts_guard_update`** (`040`). One `BEFORE UPDATE` trigger on `memory_erasure_receipts`. It raises `RAISE(ABORT, '<the message under Produces>')` when **any** of these holds:
- any of the 15 non-scrub columns `IS NOT` its `OLD` value;
- `NEW.ScrubPendingReasonMask NOT IN (OLD.ScrubPendingReasonMask, OLD.ScrubPendingReasonMask & ~1)`, so the only mask change is clearing the WAL bit;
- `NEW.ScrubStateCode IS NOT OLD.ScrubStateCode AND NOT (OLD.ScrubStateCode = 1 AND NEW.ScrubStateCode = 2 AND NEW.ScrubPendingReasonMask = 0)`, so the only state change is Pending to Verified with nothing left pending.

The trigger fires before the table `CHECK`, so a refused update always reports the guard's message. The tests assert that message.

**Disclosure index** (`050`, and appended to `Tables/disclosure_subject_state.sql` under its own one-line comment):

```sql
CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_unfolded
    ON disclosure_subject_state(OriginInstallationId, SubjectKind, SubjectId)
    WHERE LastFoldedOrdinal < LastAllocatedOrdinal;
```

The remaining V13 statements:
- `060_lexicon_fts_secure_delete.sql`: `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 1);`
- `070_lexicon_fts_optimize.sql`: `INSERT INTO lexicon_fts(lexicon_fts) VALUES('optimize');`

`ConvergeLexiconSecureDeleteAsync(SqliteConnection, SqliteTransaction, CancellationToken)` follows `CovenantAcceleratorSchemaDataInitializer`. It runs the same `INSERT`, then reads `SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';`. If the value is not 1, it throws `InvalidOperationException("The Lexicon FTS5 index did not report secure-delete after it was enabled.")`. It never runs `'optimize'`. `lexicon_fts` exists at every Core version, so the call is unconditional.

**Head comment corrections (spec §5.8).** Edit only each file's leading comment block, above the `CREATE` keyword. Never edit a comment inside a statement body: SQLite keeps inline comments in `sqlite_master`, so an inline edit would make a fresh v13 catalog's stored text differ from an evolved one's. The normalized fingerprint ignores comments either way; the Twelve fixture copies keep the raw v1–v5 pins still. What each comment must now say:
- **`lexicon_fts.sql`:** keep the existing warning against FTS5's unfiltered `'rebuild'`. Add that from Core 13 the index runs with `secure-delete = 1`, which V13/060 sets and `CoreGrimoireSchemaDataInitializer` converges and reads back on every install. A deleted or replaced row therefore leaves no token in `lexicon_fts_data` or `lexicon_fts_idx`. V13/070 ran `'optimize'` once to merge away residue from pre-v13 deletes, and nothing runs it again.
- **`disclosure_subject_state.sql`:** replace the sentence that gives compaction the folded watermark. `LastFoldedOrdinal` is now the disclosure fold's watermark: the live journal fold advances it in each acknowledged receipt's own transaction, and restore staging advances it when it folds a staged tail. A subject whose watermark trails `LastAllocatedOrdinal` holds receipts written before the live fold existed. Readers count that tail as a lower bound, and the new partial index finds it.
- **`external_disclosure_state.sql`:** replace "Subject aggregates join into it once, on terminal folding". The live fold now increments the matching bucket once per acknowledged receipt, in that receipt's transaction, and restore staging joins into it. Receipts in an unfolded tail are not counted here yet, so readers use the effective read, which adds them as `LowerBound`. Keep the semilattice and single-encoding paragraph unchanged.
- **`disclosure_subject_aggregates.sql`:** keep the key-space bound. Add that no live path writes this table and nothing joins it into `external_disclosure_state`, because the live fold already counted each receipt there. A later compaction that folds detail into this table must therefore not join it into `external_disclosure_state` again.

For the benchmark:
1. Add the 11 `.sql` paths to the catalog in ordinal order.
2. `git add` them.
3. Take the digest from the test failure, and set the `.sql` count to 462.

For the hosted-producer capsules:

```bash
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
```

Review the TSV diff. Only `CoreGrimoireSchemaDataInitializer` rows under the three producers named above may change.

- [ ] **Step 4: Run the tests to verify they pass.** Run the Step 2 command, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~RetroDownfall.Arcanum.Tests.Data.Schema|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~IdentitySpellingGuardTests|FullyQualifiedName~CovenantDisclosureJournalTests|FullyQualifiedName~DataRetentionAnnalsErasureTests|FullyQualifiedName~DataRetentionQuarantineRecoveryTests|FullyQualifiedName~BackupRestoreProtectedStatePurgeTests|FullyQualifiedName~LexiconCorrectionTests"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
```

Expected:
- `Version_five_reconstruction_matches_the_pinned_fingerprint`, `Version_ten_…` and `Current_pre_review_trees_…` stay green after the four head comment edits, because the Twelve fixture carries the HEAD bytes.
- `UtcInstantColumnInventoryTests` is unchanged at 118/13, because the new tables have no instant columns.
- `IdentitySpellingGuardTests` is unchanged, because `MutationId` is checked by the table and is not in the governed family.
- `SagaCurationEvolutionTests` is unaffected, because no new name matches its `LIKE` filters.

- [ ] **Step 5: Mutation check.** Break each of the following in turn, confirm the named test turns RED, then restore it:

| Break | Test that must turn RED |
|---|---|
| Change one hex digit of the `(Core, 13)` pin | `Version_twelve_…`, and every evolution test (`SourceDefinitionMismatch`) |
| Replace V13/070 with `SELECT 1;` | `Lexicon_residue_…` |
| Remove the initializer call | `Core_install_converges_lexicon_secure_delete` |
| Drop `AND NEW.ScrubPendingReasonMask = 0` from the guard | `A_non_upgradable_reason_can_never_reach_verified` (the message is no longer the guard's) |
| Drop the `& ~1` arm | `Clearing_the_wal_reason_keeps_…` |
| Remove the V13/050 index | `Fresh_and_evolved_…` |
| Remove the table's state/mask `CHECK` | `Table_checks_refuse_malformed_rows` |
| Revert the Ten fixture rebase | `Version_ten_…` |
| Drop the `ExternalDisclosureStateSql` (or `DisclosureSubjectAggregatesSql`) copy from the Twelve fixture, after the head comment edit | `Version_five_reconstruction_matches_the_pinned_fingerprint` (the raw v5 hash moves) |

- [ ] **Step 6: Commit.**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_fingerprints.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_receipts.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_receipt_subjects.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/memory_erasure_receipts_guard_update.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/010_memory_erasure_fingerprints.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/020_memory_erasure_receipts.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/030_memory_erasure_receipt_subjects.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/040_memory_erasure_receipts_guard_update.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/050_disclosure_subject_state_unfolded_index.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/060_lexicon_fts_secure_delete.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/070_lexicon_fts_optimize.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/disclosure_subject_state.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/external_disclosure_state.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/disclosure_subject_aggregates.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/FullTextSearch/lexicon_fts.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTwelveFixture.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionElevenFixture.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTenFixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryErasureSchemaEvolutionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryErasureReceiptGuardTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaSourceFingerprintTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaCatalogTests.cs tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkManifestTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: add Core schema version 13 erasure evidence" -m "Content-free fingerprint, receipt and subject tables with a two-change receipt guard, the unfolded-disclosure index, and Lexicon FTS secure delete with a one-time merge; corrects the Lexicon and disclosure head comments; pinned from the version-12 reconstruction and declaring no sweep." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Shared artifact plan runner and vector-mirror classification

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaVectorMirror.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantArtifactPlanRunner.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs`: `CovenantArtifactPurgeTarget` gets a shared private predicate and `CountBy`; `ApplyPlanAsync`'s projection and artifact loop becomes one runner call; the private `TableExistsAsync` is deleted.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStatePurger.cs` (`ApplyPlanAsync`)
- Modify the eight flag-gated mirror sites (each is an `if (availability.IsVecAvailable)` today): `Data/SagaMemoryStore.cs` `InsertCoreAsync` (:238), `DeleteAsync` (:522), `DeleteAllAsync` (:608); `Data/SagaMemoryStore.Curation.cs` `RetireAsync` (:172), `ReinstateAsync` (:405), `CorrectAsync` (:649); `Memory/SagaMemoryReviewService.cs` private `CorrectAsync` (:819) and `RetireAsync` (:873). All under `src/RetroDownfall.Arcanum.Infrastructure/`.
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs` (the "when sqlite-vec is available" remarks state the new rule)
- Modify: `docs/Arcanum.DESIGN.md` §10.17 paragraph "The vector mirrors are inside the erasure boundary"
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs` (keep the `WeaveIndexAvailability` it builds as `internal WeaveIndexAvailability VectorAccelerator`)
- Modify (closed inventories):
  - the three benchmark files: `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt` (+2 `src/` files), `grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`, line 69) and `AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19);
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerate: the kernel, the purger and `SagaMemoryStore` have capsule rows).
- Test (new): `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantArtifactPlanRunnerTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/SagaVectorMirrorTests.cs`
- Test (added cases): `tests/RetroDownfall.Arcanum.Tests/Memory/SagaMemoryReviewServiceTests.cs` (two), `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreProtectedStatePurgeTests.cs` (one)

**Interfaces:**
- Depends on: Task 1 (`MemoryErasureTableCount`). The Task Index's "—" understates this; the file order already satisfies it.
- Consumes: `MemoryErasureTableCount(string Table, long Rows)` (Task 1).
- Produces:
  - `internal enum SagaVectorMirrorKind { Absent = 1, PlainTable = 2, LegacyVirtualTable = 3 }`
  - `internal static class SagaVectorMirror`, every method on the caller's `(DbConnection connection, DbTransaction? transaction, …, CancellationToken cancellationToken)`:
    - `ClassifyAsync() → Task<SagaVectorMirrorKind>` (for `SagaStorageKeys.VectorTable`) and `ClassifyAsync(string table)` (any conditional plan mirror; `table` is a code literal)
    - `DeleteAsync(string memoryId) → Task<long>`, `DeleteAllAsync() → Task<long>`
    - `UpsertAsync(string memoryId, ReadOnlyMemory<float> vector, bool vecAvailable) → Task<SagaVectorMirrorKind>`
  - `CovenantArtifactPurgeTarget.CountBy(string parameter) → string`
  - `internal enum CovenantArtifactPlanMode { Count = 1, Delete = 2 }`
  - `internal sealed record CovenantArtifactPlanTally(IReadOnlyList<MemoryErasureTableCount> Targets, SagaVectorMirrorKind VectorMirror, long ArtifactRows)` (R9):
    - `Targets` is every projection in plan order followed by the artifact target, **each table exactly once**. A skipped target reports `0`.
    - When the plan has an artifact, `Targets[^1]` is that artifact and `ArtifactRows == Targets[^1].Rows`. A plan without one (only `IdempotencyClaim` today) reports only its projections and `ArtifactRows == 0`.
    - `VectorMirror` is the plan's single conditional mirror, or `Absent`.
    - Consumers compute `RowsToRemove` as `Σ Targets.Rows` and never append the artifact table again. Labels and retirement pairs are not targets.
  - `CovenantArtifactPlanRunner.RunAsync(SqliteConnection connection, SqliteTransaction? transaction, SensitiveArtifactKind kind, string artifactKey, CovenantArtifactPlanMode mode, CancellationToken cancellationToken) → Task<CovenantArtifactPlanTally>`. It never begins, authorizes, commits, touches `artifact_sensitivity`, or catches `SqliteException`.

- [ ] **Step 1: Write the failing tests**

Two mirror shapes are used throughout:
- **Plain:** `CREATE TABLE "saga_memory_embeddings_vec" ("MemoryId" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL)`. This is the `SagaCurationEndpointTests.EnableTheVectorMirrorAsync` shape.
- **Legacy stand-in:** `CREATE VIRTUAL TABLE saga_memory_embeddings_vec USING fts5(MemoryId, Embedding)`. The shipping runtime has no `vec0` module. An FTS5 table puts the same `CREATE VIRTUAL TABLE…` text in `sqlite_master`, and that text is all that classification reads.

Mirror rows reach the table through the production insert: set `VectorAccelerator.SetAvailable(true)`, call `Store.InsertAsync`, assert one mirror row, then `SetAvailable(false)` before the verb under test. The only exceptions are the two stale-row seeds named below, which no build this harness can compose would write.

`SagaVectorMirrorTests` uses `SagaStoreHarness.CreateAsync(annalsEnabled: true)`:

```csharp
[SkippableTheory]
[InlineData("absent", SagaVectorMirrorKind.Absent)]
[InlineData("plain", SagaVectorMirrorKind.PlainTable)]
[InlineData("virtual", SagaVectorMirrorKind.LegacyVirtualTable)]
public async Task Classification_reads_the_catalog_not_the_process_flag(string shape, SagaVectorMirrorKind expected)
// flag set true before classifying, so the flag cannot be what answers
    => Assert.Equal(expected, await SagaVectorMirror.ClassifyAsync(harness.Connection, null, Token));

[SkippableTheory] [InlineData(true, 1)] [InlineData(false, 0)]
public async Task Insert_writes_the_mirror_only_while_the_accelerator_is_live(bool live, int rows)
{
    // plain table; VectorAccelerator.SetAvailable(live); Store.InsertAsync(target)
    Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);
    Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));
    Assert.Equal(rows, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
}

[SkippableTheory]
[InlineData("delete")] [InlineData("delete-all")] [InlineData("retire")] [InlineData("correct")]
public async Task A_plain_mirror_row_goes_with_its_memory_while_the_flag_is_off(string verb)
{
    // plain table; flag on; production insert; Assert.Equal(1, mirror rows); flag off
    // delete: Store.DeleteAsync(id); delete-all: Store.DeleteAllAsync();
    // retire: Store.RetireAsync(id, AnnalContentDigest.ForSagaMemory(original), now, Token);
    // correct: Store.CorrectAsync(id, AnnalContentDigest.ForSagaMemory(original), corrected, harness.Embedding(2), now, Token)
    Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
}

[SkippableFact]
public async Task Reinstate_removes_a_stale_mirror_row_while_the_flag_is_off()
{
    // plain table; flag off; insert; Store.RetireAsync(...); then seed one mirror row for the memory directly
    // (an earlier build's residue); Store.ReinstateAsync(id, AnnalContentDigest.ForSagaMemory(original), harness.Embedding(1), now, Token)
    Assert.Equal(SagaCurationOutcomeKind.Applied, reinstated.Kind);
    Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));
    Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
}

[SkippableFact]
public async Task A_legacy_virtual_mirror_is_left_alone_by_every_store_write()
{
    // stand-in seeded with one row for the memory; flag on; then, in order: InsertAsync, CorrectAsync, RetireAsync,
    // ReinstateAsync, DeleteAllAsync
    Assert.Equal(SagaMemoryWriteOutcome.Written, inserted);
    Assert.All(new[] { corrected, retired, reinstated }, outcome => Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind));
    Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));
    Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
}
```

`CovenantArtifactPlanRunnerTests`. Each run opens a transaction on `(SqliteConnection)harness.Connection`, binds `CovenantIdentitySql.Key(memoryId)`, and commits.

```csharp
[SkippableFact]
public async Task Count_measures_exactly_what_Delete_removes_for_a_saga_memory()
{
    // annals on; plain mirror; flag on for inserting "target" and an unrelated "other"; flag off before the run
    IReadOnlyList<MemoryErasureTableCount> expected =
    [
        new("annal_review_decision_receipts", 0), new("annal_review_events", 1), new("annal_dependencies", 0),
        new("annal_heads", 1), new("annal_versions", 1), new("annal_claims", 1),
        new("saga_memory_embeddings", 1), new("saga_memory_embeddings_vec", 1),
        new("saga_memory_attachment_provenance", 0), new("saga_memories", 1),
    ];
    CovenantArtifactPlanTally counted = await RunAsync(target, CovenantArtifactPlanMode.Count);
    Assert.Equal(expected, counted.Targets);
    Assert.Equal(SagaVectorMirrorKind.PlainTable, counted.VectorMirror);
    Assert.Equal("saga_memories", counted.Targets[^1].Table);             // R9: the artifact is last, and only there
    Assert.Equal(counted.Targets[^1].Rows, counted.ArtifactRows);
    Assert.Equal(1, counted.ArtifactRows);
    Assert.Equal(expected, (await RunAsync(target, CovenantArtifactPlanMode.Delete)).Targets);
    Assert.All((await RunAsync(target, CovenantArtifactPlanMode.Count)).Targets, t => Assert.Equal(0, t.Rows));
    Assert.Equal(1, await harness.CountAsync("saga_memories", "1 = 1"));   // "other" survives
    Assert.Equal(1, await harness.CountAsync("annal_claims", "1 = 1"));
}
```

The `annal_review_events` row is expected because every head insert fires `annal_review_events_head_insert` (`Data/Schema/Triggers/annal_review_events_head_insert.sql`). Add:
- `Count_measures_exactly_what_Delete_removes_for_a_lexicon_entry`: seed with `CorrectionFixture(annals: true).SeedAsync()`. The expected row count per target is that table's whole-table `SELECT count(*)` before the run (one entry in the database). `VectorMirror == Absent`, `Targets[^1] == new("lexicon_entries", 1)`, and `ArtifactRows == 1`.
- `A_legacy_virtual_mirror_is_skipped_counted_as_zero_and_reported`: stand-in holding a row for the memory, `Delete` mode, no exception; `VectorMirror == LegacyVirtualTable`; the mirror target reads `new("saga_memory_embeddings_vec", 0)`; the stand-in still holds 1 row.
- `[Fact] Every_materialized_plan_has_at_most_one_conditional_mirror_and_never_mixes_projections_with_a_pointer_or_redaction`: over `CovenantArtifactPurgePlans.MaterializedKinds`:
  - `Projections.Count(t => t.ExistsConditionally) <= 1`;
  - `!(Projections.Count > 0 && (CurrentPointerTable is not null || RedactionSql is not null))`;
  - the tables of `Projections` plus `Artifact` are distinct (R9: a table can appear in a tally only once).
- `SagaMemoryReviewServiceTests.Bulk_retire_removes_the_mirror_row_while_the_accelerator_flag_is_off` and `Bulk_correct_removes_the_stale_mirror_vector_while_the_accelerator_flag_is_off`: the harness store fills the mirror with its flag on; `CreateService(harness)` has its own flag, off; drive `PrepareAsync`/`ApplyAsync` as `Lifecycle_actions_apply_to_the_exact_head_and_retirement_head_remains_reviewable` does. Each asserts the apply succeeded and `saga_memory_embeddings_vec` holds 0 rows.
- `BackupRestoreProtectedStatePurgeTests.A_label_whose_content_row_is_already_gone_counts_no_removed_artifact`: a `Summary` label via `SeedLabelAsync` with no artifact row; reconcile with `purgeProtectedState: true`; `RemovedLabels == 1UL`, `RemovedArtifacts == 0UL`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaVectorMirrorTests|FullyQualifiedName~CovenantArtifactPlanRunnerTests|FullyQualifiedName~SagaMemoryReviewServiceTests.Bulk_|FullyQualifiedName~BackupRestoreProtectedStatePurgeTests.A_label_whose_content_row"
```

Expected RED: the build fails because `SagaVectorMirror`, `CovenantArtifactPlanRunner` and `SagaStoreHarness.VectorAccelerator` do not exist. With those stubbed to throw `NotImplementedException`, the flag-off cases still see 1 mirror row.

- [ ] **Step 3: Implement**

- **`SagaVectorMirror`.** Classify with `SELECT sql FROM sqlite_master WHERE name = $table AND type IN ('table', 'view') LIMIT 1;`:
  - no row → `Absent`;
  - text starting `CREATE VIRTUAL TABLE` (trimmed, `OrdinalIgnoreCase`) → `LegacyVirtualTable`;
  - else `PlainTable`.

  Only a `PlainTable` is deleted from (`DELETE FROM "saga_memory_embeddings_vec" WHERE "MemoryId" = @id`). Upsert: `PlainTable` with `vecAvailable` → `INSERT OR REPLACE … (@id, @embedding)` using `EmbeddingBlobCodec.Encode(vector.Span)`; `PlainTable` without it → the delete; otherwise nothing. A legacy virtual table is never opened.
- **Runner.** Move today's `DeleteBy` body into a private `Predicate(string)`; `CountBy` returns `$"SELECT count(*) FROM {Table} WHERE {Predicate(parameter)};"`. For each projection, then the artifact when the plan has one:
  - honour `RequiredFromCoreVersion`, reading `GrimoireCoreSchemaVersion.ReadAsync(connection, ct, transaction)` lazily at the first gated target and once per run, so the kernel's `missing`/`malformed` metadata cases still throw `InvalidDataException`;
  - classify `ExistsConditionally` targets through `SagaVectorMirror.ClassifyAsync(…, target.Table, …)`;
  - run `CountBy("$artifactKey")` (`ExecuteScalar`) or `DeleteBy("$artifactKey")` (`ExecuteNonQuery`), binding only `$artifactKey`;
  - append one `MemoryErasureTableCount` per target, and set `ArtifactRows` from the artifact's own entry.
- **Kernel `ApplyPlanAsync`:** pointer delete (still only when `rule.RepairsCurrentPointer`) → redaction → `CovenantArtifactPlanRunner.RunAsync(…, CovenantIdentitySql.Key(item.ArtifactId), Delete)` → label, receipt and Session repair, unchanged. Moving the pointer and redaction before the projections changes nothing, because no plan has both (the structural case pins that).
- **Purger `ApplyPlanAsync`:** pointer (unconditional, as today) → redaction → runner; return `tally.ArtifactRows > 0`.
- **The eight sites:** insert, reinstate, correct and review correct call `UpsertAsync(…, availability.IsVecAvailable, …)`; delete, retire and review retire call `DeleteAsync`; delete-all calls `DeleteAllAsync`.
- **DESIGN §10.17.** Both consumers run one plan runner (`Count` and `Delete`) that classifies each mirror inside the transaction. A plain mirror is deleted whatever the flag says; a legacy virtual table is skipped and reported as unreachable residue, not as a failure. Every Saga store write follows the same rule: it deletes mirror rows regardless of the flag and writes one only while the accelerator is live.
- **Benchmark catalog** (every later task that adds a `src` `.cs` file repeats this, "as in Task 4"):
  1. `git add` the new sources, because `GrimoireAdmissionBenchmarkManifestTests` reads git-tracked files.
  2. Insert one `R\t<path>` line per file into `grimoire-admission-input-catalog-v1.txt`, in ordinal order.
  3. Run `GrimoireAdmissionBenchmarkManifestTests`. Its shape-digest assertion reports the digest it computed.
  4. Paste that digest into `grimoire-admission-workload-v1.json:69` (`inputCatalogShapeDigest`) and `AdmissionBenchmarkManifest.cs:19` (`ExactInputCatalogShapeDigest`, which `AdmissionBenchmarkManifest.Validate()` compares with the JSON), then rerun green.

- [ ] **Step 4: Run the tests to verify they pass**

Run the Step 2 filter, then the regression cluster:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~CovenantProtectedArtifactErasureContentTests|FullyQualifiedName~BackupRestoreProtectedStatePurgeTests|FullyQualifiedName~LexiconMidUpgradeCompatibilityTests|FullyQualifiedName~CovenantSensitiveArtifactPurgePolicyTests|FullyQualifiedName~AnnalsErasureTests|FullyQualifiedName~SagaCurationStoreTests|FullyQualifiedName~SagaCurationEndpointTests|FullyQualifiedName~SagaMemoryReviewServiceTests|FullyQualifiedName~SagaMemoryStoreTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~DocumentationIssueReferenceTests"
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
git diff --stat tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests"
```

Pass criteria:
- In the capsule diff, only rows whose enclosing type is `CovenantProtectedArtifactErasureKernel`, `BackupRestoreProtectedStatePurger`, `SagaMemoryStore`, `SagaVectorMirror` or `CovenantArtifactPlanRunner` change or appear, with zero diagnostics. (`SagaMemoryReviewService` has no capsule row at `fe63b204`, so it cannot move.)
- Everything is green, and the kernel keeps the `IntegrityFailure` blocker for `missingDeclaredTable`, because the runner does not swallow `SqliteException`.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Drop the `CREATE VIRTUAL TABLE` branch, so the stand-in classifies as plain | `A_legacy_virtual_mirror_is_skipped_counted_as_zero_and_reported`, `A_legacy_virtual_mirror_is_left_alone_by_every_store_write` |
| Gate `SagaMemoryStore.DeleteAsync`'s mirror call on `availability.IsVecAvailable` again | `A_plain_mirror_row_goes_with_its_memory_while_the_flag_is_off("delete")` |
| Make `UpsertAsync` do nothing when `vecAvailable` is false | `…("correct")`, `Reinstate_removes_a_stale_mirror_row_while_the_flag_is_off` |
| Make `UpsertAsync` write when `vecAvailable` is false | `Insert_writes_the_mirror_only_while_the_accelerator_is_live(false, 0)` |
| Make `CountBy` return `SELECT count(*) FROM {Table};` | `Count_measures_exactly_what_Delete_removes_for_a_saga_memory` (the "other" row doubles the counts) |
| Append the artifact target to `Targets` a second time | `Count_measures_exactly_what_Delete_removes_for_a_saga_memory` (sequence mismatch) |
| Set `ArtifactRows` to `Σ Targets.Rows` | `Count_measures_exactly_what_Delete_removes_for_a_saga_memory` (7, not 1) |
| Change the purger's `> 0` to `>= 0` | `A_label_whose_content_row_is_already_gone_counts_no_removed_artifact` |

Restore each break and rerun GREEN.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/SagaVectorMirror.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantArtifactPlanRunner.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStatePurger.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantArtifactPlanRunnerTests.cs tests/RetroDownfall.Arcanum.Tests/Data/SagaVectorMirrorTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/SagaMemoryReviewServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreProtectedStatePurgeTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "fix: share one purge plan runner and scrub plain vector mirrors" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Finish #225: purge policy truth and plan-closed Annals pins

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantSensitiveArtifactPurgePolicy.cs` (the `Policy` strings of the `Saga` and `Lexicon` rules, :198 and :213)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`. In the `MemoryResetResidueTables` remarks, the paragraph "The protected-artifact purge is a known exception" (:7190-7196) is rewritten.
- Modify: `docs/Arcanum.DESIGN.md`. In §10.17, the paragraph on the plans table. In §21.12, the "Erasure follows the subject" removal list.
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/AnnalsOrphanAssertions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/SagaAnnalsProtectedErasureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDerivedOutputInventoryTests.cs`. Delete `No_declared_producer_labels_a_kind_whose_rows_carry_an_annals_claim` and its remarks, and add the plan-closed pin.
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantSensitiveArtifactPurgePolicyTests.cs` (policy text case)
- Modify: these tests end with the shared assertion:
  - `CovenantProtectedArtifactErasureContentTests`: `A_saga_memory_written_by_extraction_is_erased_with_its_embedding` (add the precondition `Assert.Equal(1, annal_claims)` before erasing) and `A_lexicon_entry_written_by_the_lexicon_service_is_erased`.
  - `CovenantProtectedArtifactErasureKernelTests.An_ordinary_purge_authority_requires_committed_schema_metadata_and_erases_under_the_retention_purge_scope` (the `current` arm).
  - `AnnalsErasureTests`: all four cases.
  - `Data/DataRetentionAnnalsErasureTests.cs` (a file of the partial class `DataRetentionServiceTests`, which is the name filters must use):
    - `ApplyAsync_ResetMemory_Saga_ClearsSagaClaimsAndLeavesLexiconClaimsStanding`
    - `ApplyAsync_ResetMemory_Lexicon_ClearsLexiconClaimsAndLeavesSagaClaimsStanding`
    - `ApplyAsync_ResetMemory_ForOneCampaign_ClearsOnlyThatCampaignsClaims`
    - `ApplyAsync_Prune_RemovesAnAgedMemorysClaimAndCountsItFirst`
    - `ApplyAsync_FactoryReset_ClearsAClaimThatCarriesMoreThanOneRevision`
  - `LexiconMidUpgradeCompatibilityTests.Protected_purge_erases_legacy_Lexicon_and_Annals_without_version_provenance_table` (both success arms).

No `src` `.cs` file is added, so the benchmark catalog does not change.

**Interfaces:**
- Depends on: Task 4.
- Consumes: `CovenantArtifactPurgeTarget.DeleteBy/CountBy` and the runner inside the kernel and purger (Task 4).
- Produces: `internal static class AnnalsOrphanAssertions { internal static Task AssertNoOrphanClaimsAsync(DbConnection connection, CancellationToken cancellationToken = default); }`. It is the closing assertion of every later erasure-path test (Tasks 13, 14, 16, 24 and 28).

- [ ] **Step 1: Write the failing tests**

This is the plan-closed pin that replaces the type-closed one in `CovenantDerivedOutputInventoryTests`:

```csharp
[Fact]
public void Every_annals_store_plan_takes_its_claim_in_annals_order_before_the_subject_row()
{
    foreach (AnnalSubjectStore store in Enum.GetValues<AnnalSubjectStore>())
    {
        SensitiveArtifactKind kind = Enum.Parse<SensitiveArtifactKind>(store.ToString());
        string subjectTable = store switch
        {
            AnnalSubjectStore.Saga => "saga_memories",
            AnnalSubjectStore.Lexicon => "lexicon_entries",
            _ => throw new InvalidOperationException($"The Annals subject store '{store}' declares no subject table."),
        };
        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);
        IReadOnlyList<AnnalsErasureStep> annals = AnnalsErasurePlan.ForStore(store);
        CovenantArtifactPurgeTarget[] claimed = [.. plan.Projections.Where(t => t.AnnalStore == store)];
        Assert.Equal(annals.Select(s => s.Table), claimed.Select(t => t.Table));
        Assert.Equal(annals.Select(s => s.RequiredFromCoreVersion), claimed.Select(t => t.RequiredFromCoreVersion));
        Assert.DoesNotContain(plan.Projections, t => t.AnnalStore is null && annals.Any(s => s.Table == t.Table));
        IReadOnlyList<AnnalsErasureStep> subject = AnnalsErasurePlan.ForSubjectQuery(store,
            $"SELECT SubjectId FROM annal_claims WHERE {CovenantIdentitySql.Keyed("SubjectId", "$artifactKey")}");
        Assert.Equal(subject.Select(s => $"DELETE FROM {s.Table} WHERE {s.Predicate};"), claimed.Select(t => t.DeleteBy("$artifactKey")));
        Assert.Equal(subject.Select(s => $"SELECT count(*) FROM {s.Table} WHERE {s.Predicate};"), claimed.Select(t => t.CountBy("$artifactKey")));
        Assert.Equal(subjectTable, plan.Artifact!.Table);
    }
}
```

This is the policy text case in `CovenantSensitiveArtifactPurgePolicyTests`:

```csharp
[Theory] [InlineData(SensitiveArtifactKind.Saga)] [InlineData(SensitiveArtifactKind.Lexicon)]
public void Annals_store_policies_name_the_claim_graph_they_remove(SensitiveArtifactKind kind)
{
    string policy = CovenantSensitiveArtifactPurgePolicy.Resolve(kind).Value.Policy;
    foreach (string part in new[] { "Annals claim", "every version", "head", "dependency edge", "review event", "review decision receipt" })
        Assert.Contains(part, policy, StringComparison.Ordinal);
    if (kind == SensitiveArtifactKind.Lexicon) Assert.Contains("historical fact provenance", policy, StringComparison.Ordinal);
    else Assert.DoesNotContain("FTS", policy, StringComparison.Ordinal);
}
```

`AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync` works like this:
- It returns when `annal_claims` is absent.
- It is closed over `Enum.GetValues<AnnalSubjectStore>()` with the same subject-table switch. An unknown store throws.
- For each store it asserts zero from:
  `SELECT count(*) FROM annal_claims c WHERE c.SubjectStoreCode = {code} AND NOT EXISTS (SELECT 1 FROM {subject} s WHERE lower(replace(s.Id,'-','')) = lower(replace(c.SubjectId,'-','')))`
  When the subject table is absent it counts that store's claims instead.
- It asserts that `PRAGMA foreign_key_check` returns no row for a table starting `annal_` or `lexicon_annal_`.
- Every assertion message names the store.

`SagaAnnalsProtectedErasureTests` uses `SagaStoreHarness.CreateAsync(annalsEnabled: true)` and Guid memory ids in their lowercase dashed spelling:

```csharp
[SkippableTheory] [InlineData(false)] [InlineData(true)]
public async Task A_labelled_saga_memory_with_a_full_annals_graph_leaves_no_claim_row_behind(bool staged)
{
    // Store.InsertAsync(target) → v1; Store.CorrectAsync(target, AnnalContentDigest.ForSagaMemory(original), …) → v2 plus a Supersedes edge;
    // Store.InsertAsync(other); one DerivedFrom (RelationCode 2, Ordinal 1) edge other.v1 → target.v1 inserted directly
    // (no production writer emits a cross-claim edge); Confirm target's head through SagaMemoryReviewService exactly as
    // Confirm_records_only_review_and_marker_waits_for_every_older_gap does; harness.LabelSensitiveAsync(target)
    // Counts: three review events — annal_review_events_head_insert fires for each claim's first head and
    // annal_review_events_head_update for the correction; Confirm writes one annal_review_decision_receipts row
    // (one per DecisionId) and moves no head.
    Assert.Equal(2, await harness.CountAsync("annal_claims", "1 = 1"));
    Assert.Equal(3, await harness.CountAsync("annal_versions", "1 = 1"));
    Assert.Equal(2, await harness.CountAsync("annal_heads", "1 = 1"));
    Assert.Equal(2, await harness.CountAsync("annal_dependencies", "1 = 1"));
    Assert.Equal(3, await harness.CountAsync("annal_review_events", "1 = 1"));
    Assert.Equal(1, await harness.CountAsync("annal_review_decision_receipts", "1 = 1"));
    // staged: CovenantSqliteConnectionInitializer.Instance.InitializeAsync(ReadWrite); BackupRestoreProtectedStatePurger.PurgeStagedAsync
    //   (connection, transaction, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, Token); commit.
    // live: the kernel under an exclusive CovenantFamilyReinitialize authority, built the way
    //   LexiconMidUpgradeCompatibilityTests.Protected_purge_erases_legacy_Lexicon_and_Annals_without_version_provenance_table builds it.
    Assert.Equal((1, 1, 1, 0, 1, 0), (claims, versions, heads, edges, reviewEvents, receipts));
    Assert.Equal(0, await harness.CountAsync("saga_memories", $"Id = '{target}'"));
    Assert.Equal(0, await harness.CountAsync("artifact_sensitivity", "1 = 1"));
    await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
}

[SkippableFact] // delete the subject row directly; the helper must throw
public async Task The_orphan_assertion_fails_on_a_claim_whose_subject_row_is_gone()
    => await Assert.ThrowsAnyAsync<XunitException>(() => AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection));
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantSensitiveArtifactPurgePolicyTests.Annals_store_policies|FullyQualifiedName~CovenantDerivedOutputInventoryTests|FullyQualifiedName~SagaAnnalsProtectedErasureTests"
```

The only production behaviour this task changes is the two `Policy` strings. The other new tests pin the claim-graph removal that the first half of #225 already landed, so they are made to fail first by a deliberate break, not by missing code.

Expected results:
- **RED, feature missing:** `Annals_store_policies_name_the_claim_graph_they_remove`, both rows. Neither text names the Annals claim at `fe63b204` (`CovenantSensitiveArtifactPurgePolicy.cs:198` and `:213`), and the Saga text names "FTS".
- **RED by break, before any Step 3 edit.** Apply the first three Step 5 breaks one at a time, run the filter, and confirm each named test fails for the stated reason:
  - remove `AnnalsTargets(Saga)`: the structural pin and both graph rows;
  - swap `annal_heads` and `annal_versions`: both graph rows;
  - make the orphan helper return immediately: `The_orphan_assertion_fails_on_a_claim_whose_subject_row_is_gone`.

  Restore each break, then confirm those tests are green on the unchanged tree before Step 3.

- [ ] **Step 3: Implement**

The Saga `Policy`:

> "Delete the Saga row, its embedding and vector-mirror projections, its attachment provenance, its Annals claim with every version, head, dependency edge, review event, and review decision receipt, and its label in one transaction."

The Lexicon `Policy`:

> "Delete the Lexicon row, its facts and FTS projection, its current and historical fact provenance, its Annals claim with every version, head, dependency edge, review event, and review decision receipt, and its label in one transaction."

**`DataRetentionService` remark.** Replace the "known exception" paragraph. The new paragraph states:
- the Saga and Lexicon purge plans carry the store's Annals erasure steps ahead of the row;
- so the live kernel and the staged restore purge take the claim in the same transaction;
- `CovenantDerivedOutputInventoryTests.Every_annals_store_plan_takes_its_claim_in_annals_order_before_the_subject_row` pins this on the plan;
- `AnnalsOrphanAssertions` closes every erasure-path test.

Keep the closing sentence about call-site blindness.

**DESIGN §10.17.** One sentence: the Saga and Lexicon plans begin with the store's Annals erasure steps, in the §21.12 order.

**DESIGN §21.12.** Extend the removal list with "the protected-artifact erasure kernel, and the staged restore purge".

Then append `await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection)` to every existing test listed under **Files**.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantSensitiveArtifactPurgePolicyTests|FullyQualifiedName~CovenantDerivedOutputInventoryTests|FullyQualifiedName~SagaAnnalsProtectedErasureTests|FullyQualifiedName~CovenantProtectedArtifactErasureContentTests|FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~AnnalsErasureTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~LexiconMidUpgradeCompatibilityTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~DocumentationStructureTests"
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Remove `.. AnnalsTargets(AnnalSubjectStore.Saga)` from the Saga plan | The structural pin, and both `A_labelled_saga_memory_with_a_full_annals_graph…` rows (the claim graph survives the row, so the counts and the orphan assertion fail) |
| Swap `annal_heads` and `annal_versions` in `AnnalsErasurePlan.Build` | Both graph rows. The kernel reports `IntegrityFailure` and the purger throws. |
| Make `AssertNoOrphanClaimsAsync` return immediately | `The_orphan_assertion_fails_on_a_claim_whose_subject_row_is_gone` |
| Put "FTS" back into the Saga policy | `Annals_store_policies_name_the_claim_graph_they_remove(Saga)` |
| Drop "historical fact provenance" from the Lexicon policy | `Annals_store_policies_name_the_claim_graph_they_remove(Lexicon)` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/DataLifecycle/CovenantSensitiveArtifactPurgePolicy.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Support/AnnalsOrphanAssertions.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/SagaAnnalsProtectedErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDerivedOutputInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantSensitiveArtifactPurgePolicyTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureKernelTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionAnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/LexiconMidUpgradeCompatibilityTests.cs
git commit -m "fix: state and pin that protected purges take the Annals claim" -m "Refs #225." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Erasure evidence store, two-phase guard and startup key warm-up

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureGuard.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureKeyWarmup.cs` (R11)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs` (the 10-parameter `EnsureInitializedAsync` calls the warm-up)
- Modify: `docs/Arcanum.DESIGN.md` §5.4, the `GrimoireDatabaseHostedService` bullet: one sentence on the warm-up. Task 30 writes the narrative.
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureTestKeys.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs` (R7, the §19.3 "only the named deleters" pin)
- Modify (closed inventories):
  - the three benchmark files, as in Task 4 (+3 `src` files);
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerate: `EnsureInitializedAsync` has rows under the `GrimoireDatabaseHostedService` root).
  - The acquisition inventory does not change. The warm-up runs on the bootstrapper's existing install handle, so `ExpectedProductionAcquisitionCount` stays **446** (the value at `fe63b204`; Tasks 1–5 add no acquisition).
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureEvidenceTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureGuardTests.cs` (new)
- Test: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs` (one theory; `CreateScopeFactory` gains two optional parameters)

**Interfaces:**
- Depends on: Tasks 1, 2 and 3.
- Consumes:
  - From Task 1: `MemoryErasureIdentity` (and `ForSaga`, `ForLexicon`, `ForCovenant`), `MemoryErasureKey` (`KeyId`, `Fingerprint`, `HasKeyId`), `MemoryErasureStoreCountsDto`, `MemoryExternalEvidence`, and `ErrorCodes.MemoryErasure.KeyLost/KeyUnavailable/Unavailable`.
  - From Task 2: `IMemoryErasureKeyProvider`, `MemoryErasureKeyState`, `MemoryErasureKeyProbe`, `MemoryErasureKeyOpenResult`, `MemoryErasureKeyLatch`, `MemoryErasureKeyring(IOsCredentialStore)`, `IMemoryErasureKeyCreator.OpenOrCreate(bool evidenceRowsExist)` and `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`.
  - From Task 3: the Core v13 tables, the receipt update guard, and `CoreSchemaVersionTwelveFixture.ChainSet()`.
- Produces (`internal`, `Data` namespace):
  - `MemoryErasureEvidence`, every method on `(SqliteConnection connection, SqliteTransaction? transaction, …, CancellationToken cancellationToken)`:
    - `IsInstalledAsync → Task<bool>`
    - `AnyAsync(MemoryReviewStore store) → Task<bool>`
    - `AnyForeignAsync(MemoryReviewStore store, byte[] keyId) → Task<bool>`
    - `ContainsAsync(byte[] fingerprint) → Task<bool>`
    - `InsertFingerprintAsync(byte[] fingerprint, MemoryReviewStore store, byte[] keyId) → Task<bool>`
    - `DeleteFingerprintAsync(byte[] fingerprint) → Task<int>`
    - `InsertReceiptAsync(MemoryErasureReceiptRow row, IReadOnlyList<byte[]> subjectDigests)`
    - `ReadReceiptAsync(Guid mutationId) → Task<MemoryErasureReceiptRow?>`
    - `SubjectErasedAsync(byte[] subjectDigest) → Task<bool>`
    - `ClearWalPendingAsync(Guid? mutationId) → Task<long>`
    - `CountAsync(byte[]? currentKeyId) → Task<MemoryErasureEvidenceCounts>`
    - `DeleteUnverifiableAsync(byte[] currentKeyId) → Task<(long Fingerprints, long Receipts)>`

    Reads and deletes treat `Core < 13` or an absent table as empty; the two inserts throw `InvalidOperationException` there.
  - `MemoryErasureReceiptRow(Guid MutationId, MemoryReviewStore Store, byte[] KeyId, byte[] RequestDigest, byte[] EffectDigest, int ErasedItemCount, long RemovedRowCount, int RemovedLabelCount, int RemovedRetirementSuppressionCount, MemoryExternalEvidence Authorship, MemoryExternalEvidence Context, MemoryExternalEvidence Embedding, MemoryExternalEvidence Backup, MemoryExternalEvidence OtherExternal, int RetainedCopiesMask, int ScrubStateCode, int ScrubPendingReasonMask)`
  - `MemoryErasureEvidenceCounts(IReadOnlyList<MemoryErasureStoreCountsDto> Stores, long UnverifiableReceipts, long PendingScrubReceipts)`. `Stores` is always three entries in code order: Covenant, Saga, Lexicon.
  - The guard types `MemoryErasureGuardContext`, `MemoryErasureGuardVerdict`, and `MemoryErasureGuard.PrepareAsync/CheckAsync`, exactly as in the spine, except that `CheckAsync` takes `SqliteTransaction? transaction`.
  - Additions to the spine on `MemoryErasureGuard`:
    - `CheckAsync(SqliteConnection connection, SqliteTransaction? transaction, MemoryErasureGuardContext context, Func<MemoryErasureIdentity> identity, CancellationToken cancellationToken)`. The factory runs only after the in-transaction `AnyAsync` is true, so a write with no evidence never derives an identity. The value overload delegates to it. Task 7 uses the factory form, because a Saga identity parses a stored Campaign spelling (Review Focus 1).
    - `RunWithRetryAsync<T>(SqliteConnection connection, MemoryReviewStore store, IMemoryErasureKeyProvider keys, Func<MemoryErasureGuardContext, Task<T>> write, CancellationToken cancellationToken) → Task<T>`
    - `static Error RefusalFor(MemoryErasureKeyState state)` (R16): `Absent` → `KeyLostError`; `Unresolved`, `Unavailable`, `Malformed` → `KeyUnavailableError`; `Present` throws `ArgumentOutOfRangeException`. A foreign `KeyId` is always `KeyLostError`. `PrepareAsync` uses it, and Task 11 maps the Covenant latch through it at staging and in the kernel.
    - `static Error KeyLostError`, `KeyUnavailableError` and `UnavailableError`.
    - `internal sealed class MemoryErasureGuardException(Error error) : Exception` exposing `Error Error`; `internal sealed class MemoryErasureRetryException : Exception`.
  - `internal static class MemoryErasureKeyWarmup { internal static Task RunAsync(SqliteConnection connection, IMemoryErasureKeyProvider keys, CancellationToken cancellationToken); }` (R11).
  - Test support, `MemoryErasureTestKeys`:
    - `Isolated(IOsCredentialStore? credentials = null) → MemoryErasureKeyring` (a fresh latch);
    - `CreateKey(IOsCredentialStore credentials) → MemoryErasureKey` (the production creator with `evidenceRowsExist: false`);
    - `SeedFingerprintAsync(SqliteConnection connection, MemoryErasureKey key, MemoryErasureIdentity identity, CancellationToken cancellationToken)`, which calls `InsertFingerprintAsync`;
    - `sealed class CountingOsCredentialStore(InMemoryOsCredentialStore inner) : IOsCredentialStore, IOsCredentialPresenceProbe` with `int Calls` (every member counts) and `OsCredentialStoreStatus? FailWith`.
  - Test support, `MemoryErasureEvidenceDeleterTests.AllowedCallers`: the closed caller allow-list that Tasks 17, 18 and 24 extend (R7).

- [ ] **Step 1: Write the failing tests**

Both data classes run over a `GrimoireFixture` copy at head (v13), through `(SqliteConnection)context.Database.GetDbConnection()`. One keyring creates the key and a second, fresh one reads it, so the guard's latch starts `Unresolved`. Unless a case says otherwise, `Identity` is `MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "The operator prefers dark mode.")`.

```csharp
[SkippableFact]
public async Task No_evidence_means_allowed_with_no_keychain_io()
{
    CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());
    using MemoryErasureGuardContext context = (await MemoryErasureGuard.PrepareAsync(connection, MemoryReviewStore.Saga, MemoryErasureTestKeys.Isolated(credentials), Token)).Value;
    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
    Assert.Equal(MemoryErasureGuardVerdict.Allowed, await MemoryErasureGuard.CheckAsync(connection, transaction, context, Identity, Token));
    Assert.False(context.EvidencePresent);
    Assert.Equal(0, credentials.Calls);
}

[SkippableFact] // prepare finds nothing; a fingerprint commits; inside BEGIN the check sees it with no key in hand
public async Task Evidence_that_appears_after_the_probe_asks_for_one_retry_with_the_key()
{
    Assert.Equal(MemoryErasureGuardVerdict.RetryWithKey, first);
    // rollback, prepare again: EvidencePresent is true and Key is not null
    Assert.Equal(MemoryErasureGuardVerdict.Withheld, second);
}

[SkippableFact] // K1 writes a row; the credential is replaced by K2 (a second test home sharing the account)
public async Task Rows_written_under_an_overwritten_key_fail_closed_as_key_lost()
{
    Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, (await MemoryErasureGuard.PrepareAsync(…)).Error.Code);
    Assert.Equal(MemoryErasureGuardVerdict.KeyLost, await MemoryErasureGuard.CheckAsync(…)); // context prepared before the foreign row
}

[Theory]
[InlineData(MemoryErasureKeyState.Absent, ErrorCodes.MemoryErasure.KeyLost)]
[InlineData(MemoryErasureKeyState.Unresolved, ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData(MemoryErasureKeyState.Unavailable, ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData(MemoryErasureKeyState.Malformed, ErrorCodes.MemoryErasure.KeyUnavailable)]
public void Every_key_state_short_of_present_maps_to_one_refusal(MemoryErasureKeyState state, string code)
    => Assert.Equal(code, MemoryErasureGuard.RefusalFor(state).Code);
// plus Assert.Throws<ArgumentOutOfRangeException>(() => MemoryErasureGuard.RefusalFor(MemoryErasureKeyState.Present))
```

Add these guard cases:
- `Evidence_without_a_key_is_key_lost_and_an_unreadable_key_is_unavailable`: `[Theory]` over:
  - `"deleted"` → `MemoryErasure.KeyLost`;
  - `"unavailable"` (`FailWith = Unavailable`) → `MemoryErasure.KeyUnavailable`;
  - `"malformed"` (the account holds `"not-a-key"`) → `MemoryErasure.KeyUnavailable`.
- `A_matching_fingerprint_is_withheld_and_nothing_else_is`: `Withheld` for the seeded identity; `Allowed` for other content in the same scope, for the same content in Campaign scope, and for the same value under a `Lexicon` guard.
- `An_identity_is_computed_only_when_evidence_exists`: with no evidence, `CheckAsync(…, () => throw new FormatException(), …)` returns `Allowed`. After a Saga fingerprint is seeded and the context is prepared again (key in hand), the same call throws `FormatException`.
- `RunWithRetryAsync_prepares_again_when_the_write_meets_newer_evidence`: the write delegate's first call seeds the fingerprint, opens BEGIN, sees `RetryWithKey`, rolls back and throws `MemoryErasureRetryException`; its second call sees `Withheld`. Assert two invocations and a `Withheld` result.
- `A_catalog_below_version_thirteen_has_no_evidence_and_touches_no_key`: `[Theory]` over:
  - `"fixture-v12"`: an `EvolutionScratchDatabase` installed with `CoreSchemaVersionTwelveFixture.ChainSet()`;
  - `"v13-recorded-as-12"`: seed a fingerprint, then `UPDATE grimoire_feature_schemas SET SchemaVersion = 12 WHERE FamilyCode = 0 AND TransactionTierCode = 0`.

  In both: `IsInstalledAsync`, `AnyAsync` and `ContainsAsync` return false; `CountAsync` is all zeros; the guard answers `Allowed`; `MemoryErasureKeyWarmup.RunAsync` leaves `keys.Latch.State == Unresolved`; `credentials.Calls == 0`; and `InsertFingerprintAsync` throws `InvalidOperationException`.

`MemoryErasureEvidenceTests`:
- `Fingerprint_rows_round_trip_by_primary_key_and_store`: `InsertFingerprintAsync` returns `true`, then `false`; `AnyAsync(Saga)` is true and `AnyAsync(Lexicon)` false; `AnyForeignAsync(Saga, own)` is false and `(Saga, other)` true; `DeleteFingerprintAsync` returns `1`, then `0`.
- `A_receipt_and_its_subjects_round_trip_and_answer_by_subject_digest`: every field reads back (byte arrays by `Assert.Equal<byte>`); `MutationId` is stored uppercase and dashed; `SubjectErasedAsync` is true for each inserted digest and false for another.
- `Clearing_the_wal_reason_verifies_only_receipts_that_had_no_other_reason`: seed `r1` with mask `1` and `r2` with mask `1|2`. `ClearWalPendingAsync(null)` returns `1`; then `r1` reads `(2, 0)` and `r2` `(1, 2)`; a second call returns `0`.
- `Counts_and_unverifiable_deletion_split_rows_by_key_id`. Seed Saga: 2 rows under K and 1 under K′. Lexicon: 1 under K′. Receipts: a Saga receipt under K and a Lexicon receipt under K′, both pending.

  | Call | Expected result |
  |---|---|
  | `CountAsync(K)` | Stores `[(Covenant,0,0,0), (Saga,3,1,1), (Lexicon,1,1,1)]`; UnverifiableReceipts `1`; PendingScrubReceipts `2` |
  | `CountAsync(null)` | Saga Unverifiable `3`; Lexicon Unverifiable `1`; UnverifiableReceipts `2` |
  | `DeleteUnverifiableAsync(K)` | `(2, 1)` |

  Afterwards the K′ receipt's subjects are gone through the cascade, and the Saga store reads `(2,0,1)`.

`MemoryErasureEvidenceDeleterTests` (R7, §5.3, §19.3). It scans `ProductionSourceInventory.Sources()` (comment-free `src/**/*.cs`) and every `src/**/*.sql` file under `NativeSqlCipherTestPaths.RepositoryRoot()`:

```csharp
private const string Owner = "src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs";

// Closed. Task 17 adds release and operator re-creation, Task 18 reset-key, Task 24 restore staging. Nothing else.
internal static readonly string[] AllowedCallers = [];

private static readonly Regex EvidenceDelete = new(
    @"DELETE\s+FROM\s+""?memory_erasure_(fingerprints|receipts|receipt_subjects)\b",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

[Fact]
public void Only_the_evidence_store_deletes_evidence_rows()
{
    Assert.Equal([Owner], DeletingFiles());      // .cs and .sql alike; a full installation reset deletes the file, not rows
    Assert.Contains(EvidenceDelete.Matches(OwnerText()), m => m.Groups[1].Value == "fingerprints");
    Assert.Contains(EvidenceDelete.Matches(OwnerText()), m => m.Groups[1].Value == "receipts");
}

[Fact]
public void Evidence_deleter_callers_are_a_closed_allow_list()
{
    string[] members = ["MemoryErasureEvidence.DeleteFingerprintAsync", "MemoryErasureEvidence.DeleteUnverifiableAsync", "MemoryErasureEvidence.ReplaceAllAsync"];
    string[] callers = [.. ProductionSourceInventory.Sources()
        .Where(source => !source.IsExactOwner(Owner) && members.Any(source.Names))
        .Select(source => source.RelativePath).Order(StringComparer.Ordinal)];
    Assert.Equal(AllowedCallers.Order(StringComparer.Ordinal), callers);
}
```

`ReplaceAllAsync` is Task 24's member. The scan matches text, so naming it before it exists is harmless.

`GrimoireDatabaseBootstrapperTests` (R11). `CreateScopeFactory` gains `IMemoryErasureKeyProvider? erasureKeys = null` and `IGrimoireDbReadiness? readiness = null`. When given, `erasureKeys` is registered as a singleton, and `readiness` replaces the forwarding `IGrimoireDbReadiness` registration. A private `LatchRecordingReadiness(IMemoryErasureKeyProvider keys) : IGrimoireDbReadiness` records `keys.Latch.State` inside `MarkReady`.

```csharp
[Theory]
[InlineData("no-fingerprints", MemoryErasureKeyState.Unresolved, 0)]
[InlineData("present", MemoryErasureKeyState.Present, 1)]
[InlineData("deleted", MemoryErasureKeyState.Absent, 1)]
[InlineData("unreadable", MemoryErasureKeyState.Unavailable, 1)]
public async Task Startup_resolves_the_erasure_key_once_before_readiness_when_fingerprints_exist(
    string key, MemoryErasureKeyState atReadiness, int credentialCalls)
{
    // 1. EnsureInitializedAsync with _scopeFactory: a fresh install at head.
    // 2. Unless "no-fingerprints": MemoryErasureTestKeys.CreateKey(_credentialStore); open an unpooled connection the way
    //    CheckpointOnShutdownAsync_truncates_populated_wal_when_no_readers_hold_it does; SeedFingerprintAsync(connection, key,
    //    MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, "TONE"), Token). A Covenant-only row is the B8 case.
    //    "deleted": _credentialStore.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount).
    //    "unreadable": counting.FailWith = OsCredentialStoreStatus.Unavailable.
    // 3. counting = new CountingOsCredentialStore(_credentialStore); keyring = MemoryErasureTestKeys.Isolated(counting);
    //    recorder = new LatchRecordingReadiness(keyring). EnsureInitializedAsync again with
    //    CreateScopeFactory(_credentialStore, erasureKeys: keyring, readiness: recorder). Only the keyring sees `counting`.
    Assert.True(recorder.IsReady);
    Assert.Equal(atReadiness, recorder.LatchAtReady);
    Assert.Equal(credentialCalls, counting.Calls);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureGuardTests|FullyQualifiedName~MemoryErasureEvidenceTests|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests.Startup_resolves_the_erasure_key"
```

Expected RED:
- The build fails, because `MemoryErasureEvidence`, `MemoryErasureGuard`, `MemoryErasureKeyWarmup` and `MemoryErasureTestKeys` do not exist.
- With the production types stubbed to throw `NotImplementedException` and the bootstrapper not yet calling the warm-up:
  - `Only_the_evidence_store_deletes_evidence_rows` fails with an empty deleter list;
  - the bootstrapper theory's `present`, `deleted` and `unreadable` rows read `Unresolved` at readiness.
- `Evidence_deleter_callers_are_a_closed_allow_list` and the bootstrapper's `no-fingerprints` row pass on arrival. They are closed pins, and Step 5 proves they are load-bearing.

- [ ] **Step 3: Implement**

- **`IsInstalledAsync`:** `GrimoireCoreSchemaVersion.ReadAsync(connection, ct, transaction) >= 13` and `memory_erasure_fingerprints` present in `sqlite_master`. Missing or malformed metadata throws, which fails closed. Every other method calls it first.
- **SQL, all parameterized.**
  - `AnyAsync`: `SELECT EXISTS(SELECT 1 FROM memory_erasure_fingerprints WHERE StoreCode = $store)`.
  - `AnyForeignAsync`: the same plus `AND KeyId <> $keyId`.
  - Fingerprint insert: `INSERT OR IGNORE`.
  - `ClearWalPendingAsync`:
    ```sql
    UPDATE memory_erasure_receipts
    SET ScrubPendingReasonMask = ScrubPendingReasonMask & ~1,
        ScrubStateCode = CASE WHEN (ScrubPendingReasonMask & ~1) = 0 THEN 2 ELSE 1 END
    WHERE ScrubStateCode = 1
      AND (ScrubPendingReasonMask & 1) = 1
      AND ($mutationId IS NULL OR MutationId = $mutationId)
    ```
    It returns the number of receipts that reached `ScrubStateCode = 2`.
  - `DeleteUnverifiableAsync` deletes fingerprints and receipts `WHERE KeyId <> $keyId`; subjects cascade. These and `DeleteFingerprintAsync` are the only `DELETE FROM memory_erasure_*` statements in `src/`.
- **`PrepareAsync`.**
  - `AnyAsync` false → a context with `EvidencePresent = false` and `Key = keys.TryCopyLatched()`. This is never I/O, and it spares most first-fingerprint races the retry.
  - `AnyAsync` true → `keys.OpenExisting(MemoryErasureKeyProbe.UseLatched)` (which probes once only while `Unresolved`, per Task 2):
    - `Present` → run `AnyForeignAsync`; a hit disposes the key and returns `KeyLostError`;
    - any other state → `RefusalFor(state)`.
- **`CheckAsync`,** in order:
  1. `identity.Store` (or, for the factory form, `context.Store`) must match, else `ArgumentException`.
  2. Re-run `AnyAsync` inside the transaction; false → `Allowed`.
  3. No key in hand → `RetryWithKey`.
  4. `AnyForeignAsync` hit → `KeyLost`.
  5. Only now evaluate the identity factory. `ContainsAsync(context.Key.Fingerprint(identity))` → `Withheld`, else `Allowed`.
- **`RunWithRetryAsync`.** A preparation failure throws `MemoryErasureGuardException`. The first `MemoryErasureRetryException` disposes the context, prepares once more and reruns the write; a second throws `MemoryErasureGuardException(UnavailableError)`.
- **Error messages** (content-free; the guard does not log):
  - `KeyLostError`: "Erasure fingerprints exist that this installation's erasure key cannot verify, so automatic writes to this store are withheld. Run 'arcanum memory erasure status'."
  - `KeyUnavailableError`: "The erasure key could not be read, so automatic writes to this store are withheld until it can be."
  - `UnavailableError`: "Erasure evidence changed while the write was in progress; retry the write."
- **`MemoryErasureKeyWarmup.RunAsync`** (R11):
  1. `IsInstalledAsync` false → return.
  2. `AnyAsync` for Covenant, Saga and Lexicon, short-circuiting; none → return. No keychain I/O happens without evidence.
  3. `keys.OpenExisting(MemoryErasureKeyProbe.Reprobe)`, which publishes the latch; dispose any returned key at once.
  4. A state other than `Present` logs one content-free warning through Serilog's static `Log`, as the bootstrapper does: "Erasure fingerprints exist and the erasure key is {KeyState}; automatic writes to the stores that hold them stay withheld until 'arcanum memory erasure status' reports it present."

  It never throws for a key state. A SQL failure propagates, and fails startup closed exactly as every other bootstrap read does.
- **Bootstrapper.** In the 10-parameter `EnsureInitializedAsync`, directly after `RecoverRestoreAuthorityAsync` and before `RecoverProtectedMaintenanceAsync`:
  - create a scope, and resolve `IMemoryErasureKeyProvider` with `GetService`;
  - when it is composed, `await MemoryErasureKeyWarmup.RunAsync(installConnection, provider, cancellationToken)`.

  That point is after schema install and restore-authority revalidation, and before the protected pass adopts any exclusive owner into the Covenant gate (`CovenantErasureStartupRecoveryOwnerAdopter`). No transaction is open on the install handle there. So the keychain read runs outside every transaction, lease and closure (§5.1), and well before `PublishReadiness` and `MarkReady`.

  A composition without the provider (the bootstrapper tests' default container) skips the warm-up. The CLI composition registers the provider (Task 2), so a direct-Grimoire CLI verb also warms, but only when fingerprints exist.
- **DESIGN §5.4.** One sentence in the `GrimoireDatabaseHostedService` bullet: when erasure fingerprints exist, bootstrap resolves the erasure key once before readiness (a keychain read that can raise a macOS prompt), so Covenant agent writes are not withheld until an operator call.
- **Benchmark catalog:** as in Task 4, for the three new sources.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureGuardTests|FullyQualifiedName~MemoryErasureEvidenceTests|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~HostProcessToolsAdvertisementAfterStartupTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```

Then regenerate and verify the capsules with the Task 4 commands.

Pass criteria:
- The capsule diff touches only `GrimoireDatabaseBootstrapper.EnsureInitializedAsync` rows and new `MemoryErasureKeyWarmup`/`MemoryErasureEvidence` rows under the `GrimoireDatabaseHostedService` root, with zero diagnostics.
- `GrimoireConnectionAcquisitionInventoryTests` is green at 446.
- `Production_host_startup_reads_the_master_credential_exactly_once` stays green: an installation with no fingerprints does no erasure-key I/O at startup.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Remove the `IsInstalledAsync` gate from `AnyAsync` | `A_catalog_below_version_thirteen…` (both rows) |
| In `CheckAsync`, trust `context.EvidencePresent` instead of re-running `AnyAsync` | `Evidence_that_appears_after_the_probe_asks_for_one_retry_with_the_key` |
| Delete the `AnyForeignAsync` step from `CheckAsync` | `Rows_written_under_an_overwritten_key_fail_closed_as_key_lost` |
| Evaluate the identity factory before the in-transaction `AnyAsync` | `An_identity_is_computed_only_when_evidence_exists` |
| Call `OpenExisting` before `AnyAsync` in `PrepareAsync` | `No_evidence_means_allowed_with_no_keychain_io` |
| Map `Malformed` to `KeyLost` in `RefusalFor` | `Every_key_state_short_of_present_maps_to_one_refusal(Malformed)`, `Evidence_without_a_key…("malformed")` |
| Map `Absent` to `KeyUnavailable` in `RefusalFor` | `Every_key_state_short_of_present_maps_to_one_refusal(Absent)`, `Evidence_without_a_key…("deleted")` |
| Rethrow the first `MemoryErasureRetryException` | `RunWithRetryAsync_prepares_again_when_the_write_meets_newer_evidence` |
| Drop `(ScrubPendingReasonMask & 1) = 1` from the WAL clear | `Clearing_the_wal_reason_verifies_only_receipts_that_had_no_other_reason` |
| Write `KeyId = $keyId` in the unverifiable delete | `Counts_and_unverifiable_deletion_split_rows_by_key_id` |
| Remove the warm-up call from the bootstrapper | `Startup_resolves_the_erasure_key…("present")`, `("deleted")`, `("unreadable")` (latch `Unresolved` at readiness) |
| Move the warm-up call after `readiness.MarkReady()` | the same three rows |
| Drop the evidence probe from the warm-up, so it always re-probes | `Startup_resolves_the_erasure_key…("no-fingerprints")` (`Calls == 1`), `A_catalog_below_version_thirteen…` |
| Add `DELETE FROM memory_erasure_fingerprints WHERE 0;` to `DataRetentionService.cs` | `Only_the_evidence_store_deletes_evidence_rows` |
| Call `MemoryErasureEvidence.DeleteFingerprintAsync` from `SagaMemoryStore.cs` | `Evidence_deleter_callers_are_a_closed_allow_list` |

Restore each break and rerun GREEN.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureGuard.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureKeyWarmup.cs src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureTestKeys.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureEvidenceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureGuardTests.cs tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: add the erasure evidence store, two-phase guard and startup key warm-up" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Saga chokepoint and extraction gates

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaErasureWriteGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs` (a required `IMemoryErasureKeyProvider erasureKeys` after `options` in the primary constructor; `InsertCoreAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`:
  - `SagaExtractionOutcome.DeferredForErasureKey = 4`;
  - `ErasureKeyDeferralDelayForTests`;
  - the consumer-loop branch;
  - `ExtractForSessionAsync` resolves `SagaErasureWriteGate` beside `ISagaMemoryStore` (:927), and gains the per-page gate and the pre-embed skip;
  - the Suppressed log text.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (`AddArcanumInfrastructure`: `services.AddScoped<SagaErasureWriteGate>()` beside `ISagaMemoryStore`, :1360)
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs` (`SagaMemoryWriteOutcome` docs: "retired or erased") and `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs` (`InsertAsync` remarks)
- Modify: `docs/Arcanum.DESIGN.md` §21.9, one sentence: the insert chokepoint is authoritative; the pre-embed skip and pre-model deferral belong to extraction. Task 30 writes the narrative.
- Modify the construction and DI sites under `tests/RetroDownfall.Arcanum.Tests/`. The list below is every `SagaMemoryStore` construction and registration at `fe63b204`, found by grep:
  - `Fixtures/SagaStoreHarness.cs` (:104) becomes `CreateAsync(bool annalsEnabled, IMemoryErasureKeyProvider? erasureKeys = null)`, defaulting to `MemoryErasureTestKeys.Isolated()`. The parameterless `CreateAsync()` stays.
  - Constructor sites: `Weave/EmbeddingsResetServiceTests.cs` (:54, :291), `Weave/SessionAttachmentIdentitySpellingTests.cs` (:463), `Annals/AnnalsErasureTests.cs` (:211), `Annals/AnnalsStoreTests.cs` (:249), `Annals/SagaAnnalsWriteThroughTests.cs` (:548), `Data/SagaCampaignScopedRetrievalTests.cs` (:65), `Data/DataRetentionCampaignScopedResetTests.cs` (:473), `Data/SagaMemoryStoreTests.cs` (:47), `Data/Schema/SagaMemoryMidUpgradeWriteTests.cs` (`CreateStore`, :599, gains an optional provider), `Hosting/SagaExtractionServiceTests.cs` (`CreateStore`, :4593).
  - Containers:
    - `Hosting/SagaExtractionServiceTests.cs`: the inline container at :1006, and `BuildScope` (:4645, registering the store at :4691, which gains `IMemoryErasureKeyProvider? erasureKeys = null`);
    - `Api/LexiconProtectedInspectionTests.cs` (:354);
    - `Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs` (:338).

    Each registers `IMemoryErasureKeyProvider` (an isolated keyring). The ones that run extraction pages (both `SagaExtractionServiceTests` containers and `CovenantProtectedArtifactErasureContentTests`) also register `SagaErasureWriteGate`. The `WizardIntelligenceProvider*Tests` constructions use an empty scope factory and never run a page, so they need nothing.
- Modify (closed inventories):
  - `Build/NullableInterfaceConstructorDefaultTests.cs` (new closed-constructor pin);
  - `Support/GrimoireConnectionAcquisitionInventory.cs` (+3 rows) and `Data/GrimoireConnectionAcquisitionInventoryTests.cs`: `ExpectedProductionAcquisitionCount` **446 → 449** (Tasks 1–6 add none);
  - the capsule TSV (regenerate: `SagaMemoryStore` and `SagaExtractionService` have rows under the `SagaExtractionService` root);
  - the three benchmark files, as in Task 4 (+1 `src` file).
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/SagaErasureSuppressionTests.cs` (new); `Hosting/SagaExtractionServiceTests.cs` (five cases); `Data/Schema/SagaMemoryMidUpgradeWriteTests.cs` (the upgrade-window case)

**Interfaces:**
- Depends on: Task 6. Also Task 4, which edits `InsertCoreAsync` and `SagaStoreHarness` first; the file order satisfies it.
- Consumes:
  - From Task 6: `MemoryErasureGuard.RunWithRetryAsync`, `PrepareAsync`, and `CheckAsync` in its identity-factory form; `MemoryErasureGuardException`; `MemoryErasureRetryException`; `MemoryErasureEvidence.InsertFingerprintAsync`; `MemoryErasureTestKeys`.
  - From Task 1: `MemoryErasureIdentity.ForSaga`.
  - Existing: `SagaMemoryScopeClassifier.Classify` and `ResolveForSessionAsync`, and `CoreSchemaVersionElevenFixture` (rebased by Task 3).
- Produces:
  - `SagaMemoryStore(ArcanumDbContext db, WeaveIndexAvailability availability, IOptionsMonitor<ArcanumSettings> options, IMemoryErasureKeyProvider erasureKeys, ICovenantLabeledArtifactGuard? labeledArtifactGuard = null)`
  - `internal sealed class SagaErasureWriteGate(ArcanumDbContext db, IMemoryErasureKeyProvider erasureKeys)`:
    - `Task<Result<MemoryErasureGuardContext>> PrepareAsync(CancellationToken)`;
    - `Task<bool> IsWithheldAsync(MemoryErasureGuardContext context, Guid? sessionId, string content, CancellationToken)`;
    - `static MemoryErasureIdentity SagaIdentity(SagaMemoryScopeKind kind, string? campaignId, string content)`. It parses with `Guid.Parse(…, CultureInfo.InvariantCulture)` and throws `FormatException` on an unparseable value; it never falls back to the raw string. An inconsistent stored pairing (a non-Campaign kind with a Campaign value, or Campaign kind with none), which `MemoryErasureIdentity.ForSaga` rejects with `ArgumentException`, is rethrown as `FormatException`, so every caller handles one exception type and fails closed.
  - `SagaExtractionOutcome.DeferredForErasureKey = 4`
  - `SagaExtractionService.ErasureKeyDeferralDelayForTests { get; init; }`, default `TimeSpan.FromMinutes(1)`

- [ ] **Step 1: Write the failing tests**

`SagaErasureSuppressionTests` uses `SagaStoreHarness.CreateAsync(annalsEnabled: true, MemoryErasureTestKeys.Isolated(credentials))`. The key comes from `MemoryErasureTestKeys.CreateKey(credentials)`. The fingerprint is seeded with `SeedFingerprintAsync((SqliteConnection)harness.Connection, …)`, because the erase route does not exist yet.

The seeded scope is the one production derived. A probe memory is first written in the same Session, and `ReadCurationRowAsync(probe).Memory.ScopeKind/ScopeCampaignId` is read back from it.

```csharp
[SkippableTheory] [InlineData(false)] [InlineData(true)] // true: harness.SessionBoundToNewCampaignAsync()
public async Task Fingerprinted_content_in_the_same_scope_is_suppressed_and_nothing_is_written(bool campaign)
{
    Assert.Equal(SagaMemoryWriteOutcome.Suppressed, outcome);
    Assert.Equal(0, await harness.CountAsync("saga_memories", "Content = 'The operator prefers dark mode.'"));
    Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));   // the probe only
    Assert.Equal(1, await harness.CountAsync("annal_claims", "1 = 1"));
}

[SkippableTheory] // fingerprint Global "café" (U+00E9)
[InlineData("global-nfc", SagaMemoryWriteOutcome.Suppressed)]
[InlineData("global-nfd", SagaMemoryWriteOutcome.Written)]              // "café"
[InlineData("global-trailing-space", SagaMemoryWriteOutcome.Written)]   // "café "
[InlineData("campaign-nfc", SagaMemoryWriteOutcome.Written)]
public async Task Only_the_exact_bytes_in_the_exact_scope_are_suppressed(string write, SagaMemoryWriteOutcome expected)
    => Assert.Equal(expected, outcome);

// Review Focus 1, through the production composition: the classifier canonicalizes the bound spelling, then the gate derives the identity.
[Theory]
[InlineData("a0000000-0000-4000-8000-0000000000c5")]
[InlineData("A0000000-0000-4000-8000-0000000000C5")]
[InlineData("A00000000000400080000000000000C5")]
public void Every_campaign_spelling_a_binding_can_hold_yields_one_fingerprint(string spelling)
{
    (SagaMemoryScopeKind kind, string? campaignId) = SagaMemoryScopeClassifier.Classify(hasSession: true, (long)SagaMemoryScopeKind.Campaign, spelling);
    Assert.Equal(key.Fingerprint(MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Campaign, "x")),
                 key.Fingerprint(SagaErasureWriteGate.SagaIdentity(kind, campaignId, "x")));
}

[Fact]
public void An_unparseable_bound_campaign_fails_closed_instead_of_matching_loosely()
{
    (SagaMemoryScopeKind kind, string? campaignId) = SagaMemoryScopeClassifier.Classify(true, (long)SagaMemoryScopeKind.Campaign, "not-a-campaign");
    Assert.Equal("not-a-campaign", campaignId);   // the classifier keeps an unrecognizable identity verbatim
    Assert.Throws<FormatException>(() => SagaErasureWriteGate.SagaIdentity(kind, campaignId, "x"));
}

[SkippableTheory]
[InlineData("lost", ErrorCodes.MemoryErasure.KeyLost)]               // credentials.Delete(Service, MemoryErasureFingerprintKeyAccount)
[InlineData("overwritten", ErrorCodes.MemoryErasure.KeyLost)]        // a second CreateKey after the delete
[InlineData("unreadable", ErrorCodes.MemoryErasure.KeyUnavailable)]  // CountingOsCredentialStore.FailWith = Unavailable
[InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]   // the account holds "not-a-key"
public async Task A_key_that_cannot_verify_the_evidence_fails_closed_and_writes_nothing(string state, string code)
{
    // a fresh keyring, and content that was never erased: key loss withholds the whole store
    MemoryErasureGuardException refused = await Assert.ThrowsAsync<MemoryErasureGuardException>(() => harness.Store.InsertAsync(…));
    Assert.Equal(code, refused.Error.Code);
    Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));
}
```

A pending version-5 sweep implies recorded Core below 13, where evidence reads as empty (§5.4), and at Core 13 the `session_campaign_bindings` identity guards admit only the canonical spelling. So the two spelling cases above are the whole of Review Focus 1 at the live chokepoint.

Add these store cases:
- `No_fingerprints_means_no_keychain_io`: inserts in Global, Campaign and unbound-Session scopes all return `Written`, then `credentials.Calls == 0`.
- `An_insert_that_meets_evidence_committed_after_its_probe_retries_once_and_is_suppressed`: a test-local provider wraps a fresh keyring. Its first `TryCopyLatched()` inserts the fingerprint synchronously through `harness.CreateSiblingContext()` and returns `null`. The outcome is `Suppressed`, `saga_memories` holds 0 rows for that content, and the wrapper saw exactly one `OpenExisting`.

`SagaExtractionServiceTests`. These cases pass the provider through `BuildScope(…, erasureKeys)`, and roll the cursor back the way `Extraction_does_not_write_a_memory_the_operator_retired_and_still_advances_the_watermark` does:

| Case | What it asserts |
|---|---|
| `Extraction_neither_rewrites_nor_re_embeds_an_erased_conclusion_and_still_advances_the_cursor` | Setup: pass 1 writes; seed the fingerprint for the written row's scope; `Store.DeleteAsync(id)`; roll the cursor back. After pass 2: `CountMemoriesAsync() == 0`, `weave.EmbedCallCount == 1`, `intelligence.CallCount == 2`, and the watermark equals the latest entry. |
| `A_page_mixing_erased_and_fresh_candidates_writes_and_embeds_only_the_fresh_one` | Pass 2 returns the erased conclusion and "The operator uses tabs.". Afterwards: `EmbedCallCount == 2` (one per pass), one memory ("The operator uses tabs."), and the cursor advanced. |
| `An_erased_candidate_counts_as_page_progress_when_another_candidate_fails_to_embed` | Same page with `weave.EmbedShouldFail = true`. `CountMemoriesAsync() == 0`, and the cursor still advances. |
| `Extraction_defers_before_the_model_call_while_the_erasure_key_is_lost` | Fingerprint present, credential deleted, fresh keyring. `ExtractWithLeaseAsync` returns `SagaExtractionOutcome.DeferredForErasureKey`. `intelligence.CallCount == 0`, `weave.EmbedCallCount == 0`, and the watermark is `null`. |
| `ExecuteAsync_ErasureKeyDeferral_DoesNotClimbTheRetryLadderAndResumesOnceTheKeyReturns` | The service is built with `RetryBaseDelayForTests` and `ErasureKeyDeferralDelayForTests` both 10 ms. Wait until `weave.AvailabilityCheckCount >= 6`, more passes than the five-attempt ladder allows. Then: `service.RetryAttemptForTests(sessionId) == 0`, `PendingSegmentsForTests(sessionId)` is non-empty, and `intelligence.CallCount == 0`. Next, restore the saved credential and call `keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe)`. Finally, wait until `intelligence.CallCount == 1`. |

`SagaMemoryMidUpgradeWriteTests.A_memory_written_while_the_version_twelve_sweep_is_pending_needs_no_erasure_key`:
1. Install `CoreSchemaVersionElevenFixture.ChainSet()`.
2. Call `GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, TestDimensions, Token)` once. Core v12 declares `AnnalReviewEventBackfill` (`GrimoireSchemaVersionChains.cs:258`), so the call stops at recorded 11 with v12's DDL applied, as the v4→v5 case in this file does.
3. Assert that state before writing: `upgraded.Core.SchemaVersion == 11`, and `memory_erasure_fingerprints` is absent from `sqlite_master`.
4. Insert through `CreateStore(db, keyring)`, whose keyring wraps a `CountingOsCredentialStore`. The outcome is `Written` and `Calls == 0`.

`NullableInterfaceConstructorDefaultTests` (Task 8 adds `LexiconService` and Task 11 adds `CovenantMutationKernel` and `CovenantStore`, R24):

```csharp
public static TheoryData<Type> ErasureChokepointOwners => new() { typeof(SagaMemoryStore), typeof(SagaErasureWriteGate) };

[Theory] [MemberData(nameof(ErasureChokepointOwners))]
public void Every_erasure_chokepoint_owner_requires_the_key_provider(Type owner) =>
    Assert.All(owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
        constructor => Assert.Contains(constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IMemoryErasureKeyProvider) && !parameter.HasDefaultValue));
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaErasureSuppressionTests|FullyQualifiedName~SagaExtractionServiceTests|FullyQualifiedName~SagaMemoryMidUpgradeWriteTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests"
```

Expected RED: the build fails, because `SagaErasureWriteGate`, `DeferredForErasureKey`, the harness parameter and the four-argument store constructor do not exist. With stubs in place, the fingerprinted insert returns `Written`.

- [ ] **Step 3: Implement**

- **`InsertCoreAsync`** keeps its nine parameters, so its acquisition identity `InsertCoreAsync(9)` is unchanged.
  - Move `OpenConnectionAsync(cancellationToken)` in front of `MemoryErasureGuard.RunWithRetryAsync((SqliteConnection)connection, MemoryReviewStore.Saga, erasureKeys, guard => SqliteBusyRetry.ExecuteAsync(…), cancellationToken)`.
  - Inside the delegate, directly after the retirement-suppression block and before `INSERT INTO "saga_memories"`, call `MemoryErasureGuard.CheckAsync((SqliteConnection)connection, (SqliteTransaction)transaction, guard, () => SagaErasureWriteGate.SagaIdentity(scopeKind, scopeCampaignId, content), cancellationToken)`. The factory form means an installation with no evidence never parses the bound Campaign, so its insert behaves exactly as today.
  - Every non-`Allowed` verdict rolls back first: `Withheld` → return `Suppressed`; `RetryWithKey` → throw `MemoryErasureRetryException`; `KeyLost` → throw `MemoryErasureGuardException(MemoryErasureGuard.KeyLostError)`. A `FormatException` from the factory propagates after the rollback, which fails closed.
- **`SagaErasureWriteGate`** opens `db`'s scoped connection the way `SagaMemoryStore.OpenConnectionAsync` (:788) does. `IsWithheldAsync` classifies with `SagaMemoryScopeClassifier.ResolveForSessionAsync(connection, null, sessionId, ct)`, then calls the factory form of `CheckAsync(…, transaction: null, …)`:
  - `Withheld` → true;
  - `Allowed` → false;
  - anything else → throw `MemoryErasureGuardException`, which becomes `Retry`, and the cursor holds.
- **`ExtractForSessionAsync`.**
  - Resolve `SagaErasureWriteGate erasureGate = services.GetRequiredService<SagaErasureWriteGate>()` beside the store (:927).
  - For each page, after the `newEntries.Count == 0` check (:1037) and before `TryBeginExternalEffectGroup` (:1049), call `erasureGate.PrepareAsync(ct)`. A failure returns `new SagaExtractionAttemptResult(SagaExtractionOutcome.DeferredForErasureKey, sourceSegment)`, with no model call and no effect group. On success, dispose the context at the end of the page.
  - In the embedding loop, after `authorizedForEmbedding` (:1177) and before `weave.EmbedAsync` (:1184), a withheld candidate (checked on its trimmed text, which is what `InsertAsync` receives) increments `withheldBeforeEmbedding` and `continue`s.
  - Initialize `int suppressedCount = withheldBeforeEmbedding;` (:1200), so the existing `eligibleCount > 0 && insertedCount == 0 && suppressedCount == 0` guard treats a withheld candidate as progress.
  - The Suppressed log reads "…because the operator already retired or erased an equivalent conclusion."
- **Consumer loop,** after the `DeferredForMaintenance` branch and before the `Retry` branch (:607):

```csharp
if (attempt.Outcome == SagaExtractionOutcome.DeferredForErasureKey)
{
    _logger.LogWarning("Saga extraction for session {SessionId} deferred: Saga erasure fingerprints exist and the erasure key is not available.", sessionId);
    ScheduleRetry(sessionId, _erasureKeyDeferralDelay, stoppingToken);
    continue;
}
```

  The branch keeps the pending key, exactly as the `Retry` branch does during backoff. It never calls `NextRetryDelay`, so the ladder is never touched.
- **Acquisition inventory.** Three rows carry the store's classification (`GrimoirePathAuthority.LiveGrimoire`, `GrimoireAcquisitionKind.ServingRawOrdinary`, `GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory`, `null`):
  - `SagaErasureWriteGate.OpenConnectionAsync(1)` → `ProviderOpen`, `"db.Database.OpenConnectionAsync"`, `db.Database.OpenConnectionAsync(cancellationToken)`;
  - `PrepareAsync(1)` → `ProviderOpen`, `"OpenConnectionAsync"`, `OpenConnectionAsync(cancellationToken)`;
  - `IsWithheldAsync(4)` → the same.

  Set `ExpectedProductionAcquisitionCount = 449`.
- **Benchmark catalog:** as in Task 4.

- [ ] **Step 4: Run the tests to verify they pass**

Run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaMemoryStoreTests|FullyQualifiedName~SagaCurationStoreTests|FullyQualifiedName~SagaSuppressionTests|FullyQualifiedName~SagaAnnalsWriteThroughTests|FullyQualifiedName~AnnalsErasureTests|FullyQualifiedName~AnnalsStoreTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~SessionAttachmentIdentitySpellingTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~SagaCampaignScopedRetrievalTests|FullyQualifiedName~CovenantProtectedArtifactErasureContentTests|FullyQualifiedName~LexiconProtectedInspectionTests|FullyQualifiedName~SagaVectorMirrorTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```

Then regenerate and verify the capsules with the Task 4 commands. The diff may touch only `SagaMemoryStore` and `SagaExtractionService` rows and new `SagaErasureWriteGate`, `MemoryErasureGuard` and `MemoryErasureEvidence` rows under the `SagaExtractionService` root, with zero diagnostics.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Remove the `CheckAsync` call from `InsertCoreAsync` | `Fingerprinted_content_in_the_same_scope_is_suppressed_and_nothing_is_written` |
| Treat `RetryWithKey` as `Allowed` in `InsertCoreAsync` | `An_insert_that_meets_evidence_committed_after_its_probe_retries_once_and_is_suppressed` |
| Make `SagaIdentity` fall back to the raw Campaign string when it cannot parse | `An_unparseable_bound_campaign_fails_closed_instead_of_matching_loosely` |
| Build the identity from `content.Trim()` | `Only_the_exact_bytes_in_the_exact_scope_are_suppressed("global-trailing-space")` |
| Remove the pre-embed `IsWithheldAsync` skip | `A_page_mixing_erased_and_fresh_candidates…` (`EmbedCallCount` is 3) |
| Initialize `suppressedCount = 0` | `An_erased_candidate_counts_as_page_progress…` |
| Remove the per-page `PrepareAsync` deferral | `Extraction_defers_before_the_model_call_while_the_erasure_key_is_lost` |
| Route the deferral through `NextRetryDelay` | `ExecuteAsync_ErasureKeyDeferral_DoesNotClimbTheRetryLadder…` |
| Give `erasureKeys` a `= null` default | `Every_erasure_chokepoint_owner_requires_the_key_provider`, `Every_nullable_interface_constructor_default_is_removed_or_allowed_with_a_reason` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/SagaErasureWriteGate.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Data/SagaErasureSuppressionTests.cs tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaMemoryMidUpgradeWriteTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/EmbeddingsResetServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIdentitySpellingTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Data/SagaCampaignScopedRetrievalTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCampaignScopedResetTests.cs tests/RetroDownfall.Arcanum.Tests/Data/SagaMemoryStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Api/LexiconProtectedInspectionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: withhold erased Saga content at insert and before embedding" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Lexicon chokepoint and agent delete refusals

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconDeletionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs`: two members whose defaults fail closed, following the existing `FindAllLifecycleIdentityForDeletionAsync` default.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`:
  - a required `IMemoryErasureKeyProvider erasureKeys` after `options`;
  - `UpsertCoreAsync`;
  - `DeleteByNameAsync`'s body moves to the origin overload;
  - `FindAgentDeletionTargetAsync`;
  - private `ReadAgentDeletionTargetAsync`.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs` (`ExecuteDeleteLexiconAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`. The `delete_lexicon` `Description` (:1012) becomes "Removes an active, unpinned Lexicon entity by name when a memory is obsolete; retired and pinned entities are managed by the operator."
- Modify: `docs/Arcanum.DESIGN.md` §10.6 "Write path" paragraph, one sentence each: the fingerprint refusal (the sentence names the code `Lexicon.SuppressedNameRefused` verbatim); the shared text and bounded oracle; the `delete_lexicon` refusals (naming `Lexicon.PinnedMutationRefused`), with the operator's delete unchanged.
- The `ArcanumErrorMapper` 409 arms for `Lexicon.SuppressedNameRefused` and `Lexicon.PinnedMutationRefused` are Task 1's (R4). This task does not touch the mapper or its tests.
- Modify every `LexiconService` construction site under `tests/RetroDownfall.Arcanum.Tests/` (R5). The list below is every site at `fe63b204`, found by grep for `new LexiconService(` and target-typed `new(…NullLogger<LexiconService>…)`:
  - `Weave/SessionAttachmentIdentitySpellingTests.cs` (:506)
  - `Annals/AnnalsErasureTests.cs` (:225), `Annals/LexiconAnnalsWriteThroughTests.cs` (:576), `Annals/AnnalsStoreTests.cs` (:263)
  - `Mcp/ArcanumInternalToolServerTests.cs` (:3697; the provider goes before the positional `FixtureLabeledArtifactGuard.For(db)`)
  - `Lexicon/LexiconCurationInspectionTests.cs` (:46, :331), `Lexicon/LexiconServiceTests.cs` (:135), `Lexicon/LexiconMemoryReviewServiceTests.cs` (:585), `Lexicon/LexiconCampaignScopeTests.cs` (:70), `Lexicon/LexiconPinTests.cs` (:319)
  - `Lexicon/LexiconCorrectionConcurrencyTests.cs` (`Create`, :132) and `Lexicon/LexiconLifecycleConcurrencyTests.cs` (`Create`, :159)
  - `Data/DataRetentionServiceTests.cs` (`CreateLexiconService`, :3968, which the partial `DataRetentionApplyBoundaryTests.cs` also calls)
  - `Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs` (:402)
  - `Data/Schema/LexiconMidUpgradeCompatibilityTests.cs` (`CreateService`, :243, gains `IMemoryErasureKeyProvider? erasureKeys = null` and passes `erasureKeys ?? MemoryErasureTestKeys.Isolated()`)
  - `Lexicon/LexiconCorrectionTests.cs` (:704): `CorrectionFixture` gains `IMemoryErasureKeyProvider? erasureKeys = null` and exposes `ErasureKeys`.

  Production composes `LexiconService` only through `AddArcanumInfrastructure` (`ServiceCollectionExtensions.cs:1371`), where Task 2 registers the provider.
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/FakeLexiconService.cs` (implement both new members)
- Modify (closed inventories):
  - `Build/LexiconCurationArchitectureTests.cs` (`Projections`, `Readers`, the `executed` list);
  - `NullableInterfaceConstructorDefaultTests.ErasureChokepointOwners` (+`typeof(LexiconService)`);
  - `Support/GrimoireConnectionAcquisitionInventory.cs` (+1 row, and the `DeleteByNameAsync(3)` identity becomes `DeleteByNameAsync(4)`) and `ExpectedProductionAcquisitionCount` **449 → 450** in `Data/GrimoireConnectionAcquisitionInventoryTests.cs`;
  - the three benchmark files, as in Task 4 (+1 Core file; the catalog lists every `.cs` under `src/RetroDownfall.Arcanum.Core/`).
  - The capsule TSV does not change: `LexiconService` and `ArcanumInternalToolServer` have no rows in it at `fe63b204` (`grep -c` gives 0 for both). Step 4 proves the manifest still matches.
- Test (new): `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconErasureSuppressionTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconAgentDeletionTests.cs`
- Test (changed or added cases): `Mcp/ArcanumInternalToolServerTests.cs` (the existing `delete_lexicon` theory plus two cases); `Lexicon/LexiconLifecycleTests.cs` (default fail-closed case); `Data/Schema/LexiconMidUpgradeCompatibilityTests.cs` (the upgrade-window case)

**Interfaces:**
- Depends on: Task 6. Also Task 1 (the two error codes and their mapper arms) and Task 7 (it creates `ErasureChokepointOwners`).
- Consumes:
  - From Task 6: the guard (`RunWithRetryAsync`, `CheckAsync`, `KeyLostError`, the exceptions) and `MemoryErasureTestKeys`.
  - From Task 1: `MemoryErasureIdentity.ForLexicon(Guid? campaignId, string name)`, `ErrorCodes.Lexicon.SuppressedNameRefused` and `ErrorCodes.Lexicon.PinnedMutationRefused`, and their 409 mapper arms.
- Produces (Core, `RetroDownfall.Arcanum.Core.Lexicon`):
  - `public enum LexiconDeletionOrigin { Operator = 1, Agent = 2 }`
  - `public sealed record LexiconAgentDeletionTarget(Guid EntryId, bool IsRetired, bool IsPinned)`
  - `public static class LexiconAgentRefusals { const string OperatorManaged = "This Lexicon entry is managed by the operator in this scope, so nothing was recorded."; const string RetiredDeletion = "A retired Lexicon entry can only be removed by the operator; it was left unchanged."; }`
  - `ILexiconService.FindAgentDeletionTargetAsync(string name, LexiconScope scope, CancellationToken cancellationToken = default) → Task<Result<LexiconAgentDeletionTarget?>>`. The default is a `Lexicon.SearchFailed` failure.
  - `ILexiconService.DeleteByNameAsync(string name, LexiconScope scope, LexiconDeletionOrigin origin, CancellationToken cancellationToken = default) → Task<Result<bool>>`. The default is a `Lexicon.WriteFailed` failure.
  - `LexiconService(ArcanumDbContext db, ILogger<LexiconService> logger, IOptionsMonitor<ArcanumSettings> options, IMemoryErasureKeyProvider erasureKeys, ICovenantLabeledArtifactGuard? labeledArtifactGuard = null, IMemoryReviewTokenCodec? reviewTokenCodec = null, TimeProvider? reviewTimeProvider = null)`. Task 14 appends `LexiconErasureDependencies? erasure = null` after these and reuses `erasureKeys` (R5).

- [ ] **Step 1: Write the failing tests**

`LexiconErasureSuppressionTests` uses `[Collection("Grimoire")]` and a `CorrectionFixture(fixture, annals: true, erasureKeys: MemoryErasureTestKeys.Isolated(credentials))`. The fingerprint is seeded with `SeedFingerprintAsync(owner.Connection, key, MemoryErasureIdentity.ForLexicon(campaignId, "Entity"), Token)`.

```csharp
[SkippableTheory] [InlineData("Entity")] [InlineData("  entity ")] [InlineData("ENTITY")]
public async Task Scribe_of_an_erased_name_is_refused_and_records_nothing(string name)
{
    Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync(name, "Person", ["alpha"], LexiconScope.Global);
    Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribed.Error.Code);
    Assert.Equal(LexiconAgentRefusals.OperatorManaged, scribed.Error.Message);
    Assert.Empty(await owner.SnapshotAsync());
}

[SkippableTheory]
[InlineData("lost", ErrorCodes.MemoryErasure.KeyLost)]
[InlineData("overwritten", ErrorCodes.MemoryErasure.KeyLost)]
[InlineData("unreadable", ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]
public async Task A_key_that_cannot_verify_the_evidence_fails_closed_before_the_transaction(string state, string code)
{
    // fresh keyring; a name that was never erased, because key loss withholds the whole store (R16)
    Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Other", "Person", ["alpha"], LexiconScope.Global);
    Assert.Equal(code, scribed.Error.Code);
    Assert.Equal(snapshot, await owner.SnapshotAsync());
}
```

Add these cases:
- `An_erased_identity_that_still_has_a_live_row_is_refused`: `SeedAsync()` first, then the fingerprint. The scribe returns `SuppressedNameRefused` and the snapshot is unchanged.
- `Fingerprints_bind_the_exact_scope`, with "Entity" fingerprinted in Campaign A and "Other" fingerprinted Global:
  - a Global scribe of "Entity" succeeds;
  - a Campaign B scribe of "Entity" succeeds;
  - a Campaign A scribe of "Entity" returns `SuppressedNameRefused`;
  - a Campaign A scribe of "Other" succeeds.
- `No_fingerprints_means_no_keychain_io`: two scribes succeed, and `Calls == 0`.

`LexiconAgentDeletionTests` covers the service-level agent paths:

```csharp
[SkippableTheory] [InlineData("retired")] [InlineData("pinned")]
public async Task Agent_origin_delete_refuses_curated_entries_inside_its_transaction(string state)
{
    // SeedAsync, then Service.RetireAsync(target, null) or Service.PinAsync(target, null)
    Result<bool> deleted = await owner.Concrete.DeleteByNameAsync("Entity", LexiconScope.Global, LexiconDeletionOrigin.Agent);
    Assert.Equal(state == "retired" ? ErrorCodes.Lexicon.RetiredMutationRefused : ErrorCodes.Lexicon.PinnedMutationRefused, deleted.Error.Code);
    Assert.Equal(state == "retired" ? LexiconAgentRefusals.RetiredDeletion : LexiconAgentRefusals.OperatorManaged, deleted.Error.Message);
    Assert.Equal(snapshot, await owner.SnapshotAsync());
}
```

Add these cases:
- `Operator_origin_delete_still_removes_retired_and_pinned_entries`: `[Theory]` over `"retired"` and `"pinned"`. `DeleteByNameAsync(…, LexiconDeletionOrigin.Operator)` returns `true`, and `lexicon_entries` and `annal_claims` hold 0 rows.
- `Agent_deletion_target_reports_exact_scope_lifecycle`:
  - active → `(id, false, false)`;
  - retired → `IsRetired`;
  - pinned → `IsPinned`;
  - missing → `null`;
  - a Campaign entry is `null` from Global.
- `LexiconLifecycleTests.Agent_deletion_defaults_fail_closed`: `new LegacyDeletionLexicon()` yields `Lexicon.SearchFailed` and `Lexicon.WriteFailed`.

`ArcanumInternalToolServerTests`:
- In `ToolsCall_delete_lexicon_respects_retired_identity_and_purge_disposition`, the verdict becomes `bool refused = retired || (labeled && disposition != "purged");`.
  - The six `retired: true` rows now refuse, and each keeps the snapshot unchanged. That includes `(true, true, "purged")`, where the refusal must precede the fixture purger.
  - When `retired` is true, also assert `Assert.Equal(LexiconAgentRefusals.RetiredDeletion, result.Content![0].Text)`.
- `ToolsCall_delete_lexicon_refuses_a_pinned_entry_before_any_purge(bool labeled)`: pin through `owner.Service.PinAsync(before.Target, null)`, using `LifecyclePurger(owner, purgeDb, "purged")`. The result is an error, its text is `OperatorManaged`, and the snapshot is unchanged.
- `ToolsCall_scribe_lexicon_of_an_erased_name_returns_the_operator_managed_message`: uses a real `LexiconService` with the fingerprint seeded, so that `result.IsError` and the text equals `LexiconAgentRefusals.OperatorManaged`.

`LexiconMidUpgradeCompatibilityTests.A_scribe_while_the_version_twelve_sweep_is_pending_needs_no_erasure_key`:
1. Install the v11 fixture, then run the head chain once. It stops at recorded 11, as in Task 7.
2. Assert that recorded Core reads 11 and the fingerprint table is absent.
3. Scribe through `CreateService(db, erasureKeys: keyring)` over a `CountingOsCredentialStore`: success, and `Calls == 0`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconErasureSuppressionTests|FullyQualifiedName~LexiconAgentDeletionTests|FullyQualifiedName~ArcanumInternalToolServerTests.ToolsCall_delete_lexicon|FullyQualifiedName~ArcanumInternalToolServerTests.ToolsCall_scribe_lexicon_of_an_erased|FullyQualifiedName~LexiconLifecycleTests.Agent_deletion_defaults|FullyQualifiedName~LexiconMidUpgradeCompatibilityTests"
```

Expected RED: the build fails, because the contracts, the overload and the constructor parameter do not exist. Once stubbed, the fingerprinted scribe succeeds and `(true, true, "purged")` deletes the retired entry.

- [ ] **Step 3: Implement**

- **`UpsertCoreAsync`.**
  - Move `OpenConnectionAsync(cancellationToken)` in front of `MemoryErasureGuard.RunWithRetryAsync((SqliteConnection)connection, MemoryReviewStore.Lexicon, erasureKeys, guard => SqliteBusyRetry.ExecuteAsync(…), cancellationToken)`, inside the existing `try`. That keeps one open and the acquisition identity `UpsertCoreAsync(6)`.
  - Directly after `ReadByNormalizedAsync` and before the retired refusal, call `MemoryErasureGuard.CheckAsync((SqliteConnection)connection, null, guard, MemoryErasureIdentity.ForLexicon(scope.CampaignId, trimmedName), cancellationToken)`, whether or not a row exists:
    - `Withheld` → `throw new InspectionException(new Error(ErrorCodes.Lexicon.SuppressedNameRefused, LexiconAgentRefusals.OperatorManaged))`
    - `RetryWithKey` → `throw new MemoryErasureRetryException()`
    - `KeyLost` → `throw new MemoryErasureGuardException(MemoryErasureGuard.KeyLostError)`

  The existing inner `catch` rolls back. Add `catch (MemoryErasureGuardException exception) { return exception.Error; }` beside the `InspectionException` catch (:236), ahead of the generic `WriteFailed` catch. `RunWithRetryAsync` consumes `MemoryErasureRetryException` before either catch sees it.
- **`ReadAgentDeletionTargetAsync(connection, normalized, scopeKey, curation, ct)`:**
  - It selects `Id, RetiredAtUtc, PinnedAtUtc`, or `Id, NULL AS RetiredAtUtc, NULL AS PinnedAtUtc` below v11, split the way `EntryColumnsFor(curation)` is, so that both SQL texts the inventory reads project `Id,RetiredAtUtc,PinnedAtUtc`.
  - It filters `WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey LIMIT 1` and reads through `ExecuteReaderAsync`.
  - It builds `new LexiconAgentDeletionTarget(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture), !reader.IsDBNull(1), !reader.IsDBNull(2))`, so its reader slots are exactly `GetString:0`.

  **`FindAgentDeletionTargetAsync`** validates the name exactly as `FindAllLifecycleIdentityForDeletionAsync` (:268) does.
- **`DeleteByNameAsync(name, scope, origin, ct)`** holds today's body; the three-argument form delegates with `Operator`. For `Agent` only, immediately after `BEGIN IMMEDIATE`, re-read the target with `ReadAgentDeletionTargetAsync`, then `ROLLBACK` and return the typed failure:
  - retired (checked first) → `Lexicon.RetiredMutationRefused` with `RetiredDeletion`;
  - pinned → `Lexicon.PinnedMutationRefused` with `OperatorManaged`.
- **Tool.** After scope resolution and before the purger block, call `FindAgentDeletionTargetAsync`:
  - failure → `ToolError(message)`;
  - retired → `ToolError(LexiconAgentRefusals.RetiredDeletion)`;
  - pinned → `ToolError(LexiconAgentRefusals.OperatorManaged)`.

  Then call `DeleteByNameAsync(name, lexiconScope, LexiconDeletionOrigin.Agent, cancellationToken)`.
- **`FakeLexiconService`:** the target is `new(entry.Id, false, false)` or `null`; the overload delegates to its own delete.
- **Inventories.**
  - In `LexiconCurationArchitectureTests`:
    - add `{ Service, "ReadAgentDeletionTargetAsync", 0, "Id,RetiredAtUtc,PinnedAtUtc" }` to `Projections`;
    - add `{ Service, "ReadAgentDeletionTargetAsync", "GetString:0" }` to `Readers`;
    - add `"ReadAgentDeletionTargetAsync"` to the `executed` list in `Query_and_reader_inventories_are_bidirectional_including_shared_mappers`.

    The new `DeleteByNameAsync` overload shares the name the `projected` scan already excludes.
  - In the acquisition inventory, add `("…/Lexicon/LexiconService.cs", "LexiconService", "FindAgentDeletionTargetAsync(3)", ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)")` with its neighbours' classification, rename the `DeleteByNameAsync(3)` row to `DeleteByNameAsync(4)`, and set `ExpectedProductionAcquisitionCount = 450`.
- **Benchmark catalog:** as in Task 4.

- [ ] **Step 4: Run the tests to verify they pass**

Run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumInternalToolServerTests|FullyQualifiedName~LexiconPinTests|FullyQualifiedName~LexiconServiceTests|FullyQualifiedName~LexiconCorrectionTests|FullyQualifiedName~LexiconCorrectionConcurrencyTests|FullyQualifiedName~LexiconLifecycleTests|FullyQualifiedName~LexiconLifecycleConcurrencyTests|FullyQualifiedName~LexiconCampaignScopeTests|FullyQualifiedName~LexiconCurationInspectionTests|FullyQualifiedName~LexiconMemoryReviewServiceTests|FullyQualifiedName~LexiconAnnalsWriteThroughTests|FullyQualifiedName~AnnalsErasureTests|FullyQualifiedName~AnnalsStoreTests|FullyQualifiedName~SessionAttachmentIdentitySpellingTests|FullyQualifiedName~CovenantProtectedArtifactErasureContentTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~UnseenServantDaemonJobTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~LexiconCurationArchitectureTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```

Pass criteria:
- `LexiconPinTests.Pin_does_not_block_scribe_correction_retirement_reinstatement_or_hard_delete` is green, which shows the operator delete is unchanged.
- `ArcanumErrorMapperTests` is green unchanged: Task 1's arms already map both codes to 409.
- `HostedGrimoireProducerInventoryTests` is green with the capsule TSV untouched. A mismatch here is a finding to report, not a regeneration to fold into this commit.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Remove the `CheckAsync` call from `UpsertCoreAsync` | `Scribe_of_an_erased_name_is_refused_and_records_nothing` |
| Run the check only when `existing is null` | `An_erased_identity_that_still_has_a_live_row_is_refused` |
| Build the identity from `name` without trimming | `…("  entity ")` |
| Build the identity from `scope` Global always | `Fingerprints_bind_the_exact_scope` |
| Remove the `MemoryErasureGuardException` catch, so a key refusal becomes `WriteFailed` | `A_key_that_cannot_verify_the_evidence_fails_closed_before_the_transaction` (all rows) |
| Drop the tool pre-check | `ToolsCall_delete_lexicon_respects_retired_identity_and_purge_disposition(true, true, "purged")`, `ToolsCall_delete_lexicon_refuses_a_pinned_entry_before_any_purge(true)` |
| Drop the `Agent` re-read inside the transaction | `Agent_origin_delete_refuses_curated_entries_inside_its_transaction` |
| Apply that re-read to `Operator` too | `LexiconPinTests.Pin_does_not_block_scribe_correction_retirement_reinstatement_or_hard_delete`, `Operator_origin_delete_still_removes_retired_and_pinned_entries` |
| Give `erasureKeys` a `= null` default | `Every_erasure_chokepoint_owner_requires_the_key_provider(LexiconService)` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Lexicon/LexiconDeletionContracts.cs src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconErasureSuppressionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconAgentDeletionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionConcurrencyTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleConcurrencyTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationInspectionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconMemoryReviewServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCampaignScopeTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconPinTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIdentitySpellingTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/LexiconMidUpgradeCompatibilityTests.cs tests/RetroDownfall.Arcanum.Tests/Support/FakeLexiconService.cs tests/RetroDownfall.Arcanum.Tests/Build/LexiconCurationArchitectureTests.cs tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: refuse erased Lexicon scribes and agent deletes of curated entries" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Covenant canonical schema version 6

**Files:** (`Canon/` = `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/`; `T/` = `tests/RetroDownfall.Arcanum.Tests/`; `B/` = `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteAuthorizationKind.cs` (append `CovenantEntryErasure = 12`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionInitializer.cs` (`FunctionNames[12]`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs` (`InsertReceiptAsync`: new `EntryId` column)
- Modify (head): `Canon/Tables/covenant_key_epochs.sql`, `Canon/Tables/covenant_mutation_receipts.sql`, `Canon/Tables/covenant_curation_heads.sql` (KeyEpoch comment paragraph only); `Canon/Triggers/covenant_entries_guard_delete.sql`, `covenant_versions_guard_delete.sql`, `covenant_version_attachment_provenance_guard_delete.sql`, `covenant_mutation_receipts_guard_delete.sql`, `covenant_curation_versions_guard_delete.sql`, `covenant_curation_receipts_guard_delete.sql`, `covenant_search_outbox_guard_delete.sql`, `covenant_heads_key_epoch_insert.sql`, `covenant_heads_key_epoch_update.sql`, `covenant_heads_key_epoch_delete.sql`
- Create (head): `Canon/Triggers/covenant_key_epochs_guard_incarnation.sql`, `Canon/Triggers/covenant_key_epochs_guard_delete.sql`, `Canon/Triggers/covenant_curation_heads_guard_delete.sql`
- Create: `Canon/Transitions/V6/` — the 24 files named in Step 3
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs` (`CovenantCanonicalSchemaVersion = 6`, remarks for v5/v6, `SourcePins[(CovenantCanonical, 6)]`; no `Backfills` entry)
- Create: `T/Fixtures/CovenantCanonicalSchemaVersionFiveFixture.cs`; Modify: `T/Fixtures/CovenantCanonicalSchemaVersionFourFixture.cs`, `T/Fixtures/CovenantCanonicalSchemaVersionThreeFixture.cs` (rebase on Five)
- Create: `T/Data/Schema/CovenantCanonicalVersionSixEvolutionTests.cs`
- Modify tests: `T/Data/Schema/GrimoireSchemaSourceFingerprintTests.cs`, `T/Data/Schema/GrimoireSchemaVersionChainTests.cs`, `T/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`, `T/Data/Schema/CovenantCanonicalSchemaTests.cs`, `T/Data/Covenant/CovenantSqliteConnectionInitializerTests.cs`, `T/Data/Covenant/CovenantMutationKernelTests.cs`, `T/Benchmarks/GrimoireAdmissionBenchmarkManifestTests.cs` (`.sql` count 462 → 489)
- Modify (inventories): `B/grimoire-admission-input-catalog-v1.txt` (+27 `.sql` paths), `B/grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`, line 69), `B/AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19); `T/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated in Step 4; expected unchanged). Acquisition inventory: no new acquisition (delta 0).

**Interfaces:**
- Consumes: the Core v13 step (Task 3) already in the chain and in the transition-resource list, and Task 3's `.sql` count of 462.
- Produces: `CovenantSqliteAuthorizationKind.CovenantEntryErasure = 12` → `arcanum_covenant_entry_erasure_authorized()`; `covenant_key_epochs.IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0)` (the binding epoch: backfilled from `KeyEpoch`, 0 for rows created at v6, immutable); `covenant_mutation_receipts.EntryId TEXT NULL` + `idx_covenant_mutation_receipts_entry`, populated for Applied and NoChange; the nine canonical delete guards that admit entry erasure; `CovenantCanonicalSchemaVersionFiveFixture { PublishedFingerprint, Objects, Fingerprint, ChainSet() }`.

- [ ] **Step 1: Write the failing tests**

(a) **Capture the v5 pin before any head edit.** Create `CovenantCanonicalSchemaVersionFiveFixture` in the `TwoFixture`/`FourFixture` shape: `PublishedFingerprint = "E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925"`; one private raw-string const per object Step 3 edits, copied byte-exact from `git show fe63b204:<path>` and applied as `Sql = text.ReplaceLineEndings("\n") + "\n"` (13 objects: the 2 tables + `covenant_curation_heads`, the 7 guards, the 3 `covenant_heads_key_epoch_*` triggers); `AddedAtSix = { "covenant_key_epochs_guard_incarnation", "covenant_key_epochs_guard_delete", "covenant_curation_heads_guard_delete" }` excluded; `Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects)`; `ChainSet()` at `version: 5` with steps `ToVersion <= 5`. Rebase `FourFixture.Objects` on `Five.Objects` (minus `covenant_review_*`, view rows stripped) and `ThreeFixture.Objects` on `Five.Objects` (minus review objects and `covenant_utc_instant_columns`). In `GrimoireSchemaSourceFingerprintTests`:

```csharp
[Fact]
public void Covenant_version_five_reconstruction_matches_the_pinned_fingerprint()
{
    Assert.Equal("E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925", CovenantCanonicalSchemaVersionFiveFixture.Fingerprint);
    GrimoireSchemaVersionChain canonical = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);
    Assert.True(canonical.TryGetStep(6, out _));
    Assert.Equal(CovenantCanonicalSchemaVersionFiveFixture.Fingerprint, canonical.SourceDefinitionFingerprintFor(5));
}
```
The first assertion must already pass (it is the capture); `TryGetStep(6)` must fail. Keep `Current_pre_review_trees_match_their_published_fingerprints` green on the rebased Four.

(b) **Chain and resources.** `GrimoireSchemaVersionChainTests.The_shipped_head_versions_are_the_ones_this_binary_declares`: `Assert.Equal(6, GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion)`. `GrimoireSchemaTransitionResourceTests.The_shipped_catalog_declares_only_the_steps_its_tiers_have_taken`: add `(CovenantCanonical, 6)` and append after `"covenant_review_events_head_update"` the 24 names of Step 3 in ordinal order. In `GrimoireSchemaTransitionResourceTests.A_published_fingerprint_covers_head_objects_alone` correct the stale comment: canonical leaves v5 here but stays raw (§3).

(c) **`CovenantCanonicalVersionSixEvolutionTests`** (static ctor `SqliteNativeRuntime.Instance.Initialize()`, `EvolutionScratchDatabase`, `GrimoireSchemaTestInstaller.InstallAsync(connection, CovenantCanonicalSchemaVersionFiveFixture.ChainSet(), 1536, ct)`, seed raw rows at v5, then `InstallAsync(..., GrimoireSchemaVersionChains.Default, ...)`). Seed (Global scope, `LaneCode = 1`, one curation version rev 1 + head `IsPinned = 1` + Applied receipt per subject; key rows by raw `INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc)`):

| key | key row `KeyEpoch` | curation `KeyEpoch` | after v6 |
|---|---|---|---|
| `live.key` | 3 | 3 | bound |
| `stale.key` | 5 | 2 | kept, unbound |
| `keyless.key` | none | 0 | kept, bound |
| `orphan.key` | none | 4 | purged from heads, versions, receipts |

`BoundPinsSql = "SELECT c.NormalizedKey FROM covenant_curation_heads c WHERE c.IsPinned = 1 AND c.KeyEpoch = COALESCE((SELECT k.IncarnationEpoch FROM covenant_key_epochs k WHERE k.NormalizedKey = c.NormalizedKey), 0) ORDER BY 1;"`
- `A_version_five_catalog_evolves_to_a_healthy_version_six_identical_to_a_fresh_one`: `SchemaVersion == 6`, `Health == Healthy`; every `sqlite_master` row named `covenant_%` has the same key set and equal `GrimoireSqlNormalizer.Normalize` text as a fresh head install (covers both splice layouts).
- `The_upgrade_backfills_each_binding_epoch_from_the_dependency_epoch`: rows `("live.key",3,3)`, `("stale.key",5,5)`.
- `The_upgrade_keeps_live_curation_live_and_disarmed_curation_inert`: `BoundPinsSql` → `["keyless.key","live.key"]`; `stale.key` still has 1 row in each curation table.
- `A_keyless_pin_stays_bound_across_the_first_head_for_its_key`: insert entry/version/head for `keyless.key` (the `SeedHistorySql` shape of `CovenantReviewSchemaEvolutionTests`, Global, `NextSearchRowId = 10`) → key row `(KeyEpoch 1, IncarnationEpoch 0)`; update that head → `(2, 0)`; `BoundPinsSql` still contains `keyless.key`.
- `The_upgrade_purges_curation_left_behind_by_a_pre_fix_reset`: `orphan.key` counts 0 in all three tables; `keyless.key` counts 1 in each.

(d) **`CovenantCanonicalSchemaTests`** (fresh head via `CovenantSchemaScratchDatabase.InstallCanonicalAsync`):
- `[Theory] OwnerCleanup | CovenantFamilyMaintenance | CovenantEntryErasure` `Every_entry_closure_delete_guard_admits_cleanup_family_maintenance_and_entry_erasure`: under `CovenantSqliteConnectionInitializer.Instance.Authorize(connection, kind)` delete provenance, versions, entries, mutation receipts, curation heads, curation versions, curation receipts and `covenant_key_epochs`; every count is 0.
- `[Theory] AcceleratorSynchronization | CovenantFamilyMaintenance | CovenantEntryErasure` `The_outbox_admits_synchronization_family_maintenance_and_entry_erasure`; plus under `OwnerCleanup` the outbox delete still raises `"authorized synchronization scope"`.
- `Key_epoch_and_curation_head_deletes_require_an_authorized_scope`: unauthorized deletes raise `"covenant_key_epochs delete requires an authorized cleanup scope."` and `"covenant_curation_heads delete requires an authorized cleanup scope."`, rows unchanged.
- `Entry_erasure_does_not_open_turn_receipts`: under `CovenantEntryErasure`, `DELETE FROM covenant_turn_receipts` raises `"authorized cleanup scope"`.
- `Only_the_nine_canonical_delete_guards_name_the_entry_erasure_function`: names of `GrimoireSchemaCatalog.AllObjects` whose SQL contains `arcanum_covenant_entry_erasure_authorized` equal exactly the 7 rewritten guards + `covenant_key_epochs_guard_delete` + `covenant_curation_heads_guard_delete` (no Core object).
- Extend `Key_epoch_advances_on_head_insert_update_and_delete`: `IncarnationEpoch` reads 0 after insert, update and delete; `UPDATE covenant_key_epochs SET KeyEpoch = KeyEpoch + 1, IncarnationEpoch = 7 …` raises `"A covenant key binding epoch is fixed when its epoch row is created."` (KeyEpoch is advanced in the same statement so the overflow guard cannot fire first).

(e) `CovenantSqliteConnectionInitializerTests.Covenant_entry_erasure_is_code_twelve_with_its_own_function`: `Assert.Equal(12, (int)CovenantSqliteAuthorizationKind.CovenantEntryErasure)`; `Assert.Equal("arcanum_covenant_entry_erasure_authorized", CovenantSqliteConnectionInitializer.FunctionName(CovenantSqliteAuthorizationKind.CovenantEntryErasure))`.

(f) `CovenantMutationKernelTests.Both_receipt_outcomes_record_the_entry_they_resolved`: the `An_identical_set_returns_no_change_without_appending` pair (Applied, then NoChange), then `Assert.Equal(2, Scalar("SELECT COUNT(*) FROM covenant_mutation_receipts WHERE EntryId = (SELECT EntryId FROM covenant_entries);"))`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireSchemaSourceFingerprintTests|FullyQualifiedName~GrimoireSchemaVersionChainTests|FullyQualifiedName~GrimoireSchemaTransitionResourceTests|FullyQualifiedName~CovenantCanonicalVersionSixEvolutionTests|FullyQualifiedName~CovenantCanonicalSchemaTests|FullyQualifiedName~CovenantSqliteConnectionInitializerTests|FullyQualifiedName~CovenantMutationKernelTests"
```
Expected RED, in two passes. First pass: the test project does not compile because `CovenantSqliteAuthorizationKind.CovenantEntryErasure` is missing. Then add only the enum member and `FunctionNames[12]` (statement 1 of §15.2) and run again. Second pass: no step to version 6 and head version still 5; `IncarnationEpoch`/`EntryId` columns absent; guards refuse entry erasure. The v5 capture assertion is GREEN in the second pass.

- [ ] **Step 3: Implement**

Statement 1 of §15.2, added between the two RED passes: append `CovenantEntryErasure = 12` (XML doc: admits the canonical entry guards only; no Core guard names it) and `"arcanum_covenant_entry_erasure_authorized"` at index 12.

Heads. `covenant_key_epochs.sql` ends `UpdatedAtUtc TEXT NOT NULL` + newline + `, IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0));` with a comment that it is the binding epoch. `covenant_mutation_receipts.sql` gets `CommittedAtUtc TEXT NOT NULL, EntryId TEXT NULL,` on one line, followed by the unchanged table `CHECK` list, and `CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_entry ON covenant_mutation_receipts(EntryId);` after the existing indexes. Both spellings are the text SQLite's `ALTER TABLE … ADD COLUMN` writes into `sqlite_master`: it splices `, <column>` at the comma that opens the table-constraint list, or at the closing parenthesis when there is none; `A_version_five_catalog_evolves…` compares the two. Every canonical head comment edit of this slice happens in this task (the Five fixture freezes it).

Transitions, `Canon/Transitions/V6/`, in this order:

| Ordinals | Files |
|---|---|
| 010, 020 | `covenant_mutation_receipts_entry_id` (`ALTER TABLE … ADD COLUMN EntryId TEXT NULL;`), `covenant_mutation_receipts_entry_index` |
| 030 | `covenant_key_epochs_incarnation_epoch` (`ALTER TABLE covenant_key_epochs ADD COLUMN IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0);`) |
| 040–070 | `covenant_key_epochs_guard_overflow_drop` (`DROP TRIGGER IF EXISTS …`), `covenant_key_epochs_incarnation_backfill` (`UPDATE covenant_key_epochs SET IncarnationEpoch = KeyEpoch;`), `covenant_key_epochs_guard_overflow` (head CREATE, text unchanged), `covenant_key_epochs_guard_incarnation` |
| 080, 090 | `covenant_curation_versions_guard_delete_drop`, `covenant_curation_receipts_guard_delete_drop` |
| 100–120 | `covenant_curation_heads_leftover_delete`, `covenant_curation_versions_leftover_delete`, `covenant_curation_receipts_leftover_delete` |
| 130–160 | `covenant_entries_guard_delete`, `covenant_versions_guard_delete`, `covenant_version_attachment_provenance_guard_delete`, `covenant_mutation_receipts_guard_delete` (each one file holding `DROP TRIGGER IF EXISTS` then `CREATE TRIGGER IF NOT EXISTS`, the shape of Core `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V5/350_session_campaign_bindings_guard_update.sql`) |
| 170, 180 | `covenant_curation_versions_guard_delete`, `covenant_curation_receipts_guard_delete` (CREATE only; dropped at 080/090) |
| 190–210 | `covenant_search_outbox_guard_delete` (drop + create), `covenant_key_epochs_guard_delete`, `covenant_curation_heads_guard_delete` |
| 220–240 | `covenant_heads_key_epoch_insert`, `_update`, `_delete` (drop + create) |

The 040/060 pair exists because the head overflow guard (`BEFORE UPDATE`, `WHERE NEW.KeyEpoch <= OLD.KeyEpoch`) would abort the 050 backfill, which leaves `KeyEpoch` unchanged.

```sql
-- 100 (110/120: same predicate on covenant_curation_versions / covenant_curation_receipts)
DELETE FROM covenant_curation_heads
WHERE KeyEpoch <> 0
  AND NOT EXISTS (SELECT 1 FROM covenant_key_epochs k WHERE k.NormalizedKey = covenant_curation_heads.NormalizedKey);

-- guard WHEN shape (entries, versions, provenance, mutation receipts, curation versions/receipts,
-- key epochs, curation heads); the outbox keeps arcanum_accelerator_sync_authorized() first
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0

-- covenant_key_epochs_guard_incarnation
CREATE TRIGGER IF NOT EXISTS covenant_key_epochs_guard_incarnation
BEFORE UPDATE OF IncarnationEpoch ON covenant_key_epochs
WHEN NEW.IncarnationEpoch <> OLD.IncarnationEpoch
BEGIN
    SELECT RAISE(ABORT, 'A covenant key binding epoch is fixed when its epoch row is created.');
END;

-- covenant_heads_key_epoch_insert (update/delete: NEW/OLD as today); the conflict branch leaves IncarnationEpoch alone
INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc, IncarnationEpoch)
VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc, 0)
ON CONFLICT(NormalizedKey) DO UPDATE SET KeyEpoch = KeyEpoch + 1, UpdatedAtUtc = excluded.UpdatedAtUtc;
```
New guard messages: `'covenant_key_epochs delete requires an authorized cleanup scope.'`, `'covenant_curation_heads delete requires an authorized cleanup scope.'`; rewritten guards keep their existing `RAISE` text.

Chain: pin `[(CovenantCanonical, 6)] = "E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925"` (pins are keyed by the step's `ToVersion` and hold the from-version tree, as `[(CovenantCanonical, 5)]` does), with a provenance comment naming the Five fixture and the raw computation. Kernel: add `EntryId` to the `INSERT INTO covenant_mutation_receipts` column list, bound `receipt.EntryId.ToString("D")` (the spelling `covenant_entries` and the outbox already use). No UTC-view change: neither column is an instant.

Benchmark inventory (the procedure Tasks 10 and 11 reuse): `git add` the 27 new `.sql` files first (the manifest test reads git-tracked files); add their `R\t<path>` lines at their ordinal positions in `B/grimoire-admission-input-catalog-v1.txt` (the test requires ordinal order); change the `.sql` count in `GrimoireAdmissionBenchmarkManifestTests` from 462 (Task 3's value) to 489; run `GrimoireAdmissionBenchmarkManifestTests`, whose shape-digest assertion reports the expected value (`ShapeDigest`: SHA-256 over each entry's little-endian `int32` UTF-8 byte length, path bytes and optional byte, lowercase hex); paste that value into `B/grimoire-admission-workload-v1.json:69` and `B/AdmissionBenchmarkManifest.cs:19`, which `AdmissionBenchmarkManifest.Validate()` compares at `:158`.

- [ ] **Step 4: Run the tests to verify they pass**, first the Step 2 filter, then the cluster the step can break:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantCurationEvolutionTests|FullyQualifiedName~CovenantUngatedRetirementEvolutionTests|FullyQualifiedName~CovenantReviewSchemaEvolutionTests|FullyQualifiedName~CanonicalPersistenceEvolutionTests|FullyQualifiedName~CovenantSchemaManifestTests|FullyQualifiedName~GrimoireSchemaCatalogTests|FullyQualifiedName~GrimoireSchemaInstallerTests|FullyQualifiedName~UtcInstantColumnInventoryTests|FullyQualifiedName~CovenantHealthyCatalogErasureGuardTests|FullyQualifiedName~CovenantCanonicalErasureTransactionTests|FullyQualifiedName~BackupRestoreProtectedStatePurgeTests|FullyQualifiedName~CovenantOwnerCleanupTests|FullyQualifiedName~CovenantCurationLifecycleTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
git diff --stat -- tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
```
The capsule regeneration always runs, and the commit always lists the TSV (R31; an unchanged tracked file stages as a no-op). Expected diff: empty. `CovenantMutationKernel` has no capsule rows (`grep -c CovenantMutationKernel` on the TSV is 0), and `FunctionNames[12]` is a data entry, not a call. Review any non-empty diff: keep it only when every changed row's `source_path` is a file this task edits; otherwise stop and report the drift.

- [ ] **Step 5: Mutation check**
  - Drop `AND arcanum_covenant_entry_erasure_authorized() = 0` from the `covenant_versions_guard_delete` head and 140 → `Every_entry_closure_delete_guard_admits…(CovenantEntryErasure)` and `Only_the_nine…` turn RED.
  - Move 080/090 after 120 → `The_upgrade_purges_curation_left_behind_by_a_pre_fix_reset` RED (install aborts on the versions guard).
  - Make 050 `SET IncarnationEpoch = 0` → `…keeps_live_curation_live…` and `…backfills…` RED.
  - Remove `KeyEpoch <> 0` from 100–120 → `…keeps_live…` and `A_keyless_pin_stays_bound…` RED.
  - Stamp `1` instead of `0` in the insert trigger (head and 220) → `A_keyless_pin_stays_bound…` and the extended key-epoch test RED.
  - Bind `EntryId` only when `receipt.Outcome == Applied` → `Both_receipt_outcomes_record_the_entry_they_resolved` RED.
  - Change one byte of a frozen Five const → `Covenant_version_five_reconstruction…` RED.
  - Misspell `FunctionNames[12]` → `Covenant_entry_erasure_is_code_twelve_with_its_own_function` RED.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteAuthorizationKind.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionInitializer.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/{covenant_key_epochs,covenant_mutation_receipts,covenant_curation_heads}.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/{covenant_entries_guard_delete,covenant_versions_guard_delete,covenant_version_attachment_provenance_guard_delete,covenant_mutation_receipts_guard_delete,covenant_curation_versions_guard_delete,covenant_curation_receipts_guard_delete,covenant_search_outbox_guard_delete,covenant_heads_key_epoch_insert,covenant_heads_key_epoch_update,covenant_heads_key_epoch_delete,covenant_key_epochs_guard_incarnation,covenant_key_epochs_guard_delete,covenant_curation_heads_guard_delete}.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V6/{010_covenant_mutation_receipts_entry_id,020_covenant_mutation_receipts_entry_index,030_covenant_key_epochs_incarnation_epoch,040_covenant_key_epochs_guard_overflow_drop,050_covenant_key_epochs_incarnation_backfill,060_covenant_key_epochs_guard_overflow,070_covenant_key_epochs_guard_incarnation,080_covenant_curation_versions_guard_delete_drop,090_covenant_curation_receipts_guard_delete_drop,100_covenant_curation_heads_leftover_delete,110_covenant_curation_versions_leftover_delete,120_covenant_curation_receipts_leftover_delete,130_covenant_entries_guard_delete,140_covenant_versions_guard_delete,150_covenant_version_attachment_provenance_guard_delete,160_covenant_mutation_receipts_guard_delete,170_covenant_curation_versions_guard_delete,180_covenant_curation_receipts_guard_delete,190_covenant_search_outbox_guard_delete,200_covenant_key_epochs_guard_delete,210_covenant_curation_heads_guard_delete,220_covenant_heads_key_epoch_insert,230_covenant_heads_key_epoch_update,240_covenant_heads_key_epoch_delete}.sql tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantCanonicalSchemaVersion{Five,Four,Three}Fixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/{CovenantCanonicalVersionSixEvolutionTests,GrimoireSchemaSourceFingerprintTests,GrimoireSchemaVersionChainTests,GrimoireSchemaTransitionResourceTests,CovenantCanonicalSchemaTests}.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/{CovenantSqliteConnectionInitializerTests,CovenantMutationKernelTests}.cs tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkManifestTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/{grimoire-admission-input-catalog-v1.txt,grimoire-admission-workload-v1.json,AdmissionBenchmarkManifest.cs} tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: add Covenant canonical schema version 6" -m "Adds the entry-erasure authorization kind, the fixed curation binding epoch, receipt entry identity, and the reset-leftover purge, pinned against the raw version-5 tree." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Covenant curation fixes: binding epoch, replay, reset table list

**Files:** (`I/` = `src/RetroDownfall.Arcanum.Infrastructure/`, `T/` = `tests/RetroDownfall.Arcanum.Tests/`, `B/` = `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/`)
- Create: `I/Data/Covenant/CovenantCanonicalContentTables.cs`, `I/Data/Covenant/CovenantKeyEpochs.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCurationContracts.cs` (`CovenantCurationSubject`; `CovenantCurationIntent` validation), `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperatorCurationFactory.cs` (member rename; the false "with no token in hand" XML doc on `RequestDigest`)
- Modify: `I/Data/Covenant/CovenantStoreSql.cs` (`RetirementTarget`, `CampaignMasks`, `LaneHeadProbe`, `CurationEffectFacts`), `I/Data/Covenant/CovenantMutationKernel.cs` (`IsPinnedAsync`), `I/Data/Covenant/CovenantCurationKernel.cs` (`ApplyAsync`, `TryReplayAsync`, `ReadHeadAsync`, `InsertVersionAsync`, `UpsertHeadAsync`, `InsertReceiptAsync`, `BindSubject`; delete `ReadKeyEpochAsync`)
- Modify: `I/Covenant/CovenantMutationService.Curation.cs` (`PrepareCurationAsync` subject, `CurateAsync` order, `TryReplayCurationAsync`; delete the dead expiry check), `I/Covenant/CovenantMemoryReviewService.cs` (`ApplyMutationAsync`, `ApplyCurationAsync`, `ReadCurationRevisionAsync`; delete `ReadKeyEpochAsync`)
- Modify: `I/Data/Covenant/CovenantCanonicalErasureTransaction.cs` (delete `FamilyTablesInDeletionOrder`; `FamilyTables` and the delete loop in `EraseAsync` read the new list), `I/Data/Covenant/CovenantLocalErasureStorageHealth.cs` (`RequireFamilyEmptyAsync`), `I/Backup/BackupRestoreProtectedStateInspector.cs` (`CanonicalContentTables` becomes an `IReadOnlyList<string>` property), `I/Data/DataRetentionService.CovenantInventory.cs` (`CovenantInventoryRowTables` spreads `CovenantCanonicalContentTables.InDeletionOrder` directly instead of the inspector's field)
- Create: `T/Data/Covenant/CovenantCanonicalContentTablesTests.cs`
- Modify tests: `T/Data/Covenant/CovenantPinEnforcementTests.cs`, `CovenantMaskPlanningTests.cs`, `CovenantCurationLifecycleTests.cs`, `CovenantCurationKernelTests.cs`, `CovenantCurationServiceTests.cs`, `CovenantMutationServiceTests.cs`, `CovenantServiceHarness.cs`, `CovenantCurationFixture.cs`, `CovenantCanonicalErasureTransactionTests.cs`, `CovenantCanonicalErasureFixture.cs` (all under `T/Data/Covenant/`), `T/Memory/CovenantMemoryReviewServiceTests.cs`
- Modify (inventories): `B/grimoire-admission-input-catalog-v1.txt` (+2 `.cs` paths), `B/grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`), `B/AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`); the `.sql` count stays 489, so `GrimoireAdmissionBenchmarkManifestTests.cs` is not edited. `T/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated in Step 4; expected unchanged). Acquisition inventory: no new acquisition (delta 0); `TryReplayCurationAsync` keeps its existing `GetOpenConnectionAsync`, which is not an inventoried construct.

**Interfaces:**
- Consumes: `covenant_key_epochs.IncarnationEpoch` and the v6 guards (Task 9).
- Produces:
  - `public sealed record CovenantCurationSubject(CovenantOperationScope Scope, CovenantKey NormalizedKey, CovenantLane Lane, long KeyDependencyEpoch, long? KeyBindingEpoch = null)` — null means "resolved by the kernel under the write lock"; the receipt's `Subject` carries the resolved value.
  - `internal readonly record struct CovenantKeyEpochPair(long Dependency, long Binding)`; `internal static class CovenantKeyEpochs { internal static ValueTask<CovenantKeyEpochPair> ReadAsync(CovenantMutationTransaction transaction, string normalizedKey, CancellationToken cancellationToken); }` — one statement: `SELECT COALESCE(MAX(KeyEpoch), 0), COALESCE(MAX(IncarnationEpoch), 0) FROM covenant_key_epochs WHERE NormalizedKey = $key;`
  - `internal static class CovenantCanonicalContentTables { internal static IReadOnlyList<string> InDeletionOrder { get; } }` — one `Array.AsReadOnly` instance, exactly: `covenant_review_decision_receipts, covenant_review_markers, covenant_review_events, covenant_search_outbox, covenant_curation_receipts, covenant_curation_heads, covenant_curation_versions, covenant_heads, covenant_version_attachment_provenance, covenant_versions, covenant_entries, covenant_mutation_receipts, covenant_turn_receipts, covenant_turn_receipt_aggregate, covenant_key_epochs`. `CovenantCanonicalErasureTransaction.FamilyTables` and `BackupRestoreProtectedStateInspector.CanonicalContentTables` both become `=> CovenantCanonicalContentTables.InDeletionOrder` (same instance).

- [ ] **Step 1: Write the failing tests**

(a) **Binding epoch through production services** (`CovenantServiceHarness`; `AgentPropose`/`AgentRetire` from `CovenantMutationFixture` with the probe's live `KeyEpoch`; `Key = "preference.builds"`):
- `CovenantPinEnforcementTests`:
  - `[Theory] "correct" | "retire"` `A_Proposed_pin_survives_the_operators_own_later_write`: Campaign set, pin Proposed, then the operator verb on Confirmed (`PrepareCorrectAsync`/`CommitCorrectAsync`, or `RetireAsync(…, 1)`) → `Assert.True(probe(Proposed).IsPinned)`; agent propose → `Covenant.ForbiddenAuthority`.
  - `A_Campaign_pin_survives_a_Global_write_of_the_same_key`: pin Campaign Proposed, `SetAsync(Global, null, Key, …)` → agent propose refused with `Covenant.ForbiddenAuthority`.
  - `A_keyless_pin_survives_the_first_operator_set`: pin Campaign Proposed of `"fresh.key"` before any head, then `SetAsync(Campaign, CampaignOne, "fresh.key", …)` → probe `IsPinned`, agent propose refused with `Covenant.ForbiddenAuthority`.
  - `A_Global_pin_is_still_reported_after_a_Campaign_writes_the_same_key`: Global set, pin Global Confirmed, Campaign set → probe through `CanonicalCampaignContext.GlobalOnly` under a Global read lease reports `IsPinned`.
- `CovenantMaskPlanningTests.A_mask_still_suppresses_the_Global_key_after_the_Global_entry_is_corrected`: Global set K, Campaign masks K, Global correct K → `EligibleKeysAsync(harness, CampaignOne)` does not contain K and the snapshot's masked keys do.
- `CovenantCurationLifecycleTests.A_Global_pin_survives_a_Campaign_cleanup`: after `RunCleanupAsync`, also assert the Global probe reports `IsPinned` (today it only checks the row).
- `CovenantCurationKernelTests.A_curation_row_binds_the_binding_epoch_and_its_receipt_the_dependency_epoch`: operator set (key row `KeyEpoch 1`, `IncarnationEpoch 0`), pin with `KeyDependencyEpoch: 1` → head and version `KeyEpoch == 0`, receipt `KeyEpoch == 1`, `receipt.Subject.KeyBindingEpoch == 0`; `A_change_whose_asserted_binding_epoch_disagrees_is_refused` → `Covenant.StaleSnapshot`, and the curation tables' row counts are unchanged.
- `CovenantMemoryReviewServiceTests.A_review_pin_binds_the_key_binding_epoch`: after two operator writes to the key, bulk `Pin` → curation head `KeyEpoch` equals `SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key` (0), receipt `KeyEpoch` equals the dependency epoch.

(b) **Replay.** `CovenantServiceHarness.HarnessEnvelopeCodec` takes the `HarnessClock` and refuses `clock.GetUtcNow() >= body.ExpiresAtUtc` with `Covenant.StaleSnapshot` (the production rule), so `CovenantCurationServiceTests.A_repeat_commit_after_the_token_expired_still_replays_the_committed_receipt` stops passing vacuously. In `CovenantMutationServiceTests` (production `CovenantEnvelopeCodec` composed as in `A_prepared_set_commits_against_the_real_envelope_codec`; `SteppingTimeProvider` gains `Advance(TimeSpan)`):
  - `A_curation_replay_after_the_token_expired_returns_the_committed_receipt_under_the_real_codec`: pin, commit, `Advance(TimeSpan.FromMinutes(6))`, same request → `Replayed == true`, same `ResultingVersionId`.
  - `A_curation_replay_with_changed_fields_is_an_idempotency_conflict_after_expiry`: same `MutationId`, `ExpectedRevision: 1` → `"Security.IdempotencyConflict"`.
  - `A_curation_replay_reports_the_current_state_after_the_key_epoch_moved`: pin, operator writes the key again, replay → `Replayed`, `IsPinned`.

(c) **One table list.** `CovenantCanonicalContentTablesTests`:
  - `The_list_names_every_canonical_table_except_the_state_singleton`: sorted `InDeletionOrder` equals sorted names of `GrimoireSchemaCatalog.CovenantCanonicalObjects` with `Category == Tables && Name != "covenant_state"`; `Assert.Equal(15, …Count)`; distinct.
  - `Every_table_is_deleted_before_each_table_it_references`: on `CovenantSchemaScratchDatabase.InstallCanonicalAsync`, for every T and every `PRAGMA foreign_key_list("T")` target R ≠ T in the list, `Assert.True(IndexOf(T) < IndexOf(R), $"{T} references {R}")`.
  - `Key_epochs_are_deleted_last_because_deleting_heads_rewrites_them`: `Assert.Equal("covenant_key_epochs", InDeletionOrder[^1])`.
  - `Every_consumer_reads_the_one_list`: `Assert.Same(InDeletionOrder, CovenantCanonicalErasureTransaction.FamilyTables)`; `Assert.Same(InDeletionOrder, BackupRestoreProtectedStateInspector.CanonicalContentTables)`.
  - `CovenantCanonicalErasureTransactionTests.A_reset_deletes_every_canonical_family_table`: `CovenantCanonicalErasureFixture.SeedAsync` also seeds one curation version, head and receipt (new `SeedCurationAsync`); add the three curation tables to the local `FamilyTables` (1 row each before) and after the reset assert 0 for every table in `CovenantCanonicalContentTables.InDeletionOrder`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantPinEnforcementTests|FullyQualifiedName~CovenantMaskPlanningTests|FullyQualifiedName~CovenantCurationLifecycleTests|FullyQualifiedName~CovenantCurationKernelTests|FullyQualifiedName~CovenantCurationServiceTests|FullyQualifiedName~CovenantMutationServiceTests|FullyQualifiedName~CovenantMemoryReviewServiceTests|FullyQualifiedName~CovenantCanonicalContentTablesTests|FullyQualifiedName~CovenantCanonicalErasureTransactionTests"
```
Expected RED: agent writes succeed and masks lapse after any head write (dependency-epoch joins); curation rows carry the dependency epoch; replay after expiry answers `Covenant.StaleSnapshot` (token decoded before the receipt); the content-table type is missing; reset leaves one curation row per table.

- [ ] **Step 3: Implement**
  - Every pin and mask read joins `c.KeyEpoch = COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = …), 0)`: `RetirementTarget` and `CurationEffectFacts` extend their epoch CTE to `(Dependency, Binding)`, still *return* the dependency epoch, and join on `Binding`; `LaneHeadProbe` the same (its column 0 stays the dependency epoch); `CampaignMasks`; `IsPinnedAsync` drops its `keyEpoch` parameter. The expression is written inline at each site; Task 27 factors it into one constant (R31).
  - `CovenantCurationKernel.ApplyAsync`: after the generation and reclamation checks, `CovenantKeyEpochPair epochs = await CovenantKeyEpochs.ReadAsync(…)`; `epochs.Dependency != Subject.KeyDependencyEpoch` → `StaleSnapshot` "This Covenant key changed after the curation change was prepared."; `Subject.KeyBindingEpoch is { } asserted && asserted != epochs.Binding` → `StaleSnapshot`; continue with `subject = Subject with { KeyBindingEpoch = epochs.Binding }`. Heads and versions bind `$epoch = KeyBindingEpoch`; receipts bind `KeyDependencyEpoch`. `TryReplayAsync` joins heads on the current binding epoch, not the receipt's.
  - `CovenantMutationService.CurateAsync`: right after request validation and lease revalidation (before `ResolveAuthorityEpoch` and `codec.Decode`) call `TryReplayCurationAsync(CovenantCurationRequest request, CovenantOperationScope scope, string normalizedKey, CancellationToken)`: read the receipt by `MutationId` (with `r.KeyEpoch`), recompute `CovenantOperatorCurationFactory.RequestDigest(request.MutationId, request.Kind, new CovenantCurationSubject(scope, key, request.Lane, storedKeyEpoch), request.ExpectedRevision)`, equal → replay (state joined on the current binding epoch), different → `"Security.IdempotencyConflict"`. Delete the `timeProvider.GetUtcNow() > envelope.Value.ExpiresAtUtc` block.
  - `CovenantMemoryReviewService`: both apply paths use `CovenantKeyEpochs.ReadAsync`; `ApplyCurationAsync` builds the subject with both values; `ReadCurationRevisionAsync` binds the binding epoch.
  - Move the 15-name list verbatim into `CovenantCanonicalContentTables`. `CovenantCanonicalErasureTransaction.FamilyTables => CovenantCanonicalContentTables.InDeletionOrder` and the `EraseAsync` delete loop iterates it (curation now leaves with family reset and factory erasure; the v6 guards admit `CovenantFamilyMaintenance`). `RequireFamilyEmptyAsync` iterates it. `BackupRestoreProtectedStateInspector.CanonicalContentTables => CovenantCanonicalContentTables.InDeletionOrder`, and `DataRetentionService.CovenantInventoryRowTables` spreads `InDeletionOrder` directly. `SumAsync` and `BackupRestoreProtectedStatePurger.DeleteAllAsync` keep their `IEnumerable<string>` parameters (verified), so their member identities and capsule rows do not change. Edit no canonical `.sql` head file in this task.
  - Benchmark inventory: `git add` the two new `.cs` files, add their catalog lines, and update the digest in both files by the Task 9 Step 3 procedure.

- [ ] **Step 4: Run the tests to verify they pass**, then the wider cluster:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Covenant|FullyQualifiedName~BackupRestoreProtectedState|FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~DataRetention|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
git diff --stat -- tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
```
The capsule regeneration always runs, and the commit always lists the TSV (R31). Expected diff: empty. The kernels, the two services, `CovenantStoreSql` and `RequireFamilyEmptyAsync` have no capsule rows; the capsule members of `CovenantCanonicalErasureTransaction`, the inspector and the purger keep their call expressions and signatures, because only a loop source and a field-to-property swap change. Review any non-empty diff by the Task 9 Step 4 rule.

- [ ] **Step 5: Mutation check**
  - `CampaignMasks` back to the dependency epoch → `A_mask_still_suppresses_…_corrected` RED.
  - `IsPinnedAsync` back to the dependency epoch → the three `…pin survives…` tests RED.
  - `LaneHeadProbe` join back to `epochs.KeyEpoch` → the probe assertions in the pin tests RED.
  - Curation kernel binds heads with `KeyDependencyEpoch` → `A_curation_row_binds_the_binding_epoch…` RED.
  - `ReadCurationRevisionAsync` binds the dependency epoch → `A_review_pin_binds_the_key_binding_epoch` RED.
  - Restore decode-before-replay → `…under_the_real_codec` RED.
  - Remove `covenant_curation_heads` from `InDeletionOrder` → `The_list_names_every…` and `A_reset_deletes_every_canonical_family_table` RED.
  - Swap `covenant_key_epochs` and `covenant_heads` → `Every_table_is_deleted_before…` / `Key_epochs_are_deleted_last…` RED.
  - Give `BackupRestoreProtectedStateInspector.CanonicalContentTables` its own copy of the list → `Every_consumer_reads_the_one_list` RED.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Covenant/CovenantCurationContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperatorCurationFactory.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/{CovenantCanonicalContentTables,CovenantKeyEpochs,CovenantStoreSql,CovenantMutationKernel,CovenantCurationKernel,CovenantCanonicalErasureTransaction,CovenantLocalErasureStorageHealth}.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.Curation.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStateInspector.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStatePurger.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/{CovenantCanonicalContentTablesTests,CovenantPinEnforcementTests,CovenantMaskPlanningTests,CovenantCurationLifecycleTests,CovenantCurationKernelTests,CovenantCurationServiceTests,CovenantMutationServiceTests,CovenantServiceHarness,CovenantCurationFixture,CovenantCanonicalErasureTransactionTests,CovenantCanonicalErasureFixture}.cs tests/RetroDownfall.Arcanum.Tests/Memory/CovenantMemoryReviewServiceTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/{grimoire-admission-input-catalog-v1.txt,grimoire-admission-workload-v1.json,AdmissionBenchmarkManifest.cs} tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "fix: bind Covenant curation to the key's binding epoch" -m "Pins and masks survive ordinary writes, curation replay resolves by receipt before any token is decoded, and one canonical table list drives reset, its proof, the restore inspector and the reset inventory." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Covenant agent chokepoints and staging refusals

**Files:** (`I/` = `src/RetroDownfall.Arcanum.Infrastructure/`, `T/` = `tests/RetroDownfall.Arcanum.Tests/`, `B/` = `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/`)
- Create: `I/Data/Covenant/CovenantAgentErasureGate.cs`
- Modify: `I/Data/Covenant/CovenantMutationKernel.cs` (primary ctor gains the provider; delete the parameterless `internal CovenantMutationKernel()`; `CaptureErasureGate`; `ApplyBatchAsync`; `ApplyIntentAsync` after the pin check; pin message)
- Modify: `I/Data/Covenant/CovenantStore.cs` (primary ctor; `ProbeLaneHeadAsync`; `ReadRetirementTargetAsync`)
- Modify: `I/Data/MemoryErasureEvidence.cs` (`IsInstalledAsync`: table presence before the Core version; Task 6's file)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLaneHeadProbe.cs` (new `CovenantAgentErasureState`; `AgentErasure`, `IsAgentWithheld`), `src/RetroDownfall.Arcanum.Core/Covenant/CovenantManagementReadContracts.cs` (`CovenantRetirementTarget`)
- Modify: `I/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs` (`ResolveProposedLaneAsync`), `I/Covenant/CovenantTurnHeadProbe.cs` (`ResolveRetirementPreflightAsync`)
- Modify: `I/Repositories/GrimoireRepository.TurnCommit.cs` (`CommitWithinImmediateTransactionAsync` before its `BEGIN` at :86; `PublishCovenantBatchAsync`), `I/Covenant/CovenantMutationService.cs` (`CommitAsync`), `I/Covenant/CovenantMemoryReviewService.cs` (`ApplyAsync` before its `BEGIN` at :502, threaded through `ApplyMutationAsync` to `ApplyBatchAsync`)
- Modify: `I/DependencyInjection/ServiceCollectionExtensions.cs` (`AddCovenantPersistence`: the `CovenantStore` factory at :1900 and the `CovenantMutationKernel` factory at :2063)
- Create tests: `T/Data/Covenant/CovenantErasureSuppressionTests.cs`, `T/Data/Schema/CovenantMidUpgradeErasureTests.cs` (R25), `T/Data/Covenant/CovenantErasureRestartTests.cs` (R11)
- Modify tests (fixtures and new cases): `T/Data/Covenant/{CovenantCanonicalFixture,CovenantServiceHarness,CovenantPinEnforcementTests,CovenantRetirementPreflightTests}.cs`, `T/Fixtures/CovenantSchemaScratchDatabase.cs` (`RecordCoreSchemaVersionAsync`), `T/Data/MemoryErasureEvidenceTests.cs` (two ordering cases), `T/Data/Schema/SagaMemoryMidUpgradeWriteTests.cs` (make `CreateStore` and `WriteAsync` `internal`), `T/Mcp/CovenantMutationToolTests.cs`, `T/Covenant/CovenantCapabilityFixtures.cs` (`StubHeadProbe`), `T/Intelligence/CovenantProposalPublicationTests.cs`, `T/Build/NullableInterfaceConstructorDefaultTests.cs` (R24)
- Modify tests (every kernel construction site, now that the parameterless ctor is gone; every direct `ApplyBatchAsync` caller; every store construction site): `T/Data/Covenant/CovenantMutationFixture.cs` (:257), `CovenantMutationConcurrencyTests.cs` (:154), `CovenantPinEnforcementTests.cs` (:202), `CovenantMutationServiceTests.cs` (:342, :393, :525), `CovenantServiceHarness.cs` (:101), `CovenantAcrossSessionsTests.cs` (:659), `CovenantErasureSameProcessTests.cs` (`ApplyBatchAsync` at :4207), `CovenantCanonicalFixture.cs` (store at :67), `T/Data/Schema/CovenantUngatedRetirementEvolutionTests.cs` (:503), `T/Intelligence/CovenantProposalPublicationTests.cs` (kernel :340, store :278), `T/Intelligence/CovenantBootstrapProposalTests.cs` (kernel :364, store :301), `T/Intelligence/CovenantOperatorJourneyTests.cs` (:1149, :1212), `T/Memory/CovenantMemoryReviewServiceTests.cs` (:1218), `tests/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantWorkloadBed.cs` (kernel :231, store :183)
- Modify (inventories): `B/grimoire-admission-input-catalog-v1.txt` (+1 `.cs` path), `B/grimoire-admission-workload-v1.json`, `B/AdmissionBenchmarkManifest.cs`; `T/Support/hosted-grimoire-producer-capsules-v1.tsv` (always regenerated, R31). Acquisition inventory: no new acquisition (delta 0); `CommitWithinImmediateTransactionAsync` keeps its two parameters, so its row `CommitWithinImmediateTransactionAsync(2)` (`T/Support/GrimoireConnectionAcquisitionInventory.cs:1450`) is unchanged.

**Interfaces:**
- Consumes: `IMemoryErasureKeyProvider` (`Latch`, `TryCopyLatched()`), `MemoryErasureKeyState`, `MemoryErasureKeyProbe`, `MemoryErasureKeyring` and `IMemoryErasureKeyCreator.OpenOrCreate(bool)`, `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount` (Task 2); `MemoryErasureKey.KeyId`, `.Fingerprint(MemoryErasureIdentity)`, `MemoryErasureIdentity.ForCovenant(CovenantScope, Guid?, string)`, `ErrorCodes.MemoryErasure.KeyUnavailable/KeyLost` (Task 1); `CoreSchemaVersionElevenFixture.ChainSet()` with the Core v13 step (Task 3); `MemoryErasureEvidence.IsInstalledAsync/AnyAsync/AnyForeignAsync/ContainsAsync`, `MemoryErasureGuard.KeyLostError/KeyUnavailableError`, `MemoryErasureTestKeys.Isolated/SeedFingerprintAsync`, `CountingOsCredentialStore`, and the pre-readiness `MemoryErasureKeyWarmup.RunAsync` in `GrimoireDatabaseBootstrapper` (Task 6, R11); `NullableInterfaceConstructorDefaultTests.ErasureChokepointOwners` (Tasks 7 and 8); binding-epoch reads (Task 10).
- Produces:
  - Core, in `CovenantLaneHeadProbe.cs`: `public enum CovenantAgentErasureState : byte { Clear = 1, Withheld = 2, KeyUnavailable = 3, KeyLost = 4 }` (in-process only, never on the wire). `CovenantLaneHeadProbe(…, bool IsPinned = false, CovenantAgentErasureState AgentErasure = CovenantAgentErasureState.Clear)` and `CovenantRetirementTarget(…, bool IsPinned, CovenantAgentErasureState AgentErasure = CovenantAgentErasureState.Clear)`, each with `public bool IsAgentWithheld => AgentErasure == CovenantAgentErasureState.Withheld;` (the §5.4 name).
  - `internal sealed record CovenantAgentErasureGate(MemoryErasureKey? Key, MemoryErasureKeyState LatchState) : IDisposable`:
    - `static CovenantAgentErasureGate FromLatch(IMemoryErasureKeyProvider provider)`: `TryCopyLatched()` first; `LatchState` is `Present` when a copy was taken, else `provider.Latch.State`, with a racing `Present` read as `Unresolved`. Never I/O.
    - `static readonly CovenantAgentErasureGate None = new(null, MemoryErasureKeyState.Unresolved)`.
    - `internal const string OperatorManagedRefusal = "This Covenant key is managed by the operator in this scope."`
    - `internal Task<CovenantAgentErasureState> ClassifyAsync(SqliteConnection connection, SqliteTransaction transaction, CovenantScope scope, Guid? campaignId, string normalizedKey, CancellationToken cancellationToken)` — the one R16 mapping, used by the kernel and by the store.
    - `internal static Error? RefusalFor(CovenantAgentErasureState state)`.
    - `Dispose` disposes `Key` and is idempotent; `None` owns no key.
  - `CovenantMutationKernel(CovenantQuotaGuard quotas, IMemoryErasureKeyProvider erasureKeys)` as the only constructor; `internal CovenantAgentErasureGate CaptureErasureGate()` (R12: the one capture method for agent and operator paths); `ApplyBatchAsync(CovenantMutationBatch batch, CovenantMutationTransaction transaction, CovenantAgentErasureGate erasureGate, CancellationToken cancellationToken)`. Every batch receives the gate whatever its origin; Task 17 reads `erasureGate.Key` inside the same call for operator re-creation and adds no capture in the commit path, no provider and no public port method. Task 17 does add one `CaptureErasureGate()` call in `CovenantMutationService.PrepareAsync` for the preflight flag, which R12 allows (N6).
  - `CovenantStore(ICovenantConnectionSource connections, IMemoryErasureKeyProvider erasureKeys)`.
  - `MemoryErasureEvidence.IsInstalledAsync` answers `false` for an absent `memory_erasure_fingerprints` before it reads `grimoire_feature_schemas`; a present table with missing or malformed Core metadata still throws (fail closed).
  - Test support:
    - `CovenantSchemaScratchDatabase.RecordCoreSchemaVersionAsync(int version, CancellationToken)` inserts the Core row `(0, 0, version, 64 × '0', "sha256:" + 64 × '0', "2026-08-20T00:00:00.0000000Z", 0, NULL)`, which satisfies the table's length `CHECK`s; only `GrimoireCoreSchemaVersion.ReadAsync` reads it on these catalogs.
    - `CovenantCanonicalFixture.CreateAsync(…, bool withErasureEvidence = false)`: when true, it installs the Core objects `grimoire_feature_schemas` and `memory_erasure_fingerprints` and records Core `GrimoireSchemaVersionChains.CoreSchemaVersion`. It exposes `internal CountingOsCredentialStore Credentials` and `internal MemoryErasureKeyring ErasureKeys` (`MemoryErasureTestKeys.Isolated(Credentials)`); the store is built over `ErasureKeys`.
    - `CovenantServiceHarness.StartAsync(…, withErasureEvidence)`, whose kernel is built over `Fixture.ErasureKeys`.
    - `SeedCovenantFingerprintAsync(CovenantScope scope, Guid? campaignId, string key)`: `Fixture.ErasureKeys.OpenOrCreate(evidenceRowsExist: false)` creates the key on first use, returns it afterwards, and publishes `Present` as erase prepare does; then `MemoryErasureTestKeys.SeedFingerprintAsync`. Task 16's erase replaces it as the production writer.
    - `KeyringInState(MemoryErasureKeyState state) → MemoryErasureKeyring`: a fresh keyring over `Fixture.Credentials`. `Unresolved` is left unprobed. `Absent` deletes the credential, then calls `OpenExisting(Reprobe)`. `Unavailable` sets `FailWith = Unavailable`, reprobes, then clears `FailWith`. `Malformed` calls `Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount, "not-base64url")`, then reprobes.

- [ ] **Step 1: Write the failing tests**

`CovenantErasureSuppressionTests` (`CovenantServiceHarness.StartAsync(Token, withErasureEvidence: true)`; kernel `new CovenantMutationKernel(new CovenantQuotaGuard(), harness.Fixture.ErasureKeys)`; `Key = "preference.builds"`). Every refusal case also asserts nothing was written: `covenant_mutation_receipts` and `covenant_versions` counts are unchanged.
```csharp
[Fact] public async Task An_agent_proposal_of_a_fingerprinted_Campaign_key_is_refused_as_operator_managed()
// SetAsync(Campaign), SeedCovenantFingerprintAsync(Campaign, CampaignOne, Key), AgentPropose with the probe's epoch, gate = kernel.CaptureErasureGate()
//   Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);
//   Assert.Equal("This Covenant key is managed by the operator in this scope.", refused.Error.Message);
[Fact] public async Task A_pin_refusal_and_a_fingerprint_refusal_are_indistinguishable() // equal (Code, Message) from both kernel refusals
[Fact] public async Task An_approved_agent_retirement_of_a_fingerprinted_key_is_refused() // operator re-created the key after the fingerprint; AgentRetire → ForbiddenAuthority + OperatorManagedRefusal
[Fact] public async Task The_refusal_survives_the_key_epoch_moving()                        // SetAsync(Global, same key) first; still ForbiddenAuthority + OperatorManagedRefusal
[Fact] public async Task A_Global_fingerprint_does_not_block_a_Campaign_proposal_of_the_same_key() // one receipt, Outcome Applied
[Theory] // R16: the kernel maps the latch state; gate = CovenantAgentErasureGate.FromLatch(harness.KeyringInState(state)), or None
[InlineData("none", ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData(nameof(MemoryErasureKeyState.Unresolved), ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData(nameof(MemoryErasureKeyState.Absent), ErrorCodes.MemoryErasure.KeyLost)]
[InlineData(nameof(MemoryErasureKeyState.Unavailable), ErrorCodes.MemoryErasure.KeyUnavailable)]
[InlineData(nameof(MemoryErasureKeyState.Malformed), ErrorCodes.MemoryErasure.KeyUnavailable)]
public async Task Agent_writes_without_a_key_map_the_latch_state_to_its_refusal(string state, string code) // an unrelated key, Covenant evidence present
[Fact] public async Task Agent_writes_fail_closed_when_a_Covenant_fingerprint_carries_another_key_id() // raw row KeyId = randomblob(16) beside a valid one, Present key → ErrorCodes.MemoryErasure.KeyLost
[Fact] public async Task Without_Covenant_evidence_agent_writes_need_no_key()                      // None, no rows → Applied; Fixture.Credentials.Calls == 0
[Theory] [InlineData(true)] [InlineData(false)] // gate from a Present latch, or None
public async Task An_operator_set_of_a_fingerprinted_key_still_commits(bool keyInHand)            // SetAsync succeeds; the fingerprint row count stays 1 (release is Task 17)
[Theory] // the store's probe uses the same mapping and never touches the credential store
public async Task The_lane_head_probe_maps_the_latch_state_like_the_kernel(string state, CovenantAgentErasureState expected)
// rows: Present + fingerprinted key → Withheld (IsAgentWithheld true); Present + other key → Clear; Unresolved → KeyUnavailable;
//       Absent → KeyLost; Malformed → KeyUnavailable; Present + a foreign-KeyId row → KeyLost.
// Probe through new CovenantStore(new FixedCovenantConnectionSource(connection), keyring); Credentials.Calls is equal before and after ProbeLaneHeadAsync.
[Fact] public async Task A_catalog_without_Core_metadata_or_evidence_answers_clear()
// CovenantCanonicalFixture.CreateAsync(Token) (no withErasureEvidence: no grimoire_feature_schemas, no fingerprint table):
//   an agent proposal is Applied, the probe reports Clear, Credentials.Calls == 0.
```
`MemoryErasureEvidenceTests` (Task 6's file):
- `An_absent_table_answers_no_evidence_without_reading_core_metadata`: a scratch SQLite database with neither `grimoire_feature_schemas` nor `memory_erasure_fingerprints` → `IsInstalledAsync` and `AnyAsync(Covenant)` return `false` and do not throw.
- `A_present_table_without_core_metadata_fails_closed`: install only the `memory_erasure_fingerprints` object → `IsInstalledAsync` throws (`SqliteException` for the missing metadata table).

`NullableInterfaceConstructorDefaultTests.ErasureChokepointOwners` (R24): add `typeof(CovenantMutationKernel)` and `typeof(CovenantStore)`. The pin requires every constructor, public and non-public, to take a non-defaulted `IMemoryErasureKeyProvider`, which is why the parameterless kernel constructor is deleted rather than kept as a fail-closed test seam.

`CovenantPinEnforcementTests`: pin refusals assert the shared message too. `CovenantRetirementPreflightTests`: `Resolving_a_pinned_head_is_refused_before_a_Ward_can_be_raised` asserts the shared message; add `Resolving_a_fingerprinted_head_is_refused_as_operator_managed` (`Covenant.ForbiddenAuthority` + shared message), and `[Theory] (Unresolved, MemoryErasure.KeyUnavailable) (Absent, MemoryErasure.KeyLost)` `Resolving_while_Covenant_evidence_is_unverifiable_is_refused`. `CovenantMutationToolTests` (`StubHeadProbe.SetPresent(string key, long revision, bool pinned = false, CovenantAgentErasureState agentErasure = CovenantAgentErasureState.Clear)`):
  - `A_proposal_against_a_pinned_lane_is_refused_before_staging` → `Covenant.ForbiddenAuthority`, shared message, `Assert.Equal(0, session.Collector.StagedCount)` (today the handler ignores `IsPinned`).
  - `A_proposal_of_a_withheld_key_is_refused_exactly_like_a_pinned_one` → identical `Failure(result)` code and message; nothing staged.
  - `[Theory] (KeyUnavailable, MemoryErasure.KeyUnavailable) (KeyLost, MemoryErasure.KeyLost)` `A_proposal_while_Covenant_evidence_is_unverifiable_is_refused_before_staging`; nothing staged.

`CovenantProposalPublicationTests` (real Grimoire at head, keyring shared by `Repository()`'s kernel and `Gate()`'s store; key created through `OpenOrCreate(false)`):
  - `A_turn_publishes_an_unrelated_proposal_while_another_key_is_fingerprinted` → turn succeeds, `LaterProposedSectionAsync()` contains `ProposedContent`.
  - `A_key_fingerprinted_after_staging_refuses_the_whole_turn_at_publication` (`StagingChatClient` gains an `afterStaging` callback that inserts the fingerprint) → `turn.IsFailure`, `ReadLastAssistantContentAsync` is null, the proposal is absent.

`CovenantMidUpgradeErasureTests.An_agent_proposal_while_the_version_twelve_sweep_is_pending_needs_no_erasure_key` (R25; §5.4 and §19.2 #8 for the Covenant chokepoint, mirroring Tasks 7 and 8):
  1. `EvolutionScratchDatabase.Create()`, then install `CoreSchemaVersionElevenFixture.ChainSet()`. That installs Core v11 and the canonical tier at its Default head, v6.
  2. Write one Saga memory through `SagaMemoryMidUpgradeWriteTests.CreateStore(SagaMemoryMidUpgradeWriteTests.CreateContext(file))` and `WriteAsync`, so the v12 review-event sweep has a head.
  3. Call `GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, Token)` once.
  4. Assert the state before writing:
     - `upgraded.Core.SchemaVersion == 11`;
     - `memory_erasure_fingerprints` is absent from `sqlite_master`;
     - `SELECT COUNT(*) FROM pragma_table_info('covenant_mutation_receipts') WHERE name = 'EntryId'` is 1.
  5. Write through the kernel. Build it as `new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated(credentials))` over `CountingOsCredentialStore credentials`. Build an `AgentPropose(CampaignId, Key, …, expectedRevision: 0, expectedKeyEpoch: 0)` batch the way `CovenantUngatedRetirementEvolutionTests.PublishReceiptFreeRetirementThroughKernelAsync` does (dataset generation and reclamation epoch read from `covenant_state`), with `gate = kernel.CaptureErasureGate()`.
  6. Assert:
     - one receipt, `Applied`;
     - `gate.ClassifyAsync(…)` in the same transaction returns `Clear`;
     - `gate.Key` is null;
     - `credentials.Calls == 0`.

`CovenantErasureRestartTests.A_restarted_host_with_only_Covenant_fingerprints_admits_an_unrelated_agent_proposal_without_an_operator_call` (R11; `[Collection("ApiHost")]`, `[SkippableFact]` on `GrimoireFixture.SqlCipherAvailable`):
  1. **Profile.** `await using RestartableArcanumProfileFixture profile = new();`. Every factory comes from `profile.CreateFactory()` with `SettingsOverride = s => s with { Features = s.Features with { Covenant = true } }`, so both hosts share `profile.CredentialStore`.
  2. **Host A.**
     - Create a Campaign through `POST /api/campaigns`, the way `PromptCloneEndpointTests.CreateCampaignAsync` does.
     - Call `IMemoryErasureKeyCreator.OpenOrCreate(evidenceRowsExist: false)` and require `Present`.
     - On a connection from `ICovenantConnectionSource.GetOpenConnectionAsync`, call `MemoryErasureTestKeys.SeedFingerprintAsync(connection, key, MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, campaignId, "erased.key"), ct)`. The Covenant erase route arrives only in Task 16.
     - Dispose host A.
  3. **Host B**, after `CreateClient()` returns (readiness):
     - `Assert.Equal(MemoryErasureKeyState.Present, Services.GetRequiredService<IMemoryErasureKeyProvider>().Latch.State)`. The test calls no erasure, status or release route, so only the warm-up can have resolved the latch.
     - `MemoryErasureEvidence.AnyAsync` is false for Saga and Lexicon, and true for Covenant.
     - Under `ICovenantOperationGate.AcquireWriteAsync(CovenantOperationScope.ForCampaign(campaignId))`, apply a batch to the host's `CovenantMutationKernel` in a serializable transaction, as `CovenantErasureSameProcessTests.AssertFreshCrudAsync` does, with `kernel.CaptureErasureGate()`:
       - `AgentPropose(campaignId, "unrelated.key", …)` → `Applied`;
       - a second batch `AgentPropose(campaignId, "erased.key", …)` → `Covenant.ForbiddenAuthority` with `OperatorManagedRefusal`.
     - On the same transaction, `CovenantAgentErasureGate.FromLatch(provider).ClassifyAsync(…)` returns `Clear` for `unrelated.key` and `Withheld` for `erased.key`. This is the staging decision the store makes.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureSuppressionTests|FullyQualifiedName~CovenantPinEnforcementTests|FullyQualifiedName~CovenantRetirementPreflightTests|FullyQualifiedName~CovenantMutationToolTests|FullyQualifiedName~CovenantProposalPublicationTests|FullyQualifiedName~CovenantMidUpgradeErasureTests|FullyQualifiedName~CovenantErasureRestartTests|FullyQualifiedName~MemoryErasureEvidenceTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests"
```
Expected RED. First pass: the build fails because `CovenantAgentErasureGate`, `CaptureErasureGate`, `CovenantAgentErasureState`, `AgentErasure`, the two-argument kernel and store constructors, `withErasureEvidence`, `KeyringInState` and `RecordCoreSchemaVersionAsync` do not exist.

Second pass, with compile-only stubs (a gate whose `ClassifyAsync` answers `Clear`, and handlers that ignore `AgentErasure`):
- fingerprinted proposals and retirements are `Applied`, and so is every latch-state theory row;
- the pinned proposal is staged;
- the pin texts differ from the shared text;
- the probe theory reports `Clear` everywhere;
- `An_absent_table_answers_no_evidence_without_reading_core_metadata` throws, because Task 6's `IsInstalledAsync` reads the Core version first, as specified there;
- the two `ErasureChokepointOwners` rows fail, because `internal CovenantMutationKernel()` remains and `CovenantStore` has no provider parameter;
- the restart test's `erased.key` proposal is `Applied`.

`An_agent_proposal_while_the_version_twelve_sweep_is_pending…` and `A_catalog_without_Core_metadata_or_evidence_answers_clear` pass on arrival. They are characterization pins, and Step 5 proves the lines that carry them.

- [ ] **Step 3: Implement**
  - **Evidence ordering.** `MemoryErasureEvidence.IsInstalledAsync` first asks `sqlite_master` for `memory_erasure_fingerprints` (absent → `false`, §5.4 "or the table is absent"), then requires `GrimoireCoreSchemaVersion.ReadAsync(…) >= 13`, which still throws on missing or malformed metadata. Covenant-only scratch catalogs (every default `CovenantCanonicalFixture`, 131 call sites) therefore never read Core metadata, and Task 6's two `A_catalog_below_version_thirteen…` rows answer the same as before.
  - **Gate.** `ClassifyAsync` runs, in order:
    1. `MemoryErasureEvidence.AnyAsync(connection, transaction, MemoryReviewStore.Covenant, ct)`; false → `Clear`.
    2. `Key is null` → `LatchState == Absent ? KeyLost : KeyUnavailable` (R16: `Absent` with rows is key loss; `Unresolved`, `Unavailable` and `Malformed` are unavailability).
    3. Copy `Key.KeyId` to an array before any await. `AnyForeignAsync(…, Covenant, keyId)` → `KeyLost`.
    4. `ContainsAsync(Key.Fingerprint(MemoryErasureIdentity.ForCovenant(scope, campaignId, normalizedKey)))` → `Withheld`, else `Clear`.

    `RefusalFor` maps each state to one error:
    - `Withheld` → `new Error(ErrorCodes.Covenant.ForbiddenAuthority, OperatorManagedRefusal)`;
    - `KeyUnavailable` → `MemoryErasureGuard.KeyUnavailableError`;
    - `KeyLost` → `MemoryErasureGuard.KeyLostError`;
    - `Clear` → null.

    Task 6's content-free errors mean Saga, Lexicon and Covenant answer key trouble identically, at staging and at every chokepoint (R16). The gate logs nothing.
  - **Kernel.** The primary constructor becomes `(CovenantQuotaGuard quotas, IMemoryErasureKeyProvider erasureKeys)`, and the parameterless constructor is deleted. `CaptureErasureGate() => CovenantAgentErasureGate.FromLatch(erasureKeys)`. In `ApplyIntentAsync`, directly after the pin check and with the same origin predicate (`AgentProposed or AgentApproved`), return `RefusalFor(await erasureGate.ClassifyAsync(tx.Connection, tx.Transaction, scope.Kind, scope.CampaignId, intent.Target.NormalizedKey.Value, ct))` when it is non-null. The pin refusal also uses `OperatorManagedRefusal`. The check is lane- and epoch-free by design, and operator origins never consult the gate.
  - **Callers** capture once, before `BEGIN`, and dispose after the transaction ends:
    - `GrimoireRepository.CommitWithinImmediateTransactionAsync`, directly before `connection.BeginTransaction(deferred: false)`: `using CovenantAgentErasureGate erasureGate = request.Mutations.IsEmpty || _covenantKernel is null ? CovenantAgentErasureGate.None : _covenantKernel.CaptureErasureGate();`. It is passed into `PublishCovenantBatchAsync`, whose new parameter is not inventoried, and the method keeps its two parameters.
    - `CovenantMutationService.CommitAsync`, before `connections.GetOpenConnectionAsync`: `using CovenantAgentErasureGate erasureGate = kernel.CaptureErasureGate();`. This is the operator path that Task 17's re-creation release reads (R12).
    - `CovenantMemoryReviewService.ApplyAsync`, before `connection.BeginTransaction(deferred: false)`, threaded through both `ApplyMutationAsync` calls to `ApplyBatchAsync`.

    Tests that call `ApplyBatchAsync` directly pass `CovenantAgentErasureGate.None`, unless the case names a gate. `CaptureErasureGate()` is a latch copy with no I/O, so capturing it while the route's write lease or the turn lease is held never performs keychain I/O under a lease or a transaction (§5.1).
  - **Store.** After the existing read in `ProbeLaneHeadAsync` and `ReadRetirementTargetAsync`, inside the same read transaction: `using CovenantAgentErasureGate gate = CovenantAgentErasureGate.FromLatch(erasureKeys);` and `AgentErasure = await gate.ClassifyAsync(…)`. It never does keychain I/O, because the turn lease is held.
  - **Handlers.** `ResolveProposedLaneAsync` refuses before its presence switch: `probe.IsPinned` → `ForbiddenAuthority` with `OperatorManagedRefusal`; otherwise `RefusalFor(probe.AgentErasure)`. In `ResolveRetirementPreflightAsync`, the existing `IsPinned` refusal (:61) takes `OperatorManagedRefusal`, followed by `RefusalFor(target.AgentErasure)`.
  - **DI** (`AddCovenantPersistence`):
    - `new CovenantMutationKernel(sp.GetRequiredService<CovenantQuotaGuard>(), sp.GetRequiredService<IMemoryErasureKeyProvider>())`;
    - `new CovenantStore(sp.GetRequiredService<ICovenantConnectionSource>(), sp.GetRequiredService<IMemoryErasureKeyProvider>())`.

    Operator writes are never refused.
  - **Construction sites.** Test sites pass `MemoryErasureTestKeys.Isolated()`, or the fixture's `ErasureKeys` where a case needs evidence. `CovenantWorkloadBed` passes `new MemoryErasureKeyring(new InMemoryOsCredentialStore())`; Covenant.Benchmarks has `InternalsVisibleTo`.
  - **Benchmark inventory:** `git add` the new `.cs`, then follow the Task 9 Step 3 procedure.

- [ ] **Step 4: Run the tests to verify they pass**, then the wider cluster:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Covenant|FullyQualifiedName~MemoryErasureEvidenceTests|FullyQualifiedName~MemoryErasureGuardTests|FullyQualifiedName~SagaMemoryMidUpgradeWriteTests|FullyQualifiedName~LexiconMidUpgradeCompatibilityTests|FullyQualifiedName~GrimoireTurnCommitterTests|FullyQualifiedName~GrimoireRepositoryTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests"
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet build tests/RetroDownfall.Arcanum.Covenant.Benchmarks -c Release --disable-build-servers -m:1
```
Task 11 changes `TurnCommit`, so it always regenerates the capsules and always lists the TSV (R31). Review the diff: the changed rows may name only files this task edits, or `MemoryErasureEvidence.cs` as a newly reachable callee. Any other row → stop and report.

- [ ] **Step 5: Mutation check**
  - Delete the `ContainsAsync` step of `ClassifyAsync` → `An_agent_proposal_of_a_fingerprinted_Campaign_key…` and the probe theory's `Withheld` row RED.
  - Map `Absent` to `KeyUnavailable` in `ClassifyAsync` → the `Absent` rows of `Agent_writes_without_a_key_map_the_latch_state_to_its_refusal`, `The_lane_head_probe_maps_the_latch_state_like_the_kernel` and `Resolving_while_Covenant_evidence_is_unverifiable_is_refused` RED (R16).
  - Answer `Clear` when `Key is null` → the `none`, `Unresolved`, `Unavailable` and `Malformed` rows of `Agent_writes_without_a_key_map_the_latch_state_to_its_refusal`, and the `Unresolved` and `Malformed` rows of `The_lane_head_probe_maps_the_latch_state_like_the_kernel`, RED.
  - Skip `AnyForeignAsync` → `…carries_another_key_id` and the probe theory's foreign row RED.
  - Apply the check to every origin → `An_operator_set_of_a_fingerprinted_key_still_commits(false)` RED.
  - `GrimoireRepository` passes `None` instead of `CaptureErasureGate()` → `A_turn_publishes_an_unrelated_proposal…` RED.
  - Drop the `IsPinned` arm in `ResolveProposedLaneAsync` → `A_proposal_against_a_pinned_lane…` RED; restore the old pin text → `A_pin_refusal_and_a_fingerprint_refusal_are_indistinguishable` RED.
  - Have the store call `erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe)` instead of `FromLatch` → `The_lane_head_probe_maps_the_latch_state_like_the_kernel` RED (the counting store sees a call).
  - Restore the parameterless `CovenantMutationKernel()` → `Every_erasure_chokepoint_owner_requires_the_key_provider(CovenantMutationKernel)` RED; give `CovenantStore`'s provider a `= null` default → its row RED (R24).
  - In `ClassifyAsync`, replace `MemoryErasureEvidence.AnyAsync` with a raw `SELECT EXISTS(SELECT 1 FROM memory_erasure_fingerprints WHERE StoreCode = 1)` → `An_agent_proposal_while_the_version_twelve_sweep_is_pending_needs_no_erasure_key` RED (no such table) (R25).
  - Put the Core-version read back before the table check in `IsInstalledAsync` → `An_absent_table_answers_no_evidence_without_reading_core_metadata` and `A_catalog_without_Core_metadata_or_evidence_answers_clear` RED.
  - Remove the `MemoryErasureKeyWarmup.RunAsync` call from `GrimoireDatabaseBootstrapper` (Task 6) → `A_restarted_host_with_only_Covenant_fingerprints…` RED: the latch stays `Unresolved`, and the unrelated proposal answers `MemoryErasure.KeyUnavailable` (R11).

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/{CovenantAgentErasureGate,CovenantMutationKernel,CovenantStore}.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantLaneHeadProbe.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantManagementReadContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/{CovenantTurnHeadProbe,CovenantMutationService,CovenantMemoryReviewService}.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/{CovenantErasureSuppressionTests,CovenantErasureRestartTests,CovenantCanonicalFixture,CovenantServiceHarness,CovenantMutationFixture,CovenantPinEnforcementTests,CovenantRetirementPreflightTests,CovenantMutationConcurrencyTests,CovenantMutationServiceTests,CovenantErasureSameProcessTests,CovenantAcrossSessionsTests}.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/{CovenantMidUpgradeErasureTests,CovenantUngatedRetirementEvolutionTests,SagaMemoryMidUpgradeWriteTests}.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureEvidenceTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantMutationToolTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCapabilityFixtures.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/{CovenantProposalPublicationTests,CovenantBootstrapProposalTests,CovenantOperatorJourneyTests}.cs tests/RetroDownfall.Arcanum.Tests/Memory/CovenantMemoryReviewServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs tests/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantWorkloadBed.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/{grimoire-admission-input-catalog-v1.txt,grimoire-admission-workload-v1.json,AdmissionBenchmarkManifest.cs} tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: refuse agent Covenant writes at erased keys" -m "The mutation kernel and both staging handlers refuse agent authorship of a fingerprinted identity with the pin's operator-managed refusal, and map an unverifiable store to key loss or key unavailability from the latch state." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Erase protocol infrastructure: tokens, receipts, exposure, retained copies, WAL scrub

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureTokens.cs` (`IMemoryErasureTokenCodec`, the spine's two token-facts records, `MemoryErasureIssuedToken`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryReviewTokenCodec.cs` (private `TokenPurpose` at :421 gains `ErasurePlan = 4`, `ErasureKeyReset = 5`; the class also implements `IMemoryErasureTokenCodec`)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireWalCheckpoint.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs` (`CovenantWalCheckpointOutcome.IsTruncated`; `RequireTruncated` at :57 uses it; delete the private `CheckpointAsync` at :1791 and call the helper at :399, :418 and :1133)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs` (`TruncateWalAsync` at :757–791 delegates to the helper and keeps its `IntegrityError()` mapping)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureScrubber.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureExposure.cs` (`MemoryErasureExposure`, `MemoryErasureRetainedCopies`)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureProtocol.cs` (`MemoryErasureProtocol`, `MemoryErasureNotes`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Logging/InferenceAuditLogger.cs` (`ResolvePathParts` at :283 becomes `internal static`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (the codec registration at :1387; the scrubber singleton)
- Modify: `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryReviewTokenCodecTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireWalCheckpointTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureScrubberTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureExposureTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureProtocolTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (the §19.3 pins R8 assigns to Task 12; Tasks 13, 16 and 17 add theirs here)
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (one row); `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`FreshMigrationMembers` entry; `ExpectedProductionAcquisitionCount` **+1** over the value Task 11 committed)
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated: the `CheckpointAsync`/`TruncateWalAsync` DatabaseAccess rows move to `GrimoireWalCheckpoint.TruncateAsync`)
  - `tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs` (`Persisted_instant_writers_use_the_central_codec_and_new_writers_require_review` admits `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureExposure.cs`)
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt` (+5 Infrastructure `.cs` files), `grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`), `AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19)

**Interfaces:**
- Consumes:
  - Task 1: `MemoryErasureKey` and its wrappers, `MemoryErasureDigestGrammar`, every enum and DTO in `MemoryErasureContracts.cs`, `MemoryErasureScrubPendingReasons.ToMask/FromMask`, `MemoryRetainedLocalCopies.ToMask/FromMask` (the only retained-copies mask helper), `MemoryErasureIdentity`, `ErrorCodes.MemoryErasure.*` with their `ArcanumErrorMapper` arms.
  - Task 2: `IMemoryErasureKeyProvider`, `IMemoryErasureKeyCreator`, `MemoryErasureKeyState`, `MemoryErasureKeyProbe`.
  - Task 6: `MemoryErasureEvidence.{IsInstalledAsync, AnyAsync, AnyForeignAsync, ReadReceiptAsync, ClearWalPendingAsync, InsertReceiptAsync}` and `MemoryErasureReceiptRow`.
- Produces:
  - `internal sealed record MemoryErasureIssuedToken(string Token, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc)`; `internal interface IMemoryErasureTokenCodec { Result<MemoryErasureIssuedToken> IssueErasurePlan(MemoryErasurePlanTokenFacts facts); Result<MemoryErasurePlanTokenFacts> ReadErasurePlan(string token); Result<MemoryErasureIssuedToken> IssueErasureKeyReset(MemoryErasureKeyResetTokenFacts facts); Result<MemoryErasureKeyResetTokenFacts> ReadErasureKeyReset(string token); }`, plus the spine's two facts records. The codec is the one clock for a token and the DTO times that describe it.
  - `GrimoireWalCheckpoint.TruncateAsync(SqliteConnection, CancellationToken) → Task<Result<CovenantWalCheckpointOutcome>>`; `CovenantWalCheckpointOutcome.IsTruncated`.
  - `MemoryErasureScrubber(IGrimoireOrdinaryConnectionFactory connections).CheckpointAsync(CancellationToken) → Task<MemoryErasureWalCheckpointAttempt>`.
  - `MemoryErasureExposure.ReadSagaAsync(SqliteConnection, SqliteTransaction?, IReadOnlyList<string> memoryIds, CancellationToken)`, `ReadLexiconAsync(SqliteConnection, SqliteTransaction?, Guid entryId, string normalizedName, Guid? campaignId, CancellationToken)`, `ReadCovenantAsync(SqliteConnection, SqliteTransaction?, Guid entryId, CancellationToken)`, each `→ Task<MemoryErasureExternalExposureDto>`; `MemoryErasureExposure.EvidenceCodes(MemoryErasureExternalExposureDto) → IReadOnlyList<MemoryExternalEvidence>` (channel order 1..5).
  - `MemoryErasureRetainedCopies.For(MemoryReviewStore store, bool auditFilesExist) → MemoryRetainedLocalCopy[]` and `.AuditFilesExist(ArcanumSettings) → bool`. It has no mask methods (M3).
  - `internal static class MemoryErasureProtocol`:
    - `RequireInstalledAsync(SqliteConnection, CancellationToken) → Task<Result>`;
    - `OpenKeyForPrepareAsync(SqliteConnection, IMemoryErasureKeyCreator, MemoryReviewStore store, CancellationToken) → Task<Result<MemoryErasureKey>>`;
    - `OpenKeyForApplyAsync(SqliteConnection, IMemoryErasureKeyProvider, MemoryReviewStore store, CancellationToken) → Task<Result<MemoryErasureKey>>`;
    - `ProbeReceiptAsync(SqliteConnection, SqliteTransaction?, MemoryReviewStore, Guid mutationId, byte[] requestDigest, CancellationToken) → Task<Result<MemoryErasureReceiptRow?>>`;
    - `FinishAsync(SqliteConnection, MemoryErasureScrubber, MemoryErasureReceiptRow, bool replayed, MemoryErasureNote[] notes, CancellationToken) → Task<MemoryErasureResultDto>`.
  - `internal static class MemoryErasureNotes { MemoryErasureNote[] For(MemoryReviewStore store, MemoryErasureScopeKind scope, bool reclaimsKey); }` (R18).
  - `MemoryErasureStructuralTests.ContentFreeLogFiles`, the closed list every later task appends its new erasure source files to.

- [ ] **Step 1: Write the failing tests**

`MemoryReviewTokenCodecTests` (extend; `Bytes(b)` = 32 bytes of `b`):

```csharp
[Fact]
public void An_erasure_plan_round_trip_binds_store_digests_binding_and_generation()
{
    FakeTimeProvider time = FrozenTime();
    IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(time);
    Guid generation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    MemoryErasureIssuedToken issued = codec.IssueErasurePlan(new(MemoryReviewStore.Saga, Bytes(0x11), Bytes(0x22), Bytes(0x33), generation)).Value;
    Assert.Equal(220, issued.Token.Length);                           // 18 + 115 + 32 bytes, unpadded base64url
    Assert.Equal(4, Base64Url.DecodeFromChars(issued.Token)[1]);      // TokenPurpose.ErasurePlan
    Assert.Equal(time.GetUtcNow(), issued.IssuedAtUtc);
    Assert.Equal(issued.IssuedAtUtc + MemoryReviewLimits.TokenLifetime, issued.ExpiresAtUtc);
    MemoryErasurePlanTokenFacts read = codec.ReadErasurePlan(issued.Token).Value;
    Assert.Equal((MemoryReviewStore.Saga, generation), (read.Store, read.DatasetGeneration));
    Assert.Equal(Bytes(0x11), read.RequestDigest);
    Assert.Equal(Bytes(0x22), read.EffectDigest);
    Assert.Equal(Bytes(0x33), read.ContentBinding);
}
```

Also:
- `A_lexicon_erasure_plan_without_label_round_trips_absent_binding_and_generation`: `ContentBinding` and `DatasetGeneration` read back null.
- `A_key_reset_round_trip_binds_status_and_counts`: `(Lost, 7, 3)`; the token is 90 characters and byte 1 is 5.
- `Erasure_tokens_and_review_tokens_refuse_each_others_purposes`: a plan token fails `ReadCursor`, `ReadObservation`, `ReadPreparedPlan` and `ReadErasureKeyReset`; a cursor token fails `ReadErasurePlan`; every erasure-read failure carries `ErrorCodes.MemoryErasure.InvalidPreflight`.
- `An_erasure_plan_token_is_refused_at_its_expiry_boundary`: `time.Advance(MemoryReviewLimits.TokenLifetime)` → `InvalidPreflight`.
- `Erasure_plan_facts_are_validated_before_issue`: store `Covenant`, a 31-byte digest, a `ContentBinding` on a Lexicon plan, and a `Guid.Empty` generation each fail with `MemoryReview.InvalidTokenFacts`.

`MemoryErasureScrubberTests` (`[Collection("Grimoire")]`, fixture database at head, `FixtureOrdinaryConnectionFactory.For(db)`):
- `Checkpoint_truncates_a_quiet_database_on_an_unpooled_read_write_connection` → `Truncated`; `factory.Kinds == [ReadWrite]`.
- `Checkpoint_reports_busy_within_the_short_wait_while_a_reader_holds_an_older_snapshot`: a fresh `ReadOnly` lease runs `BEGIN; SELECT count(*) FROM grimoire_feature_schemas;`, then one committed write lands on a second connection (a scratch table the test creates). Assert `Busy` and `stopwatch.Elapsed < TimeSpan.FromSeconds(3)`: the connection default wait is 5000 ms, the scrubber's is 250 ms.
- `Checkpoint_reports_unavailable_when_the_fresh_open_is_refused` (a stub factory returning a failure) → `Unavailable`.

`GrimoireWalCheckpointTests`:
- `A_quiet_checkpoint_is_truncated` → `IsTruncated`.
- `The_checked_truncate_pragma_has_one_source`: under `src/`, the string literal `"PRAGMA wal_checkpoint(TRUNCATE);"` appears only in `Data/GrimoireWalCheckpoint.cs`, the unchecked shutdown checkpoint `Hosting/GrimoireDatabaseBootstrapper.cs`, and the runtime probe `Data/SqliteNativeRuntimeValidator.cs`. Comments do not count.

`MemoryErasureExposureTests` (fixture database). Rows come through production writers: `SagaMemoryStore.InsertAsync`; `LexiconService.UpsertAsync` for agent authorship and the Lexicon curation `CorrectAsync` for operator-only; the `CovenantServiceHarness` operator set and an agent-propose kernel intent. Receipts come through `CovenantDisclosureTransactionWriter.AcknowledgeAsync`, with drafts shaped like `CovenantDisclosureJournalTests.Draft`.
- `[Theory] Each_store_reports_its_fixed_channel_rules`, with no receipts:
  - Saga → `[Known, NotRecorded, Known, NotRecorded, NotRecorded]`;
  - agent-scribed Lexicon → `[Known, NotRecorded, NotApplicable, NotRecorded, NotRecorded]`;
  - operator-only Lexicon → `[NotRecorded, NotRecorded, NotApplicable, NotRecorded, NotRecorded]`;
  - agent-proposed Covenant → `[Known, NotRecorded, NotApplicable, NotRecorded, NotRecorded]`.

  In every row `Revocation == NotPerformed` and the channels are in code order 1..5.
- `A_backup_operation_counts_by_its_latest_receipt`: one Operation with its snapshot receipt before the window start and its archive receipt after → `EncryptedBackup = ReceiptWindow`; an operation with both receipts before → `NotRecorded`.
- `The_saga_backup_window_starts_at_the_earliest_twin`: ids created at t0 < t1 and a backup operation at t0 < r < t1 → `ReceiptWindow` for `[id0, id1]`, `NotRecorded` for `[id1]`.
- `A_lexicon_entry_without_a_claim_has_an_unbounded_backup_window`: any backup operation gives `ReceiptWindow`.
- `The_covenant_provider_window_is_time_only`: a Covenant-derived nonrevocable ProviderDispatch receipt that names a different dataset generation, after `CreatedAtUtc` → `InferenceProviderContext = ReceiptWindow`; a receipt before `CreatedAtUtc`, or a clean one (sensitivity 0) → `NotRecorded`.

`MemoryErasureProtocolTests` (fixture database at head; receipts through `MemoryErasureEvidence.InsertReceiptAsync`; keys through `MemoryErasureTestKeys`):
- `Retained_copies_are_the_constant_list_and_audit_only_when_audit_files_exist`: `For(s, false) == [SessionTranscripts, SearchAndSummaryDerivatives, Attachments, ResponseCaches, ApplicationLogs, BackupArchives, OtherLocalState]` for all three stores; `For(s, true)` also holds `AuditLog`; `AuditFilesExist` is true only when a `{stem}-*.jsonl` file exists beside the configured audit path.
- `Notes_are_scope_boundary_notes_in_code_order` (R18):
  - `For(Saga, LegacyUnresolved, false) == [UnresolvedScopeStopsMatchingOnResolution, OtherScopesUnaffected]`; `For(Saga, Global, false) == [OtherScopesUnaffected]`;
  - `For(Lexicon, Campaign, false) == [OtherScopesUnaffected]`;
  - `For(Covenant, Global, b) == [GlobalKeyStillProposableInCampaigns, OtherScopesUnaffected, CovenantDrainsInFlightTurns]` for both values of `b`; `For(Covenant, Campaign, b)` drops the first;
  - `For(Saga, Global, true)` and `For(Lexicon, Global, true)` throw `ArgumentException`.
- `Probe_replays_a_matching_digest_and_conflicts_on_a_different_digest_or_store`: a match returns the row; a different digest or another store → `ErrorCodes.Security.IdempotencyConflict`; absent → `Success(null)`.
- `Finish_upgrades_a_wal_only_pending_receipt_when_the_checkpoint_truncates` → `Local.Outcome == Verified`, `WalCheckpointAttempt == Truncated`, and the stored receipt's `ScrubStateCode == 2`.
- `Finish_keeps_a_non_upgradable_reason_pending` (mask `WalCheckpointPending | FullTextSecureDeleteUnverified`) → `RowsRemovedScrubPending`, `PendingReasons == [FullTextSecureDeleteUnverified]`, and the WAL bit cleared.
- `Finish_does_not_checkpoint_a_verified_receipt` → `NotAttempted`.
- `Apply_key_access_maps_every_key_state` (R16):
  - `Present` gives a key;
  - `Present` with one Saga row under a foreign `KeyId` → `KeyLost` for Saga, and a key for Lexicon;
  - `Absent` with a row in any store → `KeyLost`; `Absent` with no rows → `InvalidPreflight`;
  - `Unavailable` or `Malformed` → `KeyUnavailable`.
- `Prepare_key_access_creates_only_when_no_row_exists`: with no rows, the credential is written once and the key returned; with a row and the credential deleted, `KeyLost` and no credential is written.
- `RequireInstalledAsync_refuses_a_version_twelve_catalog` (`CoreSchemaVersionTwelveFixture.ChainSet()`) → `MemoryErasure.Unavailable`.

`MemoryErasureStructuralTests` (Task 12's R8 pins; `NativeSqlCipherTestPaths.RepositoryRoot()` and Roslyn syntax trees, as `LexiconCurationArchitectureTests` does):
- `Extraction_is_the_only_saga_writer`:
  - across `src/**/*.cs` and `src/**/*.sql` (excluding `bin`/`obj`), the case-insensitive pattern `INSERT\s+(OR\s+\w+\s+)?INTO\s+"?saga_memories"?\b` matches exactly once, in `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`;
  - in every `src/` file that names `ISagaMemoryStore`, the invocations whose expression is a member access named `InsertAsync` are exactly `[("src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs", 2)]`.
- `New_erasure_log_templates_are_content_free`: for every file in `ContentFreeLogFiles`, the file exists, and no string literal passed to an invocation whose name starts with `Log`, nor any `[LoggerMessage]` `Message`, contains `{Name}`, `{Key}`, `{NormalizedKey}`, `{Content}`, `{Fact}` or `{Facts}` (placeholder names compared case-insensitively). Task 12 seeds the list with every new erasure source file that exists at this point:
  - `Security/MemoryErasureKeyring.cs` (Task 2);
  - `Data/MemoryErasureEvidence.cs`, `Data/MemoryErasureGuard.cs`, `Data/MemoryErasureKeyWarmup.cs` (Task 6);
  - `Data/SagaErasureWriteGate.cs` (Task 7);
  - `Data/Covenant/CovenantAgentErasureGate.cs` (Task 11);
  - `Memory/MemoryErasureTokens.cs`, `Data/GrimoireWalCheckpoint.cs`, `Memory/MemoryErasureScrubber.cs`, `Memory/MemoryErasureExposure.cs`, `Memory/MemoryErasureProtocol.cs` (this task).

  Every path is under `src/RetroDownfall.Arcanum.Infrastructure/`. Existing chokepoint files such as `LexiconService.cs` are not listed, because they already log names under the old rule and the spec's rule covers new log lines.
- `The_log_template_scan_flags_a_named_placeholder`: the same scanner, run over an inline source containing `logger.LogWarning("Erased {Name}.", name)`, reports exactly one violation. This keeps the scan from passing vacuously.

- [ ] **Step 2: Run the tests to verify they fail**

First add the new members as signatures only: the codec's four methods return `InvalidPreflight`, and every other new method throws `NotImplementedException`. Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryReviewTokenCodecTests|FullyQualifiedName~MemoryErasureScrubberTests|FullyQualifiedName~GrimoireWalCheckpointTests|FullyQualifiedName~MemoryErasureExposureTests|FullyQualifiedName~MemoryErasureProtocolTests|FullyQualifiedName~MemoryErasureStructuralTests"
```

Expected RED: every new codec, scrubber, checkpoint, exposure and protocol test fails on its assertion or on `NotImplementedException`. The existing codec tests stay green. The three structural pins pass on arrival: they characterize the current tree, and Step 5 proves they are load-bearing.

- [ ] **Step 3: Implement**

- **Codec.**
  - The `ErasurePlan` payload is a fixed 115 bytes: `u8 store ‖ 32 request ‖ 32 effect ‖ u8 hasBinding ‖ 32 binding-or-zero ‖ u8 hasGeneration ‖ 16 generation (big-endian)-or-zero`. `ErasureKeyReset` is 17 bytes: `u8 status ‖ u64be fingerprints ‖ u64be receipts`.
  - Read refuses a presence byte outside {0, 1} and non-zero filler. Every erasure read failure is `new Error(ErrorCodes.MemoryErasure.InvalidPreflight, "The erasure preflight token is invalid, expired, or has the wrong purpose.")`.
  - Issue refuses a `Store` outside {Saga, Lexicon}, a digest that is not 32 bytes, a `ContentBinding` unless the store is Saga, and `Guid.Empty`. It returns `MemoryErasureIssuedToken` with `IssuedAtUtc` from the codec's `TimeProvider` and `ExpiresAtUtc = IssuedAtUtc + MemoryReviewLimits.TokenLifetime`.
  - The codec keeps its process-static key: an in-process host restart does not invalidate a token.
- **DI.** Register one `MemoryReviewTokenCodec` singleton over the host `TimeProvider`, and forward `IMemoryReviewTokenCodec` and `IMemoryErasureTokenCodec` to it. Register `MemoryErasureScrubber` as a singleton over `IGrimoireOrdinaryConnectionFactory`.
- **WAL.** `TruncateAsync` takes the body of the private `CheckpointAsync` and adds the marker pair's second-row refusal. Both refusals return the existing `Covenant.ErasureIncomplete` error. `IsTruncated => Busy == 0 && RemainingFrames <= 0`, and `RequireTruncated` is written in terms of it.
- **Scrubber.** It holds `private readonly IGrimoireOrdinaryConnectionFactory _connections = connections;`. `CheckpointAsync` opens `_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, cancellationToken)`; an open failure is `Unavailable`. On the lease's connection it runs `PRAGMA busy_timeout = 250;` (the connection is unpooled and disposed, so nothing leaks), then `TruncateAsync`: a failure is `Unavailable`, `IsTruncated` is `Truncated`, anything else is `Busy`. Its one log line carries the attempt value only.
- **Exposure SQL.** An absent `external_disclosure_receipts` reads as no receipts. Every window start is bound as `UtcInstantText.Format(UtcInstantText.Parse(stored))`; the Saga start is the minimum over the ids, parsed in C#.

```sql
-- Backup window, all stores: operations, bounded by each operation's latest receipt.
SELECT EXISTS (SELECT 1 FROM external_disclosure_receipts
               WHERE SubjectKind = 2 AND EffectCategoryCode = 4 AND DestinationCode = 8 AND RevocabilityCode = 2
               GROUP BY OriginInstallationId, SubjectId
               HAVING $windowStart IS NULL OR MAX(DisclosedAtUtc) >= $windowStart);
-- Provider window, Covenant only: time only, no generation predicate.
SELECT EXISTS (SELECT 1 FROM external_disclosure_receipts
               WHERE DisclosedAtUtc >= $windowStart AND EffectCategoryCode = 1 AND DestinationCode = 1
                 AND RevocabilityCode = 2 AND SensitivityCode = 1);
```

- **Authorship.**
  - Lexicon: `EXISTS(annal_versions v JOIN annal_claims c ON c.ClaimId = v.ClaimId WHERE c.SubjectStoreCode = 2 AND <Keyed c.SubjectId> AND v.OriginCode IN (2, 3))`. The window starts at that claim's `CreatedAtUtc`, or is unbounded when there is no claim. The claim is found by `EntryId`; `normalizedName` and `campaignId` are kept for the spine signature and read by no rule.
  - Covenant: `EXISTS(covenant_versions WHERE <Keyed EntryId> AND OriginCode IN (2, 3))`, windowed from `covenant_entries.CreatedAtUtc`.
  - Saga is always `Known`.
  - Every id predicate uses `CovenantIdentitySql.Keyed`, so any stored spelling matches.
- **Protocol.**
  - `OpenKeyForPrepareAsync` runs with no transaction open. It passes `evidenceRowsExist = AnyAsync(Covenant) || AnyAsync(Saga) || AnyAsync(Lexicon)` to `OpenOrCreate`. `Present` then runs `AnyForeignAsync(store, key.KeyId)`: a hit disposes the key and is `KeyLost`. `Absent` is `KeyLost`; every other state is `KeyUnavailable`.
  - `OpenKeyForApplyAsync` runs with no transaction open and calls `OpenExisting(MemoryErasureKeyProbe.Reprobe)`, because erase is operator-initiated (§5.1). The mapping is the one `Apply_key_access_maps_every_key_state` pins.
  - Row ids go to the grammar as stored or as requested. The grammar canonicalizes every row id to uppercase dashed `D` (R14), so no caller canonicalizes.
  - `FinishAsync` requires the connection to have no transaction open. When the receipt is Pending with the WAL bit set, it runs `scrubber.CheckpointAsync`; on `Truncated` it runs `ClearWalPendingAsync(mutationId)` and re-reads the receipt. It builds the DTO from the receipt alone:
    - `Outcome = Verified` iff `ScrubStateCode == 2`;
    - `SuppressionFingerprintRecorded = true`;
    - `EffectDigest` as lowercase hex;
    - `External` from the five codes;
    - copies from `MemoryRetainedLocalCopies.FromMask`.

    Every caller invokes `FinishAsync` after `COMMIT` with `CancellationToken.None`, so a committed erase always reports its result.
  - `MemoryErasureNotes.For` returns, in code order:
    - `GlobalKeyStillProposableInCampaigns` for Covenant `Global`;
    - `UnresolvedScopeStopsMatchingOnResolution` for Saga `LegacyUnresolved`;
    - `OtherScopesUnaffected` always;
    - `CovenantDrainsInFlightTurns` for Covenant.

    `reclaimsKey` must be false for Saga and Lexicon (`ArgumentException`). It adds no value to the closed four-value set. The reclaim consequences are carried by `CovenantErasurePlanFacts.ReclaimsKey`, which Task 19 renders (R29).
- **Inventories.**
  - Add the acquisition row `("src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureScrubber.cs", "MemoryErasureScrubber", "CheckpointAsync(1)", AcquisitionConstructKind.MarkedRouteInvocation, "OpenFreshAsync", 2, "_connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite,cancellationToken)")` as `LiveGrimoire` / `ServingRawOrdinary` / `OrdinaryConnectionFactory`, the matching `FreshMigrationMembers` entry, and `ExpectedProductionAcquisitionCount` + 1.
  - Regenerate the capsules with `ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1` on `HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery`, and review that only the checkpoint rows moved.
  - Add `MemoryErasureExposure.cs` to the UTC-format owner list, in ordinal position.
  - Benchmark: `git add` the five new sources; insert `R\t<path>` lines in ordinal order; run `GrimoireAdmissionBenchmarkManifestTests.Checked_in_input_catalog_exactly_closes_every_tracked_benchmark_and_product_input`; copy the shape digest its failure reports into both `grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`) and `AdmissionBenchmarkManifest.cs:19` (`ExactInputCatalogShapeDigest`); rerun.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then the cluster this task can break:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantLocalErasureStorageHealthTests|FullyQualifiedName~HostToolsMarkerPairResetDatabaseTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~CovenantDisclosureJournalTests|FullyQualifiedName~InferenceAuditLogger"
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Drop `PRAGMA busy_timeout = 250` | `Checkpoint_reports_busy_within_the_short_wait…`, on elapsed time |
| `IsTruncated` ignores `RemainingFrames` | `CovenantLocalErasureStorageHealthTests.Only_a_checkpoint_that_was_neither_busy_nor_partial_counts_as_truncated` |
| Backup `HAVING` uses `MIN(DisclosedAtUtc)` | `A_backup_operation_counts_by_its_latest_receipt` |
| Add `AND substr(ExactGenerationIds,1,16) = $gen` to the provider window | `The_covenant_provider_window_is_time_only` |
| Lexicon authorship ignores `OriginCode` (always `Known`) | `Each_store_reports_its_fixed_channel_rules` (operator-only Lexicon row) |
| Map `ErasurePlan` to purpose byte 3 | `Erasure_tokens_and_review_tokens_refuse_each_others_purposes` |
| `FinishAsync` skips `ClearWalPendingAsync` | `Finish_upgrades_a_wal_only_pending_receipt…` |
| `ProbeReceiptAsync` ignores the store | `Probe_replays_a_matching_digest_and_conflicts…` |
| `OpenKeyForApplyAsync` skips `AnyForeignAsync` | `Apply_key_access_maps_every_key_state` |
| Add a second `store.InsertAsync(…)` call to `SagaCurationService.cs` | `Extraction_is_the_only_saga_writer` |
| Put `{Content}` in the scrubber's log template | `New_erasure_log_templates_are_content_free` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureTokens.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryReviewTokenCodec.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireWalCheckpoint.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureScrubber.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureExposure.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureProtocol.cs src/RetroDownfall.Arcanum.Infrastructure/Logging/InferenceAuditLogger.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryReviewTokenCodecTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireWalCheckpointTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureScrubberTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureExposureTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureProtocolTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: add the selective erasure protocol infrastructure" -m "Erasure token purposes, a single checked WAL truncate helper, the post-commit scrubber, external exposure and retained-copy reporting, receipt-first replay, and the only-Saga-writer and content-free-log pins." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Saga erase: service and routes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaRetirementSuppression.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs` (delete the private `SuppressionDigests` at :871; `InsertCoreAsync` at :127 calls `SagaRetirementSuppression.Digests`) and `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs` (the reinstate release at :456 calls it too; the comments at :217 and :450 name the new helper)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureLabels.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryErasureService.cs`
- Create: `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs` (`apiGroup.MapMemoryErasureEndpoints();` after `MapLexiconCurationEndpoints()` at :1212)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (an explicit-factory scoped `ISagaMemoryErasureService` in `AddArcanumInfrastructure`, after `ISagaMemoryReviewService` at :1366; the host container only)
- Modify: `docs/Arcanum.API.md` (two §1 rows after `/api/memory/saga/{id}/unpin` at :112; a new `### 8.35 Selective erasure` after §8.34, holding the shared protocol and a Saga subsection); `docs/Arcanum.DESIGN.md` §4.3 (one sentence naming `MemoryErasureEndpoints`)
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs` (R6)
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaErasureEndpointTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/SagaErasureEndToEndTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (two R8 pins; `ContentFreeLogFiles` gains the three new Infrastructure files)
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (three rows) and `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+3** over Task 12's value)
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated: `InsertCoreAsync`'s syntax digest changes, and `SagaMemoryStore` is on the extraction producer's graph)
  - the benchmark catalog (+3 Infrastructure `.cs` files), the workload digest, and `AdmissionBenchmarkManifest.cs:19`
  - `DocumentationStructureTests.Every_registered_endpoint_has_a_row_in_the_api_reference` is satisfied by the §1 rows

**Interfaces:**
- Consumes:
  - Task 1: `ISagaMemoryErasureService` (`IMemoryErasureServices.cs`, Prepare and Apply only), `MemoryErasureIdentity.ForSaga`, `MemoryErasureKey.{SagaRequest, SagaContentBinding, Subject, Effect, Fingerprint, KeyId}`, `MemoryErasureEffectFacts`, the DTOs, `SagaErasePrepareRequest`, `SagaEraseRequest`, `MemoryRetainedLocalCopies.ToMask`, every `ArcanumJsonContext` registration including `ApiResponse<MemoryErasurePreflightDto>` and `ApiResponse<MemoryErasureResultDto>`.
  - Task 2: `IMemoryErasureKeyCreator`, `IMemoryErasureKeyProvider`.
  - Task 3: the v13 tables.
  - Task 4: `CovenantArtifactPlanRunner.RunAsync` (with `SqliteTransaction?`), `CovenantArtifactPlanTally`, whose `Targets` already end with the artifact exactly once (R9), and `SagaVectorMirrorKind`.
  - Task 5: `AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync`.
  - Task 6: `MemoryErasureEvidence.{InsertFingerprintAsync, InsertReceiptAsync, SubjectErasedAsync}`.
  - Task 7: the Saga chokepoint and the pre-embed gate, which the end-to-end test exercises.
  - Task 12: everything it produces.
- Produces:
  - `internal sealed class SagaMemoryErasureService : ISagaMemoryErasureService`, with `PrepareAsync` and `ApplyAsync` only. It has no `ReleaseAsync` (R2).
  - `internal static class SagaRetirementSuppression { (byte[] Settled, byte[] Legacy) Digests(byte[] key, SagaMemoryScopeKind, string? campaignId, string content); Task<int> CountPairAsync(DbConnection, DbTransaction?, SagaMemoryScopeKind, string? campaignId, string content, CancellationToken); Task<int> DeletePairAsync(<same>); }`. The two async methods read the retirement key through `SagaSuppressionKeyStore.ReadAsync`, never create it, and return 0 when it is absent. Task 24 reuses this helper (M8).
  - `internal static class MemoryErasureLabels { Task<IReadOnlyList<MemoryErasureLabelRow>> ReadAsync(SqliteConnection, SqliteTransaction?, SensitiveArtifactKind, IReadOnlyList<string> artifactIds, CancellationToken); Task<int> DeleteExactAsync(SqliteConnection, SqliteTransaction?, ICovenantSqliteConnectionInitializer, Guid labelId, string artifactId, CancellationToken); }` with `internal sealed record MemoryErasureLabelRow(Guid LabelId, string ArtifactId, Guid? OwnerCampaignId)`. Task 14 reuses it.
  - `MemoryErasureEndpoints.MapMemoryErasureEndpoints` with `PrepareSagaMemoryErasure` and `EraseSagaMemory`, and the file's shared private helper `ReadBodyAsync<T>`, which later tasks reuse.
  - `MemoryErasureRouteInventoryTests.Routes`, typed `(string Name, string Method, string Path, CovenantAuthorityRequirement? Authority)[]` (R13), the closed list Tasks 14, 16, 17 and 18 extend.
  - `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs`, the one shared driver (R6). It holds the union of the members Tasks 17, 18, 28 and 29 call. Those tasks modify it and never create it.

    | Member | Shape |
    |---|---|
    | class | `internal sealed class MemoryErasureRouteDriver(HttpClient client)` |
    | `Host` | `static ArcanumWebApplicationFactory Host(InMemoryOsCredentialStore credentials, RestartableArcanumProfileFixture? profile = null, bool covenant = false, IWeaveService? weave = null, Action<ArcanumSettings>? configure = null)`. `configure` runs last on the settings override (N10); `A_claimless_correction…` passes `s => s.Features.Annals = false`. Saga and Embeddings are on (`test`/`test-embed`, 64 dimensions); Covenant follows `covenant`; `IOsCredentialStore` is replaced by `credentials`; `IWeaveService` is `weave`, or a fixed 64-dimension vector fake; a non-null `profile` uses the internal `ArcanumWebApplicationFactory(RestartableArcanumProfileFixture)` constructor, so a restart reuses the Grimoire. |
    | `CreateFactory` | `static ArcanumWebApplicationFactory CreateFactory(InMemoryOsCredentialStore credentials) => Host(credentials, covenant: true)` |
    | `InsertSagaAsync` | `static Task<string> InsertSagaAsync(ArcanumWebApplicationFactory factory, string content, Guid? sessionId = null, CancellationToken ct = default)`. It calls `ISagaMemoryStore.InsertAsync` from a host scope with id `Guid.NewGuid().ToString()` (the extraction spelling), asserts `Written`, and returns the id. |
    | `InsertSagaOutcomeAsync` | the same inputs, returning the `SagaMemoryWriteOutcome` without asserting |
    | `FingerprintCountAsync` | `static Task<long> FingerprintCountAsync(ArcanumWebApplicationFactory factory, MemoryReviewStore store, CancellationToken ct = default)` (assertion-only raw `count(*)` by `StoreCode`) |
    | `HoldReaderAsync` | `static Task<IAsyncDisposable> HoldReaderAsync(ArcanumWebApplicationFactory factory, CancellationToken ct = default)`. It opens a fresh `ReadOnly` lease from the host's `IGrimoireOrdinaryConnectionFactory` and runs `BEGIN; SELECT count(*) FROM grimoire_feature_schemas;`; disposal rolls back and disposes the lease. |
    | `EraseSagaAsync` | `Task<MemoryErasureRoundTrip<SagaEraseRequest>> EraseSagaAsync(string memoryId, Guid? mutationId = null, CancellationToken ct = default)`: `GET /api/memory/saga/{id}` → prepare → apply, asserting 200 at each step |
    | `EraseLexiconAsync` | `Task<MemoryErasureRoundTrip<LexiconEraseRequest>> EraseLexiconAsync(string name, Guid? campaignId, Guid? mutationId = null, CancellationToken ct = default)`: `POST /api/memory/lexicon/show` → prepare → apply |
    | `EraseCovenantAsync` | `Task<MemoryErasureRoundTrip<CovenantEraseRequest>> EraseCovenantAsync(CovenantScope scope, Guid? campaignId, string key, Guid? mutationId = null, CancellationToken ct = default)`: `POST /api/memory/covenant/detail` → prepare → apply |
    | `ApplySagaAsync` / `ApplyLexiconAsync` / `ApplyCovenantAsync` | `Task<MemoryErasureResultDto> Apply…Async(<the store's erase request>, CancellationToken ct = default)`, asserting 200; used for replays |
    | `SetCovenantAsync` | `Task<CovenantMutationResultDto> SetCovenantAsync(CovenantScope scope, Guid? campaignId, string key, string content, CancellationToken ct = default)`: `POST /api/memory/covenant/set/prepare` → `PUT /api/memory/covenant` |
    | `PostAsync` | `Task<HttpResponseMessage> PostAsync<TRequest>(string path, TRequest body, JsonTypeInfo<TRequest> info, CancellationToken ct = default)`, raw, for status, header and refusal assertions and for Task 29's curation and review rows |
    | `ReadDataAsync` / `ReadErrorCodeAsync` | `static Task<T> ReadDataAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> info)` (asserts a success envelope); `static Task<string> ReadErrorCodeAsync(HttpResponseMessage response)` |
    | record | `internal sealed record MemoryErasureRoundTrip<TApply>(MemoryErasurePreflightDto Preflight, TApply Apply, MemoryErasureResultDto Result)` |

    Every member except the release trio is written here. `EraseLexiconAsync`, `ApplyLexiconAsync`, `EraseCovenantAsync` and `ApplyCovenantAsync` compile against Task 1 types and are first exercised by Tasks 14 and 16. Task 17 adds exactly `Task<MemoryErasureReleaseResultDto> ReleaseSagaAsync(SagaErasureReleaseRequest, CancellationToken ct = default)`, `ReleaseLexiconAsync(LexiconErasureReleaseRequest, …)` and `ReleaseCovenantAsync(CovenantErasureReleaseRequest, …)`, because `CovenantErasureReleaseRequest` is Task 17's. Tasks 18, 28 and 29 use only the members above.

- [ ] **Step 1: Write the failing tests**

`MemoryErasureRouteInventoryTests` (`[Collection("ApiHost")]`; endpoints from `factory.Services.GetRequiredService<EndpointDataSource>()`):

```csharp
internal static readonly (string Name, string Method, string Path, CovenantAuthorityRequirement? Authority)[] Routes =
[
    ("PrepareSagaMemoryErasure", "POST", "/api/memory/saga/erase/prepare", CovenantAuthorityRequirement.SensitivityRetentionPurge),
    ("EraseSagaMemory", "POST", "/api/memory/saga/erase", CovenantAuthorityRequirement.SensitivityRetentionPurge),
];

[Fact]
public async Task Every_erasure_post_is_a_static_named_route_with_exactly_its_operator_authority()
{
    // For every row whose Method is "POST": Assert.NotNull(row.Authority); the endpoint named row.Name has
    // RoutePattern.RawText == row.Path, HttpMethodMetadata.HttpMethods == ["POST"], no route parameters, and
    // Assert.Equal(row.Authority, Assert.Single(endpoint.Metadata.GetOrderedMetadata<CovenantAuthorityRequirementMetadata>()).Requirement);
    // CovenantConditionalSensitivityPurgeMetadata, CovenantConditionalReadRequirementMetadata and
    // CovenantConditionalExactWriteRequirementMetadata are all absent.
    // The GET row Task 18 adds (Authority null) is proved by Task 18's own assertion.
}
```

Also:
- `The_erasure_route_set_is_exactly_the_declared_routes`: the set of endpoints whose path matches `^/api/memory/(saga|lexicon|covenant)/(erase|release)` or starts with `/api/memory/erasure` equals `Routes`, compared by `(Name, Method, Path)`.
- `[Theory] Every_erasure_post_answers_with_the_protected_header_tuple`: body `{}` → status 400 (any 400 error code; a store may answer its own validation code), with `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0`, and no `ETag` or `Last-Modified` (N7).
- `[Theory] Erasure_posts_refuse_a_host_tools_tainted_installation`: publish a tainted authority snapshot (`HostToolsState` not `Clean`) → every POST row answers 503 `Covenant.OperatorAuthorityUnavailable` and changes nothing (§6.1, §17.1). Later tasks extend `Routes`, so this covers all twelve POSTs.
- `Erasure_routes_refuse_an_unauthenticated_caller` → 401 for every row.

`MemoryErasureStructuralTests` (R8, Task 13's two pins):
- `No_memory_item_owns_a_managed_file`:
  - `CovenantSensitiveArtifactPurgePolicy.Resolve(Saga).Value.Executor` and `Resolve(Lexicon).Value.Executor` are both `CovenantArtifactPurgeExecutor.DatabaseTransaction`;
  - `CovenantSensitiveArtifactPurgePolicy.All.Where(r => r.Executor == ManagedFileKernel).Select(r => r.Kind)` equals `[SensitiveArtifactKind.ManagedWorkspaceFile]`;
  - neither the Saga nor the Lexicon plan's tables (`CovenantArtifactPurgePlans.Resolve(kind)`: every `Projections[i].Table`, then `Artifact.Table`) nor `CovenantCanonicalContentTables.InDeletionOrder` (Task 10) contains `managed_file_write_intents` or `local_erasure_work_items`.
- `Erase_is_not_a_search_or_review_action`: `Enum.GetNames<MemorySearchActionKind>()` equals `["ShowSagaMemory", "ShowLexiconEntry"]`, and `Enum.GetNames<MemoryReviewAction>()` equals `["Confirm", "Correct", "Retire", "Pin", "Unpin"]`.

`SagaErasureEndpointTests` (`[Collection("ApiHost")]`; `MemoryErasureRouteDriver.Host(credentials)` with one `InMemoryOsCredentialStore`; two Campaigns and a Session in each created through their routes; memories through `InsertSagaAsync`; targets from `GET /api/memory/saga/{id}`). The text is `T = "The ward-stone lies under the mill."`. Every test that erases ends with `AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync`.
- `Prepare_then_apply_erases_the_twin_class_in_scope_and_leaves_other_scopes_and_content`: Session A holds the target, two twins of `T` and one other text; Session B holds `T`.
  - Before apply, an assertion-only raw count of every plan table's rows for the three ids is taken; call its sum `Before`.
  - Preflight: `Plan.ErasedItemCount == 3`; `Plan.RowsToRemove == Before` (R9: the artifact is counted once); `Notes == [OtherScopesUnaffected]`; `External.Revocation == NotPerformed`; channels `[Known, NotRecorded, Known, NotRecorded, NotRecorded]`; `RetainedLocalCopies` equals Task 12's constant list without `AuditLog`.
  - Apply returns 200 with `Replayed == false`, `EffectDigest == preflight.EffectDigest`, `Local.ErasedItemCount == 3`, `Local.RemovedRowCount == preflight.Plan.RowsToRemove`, and `SuppressionFingerprintRecorded`.
  - Afterwards the three ids answer 404 `Saga.NotFound`, while Session B's `T` and Session A's other text answer 200. `saga_memory_embeddings` holds 0 rows for the three ids, `memory_erasure_receipt_subjects` holds 3 rows for the MutationId, and `FingerprintCountAsync(Saga) == 1`.
- `A_retired_labelled_memory_is_erased_with_its_label_and_retirement_suppression_pair`: Covenant is enabled (`covenant: true`); the memory is retired through `POST /api/memory/saga/{id}/retire` and labelled through `IArtifactSensitivityLedger.LabelAsync`.
  - `Plan.LabelsToRemove == 1`, `Plan.RetirementSuppressionsToRemove == 1`, `RemovedLabelCount == 1`, `RemovedRetirementSuppressionCount == 1`.
  - No `artifact_sensitivity` row remains, and the `saga_suppression_key` row is still present.
- `A_label_that_appeared_after_prepare_is_a_stale_plan` → 409 `MemoryErasure.StalePlan`; the memory, its label and its embedding all remain.
- `A_new_twin_between_prepare_and_apply_is_a_stale_plan` → 409 `StalePlan`; all four rows remain.
- `A_claimless_correction_between_prepare_and_apply_is_a_stale_plan_even_with_the_new_hash`: with `Features.Annals = false`, correct through the route, then apply with the new `ExpectedContentHash` → 409 `StalePlan`, and the corrected row remains.
- `A_stale_content_hash_is_refused_before_anything_is_measured` → prepare returns 409 `Saga.StaleContent`.
- `Prepare_for_an_erased_memory_answers_410_and_an_unknown_memory_answers_404` → `MemoryErasure.SubjectErased` and `Saga.NotFound`.
- `A_second_mutation_prepared_before_the_first_applied_answers_410_at_apply`.
- `A_repeated_apply_replays_the_receipt_without_a_valid_token`: `driver.ApplySagaAsync(roundTrip.Apply with { PreflightToken = "x" })` → `Replayed == true`, with the same `EffectDigest`, `MutationId` and `Local` counts.
- `Concurrent_identical_applies_commit_once_and_replay_once` (`Task.WhenAll`) → two 200s, exactly one with `Replayed == false`, and exactly one receipt row for the MutationId.
- `The_same_mutation_id_for_a_different_request_is_an_idempotency_conflict` → 409 `Security.IdempotencyConflict`.
- `A_token_prepared_for_another_request_is_an_invalid_preflight` → 400 `MemoryErasure.InvalidPreflight`; the memory remains.
- `A_pinned_memory_is_erased_and_the_plan_says_it_was_pinned` → `Plan.Pinned == true`; apply 200.
- `A_non_guid_memory_id_is_an_invalid_body` → 400 `Validation.InvalidBody`.
- Review Focus 4:
  - `Apply_after_token_expiry_replays_when_committed_and_refuses_when_not`. `ServiceOverrides` replaces only the codec: `MemoryReviewTokenCodec`, `IMemoryReviewTokenCodec` and `IMemoryErasureTokenCodec` resolve to one `new MemoryReviewTokenCodec(fakeTime)`, and the host-wide `TimeProvider` stays the system clock, so no timer-driven hosted service sees the fake clock. Prepare A, advance five minutes, apply A → 400 `InvalidPreflight` with the row intact. Prepare and apply B, advance six minutes, apply B again → 200 `Replayed`.
  - `Apply_after_a_host_restart_replays_by_receipt`: `RestartableArcanumProfileFixture` and one shared credential store. Host 1 erases (`R1`) and is disposed. Host 2 on the same profile answers `ApplySagaAsync(R1.Apply with { PreflightToken = "x" })` with 200, `Replayed == true`, `EffectDigest == R1.Result.EffectDigest`, and equal `MutationId` and `Local` counts. The memory still answers 404, and `FingerprintCountAsync(Saga) == 1`. The token codec's key is process-static, so an in-process restart cannot invalidate a token; the uncommitted half of Review Focus 4 is proved by the expiry test above and by `A_token_prepared_for_another_request…`.
  - `A_busy_checkpoint_leaves_the_receipt_pending_and_a_replay_verifies_it`: `HoldReaderAsync` is held during apply → `RowsRemovedScrubPending`, `[WalCheckpointPending]`, `Busy`. Dispose the reader and replay → `Verified`, `Truncated`, `Replayed`.
- Review Focus 5: `Hundreds_of_twins_are_erased_in_one_receipt_with_exact_counts`. Insert 300 twins, and let `R1` be the `Plan.RowsToRemove` of a lone memory of other text, inserted the same way in the same Session. Assert `Plan.ErasedItemCount == 300`, `Plan.RowsToRemove == 300 * R1`, `Local.RemovedRowCount == 300 * R1`, 300 subject rows under one receipt, and the fingerprint count up by exactly 1.

`SagaErasureEndToEndTests.An_erased_memory_is_not_resurrected_by_extraction_and_its_text_is_never_re_embedded` (§19.2 #1):
- The host is `Host(credentials, weave: recording)`, where the recording `IWeaveService` fake returns 64-dimension vectors and records every text. `FakeIntelligence` answers the extraction prompt with `T`.
- Run `SagaExtractionService.ExtractForSessionAsync` once, under a `GrimoireWorkKind.SagaExtraction` work lease from `IGrimoireConnectionAdmissionGate`, then erase the memory with `driver.EraseSagaAsync`.
- Append an entry to the Session and extract again, with conclusions `[T, "The bell tolls at dusk."]`. Assert:
  - the outcome is `Completed`;
  - no row holds `T` in that scope;
  - the second pass recorded only `"The bell tolls at dusk."`;
  - the Session watermark advanced to the new entry.

- [ ] **Step 2: Run the tests to verify they fail**

Create `SagaMemoryErasureService` with both methods throwing `NotImplementedException`, register it, and map no route. Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureRouteInventoryTests|FullyQualifiedName~SagaErasureEndpointTests|FullyQualifiedName~SagaErasureEndToEndTests|FullyQualifiedName~MemoryErasureStructuralTests"
```

Expected RED: the two Saga routes answer 404, so every endpoint and end-to-end test fails on its first status assertion, and the route inventory finds no endpoint named `PrepareSagaMemoryErasure`. The two new structural pins pass on arrival as characterization; Step 5 proves them.

- [ ] **Step 3: Implement**

- **Refactor first.** Move `SuppressionDigests`, unchanged, into `SagaRetirementSuppression.Digests`. `CountPairAsync` and `DeletePairAsync` run `… FROM saga_retirement_suppressions WHERE SuppressionDigest IN (@settled, @legacy)`. Run `SagaSuppressionTests` and `SagaCurationStoreTests` green before continuing.
- **Service.** `SagaMemoryErasureService(ArcanumDbContext db, IMemoryErasureKeyCreator keyCreator, IMemoryErasureKeyProvider keys, IMemoryErasureTokenCodec tokens, MemoryErasureScrubber scrubber, ICovenantOperationGate gate, IOperatorAuthorityContextIssuer issuer, ICovenantSqliteConnectionInitializer initializer, IOptionsMonitor<ArcanumSettings> options)`. It opens the scoped connection through a private `OpenConnectionAsync` shaped like `SagaMemoryStore.OpenConnectionAsync`, called once from `PrepareAsync` and once from `ApplyAsync`.
- **Invariants shared by prepare and apply:**
  - The target is read by `CovenantIdentitySql.Keyed("Id", "$id")` bound to `CovenantIdentitySql.Key(Guid.Parse(MemoryId))`. If it is absent, the answer is 410 when `SubjectErasedAsync(key.Subject(Saga, MemoryId))`, otherwise `Saga.NotFound`.
  - `ExpectedContentHash` (hex, either case) is compared fixed-time with `AnnalContentDigest.ForSagaMemory(content)`. The claim head (`annal_heads` joined to `annal_claims`, `SubjectStoreCode = 1`) is compared with `ExpectedClaimVersionId`, where null means claimless. Either mismatch is `Saga.StaleContent`.
  - The twins share the target's `ScopeKindCode` and Campaign and have byte-identical `Content`. They are found by seeking `idx_saga_memories_scope` with the Campaign bound in its four spellings (upper and lower, dashed and `N`). An unparseable stored Campaign, or a non-GUID twin id, is `MemoryErasure.ErasureIncomplete`: fail closed, never fingerprint loosely.
  - The plan runs `RunAsync(Saga, CovenantIdentitySql.Key(id), mode)` per memory. `Targets` are the per-table sums across the class, in the tally's order, which already ends with `saga_memories` once. No extra artifact row is appended (R9).
  - Labels come from `MemoryErasureLabels.ReadAsync(Saga, ids)` and must all name one owner scope, otherwise `Covenant.ForbiddenAuthority`: one erase holds one write lease.
  - The effect facts are: `Store = Saga`; `RowIds` = the stored ids (the grammar canonicalizes and sorts them); `Versions` aligned with them; `Targets`; `Labels`; `RetirementSuppressions` (from `CountPairAsync`); `Flags = Pinned` when any member has `PinnedAtUtc`; `Covenant = null`; `Evidence = EvidenceCodes(ReadSagaAsync(ids))`; `RetainedCopiesMask = MemoryRetainedLocalCopies.ToMask(MemoryErasureRetainedCopies.For(Saga, AuditFilesExist(options.CurrentValue)))`.
- **Prepare.** The order is a decision:
  1. `RequireInstalledAsync`, then `OpenKeyForPrepareAsync(…, Saga)`, before any transaction or lease.
  2. One deferred read transaction measures everything above.
  3. That snapshot closes. Only then, when labels exist, does a short `gate.AcquireReadAsync(owner)` record `Snapshot.DatasetGeneration` and get disposed. No gate wait ever holds a SQLite snapshot.
  4. `IssueErasurePlan(new(Saga, key.SagaRequest(MutationId, MemoryId, claimVersion), key.Effect(facts), key.SagaContentBinding(MemoryId, content), generation))`. The DTO takes `IssuedAtUtc`, `ExpiresAtUtc` and `PreflightToken` from the issued token.
  - The plan DTO: `ErasedItemCount = ids.Count`; `RowsToRemove = Σ Targets.Rows`; `LabelsToRemove` and `RetirementSuppressionsToRemove` counted separately (R9); `Pinned`; `Lexicon = null`; `Covenant = null`. `Notes = MemoryErasureNotes.For(Saga, MemoryErasureIdentity.ForSaga(scope, campaign, content).Scope, false)`.
- **Apply.** The order is a decision, following spec §6.2:
  - `OpenKeyForApplyAsync(…, Saga)` → the request digest from the request alone → `ProbeReceiptAsync` with no transaction open, where a replay goes straight to `FinishAsync` → `ReadErasurePlan`: the store must be Saga and the digest must match fixed-time, otherwise `InvalidPreflight`.
  - When the token carries `DatasetGeneration`: pre-read the label owner → `AcquireWriteAsync(owner)` → `CovenantArtifactErasureAuthority.ForOrdinary(lease, context, issuer)` → the lease's generation must equal the token's, otherwise `StalePlan`. This all happens before `BEGIN`.
  - `SqliteBusyRetry` + `BEGIN IMMEDIATE`, then: re-probe (a replay rolls back and finishes) → subject check (410) → re-read and compare the hash, the claim and the `ContentBinding` (a binding mismatch is `StalePlan`) → the label proof (no label when the token names no generation; otherwise re-read each exact label and require `authority.Covers(owner)`) → re-measure and recompute the effect (`StalePlan`) → `RunAsync(Delete)` per memory, `DeleteExactAsync` per label, `DeletePairAsync` → `InsertFingerprintAsync(key.Fingerprint(ForSaga(scope, campaign, content)), Saga, key.KeyId)` → `InsertReceiptAsync` with one subject digest per memory → the absence proof → `lease.RevalidateAsync` → `COMMIT`.
  - The receipt mask is `WalCheckpointPending`, plus `VectorIndexScrubUnverified` when any Delete tally reports `LegacyVirtualTable`.
  - The absence proof requires every `RunAsync(Count)` target to be zero, `ReadAsync` to find no label, and `CountPairAsync` to be zero; otherwise `ErasureIncomplete`.
  - Dispose the lease and the transaction, then `FinishAsync(…, MemoryErasureNotes.For(Saga, MemoryErasureScopeKind.Global, false), CancellationToken.None)`. The Saga request carries no scope, so a first result and its replay both report `[OtherScopesUnaffected]`. The `LegacyUnresolved` note is a preflight note.
- **Endpoints.** Static handlers `HandleSagaErasePrepareAsync` and `HandleSagaEraseAsync` read the body through `ApiRequestJson.ReadAsync` with explicit `JsonTypeInfo`.
  - Validation: `MemoryId` must be a GUID, the hash 64 hex characters, `ExpectedClaimVersionId` null or a GUID, `MutationId` non-empty, and `PreflightToken` non-empty on apply. Any failure is `Validation.InvalidBody`.
  - They pass `CovenantRequestFeatures.Authority(ctx)!.Context` and return `Results.Json(ApiResponse<T>.FromResult(...), ArcanumJsonContext.Default.ApiResponse…, statusCode: ArcanumErrorMapper.ResolveStatusCode(...))`.
  - Both routes carry `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.SensitivityRetentionPurge)` and `.WithName(...)`. The authority middleware marks the response protected, which applies the header tuple on every status.
  - No `ArcanumJsonContext` edit is needed; Task 1 registered every type (R4).
- **Driver.** Write `MemoryErasureRouteDriver` exactly as listed under Produces.
- **Inventories.**
  - Acquisition rows, as `LiveGrimoire` / `ServingRawOrdinary` / `OrdinaryConnectionFactory`:
    - `("…/Memory/SagaMemoryErasureService.cs", "SagaMemoryErasureService", "OpenConnectionAsync(1)", ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)")`;
    - `PrepareAsync(2)` and `ApplyAsync(3)`, each `ProviderOpen`, `"OpenConnectionAsync"`, 1, `"OpenConnectionAsync(cancellationToken)"`.

    `ExpectedProductionAcquisitionCount` rises by 3.
  - Regenerate the capsule TSV and review that only the `SagaMemoryStore` rows moved.
  - `ContentFreeLogFiles` gains `Data/SagaRetirementSuppression.cs`, `Data/MemoryErasureLabels.cs` and `Memory/SagaMemoryErasureService.cs`.
  - Benchmark: the three files and both digests, as in Task 12.
- **Documentation.** API §8.35 states prepare, the five-minute token, receipt-first apply, the result fields, the status table (400/404/409/410/500/503) and the Saga subsection, with no tracker numbers. The §1 rows name both routes.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaSuppressionTests|FullyQualifiedName~SagaCurationStoreTests|FullyQualifiedName~SagaCurationEndpointTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~ApiSurfaceContractTests|FullyQualifiedName~CovenantSensitivePurgeRouteInventoryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| The twin query returns only the target | `Prepare_then_apply_erases_the_twin_class…` and `Hundreds_of_twins…` |
| Append `("saga_memories", Σ ArtifactRows)` to `Targets` again | `Prepare_then_apply…` (`Plan.RowsToRemove == Before`) |
| Skip `DeletePairAsync` | `A_retired_labelled_memory…`, which reports the proof's 500 |
| Skip the effect recompute | `A_new_twin_between_prepare_and_apply_is_a_stale_plan` |
| Remove the pre-transaction receipt probe | `A_repeated_apply_replays_the_receipt_without_a_valid_token` |
| Remove the in-transaction re-probe | `Concurrent_identical_applies_commit_once_and_replay_once` |
| Remove the subject 410 check | `A_second_mutation_prepared_before_the_first_applied_answers_410_at_apply` |
| Skip the `ContentBinding` compare | `A_claimless_correction…` |
| Skip `saga_memory_embeddings` in Delete | `Prepare_then_apply…` answers 500 `ErasureIncomplete` |
| Remove `InsertFingerprintAsync` | `SagaErasureEndToEndTests` |
| Add `Erase = 6` to `MemoryReviewAction` | `Erase_is_not_a_search_or_review_action` |
| Give the Lexicon purge rule `ManagedFileKernel` | `No_memory_item_owns_a_managed_file` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/SagaRetirementSuppression.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureLabels.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryErasureService.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs docs/Arcanum.API.md docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaErasureEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/SagaErasureEndToEndTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: erase one Saga memory and its identical-content class" -m "Prepare and apply routes under retention-purge authority, with twins, labels and retirement pairs removed in one verified transaction, the shared erasure route driver, and the managed-file and action-set pins." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Lexicon erase: service and routes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Erasure.cs` (partial `LexiconService : ILexiconErasureService`, `internal sealed record LexiconErasureDependencies`, `internal static class LexiconDaemonStateNames`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs` (the primary constructor at :36, which Task 8 gave the required fourth parameter `IMemoryErasureKeyProvider erasureKeys`, gains a last parameter `LexiconErasureDependencies? erasure = null`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs` (the private `IsProtectedDaemonStateName` at :229 is deleted; its callers use `LexiconDaemonStateNames.Is`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (in `AddArcanumInfrastructure`, beside the `LexiconService` forwards at :1371–1377: a scoped `LexiconErasureDependencies` factory and `ILexiconErasureService` → `LexiconService`; the host container only)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs`
- Modify: `docs/Arcanum.API.md` (two §1 rows after `/api/memory/lexicon/unpin` at :105; §8.33, per R27; the §8.35 Lexicon subsection)
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/LexiconErasureEndpointTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconErasureTests.cs`
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs` (`Routes` +2)
  - `tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteInventoryTests.cs`, in `Operator_Lexicon_read_sources_are_closed_to_verified_inspection_and_the_status_only_count`:
    - the consumer scan also matches `ILexiconErasureService`;
    - `expectedConsumers` adds `MemoryErasureEndpoints.cs:HandleLexiconErasePrepareAsync:ILexiconErasureService` and `MemoryErasureEndpoints.cs:HandleLexiconEraseAsync:ILexiconErasureService`;
    - the expected calls add `MemoryErasureEndpoints.cs:HandleLexiconErasePrepareAsync:PrepareAsync` and `MemoryErasureEndpoints.cs:HandleLexiconEraseAsync:ApplyAsync`.
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (`ContentFreeLogFiles` + `Lexicon/LexiconService.Erasure.cs`)
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (two rows) and `GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+2** over Task 13's value)
  - the benchmark catalog (+1 Infrastructure `.cs` file), the workload digest, and `AdmissionBenchmarkManifest.cs:19`

**Interfaces:**
- Consumes:
  - Task 1: `ILexiconErasureService` (Prepare and Apply only), `MemoryErasureIdentity.ForLexicon`, `MemoryErasureKey.LexiconRequest`, `LexiconErasePrepareRequest`, `LexiconEraseRequest`, `LexiconErasurePlanFacts`, the JSON registrations.
  - Task 3: v13 `lexicon_fts` secure-delete and `optimize`; `CoreSchemaVersionTwelveFixture`.
  - Task 4: the runner.
  - Task 5: `AnnalsOrphanAssertions`.
  - Task 6: the evidence methods and `MemoryErasureTestKeys`.
  - Task 8: `LexiconService`'s required `erasureKeys`, the scribe chokepoint and `Lexicon.SuppressedNameRefused`.
  - Task 12: everything it produces.
  - Task 13: `MemoryErasureLabels`, `MemoryErasureEndpoints.cs` and its `ReadBodyAsync<T>`, `MemoryErasureRouteInventoryTests.Routes`, and `MemoryErasureRouteDriver.{Host, EraseLexiconAsync, ApplyLexiconAsync}`.
- Produces:
  - `LexiconService : ILexiconErasureService`, with `PrepareAsync(LexiconErasePrepareRequest, CancellationToken)` and `ApplyAsync(LexiconEraseRequest, OperatorAuthorityContext, CancellationToken)` only (R2). They overload the memory-review `PrepareAsync`/`ApplyAsync` in `LexiconService.MemoryReview.cs` by parameter type.
  - `internal sealed record LexiconErasureDependencies(IMemoryErasureKeyCreator KeyCreator, IMemoryErasureTokenCodec Tokens, MemoryErasureScrubber Scrubber, ICovenantOperationGate Gate, IOperatorAuthorityContextIssuer Issuer, ICovenantSqliteConnectionInitializer Initializer)`. It has no `Keys` member; the verbs use the constructor's `erasureKeys` (R5). It has no clock, because the codec issues the times.
  - `LexiconDaemonStateNames.Is(string name)`: `OrdinalIgnoreCase` on the `daemon_state:` prefix.
  - Routes `PrepareLexiconEntryErasure` (`POST /api/memory/lexicon/erase/prepare`) and `EraseLexiconEntry` (`POST /api/memory/lexicon/erase`), both `SensitivityRetentionPurge`.

- [ ] **Step 1: Write the failing tests**

`LexiconErasureTests` (`[Collection("Grimoire")]`, `[SkippableFact]`). Entries come through `LexiconService.UpsertAsync`, `CorrectAsync`, `RetireAsync` and `PinAsync`; the service is built as `new LexiconService(db, logger, options, keys, erasure: deps)` over `MemoryErasureTestKeys.Isolated()`.

```csharp
[SkippableFact]
public async Task An_entry_corrected_and_retired_before_version_thirteen_leaves_no_token_in_lexicon_fts_data()
{
    // v12 catalog: GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 64, ct)
    // scribe "Mill Warden" (Global) with fact "xqzsentinelerase guards the mill"; CorrectAsync to "guards the gate"; RetireAsync.
    Assert.True(await SentinelBlocksAsync(connection) > 0);   // pre-v13 residue exists: the probe is live
    // migrate to head (v13 runs secure-delete + optimize), then prepare and apply the erase from the ShowExactAsync target
    Assert.Equal(0, await SentinelBlocksAsync(connection));
    Assert.Equal(MemoryLocalErasureOutcome.Verified, result.Local.Outcome);
    Result<LexiconEntryDto> scribe = await lexicon.UpsertAsync("  mill warden ", null, ["returns"], LexiconScope.Global, ct);
    Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribe.Error.Code);
    await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection);
}
// SentinelBlocksAsync: SELECT count(*) FROM lexicon_fts_data WHERE instr(block, CAST('xqzsentinelerase' AS BLOB)) > 0
// (the sentinel shares no first letter with any other token, so FTS5 prefix compression cannot hide it)
```

- `With_secure_delete_off_at_start_the_erase_enables_it_merges_and_then_deletes`: on v13, set `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0)`, then scribe and correct to leave residue (the probe reads > 0). After the erase, the probe reads 0, `SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete'` is 1, and `PendingReasons` has no `FullTextSecureDeleteUnverified`.
- `A_scribe_of_an_erased_name_is_refused_in_that_scope_only`: erase in Campaign A; the same name then scribes successfully in Global and in Campaign B, and is refused with `Lexicon.SuppressedNameRefused` in Campaign A.
- `A_global_entry_created_between_prepare_and_apply_is_a_stale_plan` (in `LexiconErasureEndpointTests`): prepare the erase of a Campaign-C entry named N while no Global N exists (`GlobalEntryResurfaces == false`); then create a Global N through host-scope `ILexiconService.UpsertAsync`; apply answers 409 `MemoryErasure.StalePlan`, the C entry is intact, and no receipt exists for the `MutationId`. The curation target is unchanged, so only the effect recompute can refuse it.
- `A_scribe_lexicon_tool_call_after_an_erase_is_refused_with_the_operator_managed_text` (§19.2 #2, production entry point): erase an entry through the route, then invoke `scribe_lexicon` for the same name and scope through the host's diagnostic MCP route `POST /api/mcp/tools/invoke`; the tool result is an error whose text is exactly "This Lexicon entry is managed by the operator in this scope, so nothing was recorded.", and `lexicon_entries` holds no row for that name and scope.
- `A_daemon_state_entry_is_refused_by_name` (`daemon_state:job:abc`) → `Lexicon.InvalidName`, with exactly the message "Unseen Servant daemon_state entries are managed by their daemon job and cannot be erased.", and the entry intact.
- `A_curation_change_between_prepare_and_apply_is_a_stale_target` (pinned through `PinAsync`) → `Lexicon.StaleCurationTarget`, with the entry intact.
- `Without_erasure_dependencies_the_verbs_are_unavailable`: built with the four-argument form `new LexiconService(db, logger, options, MemoryErasureTestKeys.Isolated())` (R5). Both verbs answer `MemoryErasure.Unavailable`, the entry is intact, and `FingerprintCount == 0`.

`LexiconErasureEndpointTests` (`[Collection("ApiHost")]`, `MemoryErasureRouteDriver.Host(credentials)`, the target from `POST /api/memory/lexicon/show`). Every erasing test ends with `AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync`.
- `Prepare_then_apply_erases_the_entry_its_annals_and_provenance`:
  - the preflight has `Plan.ErasedItemCount == 1`, `Plan.Lexicon!.GlobalEntryResurfaces == false`, channels `[Known, NotRecorded, NotApplicable, NotRecorded, NotRecorded]` for a scribed entry, and `Notes == [OtherScopesUnaffected]`;
  - after apply, show answers 404 `Lexicon.NotFound`, no `annal_claims` row exists for the EntryId, and `Local.RemovedRowCount == Plan.RowsToRemove`.
- `A_campaign_erase_reports_and_produces_global_resurfacing`: a Global entry of the same name → `GlobalEntryResurfaces == true`. After apply, `GET /api/memory/lexicon/{name}?campaignId=` returns the Global entry.
- `A_labelled_entry_is_erased_under_the_exact_scope_write_lease` (Covenant on; label through the ledger) → `RemovedLabelCount == 1` and no `artifact_sensitivity` row for the entry.
- `A_pinned_or_retired_entry_is_erasable` → `Plan.Pinned == true` for the pinned entry, and 200 for both.
- `A_repeated_apply_replays`: `driver.EraseLexiconAsync(name, campaignId)`, then `driver.ApplyLexiconAsync(roundTrip.Apply with { PreflightToken = "x" })` → `Replayed == true` with the same `EffectDigest`.
- `Prepare_for_an_erased_entry_answers_410` → `MemoryErasure.SubjectErased`.

- [ ] **Step 2: Run the tests to verify they fail**

Add `LexiconService.Erasure.cs` with both verbs returning `MemoryErasure.Unavailable`, the constructor parameter, and no route. Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconErasureTests|FullyQualifiedName~LexiconErasureEndpointTests|FullyQualifiedName~MemoryErasureRouteInventoryTests|FullyQualifiedName~CovenantSensitivePurgeRouteInventoryTests"
```

Expected RED:
- The service tests fail with `MemoryErasure.Unavailable` where they expect a preflight.
- The endpoint tests get 404.
- The route inventory lacks the two declared names.
- The Lexicon read-source inventory lacks the two handlers.
- `Without_erasure_dependencies_the_verbs_are_unavailable` passes on arrival, and Step 5 does not rely on it.

- [ ] **Step 3: Implement**

- **Common checks.** Both verbs return `MemoryErasure.Unavailable` when `erasure` is null. They then run `Target.Validate()` (`Lexicon.InvalidCurationTarget`), the `LexiconDaemonStateNames.Is(Target.NormalizedName)` refusal, and `MemoryErasureProtocol.RequireInstalledAsync`.
- **Identifiers.** The request digest is `key.LexiconRequest(mutationId, target)`. The row id passed to the grammar is `Target.EntryId.ToString()`, and the runner key is `CovenantIdentitySql.Key(EntryId)`.
- **Transactions.** Prepare and apply use raw `BEGIN DEFERRED`/`BEGIN IMMEDIATE`, like every Lexicon write, so `ReadCurationStateAsync` and `TargetsEqual` run unchanged. The runner, evidence and label calls therefore pass `transaction: null`; Task 4 and Task 6 take `SqliteTransaction?`.
- **Prepare.** The order is a decision:
  1. Open the key for prepare before any transaction.
  2. One deferred read snapshot: read state (if absent, 410 when the subject is erased, otherwise `Lexicon.NotFound`); `TargetsEqual`; the plan (`RunAsync(Lexicon, key, Count)`, where the artifact is the entry row); labels; `GlobalEntryResurfaces = Scope.Kind == Campaign && EXISTS(SELECT 1 FROM lexicon_entries WHERE ScopeCampaignId IS NULL AND NameNormalized = $name)`; `ReadLexiconAsync(EntryId, NormalizedName, CampaignId)`; the retained copies.
  3. After the snapshot closes, and only when the target declares a label, a short `gate.AcquireReadAsync(owner)` captures `DatasetGeneration`.
  - Facts: `Versions = [AnnalHead.IsPresent ? Guid.Parse(AnnalHead.VersionId!) : null]`. `Flags` gets `Pinned` when the lifecycle is pinned and `GlobalEntryResurfaces` when that fact holds. The token has `ContentBinding = null`.
  - `Notes = MemoryErasureNotes.For(Lexicon, MemoryErasureIdentity.ForLexicon(target.Scope.CampaignId, target.NormalizedName).Scope, false)` (R18).
- **Apply.** The order is a decision:
  1. Open the key for apply, probe the receipt with no transaction open, and read the token.
  2. When the target declares a label: `AcquireWriteAsync(owner)`, `ForOrdinary`, the generation equality check, and `ValidateCurationLeaseAsync`.
  3. `BEGIN IMMEDIATE`:
     1. Re-probe, then the subject check (410).
     2. The FTS check: read `lexicon_fts_config` `secure-delete`. If it is not 1, run `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 1);` then `INSERT INTO lexicon_fts(lexicon_fts) VALUES('optimize');` and read it back. Still not 1 → verdict `FullTextSecureDeleteUnverified`.
     3. `ReadCurationStateAsync` with `TargetsEqual`, then the label proof.
     4. Re-measure and compare the effect (`StalePlan`).
     5. `RunAsync(Delete)`, then `DeleteExactAsync`.
     6. The fingerprint `ForLexicon(CampaignId, row.Name)`, computed from the row's `Name`, never from `NameNormalized`.
     7. The receipt; its mask adds the FTS reason when unverified.
     8. The absence proof, then `ValidateCurationLeaseAsync`, then `COMMIT`.
  4. `FinishAsync(…, MemoryErasureNotes.For(Lexicon, <same scope>, false), CancellationToken.None)`.
- **Endpoints.** Static `HandleLexiconErasePrepareAsync(ILexiconErasureService lexicon, HttpContext context)` and `HandleLexiconEraseAsync`. The parameter name `lexicon` puts their calls in the Roslyn inventory. Both routes carry `SensitivityRetentionPurge`, and neither carries conditional read or exact-write metadata.
- **Inventories.**
  - Acquisition rows `("…/Lexicon/LexiconService.Erasure.cs", "LexiconService", "PrepareAsync(2)" | "ApplyAsync(3)", ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)")`, calling the existing `LexiconService.OpenConnectionAsync(1)`. `ExpectedProductionAcquisitionCount` rises by 2.
  - `ContentFreeLogFiles`, and the benchmark file and both digests, as in Task 12.
- **API §8.33 (R27).**
  - Add rows for `/api/memory/lexicon/erase/prepare` (`LexiconErasePrepareRequest`: `target`, `mutationId` → `MemoryErasurePreflightDto`) and `/api/memory/lexicon/erase` (+ `preflightToken` → `MemoryErasureResultDto`), each noting `SensitivityRetentionPurge` and §8.35.
  - "All six static POSTs" becomes "The six curation POSTs".
  - Replace "`DELETE` remains exact-scope hard erasure, including retired rows" with: "`DELETE` remains an exact-scope delete, including retired rows. It records no erasure fingerprint, so extraction or an agent may write the name again; only `erase` (§8.35) suppresses and verifies."

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Lexicon|FullyQualifiedName~ArcanumInternalToolServerTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests"
```

Task 14 changes no hosted-producer call graph: neither `LexiconService` nor the tool server appears in the capsule TSV.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Drop the `optimize` on the flag-off path | `With_secure_delete_off_at_start…` (probe > 0) |
| Skip the read-back | the same test, through its `PendingReasons` assertion |
| Skip `InsertFingerprintAsync` | `An_entry_corrected_and_retired_before_version_thirteen…` (the scribe succeeds) and `A_scribe_of_an_erased_name_is_refused…` |
| Fingerprint a Campaign entry as Global | `A_scribe_of_an_erased_name_is_refused_in_that_scope_only` |
| Remove `TargetsEqual` | `A_curation_change_between_prepare_and_apply_is_a_stale_target` |
| Compute `GlobalEntryResurfaces` as always false | `A_campaign_erase_reports_and_produces_global_resurfacing` |
| Remove the `daemon_state` refusal | `A_daemon_state_entry_is_refused_by_name` |
| Remove the pre-transaction receipt probe | `A_repeated_apply_replays` (the `"x"` token is refused as `InvalidPreflight`) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Erasure.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Api/Tower/LexiconErasureEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: erase one exact Lexicon entry with a full-text verdict" -m "Prepare and apply routes bound to the curation target, refusing daemon_state entries and merging pre-v13 full-text residue before deletion; the Lexicon API section now says the legacy delete is unsuppressed." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: Covenant gate: entry-erasure exclusive operation

**Files:** (`T/` = `tests/RetroDownfall.Arcanum.Tests/`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationLeaseContracts.cs` (`CovenantLeaseKind.EntryErasure = 11`; `CovenantExclusiveOperation.CovenantEntryErasure = 9` and its "eight operations" remark at :221 becomes nine; `CovenantExclusiveRecoveryOwner` range at :291; new `CovenantEntryErasureLease`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantOperationGate.cs` (`AcquireEntryErasureAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs` (`AcquireEntryErasureAsync`; the `AcquireExclusiveAsync`, `ResumeOrAcquireExclusiveAsync` and `ResumeExclusiveAsync` deny-lists become one allow-list; `ClassifyOwner`)
- Test: `T/Covenant/CovenantOperationGateTests.cs`
- Modify (closed structural tests): `T/Covenant/CovenantArchitectureBoundaryTests.cs` (`The_installation_read_lease_is_the_sole_all_scopes_capability` admits `CovenantEntryErasureLease`), `T/Covenant/CovenantArtifactErasureAuthorityTests.cs` (`[InlineData(CovenantExclusiveOperation.CovenantEntryErasure)]` on `Exclusive_authority_rejects_every_scoped_operation_without_touching_a_lease`)
- Modify (every `ICovenantOperationGate` test implementation gains the method; each mirrors that class's own `AcquireCampaignExclusiveAsync` body):
  - `T/Covenant/IdentitySpellingContractTests.cs` `GrantingProtectedTransferGate` → `throw new InvalidOperationException("A selective import erases no Covenant entry.")`
  - `T/Covenant/CovenantErasureCoordinatorTests.cs` `RecordingGate` → delegates to `Inner`
  - `T/Covenant/CovenantContextProviderTests.cs` `UnreachableGate` → `throw new UnreachableException()`
  - `T/Api/Tower/LexiconCurationEndpointTests.cs` `Probe` → `throw new NotSupportedException()`
  - `T/Backup/CovenantRestoreStagingTests.cs` `RecordingExclusiveGate` → `throw new InvalidOperationException("A restore closes the installation, never one Covenant entry.")`
  - `T/Backup/BackupSessionImporterTests.cs` `ProtectedTransferGate` → `throw new InvalidOperationException("A selective import erases no Covenant entry.")`
  - `T/Backup/CovenantBackupDisclosureOrderingTests.cs` `RecordingGate` → `throw new InvalidOperationException("A backup must not erase a Covenant entry.")`
  - `T/Data/RecordingCovenantOperationGate.cs` → `Refuse<CovenantEntryErasureLease>("entry-erasure")`
  - `T/Data/Covenant/CovenantErasureSameProcessTests.cs` `RecordingRouteGate` → delegates to `inner`
- Inventories: none change. No file is added, so the benchmark catalog and digest are unchanged. `CovenantOperationGate` has no capsule rows and no new acquisition (delta 0). `CovenantPublicContractInventoryTests` stays green, because `CovenantEntryErasureLease` ends in neither `Dto` nor `Request`.

**Interfaces:**
- Consumes: nothing from Tasks 9–11 (the enum and lease are independent of the schema). The index's dependency on Task 9 is ordering only.
- Produces:
  - `CovenantExclusiveOperation.CovenantEntryErasure = 9`, `CovenantLeaseKind.EntryErasure = 11`.
  - `public sealed class CovenantEntryErasureLease(ICovenantExclusiveLeaseRegistration registration) : CovenantExclusiveOperationLease(registration), ICovenantSnapshotReadLease { public bool CoversInstallation => Snapshot.Coverage == CovenantLeaseCoverage.Installation; }`
  - `ValueTask<Result<CovenantEntryErasureLease>> AcquireEntryErasureAsync(CovenantOperationScope entryScope, bool reclaimsKey, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken)` on `ICovenantOperationGate`. Slot: a Campaign entry with `!reclaimsKey` takes a Campaign closure over `entryScope`; a Global entry, or any `reclaimsKey`, takes an Installation closure (`scope: null`). Task 16 calls it with `reclaimsKey` from the authenticated token, completes every disposition with `CancellationToken.None`, and never hands the lease to `CovenantProtectedJsonResult`.

- [ ] **Step 1: Write the failing tests** in `CovenantOperationGateTests` (`CovenantOperationGateFixture.CreateGate`, `Owner(...)`, `CampaignContext(...)`, `WaitForAsync`):
  - `Exclusive_operation_codes_are_immutable`: add `Assert.Equal((byte)9, (byte)CovenantExclusiveOperation.CovenantEntryErasure)`; count 9.
  - New `Lease_kind_codes_are_immutable`: `InstallationRead = 1 … Exclusive = 10`, `EntryErasure = 11`, count 11.
  - `Recovery_owner_requires_identity_operation_and_effect`: `(CovenantExclusiveOperation)10` and `(CovenantExclusiveOperation)0` throw `ArgumentOutOfRangeException`; code 9 constructs.
  - `Exclusive_operation_codes_are_bound_to_their_acquisition_shape`: an entry-erasure owner is refused with `Covenant.ForbiddenAuthority` by `AcquireExclusiveAsync`, `ResumeOrAcquireExclusiveAsync`, `ResumeExclusiveAsync`, `AcquireCampaignExclusiveAsync` and `AcquireProtectedTransferAsync`; each of codes 1–8 is refused by `AcquireEntryErasureAsync` (`[Theory]` over the eight).
  - `Campaign_entry_erasure_closes_its_Campaign_and_installation_coverage_only`:
    - Hold five leases: a Campaign One turn, an installation read, a Campaign Two turn, a Global turn (`CanonicalCampaignContext.GlobalOnly`) and a Global cleanup lease.
    - Start `AcquireEntryErasureAsync(ForCampaign(CampaignOne), reclaimsKey: false, Owner(CovenantEntryErasure))`. The first two leases see `Revocation.IsCancellationRequested`, the other three do not, and the close is not complete.
    - During the closure, `AcquireTurnAsync(CampaignOne)` and `AcquireAcceleratorAsync` answer `Covenant.Unavailable`, and a Campaign Two read succeeds.
    - Dispose the two revoked leases → success, with `Assert.False(lease.CoversInstallation)` and `Assert.Equal(CovenantLeaseKind.EntryErasure, lease.Snapshot.Kind)`.
  - `Global_entry_erasure_takes_the_installation_slot_and_drains_every_turn`: Campaign One, Campaign Two and Global turns are all revoked; after disposal → `CoversInstallation`; a Campaign Two read answers `Covenant.Unavailable`.
  - `A_reclaiming_Campaign_entry_erasure_takes_the_installation_slot`: `reclaimsKey: true` on Campaign One → `CoversInstallation`; a Campaign Two turn is drained.
  - `[Theory] (Deleted, false, LifecycleConflict) (Deleted, true, LifecycleConflict) (Unknown, false, NotFound) (Unknown, true, NotFound)` `Campaign_entry_erasure_refuses_a_deleted_or_unknown_Campaign` (via `FakeCovenantCampaignScopeProbe`).
  - `Entry_erasure_owner_is_never_adopted_durably`: `AdoptDurableRecoveryOwner(Owner(CovenantEntryErasure), scope: null, false)` and with `ForCampaign(CampaignOne)` both throw `ArgumentException`.
  - `An_undrained_turn_refuses_the_entry_erasure_and_reopens` (`drainTimeout: 150 ms`): `Covenant.MaintenanceFailed`; afterwards a Campaign One turn is admitted.
  - `[Theory] RollbackAndReopen | CommitAndReopen` `Completing_with_CancellationToken_None_after_the_request_was_cancelled_reopens_the_scope`: acquire with `cts.Token`, `cts.Cancel()`, `CompleteAsync(disposition, CancellationToken.None)` succeeds, a Campaign One read is admitted.
  - `Completing_with_the_cancelled_request_token_leaves_the_scope_closed`: `Assert.ThrowsAnyAsync<OperationCanceledException>` on `CompleteAsync(RollbackAndReopen, cts.Token)`, then a Campaign One read answers `Covenant.Unavailable` (pins why Task 16 must use `None`).

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantOperationGateTests"
```
Expected RED: the members and method do not exist; once they compile, the deny-lists admit code 9 on `AcquireExclusiveAsync` and `ClassifyOwner` adopts it as an Installation owner.

- [ ] **Step 3: Implement.**
  - **`AcquireEntryErasureAsync`**, in order:
    1. The owner code must be 9; otherwise `ForbiddenOperationShape()`.
    2. `!entryScope.IsInitialized` → `Covenant.InvalidScope`.
    3. A Campaign entry always passes `RequireLiveCampaignAsync`, in both slots.
    4. Call `AcquireExclusiveCoreAsync(ClosureSlot.Campaign, CovenantLeaseKind.EntryErasure, entryScope, …)` or `AcquireExclusiveCoreAsync(ClosureSlot.Installation, CovenantLeaseKind.EntryErasure, scope: null, …)`, creating `new CovenantEntryErasureLease(registration)`.
  - **Allow-list.** Replace the three deny-lists with `private static bool IsInstallationOperation(CovenantExclusiveOperation operation) => operation is SchemaRepair or BackupRestore or CovenantFamilyReinitialize or CovenantReset or HealthyCatalogFactoryErasure;`. The campaign-exclusive allow-list {1,2} and the protected-transfer check (== 3) stay.
  - **`ClassifyOwner`** gains a first arm: `case CovenantEntryErasure: throw new ArgumentException("An entry erasure is one atomic transaction and never has a durable recovery owner.", nameof(owner));`.
  - **Owner range:** `<= CovenantExclusiveOperation.CovenantEntryErasure`.
  - **Fakes:** as listed under Files.
  - **Architecture test:** add `typeof(CovenantEntryErasureLease)`, with a comment that it is a compound exclusive lease minted only by `AcquireEntryErasureAsync`.

- [ ] **Step 4: Run the tests to verify they pass**, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantOperationGate|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~CovenantArtifactErasureAuthorityTests|FullyQualifiedName~CovenantAuthorityTransitionPublisherTests|FullyQualifiedName~CovenantErasureCoordinatorTests|FullyQualifiedName~CovenantContextProviderTests|FullyQualifiedName~IdentitySpellingContractTests|FullyQualifiedName~LexiconCurationEndpointTests|FullyQualifiedName~CovenantRestoreStagingTests|FullyQualifiedName~BackupSessionImporterTests|FullyQualifiedName~CovenantBackupDisclosureOrderingTests|FullyQualifiedName~CovenantErasureSameProcessTests|FullyQualifiedName~CovenantPublicContractInventoryTests"
```

- [ ] **Step 5: Mutation check**
  - Map a Campaign entry with `reclaimsKey: true` to the Campaign slot → `A_reclaiming_Campaign_entry_erasure_takes_the_installation_slot` RED.
  - Map a Global entry to `ClosureSlot.GlobalScope` → `Global_entry_erasure_…_drains_every_turn` RED (Campaign turns not revoked).
  - Revert one allow-list to the old deny-list → `Exclusive_operation_codes_are_bound_to_their_acquisition_shape` RED.
  - Delete the `ClassifyOwner` arm → `Entry_erasure_owner_is_never_adopted_durably` RED.
  - Skip `RequireLiveCampaignAsync` on the Installation slot → the `reclaimsKey: true` rows of the refusal theory RED.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationLeaseContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/ICovenantOperationGate.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs tests/RetroDownfall.Arcanum.Tests/Covenant/{CovenantOperationGateTests,CovenantArchitectureBoundaryTests,CovenantArtifactErasureAuthorityTests,IdentitySpellingContractTests,CovenantErasureCoordinatorTests,CovenantContextProviderTests}.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/LexiconCurationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/{CovenantRestoreStagingTests,BackupSessionImporterTests,CovenantBackupDisclosureOrderingTests}.cs tests/RetroDownfall.Arcanum.Tests/Data/RecordingCovenantOperationGate.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs
git commit -m "feat: add the Covenant entry-erasure exclusive operation" -m "A dedicated gate acquisition maps a Campaign entry to its Campaign closure and a Global or key-reclaiming entry to the installation closure; no other acquisition shape or durable adoption admits the new code." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Covenant entry erasure: plan, service and routes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs` (`CovenantEntryErasurePlan`, `CovenantEntryErasureMode`, `CovenantEntryErasureSubject`, `CovenantEntryErasureTally`). This is the only Task 16 file that names a Covenant table.
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs` (`CovenantEntryErasureService`, `ICovenantEntryErasurePreparer`, `CovenantEntryErasurePrepared`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (in `AddArcanumInfrastructure`, after `services.AddCovenantPersistence();` at :1036: one scoped `CovenantEntryErasureService` by explicit factory, forwarded to `ICovenantEntryErasureService` and `ICovenantEntryErasurePreparer`; the host container only, because `AddCovenantPersistence` also runs for the CLI at :302)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs`
- Modify: `docs/Arcanum.API.md` (two §1 rows after `/api/memory/covenant/review/apply` at :121; the §8.28 "Registered, mutation" list at :718 and its contract table get both erase rows; the §8.35 Covenant subsection)
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantEntryErasurePlanTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantEntryErasureServiceTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/Tower/CovenantErasureEndpointTests.cs`
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs` (`Routes` +2, `LifecycleManage`)
  - `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs` (`Only_the_outbox_worker_and_rebuilder_write_accelerator_state` admits `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs` in ordinal position, with a comment, R22)
  - `tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs` (the owner list admits `CovenantEntryErasurePlan.cs`, which stamps `covenant_state.UpdatedAtUtc`)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (two pins Task 9 deferred here; `ContentFreeLogFiles` + both new files)
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (one row) and `GrimoireConnectionAcquisitionInventoryTests.cs` (`FreshMigrationMembers` entry; `ExpectedProductionAcquisitionCount` **+1** over the value Task 14 committed, because Task 15 adds none)
  - the benchmark catalog (+2 Infrastructure `.cs` files), the workload digest, and `AdmissionBenchmarkManifest.cs:19`
- Not touched here (R1, R4): `CovenantErasureContracts.cs`, `IMemoryErasureServices.cs`, `CovenantPublicContractInventory.cs`, `CovenantErasurePreflightBodyTests.cs` and `ArcanumJsonContext.cs`. Task 1 owns them.

**Interfaces:**
- Consumes:
  - Task 1: `ICovenantEntryErasureService` (Prepare and Apply only); `CovenantErasePrepareRequest`; `CovenantEraseRequest` with `ToPrepareRequest()`; `CovenantEraseHeadExpectation`; `CovenantErasurePreflightBody`, which has `CovenantDigest EffectDigest`, `ulong` epochs as `CovenantOperatorPreflightBody` does, `Encode()` and `TryDecode` (R15); `MemoryErasureIdentity.ForCovenant`; `MemoryErasureKey.CovenantRequest`; `CovenantErasureEffectFacts`; `CovenantErasurePlanFacts`; the JSON registrations.
  - Task 6: the evidence methods.
  - Task 9: `CovenantSqliteAuthorizationKind.CovenantEntryErasure`, `covenant_mutation_receipts.EntryId`, `covenant_key_epochs.IncarnationEpoch`, `CovenantCanonicalSchemaVersionFiveFixture`.
  - Task 10: the binding-epoch join `COALESCE(k.IncarnationEpoch, 0)`.
  - Task 11: the staging refusal and `CovenantAgentErasureGate.OperatorManagedRefusal`.
  - Task 12: everything it produces.
  - Task 13: `MemoryErasureEndpoints.cs`, `Routes`, and `MemoryErasureRouteDriver.{Host, SetCovenantAsync, EraseCovenantAsync, ApplyCovenantAsync, PostAsync}`.
  - Task 15: `ICovenantOperationGate.AcquireEntryErasureAsync`, `CovenantEntryErasureLease.CoversInstallation`, `CovenantOperationGateFixture.CreateGate(drainTimeout: …)`, and `RecordingCovenantOperationGate`'s entry-erasure recording.
- Produces:
  - `CovenantEntryErasurePlan.RunAsync(SqliteConnection, SqliteTransaction, CovenantEntryErasureSubject, CovenantArtifactPlanMode, CovenantEntryErasureMode, CancellationToken) → Task<CovenantEntryErasureTally>`.
  - `CovenantEntryErasurePlan.ReadSubjectAsync(SqliteConnection, SqliteTransaction, Guid entryId, CancellationToken) → Task<CovenantEntryErasureSubject?>`. It computes `IsMasked` (the Campaign Confirmed curation head at the binding epoch is masked) and `ReclaimsKey` (no other `covenant_entries` or `covenant_heads` row in any scope names the key).
  - `CovenantEntryErasurePlan.ProveAbsentAsync(SqliteConnection, SqliteTransaction, CovenantEntryErasureSubject, IReadOnlyList<Guid> versionIds, CancellationToken) → Task<IReadOnlyList<MemoryErasureTableCount>>` (only the non-zero remainders).
  - `internal sealed record CovenantEntryErasureTally(IReadOnlyList<MemoryErasureTableCount> Targets, IReadOnlyList<Guid> VersionIds, int ConfirmedVersions, int ProposedVersions, bool FullTextSecureDeleteVerified, bool RetainsCampaignMask, bool KeyReclaimed)`.
    - **Task 24 consumes four fields (M9, N1):**
      - `VersionIds`, which it passes to `ProveAbsentAsync` for its post-condition;
      - `FullTextSecureDeleteVerified`, for its scrub status;
      - `KeyReclaimed`, which must equal `subject.ReclaimsKey`;
      - `Targets`, whose `covenant_entries` row count feeds `BackupRestoreErasureApplication.CovenantEntriesRemoved`.
    - Task 24 also consumes `ReadSubjectAsync`, called immediately before each staged entry's `RunAsync(Delete, Staged)`.
    - The other three fields (`ConfirmedVersions`, `ProposedVersions`, `RetainsCampaignMask`) serve only Task 16's plan facts and receipt mask.
  - `internal sealed class CovenantEntryErasureService : ICovenantEntryErasureService, ICovenantEntryErasurePreparer`, with `PrepareAsync` and `ApplyAsync` only (R2). Its test seams:
    - `internal Func<SqliteTransaction, CancellationToken, Task>? CommitForTesting { get; init; }` replaces `transaction.CommitAsync` when set;
    - `internal Func<Guid, CancellationToken, Task<Result<bool>>>? ReceiptReReadForTesting { get; init; }` replaces the fresh-connection receipt re-read when set.
  - `internal interface ICovenantEntryErasurePreparer { Task<Result<CovenantEntryErasurePrepared>> PrepareHeldAsync(CovenantErasePrepareRequest, OperatorAuthorityContext, CancellationToken); }` with `internal sealed record CovenantEntryErasurePrepared(MemoryErasurePreflightDto Preflight, ICovenantSnapshotReadLease ReadLease)`. The lease is typed as the interface so the endpoint test can hand a recording lease through a fake preparer.
  - Routes `PrepareCovenantErasure` (`POST /api/memory/covenant/erase/prepare`) and `EraseCovenantEntry` (`POST /api/memory/covenant/erase`), both `LifecycleManage`.

- [ ] **Step 1: Write the failing tests**

`CovenantEntryErasurePlanTests` (`CovenantCanonicalFixture` at canonical v6; rows through the Covenant kernel and `CovenantServiceHarness`; raw SQL only for assertions, except the one fault injection named below):
- `[Theory(Live, Staged)] Count_measures_exactly_what_Delete_removes`. The entry has two Confirmed versions, one Proposed version, a provenance leaf, receipts, a pending outbox row, a search document, and curation rows in both lanes.
  - Count and Delete report equal `Targets`, in the order `covenant_search_documents, covenant_search_outbox, covenant_mutation_receipts, covenant_version_attachment_provenance, covenant_heads, covenant_versions, covenant_review_events, covenant_review_decision_receipts, covenant_entries, covenant_curation_heads, covenant_curation_versions, covenant_curation_receipts`, plus `covenant_key_epochs` when the key is reclaimed.
  - `ProveAbsentAsync(subject, tally.VersionIds)` is empty after Delete.
- `Live_mode_appends_one_absent_delta_per_erased_head_and_advances_the_search_sequence_once`: two rows with `DesiredVersionId IS NULL` at the old `CanonicalSearchSequence + 1`, the sequence up by exactly 1, and no pending row naming an erased version. `Staged` appends none and leaves the sequence unchanged.
- `A_legacy_NoChange_receipt_in_the_entry_window_is_deleted_and_counted`: seeded by raw insert on a `CovenantCanonicalSchemaVersionFiveFixture` catalog evolved to v6 (the only shape head writers cannot produce), then counted in the `covenant_mutation_receipts` target and gone after Delete.
- `A_masked_campaign_confirmed_subject_is_retained_when_the_key_is_not_reclaimed`: a Global entry exists for the key, so the erase does not reclaim. `RetainsCampaignMask == true`; the masked head survives at the binding epoch; the Campaign pins are gone. The key row was created after v6, so `IncarnationEpoch = 0` while `KeyEpoch > 0`.
- `Reclamation_purges_curation_in_every_scope_deletes_the_key_row_and_advances_the_epoch`: the key's only entry, plus a keyless Global pin and a Campaign B mask. `KeyReclaimed == true`; zero curation rows for the key anywhere; no `covenant_key_epochs` row; `KeyReclamationEpoch` up by exactly 1.
- `Search_documents_are_deleted_even_when_covenant_fts_secure_delete_is_unverifiable`: with `INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 0)`, zero `covenant_search_documents` rows remain for the entry and `FullTextSecureDeleteVerified == false`.
- `Absence_proof_reports_any_remaining_target`: after Delete, one fault-injected raw insert of a `covenant_search_outbox` row whose `DesiredVersionId` is an erased version (the outbox has only a delete guard) makes `ProveAbsentAsync` return exactly `[("covenant_search_outbox", 1)]`.
- `ReadSubject_recomputes_reclamation_per_entry`: two Campaign entries share a key. `ReadSubjectAsync` gives `ReclaimsKey == false` for the first; after the first is deleted in `Staged` mode, it gives `true` for the second. This is the sequence Task 24 relies on.

`CovenantEntryErasureServiceTests`. They use the real `CovenantOperationGate` from `CovenantOperationGateFixture.CreateGate(drainTimeout: TimeSpan.FromMilliseconds(150))` over a fixture, unless a test names `RecordingCovenantOperationGate`. Every refused case also asserts the entry and heads intact and zero receipt and fingerprint rows.
- `Replay_is_receipt_first_and_never_closes_a_scope`: commit once with the real gate, then apply the same request with `PreflightToken = "x"` on a service over `RecordingCovenantOperationGate` → `Replayed == true`; the recorder shows exactly one read acquisition on the entry scope, no entry-erasure acquisition, and `LiveLeases == 0`.
- `The_probe_read_lease_is_released_before_the_scope_closes`: apply succeeds (`Replayed == false`, `Local.ErasedItemCount == 1`) under the 150 ms drain bound. A probe lease held into the closure would be drained against itself, and the erase would fail with `Covenant.MaintenanceFailed`.
- `An_undrained_turn_fails_the_erase_and_changes_nothing`: hold a Campaign turn → `Covenant.MaintenanceFailed`, the entry intact, and a later `AcquireReadAsync(entryScope)` admitted, so the scope reopened.
- `Authority_changed_during_the_drain_refuses_with_rollback_and_reopen`: issue the context, hold a Campaign turn, start apply, call `FakeCovenantAuthorityProvider.Advance()`, release the turn → `Covenant.StaleSnapshot`; the entry intact; `AcquireReadAsync(entryScope)` admitted afterwards.
- `A_campaign_deleted_during_the_drain_is_refused_inside_the_transaction` → `Covenant.StaleSnapshot`, the entry intact, the scope reopened.
- `Agent_publication_during_the_drain_makes_the_erase_a_revision_conflict`: an agent proposal commits in another Campaign-scope turn before the drain completes → `Covenant.RevisionConflict`, and the new Proposed head intact.
- `Reclamation_is_refused_when_the_held_lease_does_not_cover_the_installation`: a gate stub returns a Campaign-covered lease for `reclaimsKey: true` → `Covenant.ForbiddenAuthority`, and the stub records `RollbackAndReopen`.
- Uncertain commits, through the two seams:
  - `An_uncertain_commit_with_a_receipt_reopens_committed`: `CommitForTesting` commits and then throws → the result succeeds, the stub records `CommitAndReopen`, and the receipt exists.
  - `An_uncertain_commit_without_a_receipt_reopens_rolled_back`: `CommitForTesting` throws without committing → the retryable `Covenant.MaintenanceFailed` (503), the stub records `RollbackAndReopen`, and the entry is intact.
  - `An_uncertain_commit_whose_re_read_fails_stays_closed_and_reports_manual_recovery`: `CommitForTesting` throws and `ReceiptReReadForTesting` returns a failure → `Covenant.ManualRecoveryRequired`, and the stub records `KeepClosed`.
- `A_cancelled_request_after_commit_still_reopens_the_scope`: `CommitForTesting` commits and then cancels the request's `CancellationTokenSource` → the result succeeds with `Replayed == false`; the receipt exists; the entry is absent; `AcquireReadAsync(entryScope, CancellationToken.None)` is admitted.
- `The_wal_checkpoint_runs_only_after_the_closure_reopens`: one ordered recorder shared by the gate stub and the fresh factory → `["complete:CommitAndReopen", "fresh:ReadWrite"]`.
- `The_preparer_holds_an_installation_read_lease_taken_after_the_key`: `PrepareHeldAsync` returns a lease with `Snapshot.Kind == CovenantLeaseKind.InstallationRead`, and the counting credential store saw its read before the gate saw the acquisition.

`MemoryErasureStructuralTests` (the two pins Task 9's notes deferred to this task):
- `Only_the_entry_erasure_service_names_its_authorization_kind`: outside `Data/Covenant/CovenantSqliteAuthorizationKind.cs` and `Data/Covenant/CovenantSqliteConnectionInitializer.cs`, the member access `CovenantSqliteAuthorizationKind.CovenantEntryErasure` appears only in `Covenant/CovenantEntryErasureService.cs`.
- `The_entry_erasure_service_completes_every_closure_with_CancellationToken_None`: every `CompleteAsync(` invocation in `CovenantEntryErasureService.cs` passes `CancellationToken.None` as its last argument, and there is at least one.

`CovenantErasureEndpointTests` (`[Collection("ApiHost")]`; `MemoryErasureRouteDriver.Host(credentials, covenant: true)`; entries through `driver.SetCovenantAsync`; the target from `POST /api/memory/covenant/detail`):

```csharp
[SkippableFact]
public async Task An_erase_waits_for_an_in_flight_turn_and_a_later_agent_proposal_is_refused_at_staging()
{
    await using CovenantTurnLease turn = (await gate.AcquireTurnAsync(campaignContext, ct)).Value;
    MemoryErasurePreflightDto preflight = await PrepareAsync(client, detail, mutationId);
    Assert.Contains(MemoryErasureNote.CovenantDrainsInFlightTurns, preflight.Notes);
    Task<HttpResponseMessage> erase = client.PostAsJsonAsync("/api/memory/covenant/erase", Apply(detail, mutationId, preflight.PreflightToken));
    await Task.Delay(TimeSpan.FromMilliseconds(500));
    Assert.False(erase.IsCompleted);                                 // draining the covered turn
    await turn.DisposeAsync();
    Assert.Equal(HttpStatusCode.OK, (await erase).StatusCode);
    // detail → 404 Covenant.NotFound; propose_covenant for the key in that Campaign (staged as CovenantMutationToolTests does)
    // → exactly CovenantAgentErasureGate.OperatorManagedRefusal, and no Proposed version row for the key.
}
```

- `Prepare_holds_its_installation_read_lease_through_the_protected_response`, mirroring `DataRetentionEndpointTests.Covenant_reset_and_factory_plans_hold_their_read_lease_through_the_protected_response`: a fake `ICovenantEntryErasurePreparer` in `ServiceOverrides` returns a recording `ICovenantSnapshotReadLease` → 200, the protected tuple, `Revalidations == 1` and `Disposals == 1` once the response completes.
- `Apply_holds_no_lease_after_the_response_and_emits_the_protected_tuple_on_success_and_refusal`: a successful apply → 200 with the protected tuple, after which the host gate's `AcquireInstallationReadAsync` succeeds (no closure left) and is disposed. A second apply with a stale head expectation → 409 `Covenant.RevisionConflict` with the protected tuple, after which `AcquireInstallationReadAsync` succeeds again.
- `The_preflight_reports_versions_curation_outbox_and_scope_facts`: exact `CovenantErasurePlanFacts` for a Campaign entry with a Global twin key, including `AffectedCampaigns == 1`, `GlobalConfirmedResurfaces == true` and `ReclaimsKey == false`; channels `[NotRecorded, NotRecorded, NotApplicable, NotRecorded, NotRecorded]` for an operator entry; `Notes == [OtherScopesUnaffected, CovenantDrainsInFlightTurns]`.
- `A_head_moved_after_prepare_is_a_revision_conflict` → 409 `Covenant.RevisionConflict`.
- `A_global_entry_with_a_proposed_expectation_is_an_invalid_scope` → 400 `Covenant.InvalidScope`.
- `A_reclaiming_erase_makes_an_outstanding_set_preflight_stale`: prepare a `set` on another key, then `driver.EraseCovenantAsync` a key whose only entry it is (`Preflight.Plan.Covenant!.ReclaimsKey == true`), then commit the earlier `set` → 409 `Covenant.StaleSnapshot`.
- `A_repeated_apply_replays`: `driver.ApplyCovenantAsync(roundTrip.Apply with { PreflightToken = "x" })` → `Replayed == true`, the same `EffectDigest`.
- `A_pin_recorded_between_prepare_and_apply_is_a_stale_plan` (in `CovenantErasureEndpointTests`): prepare the erase of a Campaign entry; then pin its Confirmed lane through `POST /api/memory/covenant/curate/prepare` and `/curate`; apply answers 409 `MemoryErasure.StalePlan`, the entry is intact, and no receipt exists. Curation moves no head, key epoch or dataset generation, so only the effect recompute can refuse it.
- `An_erase_below_canonical_six_is_unavailable`: a **service-level** test (a host installs to head at startup, R33) over a scratch Grimoire installed with `CovenantCanonicalSchemaVersionFiveFixture.ChainSet()` → `PrepareAsync` returns `MemoryErasure.Unavailable` and no key is created (N10).

- [ ] **Step 2: Run the tests to verify they fail**

Create the plan, service and preparer with every method throwing `NotImplementedException`, register them, and map no route. Then run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantEntryErasurePlanTests|FullyQualifiedName~CovenantEntryErasureServiceTests|FullyQualifiedName~CovenantErasureEndpointTests|FullyQualifiedName~MemoryErasureRouteInventoryTests|FullyQualifiedName~MemoryErasureStructuralTests"
```

Expected RED:
- The plan and service tests fail on `NotImplementedException`.
- The endpoint tests get 404.
- The route inventory lacks the two rows.
- `Only_the_entry_erasure_service_names_its_authorization_kind` and the `CancellationToken.None` pin fail, because the stub names neither.

- [ ] **Step 3: Implement**

- **Plan.**
  - The plan runs inside the caller's transaction. Id predicates use `CovenantIdentitySql.Keyed`.
  - Capture `VersionIds` and the heads' `SearchRowId`s before anything is deleted: once the versions are gone, their predicates match nothing.
  - The delete order is the spec §9.3 order, and it is the order of `Targets` above:
    1. `covenant_search_documents` by `EntryId`, whenever the table exists. `covenant_fts` secure-delete is only read, to set `FullTextSecureDeleteVerified`.
    2. The pending outbox rows by `SearchRowId`. In Live mode, then insert one `DesiredVersionId NULL` delta per head at `CanonicalSearchSequence + 1`, and advance the sequence once, stamping `UpdatedAtUtc = UtcInstantText.Format(DateTimeOffset.UtcNow)`, as `CovenantCleanupWorker.AdvanceSearchSequenceAsync` does.
    3. The receipts:

```sql
DELETE FROM covenant_mutation_receipts
 WHERE <Keyed EntryId>
    OR ResultingVersionId IN (SELECT VersionId FROM covenant_versions WHERE <Keyed EntryId>)
    OR (EntryId IS NULL AND OutcomeCode = 2 AND ScopeCode = $scope AND CampaignId IS $campaign
        AND LaneCode IN (SELECT LaneCode FROM covenant_heads WHERE <Keyed EntryId>) AND CommittedAtUtc >= $entryCreatedAtUtc);
```

    4. Provenance, then `covenant_heads`, then `covenant_versions` in one statement (review events and decision receipts cascade), then `covenant_entries`.
    5. Curation: the subject's heads, then versions, then receipts in every epoch. When `subject.IsMasked && !subject.ReclaimsKey`, keep `(LaneCode = 1, KeyEpoch = COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key), 0), IsMasked = 1)` and its chain.
    6. Reclamation, when `ReclaimsKey`: delete every curation row for the key in every scope, delete the `covenant_key_epochs` row, then `UPDATE covenant_state SET KeyReclamationEpoch = KeyReclamationEpoch + 1, UpdatedAtUtc = $now WHERE StateKey = 1 AND KeyReclamationEpoch = $read`, requiring 1 row. `$read` is read inside the transaction in both modes.
  - Count mode applies the same predicates with no side effects.
  - Staged mode differs from Live in three ways: no absent deltas, no sequence advance, and no live-Campaign check.
  - `ProveAbsentAsync` counts by `EntryId`; by the captured version ids (provenance, receipts, and outbox rows whose `DesiredVersionId` is among them, which excludes this erase's own absent deltas); by the legacy NoChange predicate; by the non-retained curation subject; and by the key row when it was reclaimed.
- **Prepare.** The order is a decision (§9.1):
  1. Normalize the key with `new CovenantKey(Key).Value`. A Global scope with `Proposed` is `Covenant.InvalidScope`.
  2. `RequireInstalledAsync`. The recorded canonical tier (`SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 1`) must be at least 6, otherwise `MemoryErasure.Unavailable`.
  3. `OpenKeyForPrepareAsync(…, Covenant)`. Only then `gate.AcquireInstallationReadAsync`.
  4. One deferred read transaction on the lease-admitted `ICovenantConnectionSource` connection:
     - the entry and heads must equal the request's `EntryId` and heads (`Covenant.RevisionConflict`); an absent entry is 410 when the subject is erased, otherwise `Covenant.NotFound`;
     - then the subject, the Count plan (`Live`), the dataset generation, the key epoch and the reclamation epoch.
  - Every Global-scope fact is read with raw SQL inside this transaction, never through a lease-validated store port. A Campaign-slot erase covers only its own Campaign.
  - `IsPinned`: any curation head for the subject at the binding epoch has `IsPinned = 1`.
  - `GlobalConfirmedResurfaces`: a Campaign entry with a live Confirmed head (`CurrentOperationCode = 1`), a live Global Confirmed head for the key, and no retained mask.
  - `AffectedCampaigns`: 1 for a Campaign entry; `SELECT count(*) FROM "Campaigns"` for a Global one.
  - Exposure through `ReadCovenantAsync`, and the retained copies.
  - Effect facts: `RowIds = [EntryId.ToString()]`, `Versions = [Confirmed?.VersionId]`, flags from the facts, and `Covenant = new(DatasetGeneration, checked((long)keyEpoch), checked((long)reclamationEpoch))` (R15).
  - The body carries `EffectDigest = new CovenantDigest(key.Effect(facts))` and the context's `AuthorityEpoch` as `checked((ulong)…)`. It is encoded through `envelopes.Encode(CovenantEnvelopePurpose.OperatorPreflight, body.Encode(), MemoryReviewLimits.TokenLifetime, issuedAt)`, with `issuedAt` from the service's `TimeProvider`.
  - `Notes = MemoryErasureNotes.For(Covenant, MemoryErasureIdentity.ForCovenant(Scope, CampaignId, key).Scope, subject.ReclaimsKey)`.
  - `PrepareHeldAsync` returns the lease; `PrepareAsync` disposes it.
- **Apply.** The order is a decision (§6.2, §9.2):
  - Validate → `OpenKeyForApplyAsync(…, Covenant)` → the request digest from `request.ToPrepareRequest()`.
  - `ProbeReceiptAsync` under a short `gate.AcquireReadAsync(entryScope)`, disposed before the closure. A replay goes to `FinishAsync` and never closes a scope.
  - `CovenantErasurePreflightBody.TryDecode`. Digest, times, `EntryId` and head ids must match, otherwise `InvalidPreflight`. `body.OperatorAuthorityEpoch != checked((ulong)context.AuthorityEpoch)` is `Covenant.StaleSnapshot`.
  - `AcquireEntryErasureAsync(entryScope, body.ReclaimsKey, new CovenantExclusiveRecoveryOwner(MutationId, CovenantExclusiveOperation.CovenantEntryErasure, body.EffectDigest), ct)`. `body.EffectDigest` is used directly, as a `CovenantDigest` (R15). A gate refusal returns the gate's own error, and no disposition runs.
  - From here, `try`/`finally` always runs `lease.CompleteAsync(disposition, CancellationToken.None)`:
    - the lease's `RuntimeAuthorityGeneration` and `AuthorityEpoch` must equal the context's (`Covenant.StaleSnapshot`), and its `DatasetGeneration` the body's (`StalePlan`);
    - `SqliteBusyRetry`, `BEGIN IMMEDIATE` and `initializer.Authorize(connection, CovenantSqliteAuthorizationKind.CovenantEntryErasure)`;
    - re-probe → 410 → verify the generation, reclamation epoch, key epoch, heads (`RevisionConflict`) and the live `Campaigns` row (`StaleSnapshot`);
    - read the subject: `ReclaimsKey != body.ReclaimsKey` is `StalePlan`, and `subject.ReclaimsKey && !lease.CoversInstallation` is `Covenant.ForbiddenAuthority`;
    - recompute the effect (`StalePlan`);
    - `RunAsync(Delete, Live)` → `InsertFingerprintAsync(key.Fingerprint(ForCovenant(scope, campaign, key)), Covenant, key.KeyId)` → the receipt, whose mask is `WalCheckpointPending` plus `FullTextSecureDeleteUnverified` when `covenant_fts` exists and `!FullTextSecureDeleteVerified`;
    - `ProveAbsentAsync` must be empty, otherwise `ErasureIncomplete`;
    - `lease.RevalidateAsync` → `CommitForTesting ?? transaction.CommitAsync`.
  - A committed erase appends absent deltas and moves the canonical search sequence, so a `CommitAndReopen` disposition republishes `CanonicalMutation` after the erase's own `COMMIT`: `availabilityRepublisher.RepublishAsync(connection, CovenantHealthTransition.CanonicalMutation)` on the committing connection, while the lease still holds the closure and before `CompleteAsync` reopens it. A refusal or rollback republishes nothing. The dispositions:
    - success → `CommitAndReopen`;
    - proven refusal → `RollbackAndReopen`;
    - a commit exception → dispose the transaction, then run `ReceiptReReadForTesting ?? ReadReceiptOnFreshConnectionAsync(mutationId, CancellationToken.None)`. That method opens `_freshConnections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken)`. Present → `CommitAndReopen` and the committed result; absent → `RollbackAndReopen` and the retryable `Covenant.MaintenanceFailed`, since nothing changed; a failed re-read → `KeepClosed` and `Covenant.ManualRecoveryRequired`.
  - After `CompleteAsync`: `FinishAsync(…, MemoryErasureNotes.For(Covenant, <request scope>, false), CancellationToken.None)`. A replay cannot recover `ReclaimsKey`, and the flag does not change the note set.
- **Endpoints.** Prepare wraps `PrepareHeldAsync` in `new CovenantProtectedJsonResult<MemoryErasurePreflightDto>(prepared.ReadLease, …, ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto)`, with the owned-lease `finally` of `CovenantMutationEndpoints.PrepareAsync`. Apply returns plain `Results.Json`; the authority middleware marks it protected. Both routes carry `LifecycleManage`.
- **Inventories.**
  - The acquisition row `("src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs", "CovenantEntryErasureService", "ReadReceiptOnFreshConnectionAsync(2)", MarkedRouteInvocation, "OpenFreshAsync", 2, "_freshConnections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly,cancellationToken)")`, its `FreshMigrationMembers` entry, and the count + 1. `ICovenantConnectionSource` calls are not inventoried.
  - The accelerator-writer comment: "A selective entry erasure deletes the entry's search documents and appends content-free absent deltas in the same transaction, under an entry-erasure closure during which the accelerator lease is refused, so the applied tuple never claims a document this file removed."
  - The UTC owner row; `ContentFreeLogFiles`; the benchmark files and both digests.
- **API.** The §1 rows and the §8.28 lists. §8.35's Covenant subsection states:
  - the drain and its 30-second bound, with 503 on no drain;
  - that turns starting during the closure run without Covenant content;
  - that installation-coverage leases fail fast during any closure;
  - the three dispositions;
  - that a reclaiming erase (`ReclaimsKey`) makes every outstanding Covenant preflight stale installation-wide.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Covenant|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
```

The capsule test must stay green unchanged: the erase is request-driven and adds no hosted-producer call graph.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Hold the probe read lease into `AcquireEntryErasureAsync` | `The_probe_read_lease_is_released_before_the_scope_closes` (`Covenant.MaintenanceFailed` at the 150 ms bound) |
| Always pass `reclaimsKey: false` to the gate | `A_reclaiming_erase_makes_an_outstanding_set_preflight_stale`, where the Campaign-covered lease hits the coverage refusal |
| Remove the coverage refusal | `Reclamation_is_refused_when_the_held_lease_does_not_cover_the_installation` |
| Skip the reclamation-epoch bump | `A_reclaiming_erase_makes_an_outstanding_set_preflight_stale` |
| Delete the retained mask | `A_masked_campaign_confirmed_subject_is_retained…` |
| Bind the retained mask to `KeyEpoch` instead of `IncarnationEpoch` | `A_masked_campaign_confirmed_subject_is_retained…` |
| Gate search-document deletion on the FTS verdict | `Search_documents_are_deleted_even_when…` |
| Skip the absent deltas | `Live_mode_appends_one_absent_delta…` |
| Treat a failed receipt re-read as rollback | `An_uncertain_commit_whose_re_read_fails_stays_closed…` |
| Complete with the request token | `A_cancelled_request_after_commit_still_reopens_the_scope` and `The_entry_erasure_service_completes_every_closure_with_CancellationToken_None` |
| Skip the post-drain authority comparison | `Authority_changed_during_the_drain_refuses_with_rollback_and_reopen` |
| Name `CovenantSqliteAuthorizationKind.CovenantEntryErasure` in the plan file too | `Only_the_entry_erasure_service_names_its_authorization_kind` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantEntryErasurePlanTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantEntryErasureServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/CovenantErasureEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: erase one whole Covenant entry under a drained closure" -m "Both lanes, every version, receipts, outbox, search documents and curation, with key reclamation, receipt-first replay, and fresh-connection resolution of an uncertain commit." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 17: Release verbs and operator re-creation release

**Files:**
- Create (Core): `src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureRelease.cs` (the release port, R2)
- Modify (Core): `src/RetroDownfall.Arcanum.Core/Covenant/CovenantErasureContracts.cs` (add `CovenantErasureReleaseRequest` with `Validate()`); `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs` (`Ports`: `IMemoryErasureRelease`; `Contracts`: `CovenantErasureReleaseRequest`)
- Create (Infrastructure): `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureRelease.cs` (implements `IMemoryErasureRelease`); `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureFingerprintRelease.cs` (the static helper, and the only caller of `MemoryErasureEvidence.DeleteFingerprintAsync`)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs` (three release routes); `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs` (`CovenantErasureReleaseRequest` only; every other release type is registered by Task 1)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (`AddArcanumInfrastructure`: scoped `IMemoryErasureRelease`, beside the erase services)
- Modify (Covenant re-creation): `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationWireContracts.cs` (`CovenantMutationEffectDto`, `CovenantMutationResultDto`); `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs` (`PrepareAsync`, `EffectDto`, `CommitAsync`)
- Modify (Saga re-creation): `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs` (`SagaCurationOutcome`, `SagaCurationResult`); `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs` (`CorrectAsync`); `src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs` (`FinishAsync`); `src/RetroDownfall.Arcanum.Core/Memory/MemoryReviewContracts.cs` (`MemoryReviewBulkItemResultDto`); `src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs` (primary constructor, `ApplyAsync`, the `Correct` arm)
- Modify (CLI): `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs` (`CovenantMutationPlanPayload`, `CovenantMutationResultPayload`); `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs` (`ConfirmAsync`, `WritePlan`, `WriteMutation`); `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.SagaCuration.cs` (`WriteCurationResult`)
- Modify (docs): `docs/Arcanum.API.md` (§1 rows for the three routes; §8.28 set effect and result fields; §8.30 correction field; §8.34 item field; §8.35 "Release"); `docs/Arcanum.Command.Reference.md` (the `memory covenant set` and `memory saga correct` rows)
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt` (+3 `src` files), `grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`), `AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, :19) (R20)
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (+2 rows) and `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+2** over the value Task 16 left) (R21)
- Test (create): `tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureReleaseEndpointTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureOperatorRecreationTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureReleaseEndToEndTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureReleaseServiceTests.cs`
- Test (modify):
  - `tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs` (three rows)
  - `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs` (one row on Task 1's theory)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs` (`AllowedCallers`, R7)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (R8: the "no agent tool references the erase or release services" pin, and `ContentFreeLogFiles` +2 files)
  - `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs` (exactly the release trio Task 13 reserves for this task, R6)
  - `tests/RetroDownfall.Arcanum.Tests/Memory/SagaMemoryReviewServiceTests.cs` (the `CreateRuntime` construction site)
  - `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/MemorySagaCurationCommandTests.cs`

Not touched, by decision:
- `CovenantServicePorts.cs`, `CovenantMutationEndpoints.cs`, the `CovenantMutationService` constructor and its factory, and `CovenantWorkloadBed.cs`. R12 adds no port member and no constructor parameter: the key comes from the kernel's latch capture.
- The erase services of Tasks 13, 14 and 16. Release is its own port (R2).
- Every other member of `tests/…/Support/MemoryErasureRouteDriver.cs`. Task 13 creates the union API; this task adds only the release trio (R6).
- The hosted-producer capsule TSV. No member edited here sits on a hosted-producer call graph (`SagaMemoryStore.Curation.cs`, `SagaMemoryReviewService`, `CovenantMutationService` have no capsule rows); Step 4 runs the capsule test to prove it.

**Interfaces:**
- Consumes:
  - Task 1: `MemoryErasureIdentity.ForSaga/ForLexicon/ForCovenant`; `MemoryErasureKey` (`Fingerprint`, `KeyId`); `SagaErasureReleaseRequest`, `LexiconErasureReleaseRequest`, `MemoryErasureReleaseResultDto`, `MemoryErasureReleaseOutcome` and `ApiResponse<MemoryErasureReleaseResultDto>` (all registered by Task 1); `ErrorCodes.MemoryErasure.*`; the theory `Memory_erasure_wire_types_have_explicit_source_generation_registrations`.
  - Task 2: `IMemoryErasureKeyProvider.OpenExisting(MemoryErasureKeyProbe.Reprobe)` and `TryCopyLatched()`. Release never touches `IMemoryErasureKeyCreator`.
  - Task 6: `MemoryErasureEvidence.IsInstalledAsync/AnyAsync/AnyForeignAsync/ContainsAsync/DeleteFingerprintAsync`; `MemoryErasureGuard.RefusalFor` (R16 codes); `MemoryErasureTestKeys.Isolated` and `CountingOsCredentialStore`; the pre-readiness warm-up (R11), so a restarted host that holds fingerprints has a resolved latch; `MemoryErasureEvidenceDeleterTests.AllowedCallers` (R7).
  - Task 7: `SagaErasureWriteGate.SagaIdentity(SagaMemoryScopeKind, string?, string)`; `SagaMemoryStore`'s required `erasureKeys`.
  - Task 11: `CovenantMutationKernel.CaptureErasureGate() → CovenantAgentErasureGate` (R12), and the capture Task 11 already places in `CovenantMutationService.CommitAsync` before `BEGIN`.
  - Task 12: `MemoryErasureStructuralTests` and its `ContentFreeLogFiles` list (R8).
  - Task 13:
    - `MemoryErasureEndpoints.MapMemoryErasureEndpoints`;
    - `MemoryErasureRouteInventoryTests.Routes`, shaped `(string Name, string Method, string Path, CovenantAuthorityRequirement? Authority)` (R13), and its protected-header theory;
    - the extraction host setup of `SagaErasureEndToEndTests`;
    - these members of `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs` (R6), exactly as Task 13 declares them: `static Host(InMemoryOsCredentialStore credentials, RestartableArcanumProfileFixture? profile = null, bool covenant = false, IWeaveService? weave = null)`; `static InsertSagaAsync(factory, string content, Guid? sessionId = null, ct)` and `static InsertSagaOutcomeAsync(…)`; `static FingerprintCountAsync(factory, MemoryReviewStore store, ct)`; `static ReadDataAsync<T>` and `static ReadErrorCodeAsync`; and, on an instance `new MemoryErasureRouteDriver(factory.CreateClient())`, `EraseSagaAsync(string memoryId, …)`, `EraseLexiconAsync(string name, Guid? campaignId, …)`, `EraseCovenantAsync(CovenantScope scope, Guid? campaignId, string key, …)` (each `→ MemoryErasureRoundTrip<…>`), `SetCovenantAsync(CovenantScope, Guid?, string key, string content, …)` and `PostAsync<TRequest>(string path, TRequest body, JsonTypeInfo<TRequest> info, …)`.
  - Tasks 14 and 16: the Lexicon and Covenant erase routes.
- Produces:
  - `public interface IMemoryErasureRelease { Task<Result<MemoryErasureReleaseResultDto>> ReleaseSagaAsync(SagaErasureReleaseRequest request, CancellationToken cancellationToken); Task<Result<MemoryErasureReleaseResultDto>> ReleaseLexiconAsync(LexiconErasureReleaseRequest request, CancellationToken cancellationToken); Task<Result<MemoryErasureReleaseResultDto>> ReleaseCovenantAsync(CovenantErasureReleaseRequest request, CancellationToken cancellationToken); }` (R2, verbatim).
  - `public sealed record CovenantErasureReleaseRequest(CovenantScope Scope, Guid? CampaignId, string Key) { public Result Validate() => CovenantWireValidation.First(CovenantWireValidation.ValidateOperationScope(Scope, CampaignId), CovenantWireValidation.ValidateKey(Key)); }`
  - `internal sealed class MemoryErasureRelease(ArcanumDbContext db, IMemoryErasureKeyProvider keys, ILogger<MemoryErasureRelease> logger) : IMemoryErasureRelease`. Its private `ReleaseCoreAsync(MemoryReviewStore store, IReadOnlyList<MemoryErasureIdentity> candidates, CancellationToken)` is its only opener, through a private `OpenConnectionAsync(CancellationToken)` in the `SagaMemoryReviewService` shape.
  - `internal static class MemoryErasureFingerprintRelease` (`Data` namespace):
    - `Task<int> DeleteCandidatesAsync(SqliteConnection connection, SqliteTransaction transaction, MemoryErasureKey key, IReadOnlyList<MemoryErasureIdentity> candidates, CancellationToken cancellationToken)`
    - `Task<bool?> WouldReleaseAsync(SqliteConnection connection, SqliteTransaction? transaction, MemoryReviewStore store, MemoryErasureIdentity? identity, MemoryErasureKey? key, CancellationToken cancellationToken)`
    - `Task<bool?> ReleaseForOperatorWriteAsync(SqliteConnection connection, SqliteTransaction transaction, MemoryReviewStore store, MemoryErasureIdentity? identity, MemoryErasureKey? key, CancellationToken cancellationToken)`
  - Trailing optional fields:
    - `bool? ReleasesErasureFingerprint = false` on `CovenantMutationEffectDto` and `CovenantMutationPlanPayload`;
    - `bool? ReleasedErasureFingerprint = false` on `CovenantMutationResultDto`, `CovenantMutationResultPayload`, `SagaCurationOutcome`, `SagaCurationResult` and `MemoryReviewBulkItemResultDto`.

    `true` means released (or, on a preflight, would release). `false` means there was nothing to release. `null` means the store holds fingerprints this write could not check: no latched key, evidence under a foreign `KeyId`, or a stored scope that cannot be fingerprinted. The operator is told to check `memory erasure status` (§5.6, R12).
  - `SagaMemoryReviewService(ArcanumDbContext db, IWeaveService weave, IMemoryReviewTokenCodec tokenCodec, WeaveIndexAvailability availability, IOptionsMonitor<ArcanumSettings> options, IMemoryErasureKeyProvider erasureKeys, TimeProvider timeProvider)`. The provider is required; the registration stays `AddScoped<ISagaMemoryReviewService, SagaMemoryReviewService>()`.
  - Routes `ReleaseSagaErasure`, `ReleaseLexiconErasure`, `ReleaseCovenantErasure`.
  - Driver members (the trio Task 13 reserves): `Task<MemoryErasureReleaseResultDto> ReleaseSagaAsync(SagaErasureReleaseRequest request, CancellationToken ct = default)`, `ReleaseLexiconAsync(LexiconErasureReleaseRequest request, …)` and `ReleaseCovenantAsync(CovenantErasureReleaseRequest request, …)`. Each POSTs its route through `PostAsync` and returns `ReadDataAsync` of the envelope, so it asserts 200.

- [ ] **Step 1: Write the failing tests**

`MemoryErasureReleaseEndpointTests` (`[Collection("ApiHost")]`):
- Each test owns one `InMemoryOsCredentialStore credentials` and passes it to every host it starts through `MemoryErasureRouteDriver.Host(credentials, profile, covenant: true)`; `driver` is `new MemoryErasureRouteDriver(factory.CreateClient())` for the current host.
- Successful releases go through `driver.Release…Async`; refusals go through `driver.PostAsync` (or the client with a raw JSON body) and `MemoryErasureRouteDriver.ReadErrorCodeAsync`.
- Campaign-scoped Saga memories are inserted with `InsertSagaAsync(factory, content, sessionId)` for a Session bound to that Campaign.
- Lexicon preconditions go through `ILexiconService.UpsertAsync` in a host scope. Campaigns are created through their routes, as Task 13's `SagaErasureEndpointTests` does.
- `Account` is `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`, and `Service` is `ArcanumCredentialIdentity.Service`.

```csharp
[Fact] public async Task Saga_release_deletes_the_exact_fingerprint_and_the_store_accepts_the_content_again()
{   // id = InsertSagaAsync(factory, Vault); await driver.EraseSagaAsync(id); released = await driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Vault))
    Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1), released);
    Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, Vault)); }
[Fact] public async Task Saga_release_with_a_trailing_newline_releases_the_trimmed_fingerprint()
    // erase "Rotate the vault key."; release "Rotate the vault key.\n" → (Saga, Released, 1); count 0   (Review Focus 2)
[Fact] public async Task Saga_release_deletes_both_the_exact_and_the_trimmed_fingerprint()
    // erase "Rotate." and, separately, "Rotate.\n" (two memories, two erasures, count 2); release "Rotate.\n" → (Saga, Released, 2); count 0
[Fact] public async Task Saga_release_is_exact_bytes_so_nfd_does_not_release_nfc()
    // erase NFC "caf\u00E9"; release NFD "cafe\u0301" → (Saga, NotFingerprinted, 0), count 1; release NFC "caf\u00E9" → (Saga, Released, 1), count 0   (Review Focus 2)
[Theory, InlineData("saga"), InlineData("lexicon"), InlineData("covenant")]
public async Task Release_without_any_evidence_is_NotFingerprinted_and_never_creates_the_key(string store)
{   Assert.Equal(new MemoryErasureReleaseResultDto(Store(store), MemoryErasureReleaseOutcome.NotFingerprinted, 0), result);
    Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(Service, Account)); }
[Fact] public async Task Release_is_idempotent()
    // upsert + erase Lexicon "Vault Keeper" (Global); release → (Lexicon, Released, 1); the same release again → 200 (Lexicon, NotFingerprinted, 0); count 0
[Fact] public async Task Release_with_rows_but_no_key_is_KeyLost_and_deletes_nothing()
    // restartable profile; host 1 erases Lexicon "Vault Keeper"; dispose; credentials.Delete(Service, Account); host 2 on the same profile and credentials:
    // → 409, error.code "MemoryErasure.KeyLost"; FingerprintCountAsync(Lexicon) == 1; ProbePresence(Service, Account) == NotFound
[Fact] public async Task Release_under_a_replaced_key_is_KeyLost_and_deletes_nothing()
    // host 1 erases Lexicon "Vault Keeper" under K1; credentials.Set(Service, Account, K2) with K2 = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)); host 2:
    // → 409 "MemoryErasure.KeyLost"; count 1; TryGet(Service, Account).Value == K2
[Fact] public async Task Lexicon_release_normalizes_the_name_and_honors_the_exact_scope()
    // erase "Vault Keeper" in Campaign C; release (Global, "  vault keeper ") → (Lexicon, NotFingerprinted, 0);
    // (Campaign D, "vault keeper") → (Lexicon, NotFingerprinted, 0); (Campaign C, "  vault keeper ") → (Lexicon, Released, 1); count 0
[Fact] public async Task Covenant_release_refuses_a_malformed_key_and_honors_the_exact_scope()
    // driver.SetCovenantAsync + driver.EraseCovenantAsync "preference.vault" in Campaign C (the key grammar is lowercase-only; nothing is folded)
    // (Campaign C, "Preference.Vault") → 400 "Covenant.InvalidKey", count 1
    // (Global, null, "preference.vault") → (Covenant, NotFingerprinted, 0); (Campaign D) → (Covenant, NotFingerprinted, 0)
    // (Campaign C, "preference.vault") → (Covenant, Released, 1); count 0
[Theory] // route, raw body, expected code; each → 400 with that code, FingerprintCountAsync unchanged, ProbePresence == NotFound
[InlineData("saga", """{"scopeKind":2,"campaignId":null,"content":"x"}""", "Validation.InvalidBody")]
[InlineData("saga", """{"scopeKind":1,"campaignId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","content":"x"}""", "Validation.InvalidBody")]
[InlineData("saga", """{"scopeKind":1,"campaignId":null,"content":""}""", "Validation.InvalidBody")]
[InlineData("lexicon", """{"scope":{"kind":"Campaign","campaignId":null},"name":"x"}""", "Lexicon.InvalidScope")]
[InlineData("lexicon", """{"scope":{"kind":"Global","campaignId":null},"name":"   "}""", "Lexicon.InvalidName")]
[InlineData("covenant", """{"scope":"Global","campaignId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","key":"a"}""", "Covenant.InvalidScope")]
public async Task Release_requests_that_fail_validation_answer_400_and_change_nothing(string store, string body, string code)
[Fact] public async Task Release_responses_carry_the_private_no_store_tuple()
    // the 200 of a Lexicon release, and a 400 from each route: Cache-Control "no-store, private", Pragma "no-cache", Expires "0", no ETag, no Last-Modified
```

`MemoryErasureReleaseServiceTests` (`[Collection("Grimoire")]`):

```csharp
[SkippableFact] public async Task Release_below_core_thirteen_is_unavailable_and_touches_no_key()
{   // EvolutionScratchDatabase.Create(); GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 64, Token);
    // db = SagaMemoryMidUpgradeWriteTests.CreateContext(file); CountingOsCredentialStore counting = new(new InMemoryOsCredentialStore());
    // MemoryErasureRelease release = new(db, MemoryErasureTestKeys.Isolated(counting), NullLogger<MemoryErasureRelease>.Instance);
    Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, (await release.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, "x"), Token)).Error.Code);
    // the same for ReleaseLexiconAsync(Global "x") and ReleaseCovenantAsync(Global "preference.x")
    Assert.Equal(0, counting.Calls); }
```

`MemoryErasureReleaseEndToEndTests.Released_content_is_extracted_again_and_a_second_erase_succeeds` covers §19.2 #5, re-running extraction rather than a store insert (R30):
- The host is set up like Task 13's `SagaErasureEndToEndTests`:
  - `ServiceOverrides` swap `IWeaveService` for a recording fake that returns 64-dimension vectors and records every text;
  - `FakeIntelligence` answers the extraction prompt with the conclusions each pass names;
  - one `InMemoryOsCredentialStore` is used;
  - every pass runs `SagaExtractionService.ExtractForSessionAsync` under a `GrimoireWorkKind.SagaExtraction` work lease from `IGrimoireConnectionAdmissionGate`.
- `T = "The ward-stone lies under the mill."`

1. Pass 1, with conclusions `[T]`, writes M1. `GET api/memory/saga/{M1}` supplies its `Memory.ScopeKind` and `ScopeCampaignId`. Erase M1 through both routes.
2. Append an entry, then run pass 2 with `[T]`. The outcome is `Completed`, no row holds `T`, and the weave has recorded `T` exactly once.
3. Release M1's scope, M1's Campaign and `T` → `(Saga, Released, 1)`.
4. Append an entry, then run pass 3 with `[T]`. The outcome is `Completed`, exactly one row holds `T` (M2, where M2 ≠ M1), the weave has recorded `T` twice, and the Session watermark equals the newest entry.
5. Erase M2. It returns 200 with `Replayed == false` and `Local.SuppressionFingerprintRecorded == true`. `FingerprintCountAsync(Saga) == 1`, and `memory_erasure_receipts` holds 2 rows, because release keeps receipts (§6.3).

`MemoryErasureOperatorRecreationTests` (`[Collection("ApiHost")]`, `Host(credentials, covenant: true)`):
- An operator Covenant `set` means `POST api/memory/covenant/set/prepare`, then `PUT api/memory/covenant`.
- A Saga correction means `POST api/memory/saga/{id}/correct`.

```csharp
[Fact] public async Task Operator_set_of_an_erased_key_discloses_then_releases_the_fingerprint() // §19.2 #6, live half
{   // driver.SetCovenantAsync + driver.EraseCovenantAsync "preference.vault" in Campaign C; set/prepare (ExpectedRevision 0) through driver.PostAsync
    Assert.True(preflight.Effect.ReleasesErasureFingerprint);
    // PUT → 200
    Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);
    Assert.True(result.ReleasedErasureFingerprint);
    Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant)); }
    // The restore half (a backup taken after this set, then restored, keeps the entry) is Task 24's test (R28).
[Fact] public async Task A_set_refused_by_the_kernel_releases_nothing()
    // erased key; prepare with ExpectedRevision 5; PUT → 409 "Covenant.RevisionConflict"; count 1
[Fact] public async Task A_replayed_set_reports_no_release()
    // after the release above, PUT the same body (same MutationId) → 200, Replayed true, ReleasedErasureFingerprint false; count 0
[Theory, InlineData(false), InlineData(true)]
public async Task A_set_of_a_never_erased_key_reports_false_on_both_halves(bool otherKeyErased)
    // otherKeyErased: "preference.other" is set and erased first; then set "preference.fresh":
    // Effect.ReleasesErasureFingerprint == false; result.ReleasedErasureFingerprint == false; count == (otherKeyErased ? 1 : 0)
[Fact] public async Task Set_with_fingerprints_but_a_lost_key_succeeds_and_reports_null()
    // restartable profile; host 1 erases "preference.vault"; dispose; credentials.Delete(Service, Account); host 2 (the warm-up latches Absent):
    // Effect.ReleasesErasureFingerprint == null; PUT → 200, Outcome Applied, ReleasedErasureFingerprint == null; count 1; ProbePresence == NotFound
[Fact] public async Task Saga_correction_to_erased_content_in_the_same_scope_releases_it()
    // A "alpha" and B "beta" (Global); erase B; correct A (expectedContentHash of "alpha") to "beta"
    // → 200, ReleasedErasureFingerprint true; count 0; GET A reads "beta"
[Fact] public async Task Saga_correction_in_another_scope_releases_nothing()
    // B "beta" in Campaign X, erased; A "alpha" Global corrected to "beta" → ReleasedErasureFingerprint false; count 1
[Fact] public async Task Saga_correction_with_evidence_under_a_replaced_key_reports_null()
    // host 1 erases B "beta" under K1; set a fresh canonical K2; host 2 (the warm-up latches Present under K2); correct A to "beta" → 200, ReleasedErasureFingerprint null; count 1
[Fact] public async Task Bulk_review_Correct_to_erased_content_releases_and_reports_it_on_the_item()
{   // A "alpha", B "beta" (Global); erase B; POST api/memory/saga/review/prepare then apply: Correct A → "beta"
    Assert.True(Assert.Single(result.Items).ReleasedErasureFingerprint);
    Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    // apply the same request again
    Assert.True(replayed.Replayed);
    Assert.False(Assert.Single(replayed.Items).ReleasedErasureFingerprint); }
```

`MemoryErasureStructuralTests` (Task 12's file) gains the R8 pin "no agent tool references the erase or release services". It reads sources under `NativeSqlCipherTestPaths.RepositoryRoot()`.

```csharp
private static readonly Regex ForbiddenAgentToolReference = new(
    @"\b(IMemoryErasure\w*|ISagaMemoryErasureService|ILexiconErasureService|ICovenantEntryErasureService|MemoryErasureRelease|MemoryErasureFingerprintRelease|MemoryErasureAdministration|SagaMemoryErasureService|CovenantEntryErasureService)\b",
    RegexOptions.CultureInvariant);

[Fact] public void No_agent_tool_references_the_erase_or_release_services()
{   // every src/**/ArcanumInternalToolServer*.cs
    Assert.Contains("src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs", files);
    Assert.Contains("src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs", files);
    Assert.True(files.Count >= 15);   // the 15 partials present at fe63b204
    Assert.Empty(files.Where(file => ForbiddenAgentToolReference.IsMatch(File.ReadAllText(file)))); }
[Theory] // one row per forbidden name, e.g. "IMemoryErasureRelease release," and "ISagaMemoryErasureService erase,"
public void The_agent_tool_pattern_recognizes_each_service_name(string line) => Assert.Matches(ForbiddenAgentToolReference, line);
[Fact] public void The_agent_tool_pattern_ignores_the_ordinary_store_ports() => Assert.DoesNotMatch(ForbiddenAgentToolReference, "ILexiconService lexicon, ISagaMemoryStore saga");
```

CLI tests:
- `CovenantCommandTests.Set_preflight_names_the_fingerprint_release_before_the_prompt`. The preflight carries `Effect.ReleasesErasureFingerprint: true`. A recording prompt snapshots stdout when it is asked, and the snapshot contains `"  Releases an erasure fingerprint: agents may write this key in this scope again."`.
- `CovenantCommandTests.Set_preflight_with_unverifiable_fingerprints_points_to_erasure_status`. The flag is `null`, and the snapshot contains `"  Erasure fingerprints could not be checked; run 'arcanum memory erasure status'."`.
- `CovenantCommandTests.Set_json_carries_the_release_flags`, with `--json --yes`:
  - the stderr plan payload parses with `releasesErasureFingerprint` equal to `true`;
  - the stdout result document has `releasedErasureFingerprint` equal to `true`.
- `MemorySagaCurationCommandTests.Correct_reports_a_released_fingerprint`. The result flag is `true`, and stdout contains `"Released an erasure fingerprint for this content."`.
- `MemorySagaCurationCommandTests.Correct_without_a_release_prints_no_release_line`. The flag is `false`, and stdout does not contain `"erasure"`.

Closed-inventory edits:
- `MemoryErasureRouteInventoryTests.Routes` gains three rows. The protected-header theory sends `{}` to each of them and expects 400 `Validation.InvalidBody` with the tuple.
  - `("ReleaseSagaErasure", "POST", "/api/memory/saga/release", CovenantAuthorityRequirement.LifecycleManage)`
  - `("ReleaseLexiconErasure", "POST", "/api/memory/lexicon/release", CovenantAuthorityRequirement.LifecycleManage)`
  - `("ReleaseCovenantErasure", "POST", "/api/memory/covenant/release", CovenantAuthorityRequirement.LifecycleManage)`
- `ArcanumJsonContextCompletenessTests.Memory_erasure_wire_types_have_explicit_source_generation_registrations` gains `[InlineData(typeof(CovenantErasureReleaseRequest))]`. This is the only row this task adds (R4).
- `MemoryErasureEvidenceDeleterTests.AllowedCallers` becomes `["src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureFingerprintRelease.cs"]`. Release and every operator re-creation path delete through that one file.
- `MemoryErasureStructuralTests.ContentFreeLogFiles` gains `Memory/MemoryErasureRelease.cs` and `Data/MemoryErasureFingerprintRelease.cs`.

- [ ] **Step 2: Declare the contract surface, then run the tests to verify they fail**

First add only what the tests need to compile:
- `IMemoryErasureRelease`;
- `CovenantErasureReleaseRequest`, with `Validate()`, its inventory port and contract entry, and its `ArcanumJsonContext` registration;
- the seven trailing fields, each defaulting to `false`;
- the `SagaMemoryReviewService` constructor parameter, with its test site passing `MemoryErasureTestKeys.Isolated()`;
- a `MemoryErasureRelease` whose three methods throw `NotImplementedException`;
- the driver's release trio, which compiles against the new request type and first runs against a 404.

Add no route, no release logic and no DI registration.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureReleaseEndpointTests|FullyQualifiedName~MemoryErasureReleaseServiceTests|FullyQualifiedName~MemoryErasureReleaseEndToEndTests|FullyQualifiedName~MemoryErasureOperatorRecreationTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~MemoryErasureRouteInventoryTests|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~MemorySagaCurationCommandTests" </dev/null
```

Expected RED:
- The three release routes answer 404. Every `MemoryErasureReleaseEndpointTests` case fails, so does the inventory's `The_erasure_route_set_is_exactly_the_declared_routes`, and the end-to-end test fails at its step 3.
- `Release_below_core_thirteen…` throws `NotImplementedException`.
- Every re-creation test reads `false` where it expects `true` or `null`. `A_set_refused_by_the_kernel…` and `A_set_of_a_never_erased_key…` pass on arrival: they pin what must not change.
- `Evidence_deleter_callers_are_a_closed_allow_list` fails, because the allow-list is exact and the named file does not exist yet.
- The five CLI tests do not find their lines.

`No_agent_tool_references_the_erase_or_release_services` is a structural pin that is green on arrival. Step 5 proves it is load-bearing. `New_erasure_log_templates_are_content_free` fails until the two listed files exist.

- [ ] **Step 3: Implement**

- **Release (§5.5)** in `MemoryErasureRelease`.
  - **Validation.** Every request goes through these checks:
    - Saga: `ScopeKind` is defined; `CampaignId` is a non-empty id if and only if the scope is `Campaign`; `Content` is non-empty. Any failure is `Validation.InvalidBody`.
    - Lexicon: `Scope.Validate()` (`Lexicon.InvalidScope`), and a non-empty `Name.Trim()` (`Lexicon.InvalidName`).
    - Covenant: `request.Validate()`.
  - **Candidates.** Candidate identities are distinct:
    - Saga: `ForSaga(ScopeKind, CampaignId, Content)`, plus `ForSaga(…, Content.Trim())` when the trimmed text differs and is non-empty.
    - Lexicon: `ForLexicon(Scope.CampaignId, Name)`.
    - Covenant: `ForCovenant(Scope, CampaignId, Key)`. A validated key is already the normalized key.
  - **`ReleaseCoreAsync`** runs these steps in order:
    1. Open the scoped connection. If `IsInstalledAsync` is false, return `MemoryErasure.Unavailable`.
    2. If `AnyAsync(store)` is false (no transaction open), return `NotFingerprinted, 0`. No keychain call happens.
    3. Otherwise call `keys.OpenExisting(MemoryErasureKeyProbe.Reprobe)`, the operator re-probe (§5.1). `Present` continues. Any other state maps through `MemoryErasureGuard.RefusalFor(state).Code` (R16): `Absent` → `KeyLost`; `Unresolved`, `Unavailable` or `Malformed` → `KeyUnavailable`. The creator is never called.
    4. Under `SqliteBusyRetry`, open `BEGIN IMMEDIATE` and run `DeleteCandidatesAsync`:
       - a deleted count above 0 commits and returns `Released(count)`;
       - otherwise, `AnyForeignAsync(store, key.KeyId)` rolls back and returns `KeyLost`;
       - otherwise, roll back and return `NotFingerprinted, 0`.
    5. Dispose the key on every path.
  - **Messages** (content-free):
    - `KeyLost`: "Erasure fingerprints exist for this store that the erasure key cannot verify, so nothing was released. Run 'arcanum memory erasure status'."
    - `KeyUnavailable`: "The erasure key could not be read, so nothing was released."
    - `Unavailable`: "Selective erasure is unavailable until the Grimoire reaches its current schema."
  - **Log.** The one log line is `"Erasure release for {Store} removed {Count} fingerprints."`.
- **`MemoryErasureFingerprintRelease`.**
  - `DeleteCandidatesAsync` computes `key.Fingerprint(identity)` for each candidate and returns the sum of `DeleteFingerprintAsync` over them.
  - `ReleaseForOperatorWriteAsync` (§5.6) runs inside the caller's transaction:
    1. `AnyAsync(store)` false → `false`.
    2. `identity` or `key` null → `null`.
    3. `DeleteFingerprintAsync(key.Fingerprint(identity)) > 0` → `true`.
    4. `AnyForeignAsync(store, key.KeyId)` → `null`.
    5. Otherwise → `false`.
  - `WouldReleaseAsync` makes the same decision, with `ContainsAsync` in place of the delete.
- **Routes.** `MapMemoryErasureEndpoints` gains three static POST handlers. Each handler:
  - reads its body through `ApiRequestJson.ReadAsync` with explicit `JsonTypeInfo`. A null body or a null member (`Content`, `Scope`, `Name`, `Key`) is `Validation.InvalidBody`, answered before `Validate()`;
  - carries `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)` and `.WithName(...)`;
  - calls `CovenantProtectedResponseHeaders.Apply(context.Response)`;
  - returns `Results.Json(ApiResponse<MemoryErasureReleaseResultDto>.FromResult(...), ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto, statusCode: ArcanumErrorMapper.ResolveStatusCode(...))`.

  There is no `MutationId`.
- **Inventory port.**
  - `Ports` gains `"RetroDownfall.Arcanum.Core.Memory." + nameof(IMemoryErasureRelease)`, with the rationale "One store-neutral release port, so Saga, Lexicon and Covenant fingerprints are lifted by one operator-only path that never creates the erasure key."
  - `Contracts` gains `CovenantErasureReleaseRequest` as `OperatorApi`/`Request` on that port.
- **DI.** Add `services.AddScoped<IMemoryErasureRelease, MemoryErasureRelease>();` in `AddArcanumInfrastructure`, beside the erase services. It is host-only.
- **Covenant re-creation** (R12, latch only):
  - `PrepareAsync`:
    - When `operation is CovenantOperation.Set`, it captures `using CovenantAgentErasureGate erasureGate = kernel.CaptureErasureGate();`. This is R12's one capture method, called at a second site for the preflight only; the commit path adds no capture and reads the gate Task 11 already takes. It is a latch copy with no keychain I/O, which is why taking it after the endpoint's read lease is harmless.
    - It then computes `WouldReleaseAsync(await connections.GetOpenCoreConnectionAsync(ct), null, MemoryReviewStore.Covenant, MemoryErasureIdentity.ForCovenant(scopeKind, scope.CampaignId, normalizedKey), erasureGate.Key, ct)`.
    - Retire and correct report `false`.
    - The flag is not bound into the token, because operator writes are never refused over a fingerprint.
    - `EffectDto` gains the flag parameter.
  - `CommitAsync`:
    - Task 11's capture before `BEGIN` already holds the gate.
    - After `ApplyBatchAsync` succeeds and before `transaction.CommitAsync`, when `receipt.Kind is CovenantMutationKind.OperatorSet && !receipt.Replayed`, it sets `ReleasedErasureFingerprint = await ReleaseForOperatorWriteAsync(connection, transaction, MemoryReviewStore.Covenant, identity, erasureGate.Key, ct)`.
    - Every other receipt reports `false`, and `TryReplayAsync` reports `false`.
    - A kernel refusal rolls back the whole transaction, the release included.
- **Saga single correction.**
  - `SagaMemoryStore.CorrectAsync` copies `erasureKeys.TryCopyLatched()` once, before `SqliteBusyRetry.ExecuteAsync`, and disposes the copy afterwards.
  - After the content `UPDATE`, it calls `ReleaseForOperatorWriteAsync((SqliteConnection)connection, (SqliteTransaction)transaction, MemoryReviewStore.Saga, identity, key, ct)`.
    - `identity` is `SagaErasureWriteGate.SagaIdentity(scopeKind, campaignId, content)`, using the row's own stored scope and the new content.
    - A `FormatException` from an unparseable stored Campaign gives `identity = null`.
  - Every other outcome (`NotFound`, `StaleContent`, `Unchanged`, `AlreadyRetired`) reports `false`.
  - `SagaCurationService.FinishAsync` copies the flag into `SagaCurationResult`.
- **Saga bulk review.**
  - `ApplyAsync` copies `erasureKeys.TryCopyLatched()` once, before `BeginWriteTransactionAsync`, when any decision is `Correct`.
  - The `Correct` arm calls `ReleaseForOperatorWriteAsync` after its `UPDATE` and sets the item flag.
  - `ResponseDigest` does not include the flag, so stored receipts and replays are unchanged. Items that `ReadReplayAsync` builds report `false`, and Lexicon and Covenant review items report `false`.
- **CLI.**
  - `ConfirmAsync`, human mode: after the "Affects" lines, print `"  Releases an erasure fingerprint: agents may write this key in this scope again."` for `true`, or `"  Erasure fingerprints could not be checked; run 'arcanum memory erasure status'."` for `null`.
  - `WritePlan` and `WriteMutation`: the payloads carry the flags. `WriteMutation` in human mode prints `"Released an erasure fingerprint for this key."` for `true`, or the null sentence.
  - `WriteCurationResult` prints `"Released an erasure fingerprint for this content."` for `true`, or the null sentence.
- **Docs.**
  - API §1: rows for the three routes.
  - API §8.35 "Release": the authority; the exact and trimmed Saga forms; the outcomes; `KeyLost` versus `KeyUnavailable`; that release never creates the key, takes no `MutationId`, and keeps receipts.
  - API §8.28 (`releasesErasureFingerprint`, `releasedErasureFingerprint`), §8.30 and §8.34: the trailing fields, each with the tri-state meaning.
  - Command Reference: the `memory covenant set` and `memory saga correct` rows name the printed release line and its `null` form.
- **Inventories.**
  - Acquisition rows: `("src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureRelease.cs", "MemoryErasureRelease", "ReleaseCoreAsync(3)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)")` and `(…, "MemoryErasureRelease", "OpenConnectionAsync(1)", AcquisitionConstructKind.ProviderOpen, "db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)")`. Both are `LiveGrimoire`, `ServingRawOrdinary`, `OrdinaryConnectionFactory`, with a null proof.
  - Nothing else in this task adds a row. `GetOpenCoreConnectionAsync` calls are not acquisition identities, and `CorrectAsync` and `ApplyAsync` reuse their existing opens.
  - `git add` the three new `src` files. Add the three catalog lines in ordinal order. Paste the digest that `GrimoireAdmissionBenchmarkManifestTests` reports into both `AdmissionBenchmarkManifest.cs:19` and the workload JSON.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then this cluster:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~CovenantPublicContractInventoryTests|FullyQualifiedName~ApiWireContractTests|FullyQualifiedName~CovenantMutationServiceTests|FullyQualifiedName~CovenantMutationRouteTests|FullyQualifiedName~CovenantErasureSuppressionTests|FullyQualifiedName~SagaCurationEndpointTests|FullyQualifiedName~SagaCurationServiceTests|FullyQualifiedName~SagaMemoryReviewServiceTests|FullyQualifiedName~MemoryReviewEndpointTests|FullyQualifiedName~MemoryErasureKeyCustodyTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery" </dev/null
```

Everything must be green, and the capsule test must pass with no regeneration.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Drop the `Trim()` candidate | `Saga_release_with_a_trailing_newline…` |
| Return `NotFingerprinted` instead of `KeyLost` when the key is `Absent` | `Release_with_rows_but_no_key_is_KeyLost…` |
| Skip the `AnyForeignAsync` check after a zero delete | `Release_under_a_replaced_key_is_KeyLost…` |
| Resolve the key through `IMemoryErasureKeyCreator.OpenOrCreate(false)` before the `AnyAsync` probe | `Release_without_any_evidence…` (`ProbePresence` becomes `Ok`) |
| Remove the `IsInstalledAsync` gate | `Release_below_core_thirteen…` (answers `NotFingerprinted`) |
| Fold the Covenant key to lowercase before validating | `Covenant_release_refuses_a_malformed_key…` |
| Release the Covenant fingerprint in its own committed transaction before `ApplyBatchAsync` | `A_set_refused_by_the_kernel_releases_nothing` |
| Hard-code the prepare flag to `false` | `Operator_set_of_an_erased_key…` |
| Report `true` for every operator `Set` without calling the helper | `A_set_of_a_never_erased_key…` |
| Report `false` instead of `null` when evidence exists and no key is latched | `Set_with_fingerprints_but_a_lost_key…` |
| Skip the foreign-row `null` branch in `ReleaseForOperatorWriteAsync` | `Saga_correction_with_evidence_under_a_replaced_key_reports_null` |
| Compute the Saga correction identity as `Global` | `Saga_correction_in_another_scope_releases_nothing` |
| Remove the bulk `Correct` release | `Bulk_review_Correct…` |
| Add `ISagaMemoryErasureService` to `ArcanumInternalToolServer.SagaTools.cs` | `No_agent_tool_references_the_erase_or_release_services` |
| Call `DeleteFingerprintAsync` directly from `SagaMemoryStore.Curation.cs` | `Evidence_deleter_callers_are_a_closed_allow_list` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureRelease.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantErasureContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureRelease.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureFingerprintRelease.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationWireContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryReviewContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.SagaCuration.cs docs/Arcanum.API.md docs/Arcanum.Command.Reference.md tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureReleaseEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureOperatorRecreationTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureReleaseEndToEndTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureReleaseServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRouteDriver.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/SagaMemoryReviewServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemorySagaCurationCommandTests.cs
git commit -m "feat: release erasure fingerprints explicitly and on operator re-creation" -m "Adds the three LifecycleManage release routes behind one release port, and makes an operator Covenant set or Saga correction that re-creates an erased identity delete its fingerprint in the same transaction and report it." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 18: Erasure status, scrub and key reset

**Files:**
- Create (Core): `src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureAdministration.cs` (R3)
- Create (Infrastructure): `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureAdministration.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs` (add `ReadWalPendingAsync`); `src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs` (add `ProbePresence()`, the metadata-only probe)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs` (four routes); `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (`AddArcanumInfrastructure`: scoped `IMemoryErasureAdministration`, host only)
- Modify (docs): `docs/Arcanum.API.md` (§1 four rows, §8.35 "Status, scrub and key reset"); `docs/Arcanum.DEBUGGING.Human.md` (recipe 32, "Recover from a lost or replaced erasure key", R26)
- Modify (closed inventories):
  - the three benchmark files (+2 `src` files; R20);
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (+5 rows) and `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+5** over Task 17's value) (R21)
- Test (create):
  - `tests/RetroDownfall.Arcanum.Tests/Support/SecretAccessRecordingCredentialStore.cs`
  - `tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureAdministrationEndpointTests.cs` (host)
  - `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureAdministrationTests.cs` (service-level scrub and below-v13 cases, R33)
  - `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureKeyLossEndToEndTests.cs`
- Test (modify):
  - `tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs` (four rows and a GET-specific test, R13)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs` (`AllowedCallers`, R7)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (`ContentFreeLogFiles` +1 file, R8)
  - `tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureEvidenceTests.cs`
  - `tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyringTests.cs`

Not touched: `ArcanumJsonContext.cs` and `ArcanumJsonContextCompletenessTests.cs`. Task 1 registered every status, scrub and key-reset DTO, both enums and the four `ApiResponse<T>` wrappers (R4). The route driver is consumed only, as Task 13 states (R6).

**Interfaces:**
- Consumes:
  - Task 1: `MemoryErasureStatusDto`, `MemoryErasureStoreCountsDto`, `MemoryErasureScrubResultDto`, `MemoryErasureKeyResetPreflightDto`, `MemoryErasureKeyResetRequest`, `MemoryErasureKeyResetResultDto`, `MemoryErasureKeyStatus`, `MemoryErasureWalCheckpointAttempt`, and `ErrorCodes.MemoryErasure.*`.
  - Task 2: `MemoryErasureKeyring` (registered as itself and behind both ports): `OpenExisting(Reprobe)` and `CreateForReset()`.
  - Task 3: `CoreSchemaVersionTwelveFixture.ChainSet()`.
  - Task 4: `SagaStoreHarness` and its FTS5 stand-in for a legacy `vec0` mirror (`CREATE VIRTUAL TABLE saga_memory_embeddings_vec USING fts5(MemoryId, Embedding)`).
  - Task 6: `MemoryErasureEvidence.IsInstalledAsync/CountAsync/ClearWalPendingAsync/DeleteUnverifiableAsync` and `MemoryErasureEvidenceCounts`; the pre-readiness warm-up (R11); `MemoryErasureEvidenceDeleterTests.AllowedCallers`; `MemoryErasureTestKeys`.
  - Task 7: `SagaExtractionOutcome.DeferredForErasureKey`.
  - Task 8: the Lexicon scribe chokepoint.
  - Task 11: `CovenantMutationKernel.CaptureErasureGate()` and the R16 refusal mapping, driven the way `CovenantErasureRestartTests` drives a host agent proposal (under `ICovenantOperationGate.AcquireWriteAsync(CovenantOperationScope.ForCampaign(campaignId))`, a serializable transaction as in `CovenantErasureSameProcessTests.AssertFreshCrudAsync`, and `CovenantMutationFixture.AgentPropose`).
  - Task 12: `IMemoryErasureTokenCodec.IssueErasureKeyReset → Result<MemoryErasureIssuedToken>` and `ReadErasureKeyReset`, `MemoryErasureIssuedToken(Token, IssuedAtUtc, ExpiresAtUtc)`, `MemoryErasureKeyResetTokenFacts`, `MemoryErasureScrubber` (and `CheckpointAsync`), and `MemoryReviewTokenCodec`; `MemoryErasureStructuralTests.ContentFreeLogFiles`.
  - Task 13: the endpoint file; the route inventory and its protected-header theory; `SagaMemoryErasureService(db, keyCreator, keys, tokens, scrubber, gate, issuer, initializer, options)`, which the service-level scrub test builds; the driver members listed under Task 17.
  - Tasks 14 and 16: the Lexicon and Covenant erase routes.
- Produces:
  - `public interface IMemoryErasureAdministration { Task<Result<MemoryErasureStatusDto>> GetStatusAsync(CancellationToken cancellationToken); Task<Result<MemoryErasureScrubResultDto>> ScrubAsync(CancellationToken cancellationToken); Task<Result<MemoryErasureKeyResetPreflightDto>> PrepareKeyResetAsync(CancellationToken cancellationToken); Task<Result<MemoryErasureKeyResetResultDto>> ResetKeyAsync(MemoryErasureKeyResetRequest request, CancellationToken cancellationToken); }`
  - `internal sealed class MemoryErasureAdministration : IMemoryErasureAdministration`, with two constructors:
    - a public DI constructor `(ArcanumDbContext db, MemoryErasureKeyring keyring, IMemoryErasureTokenCodec tokens, MemoryErasureScrubber scrubber, ILogger<MemoryErasureAdministration> logger)`. There is no `TimeProvider`: the codec is the one clock for the token and the DTO times (Task 12);
    - an `internal` overload that adds `Func<CancellationToken, Task<MemoryErasureWalCheckpointAttempt>> checkpoint`. The public one passes `scrubber.CheckpointAsync`.

    Each public method calls a private `OpenConnectionAsync(CancellationToken)` exactly once.
  - `MemoryErasureEvidence.ReadWalPendingAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) → Task<IReadOnlyList<Guid>>`. It returns receipts with `ScrubStateCode = 1` and the WAL bit set, ordered by `MutationId`, and is empty below v13.
  - `internal OsCredentialStoreStatus? MemoryErasureKeyring.ProbePresence()`:
    - it runs under the keyring's lock;
    - when the credential store implements `IOsCredentialPresenceProbe`, it returns `ProbePresence(Service, Account)`, and an exception maps to `Unavailable`;
    - it returns `null` when the store has no probe;
    - it reads no secret and never changes the latch;
    - a disposed keyring answers `Unavailable`.

    It lives in the keyring because `MemoryErasureKeyCustodyTests` allows only the keyring (and the reset paths) to name the account.
  - Routes `GetMemoryErasureStatus`, `ScrubMemoryErasures`, `PrepareMemoryErasureKeyReset`, `ResetMemoryErasureKey`.
  - Test support: `internal sealed class SecretAccessRecordingCredentialStore(InMemoryOsCredentialStore inner) : IOsCredentialStore, IOsCredentialPresenceProbe`:
    - `int TryGetCount(string account)`, `int SetCount(string account)`, `int ProbeCount(string account)`;
    - `void FailAccount(string account, OsCredentialStoreStatus status)` and `void ClearFailure(string account)`. While an account is failed, every member for it returns that status.
    - `internal static ArcanumWebApplicationFactory WithRecordingCredentials(this ArcanumWebApplicationFactory factory, SecretAccessRecordingCredentialStore credentials)`. Before the first client access, it wraps the factory's existing `ServiceOverrides` and then replaces `IOsCredentialStore` with `credentials`. Task 13's `Host` takes an `InMemoryOsCredentialStore`, so the driver is not edited.

- [ ] **Step 1: Write the failing tests**

`MemoryErasureAdministrationEndpointTests` (`[Collection("ApiHost")]`):
- Each test owns one `InMemoryOsCredentialStore inner` and one `SecretAccessRecordingCredentialStore credentials` over it. Every host is `MemoryErasureRouteDriver.Host(inner, profile, covenant: true).WithRecordingCredentials(credentials)`, and `driver` is `new MemoryErasureRouteDriver(factory.CreateClient())`.
- `Stores` is always three entries, in store-code order: Covenant, Saga, Lexicon.
- `K2` is `Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))`.

```csharp
[Fact] public async Task Status_on_a_fresh_installation_is_Absent_with_zero_counts_and_reads_no_secret()
{   Assert.Equal(MemoryErasureKeyStatus.Absent, status.KeyStatus);
    Assert.Equal([MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon], status.Stores.Select(s => s.Store));
    Assert.All(status.Stores, s => Assert.Equal((0L, 0L, 0L), (s.Fingerprints, s.Unverifiable, s.Receipts)));
    Assert.Equal(0, status.PendingScrubReceipts);
    Assert.Equal(0, credentials.TryGetCount(Account)); Assert.Equal(1, credentials.ProbeCount(Account)); }
[Fact] public async Task Status_counts_one_fingerprint_and_one_receipt_per_erase_even_with_twins()
    // two Saga memories with identical content (one erase, two subjects) and one Lexicon erase:
    // KeyStatus Present; Covenant (0,0,0); Saga (1,0,1); Lexicon (1,0,1); PendingScrubReceipts 0; memory_erasure_receipt_subjects holds 3 rows
[Fact] public async Task Scrub_with_nothing_pending_reports_NotAttempted()
    // one Saga erase on a quiet host (its receipt is Verified); POST api/memory/erasure/scrub → 200 (NotAttempted, 0, 0) with the protected tuple
[Fact] public async Task Reset_key_discards_only_rows_under_a_foreign_key_and_keeps_the_present_key()
    // restartable profile; host 1 erases Lexicon "Vault Keeper" under K1; dispose; credentials.Set(Service, Account, K2); host 2 erases Lexicon "Harbor Master" under K2
    // prepare → KeyStatus Present, Stores [(Covenant,0,0,0), (Saga,0,0,0), (Lexicon,2,1,2)]
    // apply → (Present, FingerprintsDiscarded 1, ReceiptsDiscarded 1, KeyCreated false); status Lexicon (1,0,1)
    // TryGet(Service, Account).Value == K2; SetCount(Account) unchanged across prepare and apply
[Fact] public async Task Reset_key_creates_the_key_when_it_is_NotFound_and_rows_exist()
    // host 1 erases one Saga memory and Lexicon "Vault Keeper"; dispose; credentials.Delete(Service, Account); host 2
    // prepare → KeyStatus Lost, Stores [(Covenant,0,0,0), (Saga,1,1,1), (Lexicon,1,1,1)]
    // apply → (Present, FingerprintsDiscarded 2, ReceiptsDiscarded 2, KeyCreated true); ProbePresence(Service, Account) == Ok
    // status → Present, every store (0,0,0), PendingScrubReceipts 0; memory_erasure_receipt_subjects holds 0 rows (cascade)
[Fact] public async Task Reset_key_is_idempotent()
    // continue the case above: apply the first token again → 200 (Present, 0, 0, KeyCreated false); a fresh prepare reports Present with every Unverifiable 0
[Fact] public async Task Reset_key_refuses_an_unavailable_key_and_changes_nothing()
    // rows exist and the key was deleted; prepare → 200 (Lost); credentials.FailAccount(Account, Unavailable); apply → 503 "MemoryErasure.KeyUnavailable";
    // SetCount(Account) == 0; ClearFailure; status Saga (1,1,1) and Lexicon (1,1,1) unchanged
[Fact] public async Task Reset_key_refuses_a_malformed_item_without_overwriting_it()
    // host 1 erases Lexicon "Vault Keeper"; dispose; credentials.Set(Service, Account, "not base64url"); host 2 → prepare → 503 "MemoryErasure.KeyUnavailable";
    // TryGet(Service, Account).Value == "not base64url"; SetCount(Account) == 1 (the test's own write)
[Fact] public async Task Reset_key_apply_with_counts_that_no_longer_match_is_StalePlan()
    // the foreign-key setup above; a token issued from a host scope through IMemoryErasureTokenCodec.IssueErasureKeyReset(new(Present, 5, 5)).Value.Token
    // → 409 "MemoryErasure.StalePlan"; status Lexicon (2,1,2) unchanged
[Fact] public async Task Reset_key_whose_key_state_no_longer_holds_is_StalePlan()
    // the foreign-key setup above; prepare (Present); dispose; credentials.Delete; host 3 → apply that token → 409 "MemoryErasure.StalePlan";
    // ProbePresence == NotFound (no key minted); counts unchanged
[Fact] public async Task Reset_key_with_an_undecodable_token_is_InvalidPreflight() // {"preflightToken":"x"} → 400 "MemoryErasure.InvalidPreflight"
```

`MemoryErasureAdministrationTests` holds the service-level tests (`[Collection("Grimoire")]`, R33):
- It uses `SagaStoreHarness.CreateAsync(annalsEnabled: true, keyring)`, where `keyring` is a `MemoryErasureKeyring` over an `InMemoryOsCredentialStore`.
- Memories are inserted through `harness.Store.InsertAsync`.
- Erasures run through `SagaMemoryErasureService`'s production constructor (Task 13) over `harness.Context`, with these collaborators:
  - `keyring` as both key ports;
  - `new MemoryReviewTokenCodec(time)`;
  - `new MemoryErasureScrubber(FixtureOrdinaryConnectionFactory.For(harness.Context))`;
  - `new RecordingCovenantOperationGate()`;
  - `CovenantErasureAuthorityFixture.Issuer(authority)`;
  - `CovenantSqliteConnectionInitializer.Instance`;
  - a `TestOptionsMonitor<ArcanumSettings>` with the harness's 64-dimension embedding settings.

  Applies pass `CovenantErasureAuthorityFixture.OperatorContext(authority)`. The memories carry no label, so the gate is never reached.
- A reader is held by `harness.CreateSiblingContext()`'s connection running `BEGIN; SELECT count(*) FROM saga_memories;`.
- `admin` is `new MemoryErasureAdministration(harness.Context, keyring, codec, scrubber, NullLogger<MemoryErasureAdministration>.Instance)`.

```csharp
[SkippableFact] public async Task Scrub_upgrades_only_receipts_whose_only_reason_was_the_wal()
{   // hold the reader; erase m1 → Local.PendingReasons == [WalCheckpointPending], WalCheckpointAttempt Busy
    // create the FTS5 stand-in; erase m2 → PendingReasons == [WalCheckpointPending, VectorIndexScrubUnverified]
    // release the reader
    Assert.Equal(new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Truncated, 1, 1), (await admin.ScrubAsync(Token)).Value);
    // receipts: m1 (ScrubStateCode 2, mask 0); m2 (1, VectorIndexScrubUnverified bit only); status PendingScrubReceipts == 1 }
[SkippableFact] public async Task A_busy_scrub_changes_nothing()
    // hold the reader; erase m1 (pending, WAL only); scrub while the reader is still held → (Busy, 0, 1); m1 still (1, WAL bit)
[SkippableFact] public async Task Scrub_never_clears_a_receipt_committed_after_its_pending_snapshot()
    // erase m1 under a held reader (pending); release it; admin built through the internal constructor with a checkpoint delegate that,
    // before returning Truncated, holds a reader and completes a second erase m2 (its own checkpoint Busy)
    // → (Truncated, Verified 1, StillPending 1); m2's receipt still carries WalCheckpointPending
[SkippableFact] public async Task Scrub_and_reset_key_refuse_below_core_13_and_status_reports_zeros()
    // EvolutionScratchDatabase + CoreSchemaVersionTwelveFixture.ChainSet(); db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);
    // a keyring over a SecretAccessRecordingCredentialStore
    // ScrubAsync, PrepareKeyResetAsync and ResetKeyAsync(new("any")) → each Error.Code == "MemoryErasure.Unavailable"
    // GetStatusAsync → success: KeyStatus Absent, every store (0,0,0), PendingScrubReceipts 0; TryGetCount(Account) == 0
```

`MemoryErasureEvidenceTests.Wal_pending_ids_are_read_in_order_and_only_while_the_wal_bit_is_set`:
- Seed `r1` with mask `1`, `r2` with mask `1|2`, and `r3` Verified.
- `ReadWalPendingAsync` returns `[r1, r2]` in `MutationId` order. After `ClearWalPendingAsync(null)` it returns `[]`.
- On the `"v13-recorded-as-12"` catalog of Task 6 it returns `[]`.

`MemoryErasureKeyringTests.Presence_probe_reads_no_secret_and_leaves_the_latch_alone`:
- Task 2's `CountingCredentialStore` gains `IOsCredentialPresenceProbe`, forwarding to its inner store and counting probes.
- With a stored key: `ProbePresence() == Ok`, `TryGetCount == 0`, and `Latch.State == Unresolved`.
- Without one: `NotFound`.
- A `ScriptedCredentialStore`, which has no probe, gives `null`.
- A throwing probe gives `Unavailable`.
- A disposed keyring gives `Unavailable`.

`MemoryErasureKeyLossEndToEndTests.Deleting_the_key_while_rows_exist_fails_closed_until_reset_key` covers §19.2 #7. It uses a restartable profile, Covenant enabled, and one shared `InMemoryOsCredentialStore inner` wrapped by one `SecretAccessRecordingCredentialStore`. Every host is `MemoryErasureRouteDriver.Host(inner, profile, covenant: true).WithRecordingCredentials(credentials)`. It runs with `</dev/null`.
1. Host 1:
   - insert and erase a Saga memory;
   - upsert Lexicon "Vault Keeper" through `ILexiconService.UpsertAsync`, then erase it;
   - `driver.SetCovenantAsync`, then `driver.EraseCovenantAsync` for Covenant `preference.vault` in Campaign C.

   Dispose the host.
2. `credentials.Delete(Service, Account)`, then start host 2. The warm-up latches `Absent`.
3. Status reports `Lost`, with Covenant (1,1,1), Saga (1,1,1) and Lexicon (1,1,1).
4. `SagaExtractionService.ExtractForSessionAsync` over a Session with fresh entries returns `SagaExtractionOutcome.DeferredForErasureKey`. The fake intelligence records 0 extraction calls, and the Session watermark is unchanged.
5. The writers fail closed:
   - `ILexiconService.UpsertAsync("Harbor Master", "Place", ["alpha"], LexiconScope.Global)` has `Error.Code == "MemoryErasure.KeyLost"`;
   - an agent proposal of `preference.harbor` in Campaign C is refused with `MemoryErasure.KeyLost` (R16). It is `CovenantMutationFixture.AgentPropose(C, "preference.harbor", …)` applied to the host's `CovenantMutationKernel` under `ICovenantOperationGate.AcquireWriteAsync(CovenantOperationScope.ForCampaign(C))`, in a serializable transaction, with `kernel.CaptureErasureGate()`, as `CovenantErasureRestartTests` does;
   - `covenant_versions` holds no row for `preference.harbor`.
6. Reset-key prepare returns `(Lost, …)` with every store's `Unverifiable` equal to 1. Apply returns `(Present, 3, 3, KeyCreated true)`.
7. The writers are unblocked:
   - extraction returns `Completed`, and the fake intelligence records exactly 1 call;
   - `UpsertAsync("Vault Keeper", …)` in its erased scope succeeds, because the discarded erasures are re-learnable (§5.7);
   - the `preference.harbor` proposal is accepted, and one Proposed version exists;
   - status is `Present`, with every store at (0,0,0).

Route inventory edits (R13):
- `MemoryErasureRouteInventoryTests.Routes` gains four rows:
  - `("GetMemoryErasureStatus", "GET", "/api/memory/erasure", null)`
  - `("ScrubMemoryErasures", "POST", "/api/memory/erasure/scrub", CovenantAuthorityRequirement.LifecycleManage)`
  - `("PrepareMemoryErasureKeyReset", "POST", "/api/memory/erasure/reset-key/prepare", CovenantAuthorityRequirement.LifecycleManage)`
  - `("ResetMemoryErasureKey", "POST", "/api/memory/erasure/reset-key", CovenantAuthorityRequirement.LifecycleManage)`
- Add the test `The_status_route_is_a_GET_with_no_operator_authority_that_still_requires_authentication`. The route has no `CovenantAuthorityRequirementMetadata`. An unauthenticated GET answers 401, and an authenticated one answers 200 with the tuple.
- The protected-header theory rows expect 200 with the tuple for the status GET, scrub and reset-key prepare. For reset-key with `{}`, they expect 400 `Validation.InvalidBody`.

`MemoryErasureEvidenceDeleterTests.AllowedCallers` gains `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureAdministration.cs`, the only caller of `DeleteUnverifiableAsync`. `MemoryErasureStructuralTests.ContentFreeLogFiles` gains `Memory/MemoryErasureAdministration.cs`.

- [ ] **Step 2: Declare the contract surface, then run the tests to verify they fail**

Declare these first:
- `IMemoryErasureAdministration`;
- `MemoryErasureAdministration`, with both constructors and all four methods throwing `NotImplementedException`;
- `MemoryErasureEvidence.ReadWalPendingAsync` and `MemoryErasureKeyring.ProbePresence`, both throwing `NotImplementedException`;
- `SecretAccessRecordingCredentialStore`, complete, because it is test support.

Map no route and register nothing.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureAdministrationEndpointTests|FullyQualifiedName~MemoryErasureAdministrationTests|FullyQualifiedName~MemoryErasureKeyLossEndToEndTests|FullyQualifiedName~MemoryErasureRouteInventoryTests|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~MemoryErasureEvidenceTests.Wal_pending|FullyQualifiedName~MemoryErasureKeyringTests.Presence_probe" </dev/null
```

Expected RED:
- The four routes answer 404, so every endpoint test and the inventory tests fail.
- Every service-level test and the two new unit tests throw `NotImplementedException`.
- The end-to-end test fails at step 3, where the status route answers 404.
- The deleter allow-list test fails, because the named file does not yet call `DeleteUnverifiableAsync`.

- [ ] **Step 3: Implement**

- **Status** (§5.7, read-only):
  1. Open the scoped connection. Below v13, the evidence is empty (§5.4 reader rule).
  2. With no fingerprint or receipt rows, the key status comes from `keyring.ProbePresence()`, which reads no secret: `Ok` → `Present`, `NotFound` → `Absent`, `Unavailable` or `Failed` → `Unavailable`. When the probe returns `null`, use `OpenExisting(Reprobe)`: `Present` → `Present`, `Absent` → `Absent`, anything else → `Unavailable`.
  3. With rows, call `OpenExisting(Reprobe)`, the operator re-probe, which publishes into the latch:
     - `Present` → `CountAsync(key.KeyId)`, and the status is `Present`.
     - `Absent` → `CountAsync(null)`, where every row is unverifiable. The status is `Lost` when any fingerprint exists, otherwise `Absent`.
     - `Unresolved`, `Unavailable` or `Malformed` → `Unavailable`. The counts come from `CountAsync(null)`, but `Unverifiable` is reported as 0, meaning unknown; the CLI says so.
  4. `PendingScrubReceipts` comes from the counts. Dispose the key.
- **Scrub:**
  1. Below v13, return `MemoryErasure.Unavailable`.
  2. Read `ReadWalPendingAsync` with no transaction open, before the checkpoint. If it is empty, return `(NotAttempted, 0, PendingScrubReceipts)`.
  3. Otherwise `await checkpoint(ct)`, which in production is `MemoryErasureScrubber.CheckpointAsync`.
  4. Only on `Truncated`: under `SqliteBusyRetry`, one `BEGIN IMMEDIATE` runs `ClearWalPendingAsync(id)` for each snapshot id. `Verified` is the sum.
  5. Re-count to get `StillPending`. Log the attempt and the two counts only.
- **Prepare reset:**
  1. Below v13, return `Unavailable`.
  2. Call `OpenExisting(Reprobe)`. `Unresolved`, `Unavailable` or `Malformed` return `KeyUnavailable`. `Present` counts under its `KeyId`. `Absent` counts with `null` and reports `Lost` when fingerprints exist, otherwise `Absent`.
  3. Issue `IssueErasureKeyReset(new(KeyStatus, Σ Stores.Unverifiable, UnverifiableReceipts))`. The DTO's `IssuedAtUtc`, `ExpiresAtUtc` and `PreflightToken` are the returned `MemoryErasureIssuedToken`'s (five minutes, Task 12).
- **Apply reset** (§5.7):
  1. If `ReadErasureKeyReset` fails, return `InvalidPreflight`. Below v13, return `Unavailable`.
  2. Outside any transaction, call `OpenExisting(Reprobe)`:
     - `Unresolved`, `Unavailable` or `Malformed` → `KeyUnavailable`. Nothing is deleted and nothing is written to the keychain.
     - `Absent` → the token's status must be `Lost` or `Absent`, otherwise `StalePlan` with no create. Then `CreateForReset()` must return `Present` (otherwise `KeyUnavailable`), and `KeyCreated = true`.
     - `Present` → keep the key.
  3. Inside `SqliteBusyRetry` and `BEGIN IMMEDIATE`, re-measure the unverifiable fingerprints and receipts under the current key:
     - `(0, 0)` → commit nothing and succeed with zero discards. This is what makes the verb naturally idempotent.
     - a mismatch with the token's counts → `StalePlan`;
     - otherwise `DeleteUnverifiableAsync(key.KeyId)`, then commit.
  4. There is no `MutationId` and no receipt. The log line carries the counts only.
- **Routes:**
  - `GET /api/memory/erasure` (`GetMemoryErasureStatus`): authentication only, no authority metadata.
  - `POST /api/memory/erasure/scrub` (`ScrubMemoryErasures`, no body).
  - `POST /api/memory/erasure/reset-key/prepare` (`PrepareMemoryErasureKeyReset`, no body).
  - `POST /api/memory/erasure/reset-key` (`ResetMemoryErasureKey`, which reads `MemoryErasureKeyResetRequest` through `ApiRequestJson.ReadAsync`; a null body or token is `Validation.InvalidBody`).
  - The three POSTs carry `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.LifecycleManage)`.
  - Every handler calls `CovenantProtectedResponseHeaders.Apply(context.Response)` and writes with explicit `JsonTypeInfo` (`ApiResponseMemoryErasureStatusDto`, `…ScrubResultDto`, `…KeyResetPreflightDto`, `…KeyResetResultDto`).
- **DI.** Add `services.AddScoped<IMemoryErasureAdministration, MemoryErasureAdministration>();` in `AddArcanumInfrastructure`, beside the release registration.
- **Docs.**
  - API §1: four rows.
  - API §8.35 "Status, scrub and key reset": key states; per-store counts and "unverifiable"; the presence probe; scrub semantics (`NotAttempted` when nothing is pending); the reset-key token, idempotency, `StalePlan`, `KeyUnavailable` and the malformed-item remedy; no issue numbers.
  - DEBUGGING recipe 32, "Recover from a lost or replaced erasure key" (R26):
    - symptoms: `MemoryErasure.KeyLost` or `KeyUnavailable` from scribe, release or proposals, and extraction deferring;
    - `arcanum memory erasure status`;
    - the difference between `Lost`, `Unavailable` and a malformed item, and removing a malformed item with the OS credential tool;
    - `arcanum memory erasure reset-key`, and the relearning consequence;
    - the breakpoints `MemoryErasureAdministration.ResetKeyAsync` and `MemoryErasureKeyring.CreateForReset`.

    Task 24's restore-code recipe follows it.
- **Inventories.**
  - Five acquisition rows, `ProviderOpen`, `LiveGrimoire`/`ServingRawOrdinary`/`OrdinaryConnectionFactory`, null proof: `GetStatusAsync(1)`, `ScrubAsync(1)` and `PrepareKeyResetAsync(1)`, each `"OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)"`; `ResetKeyAsync(2)`, the same; and `OpenConnectionAsync(1)`, `"db.Database.OpenConnectionAsync", 1, "db.Database.OpenConnectionAsync(cancellationToken)"`.
  - `git add` the two new `src` files, then update the catalog and both digest sites (R20).

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureKeyringTests|FullyQualifiedName~MemoryErasureKeyCustodyTests|FullyQualifiedName~MemoryErasureEvidenceTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~DiWiringSmokeTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~EfNativeAotBoundaryTests" </dev/null
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Read the key through `OpenExisting` when no evidence rows exist | `Status_on_a_fresh_installation…` (`TryGetCount == 1`) |
| Report `Absent` instead of `Lost` when fingerprints exist without a key | `Reset_key_creates_the_key…` (prepare) and the end-to-end step 3 |
| Make `ProbePresence` publish into the latch | `Presence_probe_reads_no_secret_and_leaves_the_latch_alone` |
| Pass `null` to `ClearWalPendingAsync` instead of the snapshot ids | `Scrub_never_clears_a_receipt_committed_after_its_pending_snapshot` |
| Drop the `Truncated` condition | `A_busy_scrub_changes_nothing` |
| Clear every pending bit, ignoring the non-upgradable mask (`ScrubPendingReasonMask = 0`) | `Scrub_upgrades_only_receipts_whose_only_reason_was_the_wal` |
| Remove the v13 gate from `ScrubAsync` | `Scrub_and_reset_key_refuse_below_core_13…` |
| Call `DeleteUnverifiableAsync` with an all-zero key id (deletes every row) | `Reset_key_discards_only_rows_under_a_foreign_key…` |
| On `Unavailable`, fall through to the delete under an all-zero key id | `Reset_key_refuses_an_unavailable_key_and_changes_nothing` |
| Skip the in-transaction count compare | `Reset_key_apply_with_counts_that_no_longer_match_is_StalePlan` |
| Skip the pre-create state compare | `Reset_key_whose_key_state_no_longer_holds_is_StalePlan` (a key is minted) |
| Return `StalePlan` when the re-measure is `(0, 0)` | `Reset_key_is_idempotent` |
| Drop `CreateForReset` on `Absent` | `Reset_key_creates_the_key…` and the end-to-end `KeyCreated` assertion |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Memory/IMemoryErasureAdministration.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureAdministration.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryErasureEndpoints.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs docs/Arcanum.API.md docs/Arcanum.DEBUGGING.Human.md tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/SecretAccessRecordingCredentialStore.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/MemoryErasureAdministrationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureAdministrationTests.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryErasureKeyLossEndToEndTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryErasureRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureEvidenceTests.cs tests/RetroDownfall.Arcanum.Tests/Security/MemoryErasureKeyringTests.cs
git commit -m "feat: report erasure status, finish WAL scrubs, and reset a lost erasure key" -m "Adds GET /api/memory/erasure and the LifecycleManage scrub and reset-key prepare/apply routes; reset-key discards only evidence the current key cannot verify, and the debugging guide gains the key-loss recovery recipe." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 19: CLI erase verbs and disclosure rendering

**Files:**
- Create: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.MemoryErasure.cs`; `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Erasure.cs`; `src/RetroDownfall.Arcanum.Cli/UX/MemoryErasureRenderer.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs` (the primary constructor gains a required `IOptions<ArcanumSettings> settings` before `resourceCatalog`); `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs` (`Erase`; the constructor gains a trailing `IOptions<ArcanumSettings> settings`)
- Modify: `src/RetroDownfall.Arcanum.Cli/UX/CovenantExternalRetentionDisclosureWriter.cs` (`WriteErasure`, `WriteHelpTargets`, and `Write` uses the latter); `src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs` (the help-target loop at :534-543 becomes `new CovenantExternalRetentionDisclosureWriter(dispatcher, settings).WriteHelpTargets()`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantExternalRetentionDisclosure.cs` (`DestructiveOperationText`)
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs` (`MemoryErasureCancellationPayload`, `CliJsonContext`); `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs` (`memory saga erase`, `memory lexicon erase`); `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs` (`memory covenant erase`); `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs`
- Modify: `docs/Arcanum.Command.Reference.md`:
  - three erase rows: two in the memory family table near :596-625, and the Covenant row after the `unmask` row at :694;
  - the heading at :679, which becomes "eleven direct verbs";
  - the verb list in the :681 paragraph, which gains `erase` and "Eleven".

  Also `docs/Arcanum.CommandMap.json` (regenerated).
- Test (create): `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryErasureCommandTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientMemoryErasureTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantExternalRetentionDisclosureWriterTests.cs`
- Test (modify):
  - `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantExternalRetentionDisclosureTests.cs` (golden);
  - `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs` (count 11; `NumberWord` gains `11 => "eleven"`);
  - `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs` (`Memory_has_no_generic_delete_command`);
  - the constructor sites `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryReviewCommandTests.cs` and `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs`.

No `src/RetroDownfall.Arcanum.{Core,Infrastructure,Secrets}` file is created, so the benchmark catalog does not change.

**Interfaces:**
- Consumes:
  - the six erase routes and their request and response types (Tasks 13, 14 and 16);
  - `ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto`, `ApiResponseMemoryErasureResultDto` and `MemoryErasureResultDto` (Task 1);
  - existing: `ArcanumApiClient.ShowSagaMemoryAsync` (`GET api/memory/saga/{id}`), `ShowLexiconAsync` (`POST api/memory/lexicon/show`), `ShowCovenantAsync` (`POST api/memory/covenant/detail`, whose `CovenantDetailDto` already carries `EntryId` and each head's `VersionId` and `LaneRevision`), `CliFailureExit.ExitCode`, `CovenantCommands.Fail(Error, CliExitCode)`, `MemoryCommands.WriteError`, `IConfirmationPrompt.PromptForConfirmationAsync`, `CliTestHarness.RunAsync(services, args, input)`.
- Produces:
  - `ArcanumApiClient.PrepareSagaErasureAsync`, `EraseSagaMemoryAsync`, `PrepareLexiconErasureAsync`, `EraseLexiconEntryAsync`, `PrepareCovenantErasureAsync` and `EraseCovenantEntryAsync`, each `→ Task<Result<MemoryErasurePreflightDto | MemoryErasureResultDto>>`.
  - `MemoryCommands.SagaErase(string id, string? expectedContentHash, CancellationToken)`, `MemoryCommands.LexiconErase(string name, Guid? campaignId, CancellationToken)` and `CovenantCommands.Erase(string key, Guid? campaignId, CancellationToken)`.
  - `CovenantExternalRetentionDisclosureWriter.WriteErasure(MemoryErasureExternalExposureDto)` and `WriteHelpTargets()`.
  - `MemoryErasureRenderer.WritePreflight(IConsoleDispatcher, MemoryErasurePreflightDto, bool json)` and `WriteResult(IConsoleDispatcher, MemoryErasureResultDto)`.
  - `public sealed record MemoryErasureCancellationPayload(string Operation, MemoryReviewStore? Store, Guid? MutationId, bool Cancelled)`, where `Operation` is `"erase"`, `"release"` or `"reset-key"`.

- [ ] **Step 1: Write the failing tests**

`CovenantExternalRetentionDisclosureTests.Disclosure_constants_match_the_approved_golden_copy` now expects `"Local disable, reset, selective erasure, protected-state purge, family reinitialize, and factory erasure cannot revoke …"`, with the rest of the sentence unchanged.

`CovenantExternalRetentionDisclosureWriterTests` (the settings configure no providers unless a test says otherwise):

```csharp
[Fact] public void WriteErasure_writes_the_sentence_then_one_line_per_channel_then_help_targets_to_diagnostics_only()
{   writer.WriteErasure(new MemoryErasureExternalExposureDto(MemoryExternalRevocation.NotPerformed, [
        new(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known),
        new(MemoryExternalChannel.InferenceProviderContext, MemoryExternalEvidence.NotRecorded),
        new(MemoryExternalChannel.EmbeddingProvider, MemoryExternalEvidence.NotApplicable),
        new(MemoryExternalChannel.EncryptedBackup, MemoryExternalEvidence.ReceiptWindow),
        new(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded)]));
    Assert.Equal([
        CovenantExternalRetentionDisclosure.DestructiveOperationText,
        "Arcanum does not revoke copies that already left this machine:",
        "  Inference provider that wrote it: known — this content was sent there at least once.",
        "  Inference providers that read it in a turn: not recorded — Arcanum keeps no record for this channel, so a send cannot be ruled out.",
        "  Embedding provider: not applicable — this store never sends content on this channel.",
        "  Encrypted backups: possible — Arcanum recorded a send while this item existed.",
        "  Other external copies: not recorded — Arcanum keeps no record for this channel, so a send cannot be ruled out.",
        "  Retention guidance: docs/Arcanum.Engineering.md#covenant-provider-retention-and-deletion"], dispatcher.Diagnostics);
    Assert.Empty(dispatcher.Payloads); }
[Theory] // every (channel, evidence) pair, one channel list per MemoryExternalEvidence value
public void WriteErasure_never_says_not_disclosed_and_prints_no_count(MemoryExternalEvidence evidence)
{   Assert.DoesNotContain(dispatcher.Diagnostics, line => line.Contains("not disclosed", StringComparison.OrdinalIgnoreCase));
    Assert.DoesNotMatch(@"\d", string.Join('\n', dispatcher.Diagnostics)); }   // no providers configured, so no URI carries a digit
[Fact] public void Write_and_backup_restore_share_WriteHelpTargets()
{   // settings: Providers = [new ProviderSettings { Name = "openai", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1" }]
    Assert.Equal(HelpLines(afterWrite), HelpLines(afterWriteHelpTargets));   // HelpLines = lines starting "  Retention guidance"
    Assert.Contains(HelpLines(afterWrite), line => line.StartsWith("  Retention guidance (", StringComparison.Ordinal)); }
```

`MemoryErasureCommandTests`:
- It uses the registered tree through `CliTestHarness.RunAsync`, and a recording handler whose `Events` list holds `"<METHOD> <path>"`.
- A recording prompt appends `"prompt"` to the same list and snapshots stdout and stderr into `BeforePrompt` when asked.
- `SagaId = "7c9e6679-7425-40de-944b-e07fc1f90ae7"`.

```csharp
[Fact] public async Task Saga_erase_shows_prepares_discloses_prompts_then_applies()
{   Assert.Equal([$"GET /api/memory/saga/{SagaId}", "POST /api/memory/saga/erase/prepare", "prompt", "POST /api/memory/saga/erase"], handler.Events);
    // prepare body: memoryId SagaId, expectedContentHash == show.ContentHash, expectedClaimVersionId == show.Claim.CurrentVersionId, mutationId != Guid.Empty
    // apply body: the same four fields plus preflightToken "token-1"
    // BeforePrompt, in order: DestructiveOperationText, "Embedding provider: known", "Retention guidance"
    // stdout contains the MutationId and "No other memory store was touched." }
[Fact] public async Task Saga_erase_sends_an_explicit_expected_content_hash_instead_of_the_shown_one()
    // --expected-content-hash 00..0A (64 hex) → prepare and apply bodies carry that value; show.ContentHash appears in neither
[Fact] public async Task Saga_erase_prompt_names_the_twin_count()
    // Plan.ErasedItemCount 3 → the prompt question starts "Erase 3 Saga memories with identical content in this scope?" and ends "This cannot be undone."
[Theory, InlineData(null), InlineData("12345678-1234-1234-1234-123456789abc")]
public async Task Lexicon_erase_forwards_the_shown_target_byte_for_byte(string? campaign)
    // Events ["POST /api/memory/lexicon/show", "POST /api/memory/lexicon/erase/prepare", "prompt", "POST /api/memory/lexicon/erase"];
    // the prepare body's "target" raw JSON equals the show response's "target" raw JSON after a round trip through ArcanumJsonContext
[Fact] public async Task Covenant_erase_forwards_the_entry_and_both_head_expectations()
    // show: EntryId E, Confirmed (V1, 3), Proposed (V2, 1) → prepare body entryId E, confirmed {versionId V1, laneRevision 3}, proposed {versionId V2, laneRevision 1}
[Fact] public async Task Covenant_erase_of_a_key_with_no_entry_stops_before_prepare()
    // EntryId null → exit 1; Events == ["POST /api/memory/covenant/detail"]; stderr contains "has no entry to erase"
[Fact] public async Task Covenant_erase_prompt_states_the_drain_cost()   // R29, §9.2
    // Notes contain CovenantDrainsInFlightTurns → BeforePrompt contains "Erasing drains in-flight Covenant turns first: it waits up to 30 seconds for them to finish,
    //   turns that start meanwhile run without Covenant content, and backups, prepares and inventory fail fast until it completes."
[Theory, InlineData(true), InlineData(false)]
public async Task Covenant_erase_warns_that_reclaiming_the_key_stales_outstanding_preflights(bool reclaims)   // R29, §9.3
    // Plan.Covenant.ReclaimsKey == reclaims → BeforePrompt contains (reclaims) / does not contain (!reclaims)
    //   "This erase reclaims the key, so every outstanding Covenant preflight on this installation goes stale and must be prepared again."
[Theory, InlineData("saga"), InlineData("lexicon"), InlineData("covenant")]
public async Task Decline_writes_one_cancellation_document_and_exits_zero(string store)
{   // --json, the prompt declines
    Assert.Equal(0, result.ExitCode);
    MemoryErasureCancellationPayload payload = JsonSerializer.Deserialize(result.Output, CliJsonContext.Default.MemoryErasureCancellationPayload)!;
    Assert.Equal(("erase", Store(store), true), (payload.Operation, payload.Store, payload.Cancelled));
    Assert.Equal(preparedMutationId, payload.MutationId);
    Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal)); }
[Theory, InlineData("saga"), InlineData("lexicon"), InlineData("covenant")]
public async Task Yes_with_json_writes_exactly_one_result_document(string store)
    // --json --yes: JsonDocument.Parse(result.Output) succeeds with one root; root.mutationId == the prepared id; stderr holds DestructiveOperationText
[Theory, InlineData("saga"), InlineData("lexicon"), InlineData("covenant")]
public async Task Non_interactive_without_yes_exits_two_and_never_applies(string store)
    // the real ConfirmationPrompt with redirected stdin → exit 2; no event ends with "/erase"
[Fact] public async Task Scrub_pending_result_names_the_reasons_and_the_scrub_verb()
    // Local RowsRemovedScrubPending, PendingReasons [WalCheckpointPending], attempt Busy → stdout contains "scrub pending (WAL checkpoint pending)"
    // and "Run 'arcanum memory erasure scrub' to finish."
[Fact] public async Task Host_refusal_exits_one_and_an_unreachable_host_exits_three()
    // apply answers 409 "MemoryErasure.StalePlan" → exit 1, stderr contains the server message; a handler that throws HttpRequestException on prepare → exit 3, no prompt
```

`ArcanumApiClientMemoryErasureTests`: each of the six methods POSTs its exact path (`api/memory/saga/erase/prepare`, `api/memory/saga/erase`, `api/memory/lexicon/erase/prepare`, `api/memory/lexicon/erase`, `api/memory/covenant/erase/prepare`, `api/memory/covenant/erase`). Each body round-trips through its `ArcanumJsonContext` type info, and a canned `ApiResponse` envelope decodes to the expected DTO (`Assert.Equal` on `MutationId` and `EffectDigest`).

`MemoryCommandTests.Memory_has_no_generic_delete_command` becomes `[Theory, InlineData("delete"), InlineData("erase")]`, and each verb answers exit 2 with no requests. `CliSurfaceTests.The_covenant_heading_…` asserts `11`, and `NumberWord` gains `11 => "eleven"`.

- [ ] **Step 2: Declare the contract surface, then run the tests to verify they fail**

Declare these first, each throwing `NotImplementedException`:
- the six `ArcanumApiClient` methods;
- `WriteErasure` and `WriteHelpTargets`;
- `MemoryErasureRenderer`.

Also add `MemoryErasureCancellationPayload` with its `CliJsonContext` registration, and the two constructor parameters (with their two test sites). Register no verb.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureCommandTests|FullyQualifiedName~ArcanumApiClientMemoryErasureTests|FullyQualifiedName~CovenantExternalRetentionDisclosureWriterTests|FullyQualifiedName~CovenantExternalRetentionDisclosureTests|FullyQualifiedName~MemoryCommandTests|FullyQualifiedName~CliSurfaceTests" </dev/null
```

Expected RED:
- `erase` is not a registered verb, so every erase command test exits 2.
- The client and writer tests throw `NotImplementedException`.
- The golden copy differs.
- The Covenant count is 10.
- The new `Memory_has_no_generic_delete_command("erase")` row passes on arrival. It pins the negative, and it stays green after registration because `erase` exists only under `saga`, `lexicon` and `covenant`.

- [ ] **Step 3: Implement**

- **Flow** (§17.2), identical in all three handlers:
  1. show;
  2. prepare with `Guid.CreateVersion7()`;
  3. `MemoryErasureRenderer.WritePreflight`, on the payload stream in human mode and on diagnostics under `--json`;
  4. `WriteErasure(preflight.External)`;
  5. `confirmationPrompt.PromptForConfirmationAsync(question, ct)`;
  6. apply with the identical fields plus `PreflightToken`;
  7. `WriteResult` in human mode, or `dispatcher.WriteJson(result, ArcanumJsonContext.Default.MemoryErasureResultDto)`.

  Show output never reaches stdout.
- **Targets.**
  - Saga uses `--expected-content-hash ?? detail.ContentHash` and `detail.Claim?.CurrentVersionId`.
  - Lexicon forwards `detail.Target` unchanged.
  - Covenant builds a `CovenantEraseHeadExpectation(head.VersionId, head.LaneRevision)` for each present head. A null `EntryId` stops with exit 1: "Covenant key '<key>' has no entry to erase in this scope."
- **Prompt questions.** Each ends with "This cannot be undone.":
  - Saga: "Erase {n} Saga memories with identical content in this scope?", or the singular form when n is 1.
  - Lexicon: "Erase the Lexicon entry '{name}' in the {scope} scope?"
  - Covenant: "Erase every version in both lanes of '{key}' in the {scope} scope?"
- **Covenant availability text** (R29), written by `WritePreflight` before the prompt:
  - the drain sentence above whenever `Notes` contains `CovenantDrainsInFlightTurns`;
  - the reclaim sentence whenever `Plan.Covenant.ReclaimsKey`.
- **Decline.** Write "`<Store>` erasure cancelled." to stderr. Under `--json`, also write `MemoryErasureCancellationPayload("erase", store, mutationId, true)`. Exit 0.
- **Failures.** `MemoryCommands.WriteError` (`CliFailureExit.ExitCode`); `CovenantCommands.Erase` calls `Fail(error, (CliExitCode)CliFailureExit.ExitCode(error))`.
- **`MemoryErasureRenderer`.**
  - It holds closed maps for `MemoryErasureNote`, `MemoryRetainedLocalCopy`, `MemoryErasureScrubPendingReason` and the plan facts.
  - A pending result prints "rows removed; scrub pending (<reasons>). Run 'arcanum memory erasure scrub' to finish."
  - Every result prints its `MutationId` and "No other memory store was touched."
- **`WriteErasure`** uses the channel labels and evidence phrases pinned above. `Write` and `BackupCommands` call `WriteHelpTargets()`.
  - `MemoryCommands` and `CovenantCommands` build the writer privately from `(dispatcher, settings)`. The writer is `internal` and both command classes are `public`, so a writer constructor parameter would not compile (inconsistent accessibility).
- **Tree.**
  - `memory saga erase <id> [--expected-content-hash <hex>]`. The option is optional here, unlike on `correct`.
  - `memory lexicon erase <name> [--campaign|-C <guid>]`, added directly rather than through `AddLexiconMutation`.
  - `memory covenant erase <key> [--campaign|-C <guid>]`, placed after `retire`.
- **Examples** (R19):
  - `arcanum memory saga erase 7c9e6679-7425-40de-944b-e07fc1f90ae7`
  - `arcanum memory lexicon erase Operator`
  - `arcanum memory covenant erase preference.builds --campaign 5b2e9c41-08d3-4a7f-b6e5-2c1908fa4d77`
- **Command Reference rows.** They describe the flow, the external block, the Covenant drain and reclaim warnings, the shell-history residue of the positional name or key, and the `--json`/decline/exit-2 contract. The Saga row's example uses the GUID above.
- **Regenerate the map:**

```bash
ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSurfaceTests.Committed_command_map_matches_the_live_tree"
```

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSuggestionTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~MemoryReviewCommandTests|FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~MemoryLexiconCurationCommandTests|FullyQualifiedName~MemorySagaCurationCommandTests|FullyQualifiedName~DataRetentionCommandTests|FullyQualifiedName~BackupRestoreProtectedStateCommandTests|FullyQualifiedName~InstallationFactoryResetArgvPreflightTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests" </dev/null
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Prompt before `WriteErasure` | `Saga_erase_shows_prepares_discloses_prompts_then_applies` (ordering) |
| Write the preflight to stdout under `--json` | `Yes_with_json_writes_exactly_one_result_document` |
| Decline returns 1, or writes nothing | `Decline_writes_one_cancellation_document_and_exits_zero` |
| Map `NotRecorded` to "not disclosed" | `WriteErasure_never_says_not_disclosed…` |
| Drop the Proposed expectation | `Covenant_erase_forwards_the_entry_and_both_head_expectations` |
| Send `detail.ContentHash` even when the option is given | `Saga_erase_sends_an_explicit_expected_content_hash…` |
| Drop the drain sentence | `Covenant_erase_prompt_states_the_drain_cost` |
| Print the reclaim warning unconditionally | `Covenant_erase_warns_…(false)` |
| Revert the golden sentence | `Disclosure_constants_match_the_approved_golden_copy` |
| Keep the old help-target loop in `BackupCommands` with a different label | `Write_and_backup_restore_share_WriteHelpTargets` (via `DataRetentionCommandTests` and `BackupRestoreProtectedStateCommandTests` goldens) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.MemoryErasure.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Erasure.cs src/RetroDownfall.Arcanum.Cli/UX/MemoryErasureRenderer.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs src/RetroDownfall.Arcanum.Cli/UX/CovenantExternalRetentionDisclosureWriter.cs src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantExternalRetentionDisclosure.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs docs/Arcanum.Command.Reference.md docs/Arcanum.CommandMap.json tests/RetroDownfall.Arcanum.Tests/Cli/MemoryErasureCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientMemoryErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CovenantExternalRetentionDisclosureWriterTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantExternalRetentionDisclosureTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryReviewCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs
git commit -m "feat: add confirmed erase verbs for Saga, Lexicon and Covenant" -m "Each verb shows, prepares, renders the plan and the non-revocable external exposure, confirms, then applies; the Covenant verb states the drain and key-reclamation costs, and the shared destructive-operation sentence now names selective erasure." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 20: CLI release and `memory erasure` administration verbs

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.MemoryErasure.cs`; `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Erasure.cs` (`SagaRelease`, `LexiconRelease`, `ErasureStatus`, `ErasureScrub`, `ErasureResetKey`); `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs` (`Release`); `src/RetroDownfall.Arcanum.Cli/UX/MemoryErasureRenderer.cs` (status, scrub and reset renderings)
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs` (`memory saga release`, `memory lexicon release`, and the new `memory erasure` group); `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs` (`memory covenant release`); `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs`
- Modify: `docs/Arcanum.Command.Reference.md`:
  - release rows;
  - a `memory erasure` group table;
  - the Covenant heading at :679 becomes "twelve direct verbs", and the :681 list gains `release` and "Twelve";
  - the Covenant `release` row after `erase`.

  Also `docs/Arcanum.CommandMap.json` (regenerated).
- Test (create): `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryErasureAdministrationCommandTests.cs`
- Test (modify): `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientMemoryErasureTests.cs`; `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs` (count 12; `NumberWord` gains `12 => "twelve"`)

No `src/RetroDownfall.Arcanum.{Core,Infrastructure,Secrets}` file is created, so the benchmark catalog does not change.

**Interfaces:**
- Consumes:
  - the release routes and `CovenantErasureReleaseRequest` (Task 17);
  - the status, scrub and reset-key routes (Task 18);
  - `MemoryErasureCancellationPayload` and `MemoryErasureRenderer` (Task 19);
  - existing: `AuthoredContentReader.ReadAsync(string? file, string subject, string? emptyContentRemedy, CancellationToken)`, `ExactLexiconScope(Guid?)`, `CliInvocationContext.Current.Yes`, `CliFailureExit`.
- Produces:
  - `ArcanumApiClient.ReleaseSagaErasureAsync(SagaErasureReleaseRequest, ct)`, `ReleaseLexiconErasureAsync(LexiconErasureReleaseRequest, ct)` and `ReleaseCovenantErasureAsync(CovenantErasureReleaseRequest, ct)`, each `→ Task<Result<MemoryErasureReleaseResultDto>>`.
  - `GetMemoryErasureStatusAsync(ct)`, `ScrubMemoryErasuresAsync(ct)`, `PrepareMemoryErasureKeyResetAsync(ct)` and `ResetMemoryErasureKeyAsync(MemoryErasureKeyResetRequest, ct)`.
  - Handlers:
    - `SagaRelease(string file, Guid? campaignId, string? scope, CancellationToken)`;
    - `LexiconRelease(string name, Guid? campaignId, CancellationToken)`;
    - `CovenantCommands.Release(string key, Guid? campaignId, CancellationToken)`;
    - `ErasureStatus`, `ErasureScrub` and `ErasureResetKey`, each `(CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

`MemoryErasureAdministrationCommandTests` uses the registered tree, a recording handler, and the recording prompt shape of Task 19. `C = "12345678-1234-1234-1234-123456789abc"`.

```csharp
[Theory]
[InlineData(new string[0], SagaMemoryScopeKind.Global, null)]
[InlineData(new[] { "--scope", "unresolved" }, SagaMemoryScopeKind.LegacyUnresolved, null)]
[InlineData(new[] { "--scope", "unclassified" }, SagaMemoryScopeKind.Unclassified, null)]
[InlineData(new[] { "--campaign", C }, SagaMemoryScopeKind.Campaign, C)]
[InlineData(new[] { "--scope", "campaign", "--campaign", C }, SagaMemoryScopeKind.Campaign, C)]
public async Task Saga_release_maps_scope_flags_and_sends_the_file_bytes_verbatim(string[] flags, SagaMemoryScopeKind kind, string? campaign)
{   // --file holds "Rotate the vault key.\n"
    Assert.Equal(["prompt", "POST /api/memory/saga/release"], handler.Events);
    Assert.Equal("Rotate the vault key.\n", body.RootElement.GetProperty("content").GetString());   // trimming is the server's job
    Assert.Equal((int)kind, body.RootElement.GetProperty("scopeKind").GetInt32());
    Assert.Equal(campaign, body.RootElement.GetProperty("campaignId").GetString());
    Assert.DoesNotContain("Rotate the vault key", prompt.BeforePrompt, StringComparison.Ordinal); }   // the content is never rendered
[Theory, InlineData("campaign"), InlineData("orbit")]
public async Task Saga_release_refuses_an_incomplete_or_unknown_scope_with_exit_two(string scope)
    // --scope <scope> without --campaign → exit 2; Events empty; stderr names "--campaign" (campaign) or "global|campaign|unresolved|unclassified" (orbit)
[Fact] public async Task Saga_release_with_campaign_and_a_non_campaign_scope_exits_two()
    // --campaign C --scope global → exit 2; Events empty
[Fact] public async Task Saga_release_from_stdin_without_yes_exits_two_before_reading_or_calling()
    // modelled on MemoryLexiconCurationCommandTests.Literal_dash_without_yes_is_refused_before_reading_or_show:
    // Console.SetIn(a private ForbiddenReader); resolve MemoryCommands; invoke SagaRelease("-", null, null, ct) → 2; Events empty
[Fact] public async Task Saga_release_from_stdin_with_yes_reads_stdin()
    // CliTestHarness.RunAsync(services, ["memory","saga","release","--file","-","--yes"], input: "Rotate.\n") → exit 0; body content "Rotate.\n"; one POST
[Theory]
[InlineData("lexicon", "Vault Keeper", null)] [InlineData("lexicon", "Vault Keeper", C)]
[InlineData("covenant", "preference.vault", null)] [InlineData("covenant", "preference.vault", C)]
public async Task Release_confirms_then_posts_the_exact_scope(string store, string identity, string? campaign)
    // Events ["prompt", $"POST /api/memory/{store}/release"]; BeforePrompt contains "Agents and extraction may write this again once it is released."
    // lexicon body: scope.kind "Global"|"Campaign", scope.campaignId == campaign, name == identity (verbatim)
    // covenant body: scope "Global"|"Campaign", campaignId == campaign, key == identity
[Fact] public async Task NotFingerprinted_exits_zero_and_says_nothing_was_released()
    // (Lexicon, NotFingerprinted, 0) → exit 0; stdout contains "No erasure fingerprint matched, so nothing was released."
[Fact] public async Task Released_prints_the_count()
    // (Saga, Released, 2) → exit 0; stdout contains "Released 2 erasure fingerprints."
[Fact] public async Task A_KeyLost_refusal_exits_one_and_points_to_erasure_status()
    // 409 "MemoryErasure.KeyLost" → exit 1; stderr contains "arcanum memory erasure status"
[Theory]
[InlineData("release-lexicon")] [InlineData("release-covenant")] [InlineData("release-saga")] [InlineData("reset-key")]
public async Task Decline_writes_one_cancellation_document_and_exits_zero(string verb)
    // --json, the prompt declines → exit 0; the one stdout document is MemoryErasureCancellationPayload(verb starts "release" ? "release" : "reset-key",
    //   store for release / null for reset-key, MutationId null, Cancelled true); no POST to a release route or to "/api/memory/erasure/reset-key"
[Fact] public async Task Status_prints_key_state_per_store_counts_and_guidance_when_unverifiable()
    // (Lost, [(Covenant,0,0,0),(Saga,1,1,1),(Lexicon,0,0,0)], 0) → stdout contains "Erasure key: Lost", "Saga: 1 fingerprint, 1 unverifiable, 1 receipt",
    //   and "Run 'arcanum memory erasure reset-key'"; --json → the one document parses as MemoryErasureStatusDto with KeyStatus "Lost"
[Fact] public async Task Status_says_unverifiable_counts_are_unknown_while_the_key_is_unavailable()
    // KeyStatus Unavailable → stdout contains "Unverifiable counts are unknown while the erasure key cannot be read."
[Fact] public async Task Scrub_runs_without_confirmation_and_reports_counts()
    // (Truncated, 1, 0) → Events ["POST /api/memory/erasure/scrub"] (no "prompt"); stdout contains "Verified 1" and "still pending 0"
[Fact] public async Task Reset_key_prepares_warns_about_relearning_confirms_then_applies_the_token()
{   Assert.Equal(["POST /api/memory/erasure/reset-key/prepare", "prompt", "POST /api/memory/erasure/reset-key"], handler.Events);
    Assert.Contains("may be learned again by extraction or agent writes", prompt.BeforePrompt, StringComparison.Ordinal);
    Assert.Equal("reset-token", body.RootElement.GetProperty("preflightToken").GetString());
    // apply (Present, 2, 2, true) → stdout contains "Discarded 2 fingerprints and 2 receipts; created a new erasure key." }
[Fact] public async Task Reset_key_non_interactive_without_yes_exits_two_and_never_applies()
    // the real ConfirmationPrompt with redirected stdin → exit 2; Events == ["POST /api/memory/erasure/reset-key/prepare"]
[Fact] public async Task Reset_key_with_json_and_yes_writes_exactly_one_result_document()
    // --json --yes → JsonDocument.Parse(stdout) has one root that deserializes as MemoryErasureKeyResetResultDto; the preflight rendering is on stderr
[Fact] public async Task Reset_key_stops_before_confirming_when_prepare_refuses()
    // prepare → 503 "MemoryErasure.KeyUnavailable" → exit 1; Events == ["POST /api/memory/erasure/reset-key/prepare"]; no "prompt"
```

`ArcanumApiClientMemoryErasureTests` gains:
- the three release POSTs (`api/memory/saga/release`, `api/memory/lexicon/release`, `api/memory/covenant/release`), each body round-tripping through its type info;
- `GET api/memory/erasure` with no body;
- `POST api/memory/erasure/scrub` and `POST api/memory/erasure/reset-key/prepare` with an empty body;
- `POST api/memory/erasure/reset-key` with `{"preflightToken":…}`.

`CliSurfaceTests` asserts `12` and "twelve".

- [ ] **Step 2: Declare the contract surface, then run the tests to verify they fail**

Declare the seven client methods, each throwing `NotImplementedException`. Register no verb.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureAdministrationCommandTests|FullyQualifiedName~ArcanumApiClientMemoryErasureTests|FullyQualifiedName~CliSurfaceTests" </dev/null
```

Expected RED:
- `release` and `memory erasure` are unregistered, so the command tests exit 2 where they expect 0 or 1.
- The stdin-without-`--yes` test fails, because `SagaRelease` does not exist.
- The client tests throw `NotImplementedException`.
- The Covenant count is 11.

- [ ] **Step 3: Implement**

- **`memory saga release --file|-f <path|-> [--campaign|-C <guid>] [--scope global|campaign|unresolved|unclassified]`.**
  - `--file` is required.
  - With neither `--scope` nor `--campaign`, the scope is Global. `--campaign` alone means Campaign.
  - `--scope campaign` needs `--campaign`, and any other `--scope` combined with `--campaign` is exit 2. An unknown `--scope` is exit 2 and names the four values.
  - `--file -` without `CliInvocationContext.Current.Yes` is exit 2 before stdin is read.
  - The content comes from `AuthoredContentReader.ReadAsync(file, "Saga", null, ct)` and is sent verbatim.
- **Release flow.**
  1. Render the store, the scope, and the identity kind, never the content. Then print the warning "Agents and extraction may write this again once it is released."
  2. Confirm, then POST.
  3. On the outcome:
     - `Released` prints "Released {n} erasure fingerprint(s).";
     - `NotFingerprinted` prints "No erasure fingerprint matched, so nothing was released." and exits 0;
     - a host refusal goes through `CliFailureExit`, and `KeyLost` adds the hint "Run 'arcanum memory erasure status'.".
- **`memory erasure status`** is read-only. It prints:
  - `Erasure key: <status>`;
  - one line per store, "<Store>: n fingerprint(s), n unverifiable, n receipt(s)";
  - the reset-key guidance when the status is `Lost` or any `Unverifiable` is above 0;
  - the "unknown" sentence when the status is `Unavailable`.
- **`scrub`** runs without a prompt, because it removes nothing. It prints "Scrub: <attempt>. Verified n, still pending n."
- **`reset-key`**:
  1. Prepare. A refusal exits through `CliFailureExit` before any prompt.
  2. Render the key state and the per-store unverifiable counts.
  3. Confirm with "Discarding them means the erasures they recorded may be learned again by extraction or agent writes."
  4. Apply `new MemoryErasureKeyResetRequest(preflight.PreflightToken)`.
  5. Print "Discarded n fingerprints and n receipts" and, when `KeyCreated`, "; created a new erasure key."
- **JSON.** Under `--json`, the API DTO is written through `ArcanumJsonContext` as the one document, and every rendering goes to diagnostics. Declines write `MemoryErasureCancellationPayload` and exit 0.
- **Examples** for all six verbs, including `arcanum memory saga release --file erased.txt --campaign 5b2e9c41-08d3-4a7f-b6e5-2c1908fa4d77`.
- **Command Reference rows.** The `memory erasure` table says:
  - reset-key discards only unverifiable rows and mints a key when it is absent;
  - a malformed item is removed with the OS credential tool first;
  - status reads no secret when there is no evidence.

  Update the Covenant heading at :679 and the list at :681. Regenerate the command map with the Task 19 command.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSuggestionTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~MemoryErasureCommandTests|FullyQualifiedName~MemoryCommandTests|FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~MemoryLexiconCurationCommandTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~DocumentationStructureTests" </dev/null
```

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Trim the file content client-side | `Saga_release_maps_scope_flags_and_sends_the_file_bytes_verbatim` |
| Read stdin before the `--yes` check | `Saga_release_from_stdin_without_yes…` (`ForbiddenReader` throws) |
| Default `--scope unresolved` to Global | the `LegacyUnresolved` row |
| Accept `--campaign` together with `--scope global` | `Saga_release_with_campaign_and_a_non_campaign_scope_exits_two` |
| Render the Saga content before the prompt | the `DoesNotContain` assertion of the scope-flags theory |
| Omit the relearning warning | `Reset_key_prepares_warns_about_relearning…` |
| Treat `NotFingerprinted` as exit 1 | `NotFingerprinted_exits_zero…` |
| Prompt before `scrub` | `Scrub_runs_without_confirmation…` |
| Prompt even when reset-key prepare refuses | `Reset_key_stops_before_confirming_when_prepare_refuses` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.MemoryErasure.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Erasure.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs src/RetroDownfall.Arcanum.Cli/UX/MemoryErasureRenderer.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs docs/Arcanum.Command.Reference.md docs/Arcanum.CommandMap.json tests/RetroDownfall.Arcanum.Tests/Cli/MemoryErasureAdministrationCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientMemoryErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs
git commit -m "feat: add erasure release and key-administration commands" -m "Adds memory saga/lexicon/covenant release and memory erasure status, scrub and reset-key; release reads Saga content from a file or stdin so it never reaches shell history." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 21: Disclosure fold: live producer, effective reader, seed

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateFold.cs` (`ExternalDisclosureFoldOrigin`, `CovenantDisclosureSubject`, `ExternalDisclosureStateFold`)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateReader.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateStore.cs` (bucket `ReadAllAsync`/`ReadAsync`/`WriteAsync`/`Materialize`, moved out of `CovenantDisclosureStateJoiner`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDisclosureStateAlgebra.cs` (add `WeakenToLowerBound`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs` (`CovenantDisclosureTransactionWriter.AcknowledgeWithinTransactionAsync`: fold after `AdvanceSubjectAsync`, before `CommitAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs` (`ReadWithinAsync` sums effective states)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantRestoreStateJoiners.cs` (`CovenantDisclosureStateJoiner` reads and writes buckets through the store and passes `timeProvider.GetUtcNow()`; its `ReadAllAsync`, `ReadBucketAsync`, `WriteBucketAsync` and `Materialize` are deleted)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs` (`BackupCovenantRestoreDestinationState.ReadAsync` reads effective buckets)
- Modify: `docs/Arcanum.DESIGN.md` (§10.13 fold and watermark; §10.20.1 `PossibleDisclosures` is live, `LowerBound` for pre-fold history; the §10.19.10 "folded from the destination's own nonrevocable `external_disclosure_state` buckets" sentence names the effective read), `docs/Arcanum.API.md` (§8.20 `disclosureCountKind` note)
- Modify tests: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionSeed.cs` (`SeedDisclosureAsync`), `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStateJoinerTests.cs` (the two `CovenantDisclosureStateJoiner.ReadAllAsync` calls at :160 and :194 become `ExternalDisclosureStateStore.ReadAllAsync`), `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureJournalTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDisclosureStateAlgebraTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Backup/BackupCovenantRestoreReconcilerTests.cs`
- Create tests: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/PreFoldDisclosureHistory.cs` (writes receipt rows the way a pre-v13 build did: receipt plus a subject row with `LastFoldedOrdinal = 0`, nothing in `external_disclosure_state`), `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/ExternalDisclosureStateReaderTests.cs`
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs` (`Persisted_instant_writers_use_the_central_codec_and_new_writers_require_review`: add `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateStore.cs`; remove `src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantRestoreStateJoiners.cs`, whose only `UtcInstantText.Format(` call, at :312, moves into the store)
  - `tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt` (+3 `R` lines), `grimoire-admission-workload-v1.json` (`inputCatalogShapeDigest`), `AdmissionBenchmarkManifest.cs` (`ExactInputCatalogShapeDigest`, line 19)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (Task 12's R8 scan: `ContentFreeLogFiles` gains the three new files)
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated: `AcknowledgeWithinTransactionAsync` gains capsules)
  - Acquisition inventory: no change. The fold, store and reader run on the caller's connection, so `ExpectedProductionAcquisitionCount` is unchanged (+0).
- No `.sql` file changes (R10). The four head-comment edits, including `external_disclosure_state` and `disclosure_subject_aggregates`, are Task 3's; `idx_disclosure_subject_state_unfolded` ships from Task 3.

**Interfaces:**
- Consumes: Task 3 (`idx_disclosure_subject_state_unfolded`); Task 12 (`MemoryErasureStructuralTests.ContentFreeLogFiles`, the R8 content-free log scan). Existing `CovenantDisclosureStateAlgebra.IncrementLocal`/`CreateEvidenceBloom`, `CovenantDisclosureState.Empty(destination, revocability)`, `CovenantDisclosureReceipt(draft, ordinal)`, `CovenantDigests.Sensitivity`, `UtcInstantText.Parse`/`Format`.
- Produces:
  - `public static CovenantDisclosureState CovenantDisclosureStateAlgebra.WeakenToLowerBound(CovenantDisclosureState state)` (the empty state stays `Exact`).
  - `internal enum ExternalDisclosureFoldOrigin : byte { Live = 1, RestoreStaging = 2 }`
  - `internal readonly record struct CovenantDisclosureSubject(Guid OriginInstallationId, CovenantDisclosureSubjectKind Kind, Guid SubjectId)` with `static From(CovenantDisclosureDraft)`.
  - `ExternalDisclosureStateFold.FoldSubjectTailAsync(SqliteConnection, SqliteTransaction, CovenantDisclosureSubject subject, ExternalDisclosureFoldOrigin origin, CancellationToken) → Task`
  - `ExternalDisclosureStateFold.FoldAllUnfoldedAsync(SqliteConnection, SqliteTransaction, ExternalDisclosureFoldOrigin origin, CancellationToken) → Task<int>` (subjects folded; Task 24 calls it with `RestoreStaging`)
  - `ExternalDisclosureStateReader.ReadEffectiveAsync(SqliteConnection, CancellationToken, Func<CancellationToken, Task>? afterPersistedReadForTests = null) → Task<IReadOnlyList<CovenantDisclosureState>>` (owns one `BEGIN DEFERRED`), plus the overload `ReadEffectiveAsync(SqliteConnection, SqliteTransaction, CancellationToken)` that reads inside a caller's snapshot.
  - `ExternalDisclosureStateStore.ReadAllAsync(SqliteConnection, SqliteTransaction?, CancellationToken) → Task<List<CovenantDisclosureState>>`, `ReadAsync(SqliteConnection, SqliteTransaction, CovenantEgressDestination, CovenantDisclosureRevocability, CancellationToken) → Task<CovenantDisclosureState?>`, and `WriteAsync(SqliteConnection, SqliteTransaction, CovenantDisclosureState, DateTimeOffset updatedAtUtc, CancellationToken)`. `WriteAsync` binds `UtcInstantText.Format(updatedAtUtc)`, so the store, not its callers, is the persisted-instant writer that `UtcInstantPersistenceBoundaryTests` reviews.

- [ ] **Step 1: Write the failing tests**

`CovenantDisclosureJournalTests`. `CoreObjects` already names the three disclosure tables and their guards, and Task 3's partial index ships inside `disclosure_subject_state.sql`, so `CoreObjects` does not change.

```csharp
[Fact] public async Task AcknowledgeAsync_folds_each_receipt_into_its_bucket_in_the_same_transaction()
// Draft(1), Draft(2): ProviderDispatch, Provider/Nonrevocable, timestamp 1_700_000_000_000
CovenantDisclosureState bucket = Assert.Single(await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, ct));
Assert.Equal((CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 2ul, 638_355_968_000_000_000L),
    (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.EverOccurred, bucket.Count, bucket.MaximumTimestamp));
Assert.Equal(Or(first.Value.EvidenceBloom, second.Value.EvidenceBloom), bucket.EvidenceBloom.ToArray()); // row rebuild == live receipt
Assert.Equal(2, await ScalarAsync(fixture, "LastFoldedOrdinal"));
Assert.Equal("2023-11-14T22:13:20.0000000Z", await UpdatedAtAsync(fixture, 1, 2));

[Fact] public async Task AcknowledgeAsync_replay_folds_nothing_twice()                 // same Draft(1) twice -> Count 1, LastFoldedOrdinal 1
[Fact] public async Task AcknowledgeAsync_tool_use_folds_into_the_locally_revocable_process_bucket() // McpToolUse, Process/LocallyRevocable -> (4,1) Exact 1; (1,2) absent
[Fact] public async Task AcknowledgeAsync_a_pre_fold_backlog_is_folded_with_the_next_receipt_as_a_lower_bound()
// PreFoldDisclosureHistory.InsertAsync(Draft(1), 1), (Draft(2), 2); then journal Draft(3) -> ordinal 3
// (1,2) == LowerBound, Count 3, Bloom == OR of new CovenantDisclosureReceipt(Draft(i), i).EvidenceBloom, LastFoldedOrdinal 3
[Fact] public async Task AcknowledgeAsync_a_failed_fold_rolls_the_receipt_back()
// CREATE TEMP TRIGGER ... BEFORE INSERT ON external_disclosure_state BEGIN SELECT RAISE(ABORT,'x'); END;
// await Assert.ThrowsAsync<SqliteException>(...); receipts 0; disclosure_subject_state rows 0
[Fact] public async Task Exposure_reader_reports_live_receipts_without_seeded_state()   // 3 acks -> new CovenantDisclosureExposure(3, Exact)
```

The existing `Exposure_reader_refuses_malformed_codes_and_checked_overflow_content_free` stays unchanged and must stay green: a `CountKindCode` of 99 now throws `ArgumentException` from `CovenantDisclosureState`, and the `long.MaxValue` sum overflows the `checked` total; both still map to `MalformedExposure`.

`ExternalDisclosureStateReaderTests` (`CovenantCanonicalFixture` with the same `CoreObjects`):
- `Effective_state_counts_an_unfolded_backlog_as_a_lower_bound`: backlog of 2 on subject A, no persisted row → one bucket `(Provider, Nonrevocable, LowerBound, Count 2)`; `external_disclosure_state` still has 0 rows.
- `Effective_state_is_identical_before_and_after_the_backlog_is_folded`: subject C acknowledged live (persisted `Exact 1`), backlog 3 on A and 2 on B. `before` is `LowerBound 6`. Then `FoldAllUnfoldedAsync(…, RestoreStaging)` in `BEGIN IMMEDIATE` returns 2 and commits. `Assert.Equal(before, after)`, which compares count, ticks and Bloom. Persisted is now `LowerBound 6`, and `SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal` is 0.
- `Effective_state_reads_persisted_buckets_and_tails_in_one_snapshot`: backlog 2 on A. `afterPersistedReadForTests` opens `fixture.OpenAdditionalConnectionAsync`, then runs `FoldAllUnfoldedAsync(RestoreStaging)` and commits. The result is still `LowerBound 2`; without the snapshot it would be empty.
- `An_unrebuildable_receipt_row_is_still_counted`: a pre-fold row with `ExactGenerationIds` of 16 zero bytes, mode Exact. The single bucket is `(Provider, Nonrevocable, LowerBound, Count 1)` and its `EvidenceBloom` is not all zero.

`CovenantDisclosureStateAlgebraTests.WeakenToLowerBound_is_monotone_and_keeps_the_empty_state_exact`: `WeakenToLowerBound(Empty(Provider, Nonrevocable))` equals the input and is `Exact`; for a non-empty `Exact` state the result has kind `LowerBound` and the same count, ticks and Bloom; applying it twice equals applying it once.

`BackupCovenantRestoreReconcilerTests.Destination_state_read_counts_unfolded_receipts_as_a_lower_bound`: `BackupCovenantRestoreDestinationState.ReadAsync` over a backlog of 2 → `DisclosureBuckets` is `[LowerBound 2]`.

`CovenantRetentionSeed.SeedDisclosureAsync` changes:
- It acknowledges three `ProviderDispatch` drafts (`Provider`/`Nonrevocable`) and one `McpToolUse` draft (`Process`/`LocallyRevocable`) through `new CovenantDisclosureTransactionWriter(bootId).AcknowledgeAsync`, all on one `Turn` subject with origin `ffffffff-6666-4666-8666-ffffffffffff` and subject `ffffffff-7777-4777-8777-ffffffffffff` (the ids the inventory tests already select by), timestamp `1_767_225_600_000` (`2026-01-01T00:00:00Z`), effect digests of 32 bytes `0x01`…`0x04`, and a `ProviderCallSensitivity` built as `CovenantDisclosureJournalTests.Sensitivity` is.
- It deletes both direct `external_disclosure_state` inserts and the mismatched category-4/destination-1 receipt.

`CovenantRetentionTests` changes:
- Keep the existing `Exact 3` assertions (:397 and :461).
- `ChangeCovenantInventoryAggregateAsync(Rows)` copies the subject's tail row (`ORDER BY SubjectOrdinal DESC LIMIT 1`) instead of an arbitrary one, so `SubjectOrdinal + 1` and `CategoryPhysicalAttemptOrdinal + 1` never collide with a journal-allocated ordinal.
- Add `Seeded_disclosure_accounting_is_produced_by_the_live_journal_fold`: 4 receipts, `LastFoldedOrdinal = LastAllocatedOrdinal = 4`, `(1,2)` is `Exact 3`, `(4,1)` is `Exact 1`.
- Add `Reset_preview_rendered_by_the_cli_counts_live_receipts_instead_of_denying_disclosure`. It produces the Covenant `ResetMemory` plan from the real service, round-trips it through `ArcanumJsonContext.Default.DataRetentionPlan`, and renders it with `new CovenantExternalRetentionDisclosureWriter(recorder, Options.Create(new ArcanumSettings())).Write(plan.Covenant)`, where `recorder` is a private `IConsoleDispatcher` that records diagnostics. The output contains "This installation's own receipts record exactly 3 physical attempts that could have carried protected content out of it. Nothing this reset does can revoke any of them.", and no line contains "record no nonrevocable disclosure".

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantDisclosureJournalTests|FullyQualifiedName~ExternalDisclosureStateReaderTests|FullyQualifiedName~CovenantDisclosureStateAlgebraTests|FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~BackupCovenantRestoreReconcilerTests"
```
Expected RED:
- the fold, reader, store and `WeakenToLowerBound` do not exist (compile);
- once stubs compile, no bucket is written;
- the retention status and plan report `0`, not `3`, because the seed no longer writes state directly;
- the CLI prints "record no nonrevocable disclosure".

- [ ] **Step 3: Implement**

`FoldSubjectTailAsync` runs in this order, because the watermark compare-and-swap is what makes a concurrent or repeated fold count nothing twice:
1. Read `LastFoldedOrdinal` for the subject key (`OriginInstallationId`/`SubjectId` in `"D"` text, `SubjectKind` code).
2. Read every receipt with `SubjectOrdinal > LastFoldedOrdinal`, ordered by ordinal.
3. Rebuild each receipt as `new CovenantDisclosureReceipt(new CovenantDisclosureDraft(…), ordinal)` from the row: the stored `EffectIdentityDigest` and destination digest are the draft's; the sensitivity digest is recomputed with `CovenantDigests.Sensitivity(new SensitivityDigestInput(level, mode, ids, bloom))`, ids read big-endian; the timestamp is `UtcInstantText.Parse(DisclosedAtUtc).ToUnixTimeMilliseconds()`. On `ArgumentException` or `FormatException`, fall back to `CreateEvidenceBloom(new CovenantDigest(EffectIdentityDigest))` and still count the receipt. Never assume `SensitivityCode = 1`.
4. Fold each bucket as `IncrementLocal(bucket ?? Empty(d, r), 1, UtcTicks, bloom)`; apply `WeakenToLowerBound` when `origin is RestoreStaging || tail.Count > 1`; write through `ExternalDisclosureStateStore.WriteAsync` with `updatedAtUtc` = the parsed latest `DisclosedAtUtc` of the tail.
5. `UPDATE disclosure_subject_state SET LastFoldedOrdinal = $last WHERE <pk> AND LastFoldedOrdinal = $folded;` must change exactly 1 row, otherwise throw `InvalidOperationException`, which rolls the acknowledgement back.

The journal calls it with `(CovenantDisclosureSubject.From(draft), Live)` inside its own transaction. The replay branch rolls back before it, so a replay folds nothing.

`FoldAllUnfoldedAsync` selects `OriginInstallationId, SubjectKind, SubjectId FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal` (Task 3's partial index) and folds each subject.

`ReadEffectiveAsync` without a transaction owns one `BEGIN DEFERRED`: it reads the persisted buckets, runs the test hook, reads the unfolded subjects and their tails with the same row rebuild, folds them in memory, weakens every bucket that received an unfolded receipt, and rolls back. It treats an absent `external_disclosure_state`, `disclosure_subject_state` or `external_disclosure_receipts` table as empty. Malformed persisted codes throw `ArgumentException` from `CovenantDisclosureState`, exactly as today.

Wiring:
- `CovenantDisclosureExposureReader.ReadWithinAsync` sums the nonrevocable effective buckets with `checked`, joins their kinds, and keeps its `MalformedExposure` catch list. A non-null `transaction` uses the in-snapshot overload; a null one uses the self-snapshot overload. The one null-transaction caller, `DataRetentionService.ReadNonrevocableDisclosureExposureAsync` (`DataRetentionService.CovenantInventory.cs:352`), runs with no transaction open on that connection; the `CovenantErasureInventorySource` callers (:150, :509) pass their closed-snapshot transaction.
- `BackupCovenantRestoreDestinationState.ReadAsync` calls `ReadEffectiveAsync(destination, ct)`.
- `CovenantDisclosureStateJoiner.JoinIntoStagedAsync` reads through `ExternalDisclosureStateStore.ReadAsync` and writes through `WriteAsync(…, timeProvider.GetUtcNow(), …)`.

Inventories: `git add` the three new sources; insert their `R\t<path>` lines in ordinal order; run `GrimoireAdmissionBenchmarkManifestTests`, copy the shape digest its failure prints into `inputCatalogShapeDigest` and `ExactInputCatalogShapeDigest`, and re-run it green. Update the UTC owner list and the R8 scan list as the Files list says.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then the wider cluster and the inventories:
```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantDisclosureWriterTests|FullyQualifiedName~CovenantErasure|FullyQualifiedName~CovenantCanonicalErasureTransactionTests|FullyQualifiedName~CovenantRestoreStateJoinerTests|FullyQualifiedName~DataRetentionCommandTests|FullyQualifiedName~BackupRestoreProtectedState|FullyQualifiedName~CovenantRestoreStagingTests|FullyQualifiedName~CovenantDigestCorpusTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~DocumentationStructureTests"
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "Category=HostedProducerProductionAnalysis"
```
Review the capsule diff: only journal, fold, store and joiner rows may move.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must fail |
|---|---|
| Remove the fold call from `AcknowledgeWithinTransactionAsync` | `AcknowledgeAsync_folds_each_receipt…`, and the CLI render test |
| Drop the watermark `UPDATE` | `Effective_state_is_identical_before_and_after…` (after = 11) |
| Drop the `tail.Count > 1` weakening | `…pre_fold_backlog…as_a_lower_bound` |
| Remove `BEGIN DEFERRED` from the reader | `Effective_state_reads_…_in_one_snapshot` |
| Drop the reader's weakening | `Effective_state_counts_an_unfolded_backlog_as_a_lower_bound` |
| Revert `SeedDisclosureAsync` to its raw receipt and state inserts | `Seeded_disclosure_accounting_is_produced_by_the_live_journal_fold` (receipt count 1, `LastFoldedOrdinal` absent) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateFold.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateReader.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateStore.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantDisclosureStateAlgebra.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantRestoreStateJoiners.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs docs/Arcanum.DESIGN.md docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionSeed.cs tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStateJoinerTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/PreFoldDisclosureHistory.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/ExternalDisclosureStateReaderTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureJournalTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantDisclosureStateAlgebraTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupCovenantRestoreReconcilerTests.cs tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "fix: fold disclosure receipts live and read effective disclosure state" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 22: Restore: destination evidence read and plan reporting

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidence.cs` (`BackupRestoreErasureCodes`, `BackupRestoreErasureEvidenceKind`, `BackupRestoreErasureEvidence`)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.ErasureEvidence.cs` (partial: `ReadDestinationErasureEvidenceAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs`:
  - make the class `partial`;
  - constructor: a required `IMemoryErasureKeyProvider erasureKeys` directly after `schemaInstaller` (before the optional `options` and `maintenanceCoordination`);
  - `BuildPlanAsync`: the read, blockers, summary, and the new-profile-root warning;
  - `ExecuteAsync`: re-read directly after `BeforePhaseForTests(Stage)`, before any extraction directory exists.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs` (add `ReadSnapshotAsync` and the snapshot row records)
- Modify: `src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs` (`BackupRestoreErasureEvidenceStatus`, `BackupRestoreErasureEvidenceSummary`, and a trailing `BackupRestorePlan.DestinationErasureEvidence = null`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (`AddArcanumBackup`, :691: pass `serviceProvider.GetRequiredService<IMemoryErasureKeyProvider>()`, independently of `ResolveRestoreStaging`; Task 2's `AddMemoryErasureKeyring()` already registers the provider inside `AddArcanumBackup`)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs` (`WriteRestorePlan` line; a `BackupCliCatalog.Format(BackupRestoreErasureEvidenceStatus)` overload)
- Modify: `docs/Arcanum.Command.Reference.md` (`backup restore`: the three plan blockers and `plan.destinationErasureEvidence`)
- Create tests: `tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs`, `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureEvidenceReadTests.cs`
- Modify tests: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreServiceTests.cs` (the `Restore(...)` helper at :2107 passes `new MemoryErasureKeyring(new InMemoryOsCredentialStore())`), `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs` (`Harness.CreateService` at :703 passes `new MemoryErasureKeyring(credentials)`), `tests/RetroDownfall.Arcanum.Tests/Cli/BackupRestoreCommandTests.cs`
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs` (`ErasureChokepointOwners` gains `typeof(BackupRestoreService)`: restore is where §14 honours fingerprints)
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (one new row) and `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` +1 over its value after Task 21, which adds none)
  - the three benchmark files (+2 `R` lines, both digests)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (`ContentFreeLogFiles` +2 files)
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated)

**Interfaces:**
- Consumes:
  - Task 2: `IMemoryErasureKeyProvider.OpenExisting(MemoryErasureKeyProbe.Reprobe)`, `MemoryErasureKeyOpenResult`, `MemoryErasureKeyring(IOsCredentialStore)`, `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`, and its registration inside `AddArcanumBackup`;
  - Task 3: `CoreSchemaVersionTwelveFixture.ChainSet()`;
  - Task 6: `MemoryErasureEvidence.IsInstalledAsync`, `MemoryErasureReceiptRow`, and the `CountingOsCredentialStore` test double (`Support/MemoryErasureTestKeys.cs`: `Calls`, `FailWith`);
  - Task 7: the `NullableInterfaceConstructorDefaultTests.ErasureChokepointOwners` theory;
  - Task 13: `MemoryErasureRouteDriver` (`tests/…/Support/MemoryErasureRouteDriver.cs`), whose insert and erase members the harness delegates to;
  - Task 12: `MemoryErasureStructuralTests.ContentFreeLogFiles` (R8).
- Produces:
  - `internal static class BackupRestoreErasureCodes`: `KeyMissing`, `KeyUnavailable`, `EvidenceUnavailable`, `EvidenceUnjoinable`, `VerificationFailed`, holding the five exact spine strings.
  - `internal enum BackupRestoreErasureEvidenceKind { None = 1, Present = 2, Refused = 3 }`
  - `internal sealed record BackupRestoreErasureEvidence(BackupRestoreErasureEvidenceKind Kind, byte[] KeyId, MemoryErasureEvidenceSnapshot Rows, BackupVerifyIssue? Refusal)` with statics `None`, `Present(byte[] keyId, MemoryErasureEvidenceSnapshot rows)`, `Refused(string code, MemoryErasureEvidenceSnapshot? rows)`, and `ToSummary()`.
  - `MemoryErasureEvidence.ReadSnapshotAsync(SqliteConnection, SqliteTransaction?, CancellationToken) → Task<MemoryErasureEvidenceSnapshot?>` (null means Core < 13 or the table is absent).
  - `internal sealed record MemoryErasureFingerprintRow(byte[] Fingerprint, MemoryReviewStore Store, byte[] KeyId)`
  - `internal sealed record MemoryErasureReceiptSubjectRow(Guid MutationId, byte[] SubjectDigest)`
  - `internal sealed record MemoryErasureEvidenceSnapshot(IReadOnlyList<MemoryErasureFingerprintRow> Fingerprints, IReadOnlyList<MemoryErasureReceiptRow> Receipts, IReadOnlyList<MemoryErasureReceiptSubjectRow> Subjects)` with `HasRows` (any fingerprint or receipt) and `static Empty`.
  - `private Task<BackupRestoreErasureEvidence> BackupRestoreService.ReadDestinationErasureEvidenceAsync(CancellationToken)`
  - `public enum BackupRestoreErasureEvidenceStatus { None = 1, Present = 2, Refused = 3 }` (`StringOnlyJsonStringEnumConverter`)
  - `public sealed record BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus Status, long SagaFingerprints, long LexiconFingerprints, long CovenantFingerprints, long Receipts)`. A `Refused` summary carries the counts the Grimoire yielded, or zeros when it was unreadable.
  - Test harness `MemoryErasureRestoreHarness`, used by Tasks 23 and 24:
    - `CreateAsync()`, `Profile`, `InstallationRoot` (`Path.Combine(Profile.TempHome, ".config", "arcanum")`, where `ArcanumWebApplicationFactory` places the Grimoire), `Credentials` (the profile's `InMemoryOsCredentialStore`);
    - `StartHost(bool covenant = false)` → `MemoryErasureRouteDriver.Host(Credentials, Profile, covenant)` (Task 13), plus `StopHostAsync()`;
    - `InsertSagaAsync(string content, Guid? sessionId = null)` → `MemoryErasureRouteDriver.InsertSagaAsync(factory, content, sessionId)` (no Session means Global scope; a Campaign-scoped memory comes from a Session bound to that Campaign) and `EraseSagaAsync(memoryId)` → `new MemoryErasureRouteDriver(factory.CreateClient()).EraseSagaAsync(memoryId)`. These are one-line delegations; the harness adds no second erase client;
    - `CreateArchiveAsync(name)`, `LiveDatabaseDigestAsync()` (SHA-256 hex of the live `arcanum.db` bytes after `SqliteConnection.ClearAllPools()`);
    - `CreateRestoreService(IOsCredentialStore? credentials = null, ISecretStore? secrets = null, BackupRestoreServiceOptions? options = null, GrimoireSchemaInstaller? installer = null, Func<IBackupService>? safetyBackups = null)`.

- [ ] **Step 1: Write the failing tests**

**`MemoryErasureRestoreHarness`.** It owns one `RestartableArcanumProfileFixture`, whose `InMemoryOsCredentialStore` is the only keychain.
- Host phases run through `StartHost` (the driver's `Host`, so Saga, Embeddings and the fixed-vector weave fake are on), and each factory is disposed before the next step. Each test creates its own harness; nothing is shared between tests.
- Archives go through the production `BackupService` six-argument constructor over `BackupStatePaths(InstallationRoot, InstallationRoot, audit, guardrails)`, with a private fixed-secret `IBackupSecretSnapshotReader` serving `GrimoireFixture.TestGrimoireSecret` (the shape `BackupRestoreServiceTests` uses at :2328).
- The restore service is `new BackupRestoreService(paths, codec, secrets ?? new FixedGrimoireSecretStore(GrimoireFixture.TestGrimoireSecret), safetyBackups, TimeProvider.System, installer ?? GrimoireSchemaTestInstaller.Create(), new MemoryErasureKeyring(credentials ?? Credentials), options)`. `FixedGrimoireSecretStore(string? secret)` is a private harness `ISecretStore`; `null` serves no Grimoire secret.

`BackupRestoreErasureEvidenceReadTests` is `[Collection("ApiHost")]` and uses `[SkippableFact]`/`[SkippableTheory]` gated on `GrimoireFixture.SqlCipherAvailable`. The §14.1 rows go through `PlanAsync` on a `ReplaceInstallation` request; "keychain" is Task 6's `CountingOsCredentialStore` wrapping `harness.Credentials`.

```csharp
[SkippableFact] public async Task A_destination_that_never_erased_plans_none_without_consulting_the_keychain()
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.None, 0, 0, 0, 0), plan.DestinationErasureEvidence);
Assert.DoesNotContain(plan.Blockers, b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal));
Assert.Equal(0, keychain.Calls);

[SkippableFact] public async Task An_erased_destination_with_its_key_plans_present_with_per_store_counts()
// InsertSagaAsync("erased on purpose") (Global) -> CreateArchiveAsync -> EraseSagaAsync
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Present, 1, 0, 0, 1), plan.DestinationErasureEvidence);
Assert.Empty(plan.Blockers);

[SkippableTheory] [InlineData("replaced")] [InlineData("deleted")]
public async Task An_erased_destination_whose_key_is_replaced_or_gone_refuses_as_key_missing(string change)
// Set(... MemoryErasureFingerprintKeyAccount, base64url(32 random bytes)) or Delete(...)
Assert.Equal("backup.restore_erasure_key_missing", Assert.Single(plan.Blockers).Code);
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 0, 0, 1), plan.DestinationErasureEvidence);

[SkippableFact] public async Task An_erased_destination_with_an_unavailable_keychain_refuses_as_key_unavailable()
// keychain.FailWith = OsCredentialStoreStatus.Unavailable
Assert.Equal("backup.restore_erasure_key_unavailable", Assert.Single(plan.Blockers).Code);
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 0, 0, 1), plan.DestinationErasureEvidence);

[SkippableTheory] [InlineData(true)] [InlineData(false)]   // database moved aside | FixedGrimoireSecretStore(null)
public async Task A_missing_or_unreadable_destination_without_a_key_plans_none(bool absent)
// erase, then credentials.Delete(Service, MemoryErasureFingerprintKeyAccount), then make the Grimoire absent or unreadable
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.None, 0, 0, 0, 0), plan.DestinationErasureEvidence);
Assert.DoesNotContain(plan.Blockers, b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal));
Assert.Equal(1, keychain.Calls);                                     // the Grimoire could not answer, so the key was asked once

[SkippableTheory] [InlineData(true, false)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(false, true)]
public async Task A_missing_or_unreadable_destination_with_a_present_or_unavailable_key_refuses_as_evidence_unavailable(bool absent, bool keychainUnavailable)
Assert.Equal("backup.restore_erasure_evidence_unavailable",
    Assert.Single(plan.Blockers, b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal)).Code);
Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 0, 0, 0, 0), plan.DestinationErasureEvidence);

[SkippableFact] public async Task A_refusal_first_seen_at_execute_stops_before_extraction()
// erase; plan is Present; options.BeforePhaseForTests = phase => { if (phase == Stage) move arcanum.db.kdf aside }; Confirmed: true
// (a Present latch is never re-probed, so the execute-time change has to be the Grimoire's readability, not the keychain's)
Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
Assert.Equal("backup.restore_erasure_evidence_unavailable", Assert.Single(result.Issues).Code);
Assert.DoesNotContain(result.Phases, p => p.Phase == BackupRestorePhase.Stage);
// move the sidecar back, then:
Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(harness.InstallationRoot)!, ".arcanum-restore-*"));

[SkippableFact] public async Task Every_erasure_refusal_names_its_ways_out_and_carries_no_content()
// the three plan-time refusals: each Message contains "retry", "arcanum memory erasure reset-key" and "full installation reset";
// no Message or Path contains "erased on purpose", "memory-erasure-fingerprint-key", or the key id in hex

[SkippableFact] public async Task A_new_profile_root_plan_warns_when_this_installation_holds_an_erasure_key()
const string Warning = "This installation's erasure evidence is not applied to a new profile root; items erased here can reappear there.";
Assert.DoesNotContain(Warning, before.Warnings);                     // no key yet
Assert.Contains(Warning, after.Warnings);                            // after one erase created the key

[SkippableFact] public async Task A_catalog_below_version_thirteen_reads_as_no_evidence()
// scratch DB via GrimoireSchemaTestInstaller.InstallAsync(conn, CoreSchemaVersionTwelveFixture.ChainSet(), 1536, ct)
Assert.Null(await MemoryErasureEvidence.ReadSnapshotAsync(connection, null, ct));

[Fact] public void The_cli_client_stack_registers_exactly_one_singleton_erasure_key_provider()
// new ServiceCollection().AddArcanumCliClientStack(): descriptor level, no resolution
Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(IMemoryErasureKeyProvider)).Lifetime);
```

`BackupRestoreCommandTests.A_dry_run_renders_the_destination_erasure_evidence`, using the `FakeRestoreService` plan:
- `(Present, 1, 0, 0, 1)` prints `Erasure evidence: present (1 Saga, 0 Lexicon, 0 Covenant fingerprints; 1 receipts); archived items that match are removed before commit`;
- `(None, 0, 0, 0, 0)` prints `Erasure evidence: none`;
- `(Refused, …)` prints `Erasure evidence: could not be proven; this restore is blocked`;
- a null summary prints no `Erasure evidence` line.

`NullableInterfaceConstructorDefaultTests.Every_erasure_chokepoint_owner_requires_the_key_provider` gains the `typeof(BackupRestoreService)` row.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestoreErasureEvidenceReadTests|FullyQualifiedName~BackupRestoreCommandTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests"
```
Expected RED: `DestinationErasureEvidence`, the summary types, `ReadSnapshotAsync` and the constructor parameter do not exist. Once stubs compile, every row plans `null` evidence and no blocker, and the chokepoint-owner row fails because `BackupRestoreService` takes no provider.

- [ ] **Step 3: Implement**

`ReadDestinationErasureEvidenceAsync` reads the Grimoire first and consults the key only when rows exist.

**Grimoire readable** (`File.Exists(_paths.DatabasePath)`, a Grimoire secret with `SecretStoreReadStatus.Ok`, and the open succeeds):

| Rows | OS key (`OpenExisting(Reprobe)`) | Result |
|---|---|---|
| `ReadSnapshotAsync` returns null, or `!HasRows` | not consulted | `None` |
| ≥ 1 fingerprint or receipt row | `Present`, and every fingerprint and receipt `KeyId` equals `key.KeyId` | `Present(keyId, snapshot)` |
| ≥ 1 row | `Present` with any other `KeyId`, or `Absent` | `Refused(KeyMissing, snapshot)` |
| ≥ 1 row | `Unavailable` or `Malformed` | `Refused(KeyUnavailable, snapshot)` |

A receipt-only destination (fingerprints released, receipts kept) counts as ≥ 1 row. `Malformed` maps like `Unavailable` (R16). The snapshot is read in one `BEGIN DEFERRED` on a read-only `BackupRestoreDatabaseWorker.OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)` handle, spelled exactly as `ReadDestinationSchemaAsync` spells it, so the acquisition row below matches.

**Grimoire absent or unreadable** (a missing file, a non-`Ok` secret, or the `ReadDestinationCampaignIdsAsync` catch list: `SqliteException`, `InvalidDataException`, `IOException`, `UnauthorizedAccessException`, `NotSupportedException`, `FormatException`, `JsonException`, `CryptographicException`):

| OS key | Result |
|---|---|
| `Absent` | `None` |
| Any other state | `Refused(EvidenceUnavailable, null)` |

Always dispose the key. Every refusal message says three things, content-free:
- the destination's database or keychain could not prove its erasures;
- restore readability and retry;
- or run `arcanum memory erasure reset-key` on this installation, or a full installation reset.

`BuildPlanAsync`:
- for `ReplaceInstallation` with the maintenance lock acquired, reads the evidence, adds `Refusal` to `blockers`, and sets `DestinationErasureEvidence = evidence.ToSummary()`; otherwise the field stays null;
- for `NewProfileRoot`, adds the warning when `OpenExisting(Reprobe).State is Present`.

`ExecuteAsync`, for `ReplaceInstallation` only, re-reads the evidence directly after `BeforePhaseForTests(Stage)`:
- a `Refused` value returns `Rejected(operationId, plan, phases, [refusal])`;
- otherwise it records `Stage: "Destination erasure evidence: {status}."` and keeps the value in a local for Task 23.

The execute-time re-read cannot see a keychain change after a `Present` plan read, because Task 2 never re-probes a `Present` latch; the latched copy is the key staging uses, so that is correct. What it does catch is a Grimoire that became unreadable, which the test above exercises.

Acquisition row: `("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.ErasureEvidence.cs", "BackupRestoreService", "ReadDestinationErasureEvidenceAsync(1)", AcquisitionConstructKind.ProviderOpen, "BackupRestoreDatabaseWorker.OpenAsync", 4, "BackupRestoreDatabaseWorker.OpenAsync(_paths.DatabasePath,secret.Value,readOnly:true,cancellationToken)")` classified `LiveGrimoire` / `ServingRawOrdinary` / `OrdinaryConnectionFactory` / `null`, as `ReadDestinationSchemaAsync(1)` is. `ExpectedProductionAcquisitionCount` +1.

Benchmark catalog, R8 scan list and capsules as in Task 21.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:
```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestoreServiceTests|FullyQualifiedName~CovenantRestoreStagingTests|FullyQualifiedName~BackupRestoreProtectedState|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~MaintenanceLockTypedCallSiteTests|FullyQualifiedName~CliJsonContext|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~MemoryErasureKeyCustodyTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```
Then regenerate the capsules and run `Category=HostedProducerProductionAnalysis`, as in Task 21 Step 4. `MemoryErasureKeyCustodyTests` must stay green: no production file added here names the key account.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must fail |
|---|---|
| Probe the key before reading the Grimoire | `A_destination_that_never_erased…` (`Calls` becomes non-zero) |
| Make the `Present`-with-a-foreign-`KeyId` arm return `Present` | `…replaced_or_gone…("replaced")` |
| Make the unreadable arm return `None` whatever the key state | `…with_a_present_or_unavailable_key_refuses…` |
| Skip the re-read in `ExecuteAsync` | `A_refusal_first_seen_at_execute_stops_before_extraction` |
| Drop the new-profile-root warning | `A_new_profile_root_plan_warns_when_this_installation_holds_an_erasure_key` |
| Give the constructor parameter a `= null` default | `Every_erasure_chokepoint_owner_requires_the_key_provider(BackupRestoreService)` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidence.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.ErasureEvidence.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs docs/Arcanum.Command.Reference.md tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureEvidenceReadTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/BackupRestoreCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: read destination erasure evidence before a restore" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 23: Restore: staged drain across every tier

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreSchemaDrain.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs` (add `internal GrimoireSchemaVersionChainSet Chains => _chains;`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs`:
  - `PrepareStagedGenerationAsync` gains a 13th parameter, `BackupRestoreErasureEvidence erasure`, after `destination`;
  - it keeps the `MigrateAsync` result, adds the conditional drain, and maps a throw to the typed refusal;
  - `ExecuteAsync` passes the value read in Task 22 (`BackupRestoreErasureEvidence.None` for other modes).
- Modify: `docs/Arcanum.DESIGN.md` (§5.4.9, near :814: older snapshots stop at the first sweep-bearing step unless destination evidence forces a drain; §10.25.1: the restore-staging exception to "No draining from the CLI")
- Modify: `docs/Arcanum.Command.Reference.md` (`backup restore`: `backup.restore_erasure_evidence_unjoinable`, raised before any safety backup or displacement)
- Modify tests: `tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs` (add `CreateArchiveAtAsync(string name, GrimoireSchemaVersionChainSet chains, Func<SqliteConnection, Task> seed)`, which builds a separate source installation with its own secret and KDF sidecar, installs `chains` with `GrimoireSchemaTestInstaller.InstallAsync`, runs `seed`, then archives it through the production `BackupService`)
- Create tests: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreStagedDrainTests.cs` (with private `ThrowingBackfill` and `StalledBackfill` doubles)
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs`: re-key both `PrepareStagedGenerationAsync(12)` strings (the member identity at :1905 and the `ExactNonServingProof` name at :1909) to `(13)`. `ExpectedProductionAcquisitionCount` is unchanged (+0).
  - the three benchmark files (+1 `R` line, both digests)
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (`ContentFreeLogFiles` +1 file)
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated)

**Interfaces:**
- Consumes:
  - Task 22: `BackupRestoreErasureEvidence`, `BackupRestoreErasureCodes.EvidenceUnjoinable`, `MemoryErasureRestoreHarness`;
  - Task 3: the rebased `CoreSchemaVersionElevenFixture` and `CoreSchemaVersionTwelveFixture`;
  - Task 9: `CovenantCanonicalSchemaVersionFiveFixture.ChainSet()`;
  - Task 13: the erase route, through the harness;
  - existing `GrimoireSchemaBackfillRunner(installer, timeProvider).AdvanceAsync(connection, chain, journalRow, context, maxBatches, ct)`, `GrimoireSchemaTransitionJournal.ReadAllAsync`, `GrimoireSchemaInstaller.InstallAsync`, `GrimoireSchemaInstallResult`/`IsHealthy`, `GrimoireSchemaRefusedException`, `GrimoireSchemaBackfillBatch(NextCursor, RowsProcessed, IsComplete)`.
- Produces:
  - `internal sealed record BackupRestoreSchemaDrainReceipt(int Passes, int BatchesRun, long RowsProcessed)`
  - `internal static class BackupRestoreSchemaDrain { internal const int MaxBatchesPerPass = 64; static Task<Result<BackupRestoreSchemaDrainReceipt>> DrainAsync(SqliteConnection staged, GrimoireSchemaInstaller installer, GrimoireSchemaBackfillRunner runner, GrimoireSchemaInstallResult migrated, int embeddingDimensions, GrimoireSchemaInitializationContext context, CancellationToken cancellationToken); }`
  - `PrepareStagedGenerationAsync(…, BackupRestoreErasureEvidence erasure, …)` with arity 13, which Task 24 reuses.

- [ ] **Step 1: Write the failing tests**

`BackupRestoreStagedDrainTests` uses `[Collection("ApiHost")]`. "With evidence" means `InsertSagaAsync` then `EraseSagaAsync` on the host before the restore; "digest" is `harness.LiveDatabaseDigestAsync()` taken before the restore.

The old archive uses this chain set:

```csharp
// Declared on MemoryErasureRestoreHarness (internal static) so Task 24 can reuse it (N3).
internal static GrimoireSchemaVersionChainSet CoreElevenCanonicalFive() => new([
    CoreSchemaVersionElevenFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core),
    CovenantCanonicalSchemaVersionFiveFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
    GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator)]);
```

The archive's own rows are seeded with SQL, because this build has no writer of a v11 catalog.

```csharp
[SkippableFact] public async Task An_archive_at_core_eleven_and_canonical_five_is_drained_to_every_head_before_commit()
Assert.Equal(BackupRestoreStatus.Completed, result.Status);
Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, ct));
Assert.Equal(6L, await ScalarAsync(restored, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 1"));
Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));
Assert.True(await BackupRestoreDatabaseWorker.TableExistsAsync(restored, "memory_erasure_fingerprints", ct));
Assert.Contains(result.Phases, p => p.Phase == BackupRestorePhase.Migrate && p.Detail.StartsWith("Drained staged schema transitions", StringComparison.Ordinal));

[SkippableFact] public async Task An_archive_already_at_head_needs_no_drain()
// with evidence, archive from the live host: Completed; Core 13; no phase Detail starts with "Drained"
[SkippableFact] public async Task A_destination_without_evidence_leaves_an_older_archive_pending_exactly_as_before()
// no erase: Completed; Core 11; one grimoire_schema_transitions row; no "Drained" record
[SkippableFact] public async Task A_failing_staged_sweep_refuses_as_unjoinable_before_any_displacement()
// installer = GrimoireSchemaTestInstaller.Create(chains whose Core step 11->12 is `step with { Backfill = new ThrowingBackfill(original.Name) }`)
// ThrowingBackfill throws new InvalidOperationException("sentinel-diagnostic")
Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
BackupVerifyIssue issue = Assert.Single(result.Issues);
Assert.Equal("backup.restore_erasure_evidence_unjoinable", issue.Code);
Assert.EndsWith("Diagnostics: InvalidOperationException", issue.Message, StringComparison.Ordinal);
Assert.DoesNotContain("sentinel-diagnostic", issue.Message, StringComparison.Ordinal);
Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
Assert.Equal(0, safetyBackups.CreateCalls);           // CreateSafetyBackup: true, counting IBackupService fake
[SkippableFact] public async Task A_failing_staged_sweep_without_destination_evidence_still_restores()
// Completed; Core 11; one journal row
[SkippableFact(Timeout = 120_000)] public async Task A_drain_pass_that_makes_no_progress_is_refused_rather_than_looping()
// StalledBackfill returns new GrimoireSchemaBackfillBatch(cursor, 0, IsComplete: false) forever
// -> Rejected; "backup.restore_erasure_evidence_unjoinable"; digest unchanged
[SkippableFact] public async Task An_archive_journaled_mid_transition_for_an_older_head_refuses_with_the_typed_code()
// source: install CoreElevenCanonicalFive, then install CoreSchemaVersionTwelveFixture.ChainSet() (journals the v12 sweep, TargetVersion 12)
// with evidence -> Rejected, "backup.restore_erasure_evidence_unjoinable"; without evidence -> "backup.restore_failed" (unchanged)
[SkippableFact] public async Task A_drain_cancelled_mid_pass_refuses_before_any_displacement()
// the backfill cancels the caller's CTS, then throws OperationCanceledException
// -> Rejected; "backup.restore_erasure_evidence_unjoinable"; digest unchanged; no .arcanum-restore-* directory remains
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestoreStagedDrainTests"
```
Expected RED:
- the v11 archive restores at Core 11 with a pending journal row;
- the failing and stalled sweeps are not attempted, so those restores complete;
- the older-head journal fails as `backup.restore_failed`.

- [ ] **Step 3: Implement**

`PrepareStagedGenerationAsync` runs two phases after `MigrateAsync`.

**The throw guard.** `MigrateAsync` runs inside a `try`. A `GrimoireSchemaRefusedException`, `InvalidOperationException` or `SqliteException` returns `StageResult.Failed` with `EvidenceUnjoinable` when `request.ConflictMode == ReplaceInstallation` and `erasure.Kind is Present`. Any other combination rethrows, exactly as today.

**The drain.** It runs when `ReplaceInstallation`, `erasure.Kind is Present` (Task 22 guarantees at least one row), and the journal has rows or any tier in the `MigrateAsync` result is not `IsHealthy`. It records `Migrate: "Drained staged schema transitions: {passes} passes, {batches} batches, {rows} rows."`.

`DrainAsync` loops over passes. Each pass reads `GrimoireSchemaTransitionJournal.ReadAllAsync(staged)`, snapshots the progress key per row `(tier, CompletedThroughVersion, BackfillName, BackfillCursor, BackfillRowsProcessed)`, calls `runner.AdvanceAsync(staged, installer.Chains.ForTier(row.TransactionTier), row, context, MaxBatchesPerPass, ct)` for each row with a `BackfillName`, then calls `installer.InstallAsync(staged, embeddingDimensions, context, ct)`. It succeeds when the journal is empty and Core, canonical and accelerator are all healthy. It refuses when the progress keys and the three tier versions are unchanged across a pass. The build creates the runner as `new GrimoireSchemaBackfillRunner(_schemaInstaller, _timeProvider)`.

A refused tier, a throw, a stalled pass and an `OperationCanceledException` each return `new Error(BackupRestoreErasureCodes.EvidenceUnjoinable, "The archive's database could not be brought to this build's schema head, so this installation's erasure evidence cannot be applied. Nothing was displaced. Restore a newer archive, or run a full installation reset. Diagnostics: " + exception.GetType().Name)` (a stall or refused tier names `GrimoireSchemaTierHealth` instead). Never include `exception.Message`. The refusal comes before the Covenant arm, the safety backup and `Commit`.

Benchmark catalog, R8 scan list, acquisition re-key and capsules as the Files list says.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:
```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestoreServiceTests|FullyQualifiedName~CovenantRestoreStagingTests|FullyQualifiedName~BackupRestoreErasureEvidenceReadTests|FullyQualifiedName~GrimoireSchemaInstallerTests|FullyQualifiedName~GrimoireSchemaBackfillRunnerTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationIssueReferenceTests"
```
Then regenerate the capsules and run `Category=HostedProducerProductionAnalysis`.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must fail |
|---|---|
| Drop the `erasure.Kind is Present` gate, so the drain always runs | `A_destination_without_evidence_leaves_an_older_archive_pending…` |
| Drop the drain call | `An_archive_at_core_eleven…drained…` |
| Remove the no-progress guard | `A_drain_pass_that_makes_no_progress…` (times out at 120 s) |
| Let the throw guard rethrow even with evidence | `…older_head_refuses_with_the_typed_code` |
| Stop the loop once Core is healthy and ignore canonical | the canonical version assertion `6L` |
| Append `exception.Message` to the refusal | `A_failing_staged_sweep_refuses_as_unjoinable…` (`sentinel-diagnostic`) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreSchemaDrain.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs docs/Arcanum.DESIGN.md docs/Arcanum.Command.Reference.md tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreStagedDrainTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: drain staged schema sweeps before applying erasure evidence" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 24: Restore: staged memory-evidence reconciliation

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs` (`BackupRestoreErasureApplicationReceipt`, `BackupRestoreErasureMatches`, `ApplyAsync`, `FindMatchesAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.ErasureEvidence.cs` (`ReconcileStagedMemoryEvidenceAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs`:
  - `ExecuteAsync` reads `ReadDestinationCovenantStateAsync` for every `ReplaceInstallation` (today only inside the gate-on arm), and deletes the extracted database with a check right after `ComposeStagedTree`;
  - `PrepareStagedGenerationAsync` calls the evidence step after the drain and before the Covenant arm;
  - `StageResult` gains a trailing `BackupRestoreErasureApplicationReceipt? Erasure`;
  - `ReconcileAsync` gains a 7th parameter, the receipt, runs the post-commit proof on the connection it already opens, and sets `ErasureApplication`;
  - the "byte-for-byte what it was before this slice" remark on `EvaluateProtectedStateAsync` (:1428) says the evidence step now runs with the gate off too.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs` (remove `JoinDisclosureAsync` and `BackupCovenantRestoreReconciliationReceipt.JoinedDisclosureBuckets`; `BackupCovenantRestoreDestinationState.DisclosureBuckets` stays and feeds the evidence step)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreServiceOptions.cs` (add `internal Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? AfterErasurePurgeForTests { get; init; }`; the `RestoreStaging` remark at :36-46 no longer says an absent arm adopts the snapshot "exactly as the archive carried it")
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs` (add `ReplaceAllAsync`, the restore-staging deleter)
- Modify: `src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs` (`BackupRestoreErasureScrubStatus`, `BackupRestoreErasureApplication`, a trailing `BackupRestoreReconciliation.ErasureApplication = null`)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs` (`WriteRestoreResult` line; a `Format(BackupRestoreErasureScrubStatus)` overload)
- Modify docs (R26: Task 24 owns the restore codes):
  - `docs/Arcanum.DEBUGGING.Human.md` item 26 ("Trace portable backup without touching real state"): trace the erasure evidence a restore must not undo, naming the five `backup.restore_erasure_*` codes and where each is raised (plan, execute re-read, drain, staging post-condition);
  - `docs/Arcanum.DESIGN.md` §10.19.3 (the disclosure join is part of the evidence step in both gate states), §10.19.9 (the byte-for-byte sentence and a step 0 for the evidence step), §10.19.10 (the mode dimension: `Reject` still refuses erased-only Covenant archives), §10.19.13;
  - `docs/Arcanum.Command.Reference.md` (`reconciliation.erasureApplication`; `reject` still refuses erased-only Covenant archives; the "With the gate on…" paragraph at :1047 no longer lists the disclosure join as part of the gate-on arm).
- Create tests: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureApplicationTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureMatchTests.cs`
- Modify tests:
  - `tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs` (rename `A_restore_that_never_enabled_the_gate_behaves_exactly_as_it_always_did` to `A_restore_that_never_enabled_the_gate_acquires_no_covenant_owner`; its assertions stay);
  - `tests/RetroDownfall.Arcanum.Tests/Backup/BackupCovenantRestoreReconcilerTests.cs` (`Destination_disclosure_evidence_is_joined_rather_than_replaced` becomes `The_covenant_reconciliation_leaves_disclosure_joining_to_the_evidence_step`: with destination count 5 and archive count 2, the staged `JoinedCount` is still 2 after `ReconcileStagedAsync`);
  - `tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs`:
    - a `bool covenantStaging = false` parameter on `CreateRestoreService`, which sets `RestoreStaging = new CovenantRestoreStagingServices(new CovenantRestoreStagingTests.RecordingExclusiveGate(), new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(), <anchor store>, new BackupRestoreJournalInstallationIdentityProvider(credentials), new BackupRestoreJournalKeyProvider(credentials), new BackupRestoreEffectDigestCalculator())`, composed as `CovenantRestoreStagingTests.Harness.CreateService` does;
    - one-line delegations to the Task 13 driver's instance members `EraseLexiconAsync`, `EraseCovenantAsync`, `SetCovenantAsync` and `PostAsync`, and to Task 17's `ReleaseSagaAsync`. Reset-key goes through `PostAsync` on Task 18's two routes, because the driver has no reset-key member;
    - `EvidenceHexAsync(SqliteConnection)`;
  - `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreEffectDigestTests.cs` (the R28 frozen-digest pin);
  - `tests/RetroDownfall.Arcanum.Tests/Cli/BackupRestoreCommandTests.cs`.
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs` (R7): `AllowedCallers` gains `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs` for `ReplaceAllAsync`. `ReplaceAllAsync` itself lives in `MemoryErasureEvidence.cs`, the only file allowed to contain `DELETE FROM memory_erasure_*`.
  - `tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs` (R23): `Persisted_instant_writers_…` gains `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs` (its `covenant_state` and `session_sensitivity_state` `UpdatedAtUtc = $now` bindings).
  - `CovenantArchitectureBoundaryTests.Only_the_outbox_worker_and_rebuilder_write_accelerator_state` (R22): no edit. The applier never names `covenant_search_documents`; it removes them only through `CovenantEntryErasurePlan`, which Task 16 admits.
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs`: re-key both `ReconcileAsync(6)` strings (the member identity at :1912 and the `ExactNonServingProof` name at :1916) to `(7)`. The proof reuses `ReconcileAsync`'s connection and the checkpoint reuses the staged one, so `ExpectedProductionAcquisitionCount` is unchanged (+0).
  - the three benchmark files (+1 `R` line, both digests); `tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs` (`ContentFreeLogFiles` +1 file); `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (regenerated).

**Interfaces:**
- Consumes:
  - Task 1: `MemoryErasureIdentity.ForSaga/ForLexicon/ForCovenant`, `MemoryErasureKey.Fingerprint`/`KeyId`;
  - Task 2: `IMemoryErasureKeyProvider.TryCopyLatched()`;
  - Task 4: `CovenantArtifactPlanRunner.RunAsync(…, SensitiveArtifactKind, artifactKey, CovenantArtifactPlanMode, ct) → CovenantArtifactPlanTally`;
  - Task 6: the R7 deleter scan;
  - Task 12: `GrimoireWalCheckpoint.TruncateAsync`, `CovenantWalCheckpointOutcome.IsTruncated`, and the R8 log scan;
  - Task 13: `SagaRetirementSuppression.CountPairAsync`/`DeletePairAsync(DbConnection, DbTransaction?, SagaMemoryScopeKind, string? campaignId, string content, CancellationToken)` (M8; it reads the staged retirement key through `SagaSuppressionKeyStore.ReadAsync`, never creates it, and returns 0 when it is absent), the Saga erase and retire routes, and `MemoryErasureRouteDriver`;
  - Task 14: the Lexicon erase route; Task 16: the Covenant erase route and `CovenantEntryErasurePlan.ReadSubjectAsync(SqliteConnection, SqliteTransaction, Guid entryId, CancellationToken) → CovenantEntryErasureSubject?` (M9), `RunAsync(…, subject, Delete, CovenantEntryErasureMode.Staged, ct)`, and `ProveAbsentAsync(…, subject, versionIds, ct)`. Of `CovenantEntryErasureTally`, Task 24 reads `VersionIds` (for `ProveAbsentAsync`), `FullTextSecureDeleteVerified` (scrub status), `KeyReclaimed` (must equal `subject.ReclaimsKey`), and `Targets`, whose `covenant_entries` row count is the source of `CovenantEntriesRemoved`; it does not read `ConfirmedVersions`, `ProposedVersions` or `RetainsCampaignMask`;
  - Task 17: the Saga release route and the Covenant operator `set` re-creation; Task 18: the reset-key prepare and apply routes;
  - Task 21: `ExternalDisclosureStateFold.FoldAllUnfoldedAsync(…, RestoreStaging, ct)`; the existing `CovenantDisclosureStateJoiner.JoinIntoStagedAsync`, which now writes through Task 21's store;
  - Task 22: `BackupRestoreErasureEvidence`, its snapshot, and the harness; Task 23: the drain, `CreateArchiveAtAsync`, and the 13-arity `PrepareStagedGenerationAsync`;
  - existing `CovenantIdentitySql.Key(string)`/`Keyed`, `BackupArchivePaths.GrimoireDatabase`, `CovenantRestoreStagingTests.RecordingExclusiveGate`/`RecordingRestoreMarkerLifecycle`.
- Produces:
  - `private Task<Result<BackupRestoreErasureApplicationReceipt>> BackupRestoreService.ReconcileStagedMemoryEvidenceAsync(SqliteConnection staged, BackupRestoreErasureEvidence destination, IReadOnlyList<CovenantDisclosureState> destinationDisclosure, CancellationToken cancellationToken)`
  - `internal static class BackupRestoreErasureEvidenceApplier`:
    - `Task<Result<BackupRestoreErasureApplicationReceipt>> ApplyAsync(SqliteConnection staged, SqliteTransaction transaction, BackupRestoreErasureEvidence destination, MemoryErasureKey? key, IReadOnlyList<CovenantDisclosureState> destinationDisclosure, TimeProvider timeProvider, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPurgeForTests, CancellationToken cancellationToken)`;
    - `Task<Result<BackupRestoreErasureMatches>> FindMatchesAsync(SqliteConnection connection, SqliteTransaction? transaction, MemoryErasureKey key, MemoryErasureEvidenceSnapshot destination, CancellationToken cancellationToken)`.
  - `internal sealed record BackupRestoreErasureMatches(IReadOnlyList<string> SagaIds, IReadOnlyList<string> LexiconIds, IReadOnlyList<Guid> CovenantEntryIds)` with `IsEmpty`.
  - `internal sealed record BackupRestoreErasureApplicationReceipt(long SagaMemoriesRemoved, long LexiconEntriesRemoved, long CovenantEntriesRemoved, long RetirementPairsRemoved, long FingerprintsJoined, long ReceiptsJoined, long ArchiveRowsDropped, bool FullTextVerified, bool CheckpointTruncated)`
  - `public enum BackupRestoreErasureScrubStatus { Verified = 1, ScrubPending = 2, NotApplicable = 3 }` (string-only)
  - `public sealed record BackupRestoreErasureApplication(long SagaMemoriesRemoved, long LexiconEntriesRemoved, long CovenantEntriesRemoved, long RetirementPairsRemoved, long FingerprintsJoined, long ReceiptsJoined, long ArchiveRowsDropped, BackupRestoreErasureScrubStatus Scrub)`
  - `MemoryErasureEvidence.ReplaceAllAsync(SqliteConnection, SqliteTransaction, MemoryErasureEvidenceSnapshot destination, CancellationToken) → Task<long>`: the archive rows dropped, counted as staged fingerprints and receipts whose primary key is absent from `destination` (rows under a foreign `KeyId`, plus same-key rows the destination has released or never had). Subjects are not counted.

- [ ] **Step 1: Write the failing tests**

`BackupRestoreErasureApplicationTests` uses `[Collection("ApiHost")]` and `[SkippableFact]`/`[SkippableTheory]`. Each test establishes its preconditions through the routes (via the route driver), takes a backup, erases, stops the host, then restores with `Confirmed: true`. One `InMemoryOsCredentialStore` is shared throughout. Unless stated otherwise the restore service is gate-off (`covenantStaging: false`), the mode is `ReplaceInstallation`, "M" is `"erase me"` in Global scope, and "digest" is `LiveDatabaseDigestAsync()` before the restore. `restored` is a read-only connection on the committed Grimoire. `EvidenceHexAsync(connection)` is the sorted hex of every fingerprint row, receipt row and subject row.

```csharp
[SkippableFact] public async Task A_restore_removes_a_saga_memory_this_installation_erased_and_keeps_its_twin_elsewhere()
// Insert M (Global) and "erase me" in Campaign B (M2); backup; erase M; restore
Assert.Equal(BackupRestoreStatus.Completed, result.Status);
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM saga_memories WHERE Id = $m"));
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId = $m"));
Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM saga_memories WHERE Id = $m2"));
Assert.Equal(destinationEvidenceHex, await EvidenceHexAsync(restored));
Assert.Equal(new BackupRestoreErasureApplication(1, 0, 0, 0, 1, 1, 0, BackupRestoreErasureScrubStatus.Verified), result.Reconciliation!.ErasureApplication);
// restart the host on the profile: ISagaMemoryStore.InsertAsync("erase me", Global) -> SagaMemoryWriteOutcome.Suppressed
[SkippableFact] public async Task A_restore_of_an_archive_taken_while_the_memory_was_retired_leaves_no_retirement_digest()
// retire M via the retire route; backup; erase M; restore
// SagaRetirementSuppression.CountPairAsync(restored, null, SagaMemoryScopeKind.Global, null, "erase me", CancellationToken.None) == 0; ErasureApplication.RetirementPairsRemoved == 1
[SkippableFact] public async Task A_restore_removes_an_erased_lexicon_entry_and_leaves_no_search_token()
// upsert "Qqz Vault Sigil" with fact "qqzsigiltoken" through ILexiconService.UpsertAsync; backup; erase through the Lexicon route
// lexicon_fts MATCH 'qqzsigiltoken' -> 0 rows; SELECT COUNT(*) FROM lexicon_fts_data WHERE instr(block, 'qqzsigiltoken') > 0 -> 0;
// ErasureApplication.LexiconEntriesRemoved == 1
[SkippableFact] public async Task A_gate_off_restore_removes_an_erased_covenant_entry_and_requires_a_full_rebuild()
// Covenant-enabled host; set "preference.vault"; backup; erase; gate-off restore
// covenant_entries/versions/heads/search documents for the entry == 0;
// covenant_state AppliedDatasetGeneration IS NULL AND AppliedSearchSequence IS NULL AND RebuildStateCode = 2; CovenantEntriesRemoved == 1
[SkippableFact] public async Task Restore_protected_state_keeps_the_archive_covenant_data_except_the_erased_entry()
// covenantStaging: true; set "preference.vault" (E1) and "preference.harbor" (E2), both Global; backup; erase E1;
// ProtectedStateMode = RestoreProtectedState, ProtectedStateConfirmed: true
Assert.Equal(BackupRestoreStatus.Completed, result.Status);
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = 'preference.vault'"));
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM covenant_key_epochs WHERE NormalizedKey = 'preference.vault'")); // reclaimed in staging
Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = 'preference.harbor'"));
Assert.Equal(e2HeadVersionIdBeforeBackup, await ScalarTextAsync("SELECT CurrentVersionId FROM covenant_heads WHERE EntryId = $e2 AND LaneCode = 1"));
Assert.Equal(1, result.Reconciliation!.ErasureApplication!.CovenantEntriesRemoved);
[SkippableFact] public async Task The_default_mode_still_refuses_an_archive_whose_only_covenant_content_was_erased()
// covenantStaging: true; set E1 only; backup; erase E1; Reject mode
Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
Assert.Equal("backup.restore_protected_state_present", Assert.Single(result.Issues).Code);
Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
[SkippableFact] public async Task Purge_protected_state_runs_the_evidence_step_first()
// covenantStaging: true; Covenant host; M labelled through IArtifactSensitivityLedger.LabelAsync; backup; erase M;
// ProtectedStateMode = PurgeProtectedState, ProtectedStateConfirmed: true
// Had the purge run first it would have removed the labelled M, and the evidence step would count 0.
Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);
int evidence = Array.FindIndex(result.Phases, p => p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));
int covenant = Array.FindIndex(result.Phases, p => p.Detail.StartsWith("Stripped the archive's managed-file authority", StringComparison.Ordinal));
Assert.InRange(evidence, 0, covenant - 1);
[SkippableFact] public async Task A_restore_recounts_the_owning_sessions_tainted_artifacts()
// covenantStaging: true; Covenant host; M and N ("keep me") in one Session S, both labelled; backup; erase M; RestoreProtectedState
Assert.Equal(1L, await CountAsync("SELECT TaintedArtifactCount FROM session_sensitivity_state WHERE SessionId = $s"));   // recounted, not folded to 0
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6 AND ArtifactId = $m"));
Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6 AND ArtifactId = $n"));
[SkippableFact] public async Task An_archive_at_core_eleven_is_drained_then_purged()
// CreateArchiveAtAsync("v11", MemoryErasureRestoreHarness.CoreElevenCanonicalFive(), seed: one saga_memories row "erase me", Global, through the columns
// CoreSchemaVersionElevenFixture.Objects declares); on the host: insert and erase M
Assert.Equal(BackupRestoreStatus.Completed, result.Status);
Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, ct));
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM saga_memories WHERE Content = 'erase me' AND ScopeKindCode = 1"));
Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);
// the "Drained staged schema transitions" record precedes the "Applied destination erasure evidence" record
[SkippableFact] public async Task The_destination_evidence_replaces_the_archive_evidence_and_never_revives_a_release()
// erase M; backup (archive holds F and R); release M's content through the Saga release route; restore
// fingerprints == 0, receipts == 1, ArchiveRowsDropped == 1; restart the host: InsertAsync("erase me", Global) -> Written
[SkippableFact] public async Task Archive_evidence_under_a_foreign_key_is_dropped_and_counted()
// erase under K1; backup A1; credentials.Delete(key account); reset-key prepare + apply (Task 18) -> K2 (the K1 rows are discarded);
// erase another memory under K2; restore A1 -> ArchiveRowsDropped == 2 (fingerprint + receipt); EvidenceHex == destination's
[SkippableFact] public async Task A_backup_taken_after_an_operator_reset_of_an_erased_key_keeps_the_re_created_entry()
// §19.2 #6, restore half. covenantStaging: true; set "preference.vault"; erase it; operator set "preference.vault" again (Task 17
// releases F); backup; restore with RestoreProtectedState
Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = 'preference.vault'"));
Assert.Equal(new BackupRestoreErasureApplication(0, 0, 0, 0, 0, 1, 0, BackupRestoreErasureScrubStatus.Verified), result.Reconciliation!.ErasureApplication);
[SkippableTheory] [InlineData("upper-dashed")] [InlineData("lower-dashed")] [InlineData("lower-undashed")]
public async Task A_staged_lexicon_row_in_any_campaign_spelling_matches_the_fingerprint(string spelling)
// Review Focus 1. Campaign C through POST /api/campaigns; upsert and erase "Vault Keeper" in C;
// CreateArchiveAtAsync("spell", GrimoireSchemaVersionChains.Default, seed: a lexicon_entries row "Vault Keeper" whose ScopeCampaignId is C in `spelling`)
Assert.Equal(1, result.Reconciliation!.ErasureApplication!.LexiconEntriesRemoved);
Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM lexicon_entries WHERE Name = 'Vault Keeper'"));
[SkippableFact] public async Task A_core_four_archive_with_a_minority_campaign_spelling_is_drained_then_matched()
// Review Focus 1, "not yet canonicalized by a pending sweep". Saga memory "campaign secret" in Campaign C, erased on the host;
// CreateArchiveAtAsync("v4", CoreSchemaVersionFourFixture.ChainSet(), seed: that memory with CampaignId = C.ToString("D").ToLowerInvariant())
// (Core 4 predates the identity guard, and the v5 IdentitySpellingBackfill canonicalizes it during the drain)
Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);
[SkippableFact] public async Task A_verification_failure_rolls_back_staging_and_leaves_the_installation_unchanged()
// options.AfterErasurePurgeForTests re-inserts a saga_memories row with M's content and scope
Assert.Equal("backup.restore_erasure_verification_failed", Assert.Single(result.Issues).Code);
Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
[SkippableFact] public async Task A_post_commit_match_is_reported_as_reconciliation_required()
// options.BeforePhaseForTests(Reconcile) opens the committed Grimoire read-write and inserts M's content and scope with raw SQL
Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);
Assert.Contains("backup.restore_erasure_verification_failed: an erased item is present in the committed generation.", result.Reconciliation!.Issues);
Assert.Equal(BackupRestoreErasureScrubStatus.ScrubPending, result.Reconciliation.ErasureApplication!.Scrub);
[SkippableFact] public async Task The_extracted_archive_database_is_deleted_before_commit()
// options.BeforePhaseForTests(Commit): <staging>/work/extract/grimoire/arcanum.db, -wal and -shm are all absent
[SkippableFact] public async Task A_gate_off_restore_joins_the_destination_disclosure_buckets_and_folds_staged_tails()
// destination (host stopped): 2 acknowledgements through CovenantDisclosureTransactionWriter.AcknowledgeAsync on the Grimoire opened with
// BackupRestoreDatabaseWorker.OpenAsync -> (1,2) Exact 2; archive: CreateArchiveAtAsync(Default, seed: PreFoldDisclosureHistory backlog 1)
// restored (1,2) == LowerBound, Count 2; SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal == 0;
// ErasureApplication == (0, 0, 0, 0, 0, 0, 0, NotApplicable)
[SkippableFact] public async Task An_import_of_selected_sessions_neither_drains_nor_touches_live_evidence()
// erase M; Session S created through its route; backup; ImportSelectedSessions [S]; restore service over CountingOsCredentialStore(credentials)
Assert.Equal(BackupRestoreStatus.Completed, result.Status);
Assert.Null(result.Reconciliation?.ErasureApplication);
Assert.DoesNotContain(result.Phases, p => p.Detail.StartsWith("Drained", StringComparison.Ordinal) || p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));
Assert.Equal(evidenceHexBefore, await EvidenceHexAsync(live));
Assert.Equal(0, keychain.Calls);                                     // no evidence read, so no Present value, so no drain
```

`BackupRestoreErasureMatchTests` (unit level, `SqliteNativeRuntime.Instance.Initialize()` then an in-memory connection holding bare `saga_memories(Id, ScopeKindCode, CampaignId, Content)`, `lexicon_entries(Id, Name, NameNormalized, ScopeCampaignId)` and `covenant_entries(EntryId, ScopeCode, CampaignId, NormalizedKey)` tables; the key from `MemoryErasureTestKeys.CreateKey`):
- `[Theory] Staged_campaign_spellings_all_match_one_fingerprint(string spelling)` over upper-dashed, lower-dashed, upper-undashed and lower-undashed: a Saga row in Campaign C with `CampaignId` in that spelling matches a destination fingerprint for `ForSaga(Campaign, C, content)`; `SagaIds` has exactly that row id.
- `An_unparseable_campaign_in_a_fingerprinted_store_fails_closed`: `CampaignId = 'not-a-guid'` with a Saga fingerprint present → failure `backup.restore_erasure_verification_failed`; the same row with no Saga fingerprint in the destination → success, empty matches (the store is skipped).
- `Lexicon_matches_on_the_name_not_the_stored_normalization`: `Name = "  vault keeper "`, `NameNormalized = "SOMETHING ELSE"` matches `ForLexicon(null, "Vault Keeper")`.

`BackupRestoreEffectDigestTests.The_restore_effect_digest_and_the_v2_journal_payload_carry_no_erasure_evidence` (R28):
- `typeof(BackupRestoreEffectDigestInput)` constructor parameter names are exactly `ArchiveManifestDigest, ArchivePhysicalIdentityDigest, ProfileNamespaceDigest, InstallationId, DestinationRootIdentityDigest, ConflictMode, ProtectedStateMode, PathMappingVectorDigest, RestoreMasterApiKey, CreateSafetyBackup`;
- `typeof(BackupRestoreJournalPayloadV2)` constructor parameter names are exactly `OwnerOperationId, OwnerOperation, OwnerEffectDigest, ConflictMode, Phase, RestoreRequestDigest, LiveRoot, StagedRoot, DisplacedRoot, ArchiveSource, SafetyBackup, MarkerCleanup`;
- `Calculator.Compute(Input())` equals a golden lowercase hex, captured before any Task 24 production edit: write the assertion against `""`, run once, paste the actual value from the failure message.

`BackupRestoreCommandTests.A_restore_result_renders_the_erasure_application`: the output contains `Erasure applied: removed 1 Saga memories, 0 Lexicon entries, 0 Covenant entries and 0 retirement pairs; joined 1 fingerprints and 1 receipts; dropped 0 archive rows; local scrub verified`. A null `ErasureApplication` prints no `Erasure applied` line.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestoreErasureApplicationTests|FullyQualifiedName~BackupRestoreErasureMatchTests|FullyQualifiedName~BackupRestoreCommandTests|FullyQualifiedName~BackupCovenantRestoreReconcilerTests|FullyQualifiedName~BackupRestoreEffectDigestTests"
```
Expected RED: `ErasureApplication`, the applier and `ReplaceAllAsync` do not exist. Once they compile as stubs, the erased item is restored, the archive's evidence set survives unchanged, and the renamed reconciler test sees `JoinedCount` 5. The effect-digest pin is a characterization guard: it passes before and after this task, and Step 5 proves it load-bearing.

- [ ] **Step 3: Implement**

`ReconcileStagedMemoryEvidenceAsync` runs for every `ReplaceInstallation` with a staged Grimoire, in both gate states. An archive without a Grimoire (`PrepareStagedGenerationAsync` returns early) has nothing to reconcile; its `ErasureApplication` is all zeros with `NotApplicable`.

**Before `BEGIN`:** when `destination.Kind is Present`, `TryCopyLatched()` must return a key whose `KeyId` equals `destination.KeyId`, otherwise refuse with `KeyMissing`. This is not keychain I/O: Task 22's read latched the key in this same provider.

**One `BEGIN IMMEDIATE`** (`staged.BeginTransaction(deferred: false)`) under `CovenantSqliteConnectionInitializer.Instance.Authorize(staged, CovenantFamilyMaintenance)` and `…SensitivityRetentionPurge` (never the entry-erasure kind, which older staged canonical tiers do not know), calling `BackupRestoreErasureEvidenceApplier.ApplyAsync`. Every table is probed with `BackupRestoreDatabaseWorker.TableExistsAsync(…, transaction)`. The order below is the decision:
1. **Secure delete first**, so every later delete is secure: `INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 1)`, and the same for `covenant_fts` when present; read `…_config WHERE k = 'secure-delete'` back; anything but 1 clears `FullTextVerified`.
2. **Fold, then join:** `FoldAllUnfoldedAsync(RestoreStaging)`, then `CovenantDisclosureStateJoiner.JoinIntoStagedAsync(destinationDisclosure)`, skipped when `external_disclosure_state` is absent. The join now happens here in both gate states.
3. **Read before delete:** `FindMatchesAsync` streams each candidate table once and keeps only matching ids; it skips any store the destination holds no fingerprint for.
   - Saga: `Id, ScopeKindCode, CampaignId, Content` → `ForSaga`, the Campaign parsed with `Guid.TryParse` (any spelling yields the same GUID bytes).
   - Lexicon: `Id, Name, ScopeCampaignId` → `ForLexicon`, `''` as Global, the identity always from `Name`.
   - Covenant: `EntryId, ScopeCode, CampaignId, NormalizedKey` → `ForCovenant`.
   - An unparseable Campaign, or an unparseable Covenant `EntryId` that matches, in a fingerprinted store is `VerificationFailed`.
4. **Purge,** mirroring the live erase:
   - Each Saga and Lexicon id goes through `CovenantArtifactPlanRunner.RunAsync(…, CovenantIdentitySql.Key(id), Delete)`, then its `artifact_sensitivity` rows are deleted by `ArtifactKindCode` and `CovenantIdentitySql.Keyed("ArtifactId", …)`, then each owning Session is recounted: `UPDATE session_sensitivity_state SET TaintedArtifactCount = (SELECT COUNT(*) FROM artifact_sensitivity WHERE <Keyed SessionId>), Revision = Revision + 1, UpdatedAtUtc = $now WHERE <Keyed SessionId>` (the statement `CovenantProtectedArtifactErasureKernel` uses at :518, over normalized spellings).
   - For Saga, call `SagaRetirementSuppression.DeletePairAsync(staged, transaction, scope, campaign, content, ct)`; it reads the staged retirement key (never creating it), so a staged Grimoire with no key removes no pair.
   - Each Covenant entry: `ReadSubjectAsync` immediately before its own `RunAsync(…, Delete, Staged)`, because one entry's reclamation changes the next subject's `ReclaimsKey`; `tally.KeyReclaimed` must equal `subject.ReclaimsKey`. After any Covenant purge:
     ```sql
     UPDATE covenant_state SET AppliedDatasetGeneration = NULL, AppliedSearchSequence = NULL, RebuildStateCode = 2, RebuildTargetSequence = NULL, RebuildCursor = NULL, UpdatedAtUtc = $now WHERE StateKey = 1;
     ```
   - Every `$now` binds `UtcInstantText.Format(timeProvider.GetUtcNow())`.
5. **Destination-authoritative evidence:** `MemoryErasureEvidence.ReplaceAllAsync(staged, transaction, destination.Rows or Empty, ct)` counts the dropped rows, deletes staged subjects, then receipts, then fingerprints explicitly (it does not rely on `PRAGMA foreign_keys`), then inserts the destination's rows verbatim, receipts before subjects. It is a no-op returning 0 when the staged tables are absent and the snapshot is empty; absent tables with a non-empty snapshot throw `InvalidOperationException`, which the applier maps to `VerificationFailed`.
6. Run `afterPurgeForTests`.
7. **Post-conditions,** all before `COMMIT`: a second `FindMatchesAsync` is empty; every purged Saga or Lexicon id counts zero in every `RunAsync(Count)` target, zero labels and a zero `CountPairAsync`; every purged Covenant entry's `ProveAbsentAsync(…, tally.VersionIds)` is empty; `EvidenceHex` equals the destination snapshot's; after any Covenant purge the applied tuple is null and `RebuildStateCode = 2`. A failure calls `RollbackAsync(CancellationToken.None)` and returns `VerificationFailed`.

Then `COMMIT`, then `GrimoireWalCheckpoint.TruncateAsync(staged)`; `CheckpointTruncated` is `IsSuccess && Value.IsTruncated`. The step records `Migrate: "Applied destination erasure evidence: removed {saga} Saga, {lexicon} Lexicon and {covenant} Covenant items."`.

**The extracted database.** For `ReplaceInstallation`, `ExecuteAsync` deletes `Path.Combine(extractRoot, BackupArchivePaths.GrimoireDatabase)` plus `-wal`/`-shm` right after `ComposeStagedTree`, then checks `!File.Exists` for all three. Import still reads `extractRoot`, so it is never deleted there.

**The post-commit proof.** When the destination was `Present`, `ReconcileAsync` re-runs `FindMatchesAsync` read-only on the connection it already opens, with `TryCopyLatched()`. A match, or a null key, adds `"backup.restore_erasure_verification_failed: an erased item is present in the committed generation."` to `Issues`, which makes the status `ReconciliationRequired`.

**Scrub status:**

| Status | When |
|---|---|
| `NotApplicable` | nothing was purged, joined or dropped |
| `Verified` | otherwise, when `FullTextVerified`, every Covenant `FullTextSecureDeleteVerified`, `CheckpointTruncated`, the extract-deletion check and the post-commit proof all hold |
| `ScrubPending` | any of those did not |

Inventories: the deleter allow-list, UTC owner list, acquisition re-key, benchmark catalog, R8 scan list and capsules as the Files list says.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 filter, then:
```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~BackupRestore|FullyQualifiedName~CovenantRestoreStagingTests|FullyQualifiedName~BackupCovenantRestoreReconcilerTests|FullyQualifiedName~CovenantEntryErasurePlan|FullyQualifiedName~CovenantArtifactPlanRunner|FullyQualifiedName~MemoryErasureEvidence|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~MemoryErasureStructuralTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~CliJsonContext"
```
Then regenerate the capsules and run `Category=HostedProducerProductionAnalysis`. `Only_the_outbox_worker_and_rebuilder_write_accelerator_state` must stay green with Task 16's allow-list.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must fail |
|---|---|
| Union instead of replace in `ReplaceAllAsync` | `…never_revives_a_release` |
| Skip the retirement-pair delete | `…while_the_memory_was_retired_leaves_no_retirement_digest` |
| Skip the Covenant staged plan | `A_gate_off_restore_removes_an_erased_covenant_entry…` |
| Skip the `FullRebuildRequired` update | the same test |
| Fold the Session count to zero instead of recounting | `A_restore_recounts_the_owning_sessions_tainted_artifacts` |
| Drop post-condition 1 (the rescan) | `A_verification_failure_rolls_back…` |
| Drop the post-commit proof | `A_post_commit_match_is_reported…` |
| Keep the join inside the Covenant arm only | `A_gate_off_restore_joins_the_destination_disclosure_buckets…` |
| Run the evidence step after the Covenant arm | `Purge_protected_state_runs_the_evidence_step_first` |
| Match Lexicon on `NameNormalized` | `Lexicon_matches_on_the_name_not_the_stored_normalization` |
| Match Campaigns on stored text instead of GUID bytes | `Staged_campaign_spellings_all_match_one_fingerprint` (the lower and undashed rows) |
| Add a member to `BackupRestoreJournalPayloadV2` | `The_restore_effect_digest_and_the_v2_journal_payload_carry_no_erasure_evidence` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.ErasureEvidence.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreServiceOptions.cs src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs src/RetroDownfall.Arcanum.Cli/Commands/BackupCommands.cs docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.DESIGN.md docs/Arcanum.Command.Reference.md tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureApplicationTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreErasureMatchTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreEffectDigestTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/CovenantRestoreStagingTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/BackupCovenantRestoreReconcilerTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/MemoryErasureRestoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Cli/BackupRestoreCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureEvidenceDeleterTests.cs tests/RetroDownfall.Arcanum.Tests/Build/MemoryErasureStructuralTests.cs tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs
git commit -m "feat: apply destination erasure evidence inside restore staging" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 25: Purge coordinator per-item classification

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs` (`ResolveLabelsAsync` `catch (SqliteException)` arm at :177; `ExecuteAsync` at :242, its database-owned and managed-file arms; new private `ClassifyAsync`; `Record` at :421 becomes per-item; class remarks)
- Modify: `docs/Arcanum.DESIGN.md` §10.20.2 (the "Three dispositions" paragraph)
- Modify: `docs/Arcanum.API.md` §1 rows `DELETE /api/saga/{id}`, `DELETE /api/saga`, `DELETE /api/memory/lexicon/{name}` (add **409** `Covenant.StaleSnapshot` and **503** `Covenant.Unavailable`)
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSensitiveRetentionPurgeCoordinatorTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteDispositionTests.cs`
- Test (unchanged, must stay green): `CovenantProtectedArtifactErasureKernelTests.An_artifact_whose_label_is_already_gone_is_counted_without_being_deleted_twice`, `CovenantSensitivePurgeBoundaryTests`, `MemoryEndpointTests.Hard_delete_resolves_retired_protected_identity_before_conditional_purge`, `CovenantSensitivePurgeRouteInventoryTests`, `Data/CovenantLabeledRetentionRouteTests`
- Inventories (verified at `fe63b204`; none changes):
  - no new `src` file, so the benchmark catalog, `inputCatalogShapeDigest` and `AdmissionBenchmarkManifest.cs:19` are untouched (R20);
  - no new `OpenConnectionAsync`/`GetOpenCoreConnectionAsync` member, so `ExpectedProductionAcquisitionCount` stays at its post-Task-24 value, **465** in index order (delta 0, R21);
  - no capsule change: `CovenantSensitiveRetentionPurgeCoordinator` has no row in `hosted-grimoire-producer-capsules-v1.tsv`, so the capsule test runs unregenerated and must stay green;
  - no new log template, so `MemoryErasureStructuralTests.ContentFreeLogFiles` is untouched.

**Interfaces:**
- Consumes (Task 4): `ICovenantProtectedArtifactErasureKernel.ErasePageAsync(CovenantProtectedArtifactErasurePage, CovenantArtifactErasureAuthority, CancellationToken)`. Task 4 routes the kernel's database arm through `CovenantArtifactPlanRunner`; the port's contract, including its `(1, 0, 1, None)` "no live label" answer, is unchanged. Also the existing `IArtifactSensitivityLedger.TryReadLabelAsync`.
- Produces: no new type. The behavioural contract every later caller relies on:
  - `CovenantSensitivePurgeOutcome.Results` holds one disposition per target, computed from that target's own `CovenantArtifactErasureProgress`;
  - `Purged` ⇔ `ErasedCount == 1`;
  - any `SqliteException` while reading a label is the `Result` failure `ErrorCodes.Covenant.Unavailable`.

- [ ] **Step 1: Write the failing tests**

`CovenantSensitiveRetentionPurgeCoordinatorTests` builds the coordinator directly from:
- the real `CovenantOperationGateFixture.CreateGate(availability, authority)`;
- `FakeCovenantAvailability` (dataset `CovenantOperationGateFixture.DatasetGeneration`);
- `CovenantErasureAuthorityFixture.Issuer(authority)`;
- a `CovenantSensitivePurgeAuthorityScope` with `CovenantErasureAuthorityFixture.OperatorContext(authority)` published;
- `new CovenantManagedFileErasureRequestReader()`;
- throwing fakes for `ICovenantConnectionSource` and `ICovenantManagedFileErasureKernel` (never reached for Saga and Lexicon).

It has two private fakes:
- `ScriptedLabelLedger`: a queue of answers per artifact (a label, `null`, or `throw new SqliteException("disk I/O error", 10)`); it counts reads per id.
- `ScriptedErasureKernel`: a scripted progress per artifact; it records the item ids of every page.

```csharp
[Fact] public async Task A_label_that_moved_before_the_kernel_is_blocked_as_stale_not_purged()
// ledger(A): [Label(A, labelId: L1), Label(A, labelId: L2)]; kernel(A): new(1, 0, 1, None)
CovenantSensitivePurgeResult a = Assert.Single(outcome.Results);
Assert.Equal(CovenantSensitivePurgeDisposition.Blocked, a.Disposition);
Assert.Equal(CovenantErasureBlocker.AuthorityStale, a.Blocker);
Assert.False(outcome.WasPurged(A)); Assert.False(outcome.RequiresOrdinaryDelete(A));
Assert.Equal(2, ledger.ReadsOf(A));

[Fact] public async Task A_label_removed_before_the_kernel_is_reported_unlabeled_so_the_caller_deletes_it()
// ledger(A): [Label(A, L1), null]; kernel(A): new(1, 0, 1, None)
Assert.Equal(CovenantSensitivePurgeDisposition.Unlabeled, a.Disposition);
Assert.True(outcome.RequiresOrdinaryDelete(A)); Assert.False(outcome.IsBlocked);

[Fact] public async Task Items_before_a_blocked_item_are_purged_and_items_after_it_are_blocked_unexamined()
// four Saga targets A,B,C,D, all labelled Global; kernel: A,B → (1,1,0,None); C → (1,0,0,ManualOwnershipMismatch)
Assert.Equal([Purged, Purged, Blocked, Blocked], outcome.Results.OrderBy(TargetOrder).Select(r => r.Disposition));
Assert.All(outcome.Results.Where(r => r.ArtifactId == C || r.ArtifactId == D),
    r => Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, r.Blocker));
Assert.Equal([[A], [B], [C]], kernel.Pages);          // one item per page; D never dispatched
Assert.Equal(new CovenantArtifactErasureProgress(3, 2, 0, CovenantErasureBlocker.ManualOwnershipMismatch), outcome.Progress);
Assert.Equal(1, ledger.ReadsOf(D));                   // resolved once up front, never re-read

[Fact] public async Task A_storage_failure_while_resolving_labels_fails_closed()
// ledger: A → label; B → throws SqliteException
Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code); Assert.Empty(kernel.Pages);

[Fact] public async Task A_storage_failure_while_re_reading_a_moved_label_fails_closed()
// ledger(A): [Label(A, L1), throws SqliteException]; kernel(A): (1, 0, 1, None)
Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);
```

`CovenantSensitivePurgeRouteDispositionTests` (`[Collection("ApiHost")]`) enters through the mapped routes:
- The host is an `ArcanumWebApplicationFactory` with `Features { Covenant = true, Saga = true }` and an `InMemoryOsCredentialStore`.
- `ServiceOverrides` registers a scoped `IArtifactSensitivityLedger` decorator, `ArmedLabelLedger(new ArtifactSensitivityLedger(sp.GetRequiredService<ICovenantConnectionSource>()), arm)`. The singleton `LabelReadArm` replaces exactly the next `TryReadLabelAsync` answer for one artifact id; every other read passes through.
- Memories are seeded with `ISagaMemoryStore.InsertAsync` (Guid `D` ids) and entries with `ILexiconService.UpsertAsync`.
- Labels are written with the host's `IArtifactSensitivityLedger.LabelAsync(new DerivedArtifactWrite(kind, id, null, null, null, 1, CovenantOperationGateFixture.Digest(11), ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([availability.Current.DatasetGeneration!.Value])))`, as `CovenantLabeledRetentionRouteTests.LabelAsync` does.

```csharp
[SkippableFact] public async Task Deleting_a_Saga_memory_whose_label_moved_is_refused_and_keeps_the_row()
// labelled memory; arm: next read of id returns CovenantErasureAuthorityFixture.Label(id, Guid.NewGuid(), SensitiveArtifactKind.Saga)
Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
Assert.Contains(ErrorCodes.Covenant.StaleSnapshot, body, StringComparison.Ordinal);
Assert.Equal(1, await CountAsync("saga_memories", "Id", id)); Assert.Equal(1, await CountAsync("artifact_sensitivity", "ArtifactId", id));

[SkippableFact] public async Task Deleting_a_Saga_memory_whose_label_vanished_deletes_it_through_the_ordinary_path()
// unlabelled memory; arm: next read returns a fabricated label
Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Equal(0, await CountAsync("saga_memories", "Id", id));

[SkippableFact] public async Task Deleting_a_Lexicon_entry_whose_label_moved_is_refused_and_keeps_the_entry()
// identity from ILexiconService.FindAllLifecycleIdentityForDeletionAsync; kind Lexicon; DELETE /api/memory/lexicon/Moved%20Term
Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(1, await CountAsync("lexicon_entries", "Id", identity));
```

`CountAsync` is an assertion-only raw `count(*)` on a fresh `ReadOnly` lease from the host's `IGrimoireOrdinaryConnectionFactory`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantSensitiveRetentionPurgeCoordinatorTests|FullyQualifiedName~CovenantSensitivePurgeRouteDispositionTests"
```

Expected RED:
- the moved label is `Purged`/`None`, and the route answers 204 with the row present;
- the vanished label answers 204 with the row still present;
- the page test sees one page `[[A, B, C, D]]` and four `Blocked`;
- label-resolution failures return success with fewer labels.

- [ ] **Step 3: Implement**

In `CovenantSensitiveRetentionPurgeCoordinator`:
- `ResolveLabelsAsync`: `catch (SqliteException)` returns `new Error(ErrorCodes.Covenant.Unavailable, "The sensitivity labels could not be read, so nothing was deleted.")`. Rewrite the remark: failing open let the remaining targets read as unlabelled.
- `ExecuteAsync`: walk `databaseOwned`, then `managed`, keeping one `CovenantErasureBlocker stoppedBy`.
  - Once it is set, `Record` each remaining target `Blocked` with `stoppedBy` and dispatch nothing.
  - Otherwise dispatch one target: `ErasePageAsync([candidate], …)` or `EraseManagedFileAsync(candidate, …)`.
  - Return any `Result` failure unchanged; then `progress = progress.Add(step)`, classify, record, and set `stoppedBy` when the disposition is `Blocked`.
- `private async Task<Result<(CovenantSensitivePurgeDisposition Disposition, CovenantErasureBlocker Blocker)>> ClassifyAsync(LabeledTarget candidate, CovenantArtifactErasureProgress step, CancellationToken cancellationToken)`:
  - `step.IsBlocked` → `(Blocked, step.Blocker)`;
  - `step.ErasedCount == 1` → `(Purged, None)`;
  - otherwise re-read the label through the same `SqliteException` → `Covenant.Unavailable` wrapper: `null` → `(Unlabeled, None)`, present → `(Blocked, AuthorityStale)`.
- `ClassifyAsync` runs inside `ExecuteAsync`, so the re-read is under the held `CovenantWriteLease`.
- `Record(Dictionary<Guid, CovenantSensitivePurgeResult>, LabeledTarget, CovenantSensitivePurgeDisposition, CovenantErasureBlocker)` records one target.
- The kernel, `CovenantErasureCoordinator`, `CovenantFamilyReinitializeCoordinator`, `CovenantSensitiveDeletion` and the routes are unchanged. `BlockedError` already maps `AuthorityStale` to 409 `Covenant.StaleSnapshot`.
- Docs:
  - DESIGN §10.20.2: the per-item rule, the unexamined-after-block rule, the re-read under the held lease, and fail-closed label resolution.
  - API §1: the two new refusals on the three DELETE rows.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantSensitiveRetentionPurgeCoordinatorTests|FullyQualifiedName~CovenantSensitivePurgeRouteDispositionTests|FullyQualifiedName~CovenantSensitivePurge|FullyQualifiedName~CovenantProtectedArtifactErasureKernelTests|FullyQualifiedName~CovenantLabeledRetentionRouteTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~SagaEndpointTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
```

Every test is green, and the capsule test passes with no regeneration.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| In `ClassifyAsync`, replace `step.ErasedCount == 1` with `!step.IsBlocked` | `A_label_that_moved_before_the_kernel_is_blocked_as_stale_not_purged` and `Deleting_a_Saga_memory_whose_label_vanished_deletes_it_through_the_ordinary_path` |
| Make the "present on re-read" arm answer `Unlabeled` | `A_label_that_moved_before_the_kernel_is_blocked_as_stale_not_purged` |
| Delete the `stoppedBy` short-circuit | `Items_before_a_blocked_item_are_purged_and_items_after_it_are_blocked_unexamined` |
| Dispatch all database-owned items in one page again | the same page test |
| Restore `return Result<List<LabeledTarget>>.Success(labeled);` in the catch arm | `A_storage_failure_while_resolving_labels_fails_closed` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs docs/Arcanum.DESIGN.md docs/Arcanum.API.md tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSensitiveRetentionPurgeCoordinatorTests.cs tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteDispositionTests.cs
git commit -m "fix: classify each sensitivity purge target from its own erasure progress" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 26: #78: Saga lifecycle in search, list, explain and prune dry-run

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs`
  - `[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SagaRetrievalEligibility>))]` on `SagaRetrievalEligibility` (:27);
  - a new `SagaRetrievalEligibilityClassifier` in the same file. Using the existing file keeps the benchmark catalog, `inputCatalogShapeDigest` and `AdmissionBenchmarkManifest.cs:19` unchanged (R20).
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs` (delete `ClassifyEligibility` at :234; the `ShowAsync` site at :211 calls the classifier)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs` (the inline ternary at :1945 becomes a classifier call over `new SagaMemoryCurationRow(memory, lifecycle, hasEmbedding)`)
- Modify: `src/RetroDownfall.Arcanum.Core/Memory/MemoryDtos.cs` (`MemorySearchResultDto` at :118)
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs` (two members; rewrite the `ListAsync` remark)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs` (factor the `ListAsync` builder at :305; implement both members)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs` (`SearchSagaAsync` at :1240; the Saga row of `BuildExplainAsync` at :335)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs` (the Saga lifecycle line in `Search`, beside the Lexicon one at :373; `LexiconLifecycleText` at :582 becomes the shared `LifecycleText`)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/SagaCommands.cs` (`List`: `State` column)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs` (`WritePlan` at :445)
- Modify: `docs/Arcanum.API.md`
  - §1 rows `GET /api/saga`, `POST /api/memory/search`, `GET /api/memory/explain[/{sessionId}]`;
  - §8.20 "Dry-run plan" paragraph (`sagaCuration`);
  - §8.30 (string-only `eligibility`, which also reaches the Saga review items of §8.34).
- Modify: `docs/Arcanum.Command.Reference.md` (the `arcanum saga list` row and the `arcanum memory search` row, located by command, not line number)
- Modify (closed inventories, R21):
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (two rows);
  - `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+2** over its value after Task 25: 465 → **467** when Tasks 1–25 land in index order. The chain is 446 at `fe63b204`, 449 after Task 7, 450 after Task 8, 451 after Task 12, 454 after Task 13, 456 after Task 14, 457 after Task 16, 459 after Task 17, 464 after Task 18, 465 after Task 22, and Tasks 23–25 add none.)
- Test: `tests/RetroDownfall.Arcanum.Tests/Weave/SagaCurationServiceTests.cs` (repoint `ClassifyEligibility_orders_retired_then_ownership_then_embedding_then_eligible` at :617 and its XML-doc `cref` at :595; two new tests)
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs` (two new tests; `SaturatingSagaMemoryStore` at :1222 implements both members, and `ListCurationRowsAsync` records `limit` exactly as `ListAsync` did)
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/SagaCommandTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs`
- Test fakes (every other `ISagaMemoryStore` implementation, verified by grep): `Intelligence/WizardIntelligenceProviderTests.cs` (`FakeSagaMemoryStore`, :7652), `Intelligence/WizardIntelligenceProviderFallbackTests.cs` (`NoopSagaMemoryStore`, :1406), `Mcp/ReadSagaToolTests.cs` (`FakeSagaMemoryStore`, :491)
- Capsules: `SagaMemoryStore`'s capsule rows cover only `InsertCoreAsync`, `InsertProvenanceAsync` and `OpenConnectionAsync`, none of which this task edits. The capsule test runs unregenerated and must stay green.

**Interfaces:**
- Consumes: no other task's type. It keeps every `ISagaMemoryStore` member earlier tasks added (Tasks 4 and 7).
- Produces:
  - `public static class SagaRetrievalEligibilityClassifier { public static SagaRetrievalEligibility Classify(SagaMemoryCurationRow row); }` in `RetroDownfall.Arcanum.Core.Weave`
  - `Task<SagaMemoryCurationRow[]> ListCurationRowsAsync(string? query, Guid? sessionId, MemoryScope scope, int limit, int offset, CancellationToken cancellationToken)` and `Task<bool> AnyRetrievableAsync(MemoryScope scope, CancellationToken cancellationToken)` on `ISagaMemoryStore`
  - `MemorySearchResultDto(…, MemorySearchActionDto? Action = null, SagaMemoryLifecycle? SagaLifecycle = null, SagaRetrievalEligibility? SagaEligibility = null)`
  - `SagaCommands.DescribeState(SagaMemoryDto) → string`

- [ ] **Step 1: Write the failing tests**

In `SagaCurationServiceTests`, point the existing theory, and the `cref` in its XML doc, at `SagaRetrievalEligibilityClassifier.Classify`. Add the two rows the ladder does not yet force apart:
- `[InlineData(SagaMemoryScopeKind.LegacyUnresolved, true, false, SagaRetrievalEligibility.Retired)]`
- `[InlineData(SagaMemoryScopeKind.Unclassified, false, true, SagaRetrievalEligibility.OwnershipUnresolved)]`

(`(Campaign, false, false) → EmbeddingMissing` is already a row.)

Add:
```csharp
[Fact] public void Saga_eligibility_is_written_as_its_name_and_a_number_is_refused()
{
    Assert.Equal("\"Retired\"", JsonSerializer.Serialize(SagaRetrievalEligibility.Retired, ArcanumJsonContext.Default.SagaRetrievalEligibility));
    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("2", ArcanumJsonContext.Default.SagaRetrievalEligibility));
}

[Fact] public void Only_the_core_classifier_decides_saga_eligibility()
{
    // Roslyn over every src/**/*.cs (bin/obj excluded, NativeSqlCipherTestPaths.RepositoryRoot()):
    // the files holding a MemberAccessExpressionSyntax whose expression is the identifier SagaRetrievalEligibility.
    // Doc-comment crefs are structured trivia, so DescendantNodes() without descendIntoTrivia skips them.
    Assert.Equal(["src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs"], files);
}
```

`MemoryEndpointTests`. These are hosted tests with Saga and Embeddings on and a fixed-vector `IWeaveService`, built as `SagaCurationEndpointTests.CreateEnabledFactory` builds them. Memories are seeded as that file's `SeedMemoryAsync` seeds them.

```csharp
[SkippableFact] public async Task Search_marks_retired_Saga_hits_with_lifecycle_and_eligibility()
// seed "mem-live" and "mem-retired" ("operator likes tea"); POST /api/memory/saga/mem-live/pin;
// POST /api/memory/saga/mem-retired/retire with the contentHash from GET /api/memory/saga/mem-retired
// POST /api/memory/search { query: "tea", scope: saga }
Assert.Equal(SagaRetrievalEligibility.Eligible, live.SagaEligibility); Assert.NotNull(live.SagaLifecycle!.PinnedAtUtc); Assert.Null(live.SagaLifecycle.RetiredAtUtc);
Assert.Equal(SagaRetrievalEligibility.Retired, retired.SagaEligibility); Assert.NotNull(retired.SagaLifecycle!.RetiredAtUtc);
Assert.Contains("\"sagaEligibility\":\"Retired\"", body, StringComparison.Ordinal);
Assert.EndsWith("; retired", retired.Provenance, StringComparison.Ordinal);
Assert.False(live.Provenance.EndsWith("; retired", StringComparison.Ordinal));

[SkippableFact] public async Task Explain_excludes_retired_and_out_of_scope_Saga_rows()
// (1) one Global embedded memory → GET /api/memory/explain: the "Saga" MemoryEligibilityDto has Eligible == true
// (2) retire it → Eligible == false, while GET /api/memory/status still reports the Saga store's Count == 1
// (3) a second host with Campaign scoping on: a session bound to Campaign A; the only memory belongs to Campaign B,
//     seeded as SagaEndpointTests.Divine_WhenCampaignScopingIsOn_ReturnsThisSessionsCampaignAndTheGlobalMemoriesOnly does
//     → GET /api/memory/explain/{sessionA}: Saga Eligible == false; adding one Global memory → Eligible == true
```

In `SagaCurationEndpointTests.A_retired_memory_stops_reaching_retrieval_and_stays_visible_marked_retired`, also assert that the raw detail body contains `"eligibility":"Retired"`.

CLI tests:
```csharp
[Theory][InlineData(false)][InlineData(true)]
public void Generic_plain_search_identifies_saga_lifecycle(bool retired)
// Saga hit: SagaLifecycle(retired ? 2026-09-20T12:34:56Z : null, 2026-09-21T01:02:03Z), SagaEligibility retired ? Retired : Eligible
Assert.Contains(retired
    ? "Retrieval: Retired; retired: 2026-09-20 12:34:56Z; pinned: 2026-09-21 01:02:03Z"
    : "Retrieval: Eligible; retired: not retired; pinned: 2026-09-21 01:02:03Z", sagaBlock, StringComparison.Ordinal);
[Fact] public void Generic_plain_search_does_not_infer_eligibility_from_missing_saga_metadata()
// a Saga hit with both members null → contains "Retrieval: unknown; retired: unknown; pinned: unknown"; DoesNotContain "Eligible"
```

- Update `Generic_plain_search_identifies_lexicon_lifecycle_without_changing_other_stores` (`MemoryCommandTests.cs:82`). Its expected Saga block gains the fifth line `  Retrieval: unknown; retired: unknown; pinned: unknown`.
- New `SagaCommandTests.Saga_list_marks_retired_and_pinned_memories`: four `SagaMemoryDto` rows (none; retired; pinned; both). The output has a `State` header, and the rows read `active`, `retired`, `pinned` and `retired, pinned`.
- New `DataRetentionCommandTests.Prune_dry_run_renders_Saga_pin_inventory(bool json)`, beside `Prune_dry_run_renders_Lexicon_pin_inventory` at :47. The plan JSON carries `"sagaCuration":{"pinnedRows":4,"pinnedRowsExemptFromPlan":2}` and the Lexicon inventory.
  - JSON mode: `sagaCuration.pinnedRows == 4`.
  - Plain mode: `Saga pins: 4; exempt from this plan: 2`, printed before `Lexicon pins:`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~SagaCurationServiceTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~SagaCurationEndpointTests|FullyQualifiedName~MemoryCommandTests|FullyQualifiedName~SagaCommandTests|FullyQualifiedName~DataRetentionCommandTests"
```

Expected RED:
- The first pass fails to compile: the classifier, the two store members and the DTO members do not exist.
- With the classifier and members added as stubs (the classifier throws; both store members throw; the DTO members are present but never set):
  - the classifier theory and the source pin fail;
  - eligibility serializes as `2`;
  - search leaves `SagaEligibility` null;
  - explain reports Saga eligible after retirement and for the out-of-scope Campaign;
  - the CLI prints no Saga lifecycle line, `State` column or Saga pin line.

- [ ] **Step 3: Implement**

- **Classifier.** Move the existing four-arm order verbatim, with its remark: retired → `Unclassified`/`LegacyUnresolved` → no embedding → eligible. Both call sites use it.
- **`SagaMemoryStore`:**
  - factor `ListAsync`'s SQL into `private static void BuildListCommand(DbCommand cmd, string? query, Guid? sessionId, MemoryScope scope, int limit, int offset, bool includeEmbeddingProbe)`. `ListAsync` keeps its own `OpenConnectionAsync` call and its inventory row `ListAsync(6)`;
  - `ListCurationRowsAsync` opens through the same private `OpenConnectionAsync`, calls the builder with the extra projected column `EXISTS(SELECT 1 FROM "saga_memory_embeddings" e WHERE e."MemoryId" = m."Id")`, and returns `new SagaMemoryCurationRow(ReadMemory(reader), new(memory.RetiredAtUtc, memory.PinnedAtUtc), hasEmbedding)`;
  - `AnyRetrievableAsync` mirrors retrieval's own choice in `WizardIntelligenceProvider`: `SearchCampaignScopedAsync` over `MemoryScope.ToSagaScope()` when the scope is enforced, and unscoped `SearchAsync` otherwise. It binds the Campaign in the canonical uppercase `D` form, as `ListAsync` and `DivinationService` do:

```sql
SELECT EXISTS (SELECT 1 FROM saga_memory_embeddings e INNER JOIN saga_memories m ON m.Id = e.MemoryId
  WHERE m.RetiredAtUtc IS NULL
    AND (@enforced = 0 OR m.ScopeKindCode = @globalKind
         OR (m.ScopeKindCode = @campaignKind AND m.CampaignId = @campaignId)));
```

- **Endpoints:**
  - `SearchSagaAsync` reads `ListCurationRowsAsync` and sets `SagaLifecycle: row.Lifecycle` and `SagaEligibility: SagaRetrievalEligibilityClassifier.Classify(row)`. It appends `"; retired"` to a retired row's provenance.
  - `BuildExplainAsync`: the Saga condition becomes `stores["Saga"].Enabled && await context.RequestServices.GetRequiredService<ISagaMemoryStore>().AnyRetrievableAsync(scope, context.RequestAborted)`. The status count is unchanged: status reports what is stored, and explain reports what a turn can reach.
- **CLI:**
  - `LifecycleText(bool known, DateTimeOffset? retiredAtUtc, DateTimeOffset? pinnedAtUtc, string? eligibility)` serves both stores;
  - `SagaCommands.DescribeState(SagaMemoryDto)` returns `active`, `retired`, `pinned` or `retired, pinned`;
  - `WritePlan` prints the Saga pin line first, from `plan.SagaCuration`.
- **Inventories.** Add two acquisition rows, classified like `ListAsync(6)` (`GrimoirePathAuthority.LiveGrimoire`, `GrimoireAcquisitionKind.ServingRawOrdinary`, `GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory`, `null`):
  - `new("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", "SagaMemoryStore", "ListCurationRowsAsync(6)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)")`;
  - the same shape for `"AnyRetrievableAsync(2)"`.

  Then set `ExpectedProductionAcquisitionCount` to the previous value + 2.
- **Docs, with no tracker numbers:**
  - API §8.30: `eligibility` is written as its name (`"Retired"`), on `GET /api/memory/saga/{id}` and on the Saga review items of §8.34, and a numeric value is refused on input.
  - API §8.20 "Dry-run plan": optional `sagaCuration` contains `pinnedRows` and `pinnedRowsExemptFromPlan` (§8.30), beside the existing `lexiconCuration` sentence.
  - API §1:
    - `GET /api/saga`: each row carries `retiredAtUtc` and `pinnedAtUtc`.
    - `POST /api/memory/search`: Saga hits carry `sagaLifecycle` and `sagaEligibility`, and a retired hit's `provenance` ends `; retired`.
    - `GET /api/memory/explain`: Saga is eligible only when at least one non-retired, embedded memory is retrievable in the session's scope, not whenever rows exist.
  - Command Reference, `arcanum saga list` row: replace "and this table has no column that marks it: … changes what a turn can recall without changing what this command prints" with: "A `State` column prints `active`, `retired`, `pinned`, or `retired, pinned`. A retired memory is still listed, because the listing reads the memory rows rather than the embeddings retrieval ranks through."
  - Command Reference, `arcanum memory search` row: the Explanation cell becomes, in full, "Search persisted memory with an explicit or displayed scope. Every Saga and Lexicon hit prints a `Retrieval:` line with its eligibility, retirement time, and pin time, and a retired Saga hit's provenance ends `; retired`. Saga and Lexicon hits also print `Next action:` with the identity to pass to `arcanum memory saga show <id>` or `arcanum memory lexicon show <name> [--campaign <id>]`; session, attachment, and workspace hits remain informational. Covenant is intentionally excluded and uses its protected command below." The options cell is unchanged. This corrects the old claim that a `Next:` command is printed; the CLI prints `Next action:` plus the identity.
  - `ISagaMemoryStore.ListAsync` remark: drop "no column marks it", and say that each row carries `RetiredAtUtc` and `PinnedAtUtc`, which `saga list` renders as its `State` column.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 command, adding `|FullyQualifiedName~SagaMemoryReviewServiceTests|FullyQualifiedName~MemorySagaCurationCommandTests|FullyQualifiedName~ReadSagaToolTests|FullyQualifiedName~WizardIntelligenceProvider|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~MemoryReviewEndpointTests|FullyQualifiedName~SagaEndpointTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests`.

Everything is green. `GrimoireConnectionAcquisitionInventoryTests` passes at the new count, and the capsule test passes unregenerated.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| In the classifier, swap the retired and ownership checks | the `(LegacyUnresolved, true, false) → Retired` and `(Unclassified, true, true) → Retired` theory rows |
| Remove the converter attribute | `Saga_eligibility_is_written_as_its_name_and_a_number_is_refused` and `Search_marks_retired_Saga_hits_with_lifecycle_and_eligibility` |
| Restore the inline ternary in `SagaMemoryReviewService` | `Only_the_core_classifier_decides_saga_eligibility` |
| Put the `COUNT(*) FROM saga_memories` condition back in explain | `Explain_excludes_retired_and_out_of_scope_Saga_rows` (arm 2) |
| Drop the ownership predicate from `AnyRetrievableAsync` | `Explain_excludes_retired_and_out_of_scope_Saga_rows` (arm 3) |
| Delete the Saga branch in `MemoryCommands.Search` | `Generic_plain_search_identifies_saga_lifecycle` |
| Delete the Saga pin line | `Prune_dry_run_renders_Saga_pin_inventory` |
| Remove the `ListCurationRowsAsync(6)` acquisition row | `GrimoireConnectionAcquisitionInventoryTests` |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryDtos.cs src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/SagaCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs docs/Arcanum.API.md docs/Arcanum.Command.Reference.md tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/SagaCurationServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/SagaCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderFallbackTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/ReadSagaToolTests.cs
git commit -m "feat: report Saga lifecycle in search, list, explain and prune previews" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 27: #78: Covenant show reports exact head identity and curation state

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs`
  - new `CovenantCurationStateDto`;
  - `CovenantDetailDto` (:216) gains two trailing required members.
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantManagementReadContracts.cs` (`CovenantDetail` at :106 gains the same two)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs`
  - a new entry for `CovenantCurationStateDto`;
  - the `CovenantDetailDto` summary becomes "Both lane heads, the key epoch, and each lane's curation state; never content."
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs`
  - new `internal static string BindingEpoch(string normalizedKey)`, the one source of the binding-epoch expression (R31);
  - new `DetailCuration(bool campaignScoped)`;
  - `RetirementTarget`, `CampaignMasks`, `LaneHeadProbe` and `CurationEffectFacts` call `BindingEpoch` instead of the inline expression Task 10 wrote.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs` (`IsPinnedAsync` calls `CovenantStoreSql.BindingEpoch("$key")`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs` (Task 16's retained-mask predicate calls `CovenantStoreSql.BindingEpoch("$key")`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs` (`ReadDetailAsync` at :356: a second statement inside the same read transaction, before the rollback; the `CovenantDetail` construction at :430)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs` (the `CovenantDetailDto` construction at :261)
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs` (`[JsonSerializable(typeof(CovenantCurationStateDto))]`)
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs` (`CovenantEntryPayload` at :123 and `CovenantShowPayload` at :149; `CliJsonContext` gains `CovenantCurationStateDto`, because `CliJsonContextCoverageTests` requires every payload member type)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs` (`WriteHead` at :810, the `WriteHistoryAsync` line at :729, `Project` at :835, and the `CovenantShowPayload` construction at :646)
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs` (help at :223, :229 and :348)
- Modify: `docs/Arcanum.CommandMap.json` (regenerated)
- Modify: `docs/Arcanum.Command.Reference.md` (located by content: the frozen-payload paragraph under "Dedicated Covenant management commands"; the `show` row; the `correct` row)
- Modify: `docs/Arcanum.API.md` §8.28 (the detail shape)
- Modify: `docs/Arcanum.DESIGN.md` §10.22.6 (only the `show` sentence: content-free by rule, and what it prints)
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCurationLifecycleTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantServiceHarness.cs` (widen the private `AcquireReadAsync` at :362 to `internal`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs` (the detail fixture at :1383; `History_prints_each_revisions_operation_and_origin` at :712; `A_lookup_publishes_the_documented_show_payload_with_the_history_it_was_asked_for` at :875)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs` (new `The_binding_epoch_expression_has_one_source`)
- Test (must stay green): `CovenantPublicContractInventoryTests`, `ArcanumJsonContextCompletenessTests`, `CliJsonContextCoverageTests`, `CliSurfaceTests`, `CovenantStoreTests`, and Task 10's `CovenantPinEnforcementTests`, `CovenantMaskPlanningTests`, `CovenantRetirementPreflightTests`; Task 16's `CovenantEntryErasurePlanTests`
- Inventories:
  - no new `src` file (R20);
  - no new connection acquisition, because the curation statement runs on `ReadDetailAsync`'s connection and transaction (delta 0; count stays 467, R21);
  - capsules: always regenerated (R31), because `CovenantEntryErasurePlan` is on the restore graph Task 24 changed. The TSV is always listed in the commit; an unchanged tracked file stages as a no-op.

**Interfaces:**
- Consumes:
  - Task 10: the binding epoch `covenant_key_epochs.IncarnationEpoch`; the join rule `COALESCE((SELECT IncarnationEpoch …), 0)`, written inline at `RetirementTarget`, `CampaignMasks`, `LaneHeadProbe`, `CurationEffectFacts` and `IsPinnedAsync`.
  - Task 16: the same expression inline in `CovenantEntryErasurePlan`'s retained-mask predicate. So this task follows Task 16, beyond the Task Index's "10".
- Produces:
  - `internal static string CovenantStoreSql.BindingEpoch(string normalizedKey) => $"COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = {normalizedKey}), 0)"`. Callers pass `"$key"`, or `"h.NormalizedKey"` in `CampaignMasks`.
  - `public sealed record CovenantCurationStateDto(bool IsPinned, bool IsMasked, long Revision) { public static CovenantCurationStateDto None { get; } = new(false, false, 0); }`
  - `CovenantDetailDto(…, CovenantSourcesDto? ProposedSources, CovenantCurationStateDto ConfirmedCuration, CovenantCurationStateDto ProposedCuration)`
  - `CovenantDetail(…, long CanonicalSearchSequence, CovenantCurationStateDto ConfirmedCuration, CovenantCurationStateDto ProposedCuration)`
  - `CovenantEntryPayload(…, DateTimeOffset UpdatedAtUtc, string? RenderedHash = null)`
  - `CovenantShowPayload(…, CovenantVersionDto[] History, CovenantCurationStateDto? ConfirmedCuration = null, CovenantCurationStateDto? ProposedCuration = null)`

- [ ] **Step 1: Write the failing tests**

`CovenantCurationLifecycleTests` uses `CovenantServiceHarness` (production `SetAsync`, `AddCampaignAsync` and `CurateAsync`) and reads through `harness.Fixture.Store.ReadDetailAsync` under `harness.AcquireReadAsync`:
```csharp
[Fact] public async Task Detail_reports_each_lanes_curation_at_the_binding_epoch()
// Global "detail.pinned": SetAsync, then CurateAsync(Pin, Global, null, key) → Confirmed (true, false, 1), Proposed == None
// Global "detail.masked": SetAsync(Global); AddCampaignAsync(C); CurateAsync(Mask, Campaign, C, key)
// → Campaign detail: ConfirmedHead null, ConfirmedCuration == new(false, true, 1), ProposedCuration == None
[Fact] public async Task Detail_reports_a_keyless_pin_after_the_first_operator_set()
// CurateAsync(Pin, Global, null, "detail.keyless") before any set; then SetAsync(Global, "detail.keyless")
Assert.Equal(new CovenantCurationStateDto(true, false, 1), detail.ConfirmedCuration);
```

`CovenantArchitectureBoundaryTests`:
```csharp
[Fact] public void The_binding_epoch_expression_has_one_source()
// over ProductionSourceInventory.Sources(): the files containing "SELECT IncarnationEpoch FROM covenant_key_epochs"
Assert.Equal(["src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs"], files);
Assert.Equal(1, occurrencesInCovenantStoreSql);
```

In `CovenantCommandTests`, the detail fixture carries `ConfirmedCuration: new(true, false, 2)` and `ProposedCuration: CovenantCurationStateDto.None`:
```csharp
[Fact] public async Task Show_prints_version_lifecycle_hash_and_curation()
Assert.Contains($"Confirmed: version {head.VersionId:D}, revision {head.LaneRevision}, {head.Lifecycle}, {head.Origin}, {head.CompiledByteCost} bytes, hash {head.RenderedHash}, updated {head.UpdatedAtUtc:u}", output);
Assert.Contains("  Curation: pinned, not masked, curation revision 2", output);
Assert.Contains("Proposed: none", output);
Assert.Contains("  Curation: not pinned, not masked, curation revision 0", output);
[Fact] public async Task History_prints_each_versions_identity_and_hash()
Assert.Contains($"version {v.VersionId:D}", line); Assert.Contains($"hash {v.RenderedHash}", line);
```

Extend `A_lookup_publishes_the_documented_show_payload_with_the_history_it_was_asked_for` with:
- `confirmed.renderedHash == head.RenderedHash`;
- `confirmedCuration.isPinned == true` and `confirmedCuration.revision == 2`;
- `proposedCuration.isPinned == false`.

Update `History_prints_each_revisions_operation_and_origin` to the new line shape.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantCurationLifecycleTests|FullyQualifiedName~CovenantCommandTests|FullyQualifiedName~CovenantArchitectureBoundaryTests.The_binding_epoch_expression_has_one_source"
```

Expected RED:
- The first pass fails to compile: the DTO members and `CovenantCurationStateDto` do not exist.
- With the members added and `ReadDetailAsync` returning `None` for both lanes:
  - both lifecycle tests fail on their curation assertions;
  - `show` prints neither the version, the hash, nor any curation;
  - the source pin lists `CovenantStoreSql.cs` with more than one occurrence, plus `CovenantMutationKernel.cs` and `CovenantEntryErasurePlan.cs`.

- [ ] **Step 3: Implement**

- **One binding-epoch source (R31).** Add `CovenantStoreSql.BindingEpoch`. Run `rg -l "SELECT IncarnationEpoch FROM covenant_key_epochs" src`. Replace every occurrence it lists with a call. After Tasks 10 and 16, that is:
  - the four `CovenantStoreSql` statements (`CampaignMasks` passes `"h.NormalizedKey"`);
  - `CovenantMutationKernel.IsPinnedAsync`;
  - `CovenantEntryErasurePlan`'s retained-mask predicate.

  `The_binding_epoch_expression_has_one_source` is the definition of done. Every file the command lists is edited and staged, whether or not it is named above.
- **`DetailCuration(bool campaignScoped)`** runs inside the `ReadDetailAsync` transaction, so it shares the lease snapshot:

```csharp
internal static string DetailCuration(bool campaignScoped) => $"""
    SELECT ch.LaneCode, ch.IsPinned, ch.IsMasked, ch.CurrentRevision
    FROM covenant_curation_heads ch
    WHERE ch.CampaignId IS {(campaignScoped ? "$campaign" : "NULL")} AND ch.NormalizedKey = $key
      AND ch.KeyEpoch = {BindingEpoch("$key")};
    """;
```

  A lane with no row reads `CovenantCurationStateDto.None`, the same answer `CurationEffectFacts` gives a preflight.
- **`WriteHead`** becomes `WriteHead(string label, CovenantHeadDto? head, CovenantCurationStateDto curation)` and prints the Step 1 lines. `hash` prints `head.RenderedHash ?? "none"`. The curation line prints even when the head is absent, because a Campaign mask has no head.
- **The history line** is `  revision {r}  version {id:D}  {op}  {origin}  {bytes} bytes  hash {hash ?? "none"}  mutation {m}  {at:u}`.
- **`Project`** sets `RenderedHash`.
- **Help text:**
  - `--target-version`: "The version identity being corrected, as `show` reports it."
  - `--target-hash`: "The rendered hash of the version being corrected, as `show` reports it."
  - curation `--expected-revision`: "The curation revision `show` reports for this lane. Zero is an uncurated subject."
- **Regenerate the command map:**

```bash
ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSurfaceTests.Committed_command_map_matches_the_live_tree"
```

- **Docs:**
  - Command Reference `show` row: `show` prints version id, lane revision, lifecycle, origin, rendered hash, byte cost, and pin, mask and curation revision per lane, and never authored content; `--history` adds each version's id and rendered hash.
  - Command Reference `correct` row: all three target flags now come off plain `show`.
  - Command Reference frozen-payload paragraph: `renderedHash` on `CovenantEntryPayload`, and `confirmedCuration`/`proposedCuration` on `CovenantShowPayload`, are trailing optional members.
  - API §8.28 and DESIGN §10.22.6: `show` is content-free by rule, and curation is read at the binding epoch in the detail snapshot.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 command, adding `|FullyQualifiedName~CovenantStoreTests|FullyQualifiedName~CovenantPinEnforcementTests|FullyQualifiedName~CovenantMaskPlanningTests|FullyQualifiedName~CovenantRetirementPreflightTests|FullyQualifiedName~CovenantEntryErasurePlanTests|FullyQualifiedName~CovenantPublicContractInventoryTests|FullyQualifiedName~CovenantOperatorJourneyTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~CliSurfaceTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests`. Then:

```bash
ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
git diff --stat -- tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
```

Review the capsule diff. A changed row may name only a file this task edits. Any other row means stop and report the drift.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Make `BindingEpoch` read `KeyEpoch` instead of `IncarnationEpoch` | `Detail_reports_a_keyless_pin_after_the_first_operator_set`, Task 10's `A_keyless_pin_survives_the_first_operator_set` and `A_mask_still_suppresses_the_Global_key_after_the_Global_entry_is_corrected`, and Task 16's `A_masked_campaign_confirmed_subject_is_retained_when_the_key_is_not_reclaimed` |
| Inline the expression again in `CovenantMutationKernel.IsPinnedAsync` | `The_binding_epoch_expression_has_one_source` |
| Ignore `LaneCode`, so both lanes take the first row | `Detail_reports_each_lanes_curation_at_the_binding_epoch` (Proposed reads pinned) |
| Drop `RenderedHash` from `Project` | `A_lookup_publishes_the_documented_show_payload_with_the_history_it_was_asked_for` |
| Drop the hash from `WriteHead` | `Show_prints_version_lifecycle_hash_and_curation` |
| Skip the curation line when the head is absent | `Show_prints_version_lifecycle_hash_and_curation` (the Proposed curation line) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantManagementReadContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs docs/Arcanum.CommandMap.json docs/Arcanum.Command.Reference.md docs/Arcanum.API.md docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCurationLifecycleTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantServiceHarness.cs tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: show exact Covenant head identity and lane curation state" -m "Curation is read at the binding epoch in the detail snapshot, and the binding-epoch expression now has one source." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

If `rg` listed any further file in Step 3, add it to this `git add` line.

---

### Task 28: Lifecycle survival and retention inventory

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
  - `RetentionDataClass.MemoryErasureEvidence = 30` (after `Annals = 29`);
  - an explicit `DataRetentionSettingsCatalog.ResolveRule` arm;
  - new `DataRetentionMemoryErasureInventory`;
  - a trailing optional `MemoryErasure` on `DataRetentionStatus` (:218) and on `DataRetentionPlan` (:270).
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionPolicyStore.cs` (the refusal text in `TryUpdateRule`, beside the Covenant arm at :190)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
  - `GetStatusAsync` (:153): add the class item and the inventory;
  - new private `ReadMemoryErasureInventoryAsync(CancellationToken)`.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs` (`BuildFactoryResetPlanCoreAsync` at :141 attaches the inventory after `FinalizePlan` fixes the plan id)
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs` (`WriteStatus` at :347 only)
- Modify: `docs/Arcanum.API.md` §8.20 (the Status and Dry-run paragraphs); `docs/Arcanum.Command.Reference.md` (the `arcanum data status` and `arcanum data reset-memory` rows); `docs/Arcanum.DESIGN.md` §5.4.7: one sentence in the class list naming `MemoryErasureEvidence` verbatim as a never-aged class with no rule (placed where the file describes the other rule-less classes, `Tapestry` and `Covenant`), and the `RetentionDataClass` range sentence ending at `MemoryErasureEvidence = 30`
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRetainedEvidence.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureRetentionClassTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureLifecycleSurvivalTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs` (`Retention_and_reset_enum_codes_preserve_every_existing_value_and_append_covenant` at :118)
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionQuarantineRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs`
- Modify (closed inventories):
  - `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs` (one row) and `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs` (`ExpectedProductionAcquisitionCount` **+1** over Task 27's value: 467 → **468** in index order) (R21);
  - `tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv` (always regenerated, R31: `DataRetentionService` has 761 capsule rows under `DataRetentionSweepHostedService`, and this task edits two of its files).
- Not touched (R4 and R6):
  - `tests/…/Support/MemoryErasureRouteDriver.cs` is Task 13's. This task calls only members Task 13 declares, by their exact names, and puts every other helper in its own test class.
  - `InstallationResetCredentialCatalogTests.cs` and `FullInstallationResetTerminalContinuationTests.cs` are Task 2's. Their erasure-key cases already exist, and this task only runs them.
- Test (must stay green): `DataRetentionServiceTests.GetStatusAsync_ReportsEveryTypedRetentionClassExactlyOnce`, `DataRetentionInventoryAccuracyTests`, `ArcanumJsonContextCompletenessTests`, `NullableInterfaceConstructorDefaultTests` (no new constructor parameter), `MemoryErasureEvidenceDeleterTests` (no new deleter), and Task 2's `InstallationResetCredentialCatalogTests.Catalog_is_closed_to_fixed_configured_and_canonical_mirror_identities`, `InstallationResetCredentialCatalogTests.Installation_reset_active_accounts_require_one_canonical_profile_suffix` and `FullInstallationResetTerminalContinuationTests.An_identity_a_full_reset_must_rotate_that_is_still_present_refuses`
- Benchmark: no new `src` file, so the catalog and both digest sites are unchanged (R20).

**Interfaces:**
- Consumes:
  - Task 2: `ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount`, and the two reset-credential cases above.
  - Task 3: the three tables and `CoreSchemaVersionTwelveFixture`.
  - Task 6: `MemoryErasureEvidence.IsInstalledAsync`, `InsertFingerprintAsync`, `InsertReceiptAsync`, `MemoryErasureReceiptRow`; `MemoryErasureTestKeys`.
  - Task 8: `ILexiconService.DeleteByNameAsync(string name, LexiconScope scope, LexiconDeletionOrigin origin, CancellationToken)`.
  - Task 10: `CovenantCanonicalContentTables.InDeletionOrder`, and `BackupRestoreProtectedStateInspector.CanonicalContentTables` as a property.
  - Task 13: `MemoryErasureRouteDriver.{CreateFactory, InsertSagaAsync, InsertSagaOutcomeAsync, FingerprintCountAsync, EraseSagaAsync, EraseLexiconAsync, EraseCovenantAsync, SetCovenantAsync, PostAsync, ReadDataAsync, ReadErrorCodeAsync}`.
  - Tasks 14 and 16: the Lexicon and Covenant erase routes behind those driver members.
  - Task 17: `MemoryErasureRouteDriver.ReleaseSagaAsync`, used once to change only the fingerprint count between two factory plans.
- Produces:
  - `RetentionDataClass.MemoryErasureEvidence = 30`;
  - `public sealed record DataRetentionMemoryErasureInventory(long Fingerprints, long Receipts, long ReceiptSubjects)`;
  - `DataRetentionStatus(…, DataRetentionCovenantInventory? Covenant = null, DataRetentionMemoryErasureInventory? MemoryErasure = null)`;
  - `DataRetentionPlan(…, DataRetentionLexiconCurationInventory? LexiconCuration = null, DataRetentionMemoryErasureInventory? MemoryErasure = null)`. Both new members are `WhenWritingNull`.
  - Test helper `MemoryErasureRetainedEvidence`, with `CaptureAsync(SqliteConnection, InMemoryOsCredentialStore, CancellationToken) → MemoryErasureRetainedSnapshot` and `AssertRetainedAsync(MemoryErasureRetainedSnapshot, SqliteConnection, InMemoryOsCredentialStore, CancellationToken)`.
    - It compares all three tables byte-for-byte, ordered by primary key.
    - It also compares the key secret, read with `credentials.TryGet("arcanum", ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount)`.
    - The connection is a fresh `ReadOnly` lease from the host's `IGrimoireOrdinaryConnectionFactory`, read in one `BEGIN` snapshot.

- [ ] **Step 1: Write the failing tests**

`MemoryErasureRetentionClassTests` (service level):
```csharp
[Fact] public void The_erasure_evidence_class_has_no_rule_whatever_the_settings_say()
    // every rule in RetentionSettings configured enabled → DataRetentionSettingsCatalog.ResolveRule(settings, MemoryErasureEvidence) is null
[Fact] public void Erasure_evidence_parses_from_its_name_and_never_from_a_numeric_code()
    // DataRetentionDataClassParser: "memory-erasure-evidence" and "MemoryErasureEvidence" → MemoryErasureEvidence; "30" → false
[SkippableFact] public async Task Status_below_core_v13_reports_empty_erasure_evidence()
    // a database installed with CoreSchemaVersionTwelveFixture.ChainSet() (as Task 6's fixture-v12 row builds it),
    // with DataRetentionService constructed as DataRetentionServiceTests constructs it:
    // the MemoryErasureEvidence item has Rows 0, PolicyEnabled false and RetentionDays null, and status.MemoryErasure == new(0, 0, 0)
```

In the enum pin, also assert `Assert.Equal(30, (int)RetentionDataClass.MemoryErasureEvidence)` and `Assert.Equal(31, Enum.GetValues<RetentionDataClass>().Length)` (codes 0–30). `MemoryResetScope` stays at six members.

`MemoryErasureLifecycleSurvivalTests` (`[Collection("ApiHost")]`, `[SkippableTheory]`/`[SkippableFact]` on `GrimoireFixture.SqlCipherAvailable`). Each test owns one `InMemoryOsCredentialStore credentials` and `factory = MemoryErasureRouteDriver.CreateFactory(credentials)`. The precondition erases three items through the routes:
- a Global Saga memory `T = "The toll bridge is closed on feast days."`, through `InsertSagaAsync(factory, T)` and `driver.EraseSagaAsync(id)`;
- a Campaign-C Lexicon entry, through `POST /api/campaigns`, host-scope `ILexiconService.UpsertAsync(…, LexiconScope.ForCampaign(C), …)` and `driver.EraseLexiconAsync(name, C)`;
- a Global Covenant key, through `driver.SetCovenantAsync(Global, null, "survival.key", …)` and `driver.EraseCovenantAsync(Global, null, "survival.key")`.

That leaves 3 fingerprints, 3 receipts and 3 subjects.

```csharp
[SkippableTheory, MemberData(nameof(Paths))]
public async Task Erasure_evidence_and_the_key_survive(MemoryErasureLifecyclePath path)
{
    MemoryErasureRetainedSnapshot before = await MemoryErasureRetainedEvidence.CaptureAsync(connection, credentials, ct);
    Assert.Equal((3, 3, 3), (before.Fingerprints.Count, before.Receipts.Count, before.Subjects.Count)); Assert.NotNull(before.KeySecret);
    await SeedVictimAsync(path);
    await RunAsync(path);                                      // asserts the entry point's success status (table below)
    Assert.True(await VictimGoneAsync(path));
    await MemoryErasureRetainedEvidence.AssertRetainedAsync(before, connection, credentials, ct);
    Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T));   // chokepoint still armed
}
```

Every row asserts its entry point's success. A route answers 200 or 204. A reset or prune apply returns `Blockers` and `Conflicts` empty and `RowsDeleted ≥ 1`, except `ResetWorkspace`, below. Victims are written only through production writers.

| Path | Entry point | Victim (seeded by) |
|---|---|---|
| `RetentionPrune` | `PUT /api/data/retention` `saga-memories` enabled 1 day, then `POST /api/data/prune/plan` and `POST /api/data/prune` with its `planId` | a memory inserted by host-scope `ISagaMemoryStore.InsertAsync` with `createdAt = now - 3 days` |
| `SagaDeleteOne` / `SagaDeleteAll` | `DELETE /api/saga/{id}` / `DELETE /api/saga?confirm=true` | a live memory (`InsertSagaAsync`) |
| `LexiconDelete` | `DELETE /api/memory/lexicon/{name}` | another Global entry (`UpsertAsync`) |
| `AgentLexiconDelete` | host `ILexiconService.DeleteByNameAsync(name, LexiconScope.Global, LexiconDeletionOrigin.Agent, ct)`, the `delete_lexicon` store call | another Global entry |
| `ResetEntry` | `POST /api/data/memory/reset/plan`, then `/reset` with the plan id | a Session entry (`POST /api/sessions`, then `POST /api/sessions/{id}/entries`) |
| `ResetAttachments` | the same routes | an attachment (`POST /api/sessions/{id:guid}/attachments`) |
| `ResetWorkspace` | the same routes | none. No synchronous production writer for workspace chunks is reachable from a test, so this row's guard is the successful apply with no blockers, and `RowsDeleted` is not asserted. |
| `ResetSaga` / `ResetLexicon` / `ResetCovenant` | the same routes | a live memory / a Global entry / a second Covenant key through `SetCovenantAsync` |
| `ResetSagaCampaign` / `ResetLexiconCampaign` | the same routes with `campaignId` = C | a C-scoped memory (from a Session bound to C) / a second C-scoped entry |
| `EmbeddingsResetSaga` / `EmbeddingsResetAll` | `POST /api/embeddings/reset?confirm=true&scope=saga` or `scope=all` | a live memory (the Saga embeddings reset deletes `saga_memories`) |
| `CampaignDeletion` | `DELETE /api/campaigns/{C}`: the Campaign whose Lexicon entry was erased | Campaign C: `GET /api/campaigns/{C}` answers 404 afterwards |
| `SessionDeletion` | `DELETE /api/data/sessions/{id}` | a Session |
| `FactoryReset` | host `IDataRetentionService` plan, then `ApplyAsync(new DataRetentionApplyRequest(new DataRetentionRequest(DataRetentionOperation.FactoryReset), planId, null))` in a scope admitted through `GrimoireRequestAdmissionScope.TryAdmit(GrimoireRequestKind.Finite)`, as `CovenantErasureSameProcessTests`' harness `ApplyFactoryAsync(…, asAdmittedRequest: true)` does | a Session |

More hosted tests in the same class:
```csharp
[SkippableFact] public async Task Status_reports_erasure_evidence_under_a_never_aged_class()
// GET /api/data/status: the MemoryErasureEvidence item has Rows == 9, PolicyEnabled false, RetentionDays null,
// Store == "memory_erasure_fingerprints + memory_erasure_receipts + memory_erasure_receipt_subjects"; MemoryErasure == new(3, 3, 3)
[SkippableFact] public async Task The_factory_plan_states_the_fingerprints_that_remain_in_force()
// host IDataRetentionService factory plan: plan.MemoryErasure == new(3, 3, 3); plan.Items has no MemoryErasureEvidence item;
// then driver.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, T)) deletes one fingerprint and nothing the plan counts:
// a second plan has the same PlanId (the inventory is outside the id) and MemoryErasure == new(2, 3, 3)
[SkippableFact] public async Task A_rule_update_for_erasure_evidence_is_refused_with_its_own_reason()
// PUT /api/data/retention new RetentionRuleUpdateRequest("memory-erasure-evidence", true, 1) → 400 ErrorCodes.Data.InvalidRequest,
// message starts "Erasure evidence has no time-based retention rule"; GET /api/data/retention is unchanged
```

Service-level tests:
- `DataRetentionWorkspaceResetTests.Workspace_reset_preserves_erasure_evidence`: evidence inserted with `MemoryErasureEvidence.InsertFingerprintAsync`/`InsertReceiptAsync` under a `MemoryErasureTestKeys` key. The rows are byte-identical after the reset, and the reset's own victim rows are gone.
- `DataRetentionQuarantineRecoveryTests.MutationRecovery_ForACommittedSagaReset_IgnoresErasureEvidence`: a twin of `MutationRecovery_ForAnInterruptedSagaReset_SeesTheRetirementEvidenceLeftBehind` (:477), with the Saga tables empty and only evidence rows present. Expected: `LongRunningOperationState.Completed`, and the evidence rows unchanged.
- `DataRetentionQuarantineRecoveryTests.Reset_and_restore_lists_never_name_erasure_evidence`:
  - for every `MemoryResetScope`, `DataRetentionService.MemoryResetResidueTables(scope)` holds no `memory_erasure_` name;
  - neither do `BackupRestoreProtectedStateInspector.CanonicalContentTables` or `CovenantCanonicalContentTables.InDeletionOrder`;
  - a Roslyn scan of `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService*.cs` finds `memory_erasure_` string literals only inside `ReadMemoryErasureInventoryAsync`.
- `DataRetentionCommandTests.Status_renders_erasure_evidence_counts(bool json)`:
  - plain output `Erasure evidence: 3 fingerprints, 3 receipts, 3 subjects; never aged out`;
  - JSON output `memoryErasure.fingerprints == 3`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasureRetentionClassTests|FullyQualifiedName~MemoryErasureLifecycleSurvivalTests|FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~DataRetentionQuarantineRecoveryTests|FullyQualifiedName~DataRetentionWorkspaceResetTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~DataRetentionCommandTests" </dev/null
```

Expected RED:
- The first pass fails to compile: the class, the inventory record and the status and plan members are missing.
- With them declared, the status, plan, refusal and CLI tests fail on their assertions (no item, null inventory, the generic refusal text, no CLI line).
- The survival rows run against production code that already keeps evidence. They are characterization rows: they pass once the helpers compile, and Step 5 proves them load-bearing. A row that fails here is a real lifecycle leak. Report it to the controller as a finding with the row name and the failing assertion. It is not fixed inside this task (R31).

- [ ] **Step 3: Implement**

- **The enum member.** Add it with its remark: inventoried and never aged out. Rows are removed only by release, operator re-creation, `reset-key`, restore's destination-authoritative join, or a full installation reset.
- **The rule arm.** Add the explicit `ResolveRule` arm `RetentionDataClass.MemoryErasureEvidence => null`.
- **The policy refusal** (a second arm beside the Covenant one): "Erasure evidence has no time-based retention rule and cannot be given one. It is inventoried by 'data status' and removed only by a release, 'memory erasure reset-key', or a full installation reset."
- **`ReadMemoryErasureInventoryAsync`:**
  - `SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken)`, as `ApplyFactoryResetAsync` does;
  - when `MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken)` is false, return `new(0, 0, 0)`;
  - otherwise run three `SELECT COUNT(*)` statements, one per table.

  This is the only place `DataRetentionService*.cs` names the tables, which is what the Roslyn scan pins.
- **The status item.** Its `Rows` is the sum of the three counts. Its `Store` label (the three table names joined with " + ") is built and returned by `ReadMemoryErasureInventoryAsync` itself, so the `memory_erasure_` literals stay inside that one method as the Roslyn pin requires (N5). Its provenance is content-free: "Content-free erasure fingerprints, receipts and receipt subjects; never aged out." The status is returned `with { MemoryErasure = inventory }`.
- **The factory plan** attaches the inventory after `FinalizePlan` returns, `plan with { MemoryErasure = inventory }`, so the plan id never binds it. No other plan kind carries it.
- **`WriteStatus`** prints the Step 1 line when `status.MemoryErasure` is present. `WritePlan` is not changed. It renders only prune plans (verified: `data prune` is its only caller), and a prune plan never carries the inventory. The installation-reset CLI `data factory-reset --global|--all` removes the key and the Grimoire, so it has no "remain in force" line (see the notes).
- **Acquisition row.** `new("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs", "DataRetentionService", "ReadMemoryErasureInventoryAsync(1)", AcquisitionConstructKind.ProviderOpen, "OpenConnectionAsync", 1, "OpenConnectionAsync(cancellationToken)")`, classified like `CountTableAsync(4)`. `ExpectedProductionAcquisitionCount` + 1.
- **Docs:**
  - API §8.20: `memoryErasure` (`fingerprints`, `receipts`, `receiptSubjects`) on the status and on factory-reset plans. It is absent from every other plan and outside `planId`.
  - DESIGN §5.4.7: the class row.
  - Command Reference: the `data status` and `reset-memory` rows say that erasure evidence survives every reset scope and is removed only by release, `memory erasure reset-key`, or a full installation reset.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 command, adding `|FullyQualifiedName~DataRetentionInventoryAccuracyTests|FullyQualifiedName~DataRetentionEndpointTests|FullyQualifiedName~CovenantLabeledRetentionRouteTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~InstallationResetCredentialCatalogTests|FullyQualifiedName~FullInstallationResetTerminalContinuationTests|FullyQualifiedName~MemoryErasureEvidenceDeleterTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests`. Then regenerate and verify the capsules with the Task 27 Step 4 commands. The capsule diff may touch only `DataRetentionService` rows in `DataRetentionService.cs` or `DataRetentionService.FactoryReset.cs`, plus new `ReadMemoryErasureInventoryAsync`/`MemoryErasureEvidence` rows. Anything else means stop and report.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Add `Whole("memory_erasure_fingerprints")` to the whole-store Saga list in `UntargetedMemoryResetTables` | survival row `ResetSaga` and `Reset_and_restore_lists_never_name_erasure_evidence` |
| Add it to `MemoryResetResidueTables(MemoryResetScope.Saga)` | `MutationRecovery_ForACommittedSagaReset_IgnoresErasureEvidence`, which recovers as `Failed` |
| Add a `memory_erasure_fingerprints` entry to `FactoryDeletionTables` | survival row `FactoryReset` |
| Add `DELETE FROM memory_erasure_fingerprints WHERE StoreCode = 2` to the `EmbeddingsResetService` Saga scope | survival row `EmbeddingsResetSaga`, and Task 6's `Only_the_evidence_store_deletes_evidence_rows` |
| Map the class to `settings.SagaMemories` in `ResolveRule` | `The_erasure_evidence_class_has_no_rule_whatever_the_settings_say` |
| Add a `RetentionDataClass.MemoryErasureEvidence` item with `Rows = inventory.Fingerprints` to the factory plan's items | `The_factory_plan_states_the_fingerprints_that_remain_in_force` (an evidence item appears, and the two plan ids differ) |
| Drop the `IsInstalledAsync` gate in `ReadMemoryErasureInventoryAsync` | `Status_below_core_v13_reports_empty_erasure_evidence` (the table is absent, so the count throws) |

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionPolicyStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.DESIGN.md tests/RetroDownfall.Arcanum.Tests/Support/MemoryErasureRetainedEvidence.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureRetentionClassTests.cs tests/RetroDownfall.Arcanum.Tests/Data/MemoryErasureLifecycleSurvivalTests.cs tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionQuarantineRecoveryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Support/hosted-grimoire-producer-capsules-v1.tsv
git commit -m "feat: inventory erasure evidence and prove it survives every lifecycle path" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 29: Cross-store isolation suite

**Files:**
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/MemoryStoreSnapshot.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Memory/MemoryStoreSnapshotTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryCrossStoreIsolationTests.cs`
- Not touched (R6): `tests/…/Support/MemoryErasureRouteDriver.cs`. The curation and review rows go through Task 13's `PostAsync` and `ReadDataAsync`, with private request builders in `MemoryCrossStoreIsolationTests`.
- No production file. A row that exposes a real cross-store write is reported to the controller as a finding, with the row name and the family that changed. It is not fixed inside this task (R31).
- No inventory changes: tests only.

**Interfaces:**
- Consumes:
  - Task 13: `MemoryErasureRouteDriver.{CreateFactory, InsertSagaAsync, EraseSagaAsync, EraseLexiconAsync, EraseCovenantAsync, SetCovenantAsync, PostAsync, ReadDataAsync, FingerprintCountAsync}`, and the Saga erase routes;
  - Tasks 14 and 16: the Lexicon and Covenant erase routes;
  - Task 17: the release routes and the driver's `ReleaseSagaAsync`, `ReleaseLexiconAsync` and `ReleaseCovenantAsync`;
  - Task 3: the three evidence tables the partition covers;
  - existing: the Saga (`/api/memory/saga/{id}/{correct|retire|reinstate|pin|unpin}`), Lexicon (`/api/memory/lexicon/{correct|retire|reinstate|pin|unpin|show}`) and Covenant curation routes, `/api/memory/{store}/review/list|prepare|apply`, `MemoryReviewStore`, `AnnalSubjectStore` and `SensitiveArtifactKind`.
- Produces (test-only):
  - `[Flags] internal enum MemoryStoreFamily { Saga = 1, Lexicon = 2, Covenant = 4, Shared = 8 }`
  - `internal sealed record MemoryStoreSnapshot(IReadOnlyDictionary<MemoryStoreFamily, IReadOnlyList<string>> Rows, IReadOnlySet<string> Tables)` with:
    - `static Task<MemoryStoreSnapshot> CaptureAsync(SqliteConnection connection, CancellationToken ct)`;
    - `static Task QuiesceAsync(IServiceProvider services, CancellationToken ct)`;
    - `static void AssertOnlyChanged(MemoryStoreSnapshot before, MemoryStoreSnapshot after, MemoryStoreFamily target)`;
    - `int CountRows(MemoryStoreFamily family, string table)`.

- [ ] **Step 1: Write the failing tests**

`MemoryStoreSnapshotTests` run on a head-schema `GrimoireFixture`. Seeding raw rows is allowed here, because the subject is the helper, not a store.
```csharp
[SkippableFact] public async Task Every_memory_table_in_the_head_catalog_has_a_partition_rule()
    // CaptureAsync on a fresh head catalog does not throw;
    // snapshot.Tables equals the sqlite_master tables selected by the discovery rule, and contains
    // "saga_memories", "lexicon_entries", "covenant_entries", "annal_claims", "memory_erasure_fingerprints",
    // "memory_erasure_receipt_subjects", "artifact_sensitivity" and "external_disclosure_receipts"
[SkippableFact] public async Task Shared_tables_are_partitioned_by_their_store_code()
    // annal_claims SubjectStoreCode 1 → CountRows(Saga, "annal_claims") == 1, 2 → CountRows(Lexicon, …) == 1;
    // memory_erasure_fingerprints StoreCode 1/2/3 → one row each in Covenant/Saga/Lexicon;
    // a memory_erasure_receipt_subjects row lands in its receipt's family;
    // artifact_sensitivity kind 6 → Saga, 7 → Lexicon, 1 → Shared
[Fact] public void An_unclassified_annal_or_erasure_table_fails_loudly()
    // the partition function given "annal_future" throws InvalidOperationException whose message contains "annal_future"
[Fact] public void AssertOnlyChanged_fails_when_the_target_did_not_change()   // Assert.ThrowsAny<XunitException>
[Fact] public void AssertOnlyChanged_fails_when_another_family_changed()      // Assert.ThrowsAny<XunitException>, message names the family
[Fact] public void AssertOnlyChanged_passes_when_only_the_target_changed()    // does not throw
```

`MemoryCrossStoreIsolationTests` (`[Collection("ApiHost")]`, `MemoryErasureRouteDriver.CreateFactory(credentials)`):
- Setup asserts `factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue.Features.SagaExtraction == false`. The driver's `Host` leaves the default, so no extraction pass can write during a row.
- Every row seeds all three stores through production paths:
  - Saga: two Global memories through `InsertSagaAsync`;
  - Lexicon: a Global entry and a Campaign-C entry through host-scope `ILexiconService.UpsertAsync`;
  - Covenant: Global keys `iso.a` and `iso.b` through `SetCovenantAsync`.
- It also seeds the row's own precondition: a retire before a reinstate, a pin before an unpin, a mask before an unmask, an erase before a release.
- Snapshots are read on a fresh `ReadOnly` lease from the host's `IGrimoireOrdinaryConnectionFactory`, in one `BEGIN` snapshot.

```csharp
[SkippableTheory, MemberData(nameof(Cases))]
public async Task A_per_item_verb_changes_only_its_own_store(MemoryMutationCase row)
{
    await MemoryStoreSnapshot.QuiesceAsync(host.Services, ct);
    MemoryStoreSnapshot before = await MemoryStoreSnapshot.CaptureAsync(connection, ct);
    await row.Act(driver);                                   // asserts the verb's own success status
    await MemoryStoreSnapshot.QuiesceAsync(host.Services, ct);
    MemoryStoreSnapshot.AssertOnlyChanged(before, await MemoryStoreSnapshot.CaptureAsync(connection, ct), row.Target);
}
[SkippableTheory] [InlineData(MemoryReviewStore.Saga)] [InlineData(MemoryReviewStore.Lexicon)] [InlineData(MemoryReviewStore.Covenant)]
public async Task Erase_and_release_touch_only_their_own_fingerprint_partition(MemoryReviewStore store)
    // erase: AssertOnlyChanged, and CountRows(target, "memory_erasure_fingerprints") rises by exactly 1;
    // release: it falls by exactly 1, the release result is Released with ReleasedCount 1,
    // and every other family's memory_erasure_fingerprints rows are sequence-equal before and after
```

Rows, one per verb:
- Saga: `correct`, `retire`, `reinstate`, `pin`, `unpin` (`/api/memory/saga/{id}/…`, with the `contentHash` from `GET /api/memory/saga/{id}` where the verb takes one).
- Lexicon: `correct`, `retire`, `reinstate`, `pin`, `unpin` (static POSTs, with the target from `POST /api/memory/lexicon/show`).
- Covenant: `set`, `retire`, `correct`, and curate `pin`, `unpin`, `mask`, `unmask` (prepare, then commit; `mask` and `unmask` of `iso.a` in Campaign C).
- Bulk review apply for each store (`/api/memory/{store}/review/list`, then `prepare`, then `apply`). Each decision file covers at least one `Retire` and one `Pin` over the store's seeded items.
- Erase and release for each store.
- Legacy `DELETE /api/saga/{id}` and `DELETE /api/memory/lexicon/{name}`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryStoreSnapshotTests|FullyQualifiedName~MemoryCrossStoreIsolationTests"
```

Expected RED:
- The first pass fails to compile: `MemoryStoreSnapshot` does not exist.
- With the helper stubbed (`CaptureAsync` returns an empty snapshot, and `AssertOnlyChanged` does nothing):
  - the four partition tests fail;
  - the two `AssertOnlyChanged_fails_…` tests fail;
  - the fingerprint-partition theory fails on its `CountRows` deltas.
- The isolation rows are characterization rows over production code that is expected to be isolated. They pass once the helper is real, and Step 5 proves them load-bearing. A row that fails then is a finding (see Files).

- [ ] **Step 3: Implement the helper**

- **Discovery.** `SELECT name, sql FROM sqlite_master WHERE type = 'table'`, keeping:
  - names starting `saga_`, `lexicon_`, `covenant_`, `annal_` or `memory_erasure_`;
  - `artifact_sensitivity`, `external_disclosure_receipts`, `disclosure_subject_state`, `external_disclosure_state` and `disclosure_subject_aggregates`.

  Skip `CREATE VIRTUAL TABLE` rows, but keep the FTS shadow tables.
- **Rendering.** Order rowid tables by `rowid`, and `WITHOUT ROWID` tables by every column. Render each row as `table:` followed by its columns joined with `|`: blobs as hex, and `NULL` as `∅`.
- **Partition** (column names verified against the head `.sql` files):
  - by prefix: `saga_` → Saga; `lexicon_` → Lexicon; `covenant_` and the four disclosure tables → Covenant;
  - `annal_claims`, `annal_heads`, `annal_review_events` and `annal_review_markers` by `SubjectStoreCode` (`AnnalSubjectStore`: 1 Saga, 2 Lexicon);
  - `annal_versions` through `ClaimId`; `annal_dependencies` through `DependentVersionId` → `annal_versions`; `annal_review_decision_receipts` through `ReviewEventSequence` → `annal_review_events.Sequence`;
  - `artifact_sensitivity` by `ArtifactKindCode` (6 → Saga, 7 → Lexicon, else Shared);
  - `memory_erasure_fingerprints` and `memory_erasure_receipts` by `StoreCode` (`MemoryReviewStore`: 1 Covenant, 2 Saga, 3 Lexicon); `memory_erasure_receipt_subjects` through `MutationId`;
  - any other `annal_` or `memory_erasure_` table throws `InvalidOperationException` naming it.
- **Quiesce.** Poll every 50 ms, for at most 30 s, until `ICovenantAvailability.Current.FtsSynchronization == CovenantFtsSynchronizationState.Synchronized` and `SELECT COUNT(*) FROM covenant_search_outbox` returns 0. A timeout fails the test with the last observed state.
- **`AssertOnlyChanged`.** Every family other than `target` is sequence-equal. `target` must differ, which is the vacuous-pass guard.

- [ ] **Step 4: Run the tests to verify they pass**

Re-run the Step 2 command, adding `|FullyQualifiedName~EnvironmentIsolationContractTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~MemoryReviewEndpointTests`.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Add `UPDATE lexicon_entries SET PinnedAtUtc = @now` inside `SagaMemoryStore.SetPinAsync`'s transaction | the Saga `pin` row |
| Make the Saga erase also insert its fingerprint with `StoreCode = 3` | `Erase_and_release_touch_only_their_own_fingerprint_partition(Saga)` |
| Remove the target-differs check | `AssertOnlyChanged_fails_when_the_target_did_not_change` |
| Route every `annal_claims` row to Saga | the Lexicon `correct` row, and `Shared_tables_are_partitioned_by_their_store_code` |
| Drop the `memory_erasure_` prefix from discovery | `Every_memory_table_in_the_head_catalog_has_a_partition_rule` |

- [ ] **Step 6: Commit**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Support/MemoryStoreSnapshot.cs tests/RetroDownfall.Arcanum.Tests/Memory/MemoryStoreSnapshotTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryCrossStoreIsolationTests.cs
git commit -m "test: prove every per-item memory verb leaves the other stores byte-identical" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 30: Design narrative, doc drift, OATH and Engineering records

**Files:**
- Modify: `docs/Arcanum.DESIGN.md` (new §21.15; the sections listed under "DESIGN, sections this task writes" in Step 3)
- Modify: `docs/Arcanum.API.md` (§1: the fourteen Covenant management rows; the wording of the `DELETE /api/saga/{id}`, `DELETE /api/saga` and `DELETE /api/memory/lexicon/{name}` rows)
- Modify: `docs/Arcanum.Command.Reference.md` (the `arcanum saga delete` and `arcanum memory lexicon delete` rows, located by command)
- Modify: `docs/Arcanum.OATH.md` (§2.2 rows; §15.4; §20.5)
- Modify: `docs/Arcanum.Engineering.md` ("Unified data retention and deletion" at :644, "Covenant provider retention and deletion" at :686, "Current operator limitations" at :785)
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs` (delete the dead `CovenantRetention` at :72; rewrite `LexiconRetention` at :77 and `SagaRetention` at :79)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs` (`CovenantRetentionSummary` at :748)
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/SagaCommands.cs` (the delete message at :249); `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs` (the Lexicon delete message at :517)
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs` (help for `lexicon delete` at :129 and `saga delete` at :418); `docs/Arcanum.CommandMap.json` (regenerated)
- Test: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs` (a new test in the nested `ApiReferenceRouteTable` class)
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`; `tests/RetroDownfall.Arcanum.Tests/Cli/SagaCommandTests.cs` (`Saga_delete_calls_delete_endpoint` at :165); `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs` (`Memory_lexicon_delete_is_explicit_and_calls_item_scoped_endpoint_after_yes`)
- Test (must stay green): `DocumentationIssueReferenceTests`, `DocumentationStructureTests`, `CliSurfaceTests`, `HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery` (unregenerated; `MemoryEndpoints` and `CovenantManagementService` have no capsule row)
- Inventories: no new `src` file, no new acquisition (count stays 468), no capsule change.
- Not owned here (verified in Step 3): DEBUGGING key-loss recovery (Task 18, R26), the DEBUGGING restore codes (Task 24), API §8.23/§8.33/§8.35, the Command Reference erase, release and `memory erasure` rows, and the DESIGN sentences other tasks wrote.

**Interfaces:**
- Consumes: every earlier task's shipped behaviour and documentation.
- Produces: no code contract.

- [ ] **Step 1: Write the failing tests**

```csharp
// DocumentationStructureTests.ApiReferenceRouteTable ([Collection("ApiHost")])
[Fact] public void Every_mapped_memory_route_has_a_row_in_the_complete_api_surface_table()
// DocumentedRouteRow over the lines between "## 1. Complete API surface" and the next "## " heading only
// (not §8.28's cell table). Every registered RouteEndpoint whose normalized pattern starts "/api/memory/"
// has a "| METHOD | `path` |" row there. The failure message lists the missing "METHOD path" pairs.

// MemoryEndpointTests
[SkippableFact] public async Task Memory_sources_state_each_store_retention_honestly()
// GET /api/memory/sources: the Saga retention contains "erased", "saga-memories" and "pinned"; the Lexicon one contains
// "erased" and "lexicon-entries"; both still contain "explicitly"; typeof(MemoryEndpoints) has no field named "CovenantRetention"
[SkippableFact] public async Task Covenant_status_states_that_an_operator_can_erase_an_entry()
// Covenant enabled (SettingsOverride Features.Covenant = true): GET /api/memory/status → Covenant!.Retention contains
// "until an operator erases the entry" and "outside every local erasure path"
```

The CLI tests gain two assertions each. `CliTestHarness` routes `AnsiConsole` into `Output`, and Spectre wraps long lines, so each assertion runs over `Regex.Replace(result.Output, @"\s+", " ")`:
- `Saga_delete_calls_delete_endpoint`: `"was deleted. No suppression fingerprint was recorded"` and `"arcanum memory saga erase"`.
- `Memory_lexicon_delete_is_explicit_and_calls_item_scoped_endpoint_after_yes`: `"was deleted, not erased"` and `"arcanum memory lexicon erase"`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~MemoryEndpointTests.Memory_sources_state_each_store_retention_honestly|FullyQualifiedName~MemoryEndpointTests.Covenant_status_states_that_an_operator_can_erase_an_entry|FullyQualifiedName~SagaCommandTests|FullyQualifiedName~MemoryCommandTests"
```

Expected RED:
- the route-table test lists the fourteen Covenant management routes, which appear only in §8.28;
- the retention strings name only deletion;
- the Covenant summary says "retires the entry";
- the Saga CLI says "was forgotten", and the Lexicon CLI says only "was deleted.".

- [ ] **Step 3: Implement**

**First, verify what other tasks own.** Run this before any edit. It stops this task if a section another task owns is missing. That is a defect in the owning task. Report it to the controller, and do not write the section here (R31).

```bash
base=$(mktemp); git show fe63b204:docs/Arcanum.DESIGN.md > "$base"
section() { awk -v n="$2" '/^#+ /{l=index($0," ")-1; if(p&&l<=L)exit; if($2==n){p=1;L=l}} p' "$1"; }
for s in 4.3 10.13 10.17 10.19.3 10.19.9 10.19.10 10.19.13 10.20.2 10.25.1 21.9 21.12; do
  diff -q <(section "$base" "$s") <(section docs/Arcanum.DESIGN.md "$s") >/dev/null && echo "UNCHANGED DESIGN §$s"
done
apibase=$(mktemp); git show fe63b204:docs/Arcanum.API.md > "$apibase"
diff -q <(section "$apibase" 8.33) <(section docs/Arcanum.API.md 8.33) >/dev/null && echo "UNCHANGED API §8.33 (Task 14)"
rg -q "erasure key" docs/Arcanum.DESIGN.md || echo "MISSING DESIGN §5.4 warm-up (Task 6)"
rg -q "Lexicon.SuppressedNameRefused" docs/Arcanum.DESIGN.md || echo "MISSING DESIGN §10.6 write path (Task 8)"
rg -q 'MemoryErasureEvidence = 30' docs/Arcanum.DESIGN.md || echo "MISSING DESIGN §5.4.7 class sentence (Task 28)"
rg -q "content-free by rule" docs/Arcanum.DESIGN.md || echo "MISSING DESIGN §10.22.6 show sentence (Task 27)"
rg -q "^### 8\.35 Selective erasure" docs/Arcanum.API.md || echo "MISSING API §8.35 (Task 13)"
rg -q "MemoryErasure\.ErasureIncomplete" docs/Arcanum.API.md || echo "MISSING API §8.23 codes (Task 1)"
rg -q "releasesErasureFingerprint" docs/Arcanum.API.md || echo "MISSING API set additions (Task 17)"
rg -q "/api/memory/erasure/reset-key" docs/Arcanum.API.md || echo "MISSING API status/scrub/reset-key (Task 18)"
rg -q "memoryErasure" docs/Arcanum.API.md || echo "MISSING API §8.20 memoryErasure (Task 28)"
rg -q "arcanum memory saga erase" docs/Arcanum.Command.Reference.md || echo "MISSING CLI erase rows (Task 19)"
rg -q "arcanum memory erasure reset-key" docs/Arcanum.Command.Reference.md || echo "MISSING CLI memory erasure rows (Task 20)"
rg -q "backup.restore_erasure_evidence_unjoinable" docs/Arcanum.Command.Reference.md || echo "MISSING CLI restore codes (Tasks 22-24)"
rg -q "Recover from a lost or replaced erasure key" docs/Arcanum.DEBUGGING.Human.md || echo "MISSING DEBUGGING key-loss recipe (Task 18)"
rg -q "backup.restore_erasure_verification_failed" docs/Arcanum.DEBUGGING.Human.md || echo "MISSING DEBUGGING restore codes (Task 24)"
```

The expected output is empty. The section checks compare each owned section with its `fe63b204` text. Every token above is absent at `fe63b204`, which was verified, so any hit was written by a later task.

**Code strings:**
- `SagaRetention`: "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `saga-memories` retention rule; pinned memories are exempt from pruning, and retirement keeps a memory inspectable but out of retrieval."
- `LexiconRetention`: "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `lexicon-entries` retention rule; pinned entries are exempt from pruning, and retirement keeps an entry inspectable but out of matching."
- Delete the unused `CovenantRetention` constant.
- `CovenantRetentionSummary`: "Durable immutable versions until an operator erases the entry, its Campaign is deleted, or a Covenant reset, family reinitialize, or installation erasure removes it; retirement keeps history inspectable. Content already sent to a provider is outside every local erasure path."
- The Saga delete message: `$"Saga memory '{id}' was deleted. No suppression fingerprint was recorded, so extraction can add identical content again; use 'arcanum memory saga erase' to erase and suppress."`.
- The Lexicon delete message: `$"Lexicon entity '{name}' was deleted, not erased; use 'arcanum memory lexicon erase' to erase and suppress."`.
- Help: "Delete a single Saga memory without erasing it." and "Delete one explicitly named Lexicon entity without erasing it."
- Regenerate the command map with the Task 27 Step 3 command.

**API §1.** Insert these fourteen rows after the `DELETE /api/memory/lexicon/{name}` row, in this order and with this wording. Each row keeps §8.28 as the owner of the full shape.

| Method | Path | Row text |
|---|---|---|
| POST | `/api/memory/covenant/list` | Page of current heads for one scope (`CovenantListRequest` → `ApiResponse<CovenantPageDto>`; Covenant read authority; protected). |
| POST | `/api/memory/covenant/query` | Free-text query over the protected route, compiled by the Covenant query compiler; `search` reports accelerator health (`CovenantQueryRequest` → `ApiResponse<CovenantPageDto>`). |
| POST | `/api/memory/covenant/detail` | Both lane heads and per-lane curation, never content (`CovenantDetailRequest` → `ApiResponse<CovenantDetailDto>`). |
| POST | `/api/memory/covenant/versions` | One lane's immutable version page (`CovenantVersionsRequest` → `ApiResponse<CovenantVersionPageDto>`). |
| POST | `/api/memory/covenant/sources` | One head's provenance page (`CovenantSourcesRequest` → `ApiResponse<CovenantSourcesDto>`). |
| POST | `/api/memory/covenant/explain` | What a turn in the named Campaign would receive (`CovenantExplainRequest` → `ApiResponse<CovenantExplainDto>`). |
| POST | `/api/memory/covenant/set/prepare` | Operator `set` preflight (`CovenantSetPrepareRequest` → `ApiResponse<CovenantMutationPreflightDto>`; `CovenantManage`). |
| PUT | `/api/memory/covenant` | Commit a prepared `set` (`CovenantSetRequest` → `ApiResponse<CovenantMutationResultDto>`). |
| POST | `/api/memory/covenant/retire/prepare` | Retirement preflight (`CovenantRetirePrepareRequest` → `ApiResponse<CovenantMutationPreflightDto>`). |
| POST | `/api/memory/covenant/retire` | Commit a prepared retirement (`CovenantRetireRequest` → `ApiResponse<CovenantMutationResultDto>`). |
| POST | `/api/memory/covenant/correct/prepare` | Exact-version correction preflight (`CovenantCorrectPrepareRequest` → `ApiResponse<CovenantMutationPreflightDto>`). |
| POST | `/api/memory/covenant/correct` | Commit a prepared correction (`CovenantCorrectRequest` → `ApiResponse<CovenantMutationResultDto>`). |
| POST | `/api/memory/covenant/curate/prepare` | Pin, unpin, mask or unmask preflight (`CovenantCurationPrepareRequest` → `ApiResponse<CovenantCurationPreflightDto>`). |
| POST | `/api/memory/covenant/curate` | Commit a prepared curation change (`CovenantCurationRequest` → `ApiResponse<CovenantCurationResultDto>`). |

The three legacy DELETE rows (which already carry Task 25's 409 and 503) gain: "An unsuppressed delete, not erasure: no fingerprint is recorded, so extraction or an agent can write the same content again; see `/api/memory/{store}/erase` (§8.35)."

**Command Reference.** The `arcanum saga delete` and `arcanum memory lexicon delete` rows read "Deleted, not erased: no erasure fingerprint is recorded, so the same content can be written again. `arcanum memory saga erase` / `arcanum memory lexicon erase` erases and suppresses." Also check that the `arcanum memory search` row carries Task 26's text.

**DESIGN, sections this task writes** (with no tracker numbers):
- §5.4.5a: the Core v13 step, with no sweep; a catalog below v13 reads as no fingerprints; `lexicon_fts` `secure-delete` converged by `CoreGrimoireSchemaDataInitializer`; and the one-time `optimize`.
- §5.4.7:
  - the spec §13 lifecycle rows;
  - the `LexiconEntries` class row becomes: "Lexicon entities/facts and owned FTS/provenance/claims when the entity itself is selected; a pinned entry is never a candidate, and the plan reports how many entries are pinned and how many of those this plan's own cutoff would otherwise have selected (§10.6.3)";
  - the Saga suppression bullet separates the retirement digest from the erasure fingerprint;
  - "no new table enters the residue list".
- §5.4.9: one convergence sentence after Task 23's drain sentence: destination evidence is authoritative; staged sweeps drain first; archive rows that match a destination fingerprint are purged; and archive evidence under a foreign key is dropped.
- §10.6.2: search carries Saga lifecycle and eligibility, as it already does for Lexicon.
- §10.6.3: v13 secure-delete and the one-time merge. The `delete_lexicon` refusals and the bounded oracle are Task 8's sentences in §10.6, which the check above proves present.
- §10.18 (the "The feature is off, and the default is the guarantee" paragraph, :2112 at `fe63b204`): the sentence "a turn already in flight aborts at its required pre-dispatch revalidation, because a provider call that has already left the process cannot be recalled by a configuration edit" becomes "a turn already in flight finishes on the snapshot it was admitted with, because no inference path revalidates its turn lease; a provider call that has already left the process cannot be recalled by a configuration edit."
- §10.20.1: the second never-aged class, `MemoryErasureEvidence`, which has an explicit null rule arm and its own refusal text.
- §10.20.5:
  - `CovenantCanonicalContentTables.InDeletionOrder` includes curation and excludes erasure evidence;
  - the v6 delete guards admit `CovenantEntryErasure` beside owner cleanup and family maintenance;
  - the two new guards on `covenant_key_epochs` and `covenant_curation_heads`.
- §10.21, a new closing paragraph:
  - only the Covenant drains in-flight turns; a Saga memory or Lexicon entry already in a running turn can still be sent until that turn ends;
  - the Covenant drain's availability costs: it waits up to the gate's 30-second bound, and fails with 503 changing nothing when it cannot drain; turns that start during the closure run without Covenant content; and installation-coverage leases (backups, prepares, inventory, the accelerator) fail fast during any closure.
- §10.22.6:
  - "`arcanum memory covenant set|list|show|retire` drives these routes" becomes `set|correct|list|search|show|retire|pin|unpin|mask|unmask|erase|release` plus `review`;
  - the paragraph beginning "Free-text `query` is composed and deliberately unmapped" becomes: `query` is mapped (`POST /api/memory/covenant/query`) and implemented over the protected route, and the page reports accelerator health in its `search` field, so an empty page from a degraded accelerator is distinguishable from an empty Covenant.
- §10.26.2: the subject's key epoch is the binding epoch, `covenant_key_epochs.IncarnationEpoch`, fixed when the key row is created. Ordinary writes never move it, so pins and masks survive the operator's own writes and a keyless pin stays live across the first `set`.
- §10.26.3: "A Global pin is recorded, reported, and enforced at the one place agent authorship could ever arrive; the surfaces that will consult it more widely are the bulk-action and erasure ones" becomes: "A Global pin is recorded, reported by `covenant show` and by the erase preflight's `IsPinned`, and enforced at the one place agent authorship could ever arrive; bulk review and erasure consult it (§21.14, §21.15)."
- §10.26.6:
  - "the report says how many entries are pinned rather than describing an exemption from something that does not run" becomes "the retention report carries no Covenant pin count, and `covenant show` reports each lane's pin, mask, and curation revision instead";
  - the staging pin refusal;
  - operator `set` of an erased key releasing its fingerprint in the same transaction;
  - "Hard erasure of a single entry remains absent: …" becomes "Hard erasure of a single entry is the separate, confirmed `erase` verb (§21.15); retirement remains the tombstone that keeps history inspectable."
- §11.2.1: the credential inventory gains `memory-erasure-fingerprint-key`. It is not profile-namespaced, never exported, and removed only by a full installation reset.
- §16.2: the three Core v13 tables and `idx_disclosure_subject_state_unfolded`.
- §21.13.3: a retirement suppression versus an erasure fingerprint: which key, which lifetime, and which releases each.
- §21.13.6: "There is no hard erasure of a single memory. …" becomes "Hard erasure of one memory and its identical-content class is the separate, confirmed `erase` verb (§21.15). Retirement keeps the row inspectable and reversible; erasure removes it and leaves only a content-free fingerprint."
- The testing-table row "Eight exclusive operation codes and three dispositions" becomes "Nine exclusive operation codes".

**New §21.15 "Selective hard erasure and erasure fingerprints"**, directly after §21.14, inside chapter 21. Its subsections:
1. **The key and its custody.** The OS credential store; the latch; one create under a lock; no keychain I/O at composition. Before readiness, when any fingerprint row exists, the host resolves the key once so that automatic writers are not withheld until an operator acts. On macOS that keychain read can raise a prompt at startup on an installation that has erased something (R11).
2. **The fingerprint grammar.** Identities use exact bytes: `café` in NFC and in NFD are different identities. Release also tries the `Trim()` form.
3. **The two-phase guard and its chokepoints.** Also: a lost or foreign key fails closed as `KeyLost`, and an unreadable key as `KeyUnavailable`.
4. **Prepare and apply, tokens and receipts.**
5. **Verified versus `RowsRemovedScrubPending`, and `/scrub`.**
6. **External exposure and retained local copies.**
7. **Covenant entry erasure:**
   - the whole entry is the unit;
   - the drain;
   - key reclamation, which advances the reclamation epoch, so every outstanding Covenant preflight goes stale installation-wide, and the preflight says so;
   - legacy `NoChange` receipts in the entry's window are deleted conservatively. A very late replay of another key's `NoChange` mutation in that window therefore answers `Covenant.StaleSnapshot` instead of replaying (R29).
8. **Lifecycle and restore.**
9. **Scope boundaries:**
   - a Global Covenant fingerprint never blocks a Campaign proposal;
   - a `LegacyUnresolved` fingerprint stops matching after resolution;
   - content erased in Campaign A stays extractable in Global scope or in Campaign B.
10. **Key loss and `reset-key`.**
11. **What is deliberately absent:** no generic `memory erase`; no erase in search or review actions; no external revocation; and no managed-file artifact owned by any of the three stores.

**OATH:**
- §2.2:
  - The `#78` row becomes `| **#78** | Epic, landed | Memory curation, via #96, #97, #98, #99, and #100, all landed. Its carve-outs are recorded rather than delivered: scoped agent recall is #101, and Long Rest consolidation and decay are #93 and #95. Covenant \`show\` is content-free by rule rather than printing full content. |`.
  - Add `| **#98** | L, landed | Lexicon exact-target correct, retire, reinstate, pin, and unpin. |`.
  - Add `| **#100** | XL, landed | Selective hard erasure with OS-keyed erasure fingerprints, restore suppression, and the #225 remainder. |`.
- §15.4: the selective-hard-erasure bullet names verified local erasure, content-free OS-keyed fingerprints and release, and no external revocation.
- §20.5:
  - the `show` row adds "for the Covenant, content-free by rule: exact head identity, rendered hash, lifecycle, and per-lane curation";
  - the `forget` row's "hard-delete behind an explicit second flag" becomes "hard erasure is the separate, confirmed `erase` verb, and `release` lifts its fingerprint".

**Engineering:**
- "Unified data retention and deletion": the erasure verbs and their outcomes; erasure evidence as a never-aged class; and the legacy deletes as unsuppressed.
- "Covenant provider retention and deletion": entry erasure, the drain, and the disclosure fold.
- "Current operator limitations": the in-flight limit; byte-exact identity; positional names left in shell history; and the startup keychain read.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~CliSurfaceTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~SagaCommandTests|FullyQualifiedName~MemoryCommandTests|FullyQualifiedName~CovenantStatusTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery"
git diff --check
```

Then re-run the Step 3 verification block. Its output must still be empty.

- [ ] **Step 5: Mutation check**

| Temporary break | Test that must turn RED |
|---|---|
| Delete the `/api/memory/covenant/versions` row from §1 | `Every_mapped_memory_route_has_a_row_in_the_complete_api_surface_table` |
| Restore "Durable associative memory until that Saga memory is explicitly deleted." | `Memory_sources_state_each_store_retention_honestly` |
| Restore "retires the entry" in `CovenantRetentionSummary` | `Covenant_status_states_that_an_operator_can_erase_an_entry` |
| Restore "was forgotten." | `Saga_delete_calls_delete_endpoint` |
| Add "(#100)" to one DESIGN §21.15 sentence | `DocumentationIssueReferenceTests` |
| Number the new section `### 21.15` but place it inside chapter 20 | `Every_design_subsection_sits_inside_the_chapter_its_number_claims` |

- [ ] **Step 6: Commit**

```bash
git add docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.OATH.md docs/Arcanum.Engineering.md docs/Arcanum.CommandMap.json src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/SagaCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/SagaCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs
git commit -m "docs: record selective erasure and finish the memory curation narrative" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 31: Mutation audit and branch qualification

**Files:**
- Nothing else, unless a break stays green or a gate fails. Then add a focused RED test and the fix, and commit it as its own `fix:` or `test:` commit.

**Interfaces:**
- Consumes: every Produces block in Tasks 1–30, and the concrete test methods named below. They were verified against the fixed fragments 01–06 and this fragment (R34).
- Produces: a qualified, clean `HEAD` SHA, and a recorded evidence table (the break, the RED test and its failing assertion, the restored GREEN run) for the controller's report. The evidence is not committed.

- [ ] **Step 1: Establish the baseline**

```bash
git status --short          # must be empty
git rev-parse HEAD
dotnet build RetroDownfall.Arcanum.slnx -c Release --disable-build-servers -m:1
```

- [ ] **Step 2: Run the §19.4 mutation audit, one break at a time**

For each row:
1. Make the break.
2. Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~<method>"` for every method in the row, and record the failing assertion.
3. Run `git checkout -- <file>`, then re-run the same filter green.

Rows 10b and 10c use the effect-recompute tests Tasks 14 and 16 wrote test-first.

| # | Break | Where | RED test (owning task) |
|---|---|---|---|
| 1 | Saga chokepoint fingerprint check removed | `SagaMemoryStore.InsertCoreAsync` | `Fingerprinted_content_in_the_same_scope_is_suppressed_and_nothing_is_written` (7) |
| 2 | Lexicon chokepoint check removed | `LexiconService.UpsertCoreAsync` | `Scribe_of_an_erased_name_is_refused_and_records_nothing` (8) |
| 3 | Covenant authoritative check skipped for `AgentProposed`/`AgentApproved` | `CovenantAgentErasureGate.ClassifyAsync`'s `ContainsAsync` step | `An_agent_proposal_of_a_fingerprinted_Campaign_key_is_refused_as_operator_managed` (11) |
| 4a | Staging courtesy flag ignored for proposals | `ResolveProposedLaneAsync` ignores `IsAgentWithheld` | `A_proposal_of_a_withheld_key_is_refused_exactly_like_a_pinned_one` (11) |
| 4b | Staging courtesy flag ignored for retirements | `CovenantTurnHeadProbe.ResolveRetirementPreflightAsync` ignores `IsAgentWithheld` | `Resolving_a_fingerprinted_head_is_refused_as_operator_managed` (11) |
| 5 | Pre-embed gate removed | `SagaExtractionService`, the `IsWithheldAsync` skip before `EmbedAsync` | `A_page_mixing_erased_and_fresh_candidates_writes_and_embeds_only_the_fresh_one` (7); `An_erased_memory_is_not_resurrected_by_extraction_and_its_text_is_never_re_embedded` (13) |
| 6 | Two-phase retry treated as `Allowed` | `MemoryErasureGuard.CheckAsync` trusts `context.EvidencePresent`; `InsertCoreAsync` maps `RetryWithKey` to `Allowed` | `Evidence_that_appears_after_the_probe_asks_for_one_retry_with_the_key` (6); `An_insert_that_meets_evidence_committed_after_its_probe_retries_once_and_is_suppressed` (7) |
| 7 | `KeyId` mismatch not fail-closed | the `AnyForeignAsync` step of `MemoryErasureGuard.CheckAsync`, and of the Covenant gate | `Rows_written_under_an_overwritten_key_fail_closed_as_key_lost` (6); `Agent_writes_fail_closed_when_a_Covenant_fingerprint_carries_another_key_id` (11) |
| 8 | Pre-v13 "no fingerprints" rule removed | the `IsInstalledAsync` gate in `MemoryErasureEvidence.AnyAsync` | `A_catalog_below_version_thirteen_has_no_evidence_and_touches_no_key` (6) |
| 9 | Racing key creators both write | `MemoryErasureKeyring` create moved outside `_gate` | `Concurrent_first_creates_write_exactly_once` (2) |
| 10a | Effect digest not recomputed in Saga apply | `SagaMemoryErasureService.ApplyAsync` | `A_new_twin_between_prepare_and_apply_is_a_stale_plan` (13) |
| 10b | Effect digest not recomputed in Lexicon apply | `LexiconService.Erasure` `ApplyAsync` | `A_global_entry_created_between_prepare_and_apply_is_a_stale_plan` (14) |
| 10c | Effect digest not recomputed in Covenant apply | `CovenantEntryErasureService.ApplyAsync` | `A_pin_recorded_between_prepare_and_apply_is_a_stale_plan` (16) |
| 11 | Receipt-first replay skipped before the transaction | the pre-transaction `ProbeReceiptAsync` call in each apply | Saga `A_repeated_apply_replays_the_receipt_without_a_valid_token` (13); Lexicon `LexiconErasureEndpointTests.A_repeated_apply_replays` (14; Task 16's Covenant file has a test of the same name, so the class prefix is part of the filter); Covenant `Replay_is_receipt_first_and_never_closes_a_scope` (16); helper `Probe_replays_a_matching_digest_and_conflicts_on_a_different_digest_or_store` (12) |
| 12 | Receipt re-probe skipped inside `BEGIN IMMEDIATE` | `SagaMemoryErasureService.ApplyAsync` | `Concurrent_identical_applies_commit_once_and_replay_once` (13) |
| 13 | Subject-digest 410 skipped | the `SubjectErasedAsync` check in Saga apply | `A_second_mutation_prepared_before_the_first_applied_answers_410_at_apply` (13) |
| 14 | A durable request digest binds content | `MemoryErasureDigestGrammar.LexiconRequest` gains `lp(NormalizedName)`, or `CovenantRequest` gains `lp(Key)` | `Request_digests_bind_non_content_fields_only` (1, its invariance assertions) |
| 15 | Twins excluded | the Saga twin query returns only the target | `Prepare_then_apply_erases_the_twin_class_in_scope_and_leaves_other_scopes_and_content`; `Hundreds_of_twins_are_erased_in_one_receipt_with_exact_counts` (13) |
| 16a | Retirement suppressions not deleted, live | Saga apply skips `SagaRetirementSuppression.DeletePairAsync` | `A_retired_labelled_memory_is_erased_with_its_label_and_retirement_suppression_pair` (13) |
| 16b | Retirement suppressions not deleted, staged | the staged applier skips the pair delete | `A_restore_of_an_archive_taken_while_the_memory_was_retired_leaves_no_retirement_digest` (24) |
| 17 | Search documents skipped when the full-text verdict is unverified | `CovenantEntryErasurePlan` gates the delete on the verdict | `Search_documents_are_deleted_even_when_covenant_fts_secure_delete_is_unverifiable` (16) |
| 18 | Closure slot mapping wrong | `CovenantOperationGate.AcquireEntryErasureAsync` | `A_reclaiming_Campaign_entry_erasure_takes_the_installation_slot`; `Global_entry_erasure_takes_the_installation_slot_and_drains_every_turn` (15) |
| 19 | Reclamation without installation coverage accepted | the `CoversInstallation` refusal | `Reclamation_is_refused_when_the_held_lease_does_not_cover_the_installation` (16) |
| 20 | Key row not reclaimed | `CovenantEntryErasurePlan` skips the key-row delete and the epoch bump | `Reclamation_purges_curation_in_every_scope_deletes_the_key_row_and_advances_the_epoch`; `A_reclaiming_erase_makes_an_outstanding_set_preflight_stale` (16) |
| 21 | Binding-epoch join reads `KeyEpoch` | `CovenantStoreSql.BindingEpoch`, the single source since Task 27 | `A_keyless_pin_survives_the_first_operator_set`; `A_mask_still_suppresses_the_Global_key_after_the_Global_entry_is_corrected` (10); `Detail_reports_a_keyless_pin_after_the_first_operator_set` (27) |
| 22 | Keyless pin lost at the first operator `set` | `covenant_heads_key_epoch_insert` stamps `1` instead of `0` (head file and V6/220) | `A_keyless_pin_stays_bound_across_the_first_head_for_its_key` (9) |
| 23a | Absence proof skipped (Covenant) | `CovenantEntryErasurePlan.ProveAbsentAsync` returns an empty list | `Absence_proof_reports_any_remaining_target` (16) |
| 23b | Absence proof skipped (Saga), in two states | first skip `saga_memory_embeddings` in Delete alone, then also remove the proof call | `Prepare_then_apply_erases_the_twin_class_in_scope_and_leaves_other_scopes_and_content` (13). Record both failing assertions: with the proof present, apply answers 500 `MemoryErasure.ErasureIncomplete`; with it removed, apply answers 200 and the embeddings-zero assertion fails instead. The difference is the evidence. |
| 24 | Full-text verdict upgraded | `MemoryErasureProtocol.FinishAsync` clears the whole mask; the Lexicon erase skips the secure-delete read-back | `Finish_keeps_a_non_upgradable_reason_pending` (12); `With_secure_delete_off_at_start_the_erase_enables_it_merges_and_then_deletes` (14) |
| 25 | WAL verdict forced `Truncated`, and scrub a no-op | `CovenantWalCheckpointOutcome.IsTruncated` ignores `RemainingFrames`; `ScrubAsync` drops its `Truncated` condition | `Only_a_checkpoint_that_was_neither_busy_nor_partial_counts_as_truncated` (12); `A_busy_checkpoint_leaves_the_receipt_pending_and_a_replay_verifies_it` (13); `A_busy_scrub_changes_nothing`; `Scrub_upgrades_only_receipts_whose_only_reason_was_the_wal` (18) |
| 26 | Receipt update guard lets a non-upgradable reason reach Verified | drop `AND NEW.ScrubPendingReasonMask = 0` from the v13 guard | `A_non_upgradable_reason_can_never_reach_verified` (3) |
| 27 | The Lexicon flag-off-at-start path deletes before merging | drop the `optimize` on the flag-off path | `With_secure_delete_off_at_start_the_erase_enables_it_merges_and_then_deletes` (14) |
| 28 | Saga `ContentBinding` compare skipped | Saga apply | `A_claimless_correction_between_prepare_and_apply_is_a_stale_plan_even_with_the_new_hash` (13) |
| 29 | `reset-key` proceeds on `Unavailable`, or deletes current-key rows | `MemoryErasureAdministration.ResetKeyAsync` | `Reset_key_refuses_an_unavailable_key_and_changes_nothing`; `Reset_key_discards_only_rows_under_a_foreign_key_and_keeps_the_present_key` (18) |
| 30 | Restore purge or post-condition skipped | `ReconcileStagedMemoryEvidenceAsync` skips the Saga purge; drops post-condition 1 | `A_restore_removes_a_saga_memory_this_installation_erased_and_keeps_its_twin_elsewhere`; `A_verification_failure_rolls_back_staging_and_leaves_the_installation_unchanged` (24) |
| 31 | The join is made a union | `MemoryErasureEvidence.ReplaceAllAsync` | `The_destination_evidence_replaces_the_archive_evidence_and_never_revives_a_release` (24) |
| 32 | One tier's drain gate skipped | staged drain: drop the drain call; drop the `erasure.Kind is Present` gate | `An_archive_at_core_eleven_and_canonical_five_is_drained_to_every_head_before_commit`; `A_destination_without_evidence_leaves_an_older_archive_pending_exactly_as_before` (23) |
| 32b | Drain stops once Core is healthy and ignores the canonical tier | `BackupRestoreSchemaDrain.DrainAsync` loop condition | `An_archive_at_core_eleven_and_canonical_five_is_drained_to_every_head_before_commit` (23; the canonical version assertion `6L`) |
| 33 | Fold watermark not advanced, or reader in two snapshots | `ExternalDisclosureStateFold` drops the watermark `UPDATE`; `ExternalDisclosureStateReader` drops `BEGIN DEFERRED` | `Effective_state_is_identical_before_and_after_the_backlog_is_folded`; `Effective_state_reads_persisted_buckets_and_tails_in_one_snapshot` (21) |
| 34 | Purge coordinator classifies by blocker only | `CovenantSensitiveRetentionPurgeCoordinator.ClassifyAsync` | `A_label_that_moved_before_the_kernel_is_blocked_as_stale_not_purged` (25) |
| 35 | v6 leftover purge runs before the guard drops | move V6/080 and 090 after 120 | `The_upgrade_purges_curation_left_behind_by_a_pre_fix_reset` (9) |
| 36 | Operator re-creation leaves the fingerprint | skip the fingerprint delete in the Covenant `set` commit; skip it in the Saga correction | `Operator_set_of_an_erased_key_discloses_then_releases_the_fingerprint`; `Saga_correction_to_erased_content_in_the_same_scope_releases_it` (17) |

A break that stays green is a finding. Write the missing RED test first, confirm it fails against the break, restore the code, and commit it as `test: pin <line>`. End with `git status --short` empty.

- [ ] **Step 3: Run the focused clusters**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~MemoryErasure|FullyQualifiedName~Erasure|FullyQualifiedName~Annal|FullyQualifiedName~DataRetention|FullyQualifiedName~BackupRestore|FullyQualifiedName~Lexicon|FullyQualifiedName~Saga|FullyQualifiedName~Covenant|FullyQualifiedName~MemoryReview|FullyQualifiedName~MemoryEndpoint|FullyQualifiedName~MemoryCommand|FullyQualifiedName~MemoryCrossStoreIsolation|FullyQualifiedName~MemoryStoreSnapshot" </dev/null
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~NullableInterfaceConstructorDefaultTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireAdmissionBenchmarkManifestTests|FullyQualifiedName~HostedGrimoireProducerInventoryTests|FullyQualifiedName~ApiSurfaceContractTests|FullyQualifiedName~ApiDomainSplitContractTests|FullyQualifiedName~CovenantSensitivePurgeRouteInventoryTests|FullyQualifiedName~LexiconCurationRouteInventoryTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests|FullyQualifiedName~ArcanumErrorMapperTests|FullyQualifiedName~ErrorCodeCatalogContractTests|FullyQualifiedName~CliSurfaceTests|FullyQualifiedName~CliJsonContextCoverageTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~CovenantPublicContractInventoryTests|FullyQualifiedName~CovenantErrorContractTests|FullyQualifiedName~UtcInstantColumnInventoryTests|FullyQualifiedName~UtcInstantPersistenceBoundaryTests|FullyQualifiedName~InternalsVisibleToInventoryTests|FullyQualifiedName~EnvironmentIsolationContractTests|FullyQualifiedName~GrimoireSchemaTransitionResourceTests|FullyQualifiedName~GrimoireSchemaVersionChainTests" </dev/null
```

The first cluster includes every §19.3 structural pin: `MemoryErasureStructuralTests` (only Saga writer, content-free logs, no managed files, erase not an action, no agent tool reference), `MemoryErasureEvidenceDeleterTests`, `MemoryErasureKeyCustodyTests`, and `MemoryErasureRouteInventoryTests`.

- [ ] **Step 4: Run the §19.5 qualification, fresh, against one `HEAD`**

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --disable-build-servers -m:1     # expect: 0 Warning(s), 0 Error(s)
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1 </dev/null
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
./scripts/coverage.sh --threshold
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release
python3 scripts/hosted_producer_analysis_runner.py --dotnet dotnet --project tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release --results-directory .tmp/analysis
./scripts/verify-aot-il-warnings.sh
./scripts/verify-shipping-publish.sh --rid osx-arm64
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
git diff --check
git status --short
```

- `coverage.sh --threshold` already runs the hosted analysis as its mandatory second phase. The standalone runner isolates a capsule failure.
- If the capsules drift:
  1. regenerate them with `ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES=1` on `HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery`;
  2. review the TSV diff;
  3. commit it as `test: review erasure hosted-producer capsules`;
  4. restart Step 4.
- Record every command's exit code, and the pass, fail and skip counts, against `git rev-parse HEAD`.

- [ ] **Step 5: Mutation check**

This task is the mutation check. Confirm that every Step 2 row has a recorded RED assertion and a restored GREEN run.

- [ ] **Step 6: Commit**

There is no ceremonial commit. Commit only:
- the planned `test: pin the Lexicon and Covenant effect-digest recompute` from Step 2;
- any fix or test a finding requires.

Use `git add <explicit files>` and `git commit -m "<fix|test>: <subject>" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`. After any commit, restart Step 4 from the new `HEAD`. Report the final qualified SHA.
