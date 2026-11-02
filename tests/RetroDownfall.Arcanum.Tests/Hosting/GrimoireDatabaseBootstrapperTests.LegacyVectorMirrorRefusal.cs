using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class GrimoireDatabaseBootstrapperTests
{
    [Theory]
    [InlineData("virtual", false)]
    [InlineData("wrong-columns", true)]
    public async Task Core34_mirror_refusal_preserves_recovery_guidance_through_actual_cli_mapping(string shape, bool hostWrapped)
    {
        string database = Path.Combine(_tempDir, "arcanum.db");

        _secretStore.SetApiKey("legacy-mirror-refusal-test-key");

        IServiceScopeFactory core34Scopes = CreateScopeFactory(_credentialStore,
            chains: CoreSchemaVersionThirtyFourFixture.ChainSet());

        await GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
            _secretStore, _passphraseSource, core34Scopes, database, _tempDir, CancellationToken.None);

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Password = _passphraseSource.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        Assert.Equal(34, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = "CREATE TABLE entry_embeddings_vec(EntryId TEXT PRIMARY KEY,Embedding BLOB NOT NULL); INSERT INTO entry_embeddings_vec VALUES('preserved',X'0000803F');";

        await seed.ExecuteNonQueryAsync();

        seed.CommandText = shape == "virtual"
            ? "PRAGMA writable_schema=ON; INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql) VALUES('table','tapestry_node_embeddings_vec','tapestry_node_embeddings_vec',0,'CREATE VIRTUAL TABLE tapestry_node_embeddings_vec USING vec0(NodeId TEXT PRIMARY KEY, Embedding FLOAT[3])'); PRAGMA writable_schema=RESET;"
            : "CREATE TABLE tapestry_node_embeddings_vec(Unrelated TEXT);";

        await seed.ExecuteNonQueryAsync();

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_tempDir));

        Exception error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(_secretStore, _passphraseSource,
                _scopeFactory, database, _tempDir, held, expectedInstallationId: null, CancellationToken.None));

        Assert.Contains("tapestry_node_embeddings_vec", error.Message, StringComparison.Ordinal);

        Assert.Contains("Preserve this database", error.Message, StringComparison.Ordinal);

        Assert.Equal(34, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        seed.CommandText = "SELECT hex(Embedding) FROM entry_embeddings_vec WHERE EntryId='preserved';";

        Assert.Equal("0000803F", await seed.ExecuteScalarAsync());

        GrimoireSchemaTransitionJournalRow pending = Assert.IsType<GrimoireSchemaTransitionJournalRow>(
            await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

        Assert.Equal("legacy-vector-mirror-retirement", pending.BackfillName);

        Assert.False(IsReady());

        CliFailure mapped = CliFailureMapper.Map(hostWrapped ? new AggregateException("Host startup failed.", error) : error);

        Assert.Equal(CliExitCode.ConfigurationError, mapped.ExitCode);

        Assert.Equal(error.Message, mapped.SafeMessage);

        GrimoireDatabaseUnavailableException unavailable = Assert.IsType<GrimoireDatabaseUnavailableException>(error);

        Assert.IsType<LegacyVectorMirrorRetirementRefusedException>(unavailable.InnerException);
    }
}
