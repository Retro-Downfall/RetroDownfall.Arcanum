using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Stripping managed-file authority out of restore staging (§10.16).
/// </summary>
/// <remarks>
/// A backup taken on one machine describes files that do not exist on this one. Every restored write
/// intent and erasure work item is therefore a delete capability for a path this installation never
/// owned, and removing them is not cleanup — it is the removal of authority, which is why it has its
/// own sealed capability and its own SQL authorization.
/// </remarks>
public sealed class BackupRestoreManagedAuthoritySanitizerTests : IAsyncLifetime
{

    private static readonly string[] StagedObjects =
    [
        "artifact_sensitivity",
        "managed_file_write_intents",
        "local_erasure_work_items",
        "restored_managed_file_authority_tombstones",
        "restored_managed_file_authority_tombstones_guard_insert",
        "restored_managed_file_authority_tombstones_guard_update",
        "restored_managed_file_authority_tombstones_guard_delete",
    ];

    private readonly CovenantExclusiveRecoveryOwner _owner = new(
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        CovenantExclusiveOperation.BackupRestore,
        new CovenantDigest([.. Enumerable.Repeat((byte)7, 32)]));

    private readonly Guid _generation = Guid.Parse("cccccccc-dddd-4eee-8fff-111111111111");

    private CovenantSchemaScratchDatabase _staged = null!;

    public async Task InitializeAsync()
    {

        _staged = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _staged.InstallCoreObjectsAsync(StagedObjects, CancellationToken.None);

    }

    [Fact]
    public async Task Every_managed_write_phase_becomes_a_content_free_tombstone()
    {

        for (int phase = 1; phase <= 10; phase++)
        {

            await SeedManagedIntentAsync(phase);

        }

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt = await RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(10UL, receipt.Value.ManagedWriteIntentCount);

        Assert.Equal(0, await CountAsync("managed_file_write_intents"));

        Assert.Equal(10, await CountAsync("restored_managed_file_authority_tombstones"));

        // No decoded source location, ownership, or pending label survives. The tombstone retains
        // only identities, a state code, and a one-way commitment.
        Assert.Equal(
            0,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM pragma_table_info('restored_managed_file_authority_tombstones') "
                + "WHERE name IN ('DurableLocationEvidence', 'FinalOwnershipEvidence', "
                + "'PendingArtifactSensitivityLabel', 'CreatedChildPhysicalIdentityDigest');",
                CancellationToken.None));

    }

    [Fact]
    public async Task Every_local_erasure_state_is_sanitized_without_a_filesystem_call()
    {

        // One source per state: the schema allows at most one *active* work item per source write
        // operation, so four states need four sources rather than four rows over one.
        for (int state = 1; state <= 4; state++)
        {

            await SeedManagedIntentAsync(1, writeOperationId: $"write-{state}");

            await SeedWorkItemAsync(state, sourceWriteOperationId: $"write-{state}");

        }

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt = await RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(4UL, receipt.Value.LocalErasureWorkItemCount);

        Assert.Equal(0, await CountAsync("local_erasure_work_items"));

        // Every local tombstone links to a source tombstone that was already inserted.
        Assert.Equal(
            4,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM restored_managed_file_authority_tombstones local "
                + "WHERE local.SourceKind = 2 AND EXISTS ("
                + "SELECT 1 FROM restored_managed_file_authority_tombstones source "
                + "WHERE source.SourceKind = 1 "
                + "AND source.SourceWriteOperationId = local.SourceWriteOperationId);",
                CancellationToken.None));

    }

    [Fact]
    public async Task The_exact_adopted_label_is_removed_atomically_with_its_authority()
    {

        await SeedLabelAsync("label-1");

        await SeedManagedIntentAsync(4, sensitivityLabelId: "label-1");

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt = await RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(1UL, receipt.Value.RemovedLabelCount);

        Assert.Equal(0, await CountAsync("artifact_sensitivity"));

        Assert.Equal(
            2,
            await _staged.ScalarLongAsync(
                "SELECT LabelDispositionCode FROM restored_managed_file_authority_tombstones;",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_zero_row_sanitation_still_produces_its_canonical_receipt()
    {

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt = await RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0UL, receipt.Value.ManagedWriteIntentCount);
        Assert.Equal(0UL, receipt.Value.LocalErasureWorkItemCount);
        Assert.True(receipt.Value.TombstoneVectorDigest.IsValid);

    }

    [Fact]
    public async Task The_capability_runs_exactly_once()
    {

        await SeedManagedIntentAsync(1);

        RestoreStagingManagedAuthoritySanitizationCapability capability = Mint();

        Assert.True((await capability.RunImmediateAsync(CancellationToken.None)).IsSuccess);

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> second =
            await capability.RunImmediateAsync(CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);

    }

    [Fact]
    public async Task A_published_candidate_can_never_mint_or_run_the_capability()
    {

        Result<RestoreStagingManagedAuthoritySanitizationCapability> published =
            RestoreStagingManagedAuthoritySanitizationCapability.Mint(
                CovenantSqliteConnectionInitializer.Instance,
                _staged.Connection,
                _owner,
                _generation,
                TimeProvider.System,
                static () => false);

        Assert.True(published.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, published.Error.Code);

        // And a candidate that becomes live mid-run is caught before the commit.
        bool live = false;

        await SeedManagedIntentAsync(1);

        RestoreStagingManagedAuthoritySanitizationCapability capability =
            RestoreStagingManagedAuthoritySanitizationCapability.Mint(
                CovenantSqliteConnectionInitializer.Instance,
                _staged.Connection,
                _owner,
                _generation,
                TimeProvider.System,
                () => !live).Value;

        live = true;

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> mid =
            await capability.RunImmediateAsync(CancellationToken.None);

        Assert.True(mid.IsFailure);

        // Rolled back: a rejected check never changes a counter.
        Assert.Equal(1, await CountAsync("managed_file_write_intents"));
        Assert.Equal(0, await CountAsync("restored_managed_file_authority_tombstones"));

    }

    [Fact]
    public void A_non_restore_owner_can_never_mint_the_capability()
    {

        Assert.True(RestoreStagingManagedAuthoritySanitizationCapability.Mint(
            CovenantSqliteConnectionInitializer.Instance,
            _staged.Connection,
            new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest([.. Enumerable.Repeat((byte)3, 32)])),
            _generation,
            TimeProvider.System,
            static () => true).IsFailure);

    }

    [Fact]
    public void The_general_authorization_entry_point_refuses_code_eleven()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => CovenantSqliteConnectionInitializer.Instance.Authorize(
                _staged.Connection,
                CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization));

    }

    [Fact]
    public async Task The_tombstone_vector_digest_covers_every_stripped_row()
    {

        await SeedManagedIntentAsync(1, writeOperationId: "write-a");

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> one = await RunAsync();

        await using CovenantSchemaScratchDatabase second =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await second.InstallCoreObjectsAsync(StagedObjects, CancellationToken.None);

        await SeedManagedIntentAsync(1, writeOperationId: "write-a", database: second);

        await SeedManagedIntentAsync(1, writeOperationId: "write-b", database: second);

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> two = await RunAsync(second);

        Assert.True(one.IsSuccess, Describe(one));
        Assert.True(two.IsSuccess, Describe(two));

        // A different set of stripped rows is a different commitment.
        Assert.NotEqual(one.Value.TombstoneVectorDigest, two.Value.TombstoneVectorDigest);

    }

    public async Task DisposeAsync()
    {

        if (_staged is not null)
        {

            await _staged.DisposeAsync();

        }

    }

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private RestoreStagingManagedAuthoritySanitizationCapability Mint(
        CovenantSchemaScratchDatabase? database = null) =>
        RestoreStagingManagedAuthoritySanitizationCapability.Mint(
            CovenantSqliteConnectionInitializer.Instance,
            (database ?? _staged).Connection,
            _owner,
            _generation,
            TimeProvider.System,
            static () => true).Value;

    private Task<Result<BackupRestoreManagedAuthoritySanitizationReceipt>> RunAsync(
        CovenantSchemaScratchDatabase? database = null) =>
        Mint(database).RunImmediateAsync(CancellationToken.None);

    private Task<long> CountAsync(string table) =>
        _staged.ScalarLongAsync($"SELECT COUNT(*) FROM {table};", CancellationToken.None);

    private async Task SeedLabelAsync(string labelId, CovenantSchemaScratchDatabase? database = null)
    {

        await using SqliteCommand command = (database ?? _staged).Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, 1, $artifact, 1, 2, NULL, $bloom, NULL, NULL, NULL, 1,
                    $digest, $digest, NULL, NULL, NULL, $digest, $now);
            """;

        _ = command.Parameters.AddWithValue("$label", labelId);

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$bloom", Enumerable.Repeat((byte)0x0F, 32).ToArray());

        _ = command.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)5, 32).ToArray());

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedManagedIntentAsync(
        int phaseCode,
        string? writeOperationId = null,
        string? sensitivityLabelId = null,
        CovenantSchemaScratchDatabase? database = null)
    {

        await using SqliteCommand command = (database ?? _staged).Connection.CreateCommand();

        // The schema pairs each phase with the exact evidence that phase is allowed to hold, so the
        // seed has to satisfy the same pairing the production writer does.
        command.CommandText = """
            INSERT INTO managed_file_write_intents (
                WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($write, $effect, $artifact, $label,
                    $digest, $pendingLabel, $evidence,
                    $digest, 4, $childIdentity,
                    $ownership, $phase, 0, 0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue(
            "$write",
            writeOperationId ?? $"write-{Guid.NewGuid():N}");

        // Unique per row: the schema keeps one durable journal per stable effect, so a retried write
        // resumes the existing one instead of creating a second file.
        _ = command.Parameters.AddWithValue(
            "$effect",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _ = command.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)5, 32).ToArray());

        _ = command.Parameters.AddWithValue(
            "$pendingLabel",
            phaseCode is >= 1 and <= 6 ? new byte[64] : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$childIdentity",
            phaseCode == 1 ? DBNull.Value : Enumerable.Repeat((byte)6, 32).ToArray());

        _ = command.Parameters.AddWithValue(
            "$ownership",
            phaseCode is 7 or 10 ? new byte[32] : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$label", sensitivityLabelId ?? $"label-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue("$evidence", new byte[64]);

        _ = command.Parameters.AddWithValue("$phase", phaseCode);

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedWorkItemAsync(
        int stateCode,
        string sourceWriteOperationId,
        CovenantSchemaScratchDatabase? database = null)
    {

        await using SqliteCommand command = (database ?? _staged).Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO local_erasure_work_items (
                WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision,
                ArtifactId, SourceSensitivityLabelId, DurableLocationEvidence,
                ExpectedOwnershipEvidence, StateCode, DeletionEvidenceCode, CheckpointRevision,
                RetryCount, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($item, $erasure, $source, 0,
                    $artifact, $label, $evidence,
                    $ownership, $state, $deletion, 0,
                    0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$item", $"item-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue("$erasure", $"erasure-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue("$source", sourceWriteOperationId);

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$label", $"label-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue("$evidence", new byte[64]);

        _ = command.Parameters.AddWithValue("$ownership", new byte[32]);

        _ = command.Parameters.AddWithValue("$state", stateCode);

        _ = command.Parameters.AddWithValue(
            "$deletion",
            stateCode is 2 or 3 ? 1 : DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static string Iso() =>
        DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

}
