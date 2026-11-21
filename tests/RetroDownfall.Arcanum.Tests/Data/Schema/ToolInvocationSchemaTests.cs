using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class ToolInvocationSchemaTests
{
    [Fact]
    public async Task Version_forty_upgrades_in_place_and_tool_evidence_leaves_with_its_session()
    {
        Assert.Equal(CoreSchemaVersionFortyFixture.PublishedFingerprint, CoreSchemaVersionFortyFixture.Fingerprint);

        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(default);

        var old = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, default, CoreSchemaVersionFortyFixture.ChainSet());

        Assert.Equal(40, old.Core.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Sessions(Id,CreatedAt,UpdatedAt)
            VALUES('30000000-0000-0000-0000-000000000001','2026-11-20T01:00:00.0000000Z','2026-11-20T01:00:00.0000000Z');
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            VALUES('30000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000001',1,
                'original Session entry','fixture','2026-11-20T01:00:00.0000000Z',1);
            """;

        await command.ExecuteNonQueryAsync();

        var upgraded = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, default);

        Assert.Equal(42, upgraded.Core.SchemaVersion);

        command.CommandText = "SELECT Content FROM Entries WHERE Id='30000000-0000-0000-0000-000000000002'";

        Assert.Equal("original Session entry", await command.ExecuteScalarAsync());

        command.CommandText = """
            INSERT INTO session_tool_invocations(ReceiptId,SessionId,AssistantEntryId,RoundOrdinal,CallOrdinal,ArgumentsDigest,CreatedAtUtcTicks,StateCode)
            VALUES('30000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000001',
                '30000000-0000-0000-0000-000000000002',0,0,zeroblob(32),639000000000000000,1);
            """;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "UPDATE session_tool_invocations SET StateCode=2,DispatchId='30000000-0000-0000-0000-000000000004',ArgumentsDigest=randomblob(32)";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = "DELETE FROM Sessions WHERE Id='30000000-0000-0000-0000-000000000001'";

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
            Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT COUNT(*) FROM session_tool_invocations";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
