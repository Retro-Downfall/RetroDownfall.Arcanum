using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The storage-health proof: the checked checkpoint, the compaction and the fresh-file arm behind it,
/// the positive absence sweep, and the read-only reopen that cannot create a sidecar.
/// </summary>
/// <remarks>
/// File-backed and real throughout, and run over a database a real canonical erasure has already
/// emptied. Every claim this proof makes is a claim about what SQLite and SQLCipher actually do —
/// whether a checkpoint reports a leftover frame, whether <c>VACUUM</c> leaves a file that is exactly
/// its own pages, whether an immutable read-only handle writes a wal-index — and a faked connection
/// would answer all three by construction.
///
/// <para>What each refusal does to admission is not asserted here and is not this component's to
/// decide. Every step returns a failed result, the coordinator turns any failed step into
/// <c>KeepClosed</c> with the checkpoint left adoptable, and that mapping is asserted against the real
/// gate in the coordinator's own suite.</para>
/// </remarks>
public sealed class CovenantLocalErasureStorageHealthTests
{

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// The ordered steps the coordinator's frozen phase order runs, and the only place the sequence
    /// is written down in this suite.
    /// </summary>
    private static readonly string[] ProofSteps =
    [
        "close-handles",
        "truncate-wal",
        "compact",
        "initialize-accelerator",
        "truncate-wal",
        "verify-sidecar-absence",
        "verify-reopen",
    ];

    [Fact]
    public async Task The_whole_proof_leaves_a_compact_database_with_no_residual_artifact()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        Result<CovenantVerifiedCandidateState> proven = await erased.ProveAsync(Token);

        Assert.True(proven.IsSuccess, proven.IsFailure ? proven.Error.Message : null);

        Assert.Equal(ProofSteps, erased.Steps);

        // The candidate this proof read is the dataset the canonical transaction committed, read back
        // from the file rather than carried forward from the call that created it.
        Assert.Equal(erased.CandidateGeneration, proven.Value.Dataset.DatasetGeneration);

        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

        await erased.AssertFileIsExactlyItsPagesAsync(Token);

    }

    /// <summary>
    /// The compaction has something to do, and the file says so. A proof run over a database that was
    /// already compact would pass whether or not the compaction ran at all.
    /// </summary>
    [Fact]
    public async Task Compaction_returns_a_file_with_no_free_page_left_in_it()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        long freeBefore = await erased.ScalarLongAsync("PRAGMA freelist_count;", Token);

        Assert.True(freeBefore > 0, $"the erased database has no free pages to compact ({freeBefore}).");

        long lengthBefore = new FileInfo(erased.DatabasePath).Length;

        Result compacted = await erased.Health.CompactAsync(CovenantV3MaintenanceTestAuthority.Compaction(), Token);

        Assert.True(compacted.IsSuccess, compacted.IsFailure ? compacted.Error.Message : null);

        await erased.ReopenAsync(Token);

        Assert.Equal(0, await erased.ScalarLongAsync("PRAGMA freelist_count;", Token));

        Assert.True(
            new FileInfo(erased.DatabasePath).Length < lengthBefore,
            "compaction did not shrink the database file.");

        // This suite's own reads reopened the fixture handle, which is a live handle like any other.
        // Sweeping while it is open would report the sidecars this assertion exists to look for.
        await erased.CloseAsync();

        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

    }

    /// <summary>
    /// Compaction refuses on residue it is not entitled to clear, before it rewrites a byte, and it
    /// does not clear the copy an operator is being told to go and read.
    /// </summary>
    /// <remarks>
    /// A backup beside the database is what an atomic replace leaves exactly when it could not verify
    /// what it installed. Rewriting the destination underneath one would be rewriting a database whose
    /// identity nobody has established, and sweeping it would delete the only evidence of which
    /// database that is.
    /// </remarks>
    [Fact]
    public async Task Compaction_refuses_on_a_replaced_original_and_leaves_it_where_it_is()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        string backup = Path.Combine(
            Path.GetDirectoryName(erased.DatabasePath)!,
            AtomicFile.BackupPrefix + "0123456789abcdef");

        await File.WriteAllTextAsync(backup, "the database a replace could not verify replacing", Token);

        byte[] before = await File.ReadAllBytesAsync(erased.DatabasePath, Token);

        Result compacted = await erased.Health.CompactAsync(CovenantV3MaintenanceTestAuthority.Compaction(), Token);

        Assert.True(compacted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, compacted.Error.Code);

        Assert.Contains("replaced database", compacted.Error.Message, StringComparison.Ordinal);

        Assert.True(File.Exists(backup), "the operator's recovery copy was deleted by the proof.");

        Assert.Equal(before, await File.ReadAllBytesAsync(erased.DatabasePath, Token));

        File.Delete(backup);

    }

    /// <summary>
    /// A checkpoint that was refused is a checkpoint that proved nothing, and the busy flag is the
    /// only place the engine says so.
    /// </summary>
    [Fact]
    public async Task A_busy_checkpoint_refuses_rather_than_reporting_a_truncated_log()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await using SqliteConnection reader = await erased.OpenUnenrolledReaderAsync(Token);

        await Execute(reader, "BEGIN;", Token);

        _ = await Scalar(reader, "SELECT COUNT(*) FROM covenant_state;", Token);

        await erased.WriteWalFramesAsync(Token);

        Result truncated = await erased.Health.TruncateWalAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.WalTruncation), Token);

        Assert.True(truncated.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, truncated.Error.Code);

        Assert.Contains("busy", truncated.Error.Message, StringComparison.Ordinal);

        await Execute(reader, "ROLLBACK;", Token);

    }

    /// <summary>
    /// The three integers the pragma returns are read by ordinal, and the values here differ so a
    /// transposition cannot compare equal.
    /// </summary>
    [Fact]
    public async Task The_checkpoint_row_is_read_by_ordinal()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ReopenAsync(Token);

        await using SqliteCommand command = erased.Connection.CreateCommand();

        command.CommandText = "SELECT 5, 7, 3;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        CovenantWalCheckpointOutcome outcome = CovenantWalCheckpointOutcome.Project(reader);

        Assert.Equal(5, outcome.Busy);

        Assert.Equal(7, outcome.RemainingFrames);

        Assert.Equal(3, outcome.CheckpointedFrames);

    }

    /// <summary>
    /// The frame count is read rather than assumed, and refused on its own. A checkpoint can report
    /// that it moved frames and still leave some behind, and a rule that only looked at the busy flag
    /// would call that a proof.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, true)]

    // Delete journalling has no write-ahead log at all, which the engine reports as a negative count.
    // A log that does not exist has no frames left in it.
    [InlineData(0, -1, -1, true)]
    [InlineData(1, 1, 0, false)]
    [InlineData(0, 3, 12, false)]
    [InlineData(1, 0, 0, false)]
    public void Only_a_checkpoint_that_was_neither_busy_nor_partial_counts_as_truncated(
        int busy,
        int remaining,
        int checkpointed,
        bool expected)
    {

        Result required = new CovenantWalCheckpointOutcome(busy, remaining, checkpointed).RequireTruncated();

        Assert.Equal(expected, required.IsSuccess);

        if (expected)
        {

            return;

        }

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, required.Error.Code);

        Assert.Contains(
            busy != 0 ? "busy" : "left 3 in the log",
            required.Error.Message,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// The drain closes what it was shown, and the sidecars are what say whether that was everything.
    /// A handle nobody enrolled keeps the write-ahead log alive, and reporting a clean drain over it
    /// would hand the next step a lock whose holder it cannot name.
    /// </summary>
    [Fact]
    public async Task A_handle_the_drain_never_saw_refuses_the_close()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await using SqliteConnection survivor = await erased.OpenUnenrolledReaderAsync(Token);

        _ = await Scalar(survivor, "SELECT COUNT(*) FROM covenant_state;", Token);

        Result closed = await erased.Health.CloseHandlesAsync(Token);

        Assert.True(closed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, closed.Error.Code);

        Assert.Contains("write-ahead log", closed.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The retried proof still refuses on a handle that never lets go, and says how long it waited.
    /// </summary>
    /// <remarks>
    /// The proof is taken more than once so that a handle in the act of closing is not read as one
    /// that never will. This is the other half of that: a handle held open for the whole of the
    /// retry window produces the same refusal, at the same code, and the refusal carries the bound
    /// it exhausted. Without the bound in the message a stranded artifact and a handle nobody closed
    /// read identically to the next person holding a failed Windows run, which is exactly the
    /// question two investigations of this refusal have already had to stop and ask.
    /// </remarks>
    [Fact]
    public async Task A_handle_held_across_every_attempt_still_refuses_and_names_the_bound()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await using SqliteConnection survivor = await erased.OpenUnenrolledReaderAsync(Token);

        _ = await Scalar(survivor, "SELECT COUNT(*) FROM covenant_state;", Token);

        Result verified = await erased.Health.VerifySidecarAbsenceAsync(Token);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, verified.Error.Code);

        Assert.Contains("write-ahead log", verified.Error.Message, StringComparison.Ordinal);

        Assert.Contains("after 10 attempts over 225 ms", verified.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(erased.DatabasePath, verified.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_drained_installation_passes_the_close()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        Result closed = await erased.Health.CloseHandlesAsync(Token);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

    }

    /// <summary>
    /// Every class is looked for, and the refusal names the class rather than the file. The residue
    /// is planted by hand because the point of a positive sweep is that it reports what is there
    /// rather than what this path remembers writing.
    /// </summary>
    [Theory]
    [InlineData("-wal", "write-ahead log")]
    [InlineData("-shm", "shared-memory index")]
    [InlineData("-journal", "rollback journal")]
    [InlineData("-tmp3", "temporary database")]
    [InlineData(CovenantResidualArtifacts.ExportStagingSuffix, "export staging")]
    public async Task Every_residual_class_refuses_the_absence_proof_without_naming_a_file(
        string suffix,
        string expected)
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        await File.WriteAllTextAsync(erased.DatabasePath + suffix, "residue", Token);

        Result verified = await erased.Health.VerifySidecarAbsenceAsync(Token);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, verified.Error.Code);

        Assert.Contains(expected, verified.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(erased.DatabasePath, verified.Error.Message, StringComparison.Ordinal);

        File.Delete(erased.DatabasePath + suffix);

    }

    [Fact]
    public async Task A_replaced_original_left_behind_refuses_the_absence_proof()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        string backup = Path.Combine(
            Path.GetDirectoryName(erased.DatabasePath)!,
            AtomicFile.BackupPrefix + "0123456789abcdef");

        await File.WriteAllTextAsync(backup, "a copy of the database this erasure replaced", Token);

        Result verified = await erased.Health.VerifySidecarAbsenceAsync(Token);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, verified.Error.Code);

        Assert.Contains("replaced database", verified.Error.Message, StringComparison.Ordinal);

        File.Delete(backup);

    }

    /// <summary>
    /// The one acceptance this proof exists for: the reopen reads the candidate and leaves the file
    /// exactly as it found it, with neither sidecar created.
    /// </summary>
    [Fact]
    public async Task The_verified_reopen_creates_neither_a_write_ahead_log_nor_a_shared_memory_index()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        Result<CovenantVerifiedCandidateState> proven = await erased.ProveAsync(Token);

        Assert.True(proven.IsSuccess, proven.IsFailure ? proven.Error.Message : null);

        Assert.False(File.Exists(erased.DatabasePath + "-wal"));

        Assert.False(File.Exists(erased.DatabasePath + "-shm"));

        // Run it a second time on its own. The first reopen sat behind six other steps, and a handle
        // that only avoids a sidecar because an earlier step happened to leave none is not the handle
        // this acceptance asks for.
        Result<CovenantVerifiedCandidateState> again = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Message : null);

        Assert.False(File.Exists(erased.DatabasePath + "-wal"));

        Assert.False(File.Exists(erased.DatabasePath + "-shm"));

    }

    [Fact]
    public async Task The_verified_reopen_reads_the_candidate_dataset_master_authority_and_capability_state()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        Result<CovenantVerifiedCandidateState> proven = await erased.ProveAsync(Token);

        Assert.True(proven.IsSuccess, proven.IsFailure ? proven.Error.Message : null);

        CovenantVerifiedCandidateState state = proven.Value;

        Assert.Equal(ErasedGrimoire.PublishedDatasetGeneration, state.Dataset.DatasetGeneration);

        Assert.Equal(17, state.Dataset.CanonicalSearchSequence);

        Assert.Equal(4, state.Dataset.CoreCampaignDeletionSequence);

        Assert.Equal(ErasedGrimoire.AppliedDatasetGeneration, state.Dataset.AppliedDatasetGeneration);

        Assert.Equal(13, state.Dataset.AppliedSearchSequence);

        Assert.Equal(4, state.Dataset.AppliedCampaignDeletionSequence);

        Assert.Equal(2, state.Dataset.AppliedSessionDeletionSequence);

        Assert.Equal((ulong)29, state.Dataset.AcceleratorEpoch);

        Assert.Equal(CovenantFtsRebuildState.Rebuilding, state.Dataset.RebuildState);

        Assert.Equal(7, state.Dataset.EnvelopeMasterKeyVersion);

        Assert.Equal(Enumerable.Repeat((byte)0xC1, 32), state.Dataset.EnvelopeMasterKeyFingerprint);

        Assert.Equal(31, state.Dataset.EnvelopeKeyEpoch);

        Assert.Equal("verified-installation", state.Authority.InstallationIdentity);

        Assert.Equal(23, state.Authority.AuthorityEpoch);

        Assert.Equal(7, state.Authority.CurrentMasterKeyVersion);

        Assert.Equal(Enumerable.Repeat((byte)0xC1, 32), state.Authority.CurrentMasterKeyFingerprint);

        Assert.Equal(37, state.Authority.RecoveryEnvelopeEpoch);

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, state.Authority.HostToolsState);

        Assert.Equal("11111111-2222-4333-8444-555555555555", state.Authority.TransitionId);

        // The erasure moves both cursors to the core owner-deletion journal's maximum per owner kind,
        // and the fixture seeds distinct maxima so a transposed pair cannot compare equal.
        Assert.Equal(4, state.Capability.AppliedCampaignSequence);

        Assert.Equal(2, state.Capability.AppliedSessionSequence);

        Assert.Equal(state.Dataset.AppliedCampaignDeletionSequence, state.Capability.AppliedCampaignSequence);

        Assert.Equal(state.Dataset.AppliedSessionDeletionSequence, state.Capability.AppliedSessionSequence);

        Assert.True(state.Capability.FullSweepRequired);

    }

    public static TheoryData<string, string> MalformedPublicationStates => new()
    {
        {
            "unknown rebuild state",
            "UPDATE covenant_state SET RebuildStateCode = 99 WHERE StateKey = 1;"
        },
        {
            "unknown host-tools state",
            "UPDATE covenant_authority_state SET HostToolsStateCode = 99 WHERE StateKey = 1;"
        },
        {
            "half-present applied tuple",
            "UPDATE covenant_state SET AppliedDatasetGeneration = NULL WHERE StateKey = 1;"
        },
        {
            "non-boolean full-sweep flag",
            "UPDATE capability_cleanup_state SET FullSweepRequired = 2 WHERE CapabilityFamilyCode = 1;"
        },
        {
            "clean authority carrying a transition",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TaintTimeMasterVersion = NULL,
                TaintFingerprint = NULL,
                TransitionId = 'sentinel-publication-secret'
            WHERE StateKey = 1;
            """
        },
        {
            "master-key version outside the runtime domain",
            "UPDATE covenant_authority_state SET CurrentMasterKeyVersion = 4294967296 WHERE StateKey = 1;"
        },
        {
            "short canonical fingerprint",
            "UPDATE covenant_state SET EnvelopeMasterKeyFingerprint = X'01' WHERE StateKey = 1;"
        },
        {
            "same-version fingerprint disagreement",
            """
            UPDATE covenant_authority_state
            SET CurrentMasterKeyFingerprint = X'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
            WHERE StateKey = 1;
            """
        },
        {
            "empty applied dataset generation",
            """
            UPDATE covenant_state
            SET AppliedDatasetGeneration = zeroblob(16)
            WHERE StateKey = 1;
            """
        },
        {
            "blank installation identity",
            "UPDATE covenant_authority_state SET InstallationIdentity = '' WHERE StateKey = 1;"
        },
        {
            "malformed transition identity",
            """
            UPDATE covenant_authority_state
            SET TransitionId = 'sentinel-publication-secret'
            WHERE StateKey = 1;
            """
        },
        {
            "negative canonical sequence",
            """
            DROP TRIGGER covenant_state_validate_update;
            UPDATE covenant_state SET CanonicalSearchSequence = -1 WHERE StateKey = 1;
            """
        },
    };

    public static TheoryData<string, string> MalformedRebuildTuples => new()
    {
        {
            "rebuilding without a target",
            "UPDATE covenant_state SET RebuildTargetSequence = NULL WHERE StateKey = 1;"
        },
        {
            "idle with a cursor",
            """
            UPDATE covenant_state
            SET RebuildStateCode = 1,
                RebuildTargetSequence = NULL,
                RebuildCursor = 9
            WHERE StateKey = 1;
            """
        },
        {
            "full rebuild required with a target",
            """
            UPDATE covenant_state
            SET RebuildStateCode = 2,
                RebuildTargetSequence = 17,
                RebuildCursor = NULL
            WHERE StateKey = 1;
            """
        },
        {
            "rebuilding with a negative cursor",
            "UPDATE covenant_state SET RebuildCursor = -1 WHERE StateKey = 1;"
        },
        {
            "rebuilding with a non-integer target",
            "UPDATE covenant_state SET RebuildTargetSequence = 'sentinel-rebuild-secret' WHERE StateKey = 1;"
        },
    };

    public static TheoryData<string, string> MalformedHostToolsTaintTuples => new()
    {
        {
            "clean authority retaining a taint-time master version",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TaintTimeMasterVersion = 5,
                TaintFingerprint = NULL,
                TransitionId = NULL
            WHERE StateKey = 1;
            """
        },
        {
            "clean authority retaining a taint fingerprint",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TaintTimeMasterVersion = NULL,
                TaintFingerprint = CAST('sentinel-taint-secret' AS BLOB),
                TransitionId = NULL
            WHERE StateKey = 1;
            """
        },
        {
            "pending authority without a taint-time master version",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 2,
                TaintTimeMasterVersion = NULL
            WHERE StateKey = 1;
            """
        },
        {
            "tainted authority without a fingerprint",
            "UPDATE covenant_authority_state SET TaintFingerprint = NULL WHERE StateKey = 1;"
        },
        {
            "pending authority with a non-integer taint-time master version",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 2,
                TaintTimeMasterVersion = 'sentinel-taint-secret'
            WHERE StateKey = 1;
            """
        },
        {
            "pending authority with a zero taint-time master version",
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 2,
                TaintTimeMasterVersion = 0
            WHERE StateKey = 1;
            """
        },
        {
            "tainted authority with a short fingerprint",
            "UPDATE covenant_authority_state SET TaintFingerprint = X'01' WHERE StateKey = 1;"
        },
        {
            "tainted authority with a text fingerprint",
            """
            UPDATE covenant_authority_state
            SET TaintFingerprint = 'sentinel-taint-secret-12345678901'
            WHERE StateKey = 1;
            """
        },
    };

    [Theory]
    [MemberData(nameof(MalformedRebuildTuples))]
    public async Task The_verified_reopen_refuses_malformed_rebuild_tuples_without_content_leakage(
        string caseName,
        string corruption)
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        await erased.ExecuteUncheckedAsync(corruption, Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure, caseName);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, reopened.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-rebuild-secret", reopened.Error.Message, StringComparison.Ordinal);

    }

    [Theory]
    [MemberData(nameof(MalformedHostToolsTaintTuples))]
    public async Task The_verified_reopen_refuses_malformed_host_tools_taint_tuples_without_content_leakage(
        string caseName,
        string corruption)
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        await erased.ExecuteUncheckedAsync(corruption, Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure, caseName);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, reopened.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-taint-secret", reopened.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_verified_reopen_refuses_a_current_dataset_cursor_ahead_of_canonical_without_content_leakage()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        await erased.ExecuteAsync(
            """
            UPDATE covenant_state
            SET AppliedDatasetGeneration = DatasetGeneration,
                AppliedSearchSequence = CanonicalSearchSequence + 1,
                UpdatedAtUtc = 'sentinel-future-cursor'
            WHERE StateKey = 1;
            """,
            Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, reopened.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-future-cursor", reopened.Error.Message, StringComparison.Ordinal);

    }

    [Theory]
    [MemberData(nameof(MalformedPublicationStates))]
    public async Task The_verified_reopen_refuses_malformed_publication_state_without_content_leakage(
        string caseName,
        string corruption)
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        await erased.ExecuteUncheckedAsync(corruption, Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure, caseName);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, reopened.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-publication-secret", reopened.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Malformed_authority_refuses_accelerator_initialization_without_throwing_or_leaking()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteUncheckedAsync(
            """
            UPDATE covenant_authority_state
            SET InstallationIdentity = 'sentinel-publication-secret',
                CurrentMasterKeyVersion = 4294967296
            WHERE StateKey = 1;
            """,
            Token);

        Result initialized = await erased.Health.InitializeAcceleratorAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization), Token);

        Assert.True(initialized.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, initialized.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, initialized.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-publication-secret", initialized.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Malformed_taint_authority_refuses_accelerator_initialization_without_content_leakage()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.SeedPublicationStateAsync(Token);

        await erased.ExecuteUncheckedAsync(
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TaintTimeMasterVersion = 5,
                TaintFingerprint = CAST('sentinel-taint-secret' AS BLOB),
                TransitionId = NULL
            WHERE StateKey = 1;
            """,
            Token);

        Result initialized = await erased.Health.InitializeAcceleratorAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization), Token);

        Assert.True(initialized.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, initialized.Error.Code);

        Assert.DoesNotContain(erased.DatabasePath, initialized.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-taint-secret", initialized.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_verified_reopen_refuses_a_candidate_whose_envelope_master_leads_the_authority()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync(
            """
            UPDATE covenant_state
            SET EnvelopeMasterKeyVersion =
                (SELECT CurrentMasterKeyVersion + 1 FROM covenant_authority_state WHERE StateKey = 1)
            WHERE StateKey = 1;
            """,
            Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.Contains("envelope master", reopened.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_verified_reopen_refuses_when_the_two_owner_deletion_cursors_disagree()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync(
            "UPDATE capability_cleanup_state SET AppliedSessionSequence = 0 WHERE CapabilityFamilyCode = 1;",
            Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.Contains("cursors disagree", reopened.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The family is counted again through the reopened handle, from the same list the transaction
    /// deleted through. A proof that trusted the transaction's own report would agree with it exactly
    /// when it was wrong.
    /// </summary>
    [Fact]
    public async Task The_verified_reopen_refuses_a_family_that_is_not_empty()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync(
            """
            INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ('11111111-2222-4333-8444-555555555555', 1, NULL, 'survivor', 'survivor', '2026-02-01T00:00:00.0000000Z');
            """,
            Token);

        Result<CovenantVerifiedCandidateState> reopened = await erased.Health.VerifyReopenAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification), Token);

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, reopened.Error.Code);

        Assert.Contains("covenant_entries", reopened.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The empty index is prepared by the same initializer a fresh install runs, and it is the
    /// read-back that proves the setting took rather than the statement that asked for it.
    /// </summary>
    [Fact]
    public async Task The_empty_accelerator_reports_secure_delete_after_initialization()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync("DELETE FROM covenant_fts_config WHERE k = 'secure-delete';", Token);

        Result initialized = await erased.Health.InitializeAcceleratorAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization), Token);

        Assert.True(initialized.IsSuccess, initialized.IsFailure ? initialized.Error.Message : null);

        await erased.ReopenAsync(Token);

        Assert.Equal(
            1,
            await erased.ScalarLongAsync(
                "SELECT v FROM covenant_fts_config WHERE k = 'secure-delete';",
                Token));

    }

    /// <summary>
    /// Rank-1 integrity is run rather than named. Removing the index's own content table leaves an
    /// index FTS5 can still be asked about and cannot verify.
    /// </summary>
    [Fact]
    public async Task A_damaged_accelerator_refuses_with_an_integrity_failure()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync("DROP TABLE covenant_fts_data;", Token);

        Result initialized = await erased.Health.InitializeAcceleratorAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization), Token);

        Assert.True(initialized.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, initialized.Error.Code);

    }

    /// <summary>
    /// An installation whose optional accelerator tier is not installed still has to be resettable.
    /// The canonical tier commits without one by design, and a proof that refused would make a
    /// degraded installation impossible to erase.
    /// </summary>
    [Fact]
    public async Task An_installation_with_no_accelerator_tier_still_passes_the_whole_proof()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token, withAccelerator: false);

        Result<CovenantVerifiedCandidateState> proven = await erased.ProveAsync(Token);

        Assert.True(proven.IsSuccess, proven.IsFailure ? proven.Error.Message : null);

        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

    }

    /// <summary>
    /// The fresh-file arm, driven directly.
    /// </summary>
    /// <remarks>
    /// The arm is selected by measuring what the cheaper one left behind, and a healthy engine does
    /// not normally leave a file that fails that measurement — which is exactly why the selection is a
    /// measurement rather than a prediction, and why the arm it selects has to be exercised against a
    /// real database rather than waited for.
    /// </remarks>
    [Fact]
    public async Task An_export_and_replace_installs_a_verified_candidate_and_leaves_no_staging_behind()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        Result replaced = await erased.Health.ExportAndReplaceAsync(CovenantV3MaintenanceTestAuthority.Compaction(), Token);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Message : null);

        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

        await erased.ReopenAsync(Token);

        Assert.Equal(erased.CandidateGeneration, await erased.ReadDatasetGenerationAsync(Token));

        // sqlcipher_export writes a rollback-journalled database, and the installation opens its
        // Grimoire in write-ahead logging. A replace that left the other mode behind would change how
        // every later connection journals, underneath a proof that had already been made.
        Assert.Equal("wal", await erased.ScalarStringAsync("PRAGMA journal_mode;", Token));

        await erased.AssertFileIsExactlyItsPagesAsync(Token);

    }

    /// <summary>
    /// The candidate is proven before the destination is touched, so an export that cannot be
    /// verified costs the installation nothing.
    /// </summary>
    [Fact]
    public async Task An_export_that_cannot_be_verified_leaves_the_original_in_place()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        byte[] before = await File.ReadAllBytesAsync(erased.DatabasePath, Token);

        string staging = CovenantResidualArtifacts.ExportStagingPath(erased.DatabasePath);

        Result exported = await erased.Health.ExportAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExport), Token);

        Assert.True(exported.IsSuccess, exported.IsFailure ? exported.Error.Message : null);

        // A real corruption of a real export: the first page carries the salt and the header every
        // later page's authentication is derived from.
        byte[] candidate = await File.ReadAllBytesAsync(staging, Token);

        Array.Fill(candidate, (byte)0xA5, 0, 4096);

        await File.WriteAllBytesAsync(staging, candidate, Token);

        Result<CovenantVerifiedExport> verified = await erased.Health.VerifyExportAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExportVerification), Token);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, verified.Error.Code);

        Assert.Contains("left in place", verified.Error.Message, StringComparison.Ordinal);

        Assert.Equal(before, await File.ReadAllBytesAsync(erased.DatabasePath, Token));

        File.Delete(staging);

    }

    /// <summary>
    /// W14-6: the compensating <c>DETACH</c> in <c>ExportAsync</c>'s finally block must run on
    /// <see cref="CancellationToken.None"/>, not the caller's token. When the caller's token is
    /// already cancelled by the time the finally block runs, rolling back on that token throws a
    /// fresh <see cref="OperationCanceledException"/> that replaces the real export failure the try
    /// block already raised, turning a graceful <c>Result.Failure</c> into an unhandled throw.
    /// </summary>
    [Fact]
    public async Task ExportAsync_still_returns_a_failure_result_when_the_token_cancels_before_the_detach()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        using CancellationTokenSource cts = new();

        erased.Health.BeforeExportCommandForTesting = connection =>
        {

            SqliteException real;

            using (SqliteCommand bad = connection.CreateCommand())
            {

                bad.CommandText = "SELECT * FROM this_table_does_not_exist_for_the_red_test;";

                try
                {

                    _ = bad.ExecuteScalar();

                    throw new InvalidOperationException(
                        "Expected the statement above to raise a genuine SqliteException.");

                }
                catch (SqliteException capturedFailure)
                {

                    real = capturedFailure;

                }

            }

            // Cancels exactly where the export command has already failed for a real reason and the
            // finally block is the only thing left standing between that failure and the caller,
            // matching the finding's own interleaving.
            cts.Cancel();

            throw real;

        };

        Result? exported = null;

        Exception? thrown = await Record.ExceptionAsync(async () =>
        {

            exported = await erased.Health.ExportAsync(
                CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExport),
                cts.Token);

        });

        Assert.Null(thrown);

        Assert.NotNull(exported);

        Assert.True(exported.IsFailure);

    }

    [Fact]
    public async Task An_export_that_cannot_be_written_leaves_the_original_in_place()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        byte[] before = await File.ReadAllBytesAsync(erased.DatabasePath, Token);

        // Something that is not a database, exactly where the export would write one.
        await File.WriteAllTextAsync(
            CovenantResidualArtifacts.ExportStagingPath(erased.DatabasePath),
            "not a database",
            Token);

        Result replaced = await erased.Health.ExportAndReplaceAsync(CovenantV3MaintenanceTestAuthority.Compaction(), Token);

        Assert.True(replaced.IsFailure);

        Assert.Equal(before, await File.ReadAllBytesAsync(erased.DatabasePath, Token));

        // The staging file it could not use is still this path's own litter, and a refusal that left
        // it behind would fail the absence proof two steps later for a reason nothing had recorded.
        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

    }

    /// <summary>
    /// A candidate that exports perfectly well and still cannot be published, driven through the
    /// composed step rather than through its verification alone.
    /// </summary>
    /// <remarks>
    /// The staging file this produces is a valid, intact, compact SQLCipher database — the export is
    /// not what fails. What fails is the question the verification asks after it: whether the
    /// candidate carries an identity anything in this installation could resume from. A composition
    /// that installed before it asked would replace a working Grimoire with this one and report
    /// success.
    /// </remarks>
    [Fact]
    public async Task An_export_whose_candidate_has_no_identity_is_refused_and_the_original_stays()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.ExecuteAsync("DELETE FROM covenant_state WHERE StateKey = 1;", Token);

        await erased.DrainAsync(Token);

        byte[] before = await File.ReadAllBytesAsync(erased.DatabasePath, Token);

        Result replaced = await erased.Health.ExportAndReplaceAsync(CovenantV3MaintenanceTestAuthority.Compaction(), Token);

        Assert.True(replaced.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, replaced.Error.Code);

        Assert.Contains("canonical singleton", replaced.Error.Message, StringComparison.Ordinal);

        Assert.Equal(before, await File.ReadAllBytesAsync(erased.DatabasePath, Token));

        Assert.Empty(CovenantResidualArtifacts.Survivors(erased.DatabasePath));

    }

    /// <summary>
    /// Installing takes the proof rather than a path, so a candidate that has gone between its
    /// verification and its installation cannot be installed by accident.
    /// </summary>
    [Fact]
    public async Task A_candidate_that_disappears_after_its_proof_leaves_the_original_in_place()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        byte[] before = await File.ReadAllBytesAsync(erased.DatabasePath, Token);

        string staging = CovenantResidualArtifacts.ExportStagingPath(erased.DatabasePath);

        Result exported = await erased.Health.ExportAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExport), Token);

        Assert.True(exported.IsSuccess, exported.IsFailure ? exported.Error.Message : null);

        Result<CovenantVerifiedExport> verified = await erased.Health.VerifyExportAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExportVerification), Token);

        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);

        File.Delete(staging);

        Result replaced = await erased.Health.ReplaceAsync(verified.Value, CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore), Token);

        Assert.True(replaced.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, replaced.Error.Code);

        Assert.Equal(before, await File.ReadAllBytesAsync(erased.DatabasePath, Token));

    }

    [Fact]
    public async Task A_verification_with_no_candidate_at_all_refuses()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        Result<CovenantVerifiedExport> verified = await erased.Health.VerifyExportAsync(
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionExportVerification),
            Token);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, verified.Error.Code);

    }

    /// <summary>
    /// The cheaper arm proves itself on a healthy database, and the measurement is what says so.
    /// </summary>
    /// <remarks>
    /// Asserted against the measurement rather than against the finished file, because the fresh-file
    /// arm produces a correct file too. A proof that only looked at the result would pass whether or
    /// not compaction ever ran, and would go on passing the day the two arms diverged.
    /// </remarks>
    [Fact]
    public async Task Compaction_alone_proves_itself_on_a_healthy_database()
    {

        await using ErasedGrimoire erased = await ErasedGrimoire.CreateAsync(Token);

        await erased.DrainAsync(Token);

        Result<CovenantCompactionMeasurement?> measured = await erased.Health.VacuumAsync(CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CompactionVacuum), Token);

        Assert.True(measured.IsSuccess, measured.IsFailure ? measured.Error.Message : null);

        CovenantCompactionMeasurement measurement = Assert.NotNull(measured.Value);

        Assert.Equal(0, measurement.FreelistPages);

        Assert.True(measurement.IsProven, "compaction alone did not prove itself on a healthy database.");

    }

    /// <summary>
    /// Compaction is proven by two independent measurements, because it can fail either alone: a free
    /// page the engine is still holding, and a byte in the file the engine will never read again.
    /// </summary>
    [Theory]
    [InlineData(0, 75, 4096, 307200, true)]
    [InlineData(1, 75, 4096, 307200, false)]
    [InlineData(0, 75, 4096, 311296, false)]
    [InlineData(0, 0, 4096, 0, false)]
    public void Compaction_is_proven_only_when_the_file_is_exactly_its_pages(
        int freelist,
        int pages,
        int pageSize,
        int length,
        bool expected) =>
        Assert.Equal(
            expected,
            new CovenantCompactionMeasurement(freelist, pages, pageSize, length).IsProven);

    private static async Task Execute(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static async Task<object?> Scalar(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(cancellationToken);

    }

    /// <summary>
    /// A real Grimoire a real canonical erasure has already emptied, and the storage-health proof
    /// that has to be able to vouch for what it left behind.
    /// </summary>
    /// <remarks>
    /// The canonical transaction runs for real rather than being simulated, because the proof's whole
    /// subject is the file that transaction produced: the free pages it left, the dataset it stamped,
    /// and the cursors it moved are all things the proof reads back, and a hand-seeded approximation
    /// would let the two agree about a state neither had ever produced.
    /// </remarks>
    private sealed class ErasedGrimoire : IAsyncDisposable
    {

        private const int JunkEntryCount = 400;

        internal static Guid PublishedDatasetGeneration { get; } =
            new(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));

        internal static Guid AppliedDatasetGeneration { get; } =
            new(Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"));

        private readonly CovenantCanonicalErasureFixture _fixture;

        private ErasedGrimoire(
            CovenantCanonicalErasureFixture fixture,
            CovenantLocalErasureStorageHealth health,
            Guid candidateGeneration)
        {

            _fixture = fixture;

            Health = health;

            CandidateGeneration = candidateGeneration;

        }

        /// <summary>
        /// The concrete proof, because two of its members are the fresh-file arm the seam does not
        /// expose and this suite drives directly.
        /// </summary>
        internal CovenantLocalErasureStorageHealth Health { get; }

        internal Guid CandidateGeneration { get; }

        internal List<string> Steps { get; } = [];

        internal string DatabasePath => _fixture.DatabasePath;

        internal SqliteConnection Connection => _fixture.Connection;

        internal static async Task<ErasedGrimoire> CreateAsync(
            CancellationToken cancellationToken,
            bool withAccelerator = true)
        {

            CovenantCanonicalErasureFixture fixture =
                await CovenantCanonicalErasureFixture.CreateAsync(cancellationToken, withAccelerator);

            try
            {

                await fixture.SeedAsync(cancellationToken);

                await SeedJunkAsync(fixture, cancellationToken);

                CovenantV3MaintenanceTestConnectionFactory v3Connections = fixture.V3Connections();

                CovenantCanonicalErasureTransaction transaction = new(
                    v3Connections,
                    CovenantSqliteConnectionInitializer.Instance,
                    fixture.Drain,
                    TimeProvider.System);

                Result<Guid> applied = await transaction.ApplyAsync(
                    CovenantExclusiveOperation.CovenantReset,
                    CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
                    cancellationToken);

                Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

                return new ErasedGrimoire(
                    fixture,
                    new CovenantLocalErasureStorageHealth(
                        v3Connections,
                        v3Connections,
                        CovenantSqliteConnectionInitializer.Instance,
                        fixture.Drain,
                        TimeProvider.System),
                    applied.Value);

            }
            catch
            {

                await fixture.DisposeAsync();

                throw;

            }

        }

        /// <summary>Runs the seven steps in the coordinator's frozen order.</summary>
        internal async Task<Result<CovenantVerifiedCandidateState>> ProveAsync(
            CancellationToken cancellationToken)
        {

            // Through the seam the coordinator holds, in the order its frozen phase machine calls it.
            ICovenantLocalErasureStorageHealth seam = Health;

            Result closed = await Record("close-handles", seam.CloseHandlesAsync(cancellationToken));

            if (closed.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(closed.Error);

            }

            Result truncated = await Record(
                "truncate-wal",
                seam.TruncateWalAsync(
                    CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.WalTruncation),
                    cancellationToken));

            if (truncated.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(truncated.Error);

            }

            Result compacted = await Record(
                "compact",
                seam.CompactAsync(CovenantV3MaintenanceTestAuthority.Compaction(), cancellationToken));

            if (compacted.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(compacted.Error);

            }

            Result accelerator = await Record(
                "initialize-accelerator",
                seam.InitializeAcceleratorAsync(
                    CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization),
                    cancellationToken));

            if (accelerator.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(accelerator.Error);

            }

            Result finalTruncate = await Record(
                "truncate-wal",
                seam.TruncateWalAsync(
                    CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.WalTruncation),
                    cancellationToken));

            if (finalTruncate.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(finalTruncate.Error);

            }

            Result absent = await Record(
                "verify-sidecar-absence",
                seam.VerifySidecarAbsenceAsync(cancellationToken));

            if (absent.IsFailure)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(absent.Error);

            }

            Steps.Add("verify-reopen");

            return await seam.VerifyReopenAsync(
                CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification),
                cancellationToken);

        }

        /// <summary>Opens a handle nothing enrolled in the drain, and makes it hold the log open.</summary>
        internal async Task<SqliteConnection> OpenUnenrolledReaderAsync(CancellationToken cancellationToken)
        {

            SqliteConnection connection = await _fixture.Connections().OpenAsync(cancellationToken);

            try
            {

                await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                    connection,
                    CovenantSqliteConnectionMode.ReadWrite,
                    cancellationToken);

                _ = await Scalar(connection, "SELECT COUNT(*) FROM covenant_state;", cancellationToken);

                return connection;

            }
            catch
            {

                await connection.DisposeAsync();

                throw;

            }

        }

        /// <summary>Appends frames to the log without checkpointing them.</summary>
        internal async Task WriteWalFramesAsync(CancellationToken cancellationToken)
        {

            await ReopenAsync(cancellationToken);

            await ExecuteAsync(
                """
                UPDATE covenant_state
                SET UpdatedAtUtc = '2026-02-02T00:00:00.0000000Z'
                WHERE StateKey = 1;
                """,
                cancellationToken);

        }

        internal Task DrainAsync(CancellationToken cancellationToken) =>
            _fixture.Drain.DrainAsync(cancellationToken);

        /// <summary>Closes this suite's own handle, so a sweep sees the file at rest.</summary>
        internal async Task CloseAsync()
        {

            await Connection.CloseAsync();

            SqliteConnection.ClearAllPools();

        }

        internal Task ReopenAsync(CancellationToken cancellationToken) =>
            _fixture.ReopenAsync(cancellationToken);

        internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {

            await ReopenAsync(cancellationToken);

            await _fixture.ExecuteAsync(sql, cancellationToken);

            // The proof runs against the file rather than against this connection, and it drains
            // before every step. Closing here keeps the fixture's own handle from being the surviving
            // handle a step is entitled to refuse on.
            await Connection.CloseAsync();

        }

        internal Task SeedPublicationStateAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(
                """
                UPDATE covenant_authority_state
                SET InstallationIdentity = 'verified-installation',
                    AuthorityEpoch = 23,
                    CurrentMasterKeyVersion = 7,
                    CurrentMasterKeyFingerprint = X'C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1',
                    RecoveryEnvelopeEpoch = 37,
                    HostToolsStateCode = 3,
                    TaintTimeMasterVersion = X'0000000000000005',
                    TaintFingerprint = X'D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2',
                    TransitionId = '11111111-2222-4333-8444-555555555555'
                WHERE StateKey = 1;

                UPDATE covenant_state
                SET DatasetGeneration = X'00112233445566778899AABBCCDDEEFF',
                    CanonicalSearchSequence = 17,
                    AppliedDatasetGeneration = X'FFEEDDCCBBAA99887766554433221100',
                    AppliedSearchSequence = 13,
                    AppliedCampaignDeletionSequence = 4,
                    AppliedSessionDeletionSequence = 2,
                    AcceleratorEpoch = 29,
                    EnvelopeMasterKeyVersion = 7,
                    EnvelopeMasterKeyFingerprint = X'C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1C1',
                    EnvelopeKeyEpoch = 31,
                    RebuildStateCode = 3,
                    RebuildTargetSequence = 17,
                    RebuildCursor = 9
                WHERE StateKey = 1;

                UPDATE capability_cleanup_state
                SET AppliedCampaignSequence = 4,
                    AppliedSessionSequence = 2,
                    FullSweepRequired = 1
                WHERE CapabilityFamilyCode = 1;
                """,
                cancellationToken);

        internal Task ExecuteUncheckedAsync(string sql, CancellationToken cancellationToken) =>
            ExecuteAsync(
                $"""
                PRAGMA ignore_check_constraints = ON;
                {sql}
                PRAGMA ignore_check_constraints = OFF;
                """,
                cancellationToken);

        internal async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
        {

            await ReopenAsync(cancellationToken);

            return await _fixture.ScalarLongAsync(sql, cancellationToken);

        }

        internal async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
        {

            await ReopenAsync(cancellationToken);

            return await _fixture.ScalarStringAsync(sql, cancellationToken);

        }

        internal async Task<Guid?> ReadDatasetGenerationAsync(CancellationToken cancellationToken)
        {

            await ReopenAsync(cancellationToken);

            return await _fixture.ReadDatasetGenerationAsync(cancellationToken);

        }

        /// <summary>
        /// The file holds the pages it declares and not one byte more, measured through a handle that
        /// is then closed so the length is the length at rest.
        /// </summary>
        internal async Task AssertFileIsExactlyItsPagesAsync(CancellationToken cancellationToken)
        {

            long pages = await ScalarLongAsync("PRAGMA page_count;", cancellationToken);

            long pageSize = await ScalarLongAsync("PRAGMA page_size;", cancellationToken);

            await CloseAsync();

            Assert.Equal(pages * pageSize, new FileInfo(DatabasePath).Length);

        }

        public ValueTask DisposeAsync() => _fixture.DisposeAsync();

        /// <summary>
        /// Enough Covenant rows that emptying the family frees real pages, so the compaction under
        /// test has something to compact.
        /// </summary>
        private static async Task SeedJunkAsync(
            CovenantCanonicalErasureFixture fixture,
            CancellationToken cancellationToken)
        {

            string padding = new('k', 2000);

            for (int index = 0; index < JunkEntryCount; index++)
            {

                await fixture.ExecuteAsync(
                    $"""
                     INSERT INTO covenant_entries (
                         EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
                     VALUES (
                         '{Guid.NewGuid().ToString("D")}', 1, NULL, '{padding}',
                         'junk-{index.ToString(CultureInfo.InvariantCulture)}',
                         '2026-02-01T00:00:00.0000000Z');
                     """,
                    cancellationToken);

            }

        }

        private async Task<Result> Record(string step, Task<Result> work)
        {

            Steps.Add(step);

            return await work;

        }

    }

}
