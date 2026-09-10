using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class GrimoireSqlCommandFactoryTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _databasePath = string.Empty;

    private ArcanumDbContext? _db;

    public GrimoireSqlCommandFactoryTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _databasePath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_databasePath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [SkippableFact]
    public async Task CreateAsync_reuses_the_context_connection_and_attaches_its_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SqliteConnection expectedConnection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await _db.Database.CloseConnectionAsync();

        Assert.Equal(ConnectionState.Closed, expectedConnection.State);

        await using (SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            "SELECT 1;",
            CancellationToken.None))
        {
            Assert.Same(expectedConnection, command.Connection);
            Assert.Null(command.Transaction);
            Assert.Equal(1L, await command.ExecuteScalarAsync(CancellationToken.None));
        }

        Assert.Equal(ConnectionState.Open, expectedConnection.State);

        await using IDbContextTransaction transaction = await _db.Database
            .BeginTransactionAsync(CancellationToken.None);
        await using SqliteCommand transactionalCommand = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            "SELECT 2;",
            CancellationToken.None);

        Assert.Same(expectedConnection, transactionalCommand.Connection);
        Assert.Same(transaction.GetDbTransaction(), transactionalCommand.Transaction);
        Assert.Equal(2L, await transactionalCommand.ExecuteScalarAsync(CancellationToken.None));
    }
}
