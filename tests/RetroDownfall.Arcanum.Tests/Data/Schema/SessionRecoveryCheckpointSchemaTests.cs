using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class SessionRecoveryCheckpointSchemaTests
{
    [Fact]
    public async Task Upgrade_preserves_v41_session_and_purges_checkpoint_with_owning_question()
    {
        Assert.Equal(CoreSchemaVersionFortyOneFixture.PublishedFingerprint, CoreSchemaVersionFortyOneFixture.Fingerprint);

        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(default);

        var prior = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(
            connection, 64, default, CoreSchemaVersionFortyOneFixture.ChainSet());

        Assert.Equal(41, prior.Core.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Sessions(Id,CreatedAt,UpdatedAt)
            VALUES('40000000-0000-0000-0000-000000000001','2026-11-20T01:00:00.0000000Z','2026-11-20T01:00:00.0000000Z');
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            VALUES('40000000-0000-0000-0000-000000000002','40000000-0000-0000-0000-000000000001',1,
                'prior Session context','fixture','2026-11-20T01:00:00.0000000Z',1);
            """;

        await command.ExecuteNonQueryAsync();

        var upgraded = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, default);

        Assert.Equal(42, upgraded.Core.SchemaVersion);

        command.CommandText = "SELECT Content FROM Entries WHERE Id='40000000-0000-0000-0000-000000000002'";

        Assert.Equal("prior Session context", await command.ExecuteScalarAsync());

        command.CommandText = """
            INSERT INTO session_recovery_checkpoints(Id,SessionId,AssistantEntryId,ClaimId,KindCode,
                Provider,Model,ProviderRouteDigest,QuestionEntryId,CreatedAtUtcTicks)
            VALUES('40000000-0000-0000-0000-000000000003','40000000-0000-0000-0000-000000000001',
                '40000000-0000-0000-0000-000000000002','40000000-0000-0000-0000-000000000003',
                1,'local','fixture',zeroblob(32),'40000000-0000-0000-0000-000000000002',639000000000000000);
            """;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "UPDATE session_recovery_checkpoints SET ConsumingClaimId='40000000-0000-0000-0000-000000000004'";

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "UPDATE session_recovery_checkpoints SET ConsumingClaimId='40000000-0000-0000-0000-000000000005'";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = "DELETE FROM Entries WHERE Id='40000000-0000-0000-0000-000000000002'";

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
            Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT COUNT(*) FROM session_recovery_checkpoints";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
