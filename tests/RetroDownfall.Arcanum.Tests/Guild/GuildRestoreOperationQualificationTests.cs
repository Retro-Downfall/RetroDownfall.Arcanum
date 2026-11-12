using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRestoreOperationQualificationTests
{
    [Fact]
    public async Task Restore_retains_Guild_operation_and_its_generic_ancestor_but_interrupts_ordinary_work()
    {
        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync();

        await fixture.ExecuteAsync("""
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,Revision)
            VALUES('11111111111111111111111111111111','ordinary-root',0,0,'2026-01-01T00:00:00Z','root',1),
                  ('88888888888888888888888888888888','ordinary-independent',0,0,'2026-01-01T00:00:00Z','independent',1);
            UPDATE LongRunningOperations SET RootOperationId='11111111111111111111111111111111',
                ParentOperationId='11111111111111111111111111111111',LeaseOwner='old-host',
                LeaseExpiresAt='2099-01-01T00:00:00Z' WHERE Kind='guild-quest';
            """);

        long interrupted = await BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(fixture.Connection, CancellationToken.None);

        Assert.Equal(2, interrupted);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind='ordinary-independent'"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind='ordinary-root' AND State=6 AND TerminalErrorCode='backup.restore_interrupted_elsewhere'"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM LongRunningOperations o JOIN guild_quests q ON q.OperationId=o.Id WHERE o.State=0 AND o.LeaseOwner IS NULL AND o.LeaseExpiresAt IS NULL AND o.CheckpointVersion=1 AND length(o.CheckpointPayload)>0"));
    }

    [Fact]
    public async Task Exhausted_Guild_operation_revision_refuses_before_any_pending_operation_changes()
    {
        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync();

        await fixture.ExecuteAsync("""
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,Revision)
            VALUES('88888888888888888888888888888888','ordinary-independent',0,0,'2026-01-01T00:00:00Z','independent',1);
            UPDATE LongRunningOperations SET Revision=9223372036854775807, LeaseOwner='old-host',
                LeaseExpiresAt='2099-01-01T00:00:00Z' WHERE Kind='guild-quest';
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(fixture.Connection, CancellationToken.None));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind='ordinary-independent' AND State=0"));

        Assert.Equal(1L, await fixture.ScalarAsync("""
            SELECT count(*) FROM LongRunningOperations WHERE Kind='guild-quest' AND State=0
              AND typeof(Revision)='integer' AND Revision=9223372036854775807
              AND LeaseOwner='old-host' AND LeaseExpiresAt='2099-01-01T00:00:00Z'
            """));
    }

    [Theory]
    [InlineData("-1", "integer")]
    [InlineData("1.5", "real")]
    public async Task Malformed_leased_Guild_operation_revision_refuses_without_changing_other_work(string revision, string storageType)
    {
        await using GuildQuestLifecycleTests.Fixture fixture = await GuildQuestLifecycleTests.Fixture.CreateAsync();

        await fixture.ExecuteAsync($"""
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,Revision)
            VALUES('88888888888888888888888888888888','ordinary-independent',0,0,'2026-01-01T00:00:00Z','independent',1);
            UPDATE LongRunningOperations SET Revision={revision}, LeaseOwner='old-host' WHERE Kind='guild-quest';
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(fixture.Connection, CancellationToken.None));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind='ordinary-independent' AND State=0"));

        Assert.Equal(1L, await fixture.ScalarAsync($"""
            SELECT count(*) FROM LongRunningOperations WHERE Kind='guild-quest' AND State=0
              AND typeof(Revision)='{storageType}' AND Revision={revision} AND LeaseOwner='old-host'
            """));
    }

    [Fact]
    public async Task Restore_without_the_optional_Guild_family_still_interrupts_ordinary_operations()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            DROP TABLE guild_quests;
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,Revision)
            VALUES('88888888888888888888888888888888','ordinary-independent',0,0,'2026-01-01T00:00:00Z','independent',1);
            """;

        _ = await command.ExecuteNonQueryAsync();

        Assert.Equal(1, await BackupRestoreDatabaseWorker.ClearPendingOperationsAsync(connection, CancellationToken.None));

        command.CommandText = "SELECT count(*) FROM LongRunningOperations";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
