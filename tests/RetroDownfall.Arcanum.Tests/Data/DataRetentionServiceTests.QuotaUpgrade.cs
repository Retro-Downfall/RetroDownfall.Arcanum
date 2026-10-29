using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(129)]
    public async Task QuotaUpgrade_Complete_paged_census_returns_only_historical_excess(int liveOwners)
    {
        RequireSqlCipher();

        for (int i = 0; i < liveOwners; i++)
        {
            await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: i == 0 ? 2 : 0, consumed: i == 0 ? 1 : 0);
        }

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), claims: 1, consumed: 1);

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        int processed = await RunQuotaCensusAsync();

        Assert.Equal(liveOwners * 2, processed);

        Assert.Equal(liveOwners == 0 ? (0L, 0L, 0L) : (2L, 1L, 1L), await ReadInstallationQuotaAsync());

        Assert.Equal(liveOwners, await CountAllAsync("session_turn_quota_state"));

        Assert.Equal(liveOwners * 2, await RunQuotaCensusAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("v99:0")]
    [InlineData("v1:1:not-an-id:0:0:0:0:0:0:0")]
    public async Task QuotaUpgrade_Malformed_cursor_cannot_grant_capacity(string cursor)
    {
        RequireSqlCipher();

        await SeedQuotaOwnerAsync(Canonical(Guid.NewGuid()), claims: 2, consumed: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync(cursor));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());
    }

    [SkippableTheory]
    [InlineData("missing-owner")]
    [InlineData("orphan-owner")]
    [InlineData("undercount")]
    [InlineData("missing-singleton")]
    [InlineData("ignored-update")]
    public async Task QuotaUpgrade_Incoherent_or_failed_correction_cannot_publish_capacity(string failure)
    {
        RequireSqlCipher();

        Guid owner = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(owner), claims: 2, consumed: 1);

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), claims: 1, consumed: 1);

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (failure == "missing-owner")
        {
            using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation);

            await ExecuteAsync("DELETE FROM session_turn_quota_state WHERE SessionId = @id;", ("@id", Canonical(owner)));
        }
        else if (failure == "orphan-owner")
        {
            await ExecuteAsync("PRAGMA foreign_keys = OFF;");

            await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(owner)));
        }
        else if (failure == "undercount")
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            var released = await new CovenantQuotaGuard().ReleaseSessionCapacityAsync(
                new SessionTurnCapacityReleaseRequest(owner, 2, 1, 1), new(connection, transaction), CancellationToken.None);

            Assert.True(released.IsSuccess);

            await transaction.CommitAsync();
        }
        else if (failure == "missing-singleton")
        {
            await ExecuteAsync("DELETE FROM installation_turn_quota_state;");
        }
        else
        {
            await ExecuteAsync("""
                CREATE TRIGGER ignore_census_correction BEFORE UPDATE ON installation_turn_quota_state
                BEGIN SELECT RAISE(IGNORE); END;
                """);
        }

        var before = failure == "missing-singleton" ? (0L, 0L, 0L) : await ReadInstallationQuotaAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync());

        if (failure != "missing-singleton") Assert.Equal(before, await ReadInstallationQuotaAsync());

        await using SqliteCommand authority = connection.CreateCommand();

        authority.CommandText = "SELECT arcanum_turn_capacity_mutation_authorized();";

        Assert.Equal(0L, (long)(await authority.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task QuotaUpgrade_Cancellation_after_native_correction_rolls_back_the_final_batch()
    {
        RequireSqlCipher();

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), claims: 2, consumed: 1);

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        using CancellationTokenSource cancellation = new();

        bool observed = false;

        ((SqliteConnection)_db!.Database.GetDbConnection()).CreateFunction("cancel_census_correction", () =>
        {
            observed = true;

            cancellation.Cancel();

            return 1;
        });

        await ExecuteAsync("""
            CREATE TRIGGER cancel_census_correction AFTER UPDATE ON installation_turn_quota_state
            BEGIN SELECT cancel_census_correction(); END;
            """);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunQuotaCensusAsync(cancellationToken: cancellation.Token));

        Assert.True(observed);

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await AssertQuotaAuthorizationClosedAsync();
    }

    [SkippableFact]
    public async Task QuotaUpgrade_Empty_key_orphan_is_included_in_the_complete_owner_census()
    {
        RequireSqlCipher();

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), claims: 2, consumed: 1);

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        await ExecuteAsync("PRAGMA foreign_keys = OFF; INSERT INTO session_turn_quota_state VALUES('',0,0,0);");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync());

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaUpgrade_Impossible_cursor_owner_counts_cannot_skip_the_live_census(bool sumWithoutOwner)
    {
        RequireSqlCipher();

        Guid owner = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(owner), claims: 2, consumed: 1);

        if (sumWithoutOwner)
        {
            await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(owner)));
        }

        string cursor = sumWithoutOwner
            ? "v1|2||0|0|1|0|0|2|1|1"
            : "v1|2|FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF|0|0|0|0|0|2|1|1";

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync(cursor));

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());
    }

    [SkippableTheory]
    [InlineData("abcdefab-cdef-abcd-efab-cdefabcdefab")]
    [InlineData("aBcDeFaB-CdEf-aBcD-EfAb-cDeFaBcDeFaB")]
    public async Task QuotaUpgrade_Legacy_D_identity_is_retained_exactly(string identity)
    {
        RequireSqlCipher();

        // Historical Sessions predate the current new-write spelling guard; identities are immutable.
        await ExecuteAsync("DROP TRIGGER Sessions_Id_guard_identity_insert;");

        await SeedQuotaOwnerAsync(identity, 2, 1);

        Guid historical = Guid.NewGuid();

        await SeedQuotaOwnerAsync(Canonical(historical), 1, 1);

        await ExecuteSessionRetentionAsync("DELETE FROM Sessions WHERE Id = @id;", ("@id", Canonical(historical)));

        Assert.Equal(2, await RunQuotaCensusAsync());

        Assert.Equal((2L, 1L, 1L), await ReadInstallationQuotaAsync());

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "SELECT s.Id FROM Sessions s JOIN session_turn_quota_state q ON q.SessionId=s.Id";

        Assert.Equal(identity, (string)(await command.ExecuteScalarAsync())!);
    }

    [SkippableTheory]
    [InlineData("abcdefab-cdef-abcd-efab-cdefabcdefab", false)]
    [InlineData("aBcDeFaB-CdEf-aBcD-EfAb-cDeFaBcDeFaB", true)]
    [InlineData("abcdefabcdefabcdefabcdefabcdefab", false)]
    public async Task QuotaUpgrade_Normalized_aliases_refuse_even_across_source_pages(string alias, bool laterPage)
    {
        RequireSqlCipher();

        await ExecuteAsync("DROP TRIGGER Sessions_Id_guard_identity_insert;");

        if (laterPage)
        {
            for (int index = 1; index <= 128; index++)
            {
                await SeedQuotaOwnerAsync($"00000000-0000-0000-0000-{index:X12}", 0, 0);
            }
        }

        await SeedQuotaOwnerAsync("ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB", 1, 0);

        await SeedQuotaOwnerAsync(alias, 1, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync());

        Assert.Equal((2L, 2L, 0L), await ReadInstallationQuotaAsync());
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("{ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB}")]
    [InlineData("ABCDEFABCDEF-ABCD-EFAB-CDEFABCDEFAB")]
    [InlineData(" ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task QuotaUpgrade_Malformed_stored_identity_cannot_publish_capacity(string identity)
    {
        RequireSqlCipher();

        await ExecuteAsync("DROP TRIGGER Sessions_Id_guard_identity_insert;");

        await SeedQuotaOwnerAsync(identity, 0, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunQuotaCensusAsync());
    }

    [SkippableFact]
    public async Task QuotaUpgrade_Normalized_alias_probe_is_an_indexed_bounded_seek()
    {
        RequireSqlCipher();

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT 1 FROM Sessions other INDEXED BY IX_Sessions_Id_Norm
            WHERE lower(replace(other.Id,'-',''))=lower(replace(@id,'-','')) AND other.Id COLLATE BINARY<>@id LIMIT 1;
            """;

        command.Parameters.AddWithValue("@id", "ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB");

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> details = [];

        while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, static detail => detail.Contains("SEARCH other USING INDEX IX_Sessions_Id_Norm", StringComparison.Ordinal));

        Assert.DoesNotContain(details, static detail => detail.Contains("SCAN", StringComparison.Ordinal));
    }

    private async Task<int> RunQuotaCensusAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(_filesRoot).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStagedRestore(
            connection, held, _filesRoot, new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource);

        InstallationTurnQuotaBackfill backfill = new();

        int processed = 0;

        while (true)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            GrimoireSchemaBackfillBatch batch = await backfill.AdvanceQuiescentBatchAsync(
                connection, transaction, cursor, execution, cancellationToken);

            Assert.InRange(batch.RowsProcessed, 0, backfill.MaxRowsPerBatch);

            await transaction.CommitAsync(cancellationToken);

            processed += batch.RowsProcessed;

            if (batch.IsComplete) return processed;

            Assert.NotEqual(cursor, batch.NextCursor);

            cursor = batch.NextCursor;
        }
    }
}
