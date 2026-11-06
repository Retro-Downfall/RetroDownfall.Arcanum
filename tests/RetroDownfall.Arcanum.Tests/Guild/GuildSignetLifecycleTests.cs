using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSignetLifecycleTests
{
    [Fact]
    public async Task Restore_retires_every_imported_credential_and_rotates_epoch_but_restart_preserves_it()
    {
        await using SqliteConnection connection = await OpenAsync();

        string epoch = (string)(await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"))!;

        await InsertAsync(connection, epoch);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, CancellationToken.None)).IsFailure);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.Equal(epoch, await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT RestoredInert FROM guild_signets"));

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, CancellationToken.None)).IsSuccess);

        string restoredEpoch = (string)(await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"))!;

        Assert.NotEqual(epoch, restoredEpoch);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT RestoredInert FROM guild_signets"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT Revision FROM guild_signets"));

        Assert.Equal(epoch, await ScalarAsync(connection, "SELECT AuthorityEpoch FROM guild_signets"));

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, CancellationToken.None)).IsSuccess);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.Equal(restoredEpoch, await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT RestoredInert FROM guild_signets"));
    }

    [Fact]
    public async Task Database_guards_preserve_bindings_and_pair_credential_rotation()
    {
        await using SqliteConnection connection = await OpenAsync();

        await InsertAsync(connection, (string)(await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"))!);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_signets SET GrantJson='[]',Revision=Revision+1"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_signets SET TokenDigest=randomblob(32),Revision=Revision+1"));

        await ExecuteAsync(connection, "UPDATE guild_signets SET TokenDigest=randomblob(32),CredentialGeneration=CredentialGeneration+1,Revision=Revision+1");

        await ExecuteAsync(connection, "UPDATE guild_signets SET Revoked=1,RevocationGeneration=RevocationGeneration+1,Revision=Revision+1");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_signets SET Revoked=0,Revision=Revision+1"));
    }

    [Fact]
    public async Task Owner_cleanup_seals_but_retains_denial_lineage_and_receipts_without_terminal_proof()
    {
        await using SqliteConnection connection = await OpenAsync();

        Guid owner = Guid.NewGuid();

        await InsertAsync(connection, (string)(await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"))!, owner);

        await ExecuteAsync(connection, """
            INSERT INTO guild_signet_revocations(SignetId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,ReasonCode,Revision,Generation)
            SELECT SignetId,'retained-revoke','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',1,'Guild.Revoked',2,1
            FROM guild_signets
            """);

        GuildSignetCleanupParticipant participant = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        GuildCleanupOwner cleanup = new(2, owner, null);

        Assert.True((await participant.PrepareSealAsync(cleanup, connection, CancellationToken.None)).IsSuccess);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Assert.True((await participant.SealAsync(cleanup, connection, transaction, CancellationToken.None)).IsSuccess);

        Assert.True((await participant.DeleteAsync(cleanup, connection, transaction, CancellationToken.None)).IsSuccess);

        await transaction.CommitAsync();

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_signets WHERE Revoked=1 AND CancellationRequested=1"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT RevocationGeneration FROM guild_signets"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_signet_revocations"));
    }

    [Fact]
    public async Task Legacy_committed_proof_accepts_absence_and_refuses_case_aliased_authority_residue()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, GuildCanonicalSchemaVersionOneFixture.ChainSet(), 768, CancellationToken.None);

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, CancellationToken.None)).IsSuccess);

        await ExecuteAsync(connection, "CREATE TABLE GUILD_SIGNETS (Sentinel INTEGER)");

        Assert.True((await BackupRestoreGuildSignetSanitation.VerifyCommittedAsync(connection, CancellationToken.None)).IsFailure);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        return connection;
    }

    private static async Task InsertAsync(SqliteConnection connection, string epoch, Guid? owner = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO guild_signets(SignetId,AgentId,RunId,QuestId,RootQuestId,GrantJson,IssuedAtUtc,
                OriginPrincipalId,SessionId,InstallationGeneration,AuthorityEpoch,Revision,CredentialGeneration,
                TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired)
            VALUES($id,$agent,$run,$quest,$quest,'{}','2026-01-01T00:00:00.0000000Z',
                $principal,$owner,$installation,$epoch,1,1,zeroblob(32),0,0,0,0)
            """;

        foreach (string name in new[] { "$id", "$agent", "$run", "$quest", "$principal", "$installation" })
        {
            command.Parameters.AddWithValue(name, Guid.NewGuid().ToString("D").ToUpperInvariant());
        }

        command.Parameters.AddWithValue("$owner", owner is null ? DBNull.Value : owner.Value.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$epoch", epoch);

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }
}
