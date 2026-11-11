using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildSensitivityClockTests
{
    [Fact]
    public async Task Core_sensitivity_clock_tracks_committed_labels_without_optional_Guild()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var installer = GrimoireSchemaTestInstaller.Create();

        var installed = await installer.InstallCoreOnlyAsync(connection, GrimoireSchemaTestInstaller.CreateContext(), default);

        Assert.True(installed.IsHealthy);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='guild_content'"));

        Assert.Equal(0L, await ClockAsync(connection));

        Guid artifact = Guid.NewGuid();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            var written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, Label(artifact), default);

            Assert.True(written.IsSuccess, written.Error.Message);

            Assert.Equal(1L, await ClockAsync(connection, transaction));

            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await ClockAsync(connection));

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, Label(artifact), default)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(1L, await ClockAsync(connection));

        using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(connection,
            CovenantSqliteAuthorizationKind.ArtifactReplacement))
        {
            await using SqliteCommand deletion = connection.CreateCommand();

            deletion.CommandText = "DELETE FROM artifact_sensitivity WHERE ArtifactId=$id";

            deletion.Parameters.AddWithValue("$id", artifact.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await deletion.ExecuteNonQueryAsync());
        }

        Assert.Equal(2L, await ClockAsync(connection));

        await using SqliteCommand rewind = connection.CreateCommand();

        rewind.CommandText = "UPDATE artifact_sensitivity_clock SET Revision=0";

        await Assert.ThrowsAsync<SqliteException>(() => rewind.ExecuteNonQueryAsync());

        rewind.CommandText = "DELETE FROM artifact_sensitivity_clock";

        await Assert.ThrowsAsync<SqliteException>(() => rewind.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Published_Core38_upgrades_clock_without_rewriting_labels_and_restore_keeps_it()
    {
        Assert.Equal(CoreSchemaVersionThirtyEightFixture.PublishedFingerprint, CoreSchemaVersionThirtyEightFixture.Fingerprint);

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var previous = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyEightFixture.ChainSet(), 768, default);

        Assert.Equal(38, previous.Core.SchemaVersion);

        Guid artifact = Guid.NewGuid();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, Label(artifact), default)).IsSuccess);

            await transaction.CommitAsync();
        }

        var before = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, null, SensitiveArtifactKind.Notification, artifact, default);

        var upgraded = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(upgraded.Core.IsHealthy);

        Assert.Equal(39, upgraded.Core.SchemaVersion);

        Assert.Equal(0L, await ClockAsync(connection));

        var after = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, null, SensitiveArtifactKind.Notification, artifact, default);

        Assert.Equal(before.Value!.LabelDigest, after.Value!.LabelDigest);

        await using SqliteConnection restored = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        connection.BackupDatabase(restored);

        var admitted = await GrimoireSchemaTestInstaller.InstallAsync(restored, 768, default);

        Assert.True(admitted.Core.IsHealthy);

        Assert.Equal(0L, await ClockAsync(restored));
    }

    [Fact]
    public async Task Exhausted_sensitivity_clock_refuses_the_label_mutation_atomically()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.Create().InstallCoreOnlyAsync(connection,
            GrimoireSchemaTestInstaller.CreateContext(), default)).IsHealthy);

        await using (SqliteCommand saturated = connection.CreateCommand())
        {
            saturated.CommandText = "INSERT INTO artifact_sensitivity_clock(Id,Revision) VALUES(1,9223372036854775807)";

            Assert.Equal(1, await saturated.ExecuteNonQueryAsync());
        }

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        var refused = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, Label(Guid.NewGuid()), default);

        Assert.True(refused.IsFailure);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM artifact_sensitivity", transaction));

        Assert.Equal(long.MaxValue, await ClockAsync(connection, transaction));
    }

    private static DerivedArtifactWrite Label(Guid artifact) => new(SensitiveArtifactKind.Notification, artifact, null, null,
        null, 1, new CovenantDigest(new byte[32]), ContentSensitivity.CovenantDerived, GenerationProvenance.Create([Guid.NewGuid()]));

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task<object?> ClockAsync(SqliteConnection connection, SqliteTransaction? transaction = null) =>
        await ScalarAsync(connection, "SELECT COALESCE((SELECT Revision FROM artifact_sensitivity_clock WHERE Id=1),0)", transaction);
}
