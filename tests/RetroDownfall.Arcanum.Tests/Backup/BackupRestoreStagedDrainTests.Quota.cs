using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreStagedDrainTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core33_quota_census_finishes_in_the_private_staged_restore_before_commit(bool erasureEvidence)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        if (erasureEvidence) await EraseOnTheHostAsync(harness);
        else await WriteWithoutErasingAsync(harness);

        var archive = await harness.CreateArchiveAtAsync("core32-quota", CoreSchemaVersionThirtyTwoFixture.ChainSet(), async connection =>
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt) VALUES
                ('17A33272-611A-4AE4-88ED-1DBEFDC3F5E1','archived','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z'),
                ('17A33272-611A-4AE4-88ED-1DBEFDC3F5E2','archived','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                """;

            await command.ExecuteNonQueryAsync();

            using (CovenantSqliteAuthorizationScope quota = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation))
            {
                command.CommandText = """
                    UPDATE session_turn_quota_state SET ClaimCount=1,ReservedFinalizationCount=1;
                    UPDATE installation_turn_quota_state SET ClaimCount=1,ReservedFinalizationCount=1;
                    UPDATE installation_turn_quota_state SET ClaimCount=2,ReservedFinalizationCount=2;
                    UPDATE session_turn_quota_state SET ReservedFinalizationCount=0,ConsumedFinalizationCount=1
                    WHERE SessionId='17A33272-611A-4AE4-88ED-1DBEFDC3F5E2';
                    UPDATE installation_turn_quota_state SET ReservedFinalizationCount=1,ConsumedFinalizationCount=1;
                    """;

                await command.ExecuteNonQueryAsync();
            }

            using CovenantSqliteAuthorizationScope retention = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection, CovenantSqliteAuthorizationKind.SessionRetention);

            command.CommandText = "DELETE FROM Sessions WHERE Id='17A33272-611A-4AE4-88ED-1DBEFDC3F5E2';";

            await command.ExecuteNonQueryAsync();
        });

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions WHERE TransactionTierCode=0"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT ClaimCount FROM installation_turn_quota_state"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT ReservedFinalizationCount FROM installation_turn_quota_state"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT ConsumedFinalizationCount FROM installation_turn_quota_state"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT ClaimCount FROM session_turn_quota_state"));
    }
}
